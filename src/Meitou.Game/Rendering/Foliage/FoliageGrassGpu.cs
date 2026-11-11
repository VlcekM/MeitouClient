using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Profiling;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// A view's tables in the frame's constants, written by the CPU per view: the zones' patch ranges and the patch rows.
/// </summary>
public readonly record struct GrassTables(Transient Zones, int ZoneCount, Transient Patches, int PatchCount);

/// <summary>
/// What one view's grass cull wrote: the indirect draws (16 bytes each, the first <c>Counters[0]</c> of at most <see cref="MaxDraws"/> valid), and
/// the counters (draws, blades).
/// </summary>
public readonly record struct GrassResult(GpuBuffer Draws, ulong DrawsOffset, GpuBuffer Counters, ulong CountersOffset, int MaxDraws,
    Transient Patches)
{
    public bool IsEmpty => MaxDraws == 0;
}

/// <summary>
/// The GPU grass store and cull (docs/renderer-native.md 5.4 and 5.6.2). Every grass page's blades live in one arena (a vertex buffer: the
/// instance data of the blade draws, <c>firstInstance</c> = the first blade), and every (page, patch) in a slot of a table the cull kernels read:
/// per view <see cref="Dispatch"/> records two compute kernels into <see cref="GpuFrame.PreFrame"/> that write one indirect draw per visible
/// slot, nearest page first, with the density's blade count. The CPU records one <see cref="IGpuCommandList.DrawIndirectCount"/>. Pages arrive and
/// leave as <see cref="FoliageRenderer"/> decides; their ranges and slots are freed after the frames in flight. Render thread only.
/// </summary>
public sealed unsafe class FoliageGrassGpu : IDisposable
{
    public const int PrefixStride = FoliageGrassField.PrefixSteps + 1;
    /// <summary>
    /// Bytes of a blade (<see cref="FoliageGrassField.Stride"/> floats).
    /// </summary>
    public const int BladeBytes = FoliageGrassField.Stride * 4;
    const ulong Align = 256;
    const int MaxViews = 64;

    readonly IGpuContext _ctx;
    readonly GpuProgram? _cull, _order;
    readonly GpuComputePipeline? _cullPipe, _orderPipe;
    readonly uint[] _cullBindings = [], _orderBindings = [];
    readonly BufferArena _arena;
    readonly DeviceBuffer _slots, _prefixes;
    readonly object _gate = new();
    readonly List<int> _freeSlots = [];

    /// <summary>
    /// Whether the cull kernels exist (<see cref="Supported"/>): without them the store is only the blade arena and the CPU chooses the draws.
    /// </summary>
    public bool Kernels => _cull is not null;

    public FoliageGrassGpu(IGpuContext ctx, ulong arenaBytes, int slotCapacity)
    {
        this._ctx = ctx;
        SlotCapacity = slotCapacity;
        if (Supported(ctx))
        {
            var vk = (GpuContext)ctx;
            _cull = ctx.Shaders.Compute(FoliageGrassShaders.CullCompute, "foliage grass cull");
            _order = ctx.Shaders.Compute(FoliageGrassShaders.OrderCompute, "foliage grass order");
            _cullPipe = ctx.Pipelines.Get(new ComputePipelineDesc(_cull!.Value, "foliage grass cull"));
            _orderPipe = ctx.Pipelines.Get(new ComputePipelineDesc(_order!.Value, "foliage grass order"));
            _cullBindings = Bindings(vk, _cull!.Value);
            _orderBindings = Bindings(vk, _order!.Value);
            _scratch = new FrameScratch(vk, "foliage grass scratch",
                BufferUse.Storage | BufferUse.Indirect | BufferUse.TransferSrc | BufferUse.TransferDst, 64ul << 20);
        }
        // Storage: the traced sun's kernel reads the blades (Gi.TracedGrass).
        _arena = new BufferArena(ctx, arenaBytes, BufferUse.Vertex | BufferUse.TransferDst | (ctx is GpuContext ? BufferUse.Storage : 0), "foliage grass blades");
        _slots = DeviceBuffer.Create(ctx, (ulong)slotCapacity * GrassSlot.Size, BufferUse.Storage | BufferUse.TransferDst, "foliage grass slots");
        _prefixes = DeviceBuffer.Create(ctx, (ulong)slotCapacity * PrefixStride * 4, BufferUse.Storage | BufferUse.TransferDst,
            "foliage grass prefixes");
        _counted = new ReadbackBuffer?[ctx.FramesInFlight];
        _countedWritten = new int[ctx.FramesInFlight];
    }

