using System.Buffers;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// The grass pages' storage (docs/renderer-native.md 5.4 and 5.6.2): every page's blades sit in one arena (<see cref="FoliageGrassGpu"/>), so a
/// blade draw is <c>Draw(vertices, blades, 0, firstBlade)</c> from one vertex binding. This keeps the store (grown with the grass distance), the
/// zones' numbers in its tables, the pages' way into it and back, and the blade programs' inputs over the arena; <see cref="FoliageRenderer"/>
/// walks the pages and draws them.
/// </summary>
sealed class FoliageGrassStore
{
    #region Store

    /// <summary>
    /// Bytes of the blade arena (160 MB) and slots of the cull table at the default grass distance; <see cref="StoreScale"/> times them for a
    /// longer one.
    /// </summary>
    const ulong GrassArenaBytes = 160ul << 20;

    /// <summary>
    /// Slots of the cull table at the default grass distance.
    /// </summary>
    const int GrassSlotCapacity = 32768;

    /// <summary>
    /// The largest <see cref="StoreScale"/>: 1.28 GB of blades, a grass distance of about 2.8 times the default.
    /// </summary>
    const int MaxGrassStoreScale = 8;

    /// <summary>
    /// The share of the device's memory budget a larger store may take (an 11 GB card fits the largest, an 8 GB one 6 times the default).
    /// </summary>
    const ulong GrassStoreBudgetShare = 8;

    /// <summary>
    /// The context the store and the programs are made on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The renderer's CPU-path blade programs (their inputs over the arena are made again with every store).
    /// </summary>
    readonly NativeProg _grassProgram, _grassMotionProgram;

    /// <summary>
    /// The scale the store has, and the last one <see cref="VramGuard.Allows"/> refused (not asked again until the setting wants more).
    /// </summary>
    int _grassStoreScale, _grassStoreRefused;

    /// <summary>
    /// The store (replaced by a larger one when the grass distance grows, <see cref="Grow"/>).
    /// </summary>
    public FoliageGrassGpu Store { get; private set; }

    /// <summary>
    /// The native frame of the GPU blade programs (set 0 with the patch table as an extra storage binding); null without the kernels.
    /// </summary>
    public NativeFrame? Frame { get; }

    /// <summary>
    /// The GPU path's blade programs (null without the kernels).
    /// </summary>
    public NativeProg? GpuProgram { get; }

    /// <summary>
    /// The GPU path's blade program of the motion pass (null without the kernels).
    /// </summary>
    public NativeProg? MotionGpuProgram { get; }

    /// <summary>
    /// The blade programs' vertex layout over the arena (instance rate: vec4 at 0, float at 16, stride 20), and their pipelines per segment: the
    /// CPU path's colour program.
    /// </summary>
    public FoliageNativeMesh MeshCpu;

    /// <summary>
    /// The CPU path's motion program over the arena.
    /// </summary>
    public FoliageNativeMesh MeshMotionCpu;

    /// <summary>
    /// The GPU path's colour program over the arena.
    /// </summary>
    public FoliageNativeMesh MeshGpu;

    /// <summary>
    /// The GPU path's colour program over the arena for the depth-first pair (<see cref="FoliageRenderer.GrassDepthFirst"/>): its depth draw, and
    /// its colour draw where the depth is its own (pipelines of their own segment kinds).
    /// </summary>
    public FoliageNativeMesh MeshGpuDepth, MeshGpuEqual;

    /// <summary>
    /// The GPU path's motion program over the arena.
    /// </summary>
    public FoliageNativeMesh MeshMotionGpu;

