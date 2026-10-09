using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Meitou.Rendering;

/// <summary>A run of at most <see cref="FoliageShaders.CullChunk"/> instances of one group (std430, 32 bytes): where they are in the
/// instance arena, and the group's range for the view as the CPU computed it (<see cref="FoliageGroupRange"/>). <see cref="Flags"/> 0 for
/// the foliage meshes; <see cref="Rock"/> for TERRAIN-mode rocks (their records' <c>Ground.W</c> from <see cref="FoliageCull.RockBits"/>),
/// with <see cref="Mirrored"/> for the run of the group's mirroring placements.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FoliageCullChunk
{
    public uint First, Count;
    public float Range, RangeSquared, InverseBand;
    public uint Flags;
    /// <summary>With <see cref="ImpostorMesh"/> or <see cref="Impostor"/>: the group's transition and the crossfade band's reciprocal (<see cref="FoliageGroupRange"/>).</summary>
    public float Transition, InverseTransitionBand;
    /// <summary>With <see cref="Lod"/>: the deviation, in radii of the mesh, of the level this chunk holds and of the next coarser one (infinite for the last).</summary>
    public float LodError, LodNextError;

    public const int Size = 40;
    /// <summary><see cref="ImpostorMesh"/>: the meshes of a group with an impostor (before its transition); <see cref="Impostor"/>: its impostors.</summary>
    public const uint Rock = 1, Mirrored = 2, ImpostorMesh = 4, Impostor = 8;
    /// <summary>A generated mesh level's chunk: its instances are those the view picks this level for (<see cref="FoliageLodView"/>).</summary>
    public const uint Lod = 16;
}

/// <summary>What a view's rock chunks write into row 0 w (<see cref="FoliageShaders.CompactCompute"/>): with <see cref="BiomeRows"/> (the
/// colour views) the biome row when <see cref="Resident"/> has its bit (else -1), without it 0; as <see cref="TerrainRenderer.DrawMeshes"/>
/// writes the placements.</summary>
public struct FoliageRockView
{
    public bool BiomeRows;
    /// <summary>A bit per biome row (256), row r at word r / 32, bit r % 32.</summary>
    public ResidentBits Resident;

    [System.Runtime.CompilerServices.InlineArray(8)]
    public struct ResidentBits { uint first; }
}

/// <summary>What a view tells the cull about generated mesh levels (<see cref="FoliageCullChunk.Lod"/>): a level is used while its deviation shows less than
/// <see cref="Tolerance"/>. A perspective view measures it in pixels, <see cref="Scale"/> the pixels per radian of the render and <see cref="EyeY"/> the eye's height
/// (the cull has only its ground position); an orthographic one (a shadow cascade) in texels, <see cref="Scale"/> being 1 / the texel. Tolerance 0 uses the original everywhere.</summary>
public readonly record struct FoliageLodView(float EyeY, float Scale, float Tolerance, bool Ortho, float FarDistance = 0);

/// <summary>An indirect draw the scan fills (std430, 16 bytes): the part's index count (from <see cref="FirstIndex"/>, a generated level's place in the index buffer) and the chunks [ChunkStart, ChunkEnd) of its batch.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FoliageCullDraw
{
    public uint IndexCount, ChunkStart, ChunkEnd, FirstIndex;

    public const int Size = 16;
}

/// <summary>The kernels' push constants (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
struct FoliageCullPush
{
    public Vector2 Eye;
    public uint PlaneCount, ChunkCount, DrawCount;
    public float FullThreshold;
}

/// <summary>A view's work list in the frame's constants: the chunks in draw order and the draws, reused by the views that share it (the
/// shadow cascades of a frame).</summary>
public readonly record struct FoliageCullWork(Transient Chunks, int ChunkCount, Transient Draws, int DrawCount)
{
    public int Candidates => ChunkCount * FoliageShaders.CullChunk;
    /// <summary>The instances the chunks hold (their counts summed; -1: not given, the chunk slots are used): what the rows can need at most.</summary>
    public int Instances { get; init; } = -1;
}

/// <summary>What one view's cull wrote: the compacted matrices (bind at locations 7 to 10), the indirect arguments (one per draw, 20 bytes
/// each), and the chunks' output offsets (ChunkCount + 1, the last one the total).</summary>
public readonly record struct FoliageCullResult(Buffer Rows, ulong RowsOffset, ulong RowsBytes, Buffer Args, ulong ArgsOffset, Buffer Offsets, ulong OffsetsOffset, int ChunkCount, int DrawCount)
{
    public bool IsEmpty => DrawCount == 0;
}

