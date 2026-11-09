using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// A run of at most <see cref="FoliageShaders.CullChunk"/> instances of one group (std430, 32 bytes): where they are in the instance arena, and the
/// group's range for the view as the CPU computed it (<see cref="FoliageGroupRange"/>). <see cref="Flags"/> 0 for the foliage meshes;
/// <see cref="Rock"/> for TERRAIN-mode rocks (their records' <c>Ground.W</c> from <see cref="FoliageCull.RockBits"/>), with <see cref="Mirrored"/>
/// for the run of the group's mirroring placements.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FoliageCullChunk
{
    /// <summary>
    /// The chunk's first instance in the arena (in records) and how many it has.
    /// </summary>
    public uint First, Count;
    /// <summary>
    /// The group's range for the view (ground units), its square, and the reciprocal of its fade band.
    /// </summary>
    public float Range, RangeSquared, InverseBand;
    /// <summary>
    /// <see cref="Rock"/>, <see cref="Mirrored"/>, <see cref="ImpostorMesh"/>, <see cref="Impostor"/> and <see cref="Lod"/>; 0 for a foliage mesh.
    /// </summary>
    public uint Flags;
    /// <summary>
    /// With <see cref="ImpostorMesh"/> or <see cref="Impostor"/>: the group's transition and the crossfade band's reciprocal
    /// (<see cref="FoliageGroupRange"/>).
    /// </summary>
    public float Transition, InverseTransitionBand;
    /// <summary>
    /// With <see cref="Lod"/>: the deviation, in radii of the mesh, of the level this chunk holds and of the next coarser one (infinite for the
    /// last).
    /// </summary>
    public float LodError, LodNextError;
    /// <summary>
    /// The chunk range [BatchStart, BatchEnd) of the batch this chunk belongs to (<see cref="FoliageShaders.ScanCompute"/> lays a batch's instances
    /// out solid first, then the fading ones).
    /// </summary>
    public uint BatchStart, BatchEnd;

    /// <summary>
    /// The chunk's size in bytes (std430).
    /// </summary>
    public const int Size = 48;
    /// <summary>
    /// <see cref="ImpostorMesh"/>: the meshes of a group with an impostor (before its transition); <see cref="Impostor"/>: its impostors.
    /// </summary>
    public const uint Rock = 1, Mirrored = 2, ImpostorMesh = 4, Impostor = 8;
    /// <summary>
    /// A generated mesh level's chunk: its instances are those the view picks this level for (<see cref="FoliageLodView"/>).
    /// </summary>
    public const uint Lod = 16;
}

/// <summary>
/// What a view's rock chunks write into row 0 w (<see cref="FoliageShaders.CompactCompute"/>): with <see cref="BiomeRows"/> (the colour views) the
/// biome row when <see cref="Resident"/> has its bit (else -1), without it 0; as <see cref="TerrainRenderer.DrawMeshes"/> writes the placements.
/// </summary>
public struct FoliageRockView
{
    /// <summary>
    /// The view writes biome rows (the colour views); else 0.
    /// </summary>
    public bool BiomeRows;
    /// <summary>
    /// A bit per biome row (256), row r at word r / 32, bit r % 32.
    /// </summary>
    public ResidentBits Resident;

    /// <summary>
    /// Eight words of bits inline (256 biome rows).
    /// </summary>
    [System.Runtime.CompilerServices.InlineArray(8)]
    public struct ResidentBits
    {
        /// <summary>
        /// The first of the eight words.
        /// </summary>
        uint _first;
    }
}

/// <summary>
/// What a view tells the cull about generated mesh levels (<see cref="FoliageCullChunk.Lod"/>): a level is used while its deviation shows less than
/// <see cref="Tolerance"/>. A perspective view measures it in pixels, <see cref="Scale"/> the pixels per radian of the render and <see cref="EyeY"/>
/// the eye's height (the cull has only its ground position); an orthographic one (a shadow cascade) in texels, <see cref="Scale"/> being 1 / the
/// texel. Tolerance 0 uses the original everywhere.
/// </summary>
public readonly record struct FoliageLodView(float EyeY, float Scale, float Tolerance, bool Ortho, float FarDistance = 0);

/// <summary>
/// An indirect draw the scan fills (std430, 16 bytes): the part's index count (from <see cref="FirstIndex"/>, a generated level's place in the index
/// buffer) and the chunks [ChunkStart, ChunkEnd) of its batch.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FoliageCullDraw
{
    /// <summary>
    /// The part's index count, its batch's chunk range [ChunkStart, ChunkEnd) and its first index.
    /// </summary>
    public uint IndexCount, ChunkStart, ChunkEnd, FirstIndex;

    /// <summary>
    /// The draw's size in bytes (std430).
    /// </summary>
    public const int Size = 16;
}

/// <summary>
/// The kernels' push constants (24 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
struct FoliageCullPush
{
    /// <summary>
    /// The eye's ground position (x, z).
    /// </summary>
    public Vector2 Eye;
    /// <summary>
    /// How many of the view's planes are used (at most 8), and the work list's chunk and draw counts.
    /// </summary>
    public uint PlaneCount, ChunkCount, DrawCount;
    /// <summary>
    /// The fade at and above which an instance is drawn whole (the mesh shader's 2).
    /// </summary>
    public float FullThreshold;
    /// <summary>
    /// 1: lay each batch out solid instances first, then the ones the mesh shader dithers (colour views with <see cref="EarlyDepth"/>).
    /// </summary>
    public uint Split;
}