    /// <summary>
    /// The store for the grass distance <paramref name="grassRange"/> and <paramref name="areas"/> places kept loaded, and the GPU path's programs
    /// where the device runs its kernels.
    /// </summary>
    public FoliageGrassStore(IGpuContext gpu, NativeProg grassProgram, NativeProg grassMotionProgram, Queue<Action> uploads, float grassRange,
        int areas)
    {
        (_gpu, _grassProgram, _grassMotionProgram, _uploads) = (gpu, grassProgram, grassMotionProgram, uploads);
        _grassStoreScale = StoreScale(grassRange, areas, gpu.VideoMemoryBudget);
        Store = new FoliageGrassGpu(gpu, GrassArenaBytes * (ulong)_grassStoreScale, GrassSlotCapacity * _grassStoreScale);
        MeshCpu = ArenaMesh(_grassProgram);
        MeshMotionCpu = ArenaMesh(_grassMotionProgram);
        if (!Store.Kernels) return;
        Frame = new NativeFrame(gpu, extraStorage: 1);
        GpuProgram = new NativeProg(gpu, Frame, FoliageGrassShaders.GrassVertexGpu(), FoliageGrassShaders.GrassFragmentGpu(),
            "foliage grass gpu");
        MotionGpuProgram = new NativeProg(gpu, Frame, FoliageGrassShaders.GrassMotionVertexGpu(),
            FoliageGrassShaders.GrassMotionFragmentGpu(), "foliage grass motion gpu");
        MeshGpu = ArenaMesh(GpuProgram);
        (MeshGpuDepth, MeshGpuEqual) = (ArenaMesh(GpuProgram), ArenaMesh(GpuProgram));
        MeshMotionGpu = ArenaMesh(MotionGpuProgram);
    }

    /// <summary>
    /// A grass distance raised past what the store holds (<see cref="StoreScale"/>) replaces it with a larger one: every page of
    /// <paramref name="zones"/> is dropped and made again, nearest first. The store never shrinks while running (a lowered setting keeps it), so a
    /// slider drag rebuilds it at most a few times. The old store is disposed once the frames in flight are done with it; its pages' ranges are not
    /// given back to it (<see cref="FoliageGrassBuffer.Store"/>). The guard (<see cref="VramGuard.Allows"/>) is asked for the new store while the old
    /// one is still held; refused, the store stays as it is.
    /// </summary>
    public void Grow(float grassRange, int areas, VramGuard? guard, Dictionary<ZoneCoordinate, FoliageZoneState> zones)
    {
        if (GpuContext.DiagNoGrow) return;
        if (StoreScale(grassRange, areas, 0) is var wanted && (wanted <= _grassStoreScale || wanted <= _grassStoreRefused))
            return;
        int scale = StoreScale(grassRange, areas, guard is { BudgetBytes: > 0 } g ? (ulong)g.BudgetBytes : _gpu.VideoMemoryBudget);
        // the budget caps it where it is
        if (scale <= _grassStoreScale)
        {
            _grassStoreRefused = wanted;
            return;
        }
        if (guard is { } vram && !vram.Allows(GrassArenaBytes * (ulong)scale))
        {
            _grassStoreRefused = wanted;
            Console.WriteLine($"foliage   grass store not grown to {GrassArenaBytes * (ulong)scale >> 20} MB: memory budget (VramGuard)");
            return;
        }
        DropAllPages(zones);
        Replace(scale);
        Console.WriteLine($"foliage   grass store grown to {GrassArenaBytes * (ulong)scale >> 20} MB " +
            $"for grass distance x {grassRange / FoliageRenderer.DefaultGrassRange:0.##}");
    }

    /// <summary>
    /// How many times the default store a grass distance needs: the pages in reach grow with the square of the distance, so x 2 needs 4 (640 MB);
    /// at most <see cref="MaxGrassStoreScale"/> and 1 / <see cref="GrassStoreBudgetShare"/> of the memory budget, at least 1 (a shorter distance
    /// keeps the default store). Pages past a smaller store wait for room (<see cref="TryQueuePage"/>).
    /// </summary>
    static int StoreScale(float grassRange, int areas, ulong budget)
    {
        int fits = budget > 0 ? (int)Math.Clamp(budget / GrassStoreBudgetShare / GrassArenaBytes, 1, MaxGrassStoreScale) : MaxGrassStoreScale;

        // Each place kept loaded besides the eye's (Areas) holds its own pages: the store is that many times larger, within the same caps.
        const float defaultRange = FoliageRenderer.DefaultGrassRange;
        return Math.Min((int)Math.Ceiling(Math.Clamp(grassRange * grassRange / (defaultRange * defaultRange) * areas, 1,
            MaxGrassStoreScale)), fits);
    }