/// <summary>
/// The GPU foliage cull (docs/renderer-native.md 5.3, step A2): the instance arena (5.2: every group's <see cref="FoliageInstanceRecord"/>s,
/// uploaded at their first cull), and per view the three kernels of <see cref="FoliageShaders"/> recorded into
/// <see cref="GpuFrame.PreFrame"/>, so they run before every pass of the frame while the draws that read their output are recorded where the
/// CPU path drew (a shadow cascade, a depth slice, the reflection). Per-view outputs live in device memory of the frame's slot.
/// Render thread only.
/// </summary>
public sealed unsafe class FoliageGpuCull : IDisposable
{
    const ulong Align = 256;
    /// <summary>The kernels' View buffer (<c>ViewData</c>): 8 planes, their normals' lengths, the resident biome bits, the mode, the fog cull (<see cref="FogVolumes.CullVectors"/> vec4s), the occlusion cull (<see cref="HizPyramid.ViewVectors"/> vec4s).</summary>
    const ulong ViewBytes = 208 + FogVolumes.CullVectors * 16 + HizPyramid.ViewVectors * 16 + 16;
    readonly GpuContext ctx;
    readonly ShaderProgram cull, scan, compact;
    readonly ComputePipeline cullPipe, scanPipe, compactPipe;
    readonly uint[] cullBindings, scanBindings, compactBindings;
    BufferArena arena;
    /// <summary>Bumped whenever the arena is replaced by a larger one: a group placed in an older generation is uploaded again.</summary>
    public int Generation { get; private set; } = 1;

    // Device memory per frame slot for the per-view outputs (fades, counts, offsets, arguments, rows): bump allocated, reset per frame,
    // sized by what the frames need (FrameScratch), capped.
    readonly FrameScratch scratch;
    long scratchFrame = -1;
    int scratchSlot = -1;

    public FoliageGpuCull(GpuContext ctx, ulong arenaBytes = 16ul << 20, ulong scratchCap = 512ul << 20)
    {
        this.ctx = ctx;
        cull = ctx.Shaders.Compute(FoliageShaders.CullCompute, "foliage cull");
        scan = ctx.Shaders.Compute(FoliageShaders.ScanCompute, "foliage cull scan");
        compact = ctx.Shaders.Compute(FoliageShaders.CompactCompute, "foliage cull compact");
        cullPipe = ctx.Pipelines.Get(new ComputePipelineDesc(cull, "foliage cull"));
        scanPipe = ctx.Pipelines.Get(new ComputePipelineDesc(scan, "foliage cull scan"));
        compactPipe = ctx.Pipelines.Get(new ComputePipelineDesc(compact, "foliage cull compact"));
        cullBindings = Bindings(cull);
        scanBindings = Bindings(scan);
        compactBindings = Bindings(compact);
        arena = new BufferArena(ctx, arenaBytes, BufferUse.Storage | BufferUse.TransferDst, "foliage instances");
        scratch = new FrameScratch(ctx, "foliage cull scratch", BufferUse.Storage | BufferUse.Vertex | BufferUse.Indirect | BufferUse.TransferSrc, scratchCap, minimum: 4ul << 20);
        visible =new ReadbackBuffer?[ctx.Device.Frames.Count];
        visibleWritten = new int[ctx.Device.Frames.Count];
        fogTotals = new ReadbackBuffer?[ctx.Device.Frames.Count];
        fogWritten = new int[ctx.Device.Frames.Count];
        occTotals = new ReadbackBuffer?[ctx.Device.Frames.Count];
        occWritten = new int[ctx.Device.Frames.Count];
    }

    static uint[] Bindings(ShaderProgram p)
    {
        if (!p.PushDescriptors) throw new InvalidOperationException($"{p.Name}: the foliage cull pushes its descriptors, the device cannot");
        return [.. p.ComputeReflection!.Blocks.Where(b => b.Kind == Gpu.Shaders.BlockKind.StorageBuffer && b.Set == 0).Select(b => (uint)b.Binding).Order()];
    }

