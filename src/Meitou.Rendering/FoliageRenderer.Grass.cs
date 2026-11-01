using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The grass pages' storage and the GPU grass path (docs/renderer-native.md 5.4 and 5.6.2). Every page's blades sit in one arena
/// (<see cref="FoliageGrassGpu"/>), so a blade draw is <c>Draw(vertices, blades, 0, firstBlade)</c> from one vertex binding. The CPU path
/// (<see cref="GpuGrass"/> off) walks the pages and records those draws; the GPU path writes one patch table per view and lets the cull kernels
/// pick the pages, the blades and the order, then records one indirect draw.
/// </summary>
public sealed unsafe partial class FoliageRenderer
{
    FoliageGrassGpu grassStore = null!;
    /// <summary>The native frame of the GPU blade programs (set 0 with the patch table as an extra storage binding).</summary>
    NativeFrame? grassFrame;
    NativeProg? grassGpuProgram, grassMotionGpuProgram;
    /// <summary>The blade programs' vertex layout over the arena (instance rate: vec4 at 0, float at 16, stride 20), and their pipelines per segment.</summary>
    NativeMesh grassMeshCpu, grassMeshMotionCpu, grassMeshGpu, grassMeshMotionGpu;

    /// <summary>
    /// The grass drawn by the GPU (docs/renderer-native.md 5.6.2; where the device allows it): per view two compute kernels choose the
    /// pages in view, the blades each shows and the order, and one indirect draw records them all. Off is the CPU path (the reference): the CPU walks the pages, frustum-tests and sorts them.
    /// </summary>
    public bool GpuGrass { get; set; } = true;

    bool GpuGrassActive => GpuGrass && grassGpuProgram is not null;

    /// <summary>Bytes of the blade arena (160 MB) and slots of the cull table at the default grass distance; <see cref="GrassStoreScale"/> times them for a longer one.</summary>
    const ulong GrassArenaBytes = 160ul << 20;
    const int GrassSlotCapacity = 32768;
    /// <summary>The largest <see cref="GrassStoreScale"/>: 1.28 GB of blades, a grass distance of about 2.8 times the default.</summary>
    const int MaxGrassStoreScale = 8;
    /// <summary>The share of the device's memory budget a larger store may take (an 11 GB card fits the largest, an 8 GB one 6 times the default).</summary>
    const ulong GrassStoreBudgetShare = 8;
    /// <summary>The scale the store has, and the last one <see cref="VramGuard.Allows"/> refused (not asked again until the setting wants more).</summary>
    int grassStoreScale, grassStoreRefused;

    /// <summary>
    /// How many times the default store a grass distance needs: the pages in reach grow with the square of the distance, so x 2 needs 4 (640 MB);
    /// at most <see cref="MaxGrassStoreScale"/> and 1 / <see cref="GrassStoreBudgetShare"/> of the memory budget, at least 1 (a shorter distance
    /// keeps the default store). Pages past a smaller store wait for room (<see cref="TryQueuePage"/>).
    /// </summary>
    int GrassStoreScale(float grassRange, ulong budget)
    {
        int fits = budget > 0 ? (int)Math.Clamp(budget / GrassStoreBudgetShare / GrassArenaBytes, 1, MaxGrassStoreScale) : MaxGrassStoreScale;
        return Math.Min((int)Math.Ceiling(Math.Clamp(grassRange * grassRange / (DefaultGrassRange * DefaultGrassRange), 1, MaxGrassStoreScale)), fits);
    }

    void InitGrassStore(IGpuContext gpu)
    {
        grassStoreScale = GrassStoreScale(GrassRangeSetting, gpu.VideoMemoryBudget);
        grassStore = new FoliageGrassGpu(gpu, GrassArenaBytes * (ulong)grassStoreScale, GrassSlotCapacity * grassStoreScale);
        grassMeshCpu = ArenaMesh(grassProgram);
        grassMeshMotionCpu = ArenaMesh(grassMotionProgram);
        if (!grassStore.Kernels) return;
        grassFrame = new NativeFrame(gpu, extraStorage: 1);
        grassGpuProgram = new NativeProg(gpu, grassFrame, FoliageGrassShaders.GrassVertexGpu(), FoliageGrassShaders.GrassFragmentGpu(), "foliage grass gpu");
        grassMotionGpuProgram = new NativeProg(gpu, grassFrame, FoliageGrassShaders.GrassMotionVertexGpu(), FoliageGrassShaders.GrassMotionFragmentGpu(), "foliage grass motion gpu");
        grassMeshGpu = ArenaMesh(grassGpuProgram);
        grassMeshMotionGpu = ArenaMesh(grassMotionGpuProgram);
    }