    /// <summary>
    /// Every page of <paramref name="zones"/> dropped for a new store: their jobs finish unread.
    /// </summary>
    static void DropAllPages(Dictionary<ZoneCoordinate, FoliageZoneState> zones)
    {
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
    }

    /// <summary>
    /// The store replaced by one of <paramref name="scale"/> times the default (the old one disposed after the frames in flight).
    /// </summary>
    void Replace(int scale)
    {
        var old = Store;
        _grassStoreScale = scale;
        Store = new FoliageGrassGpu(_gpu, GrassArenaBytes * (ulong)scale, GrassSlotCapacity * scale) { Misses = old.Misses };
        MeshCpu = ArenaMesh(_grassProgram);
        MeshMotionCpu = ArenaMesh(_grassMotionProgram);
        if (GpuProgram is not null && MotionGpuProgram is not null)
        {
            MeshGpu = ArenaMesh(GpuProgram);
            (MeshGpuDepth, MeshGpuEqual) = (ArenaMesh(GpuProgram), ArenaMesh(GpuProgram));
            MeshMotionGpu = ArenaMesh(MotionGpuProgram);
        }
        Full = false;
        Version++;
        _gpu.DeferDelete(old.Dispose);
    }

    /// <summary>
    /// A blade program's inputs over the arena: location 0 the position and scale (a vec4), location 1 the yaw, both per instance.
    /// </summary>
    FoliageNativeMesh ArenaMesh(NativeProg p)
    {
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        var arena = Store.BladeBuffer;
        attributes[0] = new LegacyProgram.Attribute(new GpuBufferBinding(arena, 0), InternalFormat.Rgba32F, FoliageGrassGpu.BladeBytes, true);
        attributes[1] = new LegacyProgram.Attribute(new GpuBufferBinding(arena, 16), InternalFormat.R32F, FoliageGrassGpu.BladeBytes, true);
        return new FoliageNativeMesh { Layout = p.Layout(attributes), Vertices = p.Buffers(attributes, p.Own) };
    }

    #endregion

    #region Pages

    /// <summary>
    /// The renderer's upload queue (render thread, within a frame budget).
    /// </summary>
    readonly Queue<Action> _uploads;

    /// <summary>
    /// Numbers the pages placed (the slots' tie-break).
    /// </summary>
    uint _grassPageSerial;

    /// <summary>
    /// One past the highest zone number given out.
    /// </summary>
    int _grassZoneHigh;

    /// <summary>
    /// Zone numbers given back.
    /// </summary>
    readonly Stack<int> _freeGrassZones = [];

    /// <summary>
    /// Bumped by every change the page walk (<see cref="FoliageRenderer.UpdateGrass"/>) reads besides the eye and the kept points: a zone laid out
    /// or dropped, a page freed or attached, the store replaced.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// The store had no room for the last page tried (<see cref="TryQueuePage"/>).
    /// </summary>
    public bool Full { get; set; }

    /// <summary>
    /// A page was tried while the store was full this frame (cleared each frame by the page walk).
    /// </summary>
    public bool TriedFull { get; set; }

    /// <summary>
    /// The renderer is being disposed: the pages are freed with the store, not one by one.
    /// </summary>
    public bool Disposing { get; private set; }

    /// <summary>
    /// One past the highest zone number given out (the size of the view's zone table).
    /// </summary>
    public int ZoneHigh => _grassZoneHigh;