    static uint[] Bindings(GpuContext ctx, GpuProgram p)
    {
        var backend = ctx.Shaders.Backend(p);
        if (!backend.PushDescriptors) throw new InvalidOperationException($"{p.Name}: the grass cull pushes its descriptors, the device cannot");
        return [.. backend.ComputeReflection!.Blocks.Where(b => b.Kind == Gpu.Shaders.BlockKind.StorageBuffer && b.Set == 0).Select(b => (uint)b.Binding).Order()];
    }

    /// <summary>
    /// Whether the device can run the GPU grass (push descriptors, indirect draws with a count and a first instance).
    /// </summary>
    public static bool Supported(IGpuContext ctx) =>
        ctx is GpuContext vk && vk.Device.HasPushDescriptor && vk.Device.DrawIndirectCount && vk.Device.DrawIndirectFirstInstance && vk.Device.MultiDrawIndirect;

    // ---- the blade arena ----

    /// <summary>
    /// The arena as a vertex buffer: blade <c>i</c> is at byte <c>i * <see cref="BladeBytes"/></c>.
    /// </summary>
    public GpuBuffer BladeBuffer => _arena.Buffer.Handle;
    public ulong ArenaBytes => _arena.Buffer.Size;
    public ulong ArenaUsed => _arena.Buffer.Size - _arena.FreeBytes;
    /// <summary>
    /// Pages that could not be placed (arena or slot table full) since the start.
    /// </summary>
    public long Misses { get; set; }

    /// <summary>
    /// Room for <paramref name="blades"/> blades; empty when the arena is full.
    /// </summary>
    public ArenaRange AllocateBlades(int blades) => _arena.Allocate((ulong)blades * BladeBytes, BladeBytes);

    /// <summary>
    /// The index of a range's first blade.
    /// </summary>
    public static uint FirstBlade(ArenaRange range) => (uint)(range.Offset / BladeBytes);

    public void FreeBlades(ArenaRange range) => _arena.Free(range);

    /// <summary>
    /// Writes blade data at <paramref name="byteStart"/> of a range (through the frame's upload buffer; needs an open frame).
    /// </summary>
    public void WriteBlades(ArenaRange range, int byteStart, ReadOnlySpan<byte> data)
    {
        using var batch = _ctx.Uploads.Begin();
        batch.Write(_arena.Buffer, range.Offset + (ulong)byteStart, data);
    }

    // ---- the slot table ----

    public int SlotCapacity { get; }
    /// <summary>
    /// One more than the highest slot ever handed out: the kernels' range.
    /// </summary>
    public int SlotHigh { get; private set; }
    public int SlotsUsed { get; private set; }

    /// <summary>
    /// A free slot, or -1 when the table is full.
    /// </summary>
    public int AllocateSlot()
    {
        lock (_gate)
        {
            int slot;
            if (_freeSlots.Count > 0) { slot = _freeSlots[^1]; _freeSlots.RemoveAt(_freeSlots.Count - 1); }
            else if (SlotHigh < SlotCapacity) slot = SlotHigh++;
            else return -1;
            SlotsUsed++;
            return slot;
        }
    }

    bool _initialised;

    /// <summary>
    /// The upload commands (Vulkan's pre-frame list; Metal's main list, recorded before the pass).
    /// </summary>
    IGpuCommandList UploadCommands => _ctx is GpuContext vk ? vk.Frame.PreFrame : _ctx.Frame.Commands;

    /// <summary>
    /// The Vulkan context the cull kernels need (the Metal path never records them).
    /// </summary>
    GpuContext Vk => _ctx as GpuContext ?? throw new NotSupportedException("the Metal backend has no GPU grass cull");