    /// <summary>
    /// A grass distance raised past what the store holds (<see cref="GrassStoreScale"/>) replaces it with a larger one: every page is dropped and made
    /// again, nearest first. The store never shrinks while running (a lowered setting keeps it), so a slider drag rebuilds it at most a few times.
    /// The old store is disposed once the frames in flight are done with it; its pages' ranges are not given back to it (<see cref="GrassBuffer.Store"/>).
    /// The guard (<see cref="VramGuard.Allows"/>) is asked for the new store while the old one is still held; refused, the store stays as it is.
    /// </summary>
    void GrowGrassStore()
    {
        if (GrassStoreScale(GrassRangeSetting, 0) is var wanted && (wanted <= grassStoreScale || wanted <= grassStoreRefused)) return;
        int scale = GrassStoreScale(GrassRangeSetting, guard is { BudgetBytes: > 0 } g ? (ulong)g.BudgetBytes : Gpu.VideoMemoryBudget);
        if (scale <= grassStoreScale) { grassStoreRefused = wanted; return; }   // the budget caps it where it is
        if (guard is { } vram && !vram.Allows(GrassArenaBytes * (ulong)scale))
        {
            grassStoreRefused = wanted;
            Console.WriteLine($"foliage   grass store not grown to {GrassArenaBytes * (ulong)scale >> 20} MB: memory budget (VramGuard)");
            return;
        }
        foreach (var state in zones.Values)
        {
            foreach (var page in state.Pages.Values)
            {
                page.Dropped = true;
                (page.PendingBlades, page.PendingPrefixes, page.Buffers) = (null, null, null);
            }
            state.Pages.Clear();
            state.GrassRunning = 0;   // their jobs finish unread
        }
        var old = grassStore;
        grassStoreScale = scale;
        grassStore = new FoliageGrassGpu(Gpu, GrassArenaBytes * (ulong)scale, GrassSlotCapacity * scale) { Misses = old.Misses };
        grassMeshCpu = ArenaMesh(grassProgram);
        grassMeshMotionCpu = ArenaMesh(grassMotionProgram);
        if (grassGpuProgram is not null && grassMotionGpuProgram is not null)
        {
            grassMeshGpu = ArenaMesh(grassGpuProgram);
            grassMeshMotionGpu = ArenaMesh(grassMotionGpuProgram);
        }
        grassFull = false;
        Gpu.DeferDelete(old.Dispose);
        Console.WriteLine($"foliage   grass store grown to {GrassArenaBytes * (ulong)scale >> 20} MB for grass distance x {GrassRangeSetting / DefaultGrassRange:0.##}");
    }

    bool grassDisposing;

    void DisposeGrassStore()
    {
        grassDisposing = true;
        grassGpuProgram?.Dispose();
        grassMotionGpuProgram?.Dispose();
        grassFrame?.Dispose();
        grassStore.Dispose();
    }

    /// <summary>A blade program's inputs over the arena: location 0 the position and scale (a vec4), location 1 the yaw, both per instance.</summary>
    NativeMesh ArenaMesh(NativeProg p)
    {
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        var arena = grassStore.BladeBuffer;
        attributes[0] = new LegacyProgram.Attribute(new GpuBufferBinding(arena, 0), InternalFormat.Rgba32f, FoliageGrassGpu.BladeBytes, true);
        attributes[1] = new LegacyProgram.Attribute(new GpuBufferBinding(arena, 16), InternalFormat.R32f, FoliageGrassGpu.BladeBytes, true);
        return new NativeMesh { Layout = p.Layout(attributes), Vertices = p.Buffers(attributes, p.Own) };
    }

    // ---- pages into the store ----

    uint grassPageSerial;
    int grassZoneHigh;
    readonly Stack<int> freeGrassZones = [];

    int AllocGrassZone() => freeGrassZones.Count > 0 ? freeGrassZones.Pop() : grassZoneHigh++;

    void ReleaseGrassZone(ZoneState state)
    {
        if (state.GrassZone < 0) return;
        freeGrassZones.Push(state.GrassZone);
        state.GrassZone = -1;
    }

    /// <summary>The store had no room for the last page tried (<see cref="TryQueuePage"/>), and whether a page was tried while it was full this frame.</summary>
    bool grassFull, grassTriedFull;

