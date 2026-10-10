using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Meitou.Rendering;

/// <summary>A view's tables in the frame's constants, written by the CPU per view: the zones' patch ranges and the patch rows.</summary>
public readonly record struct GrassTables(Transient Zones, int ZoneCount, Transient Patches, int PatchCount);

/// <summary>What one view's grass cull wrote: the indirect draws (16 bytes each, the first <c>Counters[0]</c> of at most <see cref="MaxDraws"/>
/// valid), and the counters (draws, blades).</summary>
public readonly record struct GrassResult(Buffer Draws, ulong DrawsOffset, Buffer Counters, ulong CountersOffset, int MaxDraws, Transient Patches)
{
    public bool IsEmpty => MaxDraws == 0;
}

/// <summary>
/// The GPU grass store and cull (docs/renderer-native.md 5.4 and 5.6.2). Every grass page's blades live in one arena (a vertex buffer: the
/// instance data of the blade draws, <c>firstInstance</c> = the first blade), and every (page, patch) in a slot of a table the cull kernels read:
/// per view <see cref="Dispatch"/> records two compute kernels into <see cref="GpuFrame.PreFrame"/> that write one indirect draw per visible
/// slot, nearest page first, with the density's blade count. The CPU records one <see cref="CommandList.DrawIndirectCount"/>. Pages arrive and
/// leave as <see cref="FoliageRenderer"/> decides; their ranges and slots are freed after the frames in flight. Render thread only.
/// </summary>
public sealed unsafe class FoliageGrassGpu : IDisposable
{
    public const int PrefixStride = FoliageGrassField.PrefixSteps + 1;
    /// <summary>Bytes of a blade (<see cref="FoliageGrassField.Stride"/> floats).</summary>
    public const int BladeBytes = FoliageGrassField.Stride * 4;
    const ulong Align = 256;
    const int MaxViews = 64;

    readonly GpuContext ctx;
    readonly ShaderProgram? cull, order;
    readonly ComputePipeline? cullPipe, orderPipe;
    readonly uint[] cullBindings = [], orderBindings = [];
    readonly BufferArena arena;
    readonly DeviceBuffer slots, prefixes;
    readonly object gate = new();
    readonly List<int> freeSlots = [];

    /// <summary>Whether the cull kernels exist (<see cref="Supported"/>): without them the store is only the blade arena and the CPU chooses the draws.</summary>
    public bool Kernels => cull is not null;

    public FoliageGrassGpu(GpuContext ctx, ulong arenaBytes, int slotCapacity)
    {
        this.ctx = ctx;
        SlotCapacity = slotCapacity;
        if (Supported(ctx))
        {
            cull = ctx.Shaders.Compute(FoliageGrassShaders.CullCompute, "foliage grass cull");
            order = ctx.Shaders.Compute(FoliageGrassShaders.OrderCompute, "foliage grass order");
            cullPipe = ctx.Pipelines.Get(new ComputePipelineDesc(cull, "foliage grass cull"));
            orderPipe = ctx.Pipelines.Get(new ComputePipelineDesc(order, "foliage grass order"));
            cullBindings = Bindings(cull);
            orderBindings = Bindings(order);
        }
        arena = new BufferArena(ctx, arenaBytes, BufferUse.Vertex | BufferUse.TransferDst, "foliage grass blades");
        slots = DeviceBuffer.Create(ctx, (ulong)slotCapacity * GrassSlot.Size, BufferUse.Storage | BufferUse.TransferDst, "foliage grass slots");
        prefixes = DeviceBuffer.Create(ctx, (ulong)slotCapacity * PrefixStride * 4, BufferUse.Storage | BufferUse.TransferDst, "foliage grass prefixes");
        scratch = new FrameScratch(ctx, "foliage grass scratch", BufferUse.Storage | BufferUse.Indirect | BufferUse.TransferSrc | BufferUse.TransferDst, 64ul << 20);
        counted =new ReadbackBuffer?[ctx.Device.Frames.Count];
        countedWritten = new int[ctx.Device.Frames.Count];
    }

    static uint[] Bindings(ShaderProgram p)
    {
        if (!p.PushDescriptors) throw new InvalidOperationException($"{p.Name}: the grass cull pushes its descriptors, the device cannot");
        return [.. p.ComputeReflection!.Blocks.Where(b => b.Kind == Gpu.Shaders.BlockKind.StorageBuffer && b.Set == 0).Select(b => (uint)b.Binding).Order()];
    }