/// <summary>
/// A view's work list in the frame's constants: the chunks in draw order and the draws, reused by the views that share it (the shadow cascades of a
/// frame).
/// </summary>
public readonly record struct FoliageCullWork(Transient Chunks, int ChunkCount, Transient Draws, int DrawCount)
{
    /// <summary>
    /// The chunk slots (<see cref="FoliageShaders.CullChunk"/> per chunk): what the fades need.
    /// </summary>
    public int Candidates => ChunkCount * FoliageShaders.CullChunk;
    /// <summary>
    /// The instances the chunks hold (their counts summed; -1: not given, the chunk slots are used): what the rows can need at most.
    /// </summary>
    public int Instances { get; init; } = -1;
}

/// <summary>
/// What one view's cull wrote: the compacted matrices (bind at locations 7 to 10), the indirect arguments (one per draw, 20 bytes each), and the
/// chunks' output offsets (ChunkCount + 1, the last one the total).
/// </summary>
public readonly record struct FoliageCullResult(GpuBuffer Rows, ulong RowsOffset, ulong RowsBytes, GpuBuffer Args, ulong ArgsOffset,
    GpuBuffer Offsets, ulong OffsetsOffset, int ChunkCount, int DrawCount, bool Split = false)
{
    /// <summary>
    /// Nothing to draw (no draws, or the view was left out).
    /// </summary>
    public bool IsEmpty => DrawCount == 0;
}

/// <summary>
/// The GPU foliage cull (docs/renderer-native.md 5.3, step A2): the instance arena (5.2: every group's <see cref="FoliageInstanceRecord"/>s,
/// uploaded at their first cull), and per view the three kernels of <see cref="FoliageShaders"/> recorded into
/// <see cref="GpuFrame.PreFrame"/>, so they run before every pass of the frame while the draws that read their output are recorded where the
/// CPU path drew (a shadow cascade, the scene, the reflection). Per-view outputs live in device memory of the frame's slot.
/// Render thread only.
/// </summary>
public sealed unsafe class FoliageGpuCull : IDisposable
{
    #region Kernels and lifetime

    /// <summary>
    /// The alignment of the work lists and view constants in the frame's constants, in bytes.
    /// </summary>
    const ulong Align = 256;
    /// <summary>
    /// The kernels' View buffer (<c>ViewData</c>): 8 planes, their normals' lengths, the resident biome bits, the mode, the fog cull
    /// (<see cref="FogVolumes.CullVectors"/> vec4s), the occlusion cull (<see cref="HizPyramid.ViewVectors"/> vec4s), the LOD view, the interior
    /// masks' boxes.
    /// </summary>
    const ulong ViewBytes = InteriorOffset + MaxInteriorBoxes * 16;
    /// <summary>
    /// Where the interior boxes start in the View buffer, in bytes.
    /// </summary>
    const ulong InteriorOffset = 208 + FogVolumes.CullVectors * 16 + HizPyramid.ViewVectors * 16 + 16;
    /// <summary>
    /// The interior masks' boxes the cull takes (<c>ViewData.interior</c>); more are merged into the last.
    /// </summary>
    public const int MaxInteriorBoxes = 32;
    /// <summary>
    /// The device the kernels run on.
    /// </summary>
    readonly IGpuContext _ctx;
    /// <summary>
    /// The three kernels: the cull, the scan and the compaction (<see cref="FoliageShaders"/>).
    /// </summary>
    readonly GpuProgram _cull, _scan, _compact;
    /// <summary>
    /// The kernels' compute pipelines.
    /// </summary>
    readonly GpuComputePipeline _cullPipe, _scanPipe, _compactPipe;
    /// <summary>
    /// Each kernel's storage-buffer bindings at set 0, in order (<see cref="Bindings"/>).
    /// </summary>
    readonly uint[] _cullBindings, _scanBindings, _compactBindings;

    /// <summary>
    /// Compiles the kernels and makes the instance arena (<paramref name="arenaBytes"/>), the per-view memory (capped at
    /// <paramref name="scratchCap"/>) and the read-back rings.
    /// </summary>
    public FoliageGpuCull(IGpuContext ctx, ulong arenaBytes = 16ul << 20, ulong scratchCap = 512ul << 20)
    {
        this._ctx = ctx;
        _cull = ctx.Shaders.Compute(FoliageShaders.CullCompute, "foliage cull");
        _scan = ctx.Shaders.Compute(FoliageShaders.ScanCompute, "foliage cull scan");
        _compact = ctx.Shaders.Compute(FoliageShaders.CompactCompute, "foliage cull compact");
        _cullPipe = ctx.Pipelines.Get(new ComputePipelineDesc(_cull, "foliage cull"));
        _scanPipe = ctx.Pipelines.Get(new ComputePipelineDesc(_scan, "foliage cull scan"));
        _compactPipe = ctx.Pipelines.Get(new ComputePipelineDesc(_compact, "foliage cull compact"));
        _cullBindings = Bindings(_cull);
        _scanBindings = Bindings(_scan);
        _compactBindings = Bindings(_compact);
        _arena = new BufferArena(ctx, arenaBytes, BufferUse.Storage | BufferUse.TransferDst, "foliage instances");
        _scratch = new FrameScratch(ctx, "foliage cull scratch", BufferUse.Storage | BufferUse.Vertex | BufferUse.Indirect | BufferUse.TransferSrc,
            scratchCap, minimum: 4ul << 20);
        _visible = new IGpuReadback?[ctx.FramesInFlight];
        _visibleWritten = new int[ctx.FramesInFlight];
        _fogTotals = new IGpuReadback?[ctx.FramesInFlight];
        _fogWritten = new int[ctx.FramesInFlight];
        _occTotals = new IGpuReadback?[ctx.FramesInFlight];
        _occWritten = new int[ctx.FramesInFlight];
    }