    /// <summary>
    /// The slot table starts as zeros (every slot has no blades), written once by the first frame that needs it.
    /// </summary>
    void Initialise()
    {
        if (_initialised) return;
        var cmd = UploadCommands;
        cmd.FillBuffer(_slots.Handle, 0, _slots.Size, 0);
        Barrier(cmd, Access.TransferDst, Access.TransferSrc | Access.TransferDst);   // the slots are copied from and into after the fill
        _initialised = true;
    }

    /// <summary>
    /// Writes a slot and its blade prefixes (<see cref="PrefixStride"/> counts; needs an open frame).
    /// </summary>
    public void WriteSlot(int slot, in GrassSlot record, ReadOnlySpan<int> prefix)
    {
        if (prefix.Length != PrefixStride) throw new ArgumentException($"{PrefixStride} prefix counts", nameof(prefix));
        Initialise();
        using var batch = _ctx.Uploads.Begin();
        batch.Write(_slots, (ulong)slot * GrassSlot.Size, MemoryMarshal.AsBytes(new ReadOnlySpan<GrassSlot>(in record)));
        batch.Write(_prefixes, (ulong)slot * PrefixStride * 4, MemoryMarshal.AsBytes(prefix));
    }

    /// <summary>
    /// Stops a slot drawing now (its blade count written as 0, when a frame is open) and gives it back after the frames in flight.
    /// </summary>
    public void FreeSlot(int slot)
    {
        if (_ctx.Frame.Open && _initialised)
        {
            uint zero = 0;

            // The count is the fifth field of the record.
            using var batch = _ctx.Uploads.Begin();
            batch.Write(_slots, (ulong)slot * GrassSlot.Size + 16, MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(in zero)));
        }
        _ctx.DeferDelete(() =>
        {
            lock (_gate) { _freeSlots.Add(slot); SlotsUsed--; }
        });
    }

    // ---- per view ----

    /// <summary>
    /// Copies the tables of a view into the frame's constants (valid for the frame).
    /// </summary>
    public GrassTables Prepare(ReadOnlySpan<GrassZoneRow> zones, ReadOnlySpan<GrassPatchRow> patches)
    {
        // A table is never empty memory (a binding must have a range).
        ReadOnlySpan<GrassZoneRow> zoneRows = zones.IsEmpty ? new GrassZoneRow[1] : zones;
        ReadOnlySpan<GrassPatchRow> patchRows = patches.IsEmpty ? new GrassPatchRow[1] : patches;
        Transient z = _ctx.Frame.WriteConstants(MemoryMarshal.AsBytes(zoneRows), Align);
        Transient p = _ctx.Frame.WriteConstants(MemoryMarshal.AsBytes(patchRows), Align);
        return new GrassTables(z, zones.Length, p, patches.Length);
    }

    readonly FrameScratch? _scratch;
    int _scratchSlot = -1;
    long _scratchFrame = -1;
    int _viewIndex;
    readonly ReadbackBuffer?[] _counted;
    readonly int[] _countedWritten;

    /// <summary>
    /// The draws and blades the view with this call number drew a frame ring ago (0 before): statistics for the CPU.
    /// </summary>
    public int LateDraws { get; private set; }
    public long LateBlades { get; private set; }

    void NewFrame()
    {
        var frame = _ctx.Frame;
        if (_scratchFrame == frame.Number) return;
        if (_scratchSlot >= 0) _countedWritten[_scratchSlot] = _viewIndex;
        (_scratchFrame, _scratchSlot, _viewIndex) = (frame.Number, frame.Slot, 0);
    }

    /// <summary>
    /// The per-view lists' memory (<see cref="FrameScratch"/>); null without the cull kernels.
    /// </summary>
    public FrameScratch? Scratch => _scratch;

    readonly List<(QuerySlot Begin, QuerySlot End)> _pendingTimes = [];
    public double GpuMicroseconds { get; private set; }
    public long GpuTimedViews { get; private set; }
    public long Dispatched { get; private set; }

    void CollectTimes()
    {
        var q = _ctx.Frame.Timestamps;
        for (int i = 0; i < _pendingTimes.Count; i++)
        {
            var (b, e) = _pendingTimes[i];
            if (b.Frame + _ctx.FramesInFlight + 2 < _ctx.Frame.Number) { _pendingTimes.RemoveAt(i--); continue; }
            if (!q.TryRead(b, out ulong tb) || !q.TryRead(e, out ulong te)) continue;
            GpuMicroseconds += (te - tb) / 1000.0;
            GpuTimedViews++;
            _pendingTimes.RemoveAt(i--);
        }
    }

    /// <summary>
    /// Records the cull of one view into <see cref="GpuFrame.PreFrame"/>: <paramref name="planes"/> (at most 8), the eye along the ground, the page
    /// size, the density as the prefix step and the fraction into it (<see cref="DensityStep"/>); for the motion pass <paramref name="motionScale"/>
    /// (<see cref="GrassMotionReach"/>, 0: the patches' ranges only). The draws are read by this frame's draw.
    /// </summary>
    public GrassResult Dispatch(ReadOnlySpan<Vector4> planes, Vector2 eye, float pageSize, int prefixIndex, float fraction, in GrassTables tables,
        float motionScale = 0)
    {
        int n = SlotHigh;
        if (n == 0 || tables.PatchCount == 0) return default;
        if (planes.Length > 8) throw new ArgumentException("at most 8 planes", nameof(planes));
        Initialise();
        CollectTimes();
        NewFrame();
        var scratch = this._scratch!;
        if (!(scratch.TryAllocate((ulong)n * 16, out var keysBuffer, out var keysOffset) && scratch.TryAllocate((ulong)n * 16, out var drawsBuffer,
                out var drawsOffset) &&
              scratch.TryAllocate(16, out var countersBuffer, out var countersOffset) &&
              scratch.TryAllocate((ulong)n * 4, out var firstsBuffer, out var firstsOffset)))
            return default;
        var keys = (Buffer: keysBuffer, Offset: keysOffset);
        var draws = (Buffer: drawsBuffer, Offset: drawsOffset);
        var counters = (Buffer: countersBuffer, Offset: countersOffset);
        var viewData = Vk.Frame.Constants.Allocate(160, Align);
        var p = (Vector4*)viewData.Pointer;
        for (int i = 0; i < 8; i++) p[i] = i < planes.Length ? planes[i] : default;
        var rest = (uint*)(viewData.Pointer + 128);
        *(Vector2*)rest = eye;
        rest[2] = (uint)planes.Length;
        rest[3] = (uint)n;
        ((float*)rest)[4] = pageSize;
        ((float*)rest)[5] = fraction;
        rest[6] = (uint)prefixIndex;
        ((float*)rest)[7] = motionScale;

        Span<GpuBufferBinding> b = stackalloc GpuBufferBinding[9];
        b[0] = new GpuBufferBinding(viewData.Handle, viewData.Offset, 160);
        b[1] = new GpuBufferBinding(_slots.Handle, 0, _slots.Size);
        b[2] = new GpuBufferBinding(_prefixes.Handle, 0, _prefixes.Size);
        b[3] = new GpuBufferBinding(tables.Zones.Handle, tables.Zones.Offset, (ulong)Math.Max(tables.ZoneCount, 1) * 8);
        b[4] = new GpuBufferBinding(tables.Patches.Handle, tables.Patches.Offset, (ulong)tables.PatchCount * GrassPatchRow.Size64);
        b[5] = new GpuBufferBinding(keys.Buffer, keys.Offset, (ulong)n * 16);
        b[6] = new GpuBufferBinding(draws.Buffer, draws.Offset, (ulong)n * 16);
        b[7] = new GpuBufferBinding(counters.Buffer, counters.Offset, 16);
        b[8] = new GpuBufferBinding(firstsBuffer, firstsOffset, (ulong)n * 4);   // the draws' first blades, in kernel 1's list order

        var cmd = UploadCommands;
        var stamps = (_ctx.Frame.Timestamps.Allocate(), _ctx.Frame.Timestamps.Allocate());
        FrameProfiler.PreStamp(cmd, StageClock.Uploads);
        cmd.BeginLabel("foliage grass");
        cmd.FillBuffer(counters.Buffer, counters.Offset, 16, 0);
        Barrier(cmd, Access.TransferDst, Access.StorageRead | Access.StorageWrite);
        cmd.Timestamp(_ctx.Frame.Timestamps, stamps.Item1, GpuStage.Compute);
        uint groups = (uint)((n + FoliageGrassShaders.Group - 1) / FoliageGrassShaders.Group);
        Run(cmd, _cull!.Value, _cullPipe!.Value, _cullBindings, b, groups);
        Barrier(cmd, Access.StorageWrite, Access.StorageRead);
        Run(cmd, _order!.Value, _orderPipe!.Value, _orderBindings, b, groups);
        Barrier(cmd, Access.StorageWrite, Access.IndirectRead | Access.StorageRead | Access.TransferSrc);
        cmd.Timestamp(_ctx.Frame.Timestamps, stamps.Item2, GpuStage.Compute);
        var ring = _counted[_scratchSlot] ??= ReadbackBuffer.Create(Vk, MaxViews * 16, $"foliage grass counters {_scratchSlot}");
        if (_viewIndex < MaxViews)
        {
            if (_viewIndex < _countedWritten[_scratchSlot])
            {
                var v = MemoryMarshal.Cast<byte, uint>(ring.Read((ulong)_viewIndex * 16, 16));
                (LateDraws, LateBlades) = ((int)v[0], v[1]);
            }
            else (LateDraws, LateBlades) = (0, 0);
            cmd.CopyBuffer(counters.Buffer, ring.Handle, new GpuBufferCopy(counters.Offset, (ulong)_viewIndex * 16, 16));
            _viewIndex++;
        }
        cmd.EndLabel();
        FrameProfiler.PreStamp(cmd, StageClock.Cull);
        if (stamps.Item1.IsValid && stamps.Item2.IsValid) _pendingTimes.Add(stamps);
        Dispatched++;
        return new GrassResult(draws.Buffer, draws.Offset, counters.Buffer, counters.Offset, n, tables.Patches);
    }

    void Run(IGpuCommandList cmd, GpuProgram program, GpuComputePipeline pipe, uint[] bindings, ReadOnlySpan<GpuBufferBinding> b, uint groups)
    {
        cmd.BindPipeline(pipe);
        int count = bindings.Length;
        var list = stackalloc GpuBinding[count];
        for (int i = 0; i < count; i++)
        {
            ref readonly var bb = ref b[(int)bindings[i]];
            list[i] = GpuBinding.Storage(bindings[i], bb);
        }
        cmd.BindProgramResources(program, 0, new ReadOnlySpan<GpuBinding>(list, count));
        cmd.Dispatch(groups);
    }

    /// <summary>
    /// Orders a pass's work against the next one's (the engine's words; the backend's masks).
    /// </summary>
    static void Barrier(IGpuCommandList cmd, Access from, Access to) => cmd.Barrier([new GpuDependency(from, to)]);

    /// <summary>
    /// The density setting as the prefix step and the fraction into it, as <see cref="FoliageGrassField.PrefixCount"/> takes them.
    /// </summary>
    public static (int Index, float Fraction) DensityStep(float fraction)
    {
        float at = Math.Clamp(fraction, 0, 1) * FoliageGrassField.PrefixSteps;
        int i = Math.Min((int)at, FoliageGrassField.PrefixSteps - 1);
        return (i, at - i);
    }

    /// <summary>
    /// Bytes <see cref="CopyForReadback"/> writes: the counters (16), then the draws.
    /// </summary>
    public static ulong ReadbackBytes(in GrassResult r) => 16 + (ulong)r.MaxDraws * 16;

    /// <summary>
    /// Copies a result (counters, then every draw slot) into <paramref name="target"/>, after the kernels (the tests read them back).
    /// </summary>
    public void CopyForReadback(in GrassResult r, ReadbackBuffer target)
    {
        var cmd = UploadCommands;
        cmd.CopyBuffer(r.Counters, target.Handle, new GpuBufferCopy(r.CountersOffset, 0, 16));
        cmd.CopyBuffer(r.Draws, target.Handle, new GpuBufferCopy(r.DrawsOffset, 16, (ulong)r.MaxDraws * 16));
    }

    public void Dispose()
    {
        foreach (var p in new[] { _cull, _order })
            if (p is not null)
            {
                _ctx.Pipelines.Forget(p.Value);
                _ctx.Shaders.Dispose(p.Value);
            }
        _arena.Dispose();
        _slots.Dispose();
        _prefixes.Dispose();
        _scratch?.Dispose();
        foreach (var c in _counted) c?.Dispose();
    }
}