    /// <summary>Whether the device can run the GPU grass (push descriptors, indirect draws with a count and a first instance).</summary>
    public static bool Supported(GpuContext ctx) =>
        ctx.Device.HasPushDescriptor && ctx.Device.DrawIndirectCount && ctx.Device.DrawIndirectFirstInstance && ctx.Device.MultiDrawIndirect;

    // ---- the blade arena ----

    /// <summary>The arena as a vertex buffer: blade <c>i</c> is at byte <c>i * <see cref="BladeBytes"/></c>.</summary>
    public Buffer BladeBuffer => arena.Buffer.Handle;
    public ulong ArenaBytes => arena.Buffer.Size;
    public ulong ArenaUsed => arena.Buffer.Size - arena.FreeBytes;
    /// <summary>Pages that could not be placed (arena or slot table full) since the start.</summary>
    public long Misses { get; set; }

    /// <summary>Room for <paramref name="blades"/> blades; empty when the arena is full.</summary>
    public ArenaRange AllocateBlades(int blades) => arena.Allocate((ulong)blades * BladeBytes, BladeBytes);

    /// <summary>The index of a range's first blade.</summary>
    public static uint FirstBlade(ArenaRange range) => (uint)(range.Offset / BladeBytes);

    public void FreeBlades(ArenaRange range) => arena.Free(range);

    /// <summary>Writes blade data at <paramref name="byteStart"/> of a range (through the frame's upload buffer; needs an open frame).</summary>
    public void WriteBlades(ArenaRange range, int byteStart, ReadOnlySpan<byte> data) => ctx.Uploads.Write(arena.Buffer, range.Offset + (ulong)byteStart, data);

    // ---- the slot table ----

    public int SlotCapacity { get; }
    /// <summary>One more than the highest slot ever handed out: the kernels' range.</summary>
    public int SlotHigh { get; private set; }
    public int SlotsUsed { get; private set; }

    /// <summary>A free slot, or -1 when the table is full.</summary>
    public int AllocateSlot()
    {
        lock (gate)
        {
            int slot;
            if (freeSlots.Count > 0) { slot = freeSlots[^1]; freeSlots.RemoveAt(freeSlots.Count - 1); }
            else if (SlotHigh < SlotCapacity) slot = SlotHigh++;
            else return -1;
            SlotsUsed++;
            return slot;
        }
    }

    bool initialised;

    /// <summary>The slot table starts as zeros (every slot has no blades), written once by the first frame that needs it.</summary>
    void Initialise()
    {
        if (initialised) return;
        var cmd = ctx.Frame.PreFrame;
        cmd.FillBuffer(slots.Handle, 0, slots.Size, 0);
        Barrier(cmd, PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit, PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit);
        initialised = true;
    }

    /// <summary>Writes a slot and its blade prefixes (<see cref="PrefixStride"/> counts; needs an open frame).</summary>
    public void WriteSlot(int slot, in GrassSlot record, ReadOnlySpan<int> prefix)
    {
        if (prefix.Length != PrefixStride) throw new ArgumentException($"{PrefixStride} prefix counts", nameof(prefix));
        Initialise();
        ctx.Uploads.Write(slots, (ulong)slot * GrassSlot.Size, MemoryMarshal.AsBytes(new ReadOnlySpan<GrassSlot>(in record)));
        ctx.Uploads.Write(prefixes, (ulong)slot * PrefixStride * 4, MemoryMarshal.AsBytes(prefix));
    }