    /// <summary>
    /// Frees the kernels, the arena, the per-view memory and the read-backs.
    /// </summary>
    public void Dispose()
    {
        foreach (var p in new[] { _cull, _scan, _compact })
        {
            _ctx.Pipelines.Forget(p);
            _ctx.Shaders.Dispose(p);
        }
        _arena.Dispose();
        _scratch.Dispose();
        foreach (var v in _visible) v?.Dispose();
        foreach (var v in _fogTotals) v?.Dispose();
        foreach (var v in _occTotals) v?.Dispose();
    }

    /// <summary>
    /// A kernel's storage-buffer bindings at set 0, sorted.
    /// </summary>
    static uint[] Bindings(GpuProgram p) =>
        [.. p.ComputeReflection!.Blocks.Where(b => b.Kind == Gpu.Shaders.BlockKind.StorageBuffer && b.Set == 0).Select(b => (uint)b.Binding).Order()];

    #endregion

    #region Instance arena

    /// <summary>
    /// The instance arena; replaced by a larger one when full (<see cref="Generation"/>).
    /// </summary>
    BufferArena _arena;
    /// <summary>
    /// Bumped whenever the arena is replaced by a larger one: a group placed in an older generation is uploaded again.
    /// </summary>
    public int Generation { get; private set; } = 1;
    /// <summary>
    /// Scratch for packing records into floats before their upload, made on first use.
    /// </summary>
    float[]? _packed;
    /// <summary>
    /// Backing field of <see cref="MayGrow"/>.
    /// </summary>
    Func<ulong, bool>? _mayGrow;
    /// <summary>
    /// Bytes of the instance arena, and bytes in use.
    /// </summary>
    public ulong ArenaBytes => _arena.Buffer.Size;
    /// <summary>
    /// Bytes of the arena in use.
    /// </summary>
    public ulong ArenaUsed => _arena.Buffer.Size - _arena.FreeBytes;
    /// <summary>
    /// Times the arena was replaced by a larger one.
    /// </summary>
    public int Grows { get; private set; }
    /// <summary>
    /// Records uploaded into the arena so far.
    /// </summary>
    public long UploadedInstances { get; private set; }
    /// <summary>
    /// Records whose packing was not lossless (<see cref="FoliageInstanceRecord.Pack"/>); 0 for every placement the game makes.
    /// </summary>
    public long PackMismatches { get; private set; }
    /// <summary>
    /// Asked before the arena or the scratch memory grows: false refuses (<c>VramGuard.Allows</c>). Also given to <see cref="Scratch"/>.
    /// </summary>
    public Func<ulong, bool>? MayGrow
    {
        get => _mayGrow;
        set
        {
            _mayGrow = value;
            _scratch.MayGrow = value;
        }
    }
    /// <summary>
    /// Times the arena could not grow for the memory (the group was left out of the frame).
    /// </summary>
    public int GrowRefusals { get; private set; }

    /// <summary>
    /// Puts a group's records into the arena (when not there in this <see cref="Generation"/>) and returns the index of its first record.
    /// False when the arena is full: it has then been replaced by one twice the size (<see cref="Generation"/> moved), and every group placed
    /// so far must be placed again before a dispatch uses them. The upload goes into the frame's upload buffer, so the frame's dispatches see
    /// it; the arena is never marked as used by a frame (<see cref="DeviceBuffer.Binding"/>), since a range is written only while no
    /// dispatch of the frame names it (a group is placed before the chunks naming it are written).
    /// </summary>
    public bool Place(ref ArenaRange range, ref int generation, ReadOnlySpan<FoliageInstanceRecord> records)
    {
        if (generation == Generation) return true;
        ulong bytes = (ulong)records.Length * FoliageInstanceRecord.GpuSize;
        var r = _arena.Allocate(bytes, FoliageInstanceRecord.GpuSize);
        if (r.IsEmpty && bytes > 0)
        {
            // A refused growth (the video memory is nearly used up, VramGuard) leaves the range empty: the caller leaves the group out of the frame.
            if (MayGrow is { } may && !may(GrowSize(bytes)))
            {
                GrowRefusals++;
                (range, generation) = (default, 0);
                return true;
            }
            Grow(bytes);
            return false;
        }

        // Packed in slices (17 floats a record, FoliageInstanceRecord.Pack) into a reused array, then into the frame's upload buffer.
        const int slice = 4096;
        const int floats = FoliageInstanceRecord.GpuSize / sizeof(float);
        _packed ??= new float[slice * floats];
        for (int at = 0; at < records.Length; at += slice)
        {
            int n = Math.Min(slice, records.Length - at);
            for (int i = 0; i < n; i++)
                if (!FoliageInstanceRecord.Pack(in records[at + i], _packed.AsSpan(i * floats, floats)))
                    PackMismatches++;
            using var batch = _ctx.Uploads.Begin();
            batch.Write(_arena.Buffer, r.Offset + (ulong)at * FoliageInstanceRecord.GpuSize, MemoryMarshal.AsBytes(_packed.AsSpan(0, n * floats)));
        }
        (range, generation) = (r, Generation);
        UploadedInstances += records.Length;
        return true;
    }

    /// <summary>
    /// The first record of a placed range.
    /// </summary>
    public static uint FirstOf(ArenaRange range) => (uint)(range.Offset / FoliageInstanceRecord.GpuSize);

    /// <summary>
    /// Gives a group's range back (after the frames in flight), when it is from the current arena.
    /// </summary>
    public void Free(ArenaRange range, int generation)
    {
        if (generation == Generation && !range.IsEmpty) _arena.Free(range);
    }

    /// <summary>
    /// The arena's next size: doubled until it holds twice <paramref name="atLeast"/>.
    /// </summary>
    ulong GrowSize(ulong atLeast)
    {
        ulong size = _arena.Buffer.Size * 2;
        while (size < atLeast * 2) size *= 2;
        return size;
    }