    /// <summary>
    /// Something the page walk reads changed (a zone laid out or dropped).
    /// </summary>
    public void Changed() => Version++;

    /// <summary>
    /// Gives a zone's number in the tables back (when it is dropped).
    /// </summary>
    public void ReleaseZone(FoliageZoneState state)
    {
        if (state.GrassZone < 0) return;
        _freeGrassZones.Push(state.GrassZone);
        state.GrassZone = -1;
    }

    /// <summary>
    /// <see cref="QueuePage"/>, but while the store is full only one page a frame is tried; the others keep their blades and wait. Every page tried
    /// in a full store scans the arena's free list under its lock and gives back what it took: with a longer grass distance than the store holds
    /// (the Ultra tier's x 2, four times the pages) thousands of pages were retried every frame, 15 ms of the render thread.
    /// </summary>
    public void TryQueuePage(FoliageZoneState state, int key, FoliageGrassPage page, float[][] blades, int[] floats, int[][] prefixes)
    {
        if (Full && TriedFull)
        {
            if (page.PendingBlades is null) Store.Misses++;
            (page.PendingBlades, page.PendingFloats, page.PendingPrefixes) = (blades, floats, prefixes);
            return;
        }
        TriedFull |= Full;
        Full = !QueuePage(state, key, page, blades, floats, prefixes);
    }

    /// <summary>
    /// A page out of range: its queued uploads free what they made instead of attaching it, and its blades go back to the store.
    /// </summary>
    public void FreePage(FoliageGrassPage page)
    {
        Version++;
        page.Dropped = true;   // an upload still queued for it frees what it made instead of attaching it
        page.PendingBlades = null;
        page.PendingPrefixes = null;
        if (page.Buffers is null) return;
        ReleaseBuffers(page.Buffers);
        page.Buffers = null;
    }

    /// <summary>
    /// A page's blade ranges and slots back to the grass store (after the frames in flight).
    /// </summary>
    public void ReleaseBuffers(FoliageGrassBuffer[] buffers)
    {
        if (Disposing) return;
        foreach (var b in buffers)
        {
            if (b.Store != Store) continue;
            if (b.Slot >= 0) Store.FreeSlot(b.Slot);
            if (!b.Range.IsEmpty) Store.FreeBlades(b.Range);
            b.Slot = -1;
            b.Range = default;
        }
    }

    /// <summary>
    /// The next zone number (a given-back one first).
    /// </summary>
    int AllocZone() => _freeGrassZones.Count > 0 ? _freeGrassZones.Pop() : _grassZoneHigh++;