    /// <summary>
    /// <see cref="QueuePage"/>, but while the store is full only one page a frame is tried; the others keep their blades and wait. Every page tried
    /// in a full store scans the arena's free list under its lock and gives back what it took: with a longer grass distance than the store holds
    /// (the Ultra tier's x 2, four times the pages) thousands of pages were retried every frame, 15 ms of the render thread.
    /// </summary>
    void TryQueuePage(ZoneState state, int key, GrassPage page, float[][] blades, int[] floats, int[][] prefixes)
    {
        if (grassFull && grassTriedFull)
        {
            if (page.PendingBlades is null) grassStore.Misses++;
            (page.PendingBlades, page.PendingFloats, page.PendingPrefixes) = (blades, floats, prefixes);
            return;
        }
        grassTriedFull |= grassFull;
        grassFull = !QueuePage(state, key, page, blades, floats, prefixes);
    }

    /// <summary>
    /// A generated grass page into the store: its blade ranges and slots now (all or none: without room the blades wait in the page and
    /// <see cref="UpdateGrass"/> tries again, <see cref="FoliageGrassGpu.Misses"/>), then the blades in steps of about 512 KB so no frame holds
    /// a whole page, then the slots, which make the page draw. Returns false when there was no room.
    /// </summary>
    bool QueuePage(ZoneState state, int key, GrassPage page, float[][] blades, int[] floats, int[][] prefixes)
    {
        var store = grassStore;
        var buffers = new GrassBuffer[blades.Length];
        bool room = true;
        for (int i = 0; i < blades.Length && room; i++)
        {
            var b = buffers[i] = new GrassBuffer { Count = floats[i] / FoliageGrassField.Stride, Prefixes = prefixes[i] };
            if (b.Count == 0) continue;
            b.Store = store;
            b.Range = store.AllocateBlades(b.Count);
            if (b.Range.IsEmpty) { room = false; break; }
            b.FirstBlade = FoliageGrassGpu.FirstBlade(b.Range);
            b.Slot = store.AllocateSlot();
            if (b.Slot < 0) room = false;
        }
        if (!room)
        {
            ReleaseBuffers(buffers.Where(b => b is not null).ToArray());
            if (page.PendingBlades is null) store.Misses++;
            (page.PendingBlades, page.PendingFloats, page.PendingPrefixes) = (blades, floats, prefixes);
            return false;
        }
        (page.PendingBlades, page.PendingFloats, page.PendingPrefixes) = (null, null, null);
        if (state.GrassZone < 0) state.GrassZone = AllocGrassZone();
        uint zone = (uint)state.GrassZone, serial = ++grassPageSerial;
        float x0 = state.X0 + key % PagesPerZone * PageSize, z0 = state.Z0 + key / PagesPerZone * PageSize;
        float y = state.Ground is { } ground ? ground.Height(x0 + PageSize / 2, z0 + PageSize / 2) : 0;
        for (int i = 0; i < buffers.Length; i++)
        {
            var b = buffers[i];
            if (b.Count == 0) continue;
            var data = blades[i];
            int bytes = floats[i] * sizeof(float);
            for (int at = 0; at < bytes; at += SlabBytes)
            {
                int start = at, length = Math.Min(SlabBytes, bytes - at);
                uploads.Enqueue(() =>
                {
                    if (!page.Dropped) store.WriteBlades(b.Range, start, MemoryMarshal.AsBytes(data.AsSpan()).Slice(start, length));
                });
            }
        }
        uploads.Enqueue(() =>
        {
            // Every blade write is done: the rented blades go back.
            foreach (var data in blades) if (data.Length > 0) ArrayPool<float>.Shared.Return(data);
            if (page.Dropped)
            {
                ReleaseBuffers(buffers);   // the page went out of range while its blades were being written
                return;
            }
            for (int i = 0; i < buffers.Length; i++)
            {
                var b = buffers[i];
                if (b.Slot < 0) continue;
                store.WriteSlot(b.Slot, new GrassSlot { X0 = x0, Z0 = z0, Y = y, FirstBlade = b.FirstBlade, Count = (uint)b.Count, Zone = zone, Local = (uint)i, Tie = serial << 8 | (uint)i }, b.Prefixes);
            }
            page.Buffers = buffers;
        });
        return true;
    }

    // ---- the GPU path ----

    readonly List<GrassPatchRow> grassPatchRows = [];
    GrassZoneRow[] grassZoneRows = new GrassZoneRow[16];