    /// <summary>
    /// Replaces the arena with a larger one (the old one freed after this frame's dispatches) and bumps <see cref="Generation"/>.
    /// </summary>
    void Grow(ulong atLeast)
    {
        ulong size = GrowSize(atLeast);
        _arena.Dispose();   // deferred: this frame's earlier dispatches may read it
        _arena = new BufferArena(_ctx, size, BufferUse.Storage | BufferUse.TransferDst, "foliage instances");
        Generation++;
        Grows++;
    }

    #endregion

    #region Views

    /// <summary>
    /// Device memory per frame slot for the per-view outputs (fades, counts, offsets, arguments, rows): bump allocated, reset per frame, sized by
    /// what the frames need (<see cref="FrameScratch"/>), capped.
    /// </summary>
    readonly FrameScratch _scratch;
    /// <summary>
    /// The GPU frame the per-view state is for (-1: none yet; <see cref="NewFrame"/>).
    /// </summary>
    long _scratchFrame = -1;
    /// <summary>
    /// The frame slot of <see cref="_scratchFrame"/> (-1: none yet).
    /// </summary>
    int _scratchSlot = -1;
    /// <summary>
    /// The per-view outputs' memory: its size, the need of the frames, and what it refused (<see cref="FrameScratch"/>).
    /// </summary>
    public FrameScratch Scratch => _scratch;

    /// <summary>
    /// Copies a view's chunks and draws into the frame's constants (valid for the frame).
    /// </summary>
    public FoliageCullWork Prepare(ReadOnlySpan<FoliageCullChunk> chunks, ReadOnlySpan<FoliageCullDraw> draws)
    {
        var c = _ctx.Frame.WriteConstants(MemoryMarshal.AsBytes(chunks), Align);
        var d = _ctx.Frame.WriteConstants(MemoryMarshal.AsBytes(draws), Align);
        return new FoliageCullWork(c, chunks.Length, d, draws.Length);
    }

    /// <summary>
    /// Records the cull of one view into <see cref="GpuFrame.PreFrame"/>: the planes of <paramref name="view"/> (at most 8) with their normals'
    /// lengths, the eye along the ground, the work list; <paramref name="fullThreshold"/> the fade at and above which an instance is drawn whole
    /// (the mesh shader's 2). The result's buffers are read by this frame's draws.
    /// </summary>
    public FoliageCullResult Dispatch(in FoliageCullWork work, FoliageCullView view, Vector2 eye, float fullThreshold = 0.999f) =>
        Dispatch(work, view, eye, default, fullThreshold);

    /// <summary>
    /// <see cref="Dispatch(in FoliageCullWork, FoliageCullView, Vector2, float)"/> with what the view's rock chunks write
    /// (<see cref="FoliageRockView"/>).
    /// </summary>
    public FoliageCullResult Dispatch(in FoliageCullWork work, FoliageCullView view, Vector2 eye, in FoliageRockView rock,
        float fullThreshold = 0.999f, ReadOnlySpan<Vector4> fogCull = default, in OcclusionView occlusion = default, FoliageLodView lod = default,
        bool split = false, IReadOnlyList<(Vector2 Min, Vector2 Max)>? interior = null)
    {
        if (work.ChunkCount == 0) return default;
        if (view.Planes.Length > 8) throw new ArgumentException("at most 8 planes", nameof(view));
        CollectTimes();
        int n = work.ChunkCount;
        NewFrame();

        // The rows need a slot per instance that can be visible (the chunks' counts summed), not per chunk slot (a chunk of a small group is mostly
        // empty).
        ulong rowsBytes = (ulong)(work.Instances >= 0 ? work.Instances : work.Candidates) * 64;
        if (!(_scratch.TryAllocate((ulong)work.Candidates * 4, out var fadesBuffer, out var fadesOffset) &&
              _scratch.TryAllocate((ulong)n * 16, out var countsBuffer, out var countsOffset) &&
              _scratch.TryAllocate((ulong)(2 * n + 5) * 4, out var offsetsBuffer, out var offsetsOffset) &&
              _scratch.TryAllocate((ulong)Math.Max(work.DrawCount, 1) * 60, out var argsBuffer, out var argsOffset) &&
              _scratch.TryAllocate(rowsBytes, out var rowsBuffer, out var rowsOffset)))
            return default;   // over the cap: this view is left out of the frame (FrameScratch); the guard shortens the ranges
        var fades = (Buffer: fadesBuffer, Offset: fadesOffset);
        var counts = (Buffer: countsBuffer, Offset: countsOffset);
        var offsets = (Buffer: offsetsBuffer, Offset: offsetsOffset);
        var args = (Buffer: argsBuffer, Offset: argsOffset);
        var rows = (Buffer: rowsBuffer, Offset: rowsOffset);
        var viewData = WriteViewData(view, rock, fogCull, occlusion, lod, interior);

        Span<GpuBufferBinding> b = stackalloc GpuBufferBinding[10];
        b[0] = new GpuBufferBinding(viewData.Handle, viewData.Offset, ViewBytes);
        b[1] = new GpuBufferBinding(_arena.Buffer.Handle, 0, _arena.Buffer.Size);
        b[2] = new GpuBufferBinding(work.Chunks.Handle, work.Chunks.Offset, (ulong)n * FoliageCullChunk.Size);
        b[3] = new GpuBufferBinding(fades.Buffer, fades.Offset, (ulong)work.Candidates * 4);
        b[4] = new GpuBufferBinding(counts.Buffer, counts.Offset, (ulong)n * 16);
        b[5] = new GpuBufferBinding(offsets.Buffer, offsets.Offset, (ulong)(2 * n + 5) * 4);
        b[6] = new GpuBufferBinding(work.Draws.Handle, work.Draws.Offset, (ulong)Math.Max(work.DrawCount, 1) * FoliageCullDraw.Size);
        b[7] = new GpuBufferBinding(args.Buffer, args.Offset, (ulong)Math.Max(work.DrawCount, 1) * 60);
        b[8] = new GpuBufferBinding(rows.Buffer, rows.Offset, rowsBytes);
        b[9] = occlusion.IsEmpty ? new GpuBufferBinding(_arena.Buffer.Handle, 0,
            _arena.Buffer.Size) : occlusion.Buffer;   // the pyramid (a stand-in when the occlusion cull is off)
        var push = new FoliageCullPush { Eye = eye, PlaneCount = (uint)view.Planes.Length, ChunkCount = (uint)n, DrawCount = (uint)work.DrawCount,
                FullThreshold = fullThreshold, Split = split ? 1u : 0u };

        var cmd = _ctx.Frame.PreFrame;
        var stamps = (_ctx.Frame.Timestamps.Allocate(), _ctx.Frame.Timestamps.Allocate());
        FrameProfiler.PreStamp(cmd, StageClock.Uploads);
        cmd.BeginLabel("foliage cull");

        // The arena's uploads (and anything else uploaded so far) before the reads.
        Barrier(cmd, Access.TransferDst, Access.StorageRead);
        cmd.Timestamp(_ctx.Frame.Timestamps, stamps.Item1, GpuStage.Compute);
        uint x = (uint)Math.Min(n, 65535), y = (uint)((n + 65534) / 65535);
        Run(cmd, _cull, _cullPipe, _cullBindings, b, push, x, y);
        Barrier(cmd, Access.StorageWrite, Access.StorageRead);
        Run(cmd, _scan, _scanPipe, _scanBindings, b, push, 1, 1);
        Barrier(cmd, Access.StorageWrite, Access.StorageRead);
        Run(cmd, _compact, _compactPipe, _compactBindings, b, push, x, y);

        // The draws read the arguments and the rows (the upload buffer's closing full barrier orders them too; this one says what for).
        Barrier(cmd, Access.StorageWrite, Access.IndirectRead | Access.VertexRead | Access.TransferSrc);
        cmd.Timestamp(_ctx.Frame.Timestamps, stamps.Item2, GpuStage.Compute);
        CopyTotals(cmd, offsets.Buffer, offsets.Offset, n, fogCull, occlusion);
        if (CountTriangles) CopyTally(cmd, args.Buffer, args.Offset, work.DrawCount);
        cmd.EndLabel();
        FrameProfiler.PreStamp(cmd, StageClock.Cull);
        if (stamps.Item1.IsValid && stamps.Item2.IsValid) _pendingTimes.Add((stamps.Item1, stamps.Item2, TimingClass));
        Dispatched++;
        return new FoliageCullResult(rows.Buffer, rows.Offset, rowsBytes, args.Buffer, args.Offset, offsets.Buffer, offsets.Offset, n, work.DrawCount,
            split);
    }