    /// <summary>
    /// A generated grass page into the store: its blade ranges and slots now (all or none: without room the blades wait in the page and
    /// <see cref="FoliageRenderer.UpdateGrass"/> tries again, <see cref="FoliageGrassGpu.Misses"/>), then the blades in steps of about 512 KB so no
    /// frame holds a whole page, then the slots, which make the page draw. Returns false when there was no room. (One method: the upload steps
    /// share the page's captured state.)
    /// </summary>
    public bool QueuePage(FoliageZoneState state, int key, FoliageGrassPage page, float[][] blades, int[] floats, int[][] prefixes)
    {
        var store = Store;
        var buffers = new FoliageGrassBuffer[blades.Length];
        if (!AllocateBuffers(store, buffers, floats, prefixes))
        {
            ReleaseBuffers(buffers.Where(b => b is not null).ToArray());
            if (page.PendingBlades is null) store.Misses++;
            (page.PendingBlades, page.PendingFloats, page.PendingPrefixes) = (blades, floats, prefixes);
            return false;
        }
        (page.PendingBlades, page.PendingFloats, page.PendingPrefixes) = (null, null, null);
        if (state.GrassZone < 0) state.GrassZone = AllocZone();
        uint zone = (uint)state.GrassZone, serial = ++_grassPageSerial;
        float x0 = state.X0 + key % FoliageRenderer.PagesPerZone * FoliageRenderer.PageSize;
        float z0 = state.Z0 + key / FoliageRenderer.PagesPerZone * FoliageRenderer.PageSize;
        SpanHeights(state, page, blades, floats);
        float pageMinY = page.MinY, pageMaxY = page.MaxY;
        for (int i = 0; i < buffers.Length; i++)
        {
            var b = buffers[i];
            if (b.Count == 0) continue;
            var data = blades[i];
            int bytes = floats[i] * sizeof(float);
            for (int at = 0; at < bytes; at += FoliageRenderer.SlabBytes)
            {
                int start = at, length = Math.Min(FoliageRenderer.SlabBytes, bytes - at);
                _uploads.Enqueue(() =>
                {
                    if (!page.Dropped)
                        store.WriteBlades(b.Range, start, MemoryMarshal.AsBytes(data.AsSpan()).Slice(start, length));
                });
            }
        }
        _uploads.Enqueue(() =>
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
                store.WriteSlot(b.Slot, new GrassSlot { X0 = x0, Z0 = z0, YMin = pageMinY, YMax = pageMaxY, FirstBlade = b.FirstBlade,
                    Count = (uint)b.Count, Zone = zone, Local = (uint)i, Tie = serial << 8 | (uint)i }, b.Prefixes);
            }
            page.Buffers = buffers;
            Version++;
        });
        return true;
    }

    /// <summary>
    /// The heights the page's blades span into <see cref="FoliageGrassPage.MinY"/> and <see cref="FoliageGrassPage.MaxY"/>: the roots' (the second
    /// float of a blade), and the tallest quad of the patches above the highest.
    /// </summary>
    static void SpanHeights(FoliageZoneState state, FoliageGrassPage page, float[][] blades, int[] floats)
    {
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity, tallest = 0;
        for (int i = 0; i < blades.Length; i++)
        {
            if (floats[i] == 0) continue;

            var data = blades[i];
            for (int at = 1; at < floats[i]; at += FoliageGrassField.Stride)
            {
                float h = data[at];
                if (h < minY) minY = h;
                if (h > maxY) maxY = h;
            }

            if (i < state.Patches.Count) tallest = MathF.Max(tallest, state.Patches[i].Grass.QuadMaxHeight);
        }

        (page.MinY, page.MaxY) = (minY, maxY + tallest);
    }

    /// <summary>
    /// Each patch's blade range and slot in <paramref name="store"/> (all or none: false at the first that finds no room, the ones made so far in
    /// <paramref name="buffers"/>).
    /// </summary>
    static bool AllocateBuffers(FoliageGrassGpu store, FoliageGrassBuffer[] buffers, int[] floats, int[][] prefixes)
    {
        bool room = true;
        for (int i = 0; i < buffers.Length && room; i++)
        {
            var b = buffers[i] = new FoliageGrassBuffer { Count = floats[i] / FoliageGrassField.Stride, Prefixes = prefixes[i] };
            if (b.Count == 0) continue;
            b.Store = store;
            b.Range = store.AllocateBlades(b.Count);
            if (b.Range.IsEmpty)
            {
                room = false;
                break;
            }
            b.FirstBlade = FoliageGrassGpu.FirstBlade(b.Range);
            b.Slot = store.AllocateSlot();
            if (b.Slot < 0) room = false;
        }
        return room;
    }

    #endregion

    #region Lifetime

    /// <summary>
    /// The renderer's disposal began: the pages are freed with the store, not given back one by one.
    /// </summary>
    public void BeginDispose() => Disposing = true;

    /// <summary>
    /// Disposes the GPU path's programs, their frame and the store.
    /// </summary>
    public void Dispose()
    {
        Disposing = true;
        GpuProgram?.Dispose();
        MotionGpuProgram?.Dispose();
        Frame?.Dispose();
        Store.Dispose();
    }

    #endregion
}