    /// <summary>
    /// The view's zone and patch tables: for every zone with pages in the store, its patches' rows (a zone's patches can change when it is laid
    /// out again, so the rows are written per view), as <see cref="PrepareGrass"/> and <see cref="PrepareMotion"/> read them: the range at the
    /// current grass range setting, the sprite's readiness, the push values of <see cref="GrassPush"/>. Textures are GL ids here (see <see cref="BindRows"/>).
    /// Returns the number of patches that can draw.
    /// </summary>
    int BuildGrassRows(bool motion, WorldRenderOptions options, bool coverage)
    {
        grassPatchRows.Clear();
        if (grassZoneRows.Length < grassZoneHigh) grassZoneRows = new GrassZoneRow[Math.Max(grassZoneHigh, grassZoneRows.Length * 2)];
        else Array.Clear(grassZoneRows);
        int active = 0;
        foreach (var state in zones.Values)
        {
            if (state.GrassZone < 0) continue;
            grassZoneRows[state.GrassZone] = new GrassZoneRow { PatchBase = (uint)grassPatchRows.Count, PatchCount = (uint)state.Patches.Count };
            for (int i = 0; i < state.Patches.Count; i++)
            {
                var patch = state.Patches[i];
                var g = patch.Grass;
                var (sprite, colour) = state.PatchTextures[i];
                // As Patch does on each call: counts as use, and brings an unloaded texture back.
                uint spriteId = sprite?.Key ?? 0, colourId = colour?.Key ?? 0;
                bool wind = patch.Layer.Wind;
                bool isActive = spriteId != 0 && (!motion || (wind && g.SwayLength != 0));
                bool hasColour = !motion && options.Textures && colourId != 0;
                uint flags = (g.CrossQuads ? FoliageGrassShaders.FlagCross : 0) | (hasColour ? FoliageGrassShaders.FlagColourMap : 0) |
                    (!motion && coverage ? FoliageGrassShaders.FlagCoverage : 0) | (!motion && options.Wireframe == 2 ? FoliageGrassShaders.FlagWireframe : 0) |
                    (isActive ? FoliageGrassShaders.FlagActive : 0);
                if (isActive) active++;
                grassPatchRows.Add(new GrassPatchRow
                {
                    Size = new Vector4(g.QuadMinWidth, g.QuadMaxWidth, g.QuadMinHeight, g.QuadMaxHeight),
                    ColourBounds = new Vector4(patch.X0, patch.Z0, patch.X1, patch.Z1),
                    Sway = motion || wind ? g.SwayLength : 0f, Range = GrassRange(patch), Frequency = 2f, Flags = flags,
                    Sprite = motion || options.Textures ? spriteId : 0, ColourMap = hasColour ? colourId : 0, VertexCount = g.CrossQuads ? 12u : 6u,
                });
            }
        }
        return active;
    }

    /// <summary>The rows' texture keys as bindless indices (inside the segment, as the CPU path's <see cref="Texture"/> calls are), and the tables into the frame's constants.</summary>
    GrassTables BindRows(uint nearDepth)
    {
        var rows = CollectionsMarshal.AsSpan(grassPatchRows);
        for (int i = 0; i < rows.Length; i++)
        {
            ref var r = ref rows[i];
            r.Sprite = Texture(r.Sprite);
            r.ColourMap = Texture(r.ColourMap);
            r.NearDepth = nearDepth;
        }
        return grassStore.Prepare(grassZoneRows.AsSpan(0, Math.Max(grassZoneHigh, 1)), rows);
    }