    /// <summary>
    /// The view's constants in this frame's constants: the planes and their normals' lengths, the rocks' resident rows, the modes, the fog and
    /// occlusion vectors, the level view and the interior boxes.
    /// </summary>
    Transient WriteViewData(FoliageCullView view, in FoliageRockView rock, ReadOnlySpan<Vector4> fogCull, in OcclusionView occlusion,
        FoliageLodView lod, IReadOnlyList<(Vector2 Min, Vector2 Max)>? interior)
    {
        var viewData = _ctx.Frame.AllocateConstants(ViewBytes, Align);
        var planes = (Vector4*)viewData.Pointer;
        var lengths = (float*)(viewData.Pointer + 128);
        for (int i = 0; i < 8; i++) planes[i] = i < view.Planes.Length ? view.Planes[i] : default;
        for (int i = 0; i < 8; i++) lengths[i] = i < view.Planes.Length ? view.NormalLengths[i] : 0;
        var resident = (uint*)(viewData.Pointer + 160);
        for (int i = 0; i < 8; i++) resident[i] = rock.Resident[i];
        var mode = (uint*)(viewData.Pointer + 192);
        (mode[0], mode[1], mode[2], mode[3]) = (rock.BiomeRows ? 1u : 0u, fogCull.Length == FogVolumes.CullVectors ? 1u : 0u,
            occlusion.IsEmpty ? 0u : 1u, (uint)Math.Min(interior?.Count ?? 0, MaxInteriorBoxes));
        var fogData = (Vector4*)(viewData.Pointer + 208);
        for (int i = 0; i < FogVolumes.CullVectors; i++) fogData[i] = fogCull.Length == FogVolumes.CullVectors ? fogCull[i] : default;
        var hzData = fogData + FogVolumes.CullVectors;
        for (int i = 0; i < HizPyramid.ViewVectors; i++) hzData[i] = occlusion.IsEmpty ? default : occlusion.Vectors[i];
        hzData[HizPyramid.ViewVectors] = new Vector4(lod.EyeY, lod.Scale, lod.Tolerance, lod.Ortho ? 1 : -lod.FarDistance);

        // The interior masks (a mesh instance over one is drawn with the cut: docs/render-interior-cut.md); past the last slot each box widens the
        // last.
        var boxes = (Vector4*)(viewData.Pointer + InteriorOffset);
        for (int i = 0; i < (interior?.Count ?? 0); i++)
        {
            var (min, max) = interior![i];
            int at = Math.Min(i, MaxInteriorBoxes - 1);
            boxes[at] = i < MaxInteriorBoxes ? new Vector4(min.X, min.Y, max.X, max.Y)
                : new Vector4(MathF.Min(boxes[at].X, min.X), MathF.Min(boxes[at].Y, min.Y), MathF.Max(boxes[at].Z, max.X), MathF.Max(boxes[at].W,
                        max.Y));
        }
        return viewData;
    }