    /// <summary>Bytes of the instance arena, and bytes in use.</summary>
    public ulong ArenaBytes => arena.Buffer.Size;
    public ulong ArenaUsed => arena.Buffer.Size - arena.FreeBytes;
    public int Grows { get; private set; }
    /// <summary>The per-view outputs' memory: its size, the need of the frames, and what it refused (<see cref="FrameScratch"/>).</summary>
    public FrameScratch Scratch => scratch;
    public long UploadedInstances { get; private set; }

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
        var r = arena.Allocate(bytes, FoliageInstanceRecord.GpuSize);
        if (r.IsEmpty && bytes > 0)
        {
            // A refused growth (the video memory is nearly used up, VramGuard) leaves the range empty: the caller leaves the group out of the frame.
            if (MayGrow is { } may && !may(GrowSize(bytes))) { GrowRefusals++; (range, generation) = (default, 0); return true; }
            Grow(bytes);
            return false;
        }
        // Packed in slices (17 floats a record, FoliageInstanceRecord.Pack) into a reused array, then into the frame's upload buffer.
        const int Slice = 4096;
        const int Floats = FoliageInstanceRecord.GpuSize / sizeof(float);
        packed ??= new float[Slice * Floats];
        for (int at = 0; at < records.Length; at += Slice)
        {
            int n = Math.Min(Slice, records.Length - at);
            for (int i = 0; i < n; i++)
                if (!FoliageInstanceRecord.Pack(in records[at + i], packed.AsSpan(i * Floats, Floats))) PackMismatches++;
            ctx.Uploads.Write(arena.Buffer, r.Offset + (ulong)at * FoliageInstanceRecord.GpuSize, MemoryMarshal.AsBytes(packed.AsSpan(0, n * Floats)));
        }
        (range, generation) = (r, Generation);
        UploadedInstances += records.Length;
        return true;
    }

    /// <summary>The first record of a placed range.</summary>
    public static uint FirstOf(ArenaRange range) => (uint)(range.Offset / FoliageInstanceRecord.GpuSize);

    float[]? packed;
    /// <summary>Records whose packing was not lossless (<see cref="FoliageInstanceRecord.Pack"/>); 0 for every placement the game makes.</summary>
    public long PackMismatches { get; private set; }

    /// <summary>Gives a group's range back (after the frames in flight), when it is from the current arena.</summary>
    public void Free(ArenaRange range, int generation)
    {
        if (generation == Generation && !range.IsEmpty) arena.Free(range);
    }

    /// <summary>Asked before the arena or the scratch memory grows: false refuses (<c>VramGuard.Allows</c>). Also given to <see cref="Scratch"/>.</summary>
    public Func<ulong, bool>? MayGrow
    {
        get => mayGrow;
        set { mayGrow = value; scratch.MayGrow = value; }
    }
    Func<ulong, bool>? mayGrow;
    /// <summary>Times the arena could not grow for the memory (the group was left out of the frame).</summary>
    public int GrowRefusals { get; private set; }

    ulong GrowSize(ulong atLeast)
    {
        ulong size = arena.Buffer.Size * 2;
        while (size < atLeast * 2) size *= 2;
        return size;
    }

    void Grow(ulong atLeast)
    {
        ulong size = GrowSize(atLeast);
        arena.Dispose();   // deferred: this frame's earlier dispatches may read it
        arena = new BufferArena(ctx, size, BufferUse.Storage | BufferUse.TransferDst, "foliage instances");
        Generation++;
        Grows++;
    }

    /// <summary>Copies a view's chunks and draws into the frame's constants (valid for the frame).</summary>
    public FoliageCullWork Prepare(ReadOnlySpan<FoliageCullChunk> chunks, ReadOnlySpan<FoliageCullDraw> draws)
    {
        var c = ctx.Frame.Constants.Write(chunks, Align);
        var d = ctx.Frame.Constants.Write(draws, Align);
        return new FoliageCullWork(c, chunks.Length, d, draws.Length);
    }

    // The visible total of each view, copied per frame slot and call number, read when the slot comes round (a frame ring later).
    const int MaxViews = 256;
    readonly ReadbackBuffer?[] visible;
    readonly int[] visibleWritten;
    int viewIndex;

    /// <summary>The visible instances the view with this call number drew a frame ring ago (0 before): a statistic for the CPU, which no
    /// longer knows the count when it draws (<see cref="FoliageRenderer.DrawnInstances"/>).</summary>
    public int LateVisible { get; private set; }
    /// <summary>The instances the fog cull left out of all the views of the frame this slot drew a frame ring ago, given at the first fog view of
    /// a frame (0 for the later ones and when it was off). The views' order varies from frame to frame (the shadow cascades), so they are summed, not matched by number.</summary>
    public int LateFogCulled { get; private set; }
    const int MaxFogViews = 16;
    readonly ReadbackBuffer?[] fogTotals;
    readonly int[] fogWritten;
    int fogIndex;
    /// <summary>The instances the occlusion cull left out of the frame's main colour views (the slot's previous frame, as <see cref="LateFogCulled"/>).</summary>
    public int LateOccluded { get; private set; }
    /// <summary>The last dispatch with the occlusion cull was the first of its frame (so <see cref="LateOccluded"/> is new).</summary>
    public bool OccludedIsFresh { get; private set; }
    readonly ReadbackBuffer?[] occTotals;
    readonly int[] occWritten;
    int occIndex;

    void NewFrame()
    {
        var frame = ctx.Frame;
        if (scratchFrame == frame.Number) return;
        if (scratchSlot >= 0) (visibleWritten[scratchSlot], fogWritten[scratchSlot], occWritten[scratchSlot]) = (viewIndex, fogIndex, occIndex);   // what the slot's buffer holds when it comes round
        (scratchFrame, scratchSlot, viewIndex, fogIndex, occIndex) = (frame.Number, frame.Slot, 0, 0, 0);
        if (DrawTally) CollectTally();
    }

    // ---- MEITOU_FOLIAGE_TRIS=1: what the indirect draws drew, by view kind (benchmark statistics; the arguments are copied after the
    // kernels, read a frame ring later; nothing else changes) ----

    /// <summary><c>MEITOU_FOLIAGE_TRIS=1</c>: tally each view's indirect arguments (triangles and instances of the meshes, the TERRAIN-mode rocks
    /// and the impostors) into <see cref="Tally"/>, read a frame ring later (docs/render-distance-benchmark.md).</summary>
    public static readonly bool NamedTally = Environment.GetEnvironmentVariable("MEITOU_FOLIAGE_TRIS") == "1";
    /// <summary>The viewer's F11 statistics: the same tally without the per-mesh names, for the total triangles (set while they are shown).</summary>
    public static bool CountTriangles { get; set; }
    /// <summary>Whether the dispatches copy their indirect arguments for <see cref="Tally"/>: <see cref="NamedTally"/> or <see cref="CountTriangles"/>.</summary>
    public static bool DrawTally => NamedTally || CountTriangles;

    /// <summary>What the next <see cref="Dispatch(in FoliageCullWork, FoliageCullView, Vector2, in FoliageRockView, float)"/> is, for the tally:
    /// the view kind (0 colour, 1 shadow cascade, 2 reflection) and where the rock draws and the impostor draws begin among its draws.</summary>
    public (int Kind, int Rocks, int Impostors) TallyView;
    /// <summary>The next dispatch's draws by name (one per draw), for <see cref="TallyByName"/>; null: not named.</summary>
    public string[]? TallyNames;
    /// <summary>The colour views' triangles and instance-draws (impostors: quads) per drawn mesh name, summed since <see cref="ResetTally"/>.</summary>
    public readonly Dictionary<string, (long Triangles, long Instances)> TallyByName = [];
    /// <summary>The same for the shadow cascades (all cascades summed).</summary>
    public readonly Dictionary<string, (long Triangles, long Instances)> TallyShadowByName = [];

    /// <summary>Per view kind (0 colour, 1 shadow, 2 reflection): mesh triangles, mesh instance-draws (one per part), rock triangles, rock
    /// instance-draws, impostor quads, views; summed since the start (or <see cref="ResetTally"/>).</summary>
    public readonly long[,] Tally = new long[3, 6];
    /// <summary>Per view kind: the indirect draws by instance count, 0 / 1-2 / 3-15 / 16+, summed since <see cref="ResetTally"/>.</summary>
    public readonly long[,] TallyHistogram = new long[3, 4];
    public long TallyFrames { get; private set; }

    public void ResetTally() { Array.Clear(Tally); Array.Clear(TallyHistogram); TallyByName.Clear(); TallyShadowByName.Clear(); TallyFrames = 0; }

    const ulong TallyBytes = 4ul << 20;
    ReadbackBuffer?[]? tallyBuffers;
    List<(int Kind, int Rocks, int Impostors, int Count, ulong Offset, string[]? Names)>[]? tallyViews;
    long[]? tallyFrame;
    ulong[]? tallyUsed;

    void CollectTally()
    {
        int slots = ctx.Device.Frames.Count;
        tallyBuffers ??= new ReadbackBuffer?[slots];
        tallyViews ??= [.. Enumerable.Range(0, slots).Select(_ => new List<(int, int, int, int, ulong, string[]?)>())];
        tallyFrame ??= Enumerable.Repeat(-1L, slots).ToArray();
        tallyUsed ??= new ulong[slots];
        int s = scratchSlot;
        var views = tallyViews[s];
        if (views.Count > 0 && tallyBuffers[s] is { } rb && ReadbackBuffer.Completed(ctx, tallyFrame[s]))
        {
            foreach (var (kind, rocks, impostors, count, offset, names) in views)
            {
                var args = MemoryMarshal.Cast<byte, uint>(rb.Read(offset, (ulong)count * 20));
                for (int i = 0; i < count; i++)
                {
                    long indices = args[i * 5], instances = args[i * 5 + 1];
                    TallyHistogram[kind, instances == 0 ? 0 : instances <= 2 ? 1 : instances <= 15 ? 2 : 3]++;
                    if (kind <= 1 && names is not null && i < names.Length && instances > 0)
                    {
                        var byName = kind == 0 ? TallyByName : TallyShadowByName;
                        byName.TryGetValue(names[i], out var t);
                        byName[names[i]] = (t.Triangles + indices / 3 * instances, t.Instances + instances);
                    }
                    if (i < rocks) { Tally[kind, 0] += indices / 3 * instances; Tally[kind, 1] += instances; }
                    else if (i < impostors) { Tally[kind, 2] += indices / 3 * instances; Tally[kind, 3] += instances; }
                    else Tally[kind, 4] += instances;
                }
                Tally[kind, 5]++;
            }
            TallyFrames++;
        }
        views.Clear();
        tallyUsed[s] = 0;
        tallyFrame[s] = ctx.Frame.Number;
    }

    void CopyTally(CommandList cmd, Buffer args, ulong argsOffset, int count)
    {
        if (tallyViews is null || count == 0) return;
        int s = scratchSlot;
        ulong bytes = (ulong)count * 20;
        if (tallyUsed![s] + bytes > TallyBytes) return;
        var rb = tallyBuffers![s] ??= ReadbackBuffer.Create(ctx, TallyBytes, $"foliage draw tally {s}");
        cmd.CopyBuffer(args, rb.Handle, new BufferCopy(argsOffset, tallyUsed[s], bytes));
        tallyViews[s].Add((TallyView.Kind, TallyView.Rocks, TallyView.Impostors, count, tallyUsed[s], TallyNames));
        TallyNames = null;
        tallyUsed[s] += (bytes + 15) / 16 * 16;
    }

    /// <summary>GPU time of the dispatches (begin and end timestamps per view), read a frame ring later.</summary>
    readonly List<(QuerySlot Begin, QuerySlot End, int Class)> pendingTimes = [];
    public double GpuMicroseconds { get; private set; }
    public long GpuTimedViews { get; private set; }
    /// <summary>What the next dispatch is for the timing below: 0 any, 1 a main colour view without the occlusion cull, 2 with it.</summary>
    public int TimingClass;
    /// <summary>GPU microseconds and views of the main colour views, without (0) and with (1) the occlusion cull.</summary>
    public readonly double[] MainMicroseconds = new double[2];
    public readonly long[] MainViews = new long[2];
    public long Dispatched { get; private set; }

    void CollectTimes()
    {
        var arenaQ = ctx.Frame.Timestamps;
        for (int i = 0; i < pendingTimes.Count; i++)
        {
            var (b, e, cls) = pendingTimes[i];
            if (b.Frame + ctx.Device.Frames.Count + 2 < ctx.Frame.Number) { pendingTimes.RemoveAt(i--); continue; }   // never collected
            if (!arenaQ.TryRead(b, out ulong tb) || !arenaQ.TryRead(e, out ulong te)) continue;
            GpuMicroseconds += (te - tb) / 1000.0;
            GpuTimedViews++;
            if (cls > 0) { MainMicroseconds[cls - 1] += (te - tb) / 1000.0; MainViews[cls - 1]++; }
            pendingTimes.RemoveAt(i--);
        }
    }

    /// <summary>
    /// Records the cull of one view into <see cref="GpuFrame.PreFrame"/>: <paramref name="planes"/> (at most 8) with their normals' lengths,
    /// the eye along the ground, the work list; <paramref name="fullThreshold"/> the fade at and above which an instance is drawn whole (the
    /// mesh shader's 2). The result's buffers are read by this frame's draws.
    /// </summary>
    public FoliageCullResult Dispatch(in FoliageCullWork work, FoliageCullView view, Vector2 eye, float fullThreshold = 0.999f) =>
        Dispatch(work, view, eye, default, fullThreshold);

    /// <summary><see cref="Dispatch(in FoliageCullWork, FoliageCullView, Vector2, float)"/> with what the view's rock chunks write (<see cref="FoliageRockView"/>).</summary>
    public FoliageCullResult Dispatch(in FoliageCullWork work, FoliageCullView view, Vector2 eye, in FoliageRockView rock, float fullThreshold = 0.999f, ReadOnlySpan<Vector4> fogCull = default, in OcclusionView occlusion = default, FoliageLodView lod = default)
    {
        if (work.ChunkCount == 0) return default;
        if (view.Planes.Length > 8) throw new ArgumentException("at most 8 planes", nameof(view));
        CollectTimes();
        int n = work.ChunkCount;
        NewFrame();
        // The rows need a slot per instance that can be visible (the chunks' counts summed), not per chunk slot (a chunk of a small group is mostly empty).
        ulong rowsBytes = (ulong)(work.Instances >= 0 ? work.Instances : work.Candidates) * 64;
        if (!(scratch.TryAllocate((ulong)work.Candidates * 4, out var fadesBuffer, out var fadesOffset) &&
              scratch.TryAllocate((ulong)n * 12, out var countsBuffer, out var countsOffset) &&
              scratch.TryAllocate((ulong)(n + 3) * 4, out var offsetsBuffer, out var offsetsOffset) &&
              scratch.TryAllocate((ulong)Math.Max(work.DrawCount, 1) * 20, out var argsBuffer, out var argsOffset) &&
              scratch.TryAllocate(rowsBytes, out var rowsBuffer, out var rowsOffset)))
            return default;   // over the cap: this view is left out of the frame (FrameScratch); the guard shortens the ranges
        var fades = (Buffer: fadesBuffer, Offset: fadesOffset);
        var counts = (Buffer: countsBuffer, Offset: countsOffset);
        var offsets = (Buffer: offsetsBuffer, Offset: offsetsOffset);
        var args = (Buffer: argsBuffer, Offset: argsOffset);
        var rows = (Buffer: rowsBuffer, Offset: rowsOffset);
        var viewData = ctx.Frame.Constants.Allocate(ViewBytes, Align);
        var planes = (Vector4*)viewData.Pointer;
        var lengths = (float*)(viewData.Pointer + 128);
        for (int i = 0; i < 8; i++) planes[i] = i < view.Planes.Length ? view.Planes[i] : default;
        for (int i = 0; i < 8; i++) lengths[i] = i < view.Planes.Length ? view.NormalLengths[i] : 0;
        var resident = (uint*)(viewData.Pointer + 160);
        for (int i = 0; i < 8; i++) resident[i] = rock.Resident[i];
        var mode = (uint*)(viewData.Pointer + 192);
        (mode[0], mode[1], mode[2], mode[3]) = (rock.BiomeRows ? 1u : 0u, fogCull.Length == FogVolumes.CullVectors ? 1u : 0u, occlusion.IsEmpty ? 0u : 1u, 0u);
        var fogData = (Vector4*)(viewData.Pointer + 208);
        for (int i = 0; i < FogVolumes.CullVectors; i++) fogData[i] = fogCull.Length == FogVolumes.CullVectors ? fogCull[i] : default;
        var hzData = fogData + FogVolumes.CullVectors;
        for (int i = 0; i < HizPyramid.ViewVectors; i++) hzData[i] = occlusion.IsEmpty ? default : occlusion.Vectors[i];
        hzData[HizPyramid.ViewVectors] = new Vector4(lod.EyeY, lod.Scale, lod.Tolerance, lod.Ortho ? 1 : -lod.FarDistance);

        Span<BufferBinding> b = stackalloc BufferBinding[10];
        b[0] = new BufferBinding(viewData.Handle, viewData.Offset, ViewBytes);
        b[1] = new BufferBinding(arena.Buffer.Handle, 0, arena.Buffer.Size);
        b[2] = new BufferBinding(work.Chunks.Handle, work.Chunks.Offset, (ulong)n * FoliageCullChunk.Size);
        b[3] = new BufferBinding(fades.Buffer, fades.Offset, (ulong)work.Candidates * 4);
        b[4] = new BufferBinding(counts.Buffer, counts.Offset, (ulong)n * 12);
        b[5] = new BufferBinding(offsets.Buffer, offsets.Offset, (ulong)(n + 3) * 4);
        b[6] = new BufferBinding(work.Draws.Handle, work.Draws.Offset, (ulong)Math.Max(work.DrawCount, 1) * FoliageCullDraw.Size);
        b[7] = new BufferBinding(args.Buffer, args.Offset, (ulong)Math.Max(work.DrawCount, 1) * 20);
        b[8] = new BufferBinding(rows.Buffer, rows.Offset, rowsBytes);
        b[9] = occlusion.IsEmpty ? new BufferBinding(arena.Buffer.Handle, 0, arena.Buffer.Size) : occlusion.Buffer;   // the pyramid (a stand-in when the occlusion cull is off)
        var push = new FoliageCullPush { Eye = eye, PlaneCount = (uint)view.Planes.Length, ChunkCount = (uint)n, DrawCount = (uint)work.DrawCount, FullThreshold = fullThreshold };

        var cmd = ctx.Frame.PreFrame;
        var stamps = (ctx.Frame.Timestamps.Allocate(), ctx.Frame.Timestamps.Allocate());
        cmd.BeginLabel("foliage cull");
        // The arena's uploads (and anything else uploaded so far) before the reads.
        Barrier(cmd, PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit);
        cmd.Timestamp(ctx.Frame.Timestamps, stamps.Item1, PipelineStageFlags2.ComputeShaderBit);
        uint x = (uint)Math.Min(n, 65535), y = (uint)((n + 65534) / 65535);
        Run(cmd, cull, cullPipe, cullBindings, b, push, x, y);
        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit);
        Run(cmd, scan, scanPipe, scanBindings, b, push, 1, 1);
        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit);
        Run(cmd, compact, compactPipe, compactBindings, b, push, x, y);
        // The draws read the arguments and the rows (the upload buffer's closing full barrier orders them too; this one says what for).
        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.DrawIndirectBit | PipelineStageFlags2.VertexAttributeInputBit | PipelineStageFlags2.AllTransferBit,
            AccessFlags2.IndirectCommandReadBit | AccessFlags2.VertexAttributeReadBit | AccessFlags2.TransferReadBit);
        cmd.Timestamp(ctx.Frame.Timestamps, stamps.Item2, PipelineStageFlags2.ComputeShaderBit);
        // The total for the CPU's statistics, a frame ring later (the slot's previous frame has completed: its fence was waited for).
        LateFogCulled = 0;
        if (fogCull.Length == FogVolumes.CullVectors && fogIndex < MaxFogViews)
        {
            var fogRead = fogTotals[scratchSlot] ??= ReadbackBuffer.Create(ctx, MaxFogViews * 4, $"foliage fog cull totals {scratchSlot}");
            if (fogIndex == 0 && fogWritten[scratchSlot] > 0)
                foreach (uint v in MemoryMarshal.Cast<byte, uint>(fogRead.Read(0, (ulong)fogWritten[scratchSlot] * 4))) LateFogCulled += (int)v;
            cmd.CopyBuffer(offsets.Buffer, fogRead.Handle, new BufferCopy(offsets.Offset + (ulong)(n + 1) * 4, (ulong)fogIndex * 4, 4));
            fogIndex++;
        }
        if (!occlusion.IsEmpty && occIndex < MaxFogViews)
        {
            var occRead = occTotals[scratchSlot] ??= ReadbackBuffer.Create(ctx, MaxFogViews * 4, $"foliage occlusion cull totals {scratchSlot}");
            OccludedIsFresh = occIndex == 0;
            if (occIndex == 0)   // the first view of the frame reads the slot's previous frame: the sum over its views (the later ones leave it)
            {
                LateOccluded = 0;
                if (occWritten[scratchSlot] > 0)
                    foreach (uint v in MemoryMarshal.Cast<byte, uint>(occRead.Read(0, (ulong)occWritten[scratchSlot] * 4))) LateOccluded += (int)v;
            }
            cmd.CopyBuffer(offsets.Buffer, occRead.Handle, new BufferCopy(offsets.Offset + (ulong)(n + 2) * 4, (ulong)occIndex * 4, 4));
            occIndex++;
        }
        var counter = visible[scratchSlot] ??= ReadbackBuffer.Create(ctx, MaxViews * 4, $"foliage cull totals {scratchSlot}");
        if (viewIndex < MaxViews)
        {
            LateVisible = viewIndex < visibleWritten[scratchSlot] ? (int)MemoryMarshal.Read<uint>(counter.Read((ulong)viewIndex * 4, 4)) : 0;
            cmd.CopyBuffer(offsets.Buffer, counter.Handle, new BufferCopy(offsets.Offset + (ulong)n * 4, (ulong)viewIndex * 4, 4));
            viewIndex++;
        }
        if (DrawTally) CopyTally(cmd, args.Buffer, args.Offset, work.DrawCount);
        cmd.EndLabel();
        if (stamps.Item1.IsValid && stamps.Item2.IsValid) pendingTimes.Add((stamps.Item1, stamps.Item2, TimingClass));
        Dispatched++;
        return new FoliageCullResult(rows.Buffer, rows.Offset, rowsBytes, args.Buffer, args.Offset, offsets.Buffer, offsets.Offset, n, work.DrawCount);
    }

    void Run(CommandList cmd, ShaderProgram p, ComputePipeline pipe, uint[] bindings, ReadOnlySpan<BufferBinding> b, in FoliageCullPush push, uint x, uint y)
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
        cmd.PushDescriptors(p.Layout, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, count), PipelineBindPoint.Compute);
        cmd.PushConstants(p.Layout, ShaderStageFlags.ComputeBit, in push);
        cmd.Dispatch(x, y);
    }

    static void Barrier(CommandList cmd, PipelineStageFlags2 srcStages, AccessFlags2 srcAccess, PipelineStageFlags2 dstStages, AccessFlags2 dstAccess)
    {
        var batch = new BarrierBatch();
        batch.Add(srcStages, srcAccess, dstStages, dstAccess);
        cmd.Barrier(in batch);
    }

    /// <summary>Copies a result (offsets, arguments, rows) into <paramref name="target"/> at 0, (ChunkCount + 1) × 4 and then the arguments' end
    /// (16-aligned), after the dispatches (the verify mode). Returns the bytes needed when <paramref name="target"/> is null.</summary>
    public static ulong ReadbackBytes(in FoliageCullResult r) => Align16((ulong)(r.ChunkCount + 1) * 4) + Align16((ulong)r.DrawCount * 20) + r.RowsBytes;

    public static ulong Align16(ulong v) => (v + 15) / 16 * 16;

    public void CopyForReadback(in FoliageCullResult r, ReadbackBuffer target)
    {
        var cmd = ctx.Frame.PreFrame;
        ulong o = 0;
        ulong offsetsBytes = (ulong)(r.ChunkCount + 1) * 4, argsBytes = (ulong)r.DrawCount * 20;
        cmd.CopyBuffer(r.Offsets, target.Handle, new BufferCopy(r.OffsetsOffset, o, offsetsBytes));
        o += Align16(offsetsBytes);
        if (argsBytes > 0) cmd.CopyBuffer(r.Args, target.Handle, new BufferCopy(r.ArgsOffset, o, argsBytes));
        o += Align16(argsBytes);
        if (r.RowsBytes > 0) cmd.CopyBuffer(r.Rows, target.Handle, new BufferCopy(r.RowsOffset, o, r.RowsBytes));
    }

    public void Dispose()
    {
        foreach (var p in new[] { cull, scan, compact })
        {
            ctx.Pipelines.Forget(p);
            p.Dispose();
        }
        arena.Dispose();
        scratch.Dispose();
        foreach (var v in visible) v?.Dispose();
        foreach (var v in fogTotals) v?.Dispose();
        foreach (var v in occTotals) v?.Dispose();
    }
}