    /// <summary>One view's grass on the GPU: the cull into <see cref="GpuFrame.PreFrame"/>, then one indirect draw in a native segment. False when nothing can draw.</summary>
    bool DrawGrassGpu(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, Vector3 light, Vector3 fogColour, float fogDistance, bool coverage)
    {
        if (grassStore.SlotHigh == 0 || frustum.Length > 8) return false;
        if (BuildGrassRows(false, options, coverage) == 0) return false;
        var gp = grassGpuProgram!;
        var p = gp.P;
        // Prepare (wave 4, docs/renderer-native.md 6.2): the pass state, the cull (into PreFrame), the sets and the draw, into a job.
        var targets = Gpu.CurrentTargets();
        var state = GrassState(Gpu, coverage);
        int segment = SegmentId(GrassKind, p, targets, state);
        NewTextureSegment();
        var tables = BindRows(0);
        var (prefixIndex, fraction) = FoliageGrassGpu.DensityStep(Math.Min(GrassDensitySetting, MaxGrassDensity) / MaxGrassDensity);
        var result = grassStore.Dispatch(frustum, new Vector2(eye.X, eye.Z), PageSize, prefixIndex, fraction, in tables);
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance, Time = SwayPhase() };
        if (!depthPass) InteriorMaskRenderer.Apply(ref view, InteriorCut);
        var job = grassGpuJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Program) = (this, targets, state, p);
        job.Frame = grassFrame!.Prepare(in view, [tables.Patches.Binding]);
        job.Pipeline = result.IsEmpty ? null : PipelineFor(ref grassMeshGpu, gp, segment, state, targets.Formats, "foliage grass gpu");
        (job.Vertices, job.Result) = (grassMeshGpu.Vertices, result);
        Gpu.Record("foliage grass", job);
        DrawCalls += grassStore.LateDraws;
        DrawnBlades += (int)grassStore.LateBlades;
        return true;
    }

    readonly JobPool<GrassGpuJob> grassGpuJobs = new();

    /// <summary>One view's GPU grass as prepared (<see cref="DrawGrassGpu"/>): recorded on any thread, the same commands as before wave 4.</summary>
    sealed class GrassGpuJob : RecordJob
    {
        public FoliageRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public GpuProgram Program = default;
        public FrameBinding Frame;
        public GpuPipeline? Pipeline;
        public GpuBufferBinding[] Vertices = [];
        public GrassResult Result;

        public override void Record(IGpuCommandList cmd)
        {
            var (targets, state) = (Targets, State);
            cmd.SetViewport(targets.Viewport);
            cmd.SetScissor(targets.Scissor);
            cmd.SetRaster(state.Cull, state.Front);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            NativeFrame.Record(cmd, Program, in Frame);
            if (Pipeline is { } pipeline)
            {
                var r = Result;
                cmd.BindPipeline(pipeline);
                cmd.BindVertexBuffers(0, Vertices);
                cmd.DrawIndirectCount(r.Draws, r.DrawsOffset, r.Counters, r.CountersOffset, (uint)r.MaxDraws);
            }
        }

        public override void Release()
        {
            Pipeline = null;
            Owner.grassGpuJobs.Return(this);
        }
    }

    /// <summary><see cref="DrawGrassMotion"/> on the GPU: the same cull for the near slice's camera over the patches that sway, one indirect draw.</summary>
    void DrawGrassMotionGpu(MotionTargets targets, Vector3 eye, float time, float previousTime, Matrix4x4 previous)
    {
        if (grassStore.SlotHigh == 0 || motionFrustum.Length > 8) return;
        if (BuildGrassRows(true, default!, false) == 0) return;
        var gp = grassMotionGpuProgram!;
        var p = gp.P;
        var cmd = Gpu.BeginGuest("foliage grass motion");
        var pass = Gpu.CurrentTargets();
        var drawState = MotionState(Gpu);
        int segment = SegmentId(MotionKind, p, pass, drawState);
        cmd.SetViewport(pass.Viewport);
        cmd.SetScissor(pass.Scissor);
        cmd.SetRaster(drawState.Cull, drawState.Front);
        cmd.SetDepth(drawState.DepthTest, drawState.DepthWrite, drawState.Compare);
        cmd.SetDepthBias(drawState.BiasEnable, drawState.BiasConstant, drawState.BiasSlope);
        var view = new ViewConstants
        {
            ViewProjection = motionViewProjection, PreviousViewProjection = previous, Eye = eye, Time = time, PreviousTime = previousTime,
            NearPlanes = targets.NearPlanes, JitterNdc = targets.JitterNdc,
        };
        NewTextureSegment();
        var tables = BindRows(NearDepthIndex(targets.NearDepth));
        var (prefixIndex, fraction) = FoliageGrassGpu.DensityStep(Math.Min(GrassDensitySetting, MaxGrassDensity) / MaxGrassDensity);
        var result = grassStore.Dispatch(motionFrustum, new Vector2(eye.X, eye.Z), PageSize, prefixIndex, fraction, in tables, motionScale);
        grassFrame!.Bind(cmd, p, in view, [tables.Patches.Binding]);
        if (!result.IsEmpty)
        {
            cmd.BindPipeline(PipelineFor(ref grassMeshMotionGpu, gp, segment, drawState, pass.Formats, "foliage grass motion gpu"));
            cmd.BindVertexBuffers(0, grassMeshMotionGpu.Vertices);
            cmd.DrawIndirectCount(result.Draws, result.DrawsOffset, result.Counters, result.CountersOffset, (uint)result.MaxDraws);
        }
        Gpu.EndGuest(cmd);
    }
}