    /// <summary>Stops a slot drawing now (its blade count written as 0, when a frame is open) and gives it back after the frames in flight.</summary>
    public void FreeSlot(int slot)
    {
        if (ctx.Frame.Open && initialised)
        {
            uint zero = 0;
            // The count is the fifth field of the record.
            ctx.Uploads.Write(slots, (ulong)slot * GrassSlot.Size + 16, MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(in zero)));
        }
        ctx.Device.Frames.DeferDelete(() =>
        {
            lock (gate) { freeSlots.Add(slot); SlotsUsed--; }
        });
    }

    // ---- per view ----

    /// <summary>Copies the tables of a view into the frame's constants (valid for the frame).</summary>
    public GrassTables Prepare(ReadOnlySpan<GrassZoneRow> zones, ReadOnlySpan<GrassPatchRow> patches)
    {
        // A table is never empty memory (a binding must have a range).
        Transient z = zones.IsEmpty ? ctx.Frame.Constants.Write<GrassZoneRow>(new GrassZoneRow[1], Align) : ctx.Frame.Constants.Write(zones, Align);
        Transient p = patches.IsEmpty ? ctx.Frame.Constants.Write<GrassPatchRow>(new GrassPatchRow[1], Align) : ctx.Frame.Constants.Write(patches, Align);
        return new GrassTables(z, zones.Length, p, patches.Length);
    }

    readonly FrameScratch scratch;
    int scratchSlot = -1;
    long scratchFrame = -1;
    int viewIndex;
    readonly ReadbackBuffer?[] counted;
    readonly int[] countedWritten;

    /// <summary>The draws and blades the view with this call number drew a frame ring ago (0 before): statistics for the CPU.</summary>
    public int LateDraws { get; private set; }
    public long LateBlades { get; private set; }

    void NewFrame()
    {
        var frame = ctx.Frame;
        if (scratchFrame == frame.Number) return;
        if (scratchSlot >= 0) countedWritten[scratchSlot] = viewIndex;
        (scratchFrame, scratchSlot, viewIndex) = (frame.Number, frame.Slot, 0);
    }

    /// <summary>The per-view lists' memory (<see cref="FrameScratch"/>).</summary>
    public FrameScratch Scratch => scratch;

    readonly List<(QuerySlot Begin, QuerySlot End)> pendingTimes = [];
    public double GpuMicroseconds { get; private set; }
    public long GpuTimedViews { get; private set; }
    public long Dispatched { get; private set; }

    void CollectTimes()
    {
        var q = ctx.Frame.Timestamps;
        for (int i = 0; i < pendingTimes.Count; i++)
        {
            var (b, e) = pendingTimes[i];
            if (b.Frame + ctx.Device.Frames.Count + 2 < ctx.Frame.Number) { pendingTimes.RemoveAt(i--); continue; }
            if (!q.TryRead(b, out ulong tb) || !q.TryRead(e, out ulong te)) continue;
            GpuMicroseconds += (te - tb) / 1000.0;
            GpuTimedViews++;
            pendingTimes.RemoveAt(i--);
        }
    }

    /// <summary>
    /// Records the cull of one view into <see cref="GpuFrame.PreFrame"/>: <paramref name="planes"/> (at most 8), the eye along the ground, the
    /// page size, the density as the prefix step and the fraction into it (<see cref="DensityStep"/>); for the motion pass <paramref name="motionScale"/> (<see cref="GrassMotionReach"/>, 0: the patches' ranges only). The draws are read by this frame's draw.
    /// </summary>
    public GrassResult Dispatch(ReadOnlySpan<Vector4> planes, Vector2 eye, float pageSize, int prefixIndex, float fraction, in GrassTables tables, float motionScale = 0)
    {
        int n = SlotHigh;
        if (n == 0 || tables.PatchCount == 0) return default;
        if (planes.Length > 8) throw new ArgumentException("at most 8 planes", nameof(planes));
        Initialise();
        CollectTimes();
        NewFrame();
        if (!(scratch.TryAllocate((ulong)n * 16, out var keysBuffer, out var keysOffset) && scratch.TryAllocate((ulong)n * 16, out var drawsBuffer, out var drawsOffset) &&
              scratch.TryAllocate(16, out var countersBuffer, out var countersOffset)))
            return default;
        var keys = (Buffer: keysBuffer, Offset: keysOffset);
        var draws = (Buffer: drawsBuffer, Offset: drawsOffset);
        var counters = (Buffer: countersBuffer, Offset: countersOffset);
        var viewData = ctx.Frame.Constants.Allocate(160, Align);
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

        Span<BufferBinding> b = stackalloc BufferBinding[8];
        b[0] = new BufferBinding(viewData.Handle, viewData.Offset, 160);
        b[1] = new BufferBinding(slots.Handle, 0, slots.Size);
        b[2] = new BufferBinding(prefixes.Handle, 0, prefixes.Size);
        b[3] = new BufferBinding(tables.Zones.Handle, tables.Zones.Offset, (ulong)Math.Max(tables.ZoneCount, 1) * 8);
        b[4] = new BufferBinding(tables.Patches.Handle, tables.Patches.Offset, (ulong)tables.PatchCount * GrassPatchRow.Size64);
        b[5] = new BufferBinding(keys.Buffer, keys.Offset, (ulong)n * 16);
        b[6] = new BufferBinding(draws.Buffer, draws.Offset, (ulong)n * 16);
        b[7] = new BufferBinding(counters.Buffer, counters.Offset, 16);

        var cmd = ctx.Frame.PreFrame;
        var stamps = (ctx.Frame.Timestamps.Allocate(), ctx.Frame.Timestamps.Allocate());
        FrameProfiler.PreStamp(cmd, StageClock.Uploads);
        cmd.BeginLabel("foliage grass");
        cmd.FillBuffer(counters.Buffer, counters.Offset, 16, 0);
        Barrier(cmd, PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        cmd.Timestamp(ctx.Frame.Timestamps, stamps.Item1, PipelineStageFlags2.ComputeShaderBit);
        uint groups = (uint)((n + FoliageGrassShaders.Group - 1) / FoliageGrassShaders.Group);
        Run(cmd, cull!, cullPipe!, cullBindings, b, groups);
        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit);
        Run(cmd, order!, orderPipe!, orderBindings, b, groups);
        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.DrawIndirectBit | PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.AllTransferBit,
            AccessFlags2.IndirectCommandReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit);
        cmd.Timestamp(ctx.Frame.Timestamps, stamps.Item2, PipelineStageFlags2.ComputeShaderBit);
        var ring = counted[scratchSlot] ??= ReadbackBuffer.Create(ctx, MaxViews * 16, $"foliage grass counters {scratchSlot}");
        if (viewIndex < MaxViews)
        {
            if (viewIndex < countedWritten[scratchSlot])
            {
                var v = MemoryMarshal.Cast<byte, uint>(ring.Read((ulong)viewIndex * 16, 16));
                (LateDraws, LateBlades) = ((int)v[0], v[1]);
            }
            else (LateDraws, LateBlades) = (0, 0);
            cmd.CopyBuffer(counters.Buffer, ring.Handle, new BufferCopy(counters.Offset, (ulong)viewIndex * 16, 16));
            viewIndex++;
        }
        cmd.EndLabel();
        FrameProfiler.PreStamp(cmd, StageClock.Cull);
        if (stamps.Item1.IsValid && stamps.Item2.IsValid) pendingTimes.Add(stamps);
        Dispatched++;
        return new GrassResult(draws.Buffer, draws.Offset, counters.Buffer, counters.Offset, n, tables.Patches);
    }

    void Run(CommandList cmd, ShaderProgram program, ComputePipeline pipe, uint[] bindings, ReadOnlySpan<BufferBinding> b, uint groups)
    {
        cmd.BindPipeline(pipe);
        int count = bindings.Length;
        var infos = stackalloc DescriptorBufferInfo[count];
        var writes = stackalloc WriteDescriptorSet[count];
        for (int i = 0; i < count; i++)
        {
            ref readonly var bb = ref b[(int)bindings[i]];
            infos[i] = new DescriptorBufferInfo(bb.Buffer, bb.Offset, bb.Size);
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstBinding = bindings[i], DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &infos[i],
            };
        }
        cmd.PushDescriptors(program.Layout, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, count), PipelineBindPoint.Compute);
        cmd.Dispatch(groups);
    }

    static void Barrier(CommandList cmd, PipelineStageFlags2 srcStages, AccessFlags2 srcAccess, PipelineStageFlags2 dstStages, AccessFlags2 dstAccess)
    {
        var batch = new BarrierBatch();
        batch.Add(srcStages, srcAccess, dstStages, dstAccess);
        cmd.Barrier(in batch);
    }

    /// <summary>The density setting as the prefix step and the fraction into it, as <see cref="FoliageGrassField.PrefixCount"/> takes them.</summary>
    public static (int Index, float Fraction) DensityStep(float fraction)
    {
        float at = Math.Clamp(fraction, 0, 1) * FoliageGrassField.PrefixSteps;
        int i = Math.Min((int)at, FoliageGrassField.PrefixSteps - 1);
        return (i, at - i);
    }

    /// <summary>Bytes <see cref="CopyForReadback"/> writes: the counters (16), then the draws.</summary>
    public static ulong ReadbackBytes(in GrassResult r) => 16 + (ulong)r.MaxDraws * 16;

    /// <summary>Copies a result (counters, then every draw slot) into <paramref name="target"/>, after the kernels (the verify mode).</summary>
    public void CopyForReadback(in GrassResult r, ReadbackBuffer target)
    {
        var cmd = ctx.Frame.PreFrame;
        cmd.CopyBuffer(r.Counters, target.Handle, new BufferCopy(r.CountersOffset, 0, 16));
        cmd.CopyBuffer(r.Draws, target.Handle, new BufferCopy(r.DrawsOffset, 16, (ulong)r.MaxDraws * 16));
    }

    public void Dispose()
    {
        foreach (var p in new[] { cull, order })
            if (p is not null)
            {
                ctx.Pipelines.Forget(p);
                p.Dispose();
            }
        arena.Dispose();
        slots.Dispose();
        prefixes.Dispose();
        scratch.Dispose();
        foreach (var c in counted) c?.Dispose();
    }
}