    /// <summary>
    /// Binds a kernel's pipeline, its storage buffers (picked from <paramref name="b"/> by its bindings) and the push constants, then dispatches
    /// <paramref name="x"/> × <paramref name="y"/> groups.
    /// </summary>
    void Run(IGpuCommandList cmd, GpuProgram p, GpuComputePipeline pipe, uint[] bindings, ReadOnlySpan<GpuBufferBinding> b, in FoliageCullPush push,
        uint x, uint y)
    {
        cmd.BindPipeline(pipe);
        int count = bindings.Length;
        var list = stackalloc GpuBinding[count];
        for (int i = 0; i < count; i++)
        {
            ref readonly var bb = ref b[(int)bindings[i]];
            list[i] = GpuBinding.Storage(bindings[i], bb);
        }
        cmd.BindProgramResources(p, 0, new ReadOnlySpan<GpuBinding>(list, count));
        cmd.PushConstants(p, GpuStage.Compute, in push);
        cmd.Dispatch(x, y);
    }

    /// <summary>
    /// Orders a pass's work against the next one's (the engine's words; the backend's masks).
    /// </summary>
    static void Barrier(IGpuCommandList cmd, Access from, Access to) => cmd.Barrier([new GpuDependency(from, to)]);

    /// <summary>
    /// The bytes <see cref="CopyForReadback"/> writes for <paramref name="r"/>: the offsets, the arguments and the rows, each 16-aligned.
    /// </summary>
    public static ulong ReadbackBytes(in FoliageCullResult r) =>
        Align16((ulong)(r.ChunkCount + 1) * 4) + Align16((ulong)r.DrawCount * 20) + r.RowsBytes;

    /// <summary>
    /// <paramref name="v"/> rounded up to a multiple of 16.
    /// </summary>
    public static ulong Align16(ulong v) => (v + 15) / 16 * 16;

    /// <summary>
    /// Copies a result (offsets, arguments, rows) into <paramref name="target"/> at 0, (ChunkCount + 1) × 4 and then the arguments' end (16-aligned),
    /// after the dispatches (the tests read them back); <see cref="ReadbackBytes"/> is the size it needs.
    /// </summary>
    public void CopyForReadback(in FoliageCullResult r, IGpuReadback target)
    {
        var cmd = _ctx.Frame.PreFrame;
        ulong o = 0;
        ulong offsetsBytes = (ulong)(r.ChunkCount + 1) * 4, argsBytes = (ulong)r.DrawCount * 20;
        cmd.CopyBuffer(r.Offsets, target.Handle, new GpuBufferCopy(r.OffsetsOffset, o, offsetsBytes));
        o += Align16(offsetsBytes);
        if (argsBytes > 0) cmd.CopyBuffer(r.Args, target.Handle, new GpuBufferCopy(r.ArgsOffset, o, argsBytes));
        o += Align16(argsBytes);
        if (r.RowsBytes > 0) cmd.CopyBuffer(r.Rows, target.Handle, new GpuBufferCopy(r.RowsOffset, o, r.RowsBytes));
    }

    #endregion

    #region Late totals

    /// <summary>
    /// The visible total of each view, copied per frame slot and call number, read when the slot comes round (a frame ring later): at most this many
    /// views a frame.
    /// </summary>
    const int MaxViews = 256;
    /// <summary>
    /// The visible totals' read-back per frame slot (<see cref="MaxViews"/> uints).
    /// </summary>
    readonly IGpuReadback?[] _visible;
    /// <summary>
    /// How many views each slot's visible totals hold.
    /// </summary>
    readonly int[] _visibleWritten;
    /// <summary>
    /// This frame's views so far (the next view's call number).
    /// </summary>
    int _viewIndex;
    /// <summary>
    /// The most views a frame whose fog-culled and occluded totals are copied.
    /// </summary>
    const int MaxFogViews = 16;
    /// <summary>
    /// The fog-culled totals' read-back per frame slot.
    /// </summary>
    readonly IGpuReadback?[] _fogTotals;
    /// <summary>
    /// How many fog views each slot's totals hold.
    /// </summary>
    readonly int[] _fogWritten;
    /// <summary>
    /// This frame's fog views so far.
    /// </summary>
    int _fogIndex;
    /// <summary>
    /// The occluded totals' read-back per frame slot.
    /// </summary>
    readonly IGpuReadback?[] _occTotals;
    /// <summary>
    /// How many occlusion views each slot's totals hold.
    /// </summary>
    readonly int[] _occWritten;
    /// <summary>
    /// This frame's occlusion views so far.
    /// </summary>
    int _occIndex;
    /// <summary>
    /// The visible instances the view with this call number drew a frame ring ago (0 before): a statistic for the CPU, which no longer knows the
    /// count when it draws (<see cref="FoliageRenderer.DrawnInstances"/>).
    /// </summary>
    public int LateVisible { get; private set; }
    /// <summary>
    /// The instances the fog cull left out of all the views of the frame this slot drew a frame ring ago, given at the first fog view of a frame (0
    /// for the later ones and when it was off). The views' order varies from frame to frame (the shadow cascades), so they are summed, not matched by
    /// number.
    /// </summary>
    public int LateFogCulled { get; private set; }
    /// <summary>
    /// The instances the occlusion cull left out of the frame's main colour views (the slot's previous frame, as <see cref="LateFogCulled"/>).
    /// </summary>
    public int LateOccluded { get; private set; }
    /// <summary>
    /// The last dispatch with the occlusion cull was the first of its frame (so <see cref="LateOccluded"/> is new).
    /// </summary>
    public bool OccludedIsFresh { get; private set; }

    /// <summary>
    /// At a frame's first dispatch: notes what the last slot's read-backs hold, starts the counts again for this frame's slot and collects the tally
    /// when it is on.
    /// </summary>
    void NewFrame()
    {
        var frame = _ctx.Frame;
        if (_scratchFrame == frame.Number) return;
        if (_scratchSlot >= 0) (_visibleWritten[_scratchSlot], _fogWritten[_scratchSlot], _occWritten[_scratchSlot]) = (_viewIndex, _fogIndex,
                _occIndex);   // what the slot's buffer holds when it comes round
        (_scratchFrame, _scratchSlot, _viewIndex, _fogIndex, _occIndex) = (frame.Number, frame.Slot, 0, 0, 0);
        if (CountTriangles) CollectTally();
    }

    /// <summary>
    /// The totals for the CPU's statistics (fog-culled, occluded, visible), copied out of the offsets and read a frame ring later (the slot's
    /// previous frame has completed: its fence was waited for).
    /// </summary>
    void CopyTotals(IGpuCommandList cmd, GpuBuffer offsetsBuffer, ulong offsetsOffset, int n, ReadOnlySpan<Vector4> fogCull, in OcclusionView occlusion)
    {
        LateFogCulled = 0;
        if (fogCull.Length == FogVolumes.CullVectors && _fogIndex < MaxFogViews)
        {
            var fogRead = _fogTotals[_scratchSlot] ??= _ctx.CreateReadback(MaxFogViews * 4, $"foliage fog cull totals {_scratchSlot}");
            if (_fogIndex == 0 && _fogWritten[_scratchSlot] > 0)
                foreach (uint v in MemoryMarshal.Cast<byte, uint>(fogRead.Read(0, (ulong)_fogWritten[_scratchSlot] * 4))) LateFogCulled += (int)v;
            cmd.CopyBuffer(offsetsBuffer, fogRead.Handle, new GpuBufferCopy(offsetsOffset + (ulong)(n + 1) * 4, (ulong)_fogIndex * 4, 4));
            _fogIndex++;
        }
        if (!occlusion.IsEmpty && _occIndex < MaxFogViews)
        {
            var occRead = _occTotals[_scratchSlot] ??= _ctx.CreateReadback(MaxFogViews * 4, $"foliage occlusion cull totals {_scratchSlot}");
            OccludedIsFresh = _occIndex == 0;

            // the first view of the frame reads the slot's previous frame: the sum over its views (the later ones leave it)
            if (_occIndex == 0)
            {
                LateOccluded = 0;
                if (_occWritten[_scratchSlot] > 0)
                    foreach (uint v in MemoryMarshal.Cast<byte, uint>(occRead.Read(0, (ulong)_occWritten[_scratchSlot] * 4))) LateOccluded += (int)v;
            }
            cmd.CopyBuffer(offsetsBuffer, occRead.Handle, new GpuBufferCopy(offsetsOffset + (ulong)(n + 2) * 4, (ulong)_occIndex * 4, 4));
            _occIndex++;
        }
        var counter = _visible[_scratchSlot] ??= _ctx.CreateReadback(MaxViews * 4, $"foliage cull totals {_scratchSlot}");
        if (_viewIndex < MaxViews)
        {
            LateVisible = _viewIndex < _visibleWritten[_scratchSlot] ? (int)MemoryMarshal.Read<uint>(counter.Read((ulong)_viewIndex * 4, 4)) : 0;

            // the visible total (solid and fading)
            cmd.CopyBuffer(offsetsBuffer, counter.Handle, new GpuBufferCopy(offsetsOffset + (ulong)(2 * n + 4) * 4, (ulong)_viewIndex * 4, 4));
            _viewIndex++;
        }
    }

    #endregion

    #region Draw tally

    /// <summary>
    /// Tally each view's indirect arguments (triangles and instances of the meshes, the TERRAIN-mode rocks and the impostors) into
    /// <see cref="Tally"/>, read a frame ring later (set while the viewer's F11 statistics are shown and by <c>--bench-tris</c>); when set the
    /// dispatches copy their indirect arguments after the kernels, and nothing else changes.
    /// </summary>
    public static bool CountTriangles { get; set; }
    /// <summary>
    /// What the next <see cref="Dispatch(in FoliageCullWork, FoliageCullView, Vector2, in FoliageRockView, float)"/> is, for the tally: the view kind
    /// (0 colour, 1 shadow cascade, 2 reflection) and where the rock draws and the impostor draws begin among its draws.
    /// </summary>
    public (int Kind, int Rocks, int Impostors) TallyView;
    /// <summary>
    /// Per view kind (0 colour, 1 shadow, 2 reflection): mesh triangles, mesh instance-draws (one per part), rock triangles, rock instance-draws,
    /// impostor quads, views; summed since the start (or <see cref="ResetTally"/>).
    /// </summary>
    public readonly long[,] Tally = new long[3, 6];
    /// <summary>
    /// Per view kind: the indirect draws by instance count, 0 / 1-2 / 3-15 / 16+, summed since <see cref="ResetTally"/>.
    /// </summary>
    public readonly long[,] TallyHistogram = new long[3, 4];
    /// <summary>
    /// Frames tallied since <see cref="ResetTally"/>.
    /// </summary>
    public long TallyFrames { get; private set; }
    /// <summary>
    /// The tally read-back's size per frame slot, in bytes; views past it are not tallied.
    /// </summary>
    const ulong TallyBytes = 4ul << 20;
    /// <summary>
    /// The indirect arguments copied for the tally, per frame slot (made on first use).
    /// </summary>
    IGpuReadback?[]? _tallyBuffers;
    /// <summary>
    /// Per frame slot: each tallied view's kind, rock and impostor draw starts, draw count and offset in the read-back.
    /// </summary>
    List<(int Kind, int Rocks, int Impostors, int Count, ulong Offset)>[]? _tallyViews;
    /// <summary>
    /// The frame each slot's tally was copied in (-1: none).
    /// </summary>
    long[]? _tallyFrame;
    /// <summary>
    /// Bytes of each slot's tally read-back in use.
    /// </summary>
    ulong[]? _tallyUsed;

    /// <summary>
    /// Clears the tally and its frame count.
    /// </summary>
    public void ResetTally() { Array.Clear(Tally); Array.Clear(TallyHistogram); TallyFrames = 0; }

    /// <summary>
    /// Adds the slot's tally of a frame ring ago into <see cref="Tally"/> and <see cref="TallyHistogram"/> once that frame has completed, then starts
    /// the slot again.
    /// </summary>
    void CollectTally()
    {
        int slots = _ctx.FramesInFlight;
        _tallyBuffers ??= new IGpuReadback?[slots];
        _tallyViews ??= [.. Enumerable.Range(0, slots).Select(_ => new List<(int, int, int, int, ulong)>())];
        _tallyFrame ??= Enumerable.Repeat(-1L, slots).ToArray();
        _tallyUsed ??= new ulong[slots];
        int s = _scratchSlot;
        var views = _tallyViews[s];
        if (views.Count > 0 && _tallyBuffers[s] is { } rb && _ctx.FrameCompleted(_tallyFrame[s]))
        {
            foreach (var (kind, rocks, impostors, count, offset) in views)
            {
                var args = MemoryMarshal.Cast<byte, uint>(rb.Read(offset, (ulong)count * 20));
                for (int i = 0; i < count; i++)
                {
                    long indices = args[i * 5], instances = args[i * 5 + 1];
                    TallyHistogram[kind, instances == 0 ? 0 : instances <= 2 ? 1 : instances <= 15 ? 2 : 3]++;
                    if (i < rocks)
                    {
                        Tally[kind, 0] += indices / 3 * instances;
                        Tally[kind, 1] += instances;
                    }
                    else if (i < impostors)
                    {
                        Tally[kind, 2] += indices / 3 * instances;
                        Tally[kind, 3] += instances;
                    }
                    else Tally[kind, 4] += instances;
                }
                Tally[kind, 5]++;
            }
            TallyFrames++;
        }
        views.Clear();
        _tallyUsed[s] = 0;
        _tallyFrame[s] = _ctx.Frame.Number;
    }

    /// <summary>
    /// Copies a view's indirect arguments into the slot's tally read-back (while it has room).
    /// </summary>
    void CopyTally(IGpuCommandList cmd, GpuBuffer args, ulong argsOffset, int count)
    {
        if (_tallyViews is null || count == 0) return;
        int s = _scratchSlot;
        ulong bytes = (ulong)count * 20;
        if (_tallyUsed![s] + bytes > TallyBytes) return;
        var rb = _tallyBuffers![s] ??= _ctx.CreateReadback(TallyBytes, $"foliage draw tally {s}");
        cmd.CopyBuffer(args, rb.Handle, new GpuBufferCopy(argsOffset, _tallyUsed[s], bytes));
        _tallyViews[s].Add((TallyView.Kind, TallyView.Rocks, TallyView.Impostors, count, _tallyUsed[s]));
        _tallyUsed[s] += (bytes + 15) / 16 * 16;
    }

    #endregion

    #region GPU timing

    /// <summary>
    /// GPU time of the dispatches (begin and end timestamps per view), read a frame ring later.
    /// </summary>
    readonly List<(QuerySlot Begin, QuerySlot End, int Class)> _pendingTimes = [];
    /// <summary>
    /// The dispatches' GPU time summed, in microseconds.
    /// </summary>
    public double GpuMicroseconds { get; private set; }
    /// <summary>
    /// The views whose GPU time was read.
    /// </summary>
    public long GpuTimedViews { get; private set; }
    /// <summary>
    /// What the next dispatch is for the timing below: 0 any, 1 a main colour view without the occlusion cull, 2 with it.
    /// </summary>
    public int TimingClass;
    /// <summary>
    /// GPU microseconds and views of the main colour views, without (0) and with (1) the occlusion cull.
    /// </summary>
    public readonly double[] MainMicroseconds = new double[2];
    /// <summary>
    /// The main colour views timed, without (0) and with (1) the occlusion cull.
    /// </summary>
    public readonly long[] MainViews = new long[2];
    /// <summary>
    /// Views dispatched so far.
    /// </summary>
    public long Dispatched { get; private set; }

    /// <summary>
    /// Reads the timestamps of earlier dispatches that are ready into the sums (and drops the ones too old to be read).
    /// </summary>
    void CollectTimes()
    {
        var arenaQ = _ctx.Frame.Timestamps;
        for (int i = 0; i < _pendingTimes.Count; i++)
        {
            var (b, e, cls) = _pendingTimes[i];
            // never collected
            if (b.Frame + _ctx.FramesInFlight + 2 < _ctx.Frame.Number)
            {
                _pendingTimes.RemoveAt(i--);
                continue;
            }
            if (!arenaQ.TryRead(b, out ulong tb) || !arenaQ.TryRead(e, out ulong te)) continue;
            GpuMicroseconds += (te - tb) / 1000.0;
            GpuTimedViews++;
            if (cls > 0)
            {
                MainMicroseconds[cls - 1] += (te - tb) / 1000.0;
                MainViews[cls - 1]++;
            }
            _pendingTimes.RemoveAt(i--);
        }
    }

    #endregion
}