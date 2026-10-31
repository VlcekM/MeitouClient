using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;
using Meitou.Rendering.Impostors;

namespace Meitou.Rendering;

/// <summary>
/// Kenshi's foliage around the camera (docs/render-foliage.md; the rules in docs/formats/foliage.md): trees, bushes, rocks
/// (mineable resource rocks too) and grass, placed per zone by <see cref="FoliageLayout"/> on worker threads.
/// <list type="bullet">
/// <item>Meshes: one instanced draw per (mesh part, material) with the shared mesh shader (<see cref="FoliageShaders"/>);
/// leaves and FOLIAGE-mode meshes are cut out on the normal map's alpha, double-sided, with alpha to coverage when the target is
/// multisampled; every instance fades out (dithered) at its layer's range, by distance along the ground as the game's pages.
/// TERRAIN-mode meshes go through the terrain shader (<see cref="TerrainRenderer.DrawMeshes"/>), one draw each.</item>
/// <item>Grass: blades generated per 576-unit page in range (<see cref="FoliageGrassField"/>), one instanced draw per (page, grass).</item>
/// </list>
/// <see cref="Draw"/> can be called with any camera (e.g. the water reflection's) between <see cref="Update"/> calls.
/// </summary>
public sealed unsafe partial class FoliageRenderer : IDisposable
{
    /// <summary>Grass pages per zone side (576 units each).</summary>
    const int PagesPerZone = 8;
    const float PageSize = WorldLayout.ZoneSize / (float)PagesPerZone;
    const int InstanceStride = 64;

    /// <summary>The native GPU API (docs/renderer-native.md 7.1 step 8; since phase 8 stage 1 the only one this renderer uses).</summary>
    public IGpuContext Gpu { get; }
    /// <summary>The meshes' allocation name (the F12 VRAM pie groups by it).</summary>
    const string MeshAllocationName = "foliage meshes";
    readonly GameInstall install;
    readonly GameDatabase db;
    readonly WorldLevelData levels;
    readonly AssetLocator assets;
    readonly FoliageCatalog catalog;
    // The native programs (docs/renderer-native.md 3.3, step O: the native model, the same maths), made with the renderer so a draw never compiles.
    readonly NativeFrame nativeFrame;
    readonly NativeProg colourMesh, colourSolidMesh, depthMesh, prepassMesh, prepassSolidMesh, grassProgram, grassMotionProgram;   // colourSolidMesh: colourMesh without the discards (EarlyDepth)
    readonly WorldTextureCache textures;
    readonly ConcurrentBag<FoliageWorld> worlds = [];
    /// <summary>The zone layout cache (null with <c>--no-load-cache</c>); see <see cref="FoliageLayoutCache"/>.</summary>
    public FoliageLayoutCache? LayoutCache { get; }
    readonly List<FoliageWorld> allWorlds = [];
    readonly Dictionary<ZoneCoordinate, ZoneState> zones = [];
    readonly Dictionary<FoliageMesh, MeshAsset> assetsByMesh = [];
    readonly List<MeshAsset> decoding = [];
    readonly Queue<Action> uploads = new();
    readonly StepCosts stepCosts = new();
    readonly Dictionary<MeshAsset, Batch> batches = [];
    readonly List<Batch> active = [];
    readonly List<(MeshBindings, int, Matrix4x4)> terrainDraws = [];
    readonly List<(MeshBindings, int, Matrix4x4)> terrainDrawsCut = [];   // those over an interior mask (InteriorCut)
    readonly Stopwatch clock = Stopwatch.StartNew();
    /// <summary>Seconds that drive the grass sway; null uses a wall clock started with the renderer. Screenshots fix it (pictures repeat exactly) and the game sets its own clock.</summary>
    public double? SwaySeconds { get; set; }

    /// <summary>
    /// A presentation-only move of some placed meshes (the Meitou title's staging): every instance of a mesh whose name <see cref="FoliageShift.Moves"/>
    /// within <see cref="FoliageShift.Radius"/> of <see cref="FoliageShift.Centre"/> (x, z) is carried by <see cref="FoliageShift.Offset"/> and set on the
    /// ground there. Applied as zones are accepted; setting or clearing it lays the zones it touches out again. Null, the game's placement, in play.
    /// </summary>
    public FoliageShift? Shift
    {
        get => shift;
        set
        {
            var touched = new[] { shift, value };
            shift = value;
            dropping.Clear();
            foreach (var state in zones.Values)
                if (state.Job is null && touched.Any(s => s is not null && BoxDistance(state.X0, state.Z0, WorldLayout.ZoneSize, s.Centre with { Y = 0 }) <= s.Radius))
                    dropping.Add(state);
            foreach (var state in dropping) Drop(state);
        }
    }
    FoliageShift? shift;

    void ApplyShift(FoliageMesh mesh, FoliageInstanceRecord[] instances)
    {
        if (shift is not { } s || !s.Moves(mesh.Name)) return;
        for (int i = 0; i < instances.Length; i++)
        {
            ref var r = ref instances[i];
            float x = r.Transform.M41, z = r.Transform.M43;
            if ((x - s.Centre.X) * (x - s.Centre.X) + (z - s.Centre.Z) * (z - s.Centre.Z) > s.Radius * s.Radius) continue;
            float nx = x + s.Offset.X, nz = z + s.Offset.Z;
            r.Transform.M41 = nx;
            r.Transform.M42 += s.Ground(nx, nz) - s.Ground(x, z);
            r.Transform.M43 = nz;
            (r.Ground.X, r.Ground.Y) = (nx, nz);
        }
    }
    readonly int workers;
    /// <summary>Draw the grass / the meshes and impostors (not the TERRAIN-mode rocks) (the bench's `grass` and `foliage-meshes` switches).</summary>
    public bool DrawGrass { get; set; } = true;
    public bool DrawMeshes { get; set; } = true;
    /// <summary>Zone layouts in flight: whole ones and far-only ones, each limited to <see cref="workers"/> at a time.</summary>
    int runningWhole, runningFar;
    /// <summary>
    /// The view is under the loading screen (the host sets it each frame): its frames need not be smooth, so enough layouts are in flight to keep every streaming
    /// thread busy between two frames (four per <see cref="BackgroundWork.Threads"/>, not <see cref="workers"/>: one from the cache takes a few milliseconds, and finished
    /// ones are taken once a frame) and uploads take up to <see cref="CoveredBudgetMs"/> a frame. A town's thousands of zones out to the Meitou range, three at a
    /// time, held its loading screen up seconds with the workers idle.
    /// </summary>
    public bool Covered { get; set; }
    const double CoveredBudgetMs = 16;

    public FoliageRenderer(IGpuContext gpu, GameInstall install, GameDatabase db, WorldLevelData levels, AssetLocator assets)
    {
        Gpu = gpu;
        this.install = install;
        this.db = db;
        this.levels = levels;
        this.assets = assets;
        var watch = Stopwatch.StartNew();
        catalog = FoliageCatalog.Load(db);
        if (!LoadCaches.Disabled)
        {
            try { LayoutCache = new FoliageLayoutCache(install, catalog, FoliageWorld.ReadTowns(db, levels)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LayoutCache = null; }
        }
        nativeFrame = new NativeFrame(gpu);
        colourMesh = new NativeProg(gpu, nativeFrame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshFragmentNative(), "foliage meshes");
        colourSolidMesh = new NativeProg(gpu, nativeFrame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshFragmentNative(solid: true), "foliage meshes solid");
        depthMesh = new NativeProg(gpu, nativeFrame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshDepthNative(), "foliage mesh depth");
        prepassMesh = new NativeProg(gpu, nativeFrame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshPrepassNative(), "foliage mesh prepass");
        prepassSolidMesh = new NativeProg(gpu, nativeFrame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshPrepassNative(solid: true), "foliage mesh prepass solid");
        grassProgram = new NativeProg(gpu, nativeFrame, FoliageShaders.GrassVertexNative(), FoliageShaders.GrassFragmentNative(), "foliage grass");
        grassMotionProgram = new NativeProg(gpu, nativeFrame, FoliageShaders.GrassMotionVertexNative(), FoliageShaders.GrassMotionFragmentNative(), "foliage grass motion");
        textures = new WorldTextureCache(gpu, assets, "foliage textures");
        // Metal's GPU cull and its separate pre-frame command buffer are opt-in after the 2026-10-23 progress
        // timeouts; the CPU cull is the stable reference. Vulkan keeps the GPU path.
        bool metalGpuCull = Environment.GetEnvironmentVariable("MEITOU_METAL_GPU_CULL") is "1" or "true";
        gpuCull = gpu is GpuContext || metalGpuCull ? new FoliageGpuCull(gpu) : null;
        InitGrassStore(gpu);
        workers = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
        var used = catalog.ByBiome.Values.SelectMany(l => l).Distinct().ToList();
        MeshRange = used.Where(l => !l.IsGrass).Select(l => l.Range).DefaultIfEmpty(0).Max();
        NearMeshRange = used.Where(l => !l.IsGrass && !FoliageLayout.IsFarLayer(l)).Select(l => l.Range).DefaultIfEmpty(0).Max();
        GrassMaxRange = used.Where(l => l.IsGrass).Select(l => l.Range).DefaultIfEmpty(0).Max();
        LoadMs = watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Set by the world frame around the main view's draw only: the meshes may be drawn with the depth prepass (<see cref="FoliagePrepass"/>); the reflection and the shadow cascades never are.</summary>
    public bool PrepassView { get; set; }

    /// <summary>Foliage drawn at all (the F key).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The mesh instances culled on the GPU (docs/renderer-native.md 5.3 to 5.6, step A2): per view, compute kernels write the
    /// visible instances and the indirect draw arguments, the CPU walks only zones and groups. Off is the A1 CPU
    /// cull (<see cref="FoliageCull"/>), the reference of the tests. The TERRAIN-mode rocks go with it: the same dispatch, drawn through <see cref="TerrainRenderer.DrawMeshesIndirect"/> (5.6.1).
    /// </summary>
    public bool GpuCull { get; set; } = true;
    readonly FoliageGpuCull? gpuCull;
    /// <summary>settings.cfg <c>foliage range</c>, <c>grass range</c> and <c>grass density</c> (the game's defaults are 1; the viewer draws
    /// foliage 4x and grass 8x as far by default).</summary>
    public float RangeSetting { get; set; } = Env("MEITOU_FOLIAGE_RANGE", 4);
    public float GrassRangeSetting { get; set; } = Env("MEITOU_GRASS_RANGE", DefaultGrassRange);
    /// <summary>The "Grass draw distance x" setting's default.</summary>
    public const float DefaultGrassRange = 8;
    public float GrassDensitySetting { get; set; } = 1;
    /// <summary>The Meitou <c>grass-velocity</c> switch (docs/render-foliage.md "Grass motion vectors"): the motion pass redraws only the swaying blades near enough to move by <see cref="GrassVelocityPixels"/> a frame (<see cref="GrassMotionReach"/>); off redraws all of them.</summary>
    public bool GrassVelocityCull { get; set; } = true;
    /// <summary>Pixels of a blade's own motion a frame under which the camera reprojection stands for it.</summary>
    public float GrassVelocityPixels { get; set; } = GrassMotionReach.DefaultThresholdPixels;
    /// <summary>MEITOU_FOLIAGE_RANGE and MEITOU_GRASS_RANGE start the two range settings at other values (offscreen measurements).</summary>
    static float Env(string name, float fallback) => float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>The longest mesh layer range in the catalog at setting 1 (zones are laid out up to it).</summary>
    public float MeshRange { get; }
    /// <summary>The longest grass layer range in the catalog at setting 1.</summary>
    public float GrassMaxRange { get; }
    public double LoadMs { get; }
    public int DrawnInstances { get; private set; }
    public int DrawnBlades { get; private set; }
    public int DrawCalls { get; private set; }
    public double LastDrawCpuMs { get; private set; }
    /// <summary>The groups the last CPU cull tested (diagnostics for the parallel path).</summary>
    public int LastCullGroups { get; private set; }
    /// <summary>Views the GPU cull dispatched in all (diagnostic; 0 while the CPU path or the Metal GPU cull is off).</summary>
    public long GpuCullDispatches => gpuCull?.Dispatched ?? 0;
    /// <summary>With <see cref="WorldFrame.DetailedStats"/>: the last depth draw's and the frame's main draws' CPU time by step (culling, upload, meshes, grass, TERRAIN-mode rocks).</summary>
    internal string DepthDetail = "", MainDetail = "";
    /// <summary>GPU time of the foliage draws of one frame (the main camera's draw, grass included), from timestamp queries a frame or two old.</summary>
    public double GpuMs { get; private set; }
    public double LastUpdateMs { get; private set; }
    /// <summary>Streaming diagnostics (the fly benchmark), ground distances from the eye, infinity when none: the nearest zone within the
    /// <see cref="NearReach"/> not laid out whole yet, the nearest within the <see cref="FarReach"/> not laid out at all, and the nearest zone
    /// with a group in range whose mesh is not resident (since the last <see cref="Update"/>).</summary>
    public float NearestIncompleteZone { get; private set; } = float.PositiveInfinity;
    public float NearestUnlaidZone { get; private set; } = float.PositiveInfinity;
    public float NearestMissingMesh { get; private set; } = float.PositiveInfinity;
    /// <summary>The nearest grass page of a laid-out zone within four fifths of its range (before the blades sink) that is not uploaded yet.</summary>
    public float NearestMissingGrass { get; private set; } = float.PositiveInfinity;

    /// <summary>
    /// The <c>range</c> switch (Enhancements): Meitou (true, the default) draws each mesh to the range of its size class
    /// (<see cref="SmallRange"/>, <see cref="MediumRange"/>, <see cref="LargeRange"/>; <see cref="FoliageSizes"/>) instead of its layer's
    /// range × <see cref="RangeSetting"/>. Large meshes of FAR layers keep the longer of the two, so the landmark formations still reach
    /// 8000 × the setting. False: the game's per-layer ranges.
    /// </summary>
    public bool MeitouRange { get; set; } = true;
    /// <summary>How far small, medium and large meshes are drawn with <see cref="MeitouRange"/> (units along the ground; the Tab sliders).</summary>
    public float SmallRange { get; set; } = FoliageSizes.DefaultSmallRange;
    public float MediumRange { get; set; } = FoliageSizes.DefaultMediumRange;
    public float LargeRange { get; set; } = FoliageSizes.DefaultLargeRange;
    /// <summary>The memory-pressure guard (<see cref="VramGuard"/>): every range here (and the reaches that decide which zones are laid out) is
    /// multiplied by its scale, nothing new is started while it is under pressure, and idle meshes are evicted sooner. Null: no clamp.</summary>
    public VramGuard? Guard
    {
        get => guard;
        set
        {
            guard = value;
            textures.Guard = value;
            if (gpuCull is not null)
            {
                gpuCull.MayGrow = value is null ? null : value.Allows;
                gpuCull.Scratch.MayGrow = value is null ? null
                    : Gpu is GpuContext vk ? (Func<ulong, bool>)(bytes => vk.Device.Allocator.FitsInFreeSpace(bytes) || value.AllowsPriority(bytes))
                    : value.Allows;
            }
        }
    }
    VramGuard? guard;
    float Scale => guard?.RangeScale ?? 1f;
    /// <summary>Seconds an unused mesh stays under pressure (instead of <see cref="IdleSeconds"/>).</summary>
    public double GuardIdleSeconds { get; set; } = 2;
    float LongestClassRange => Math.Max(Math.Max(SmallRange, MediumRange), LargeRange) * Scale;
    float ClassRange(FoliageSizeClass c) => (c switch { FoliageSizeClass.Small => SmallRange, FoliageSizeClass.Medium => MediumRange, _ => LargeRange }) * Scale;

    /// <summary>Zones within this ground distance of the eye are laid out whole: the longest MEDIUM / CLOSE mesh layer range (with
    /// <see cref="MeitouRange"/>: the longest class range, as any class can be in a MEDIUM layer) or grass range at the current settings.</summary>
    public float NearReach => Math.Max(MeitouRange ? LongestClassRange : NearMeshRange * RangeSetting * Scale, GrassMaxRange * GrassRangeSetting * Scale);
    /// <summary>Zones within this distance are laid out at least for their far layers (FAR, 8000 x the setting).</summary>
    public float FarReach => Math.Max(MeshRange * RangeSetting * Scale, NearReach);
    /// <summary>The longest range of a mesh layer that is not a far layer (<see cref="FoliageLayout.IsFarLayer"/>), at setting 1.</summary>
    public float NearMeshRange { get; }
    /// <summary>How far ahead (seconds of the eye's current motion) the layout looks; <see cref="MaxLookahead"/> caps the distance.</summary>
    public float LookaheadSeconds { get; set; } = 1.5f;
    const float MaxLookahead = 2 * WorldLayout.ZoneSize;
    /// <summary>Whole layouts reach this much beyond the near reach even at rest, so a start at full speed (or a turn) finds the next ring ready.</summary>
    const float PrefetchMargin = WorldLayout.ZoneSize / 2;
    Vector3? lastEye;
    double lastEyeTime;
    Vector2 velocity;
    readonly List<(ZoneCoordinate Zone, Tier Tier, float Urgency)> wanted = [];
    readonly List<ZoneState> dropping = [];
    readonly HashSet<ZoneCoordinate> candidates = [];
    public List<string> Messages { get; } = [];

    /// <summary>Requested work: layouts, grass, meshes, textures and impostors, including used textures deferred by memory pressure.</summary>
    public int Pending => runningWhole + runningFar + zonesWaiting + grassWaiting + decoding.Count + uploads.Count + lodRunning + lodWaiting + textures.AwaitedCount + textures.UnresidentUsedCount + zones.Values.Sum(z => z.GrassRunning) + ImpostorPending;

    /// <summary>
    /// <see cref="Pending"/> as a loading screen waits for it: the grass only within the range the default grass distance gives (<see cref="DefaultGrassRange"/>). A longer
    /// setting's farther pages (four times the grass at twice the distance, a minute and more of generation) grow in, nearest first, once the screen lifts.
    /// </summary>
    public int LoadingPending => Pending - grassWaiting - zones.Values.Sum(z => z.GrassRunning) + grassNear;

    /// <summary>What <see cref="Pending"/> is made of, for the loading log.</summary>
    public string PendingDetail => $"layouts {runningWhole + runningFar + zonesWaiting}, grass {grassWaiting + zones.Values.Sum(z => z.GrassRunning)}, meshes {decoding.Count} decoding {uploads.Count} upload steps, lods {lodRunning + lodWaiting}, textures {textures.AwaitedCount} loading {textures.UnresidentUsedCount} deferred, impostors {ImpostorPending}";

    /// <summary>GPU memory held by foliage meshes, textures and grass pages.</summary>
    public long ResidentBytes => residentMeshBytes + textures.ResidentBytes + GrassBytes() + impostorBytes;
    public string ResidentDescription =>
        $"{residentMeshBytes / 1048576.0:0} MB in {assetsByMesh.Values.Count(a => a.Resident)} meshes ({meshUnloads} unloaded, {meshReloads} reloaded), {textures.Describe()}, {GrassBytes() / 1048576.0:0} MB of grass pages, {ImpostorDescription}";
    /// <summary>The GPU cull's and the grass kernels' per-view memory (<see cref="FrameScratch"/>): held now, the need per frame (mean, peak, largest demand), what was refused. For the benchmark and the statistics.</summary>
    public string ScratchDescription
    {
        get
        {
            static string One(string name, FrameScratch s) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{name} {s.AllocatedBytes / 1048576.0:0.0} MB held, need per frame mean {s.MeanNeed / 1048576.0:0.0} / peak {s.PeakNeed / 1048576.0:0.0} MB (demand peak {s.PeakDemand / 1048576.0:0.0}), cap {s.Cap / 1048576.0:0} MB per slot, {s.Overflows} refused in {s.OverflowFrames} frames, {s.Rebuilds} rebuilds ({s.KeptOnRefusal} kept for a refusal)");
            return (gpuCull is { } g ? One("cull", g.Scratch) + $"; arena {g.ArenaUsed / 1048576.0:0.0} of {g.ArenaBytes / 1048576.0:0} MB at {FoliageInstanceRecord.GpuSize} B a record ({g.PackMismatches} not lossless)" : "no GPU cull") + "; " + (grassStore.Scratch is { } grassScratch ? One("grass", grassScratch) : "grass store (CPU)");
        }
    }

    public void ResetDrawTally() => gpuCull?.ResetTally();

    /// <summary>The indirect draws per frame by instance count (0, 1-2, 3-15, 16+) for the colour views, the shadow cascades (all cascades summed) and the reflection, from the tally.</summary>
    public IEnumerable<(string Name, double Value)>? DrawHistogram()
    {
        if (gpuCull is not { TallyFrames: > 0 } g) return null;
        double n = g.TallyFrames;
        string[] kinds = ["colour", "shadow", "reflection"], buckets = ["0", "1-2", "3-15", "16+"];
        return Enumerable.Range(0, 3).SelectMany(k => Enumerable.Range(0, 4).Select(b => ($"hist {kinds[k]} draws {buckets[b]} inst", g.TallyHistogram[k, b] / n)));
    }

    /// <summary>The main colour views' foliage triangles per frame since <see cref="ResetDrawTally"/> (meshes, TERRAIN-mode rocks, impostor quads as two),
    /// from the GPU cull's indirect arguments (<see cref="FoliageGpuCull.CountTriangles"/>); null before a tallied frame has been read back.</summary>
    public double? ColourTrianglesPerFrame => gpuCull is { TallyFrames: > 0 } g ? (g.Tally[0, 0] + g.Tally[0, 2] + 2.0 * g.Tally[0, 4]) / g.TallyFrames : null;

    public string Describe() =>
        $"{zones.Values.Count(z => z.Ready)} zones laid out ({zones.Values.Where(z => z.Ready).Sum(z => z.Instances):N0} meshes, " +
        $"{zones.Values.Sum(z => z.Pages.Count):N0} grass pages), {assetsByMesh.Count} foliage meshes ({assetsByMesh.Values.Count(a => a.Resident)} resident), catalog of {catalog.Layers.Count} layers" + (Lod && lodBuilt + lodCacheHits > 0 ? $"; generated levels: {LodSummary}" : "");

    // ------------------------------------------------------------------ streaming

    /// <summary>What a zone's layout holds (docs/render-foliage.md). <see cref="Far"/>: the FAR layers only (no grass coverage, 9/10 of the cost).
    /// <see cref="Meshes"/>: every mesh layer, and, with the Meitou <c>range</c> switch, only the large meshes (the zone is farther than the medium
    /// and small ranges and the grass, so nothing else can be drawn there). <see cref="Whole"/>: every layer and the grass.</summary>
    enum Tier { Far, Meshes, Whole }

    sealed class ZoneState
    {
        public required ZoneCoordinate Zone;
        public float X0, Z0;
        public Task<GroupedFoliageZone>? Job;
        /// <summary>Laid out (at least the far layers); <see cref="Complete"/>: every layer, grass included, nothing left out. <see cref="MeshLayers"/>: every
        /// mesh layer was laid out (<see cref="Complete"/> implies it). <see cref="Filtered"/>: the groups that cannot be drawn at this zone's distance
        /// (a mesh that is not large) were left out. <see cref="JobTier"/>: what the job in flight lays out.</summary>
        public bool Ready, Complete, MeshLayers, Filtered, NeedsPrune;
        public Tier JobTier;
        public List<Group> Groups = [];
        public FoliageGround? Ground;
        public List<FoliageGrassPatch> Patches = [];
        /// <summary>The sprite and colour map of each of <see cref="Patches"/> (looked up once when the patches are set), for the GPU grass's patch table.</summary>
        public (WorldTexture? Sprite, WorldTexture? ColourMap)[] PatchTextures = [];
        /// <summary>This zone's number in the GPU grass tables (<see cref="FoliageGrassGpu"/>'s slots name it), -1 before its first page is placed.</summary>
        public int GrassZone = -1;
        public readonly Dictionary<int, GrassPage> Pages = [];
        public int GrassRunning;
        public int Instances;
        /// <summary>The cull box's margin (<see cref="ZoneMargin"/>) and what it was computed for.</summary>
        public float Margin;
        public int MarginStamp = -1;
        public List<Group>? MarginGroups;
        /// <summary>Lowest and highest placed instance (for culling the zone as a box).</summary>
        public float MinY, MaxY;
    }


    /// <summary>The instances of one mesh in one zone, with the range of the layer that placed them.</summary>
    sealed class Group
    {
        public required MeshAsset Asset;
        /// <summary>The layer's range at setting 1 and its transition: the drawn range follows the current <see cref="RangeSetting"/> (<see cref="RangeOf"/>).</summary>
        public required float BaseRange, Transition;
        /// <summary>Placed by a FAR layer (<see cref="FoliageLayout.IsFarLayer"/>).</summary>
        public required bool Far;
        /// <summary>The largest instance scale: how far a mesh's bounds can reach from its instance's position (the zone's cull box).</summary>
        public required float MaxScale;
        /// <summary>The zone the group is in (its cull box: the GPU path's per-cascade batch test, <see cref="ShowBatches"/>).</summary>
        public required ZoneState Zone;
        /// <summary>The instances (<see cref="FoliageCull"/>'s records), their bounding spheres filled once the mesh's bounds are known (<see cref="SpheresReady"/>).</summary>
        public required FoliageInstanceRecord[] Instances;
        public bool SpheresReady;
        /// <summary>Where <see cref="Instances"/> are in the GPU cull's arena, and the arena generation they were placed in (0: never).</summary>
        public ArenaRange Arena;
        public int ArenaGeneration;
        /// <summary>A box around every instance's bounding sphere (with a unit of slack for rounding), set with the spheres: the GPU path
        /// draws a batch in a view only when one of its groups' boxes meets the frustum (<see cref="ShowBatches"/>).</summary>
        public Vector3 BoundMin, BoundMax;
        public bool BoundsReady;
        /// <summary>The smallest and largest bounding radius among the instances (set with <see cref="BoundMin"/>): which generated levels a view can pick in the group.</summary>
        public float MinRadius, MaxRadius;
        /// <summary>A TERRAIN-mode rock group on the GPU cull: <see cref="Instances"/>' <c>Ground.W</c> holds <see cref="FoliageCull.RockBits"/>,
        /// made with the terrain's biome source <see cref="RockBiomes"/> (<see cref="TerrainRenderer.FeatureBiomes"/>); whether it has plain and
        /// mirroring placements, and whether its first one mirrors (the order its two batches are numbered in).</summary>
        public bool RockReady, RockPlain, RockMirrored, RockFirstMirrored;
        /// <summary>With rock impostors on (<see cref="FoliageRenderer.RockImpostorsActive"/>): the records are sorted by biome row (stable) and <see cref="RockSegments"/> are
        /// the runs of one row (<c>Row</c> -1: no biome), in order; the rows are what the impostor atlases are made for (docs/impostors.md section 13).</summary>
        public bool RockSorted;
        public (int Row, int Start, int Length)[] RockSegments = [];
        public object? RockBiomes;

        public void ComputeBounds()
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            (MinRadius, MaxRadius) = (float.MaxValue, 0);
            foreach (ref readonly var r in Instances.AsSpan())
            {
                (MinRadius, MaxRadius) = (Math.Min(MinRadius, r.Sphere.W), Math.Max(MaxRadius, r.Sphere.W));
                var c = new Vector3(r.Sphere.X, r.Sphere.Y, r.Sphere.Z);
                min = Vector3.Min(min, c - new Vector3(r.Sphere.W + 1));
                max = Vector3.Max(max, c + new Vector3(r.Sphere.W + 1));
            }
            (BoundMin, BoundMax, BoundsReady) = (min, max, true);
        }
    }

    sealed class GrassPage
    {
        public Task<(float[][] Blades, int[][] Prefixes)>? Job;
        public GrassBuffer[]? Buffers;
        public bool Dropped;
        /// <summary>The generated blades while the arena or the slot table had no room (<see cref="FoliageGrassGpu.Misses"/>): placed again by <see cref="UpdateGrass"/>.</summary>
        public float[][]? PendingBlades;
        public int[][]? PendingPrefixes;
    }

    sealed class GrassBuffer
    {
        /// <summary>Where the blades are in the grass arena (<see cref="FoliageGrassGpu"/>): the draw's first instance is <see cref="FirstBlade"/>; <see cref="Slot"/> is its
        /// place in the GPU cull's slot table (-1 for a patch without blades).</summary>
        public ArenaRange Range;
        public uint FirstBlade;
        public int Slot = -1;
        /// <summary>The store <see cref="Range"/> and <see cref="Slot"/> are in: a store replaced by a larger one (<see cref="GrowGrassStore"/>) is not given them back.</summary>
        public FoliageGrassGpu? Store;
        public int Count;
        /// <summary>The patch the cached textures and count belong to (<see cref="Patch"/>): its sprite and colour map looked up once, and the
        /// blades shown at <see cref="ShownDensity"/>.</summary>
        public FoliageGrassPatch? Patch;
        public WorldTexture? Sprite, ColourMap;
        public float ShownDensity = float.NaN;
        public int Shown;
        /// <summary>Blades after each 1/64 of the candidates (<see cref="FoliageGrassField.BladesWithPrefixes"/>): the density setting draws a prefix.</summary>
        public int[] Prefixes = [];
    }

    /// <summary>Follows the eye: lays out zones, decodes meshes, makes grass pages and uploads, within a frame budget.</summary>
    public void Update(Vector3 eye) => Update(eye, settling: false);

    /// <summary>
    /// The arrival eye of a character flight (docs/render-objects.md "Arrival prefetch"), or null: zones within the near reach of it are laid out
    /// and kept as from the eye, and ahead of everything else, so the landing view's foliage loads during the flight (the grass pages wait for the eye).
    /// </summary>
    public Vector3? Ahead { get; set; }

    void Update(Vector3 eye, bool settling)
    {
        updates++;
        var watch = Stopwatch.StartNew();
        // Uploads get 2 ms a frame (texture decodes are never waited for) except while settling for a screenshot.
        double budget = settling ? 1e9 : Covered ? CoveredBudgetMs : 2.0;
        int layouts = Covered ? Math.Max(workers, 4 * BackgroundWork.Threads) : workers;
        // Two tiers (docs/render-foliage.md): within the near reach (the MEDIUM layers and the grass) a zone is laid out whole; beyond it, out
        // to the far reach, only its FAR layers, which skips the grass coverage, nine tenths of a zone's layout. A far zone is laid out whole once
        // it comes within the near reach (its far instances are the same either way, FoliageLayout.Place).
        // With the Meitou range switch a third tier sits between them (Tier.Meshes): beyond the whole reach (the longest of the small, medium and grass
        // ranges) and within the near reach only the large meshes can be drawn, so such a zone keeps those and nothing else.
        float nearReach = NearReach, farReach = FarReach, wholeReach = WholeReach;
        foreach (var state in zones.Values)
        {
            if (state.Job is not { IsCompleted: true } job) continue;
            if (state.JobTier != Tier.Far) runningWhole--; else runningFar--;
            state.Job = null;
            try { Accept(state, job.Result, state.JobTier); }
            catch (AggregateException e) { Messages.Add($"foliage zone {state.Zone}: {e.InnerException?.Message ?? e.Message}"); state.Ready = state.Complete = state.MeshLayers = true; }
        }
        dropping.Clear();
        foreach (var state in zones.Values)
            if (state.Job is null && ZoneDistance(state.Zone, eye) > farReach + WorldLayout.ZoneSize && !(Ahead is { } kept && ZoneDistance(state.Zone, kept) <= nearReach + WorldLayout.ZoneSize))
                dropping.Add(state);
        foreach (var state in dropping) Drop(state);

        // Look ahead along the motion: the work is ordered by how soon a zone becomes needed from where the eye will be in a second
        // (LookaheadSeconds), and whole layouts are started for zones that will be within the near reach by then. A jump (a camera code,
        // a teleport) is not a motion: the step is capped.
        double now = clock.Elapsed.TotalSeconds;
        if (lastEye is { } previous && now > lastEyeTime)
        {
            var step = new Vector2(eye.X - previous.X, eye.Z - previous.Z);
            float dt = (float)(now - lastEyeTime);
            var v = step.Length() > MaxLookahead ? Vector2.Zero : step / dt;
            float k = Math.Clamp(dt / 0.25f, 0, 1);   // smoothed over about a quarter second
            velocity = Vector2.Lerp(velocity, v, k);
        }
        (lastEye, lastEyeTime) = (eye, now);
        var ahead = velocity * LookaheadSeconds;
        if (ahead.Length() > MaxLookahead) ahead = Vector2.Normalize(ahead) * MaxLookahead;
        var predicted = settling ? eye : eye + new Vector3(ahead.X, 0, ahead.Y);

        wanted.Clear();
        var centre = WorldLayout.ZoneOf(eye.X, eye.Z);
        int reach = (int)Math.Ceiling((farReach + ahead.Length()) / WorldLayout.ZoneSize) + 1;
        NearestIncompleteZone = NearestUnlaidZone = float.PositiveInfinity;
        var arrival = settling ? null : Ahead;
        float arrivalReach = nearReach + PrefetchMargin;
        candidates.Clear();
        for (int dz = -reach; dz <= reach; dz++)
            for (int dx = -reach; dx <= reach; dx++)
                candidates.Add(new ZoneCoordinate(centre.X + dx, centre.Y + dz));
        if (arrival is { } at)
        {
            var landing = WorldLayout.ZoneOf(at.X, at.Z);
            int around = (int)Math.Ceiling(arrivalReach / WorldLayout.ZoneSize) + 1;
            for (int dz = -around; dz <= around; dz++)
                for (int dx = -around; dx <= around; dx++)
                    candidates.Add(new ZoneCoordinate(landing.X + dx, landing.Y + dz));
        }
        foreach (var z in candidates)
        {
            if (!z.IsInsideGrid) continue;
            float d = ZoneDistance(z, eye), dp = ZoneDistance(z, predicted), da = arrival is { } a ? ZoneDistance(z, a) : float.PositiveInfinity;
            bool landing = da <= arrivalReach;
            float soon = Math.Min(Math.Min(d, dp), da);
            if (d > farReach && !landing) continue;   // the prediction orders and adds whole layouts, it never adds zones
            bool meitou = MeitouRange;
            var need = soon <= wholeReach + PrefetchMargin ? Tier.Whole : meitou && soon <= nearReach + PrefetchMargin ? Tier.Meshes : Tier.Far;
            zones.TryGetValue(z, out var state);
            if (state is not null && Satisfies(state, need, meitou)) continue;   // has what it needs
            if (d <= wholeReach) NearestIncompleteZone = Math.Min(NearestIncompleteZone, d);
            if (state is not { Ready: true }) NearestUnlaidZone = Math.Min(NearestUnlaidZone, d);
            if (state?.Job is not null) continue;   // on its way
            // Whole layouts first (what is missing there is in view near the eye), then the large-only ones, then the far ones, each nearest
            // first (from the eye or where it will be, whichever is nearer).
            // During a flight the arrival's zones go first, then the flight path's (the eye's), each in the same order.
            float tierBase = need == Tier.Whole ? 0 : need == Tier.Meshes ? nearReach : farReach;
            wanted.Add((z, need, arrival is null ? tierBase + soon : landing ? tierBase + da : 2 * farReach + tierBase + Math.Min(d, dp)));
        }
        wanted.Sort((a, b) => a.Urgency.CompareTo(b.Urgency));
        int started = 0;
        foreach (var (zone, tier, _) in wanted)
        {
            if (guard is { Streaming: false }) break;   // memory pressure: no new layout is started
            bool whole = tier != Tier.Far;   // a Meshes layout is a whole one whose far-away groups are left out (Accept)
            if (whole ? runningWhole >= layouts : runningFar >= layouts) continue;
            if (!zones.TryGetValue(zone, out var state))
            {
                var (x0, z0) = WorldLayout.ZoneOrigin(zone);
                zones[zone] = state = new ZoneState { Zone = zone, X0 = (float)x0, Z0 = (float)z0 };
            }
            if (whole) runningWhole++; else runningFar++;
            state.JobTier = tier;
            started++;
            bool farOnly = !whole;
            Func<GroupedFoliageZone> job = () =>
            {
                if (!worlds.TryTake(out var world))
                {
                    world = new FoliageWorld(install, db, levels, catalog, cache: LayoutCache);
                    lock (allWorlds) allWorlds.Add(world);
                }
                try
                {
                    // Grouped on the worker (thousands of instances cost 15 to 20 ms on the render thread), straight from the cache file's records when it has the zone.
                    return world.LoadGrouped(zone, farOnly);
                }
                finally { worlds.Add(world); }
            };
            // Whole layouts go ahead of the grass pages and texture decodes queued meanwhile (a dozen grass pages could hold every worker).
            state.Job = whole ? BackgroundWork.RunUrgent(job) : BackgroundWork.Run(job);
        }
        zonesWaiting = wanted.Count - started;   // needed but not started yet (the workers are busy)
        NearestMissingMesh = float.PositiveInfinity;

        UpdateGrass(eye);
        PumpMeshes();
        PumpLods();
        Prune();
        TrimResident(eye, force: settling);
        textures.Pump(wait: settling, max: settling ? 64 : Covered ? 8 : 1);
        // The budget counts from here: the work above can take it all on a slower machine, and the uploads would then never run.
        int ranSteps = 0;
        double uploadStart = watch.Elapsed.TotalMilliseconds;
        while (uploads.Count > 0 && (ranSteps == 0 || watch.Elapsed.TotalMilliseconds - uploadStart < budget))
        {
            // The first step always runs; a later one waits for the next frame when steps of its kind have cost more than what is left plus StepCosts.Slack.
            if (ranSteps > 0 && !stepCosts.Fits(uploads.Peek().Method, watch.Elapsed.TotalMilliseconds - uploadStart, budget)) break;
            var step = uploads.Dequeue();
            var one = Stopwatch.StartNew();
            step();
            double stepMs = one.Elapsed.TotalMilliseconds;
            stepCosts.Learn(step.Method, stepMs);
            ranSteps++;
            if (stepMs > 3) SpikeLog.Note($"slow foliage step {step.Method.Name}", ms: stepMs);
        }
        lock (textures.Messages)
        {
            foreach (var m in Messages.Concat(textures.Messages).Distinct().Take(20)) Console.WriteLine($"warning   {m}");
            Messages.Clear();
            textures.Messages.Clear();
        }
        UpdateImpostors(eye, settling);
        PollTimers(wait: false);
        if (!settling && watch.Elapsed.TotalMilliseconds > 8) SpikeLog.Note("slow foliage update", ms: watch.Elapsed.TotalMilliseconds);
        LastUpdateMs = watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>The last <see cref="Settle"/> in numbers: when each kind of work was last still going.</summary>
    public string SettleTimeline { get; private set; } = "";

    /// <summary>Waits until everything wanted around <paramref name="eye"/> is laid out, decoded, uploaded and textured (offscreen rendering).</summary>
    public void Settle(Vector3 eye, int timeoutMs = 300000)
    {
        var watch = Stopwatch.StartNew();
        int rounds = 0;
        double? pressureSince = null;
        var busyUntil = new double[6];
        void Timeline() => SettleTimeline = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"busy until (ms): zone layouts {busyUntil[0]:0}, grass pages {busyUntil[1]:0}, mesh decode and upload {busyUntil[2]:0}, textures {busyUntil[3]:0}, impostors and generated levels {busyUntil[4]:0}");
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            guard?.Tick();
            Update(eye, settling: true);
            {
                double t = watch.Elapsed.TotalMilliseconds;
                if (runningWhole + runningFar + zonesWaiting > 0) busyUntil[0] = t;
                if (grassWaiting > 0 || zones.Values.Sum(z => z.GrassRunning) > 0) busyUntil[1] = t;
                if (decoding.Count + uploads.Count > 0) busyUntil[2] = t;
                if (textures.PendingCount > 0) busyUntil[3] = t;
                if (ImpostorPending + lodRunning + lodWaiting > 0) busyUntil[4] = t;
            }
            if (++rounds > 2 && Pending == 0) { Timeline(); return; }
            // Under memory pressure the guard holds the loading back: what is missing will not come until it ends, so do not wait for it.
            pressureSince = guard is { Pressure: true } ? pressureSince ?? watch.Elapsed.TotalSeconds : null;
            if (pressureSince is { } since && watch.Elapsed.TotalSeconds - since > 5) { Console.WriteLine("warning   foliage streaming stopped waiting: memory pressure (VramGuard)"); return; }
            // An impostor bake reads its rows back after the frame they were recorded into, and atlas uploads are spread over frames: end it.
            // A live host owns its open frame (Metal cannot end and reopen it here): leave the remaining bakes to its later frames.
            if (impostorBake is { NeedsFrame: true } || impostorUploadsWaiting)
            {
                if (Gpu.Frame.Open) return;
                Gpu.Finish();
            }
            else Thread.Sleep(2);
        }
        Console.WriteLine($"warning   foliage streaming did not finish in {timeoutMs / 1000} s");
    }

    static float ZoneDistance(ZoneCoordinate zone, Vector3 eye)
    {
        var (x0, z0) = WorldLayout.ZoneOrigin(zone);
        return BoxDistance((float)x0, (float)z0, WorldLayout.ZoneSize, eye);
    }

    static float BoxDistance(float x0, float z0, float size, Vector3 eye)
    {
        float dx = Math.Max(Math.Max(x0 - eye.X, eye.X - (x0 + size)), 0);
        float dz = Math.Max(Math.Max(z0 - eye.Z, eye.Z - (z0 + size)), 0);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>A finished layout: its groups replace the zone's (a whole layout after a far one brings the same far instances back with the rest).</summary>
    void Accept(ZoneState state, GroupedFoliageZone prepared, Tier tier)
    {
        var (zone, ground) = (prepared.Zone, prepared.Ground);
        // With the Meitou range switch a zone beyond the whole reach can only show large meshes (the small and medium ranges and the grass end
        // before it): the other groups would be instances, meshes and textures held for nothing. A group whose mesh is not decoded yet has no
        // size class; it is kept and the prune pass (TrimResident) drops it when the size is known.
        bool filter = MeitouRange && tier != Tier.Whole;
        var groups = new List<Group>(prepared.Groups.Count);
        bool unknown = false;
        foreach (var g in prepared.Groups)
        {
            var asset = AssetFor(g.Mesh);
            ApplyShift(g.Mesh, g.Instances);
            if (filter && asset.HasBounds && asset.SizeClass != FoliageSizeClass.Large) continue;
            if (filter && !asset.HasBounds) unknown = true;
            groups.Add(new Group
            {
                Asset = asset,
                BaseRange = g.Layer.Range,
                Transition = g.Layer.Transition,
                Far = FoliageLayout.IsFarLayer(g.Layer),
                MaxScale = g.MaxScale,
                Instances = g.Instances,
                Zone = state,
            });
        }
        FreeArena(state.Groups);
        state.Groups = groups;
        state.Instances = prepared.InstanceCount;
        state.MinY = prepared.MinY;
        state.MaxY = prepared.MaxY;
        state.Ground = ground;
        state.Patches = filter ? [] : zone.Grass.Where(p => p.Grass.Sprite is not null && p.Density.Any(d => d != 0)).ToList();
        state.PatchTextures = new (WorldTexture?, WorldTexture?)[state.Patches.Count];
        for (int i = 0; i < state.Patches.Count; i++)
        {
            var p = state.Patches[i];
            state.PatchTextures[i] = (textures.Get(p.Grass.Sprite, false), textures.Get(p.Grass.ColourMap, false));   // start decoding now, not at the first draw
        }
        state.Ready = true;
        state.Complete = zone.Complete && !filter;
        state.MeshLayers = zone.Complete || tier != Tier.Far;
        state.Filtered = filter;
        state.NeedsPrune = unknown;
    }

    /// <summary>Whether a zone as laid out already holds what <paramref name="need"/> asks for (nothing to load again).</summary>
    static bool Satisfies(ZoneState s, Tier need, bool meitou) => need switch
    {
        Tier.Whole => s.Complete,
        Tier.Meshes => s.Complete || s.MeshLayers,
        _ => s.Ready && (!s.Filtered || meitou),
    };

    /// <summary>Zones within this ground distance of the eye are laid out whole: with <see cref="MeitouRange"/> the longer of the small and
    /// medium ranges and the grass range (beyond it only large meshes can be drawn, see <see cref="Tier.Meshes"/>), else <see cref="NearReach"/>.</summary>
    public float WholeReach => MeitouRange ? Math.Max(Math.Max(ClassRange(FoliageSizeClass.Small), ClassRange(FoliageSizeClass.Medium)), GrassMaxRange * GrassRangeSetting * Scale) : NearReach;

    /// <summary>Drops the groups that cannot be drawn in a filtered zone once their mesh sizes are known (they were kept while the mesh was not decoded).</summary>
    void Prune()
    {
        foreach (var state in zones.Values)
        {
            if (!state.NeedsPrune || state.Job is not null) continue;
            bool unknown = false;
            foreach (var g in state.Groups) if (!g.Asset.HasBounds && !g.Asset.Failed) { unknown = true; break; }
            if (unknown) continue;
            var keep = new List<Group>(state.Groups.Count);
            var drop = new List<Group>();
            foreach (var g in state.Groups) (g.Asset.SizeClass == FoliageSizeClass.Large || !g.Asset.HasBounds ? keep : drop).Add(g);
            FreeArena(drop);
            if (drop.Count > 0) { state.Groups = keep; state.MarginStamp = -1; }
            state.NeedsPrune = false;
        }
    }

    /// <summary>The range a group is drawn to at the current <see cref="RangeSetting"/>, and its fade band: the game's transition is 10 units
    /// (100 for wind layers), too short to see, so a tenth of the range instead.
    /// With <see cref="MeitouRange"/> the range is the mesh's size class's (the longest class range until the mesh is decoded and its size known),
    /// or for a large mesh of a FAR layer the longer of that and the layer's; the band is the same rule.</summary>
    (float Range, float Band) RangeOf(Group g)
    {
        float range = g.BaseRange * RangeSetting * Scale;
        if (MeitouRange)
        {
            var a = g.Asset;
            float size = a.HasBounds ? ClassRange(a.SizeClass) : LongestClassRange;
            range = g.Far && (!a.HasBounds || a.SizeClass == FoliageSizeClass.Large) ? Math.Max(range, size) : size;
        }
        return (range, Math.Max(g.Transition, range * 0.1f));
    }

    /// <summary>The groups' instances leave the GPU cull's arena (after the frames in flight).</summary>
    void FreeArena(List<Group> groups)
    {
        if (gpuCull is null) return;
        foreach (var g in groups)
        {
            gpuCull?.Free(g.Arena, g.ArenaGeneration);
            (g.Arena, g.ArenaGeneration) = (default, 0);
        }
    }

    void Drop(ZoneState state)
    {
        FreeArena(state.Groups);
        foreach (var page in state.Pages.Values) FreePage(page);
        state.Pages.Clear();
        ReleaseGrassZone(state);
        zones.Remove(state.Zone);
    }

    void FreePage(GrassPage page)
    {
        page.Dropped = true;   // an upload still queued for it frees what it made instead of attaching it
        page.PendingBlades = null;
        page.PendingPrefixes = null;
        if (page.Buffers is null) return;
        ReleaseBuffers(page.Buffers);
        page.Buffers = null;
    }

    /// <summary>A page's blade ranges and slots back to the grass store (after the frames in flight).</summary>
    void ReleaseBuffers(GrassBuffer[] buffers)
    {
        if (grassDisposing) return;
        foreach (var b in buffers)
        {
            if (b.Store != grassStore) continue;
            if (b.Slot >= 0) grassStore.FreeSlot(b.Slot);
            if (!b.Range.IsEmpty) grassStore.FreeBlades(b.Range);
            b.Slot = -1;
            b.Range = default;
        }
    }

    float GrassRange(FoliageGrassPatch patch) => patch.Layer.Range * GrassRangeSetting * Scale;

    /// <summary>The highest "Grass density x" the pages are generated for (the settings slider's top): lower settings draw a prefix of the blades.</summary>
    public const float MaxGrassDensity = 2f;
    /// <summary>Grass pages generating at once, over all zones; under the loading screen (<see cref="Covered"/>) enough to keep every streaming thread busy between two
    /// frames (a page takes a millisecond or two, and finished ones are only collected once a frame).</summary>
    const int MaxGrassJobs = 12;
    static readonly int CoveredGrassJobs = Math.Max(MaxGrassJobs, 4 * BackgroundWork.Threads);
    readonly List<(float Distance, ZoneState State, int Key)> grassWanted = [];
    readonly (float Distance, ZoneState State, int Key)[] grassPicks = new (float, ZoneState, int)[CoveredGrassJobs];
    int zonesWaiting, grassWaiting, grassNear;

    void UpdateGrass(Vector3 eye)
    {
        GrowGrassStore();
        grassWanted.Clear();
        grassNear = 0;
        grassTriedFull = false;
        NearestMissingGrass = float.PositiveInfinity;
        int inFlight = 0;
        foreach (var state in zones.Values) inFlight += state.GrassRunning;
        foreach (var state in zones.Values)
        {
            if (!state.Ready || state.Ground is null || state.Patches.Count == 0) continue;
            float range = state.Patches.Max(GrassRange), near = range * Math.Min(1, DefaultGrassRange / GrassRangeSetting);
            if (state.Pages.Count == 0 && ZoneDistance(state.Zone, eye) > range + PageSize) continue;
            for (int pz = 0; pz < PagesPerZone; pz++)
                for (int px = 0; px < PagesPerZone; px++)
                {
                    int key = pz * PagesPerZone + px;
                    float x0 = state.X0 + px * PageSize, z0 = state.Z0 + pz * PageSize;
                    float d = BoxDistance(x0, z0, PageSize, eye);
                    state.Pages.TryGetValue(key, out var page);
                    if (page?.Buffers is null && d < range * 0.8f) NearestMissingGrass = Math.Min(NearestMissingGrass, d);   // not sinking yet: would be seen
                    if (page is null)
                    {
                        if (d < range + PageSize * 0.5f)
                        {
                            grassWanted.Add((d, state, key));
                            if (d < near) grassNear++;
                        }
                        continue;
                    }
                    if (page.Job is null && d > range + PageSize * 2)
                    {
                        FreePage(page);
                        state.Pages.Remove(key);
                    }
                    else if (page.Job is { IsCompleted: false } && d < near) grassNear++;
                    else if (page.Job is { IsCompleted: true } job)
                    {
                        state.GrassRunning--;
                        inFlight--;   // its slot is free for this frame's picks
                        page.Job = null;
                        var result = job.Result;
                        TryQueuePage(state, key, page, result.Blades, result.Prefixes);
                    }
                    else if (page.PendingBlades is not null) TryQueuePage(state, key, page, page.PendingBlades, page.PendingPrefixes!);   // no room last time
                }
        }
        // Missing pages nearest first, a bounded number at a time. Pages hold the blades of the highest density setting,
        // so changing the setting never regenerates them (Draw shows a prefix). Only the nearest free jobs' worth is picked: sorting every
        // missing page (tens of thousands at a long grass range) took milliseconds a frame.
        grassWaiting = grassWanted.Count;
        int free = guard is { Streaming: false } ? 0 : Math.Max(0, (Covered ? CoveredGrassJobs : MaxGrassJobs) - inFlight), picked = 0;
        foreach (var wanted in grassWanted)
        {
            if (picked == free && (free == 0 || wanted.Distance >= grassPicks[picked - 1].Distance)) continue;
            int at = picked < free ? picked++ : picked - 1;
            for (; at > 0 && grassPicks[at - 1].Distance > wanted.Distance; at--) grassPicks[at] = grassPicks[at - 1];
            grassPicks[at] = wanted;
        }
        for (int i = 0; i < picked; i++)
        {
            var (_, state, key) = grassPicks[i];
            grassPicks[i] = default;
            float x0 = state.X0 + key % PagesPerZone * PageSize, z0 = state.Z0 + key / PagesPerZone * PageSize;
            var page = state.Pages[key] = new GrassPage();
            var patches = state.Patches;
            var ground = state.Ground!;
            state.GrassRunning++;
            grassWaiting--;
            page.Job = BackgroundWork.Run(() =>
            {
                var blades = new float[patches.Count][];
                var prefixes = new int[patches.Count][];
                for (int i = 0; i < blades.Length; i++)
                    blades[i] = FoliageGrassField.BladesWithPrefixes(patches[i], ground, x0, z0, PageSize, out prefixes[i], MaxGrassDensity);
                return (blades, prefixes);
            });
        }
    }

    const int SlabBytes = 512 << 10;

    /// <summary>GPU bytes of the grass pages (blades and the instance data).</summary>
    long GrassBytes() => zones.Values.Sum(z => z.Pages.Values.Sum(p => p.Buffers?.Sum(b => (long)b.Count * FoliageGrassField.Stride * 4) ?? 0));

    // ------------------------------------------------------------------ meshes and materials

    sealed class MeshAsset
    {
        public required FoliageMesh Mesh;
        /// <summary>Its meshes are welded (<see cref="MeshWeld"/>, as it was when the decode began): its LOD levels must be made from the same vertices.</summary>
        public bool Weld;
        public Task<(Model? Main, Model? Leaves)>? Job;
        public GpuMesh? Main, Leaves;
        public bool Resident, Failed, Uploading;
        public long LastUsed, Bytes;
        public FoliageMaterial? MainMaterial, LeavesMaterial;
        public bool Terrain => Mesh.MaterialType == 2;
        public Vector3 Centre;
        public float Radius = 1;
        /// <summary>The bounding box's half extents (main and leaves meshes), for the tight billboard quad (<see cref="Impostors.ImpostorQuad"/>).</summary>
        public Vector3 HalfExtent;
        /// <summary>Radius × largest scale (<see cref="FoliageSizes.Size"/>), known with <see cref="HasBounds"/>: the shadow cascades skip meshes too small for their texels.</summary>
        public float Size;
        /// <summary><see cref="Centre"/>, <see cref="Radius"/> and <see cref="SizeClass"/> are known (the mesh was decoded once; they never change).</summary>
        public bool HasBounds;
        public FoliageSizeClass SizeClass;
        /// <summary>Billboarded from <see cref="LargeImpostorDistance"/> rather than <see cref="ImpostorDistance"/> (<see cref="FoliageSizes.LargeBillboard"/>; trees are not).</summary>
        public bool LargeBillboard;
        /// <summary>The triangles of the mesh and its leaves mesh (known with <see cref="HasBounds"/>): the small impostor class is for meshes with many.</summary>
        public int Triangles;
        /// <summary>The mean triangle edge of the mesh and its leaves mesh in mesh units: the square root of the mean triangle area (known with <see cref="HasBounds"/>; <c>screen-lod</c>, <see cref="FoliageScreenLod.TriangleExtent(IEnumerable{float})"/>).</summary>
        public float TriExtent;
        /// <summary>The <c>screen-lod</c> transition terms of this mesh (ground distance per unit of instance radius, and the atlas frame floor) and what they were worked out for.</summary>
        public float ScreenPerRadian, ScreenFloor;
        public (float, float, int, int) ScreenKey;
        /// <summary>The impostor class estimated from the mesh's own bounds, once (<see cref="FoliageRenderer.EstimateClass"/>).</summary>
        public ImpostorClass? EstimatedClass;
        public bool ClassEstimated;
        /// <summary>The GPU work list that last numbered this mesh's batch (<see cref="BuildGpuWork"/>), and the batch's number in it.</summary>
        public int WorkStamp, WorkIndex;
        /// <summary>For a TERRAIN-mode mesh: the work list that last numbered its rock batches, and their numbers among the rock batches
        /// (plain placements, mirroring placements; -1: none in the work list).</summary>
        public int RockStamp, RockPlainIndex, RockMirroredIndex;
        /// <summary>Generated levels (<see cref="RockLod"/>, TERRAIN-mode meshes only): uploaded, the job making them (or reading the cache), whether it ran for the current residency, and the set the current GPU work list was built with.</summary>
        public RockLod? Lod, WorkLod;
        public bool LodDone;
        /// <summary>Its impostor (<see cref="FoliageRenderer.Impostors"/>): null until asked for.</summary>
        public ImpostorState? Impostor;
        /// <summary>
        /// A TERRAIN-mode rock's impostors are per biome (the terrain material is the biome's at each instance, docs/impostors.md section 13). The rock's own asset has
        /// <see cref="RockVariants"/>, one holder per biome row; a holder (<see cref="RockOf"/> the rock's asset, <see cref="RockRow"/> the row) carries only the
        /// <see cref="Impostor"/> state and a copy of the bounds, and is never in <c>assetsByMesh</c>.
        /// </summary>
        public MeshAsset? RockOf;
        public int RockRow = -1;
        public Dictionary<int, MeshAsset>? RockVariants;
    }

    sealed class GpuMesh
    {
        public List<GpuPart> Parts = [];
    }

    /// <summary>A mesh part on the GPU: its vertex and index buffers (native, "foliage meshes"), the vertex attributes at locations 0 to 4 as
    /// the GL vertex array had them, and for the TERRAIN-mode rocks the mesh as the terrain's mesh path takes it, made when first asked for
    /// (<see cref="PlainMesh"/>).</summary>
    sealed class GpuPart
    {
        public required DeviceBuffer Vertices, Indices;
        public LegacyProgram.Attribute?[] Attributes = [];
        public MeshBindings? Plain;
        /// <summary>What a native draw needs, per program (<see cref="NativeMesh"/>).</summary>
        public NativeMesh ColourNative, DepthNative, ColourSolidNative, PrepassNative, PrepassSolidNative;
        public int Count;
        /// <summary>Blades after each 1/64 of the candidates (<see cref="FoliageGrassField.BladesWithPrefixes"/>): the density setting draws a prefix.</summary>
        public int[] Prefixes = [];
        public bool HasTangents, HasColours;
        /// <summary>The cards-trimmed index list after the original <see cref="Count"/> indices in <see cref="Indices"/> (0: none; card trimming).</summary>
        public int TrimCount;
    }

    sealed record FoliageMaterial(WorldTexture? Diffuse, WorldTexture? Normal, WorldTexture? Diffuse2, WorldTexture? Normal2,
        float AlphaThreshold, bool DoubleSided, bool Triplanar, Vector2 Tile, bool Emissive, float Specular, uint Surface);

    MeshAsset AssetFor(FoliageMesh mesh)
    {
        if (assetsByMesh.TryGetValue(mesh, out var a)) return a;
        a = assetsByMesh[mesh] = new MeshAsset { Mesh = mesh };
        // MapFeatureMode: UV_MAPPED, TRIPLANAR, TERRAIN, DUAL_TEXTURE, FOLIAGE, DUAL_TRIPLANAR, EMISSIVE (the map-feature builder's rules,
        // docs/formats/runtime-materials.md); FOLIAGE cuts out on the normal map's alpha at "alpha threshold" / 255, double-sided.
        int mode = mesh.MaterialType;
        bool dual = mode is 3 or 5;
        a.MainMaterial = new FoliageMaterial(textures.Get(mesh.Texture, false, deferred: true), textures.Get(mesh.Normal, false, deferred: true),
            dual ? textures.Get(mesh.Texture2, false, deferred: true) : null, dual ? textures.Get(mesh.Normal2, false, deferred: true) : null,
            mode == 4 ? mesh.AlphaThreshold / 255f : 0, mode == 4, mode is 1 or 5, new Vector2(mesh.TileX, mesh.TileY), mode == 6, mesh.SpecularMult,
            (mode is 1 or 5 ? MeshSurface.TriplanarDust : 0u) | (mode == 4 ? MeshSurface.Foliage : 0u));
        // The leaves: their own texture pair, transparent and double-sided, cut out at "leaves alpha threshold" / 255.
        if (mesh.LeavesMesh is not null)
            a.LeavesMaterial = new FoliageMaterial(textures.Get(mesh.LeavesTexture, false, deferred: true), textures.Get(mesh.LeavesNormal, false, deferred: true), null, null,
                mesh.LeavesAlphaThreshold / 255f, true, false, Vector2.One, false, 0, MeshSurface.Foliage);
        string main = mesh.MeshPath, leaves = mesh.LeavesMesh ?? "";
        bool weld = a.Weld = MeshWeld.Enabled;
        a.Job = BackgroundWork.Run(() => (DecodeTrimmed(main, mesh, false, weld), leaves.Length > 0 ? DecodeTrimmed(leaves, mesh, true, weld) : null));
        decoding.Add(a);
        return a;
    }

    Model? Decode(string name, bool weld)
    {
        var path = assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        if (path is null) { lock (Messages) Messages.Add($"foliage mesh not found: {name}"); return null; }
        try
        {
            var mesh = OgreMeshReader.ReadFile(path);
            if (weld) OgreMeshWeld.Apply(mesh);
            return Model.Build(mesh, null);
        }
        catch (Exception e) when (e is OgreFormatException or EndOfStreamException or IOException or InvalidDataException)
        {
            lock (Messages) Messages.Add($"foliage mesh {name}: {e.Message}");
            return null;
        }
    }

    void PumpMeshes()
    {
        for (int i = 0; i < decoding.Count; i++)
        {
            var a = decoding[i];
            if (!a.Job!.IsCompleted) continue;
            decoding.RemoveAt(i--);
            (Model? main, Model? leaves) = (null, null);
            try { (main, leaves) = a.Job.Result; }
            catch (AggregateException e) { Messages.Add($"foliage mesh {a.Mesh.MeshPath}: {e.InnerException?.Message ?? e.Message}"); }
            a.Job = null;
            if (main is null) { a.Failed = true; continue; }
            var min = main.Min;
            var max = main.Max;
            if (leaves is not null) { min = Vector3.Min(min, leaves.Min); max = Vector3.Max(max, leaves.Max); }
            a.Centre = (min + max) / 2;
            a.HalfExtent = (max - min) / 2;
            a.Radius = Math.Max((max - min).Length() / 2, 1);
            a.Size = FoliageSizes.Size(a.Radius, a.Mesh);
            a.SizeClass = FoliageSizes.Classify(a.Size);
            a.LargeBillboard = FoliageSizes.LargeBillboard(a.Radius, a.Mesh);
            a.Triangles = main.Parts.Sum(p => p.Indices.Length / 3) + (leaves?.Parts.Sum(p => p.Indices.Length / 3) ?? 0);
            a.HasBounds = true;
            a.TriExtent = FoliageScreenLod.TriangleExtent(Triangles(main).Concat(leaves is null ? [] : Triangles(leaves)));
            QueueMesh(a, main, leaves);
        }
    }

    /// <summary>The areas of a model's triangles, in mesh units squared.</summary>
    static IEnumerable<float> Triangles(Model model)
    {
        foreach (var part in model.Parts)
        {
            var v = part.Vertices;
            var ix = part.Indices;
            for (int i = 0; i + 2 < ix.Length; i += 3)
                yield return 0.5f * Vector3.Cross(v[ix[i + 1]].Position - v[ix[i]].Position, v[ix[i + 2]].Position - v[ix[i]].Position).Length();
        }
    }

    /// <summary>A mesh's upload as steps of about 512 KB (per part: allocate and set up the vertex arrays, fill the buffers in slabs); the mesh becomes resident with the last step.</summary>
    void QueueMesh(MeshAsset a, Model main, Model? leaves)
    {
        a.Uploading = true;
        var gpuMain = new GpuMesh();
        var gpuLeaves = leaves is null ? null : new GpuMesh();
        var bytesTotal = new long[1];
        QueueModel(gpuMain, main, bytesTotal);
        if (leaves is not null) QueueModel(gpuLeaves!, leaves, bytesTotal);
        uploads.Enqueue(() =>
        {
            a.Main = gpuMain;
            a.Leaves = gpuLeaves;
            a.Bytes = bytesTotal[0];
            a.LastUsed = Environment.TickCount64;
            a.Resident = true;
            a.Uploading = false;
            residentStamp++;
            residentMeshBytes += a.Bytes;
        });
    }

    void QueueModel(GpuMesh mesh, Model model, long[] tally)
    {
        foreach (var part in model.Parts)
        {
            if (part.Indices.Length == 0) continue;
            var p = part;
            GpuPart? gp = null;
            // The trimmed list (card trimming) follows the original one in the same buffer: the draws pick a range (FoliageRenderer.Trim.cs).
            var allIndices = p.TrimmedIndices is null ? p.Indices : [.. p.Indices, .. p.TrimmedIndices];
            var packed = GpuVertex.PackObject(p.Vertices);
            int vertexBytes = packed.Length, indexBytes = allIndices.Length * sizeof(uint);
            tally[0] += vertexBytes + indexBytes;
            uploads.Enqueue(() =>
            {
                gp = new GpuPart
                {
                    Vertices = DeviceBuffer.Create(Gpu, (ulong)vertexBytes, BufferUse.Vertex | RayInput, MeshAllocationName),
                    Indices = DeviceBuffer.Create(Gpu, (ulong)indexBytes, BufferUse.Index | RayInput, MeshAllocationName),
                    Count = p.Indices.Length, TrimCount = p.TrimmedIndices?.Length ?? 0, HasTangents = p.HasTangents, HasColours = p.HasColours,
                };
                gp.Attributes = VertexAttributes(gp.Vertices);
                mesh.Parts.Add(gp);
            });
            for (int at = 0; at < vertexBytes; at += SlabBytes)
            {
                int start = at, length = Math.Min(SlabBytes, vertexBytes - at);
                uploads.Enqueue(() =>
                {
                    Gpu.EnsureFrame();
                {
                    using var batch = Gpu.Uploads.Begin();
                    batch.Write(gp!.Vertices, (ulong)start, packed.AsSpan(start, length));
                }
                });
            }
            for (int at = 0; at < indexBytes; at += SlabBytes)
            {
                int start = at, length = Math.Min(SlabBytes, indexBytes - at);
                uploads.Enqueue(() =>
                {
                    Gpu.EnsureFrame();
                {
                    using var batch = Gpu.Uploads.Begin();
                    batch.Write(gp!.Indices, (ulong)start, System.Runtime.InteropServices.MemoryMarshal.AsBytes(allIndices.AsSpan()).Slice(start, length));
                }
                });
            }
        }
    }

    long residentMeshBytes, lastTrim;
    /// <summary>Counts meshes becoming resident (the zones' cull margins are recomputed after it changes).</summary>
    int residentStamp;
    int meshUnloads, meshReloads;

    /// <summary>Meshes not needed (no loaded zone has a group of them within its range) for this long are unloaded.</summary>
    public double IdleSeconds { get; set; } = 60;

    /// <summary>
    /// Once a second: marks the meshes (and their textures) of every zone group within its range as in use, whatever the camera looks at, and
    /// unloads the GPU meshes unused for <see cref="IdleSeconds"/>. A mesh needed again is decoded and uploaded afresh (<see cref="Reload"/>).
    /// </summary>
    void TrimResident(Vector3 eye, bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now - lastTrim < 1000) return;
        lastTrim = now;
        foreach (var state in zones.Values)
        {
            if (!state.Ready) continue;
            float zoneDistance = ZoneDistance(state.Zone, eye);
            foreach (var g in state.Groups)
            {
                if (zoneDistance > RangeOf(g).Range + WorldLayout.ZoneSize * 0.5f) continue;
                var a = g.Asset;
                a.LastUsed = now;
                if (!a.Resident) Reload(a);   // in range but unloaded (or never loaded after an unload): decode it again, wherever the camera looks
                foreach (var m in (ReadOnlySpan<FoliageMaterial?>)[a.MainMaterial, a.LeavesMaterial])
                    if (m is not null && a.HasBounds) _ = (m.Diffuse?.Key, m.Normal?.Key, m.Diffuse2?.Key, m.Normal2?.Key);   // reading a key counts as use (WorldTexture.Key)
            }
        }
        foreach (var a in assetsByMesh.Values)
        {
            if (!a.Resident || (now - a.LastUsed) / 1000.0 <= (guard is { Pressure: true } ? Math.Min(IdleSeconds, GuardIdleSeconds) : IdleSeconds)) continue;
            foreach (var m in new[] { a.Main, a.Leaves })
                if (m is not null) DeleteMesh(m);
            a.Main = a.Leaves = null;
            DropLods(a);
            a.Resident = false;
            residentMeshBytes -= a.Bytes;
            a.Bytes = 0;
            meshUnloads++;
        }
    }

    void DeleteMesh(GpuMesh m)
    {
        RayGeneration++;   // the traced scene drops what it built from these buffers
        foreach (var gp in m.Parts)
        {
            gp.Vertices.Dispose();   // freed after the frames in flight
            gp.Indices.Dispose();
        }
    }

    /// <summary>Decodes an unloaded mesh again (from <see cref="Draw"/>, when something in range needs it).</summary>
    void Reload(MeshAsset a)
    {
        if (a.Resident || a.Failed || a.Job is not null || a.Uploading || guard is { Streaming: false }) return;
        string main = a.Mesh.MeshPath, leaves = a.Mesh.LeavesMesh ?? "";
        a.LastUsed = Environment.TickCount64;
        bool weld = a.Weld = MeshWeld.Enabled;
        a.Job = BackgroundWork.Run(() => (DecodeTrimmed(main, a.Mesh, false, weld), leaves.Length > 0 ? DecodeTrimmed(leaves, a.Mesh, true, weld) : null));
        decoding.Add(a);
        meshReloads++;
    }

    /// <summary>The attributes at locations 0 to 4 (position, normal, texture coordinates, tangent, colour) of the object layout (<see cref="GpuVertex"/>).</summary>
    internal static LegacyProgram.Attribute?[] VertexAttributes(DeviceBuffer vertices) => GpuVertex.ObjectAttributes(vertices);

    /// <summary>The part for the terrain's mesh path (the attributes at 0 to 4 and the index buffer); made on first use.</summary>
    static MeshBindings PlainMesh(GpuPart gp) => gp.Plain ??= MeshBindings.Of(VertexAttributes(gp.Vertices), gp.Indices);

    // ------------------------------------------------------------------ drawing

    sealed class Batch
    {
        public required MeshAsset Asset;
        public Matrix4x4[] Data = new Matrix4x4[64];
        public int Count, Offset;
        /// <summary>The <see cref="Draw"/> call that last put this batch into <c>active</c> (<see cref="BatchOf"/>), and its place there.</summary>
        public long Stamp = -1;
        public int Index;

        public void Add(in Matrix4x4 m)
        {
            if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
            Data[Count++] = m;
        }
    }


    /// <summary>An instance in range of the shadow cascades' eye (its group's records and index), kept for the frame's further cascades (see <see cref="Draw"/>).</summary>
    readonly record struct ShadowCandidate(MeshAsset Asset, FoliageInstanceRecord[] Instances, int Index, float Mesh, float Impostor);
    readonly List<ShadowCandidate> shadowCandidates = [];
    /// <summary>The meshes of the recording pass's work list, in order: the further cascades' batch order (<see cref="CullCandidates"/>).</summary>
    readonly List<(MeshAsset Asset, int Parts)> shadowCandidateOrder = [];
    /// <summary>Counts <see cref="Draw"/> calls (<see cref="Batch.Stamp"/>).</summary>
    long drawStamp;
    readonly FoliageCullView cullView = new();
    long shadowCandidatesFrame = -1, updates;
    Vector3 shadowCandidatesEye;
    float shadowCandidatesRange;

    /// <summary>One visible instance: into its mesh's batch, or the terrain's mesh path for TERRAIN-mode rocks.</summary>
    void Emit(MeshAsset a, in Matrix4x4 t, float w, WorldRenderOptions options)
    {
        DrawnInstances++;
        if (a.Terrain && options.Textures)
        {
            if (w < 0.5f) return;   // the terrain shader has no dither: the instance goes at the middle of the fade
            // Over an interior mask: drawn with the cut (docs/render-interior-cut.md); the sphere is the mesh's bounds placed.
            var into = terrainDraws;
            if (!depthPass && InteriorCut is { Active: true } mask)
            {
                float scale = MathF.Sqrt(MathF.Max(new Vector3(t.M11, t.M12, t.M13).LengthSquared(), MathF.Max(new Vector3(t.M21, t.M22, t.M23).LengthSquared(), new Vector3(t.M31, t.M32, t.M33).LengthSquared())));
                if (mask.Overlaps(Vector3.Transform(a.Centre, t), a.Radius * scale)) into = terrainDrawsCut;
            }
            foreach (var gp in a.Main!.Parts) into.Add((PlainMesh(gp), gp.Count, t));
            return;
        }
        BatchOf(a).Add(FoliageCull.Packed(t, w));
    }
    /// <summary>The fog cull (set by the world frame around the main camera's draw only): zones wholly hidden by the fog the eye is in are skipped (meshes and impostors; the grass is range-limited well inside the hide distance).</summary>
    internal FogVolumes? FogCull { get; set; }
    /// <summary>The interior cut (docs/render-interior-cut.md; set by the world frame around its near slice's colour draw, null otherwise): the meshes and grass over a mask take it.</summary>
    internal InteriorMaskRenderer? InteriorCut { get; set; }
    /// <summary>The last frame's depth pyramid for the occlusion cull (empty: none; set by the world frame around the main camera's draw only, like <see cref="FogCull"/>).</summary>
    internal OcclusionView Occlusion { get; set; }
    /// <summary>Foliage instances the occlusion cull left out of the main colour views (a frame ring late, summed over the frame's views).</summary>
    public int OccludedInstances { get; private set; }

    /// <summary>Draws the foliage seen from <paramref name="eye"/> through <paramref name="frustum"/>. Without <paramref name="grass"/> only the meshes (e.g. for a reflection).
    /// <paramref name="continuation"/>: a further depth slice of the same frame, adding to the counts and the GPU time. <paramref name="maxRange"/> caps every layer's range (the reflection).</summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, Vector3 light, Vector3 fogColour, float fogDistance, TerrainRenderer terrain, bool grass = true, bool continuation = false, float maxRange = float.PositiveInfinity)
    {
        if (!continuation) { DrawnInstances = 0; DrawnBlades = 0; DrawCalls = 0; if (!depthPass) MainDetail = ""; }
        if (!Enabled) return;
        (rockTerrain, drawTextures, drawMaterialDistance) = (terrain, options.Textures, options.MaterialDistance);
        int timer = grass ? BeginTimer(continuation) : -1;
        var cpu = Stopwatch.StartNew();
        drawStamp++;
        active.Clear();
        impostorActive.Clear();
        terrainDraws.Clear();
        terrainDrawsCut.Clear();

        // 1. Cull: each instance by its distance along the ground (the game's pages) and its bounding sphere, the groups (a mesh in a zone)
        // in parallel, each into its own list, merged into the batches in order (the same instance order as one pass would give). The shadow
        // cascades of a frame share one pass over the zones (DrawDepth with the same eye and range): it keeps every instance in range, and
        // each cascade then only tests those spheres against its own frustum. The batches are drawn in the order of their mesh's first
        // group in the work list, whatever is visible (step A1 of docs/renderer-native.md: an order a GPU cull can reproduce), minus the empty ones.
        // With the GPU cull (step A2) the CPU walks the same zones and groups into the same work list, and the instances are tested by
        // compute kernels recorded ahead of the frame (FoliageGpuCull); every candidate batch is drawn, in the same order, with the visible
        // count the GPU wrote (0 draws nothing). The shadow cascades of a frame share one work list as they share the candidates above.
        bool gpu = GpuCull && gpuCull is not null;
        bool cachedDepth = depthPass && shadowCandidatesFrame == updates && shadowCandidatesEye == eye && shadowCandidatesRange == maxRange;
        cullView.Set(frustum);
        // With the zone box test in the cascades (ShadowZoneBox) a list is this cascade's alone: the planes are part of the key (each cascade passes its own array).
        bool zoneBox = gpu && depthPass && ShadowZoneBox.Enabled;
        bool reuseWork = gpu && depthPass && gpuWorkFrame == Gpu.Frame.Number && gpuWorkEye == eye && gpuWorkRange == maxRange && gpuWorkTexel == (lodCoarse ? lodTexel : 0)
                         && (zoneBox ? ReferenceEquals(gpuWorkPlanes, frustum) : gpuWorkPlanes is null);
        if (gpu)
        {
            if (!reuseWork)
            {
                CollectWork(eye, maxRange, boxTest: !depthPass || zoneBox);
                BuildGpuWork(options, terrain);
            }
            foreach (var a in gpuOrder) BatchOf(a);
        }
        else
        {
            if (cachedDepth) CullCandidates(options);
            else CullZones(eye, options, maxRange, record: depthPass);
            active.RemoveAll(static b => b.Count == 0);
        }
        if (gpu && !reuseWork) (gpuWorkFrame, gpuWorkEye, gpuWorkRange, gpuWorkTexel, gpuWorkPlanes) = depthPass ? (Gpu.Frame.Number, eye, maxRange, lodCoarse ? lodTexel : 0, zoneBox ? frustum : null) : (-1, default, 0, 0, null);
        lodView = gpu ? CurrentLodView(eye.Y) : default;
        if (gpu) ShowBatches(eye);
        double tCull = cpu.Elapsed.TotalMilliseconds;
        StageClock.Sub("fol cull");

        // 2. The instances: each batch's matrices contiguous, in this frame's constants (one copy; the draws reach a batch by firstInstance).
        int total = 0;
        foreach (var b in active) { b.Offset = total; total += b.Count; }
        if (!gpu) foreach (var b in impostorActive) { b.Offset = total; total += b.Count; }
        if (total > 0 && !gpu)
        {
            instances = Gpu.Frame.AllocateConstants((ulong)(total * InstanceStride), 16);
            var target = instances.Bytes;
            foreach (var b in active)
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * InstanceStride)..]);
            foreach (var b in impostorActive)
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * InstanceStride)..]);
        }

        double tUpload = cpu.Elapsed.TotalMilliseconds;
        StageClock.Sub("fol upload");
        // Alpha to coverage with a multisampled target (the GL version enabled SAMPLE_ALPHA_TO_COVERAGE around the meshes and the grass).
        bool coverage = Gpu.CurrentTargets().Formats.Samples > 1;

        // 3. Meshes: Prepare reads the textures (WorldTexture.Key) and makes the draw list, Record puts it into a native segment of VkGl's pass.
        if (active.Count > 0 && DrawMeshes) PrepareMeshes(options, gpu);
        else meshDraws.Clear();
        // The GPU path's TERRAIN-mode rocks: one draw per part of each rock batch in view, culled by the same dispatch (after the meshes' draws).
        if (gpu) PrepareRocks();
        else rockDraws.Clear();
        if (DrawMeshes) PrepareImpostors(gpu);
        else impostorDraws.Clear();
        if (impostorDraws.Count == 0) WarmImpostorPipeline(depthPass, coverage);
        gpuResult = default;
        if (meshDraws.Count > 0 || rockDraws.Count > 0 || impostorDraws.Count > 0)
        {
            if (gpu)
            {
                gpuResult = DispatchGpuCull(new Vector2(eye.X, eye.Z), terrain, coverage);
                if (gpuResult.IsEmpty) { meshDraws.Clear(); rockDraws.Clear(); }   // the scratch memory is over its cap: this view has no foliage this frame
            }
            if (meshDraws.Count > 0 || impostorDraws.Count > 0)
            {
                // The depth prepass (FoliagePrepass): the main colour view with the GPU cull only (the reflection and the multisampled targets keep the single colour pass).
                bool prepass = FoliagePrepass.Enabled && PrepassView && !depthPass && !coverage && !gpuResult.IsEmpty && (meshDraws.Count > 0 || ImpostorPrepass.Enabled && impostorDraws.Count > 0);
                if (prepass)
                {
                    RecordMeshes(prepassMesh, MeshPass.Prepass, viewProjection, eye, light, fogColour, fogDistance, coverage);
                    RecordMeshes(colourSolidMesh, MeshPass.Equal, viewProjection, eye, light, fogColour, fogDistance, coverage);
                }
                else RecordMeshes(depthPass ? depthMesh : colourMesh, depthPass ? MeshPass.Depth : MeshPass.Colour, viewProjection, eye, light, fogColour, fogDistance, coverage);
            }
        }

        double tMeshes = cpu.Elapsed.TotalMilliseconds;
        StageClock.Sub("fol meshes");
        // 4. Grass.
        if (grass && DrawGrass)
        {
            if (GpuGrassActive)
                DrawGrassGpu(viewProjection, eye, frustum, options, light, fogColour, fogDistance, coverage);
            else
            {
                PrepareGrass(eye, frustum, options, coverage);
                if (grassDraws.Count > 0)
                    RecordGrass(viewProjection, eye, light, fogColour, fogDistance, coverage);
            }
        }
        double tGrass = cpu.Elapsed.TotalMilliseconds;
        StageClock.Sub("fol grass");

        // 5. TERRAIN-mode rocks through the terrain's own mesh path: the GPU's placements and arguments, or the CPU's placements.
        if (gpu)
        {
            if (rockDraws.Count > 0 && gpuResult.Split && !depthPass)
            {
                // The split: the rocks that are not mid-transition with their impostor use the program without the dither's discard; a rock fades only into its impostor.
                var rocksNow = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rockDraws);
                DrawCalls += terrain.DrawMeshesIndirect(rocksNow, gpuResult.Rows, gpuResult.RowsOffset, gpuResult.Args,
                    gpuResult.ArgsOffset + (ulong)(gpuResult.DrawCount + meshDraws.Count) * 20, false, solid: true);
                // The rest: mid-transition rocks and, with an interior mask drawn, those over it (the cull lays them out with the fading ones), with the cut on.
                if (RockImpostorsActive || InteriorCut is { Active: true })
                    DrawCalls += terrain.DrawMeshesIndirect(rocksNow, gpuResult.Rows, gpuResult.RowsOffset, gpuResult.Args,
                        gpuResult.ArgsOffset + (ulong)(2 * gpuResult.DrawCount + meshDraws.Count) * 20, false, solid: false, cut: InteriorCut);
            }
            else if (rockDraws.Count > 0)
                DrawCalls += terrain.DrawMeshesIndirect(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rockDraws), gpuResult.Rows, gpuResult.RowsOffset,
                    gpuResult.Args, gpuResult.ArgsOffset + (ulong)meshDraws.Count * 20, depthPass, cut: depthPass ? null : InteriorCut);
        }
        else if (terrainDraws.Count > 0)
        {
            DrawCalls += terrain.DrawMeshes(terrainDraws, depthPass);
        }
        if (!gpu && terrainDrawsCut.Count > 0) DrawCalls += terrain.DrawMeshes(terrainDrawsCut, cut: InteriorCut);
        StageClock.Sub("fol rocks");
        int rocks = gpu ? rockDraws.Count : terrainDraws.Count + terrainDrawsCut.Count;
        if (WorldFrame.DetailedStats)
        {
            double tEnd = cpu.Elapsed.TotalMilliseconds;
            string steps = $"cull{(cachedDepth ? " (cached)" : "")} {tCull:0.00}, upload {tUpload - tCull:0.00}, meshes {tMeshes - tUpload:0.00} in {active.Count} batches, " +
                $"grass {tGrass - tMeshes:0.00}, TERRAIN-mode rocks {rocks}{(gpu ? " draws" : "")} {tEnd - tGrass:0.00}";
            if (depthPass) DepthDetail = steps;
            else MainDetail += $" [{steps}]";
        }
        EndTimer(timer);
        LastDrawCpuMs = (continuation ? LastDrawCpuMs : 0) + cpu.Elapsed.TotalMilliseconds;
    }

    // ---- culling (Draw step 1) ----


    /// <summary>A further cascade's share of the candidates: the visible ones as batch matrices, in runs of one mesh.</summary>
    sealed class CandidateOutput
    {
        public Matrix4x4[] Visible = new Matrix4x4[64];
        public int Count;
        public readonly List<(MeshAsset Asset, bool Impostor, int Start, int Count)> Runs = [];
    }

    /// <summary>The groups a culling pass tests, in order (the batch order follows it), with their range for this view.</summary>
    readonly List<(Group Group, FoliageGroupRange Range, int Parts)> cullWork = [];
    readonly List<FoliageCullOutput> cullOutputs = [];

    /// <summary>
    /// How far beyond its instances' positions a zone's meshes can reach: the largest (bounds radius + bounds centre offset) x the largest scale
    /// over its groups with decoded meshes (only those are drawn), at least 10 units. Cached until another mesh becomes resident.
    /// </summary>
    float ZoneMargin(ZoneState state)
    {
        if (state.MarginStamp == residentStamp && ReferenceEquals(state.MarginGroups, state.Groups)) return state.Margin;
        float margin = 10;
        foreach (var g in state.Groups)
            if (g.Asset.Resident) margin = Math.Max(margin, (g.Asset.Radius + g.Asset.Centre.Length()) * g.MaxScale);
        (state.Margin, state.MarginStamp, state.MarginGroups) = (margin, residentStamp, state.Groups);
        return margin;
    }

    void CullZones(Vector3 eye, WorldRenderOptions options, float maxRange, bool record)
    {
        if (record) { shadowCandidates.Clear(); shadowCandidateOrder.Clear(); (shadowCandidatesFrame, shadowCandidatesEye, shadowCandidatesRange) = (updates, eye, maxRange); }
        CollectWork(eye, maxRange, boxTest: !record);
        CullWork(eye, options, record);
    }

    /// <summary>The zones and groups in range into <see cref="cullWork"/>, on this thread (reloads start here). <paramref name="boxTest"/>:
    /// each zone's box against the frustum first (not while recording for the cascades: each has its own frustum).</summary>
    void CollectWork(Vector3 eye, float maxRange, bool boxTest)
    {
        var frustum = cullView.Planes;
        // Impostors in colour views, and as casters in the cascades (docs/impostors.md "Shadows").
        bool impostorView = Impostors;
        cullWork.Clear();
        foreach (var state in zones.Values)
        {
            if (!state.Ready) continue;
            float zoneDistance = ZoneDistance(state.Zone, eye);
            // The zone's box against the frustum first: most of a far, wide ring is behind the camera. Its margin is how far the bounds of
            // the zone's meshes reach from their instances' positions (ruins and rock pillars reach hundreds to thousands of units; a fixed
            // margin of 300 culled such a mesh whose zone was out of view while the mesh itself was in it). Not while recording for the
            // cascades on the CPU path, whose candidates serve every cascade of the frame; the GPU path tests each cascade's own list against
            // its planes (ShadowZoneBox), so cascade 0's list no longer holds every zone within the shadow range.
            if (boxTest)
            {
                float m = ZoneMargin(state);
                if (!WorldCamera.Intersects(frustum, new Vector3(state.X0 - m, state.MinY - m, state.Z0 - m), new Vector3(state.X0 + WorldLayout.ZoneSize + m, state.MaxY + m, state.Z0 + WorldLayout.ZoneSize + m))) continue;
                if (FogCull?.Hidden(new Vector3(state.X0 - m, state.MinY - m, state.Z0 - m), new Vector3(state.X0 + WorldLayout.ZoneSize + m, state.MaxY + m, state.Z0 + WorldLayout.ZoneSize + m), FogVolumes.CullKind.Foliage) == true) continue;
            }
            foreach (var g in state.Groups)
            {
                var (full, fullBand) = RangeOf(g);
                float range = Math.Min(full, maxRange), band = range < full ? Math.Min(fullBand, range * 0.25f) : fullBand;   // maxRange: the reflection draws a shorter range
                if (zoneDistance > range) continue;
                if (!g.Asset.Resident)
                {
                    Reload(g.Asset);
                    if (!g.Asset.Failed) NearestMissingMesh = Math.Min(NearestMissingMesh, zoneDistance);
                    continue;
                }
                var (withImpostor, parts) = WithImpostor(FoliageGroupRange.Of(range, band), g, range, band, state, eye, impostorView);
                cullWork.Add((g, withImpostor, parts));
            }
        }
    }

    /// <summary>The CPU cull of <see cref="cullWork"/> (step A1): every instance tested, the visible ones emitted in order.</summary>
    void CullWork(Vector3 eye, WorldRenderOptions options, bool record)
    {
        // The batch order: each mesh's first group in the work list (TERRAIN-mode rocks have no batch).
        foreach (var (g, _, parts) in cullWork)
        {
            if (record) shadowCandidateOrder.Add((g.Asset, parts));
            if ((parts & FoliageCull.MeshPart) != 0 && !(g.Asset.Terrain && options.Textures)) BatchOf(g.Asset);
            if ((parts & FoliageCull.ImpostorPart) != 0) ImpostorBatchOf(g.Asset);
        }
        while (cullOutputs.Count < cullWork.Count) cullOutputs.Add(new FoliageCullOutput());
        LastCullGroups = cullWork.Count;
        var eyeXz = new Vector2(eye.X, eye.Z);
        var view = cullView;
        // The Hub has well over a thousand groups; one work item per group spends more time in the job queue than in the
        // short cull. Each chunk writes its groups' own outputs, so the order and results are unchanged.
        const int CullChunk = 16;
        int chunks = (cullWork.Count + CullChunk - 1) / CullChunk;
        RenderJobs.For(chunks, chunk =>
        {
            int start = chunk * CullChunk, end = Math.Min(start + CullChunk, cullWork.Count);
            for (int k = start; k < end; k++)
            {
                var (g, range, parts) = cullWork[k];
                if (!g.SpheresReady)
                {
                    FoliageCull.FillSpheres(g.Instances, g.Asset.Centre, g.Asset.Radius);   // the mesh's bounds are known once it is resident, and never change
                    g.SpheresReady = true;
                }
                FoliageCull.CullGroup(g.Instances, range, eyeXz, view, record, cullOutputs[k], parts, record ? null : FogCull);
            }
        });
        for (int k = 0; k < cullWork.Count; k++)
        {
            var output = cullOutputs[k];
            var g = cullWork[k].Group;
            if (record)
                for (int i = 0; i < output.InRangeCount; i++)
                    shadowCandidates.Add(new ShadowCandidate(g.Asset, g.Instances, output.InRange[i], output.InRangeMesh[i], output.InRangeImpostor[i]));
            EmitAll(g.Asset, output.Visible.AsSpan(0, output.Count), options);
            EmitImpostors(g.Asset, output.ImpostorVisible.AsSpan(0, output.ImpostorCount));
            if (output.FogCulled > 0) FogCull?.AddCulled(FogVolumes.CullKind.FoliageInstances, output.FogCulled);
        }
    }

    /// <summary>A further shadow cascade: the frame's recorded candidates against this cascade's frustum (tested in parallel, emitted in order).</summary>
    void CullCandidates(WorldRenderOptions options)
    {
        foreach (var (a, parts) in shadowCandidateOrder)
        {
            if ((parts & FoliageCull.MeshPart) != 0 && !(a.Terrain && options.Textures)) BatchOf(a);
            if ((parts & FoliageCull.ImpostorPart) != 0) ImpostorBatchOf(a);
        }
        int count = shadowCandidates.Count, chunks = (count + CandidateChunk - 1) / CandidateChunk;
        while (candidateOutputs.Count < chunks) candidateOutputs.Add(new CandidateOutput());
        var list = shadowCandidates;
        var view = cullView;
        // Each chunk keeps its visible candidates as batch matrices, in runs of one mesh (the candidates were recorded group by group).
        RenderJobs.For(chunks, k =>
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
            var output = candidateOutputs[k];
            output.Count = 0;
            output.Runs.Clear();
            for (int i = k * CandidateChunk, end = Math.Min(i + CandidateChunk, count); i < end; i++)
            {
                ref readonly var c = ref span[i];
                ref readonly var r = ref c.Instances[c.Index];
                if (!FoliageCull.SphereVisible(view, r.Sphere)) continue;
                for (int kind = 0; kind < 2; kind++)
                {
                    float packed = kind == 0 ? c.Mesh : c.Impostor;
                    if (!(packed > FoliageCull.Hidden)) continue;
                    if (output.Runs.Count == 0 || output.Runs[^1].Asset != c.Asset || output.Runs[^1].Impostor != (kind == 1)) output.Runs.Add((c.Asset, kind == 1, output.Count, 0));
                    if (output.Count == output.Visible.Length) Array.Resize(ref output.Visible, output.Count * 2);
                    var t = r.Transform;
                    t.M14 = packed;
                    output.Visible[output.Count++] = t;
                    var run = output.Runs[^1];
                    output.Runs[^1] = run with { Count = run.Count + 1 };
                }
            }
        });
        for (int k = 0; k < chunks; k++)
        {
            var output = candidateOutputs[k];
            foreach (var (asset, impostor, start, n) in output.Runs)
                if (impostor) EmitImpostors(asset, output.Visible.AsSpan(start, n));
                else EmitAll(asset, output.Visible.AsSpan(start, n), options);
        }
    }

    const int CandidateChunk = 2048;
    readonly List<CandidateOutput> candidateOutputs = [];

    // ---- the GPU cull (docs/renderer-native.md 5.3 to 5.6, step A2; the TERRAIN-mode rocks, 5.6.1) ----

    /// <summary>The GPU work list: its meshes in batch order (each mesh's first group in <see cref="cullWork"/>, as the CPU's batches), then
    /// its TERRAIN-mode rock batches (<see cref="gpuRockOrder"/>), its chunks grouped by batch (each batch's groups in work-list order, each
    /// group's instances in order: the CPU's emission order), and each batch's chunk range [<c>gpuBatchStart[b]</c>, <c>gpuBatchStart[b + 1]</c>);
    /// rock batch r is batch <c>gpuOrder.Count + r</c>.</summary>
    readonly List<MeshAsset> gpuOrder = [];
    /// <summary>
    /// The rock batches: a TERRAIN-mode mesh's placements that keep the winding, and those that turn it round (two batches, as
    /// <see cref="TerrainRenderer.DrawMeshes"/> groups them by (mesh part, mirroring)), numbered by their first candidate in the work list.
    /// The CPU's terrain path orders its groups by their first visible placement instead; the order only matters between two different
    /// rocks at exactly the same depth in colour (depth passes keep the nearest whatever the order), as for A1's foliage batches.
    /// </summary>
    readonly List<(MeshAsset Asset, bool Mirrored, int Level)> gpuRockOrder = [];
    FoliageCullChunk[] gpuChunks = new FoliageCullChunk[1024];
    int gpuChunkCount, gpuInstances;
    int[] gpuBatchStart = new int[65], gpuCursor = new int[64];
    /// <summary>The chunks in the frame's constants, written once a frame per work list (<see cref="gpuChunkFrame"/>).</summary>
    Transient gpuChunkData;
    long gpuChunkFrame = -1;
    /// <summary>The shadow cascades' shared work list: the frame, eye and range it was built for (as the CPU's candidates).</summary>
    long gpuWorkFrame = -1;
    Vector3 gpuWorkEye;
    float gpuWorkRange;
    /// <summary>The coarsened cascade's texel the work list was built for (0: not coarsened): its impostor transitions depend on it (<see cref="ShadowLod.ImpostorTransition"/>).</summary>
    float gpuWorkTexel;
    /// <summary>The cascade's planes the depth work list was box-tested against (<see cref="ShadowZoneBox"/>); null: the list holds every zone in range.</summary>
    Vector4[]? gpuWorkPlanes;
    int gpuWorkStamp;
    FoliageCullDraw[] gpuDraws = new FoliageCullDraw[64];
    /// <summary>The current view's cull (rows and indirect arguments), read by <see cref="RecordMeshes"/> and the rocks' draws.</summary>
    FoliageCullResult gpuResult;
    /// <summary>The current view's rock draws (one per part of each rock batch it shows), their batches, in order: their arguments follow
    /// the mesh draws' (<see cref="DispatchGpuCull"/>).</summary>
    readonly List<TerrainRenderer.IndirectMesh> rockDraws = [];
    readonly List<int> rockDrawBatch = [];
    /// <summary>Each rock draw's first index (a generated level's place in its part's level buffer; 0 for the original).</summary>
    readonly List<int> rockDrawFirst = [];

    static bool IsRock(MeshAsset a, WorldRenderOptions options) => a.Terrain && options.Textures;

    /// <summary>
    /// A rock group's records for the GPU: <c>Ground.W</c> = <see cref="FoliageCull.RockBits"/> (the mirroring test and the biome map row,
    /// the same calls the terrain's mesh path makes per frame), made once per biome source (the residency stays per view:
    /// <see cref="FoliageRockView"/>). A group made again leaves the arena, to be placed again with the new bits.
    /// </summary>
    void PrepareRock(Group g, TerrainRenderer terrain)
    {
        var source = terrain.FeatureBiomes;
        bool sorted = RockImpostorsActive;
        if (g.RockReady && ReferenceEquals(g.RockBiomes, source) && (g.RockSorted || !sorted)) return;
        bool plain = false, mirrored = false;
        foreach (ref var r in g.Instances.AsSpan())
        {
            bool m = FoliageCull.Mirrors(r.Transform);
            (plain, mirrored) = (plain | !m, mirrored | m);
            r.Ground.W = FoliageCull.RockBits(r.Transform, terrain.FeatureBiomeRowAny(r.Transform.M41, r.Transform.M43));
        }
        if (sorted)
        {
            // Rock impostors are per biome: the records are put in order of their biome row (stable), so each row is one run, which the cull
            // handles as a chunk range of its own (docs/impostors.md section 13). Only with the switch on: Faithful keeps the placement order.
            var instances = g.Instances;
            static int Row(in FoliageInstanceRecord r) => (int)((uint)r.Ground.W & 1023u) - 1;
            bool ordered = true;
            for (int i = 1; i < instances.Length && ordered; i++) ordered = Row(instances[i - 1]) <= Row(instances[i]);
            if (!ordered)
            {
                var order = Enumerable.Range(0, instances.Length).OrderBy(i => Row(instances[i])).ToArray();
                var copy = (FoliageInstanceRecord[])instances.Clone();
                for (int i = 0; i < order.Length; i++) instances[i] = copy[order[i]];
            }
            var segments = new List<(int, int, int)>();
            for (int i = 0; i < instances.Length;)
            {
                int row = Row(instances[i]), end = i + 1;
                while (end < instances.Length && Row(instances[end]) == row) end++;
                segments.Add((row, i, end - i));
                i = end;
            }
            g.RockSegments = [.. segments];
        }
        else g.RockSegments = [];
        g.RockSorted = sorted;
        g.RockFirstMirrored = g.Instances.Length > 0 && FoliageCull.Mirrors(g.Instances[0].Transform);
        (g.RockPlain, g.RockMirrored, g.RockBiomes, g.RockReady) = (plain, mirrored, source, true);
        gpuCull!.Free(g.Arena, g.ArenaGeneration);
        (g.Arena, g.ArenaGeneration) = (default, 0);
    }

    /// <summary>The work list of <see cref="cullWork"/> for the GPU: each group's records in the arena, the batch order, the chunks.</summary>
    void BuildGpuWork(WorldRenderOptions options, TerrainRenderer terrain)
    {
        var cull = gpuCull!;
        bool placed;
        do
        {
            placed = true;
            for (int k = 0; k < cullWork.Count; k++)
            {
                var g = cullWork[k].Group;
                if (!g.SpheresReady)
                {
                    FoliageCull.FillSpheres(g.Instances, g.Asset.Centre, g.Asset.Radius);   // as CullWork does: the mesh is resident, its bounds known
                    g.SpheresReady = true;
                }
                if (!g.BoundsReady) g.ComputeBounds();
                if (IsRock(g.Asset, options)) PrepareRock(g, terrain);
                // A grown arena (full) moved every group: place them all again before a chunk names one.
                if (!cull.Place(ref g.Arena, ref g.ArenaGeneration, g.Instances)) { placed = false; break; }
                if (g.Arena.IsEmpty && g.Instances.Length > 0) cullWork.RemoveAt(k--);   // the arena is full and the memory is short (VramGuard): left out of this frame
            }
        } while (!placed);

        bool wantLods = LodWanted;
        int stamp = ++gpuWorkStamp;
        gpuOrder.Clear();
        gpuRockOrder.Clear();
        gpuImpostorOrder.Clear();
        foreach (var (g, _, parts) in cullWork)
        {
            var a = g.Asset;
            bool rockSegments = IsRock(a, options) && g.RockSorted;
            if ((parts & FoliageCull.ImpostorPart) != 0)
            {
                if (rockSegments)
                {
                    // A rock's impostor batches are those of the biome rows its segments have an atlas for.
                    foreach (var (row, _, _) in g.RockSegments)
                        if (ReadyRock(a, row) is { } v && v.Impostor!.Stamp != stamp) { (v.Impostor.Stamp, v.Impostor.Index) = (stamp, gpuImpostorOrder.Count); gpuImpostorOrder.Add(v); }
                }
                else if (a.Impostor is { } imp && imp.Stamp != stamp) { (imp.Stamp, imp.Index) = (stamp, gpuImpostorOrder.Count); gpuImpostorOrder.Add(a); }
            }
            // A rock's segment without an atlas is a mesh whatever the parts say (its group's other rows may be impostors).
            if ((parts & FoliageCull.MeshPart) == 0 && !(rockSegments && RockMeshWanted(g, parts))) continue;
            if (IsRock(a, options))
            {
                if (a.RockStamp != stamp)
                {
                    (a.RockStamp, a.RockPlainIndex, a.RockMirroredIndex) = (stamp, -1, -1);
                    // The levels this work list uses are fixed here: every view of the frame that shares it draws the batches it numbers.
                    a.WorkLod = wantLods && a.Lod is { } lod && a.Main is { } lodMain && lodMain.Parts.Count == lod.Parts.Length ? lod : null;
                    if (a.WorkLod is { } work) work.Capped = !LodFar;
                }
                // The group's first placement's batch first (the order its runs are emitted in).
                for (int pass = 0; pass < 2; pass++)
                {
                    bool m = pass == 0 ? g.RockFirstMirrored : !g.RockFirstMirrored;
                    if (!(m ? g.RockMirrored : g.RockPlain)) continue;
                    ref int index = ref m ? ref a.RockMirroredIndex : ref a.RockPlainIndex;
                    if (index < 0)
                    {
                        index = gpuRockOrder.Count;
                        for (int level = 0; level < (a.WorkLod?.Levels ?? 1); level++) gpuRockOrder.Add((a, m, level));
                    }
                }
                continue;
            }
            if (a.WorkStamp != stamp) { (a.WorkStamp, a.WorkIndex) = (stamp, gpuOrder.Count); gpuOrder.Add(a); }
        }
        int meshBatches = gpuOrder.Count, impostorFirst = meshBatches + gpuRockOrder.Count, batches = impostorFirst + gpuImpostorOrder.Count;
        gpuEntries.Clear();
        foreach (var (g, _, parts) in cullWork)
        {
            var a = g.Asset;
            if (IsRock(a, options) && g.RockSorted && (parts & FoliageCull.ImpostorPart) != 0)
            {
                // Per biome row: the rock's mesh chunks (flagged for the transition when the row has an atlas) and, for a row with one, its impostor chunks.
                foreach (var (row, start, length) in g.RockSegments)
                {
                    var v = ReadyRock(a, row);
                    if (v is null || (parts & FoliageCull.MeshPart) != 0)
                    {
                        if (g.RockPlain) AddRockEntries(meshBatches + a.RockPlainIndex, a, g, start, length, v is not null);
                        if (g.RockMirrored) AddRockEntries(meshBatches + a.RockMirroredIndex, a, g, start, length, v is not null);
                    }
                    if (v is not null) gpuEntries.Add(new GpuEntry(impostorFirst + v.Impostor!.Index, g, start, length, false));
                }
                continue;
            }
            if ((parts & FoliageCull.ImpostorPart) != 0)
            {
                if ((parts & FoliageCull.MeshPart) != 0) gpuEntries.Add(new GpuEntry(a.WorkIndex, g, 0, g.Instances.Length, false));
                gpuEntries.Add(new GpuEntry(impostorFirst + a.Impostor!.Index, g, 0, g.Instances.Length, false));
                continue;
            }
            if (!IsRock(a, options)) { gpuEntries.Add(new GpuEntry(a.WorkIndex, g, 0, g.Instances.Length, false)); continue; }
            if (g.RockPlain) AddRockEntries(meshBatches + a.RockPlainIndex, a, g, 0, g.Instances.Length, false);
            if (g.RockMirrored) AddRockEntries(meshBatches + a.RockMirroredIndex, a, g, 0, g.Instances.Length, false);
        }
        if (gpuBatchStart.Length < batches + 1) { gpuBatchStart = new int[batches * 2 + 1]; gpuCursor = new int[batches * 2]; }
        Array.Clear(gpuBatchStart, 0, batches + 1);
        const int Chunk = FoliageShaders.CullChunk;
        // Chunks per batch: a mesh group's instances once; a rock group's once per batch it has (a group with both kinds of placement is
        // tested twice, each run keeping only its own kind).
        foreach (var e in gpuEntries)
            gpuBatchStart[e.Batch + 1] += (e.Length + Chunk - 1) / Chunk;
        for (int b = 0; b < batches; b++) gpuBatchStart[b + 1] += gpuBatchStart[b];
        int n = gpuBatchStart[batches];
        if (gpuChunks.Length < n) gpuChunks = new FoliageCullChunk[Math.Max(n, gpuChunks.Length * 2)];
        Array.Copy(gpuBatchStart, gpuCursor, batches);
        int entry = 0;
        foreach (var (g, range, _) in cullWork)
        {
            // gpuEntries lists cullWork's groups in order, a rock group once per batch it has, a group with an impostor once per part.
            for (; entry < gpuEntries.Count && ReferenceEquals(gpuEntries[entry].Group, g); entry++)
            {
                var e = gpuEntries[entry];
                int b = e.Batch, length = e.Length;
                uint flags = b < meshBatches ? (range.HasImpostor ? FoliageCullChunk.ImpostorMesh : 0)
                    : b >= impostorFirst ? FoliageCullChunk.Impostor
                    : FoliageCullChunk.Rock | (gpuRockOrder[b - meshBatches].Mirrored ? FoliageCullChunk.Mirrored : 0) | (e.ImpostorMesh ? FoliageCullChunk.ImpostorMesh : 0);
                float lodError = 0, lodNext = float.PositiveInfinity;
                if (b >= meshBatches && b < impostorFirst && gpuRockOrder[b - meshBatches].Asset.WorkLod is { } workLod)
                {
                    flags |= FoliageCullChunk.Lod;
                    var levelErrors = LevelErrors(workLod);
                    lodError = levelErrors[e.Level];
                    lodNext = e.Level + 1 < workLod.Levels ? levelErrors[e.Level + 1] : float.PositiveInfinity;
                }
                uint first = FoliageGpuCull.FirstOf(g.Arena) + (uint)e.Start;
                for (int at = 0; at < length; at += Chunk)
                    gpuChunks[gpuCursor[b]++] = new FoliageCullChunk
                    {
                        First = first + (uint)at, Count = (uint)Math.Min(Chunk, length - at),
                        Range = range.Range, RangeSquared = range.RangeSquared, InverseBand = range.InverseBand, Flags = flags,
                        Transition = range.HasImpostor ? range.Transition : 0, InverseTransitionBand = range.InverseTransitionBand,
                        LodError = lodError, LodNextError = lodNext,
                    };
            }
        }
        for (int b = 0; b < batches; b++)
            for (int c = gpuBatchStart[b]; c < gpuBatchStart[b + 1]; c++) (gpuChunks[c].BatchStart, gpuChunks[c].BatchEnd) = ((uint)gpuBatchStart[b], (uint)gpuBatchStart[b + 1]);   // the split lays a batch out whole
        gpuChunkCount = n;
        gpuInstances = 0;
        foreach (var e in gpuEntries) if (e.Level == 0) gpuInstances += e.Length;   // a generated level's entry repeats the instances; each is drawn at one level
        gpuChunkFrame = -1;
    }

    /// <summary>The work list's groups as (batch, group), in work-list order (a rock group once per batch it has), and per batch whether this
    /// view draws it (<see cref="ShowBatches"/>).</summary>
    readonly List<GpuEntry> gpuEntries = [];
    bool[] gpuBatchShown = new bool[64];

    /// <summary>One group's records (<c>Start</c> and <c>Length</c> in <c>Group.Instances</c>: all of them, or a rock's run of one biome row) in a batch; <c>ImpostorMesh</c>:
    /// a rock's mesh run whose row has an atlas (its chunks carry the transition).</summary>
    readonly record struct GpuEntry(int Batch, Group Group, int Start, int Length, bool ImpostorMesh, int Level = 0);

    /// <summary>A rock run's entries: one per generated level in use (<see cref="MeshAsset.WorkLod"/>), the batches of the levels consecutive from <paramref name="batch"/>.</summary>
    void AddRockEntries(int batch, MeshAsset a, Group g, int start, int length, bool impostorMesh)
    {
        for (int level = 0; level < (a.WorkLod?.Levels ?? 1); level++) gpuEntries.Add(new GpuEntry(batch + level, g, start, length, impostorMesh, level));
    }

    /// <summary>A rock group whose work list has impostors needs its mesh chunks too when some of its rows have no atlas (those stay meshes whatever the zone's distance).</summary>
    static bool RockMeshWanted(Group g, int parts)
    {
        // (the caller knows the group is a sorted rock group with an impostor part)
        foreach (var (row, _, _) in g.RockSegments)
            if (ReadyRock(g.Asset, row) is null) return true;
        return false;
    }

    /// <summary>
    /// Which batches this view draws. Every candidate batch is drawn indirect, an empty one with 0 instances, so a cascade (whose work list,
    /// like the CPU's candidates, has no zone test) would record draws for meshes far outside it, at the CPU cost of a draw each. A batch is
    /// drawn only when the box round one of its groups' spheres (<see cref="Group.BoundMin"/>) meets the frustum. A batch left out has no
    /// visible instance (every sphere of its groups is outside a plane), so the picture is the same; the GPU still culls its groups.
    /// </summary>
    void ShowBatches(Vector3 eye)
    {
        int n = gpuOrder.Count + gpuRockOrder.Count + gpuImpostorOrder.Count;
        if (gpuBatchShown.Length < n) gpuBatchShown = new bool[n * 2];
        Array.Clear(gpuBatchShown, 0, n);
        var planes = cullView.Planes;
        foreach (var e in gpuEntries)
            if (!gpuBatchShown[e.Batch] && WorldCamera.Intersects(planes, e.Group.BoundMin, e.Group.BoundMax) && LevelNeeded(e, eye)) gpuBatchShown[e.Batch] = true;
    }

    /// <summary>The rock draws of this view: per rock batch shown, one draw per part of its mesh (<see cref="Emit"/>'s placements, one per part).</summary>
    void PrepareRocks()
    {
        rockDraws.Clear();
        rockDrawBatch.Clear();
        rockDrawFirst.Clear();
        int meshBatches = gpuOrder.Count;
        for (int r = 0; r < gpuRockOrder.Count; r++)
        {
            if (!gpuBatchShown[meshBatches + r]) continue;
            var (a, mirrored, level) = gpuRockOrder[r];
            if (level > 0 && !lodActive) continue;   // this view picks no generated level (a shadow cascade of unknown texel): the batch would draw nothing
            for (int p = 0; p < a.Main!.Parts.Count; p++)
            {
                var gp = a.Main.Parts[p];
                if (level == 0) { rockDraws.Add(new TerrainRenderer.IndirectMesh(PlainMesh(gp), gp.Count, mirrored)); rockDrawFirst.Add(0); }
                else
                {
                    var pl = a.WorkLod!.Parts[p];
                    rockDraws.Add(new TerrainRenderer.IndirectMesh(pl.Bindings, pl.Count[level], mirrored));
                    rockDrawFirst.Add(pl.First[level]);
                }
                rockDrawBatch.Add(meshBatches + r);
            }
        }
    }

    /// <summary>Records this view's cull ahead of the frame: the chunks (once a frame per work list), one draw per mesh draw (its batch's
    /// chunks), then one per rock draw.</summary>
    FoliageCullResult DispatchGpuCull(Vector2 eye, TerrainRenderer terrain, bool coverage)
    {
        var cull = gpuCull!;
        var frame = Gpu.Frame;
        if (gpuChunkFrame != frame.Number)
        {
            gpuChunkData = frame.WriteConstants(MemoryMarshal.AsBytes(gpuChunks.AsSpan(0, gpuChunkCount)), 256);
            gpuChunkFrame = frame.Number;
        }
        int meshes = meshDraws.Count, rocks = meshes + rockDraws.Count, count = rocks + impostorDraws.Count;
        if (gpuDraws.Length < count) gpuDraws = new FoliageCullDraw[count * 2];
        for (int i = 0; i < meshes; i++)
        {
            var d = meshDraws[i];
            gpuDraws[i] = new FoliageCullDraw { FirstIndex = DrawFirst(d.Part), IndexCount = DrawCount(d.Part), ChunkStart = (uint)gpuBatchStart[d.Batch], ChunkEnd = (uint)gpuBatchStart[d.Batch + 1] };
        }
        for (int k = 0; k < rockDraws.Count; k++)
        {
            int b = rockDrawBatch[k];
            gpuDraws[meshes + k] = new FoliageCullDraw { IndexCount = (uint)rockDraws[k].IndexCount, FirstIndex = (uint)rockDrawFirst[k], ChunkStart = (uint)gpuBatchStart[b], ChunkEnd = (uint)gpuBatchStart[b + 1] };
        }
        for (int k = 0; k < impostorDraws.Count; k++)
        {
            int b = impostorDraws[k].Batch;
            gpuDraws[rocks + k] = new FoliageCullDraw { IndexCount = 6, ChunkStart = (uint)gpuBatchStart[b], ChunkEnd = (uint)gpuBatchStart[b + 1] };
        }
        var draws = frame.WriteConstants(MemoryMarshal.AsBytes(gpuDraws.AsSpan(0, count)), 256);
        // What the rocks' row 0 w carries, as the terrain's mesh path writes it: the biome row in colour (resident ones, now), 0 in depth.
        var rock = new FoliageRockView { BiomeRows = !depthPass };
        if (!depthPass && gpuRockOrder.Count > 0) terrain.FeatureResidentBiomes(rock.Resident);
        if (FoliageGpuCull.CountTriangles) cull.TallyView = (depthPass ? 1 : coverage ? 2 : 0, meshes, rocks);
        // The fog cull rides in the main colour view's dispatch only (FogCull is set around that draw alone): per instance, in the cull kernel.
        Span<Vector4> fog = stackalloc Vector4[FogVolumes.CullVectors];
        bool fogOn = !depthPass && FogCull is { } fogCull && fogCull.WriteCull(fog);
        bool occlusionOn = !depthPass && !Occlusion.IsEmpty;
        cull.TimingClass = depthPass || coverage ? 0 : occlusionOn ? 2 : 1;
        // The split (EarlyDepth): in the colour views each batch's dithered instances come after its fully visible ones, drawn with the other program.
        bool split = !depthPass && EarlyDepth.Enabled;
        // The interior cut (main colour view): with the split, the fully visible instances over a mask go with the fading ones, to the program with the cut.
        var interior = split && InteriorCut is { Active: true } mask ? mask.Boxes : null;
        var result = cull.Dispatch(new FoliageCullWork(gpuChunkData, gpuChunkCount, draws, count) { Instances = gpuInstances }, cullView, eye, in rock, 0.999f, fogOn ? fog : default, occlusionOn ? Occlusion : default, lodView, split, interior);
        if (occlusionOn && cull.OccludedIsFresh) OccludedInstances = cull.LateOccluded;
        DrawnInstances += cull.LateVisible;
        if (fogOn && cull.LateFogCulled > 0) FogCull!.AddCulled(FogVolumes.CullKind.FoliageInstances, cull.LateFogCulled);
        return result;
    }

    /// <summary>The mesh's batch, put into <c>active</c> (emptied) by the first call of this <see cref="Draw"/>.</summary>
    Batch BatchOf(MeshAsset a)
    {
        if (!batches.TryGetValue(a, out var batch)) batches[a] = batch = new Batch { Asset = a };
        if (batch.Stamp != drawStamp)
        {
            batch.Stamp = drawStamp;
            batch.Count = 0;
            batch.Index = active.Count;
            active.Add(batch);
        }
        return batch;
    }

    /// <summary>A group's visible instances (batch matrices) into its mesh's batch, or the terrain's mesh path for TERRAIN-mode rocks.</summary>
    void EmitAll(MeshAsset a, ReadOnlySpan<Matrix4x4> visible, WorldRenderOptions options)
    {
        if (visible.Length == 0) return;
        if (a.Terrain && options.Textures)
        {
            foreach (var m in visible)
            {
                var t = m;
                float w = t.M14 >= 2 ? 1 : t.M14;
                t.M14 = 0;
                Emit(a, t, w, options);
            }
            return;
        }
        DrawnInstances += visible.Length;
        var batch = BatchOf(a);
        if (batch.Count + visible.Length > batch.Data.Length) Array.Resize(ref batch.Data, Math.Max(batch.Data.Length * 2, batch.Count + visible.Length));
        visible.CopyTo(batch.Data.AsSpan(batch.Count));
        batch.Count += visible.Length;
    }

    // ---- depth only (the sun's shadow map, ShadowPass) ----
    bool depthPass;
    /// <summary>The cascade being drawn skips meshes smaller than this (world units, <see cref="MeshAsset.Size"/>): their shadow would cover a few texels.</summary>
    float depthMinSize;

    /// <summary>
    /// Draws the foliage meshes' depth for a shadow cascade: <see cref="Draw"/>'s culling and ranges (measured from the camera's
    /// <paramref name="eye"/>) with <see cref="ShadowShaders.MeshDepthFragment"/>, so the leaves' cut-out holds; no grass, no mesh smaller than <paramref name="minSize"/>. <paramref name="texel"/> is the cascade's texel (world units; 0: unknown) for the generated levels. <paramref name="coarse"/>: a far cascade drawn with coarser casters (<see cref="ShadowLod"/>: the generated levels allow more texels). Leaves the draw
    /// counters describing this call.
    /// </summary>
    public void DrawDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, TerrainRenderer terrain, float maxRange = float.PositiveInfinity, float minSize = 0, float texel = 0, bool coarse = false)
    {
        (depthPass, depthMinSize, lodTexel, lodCoarse) = (true, minSize, texel, coarse);
        try { Draw(viewProjection, eye, frustum, options, Vector3.UnitY, Vector3.Zero, 0, terrain, grass: false, maxRange: maxRange); }
        finally { (depthPass, depthMinSize, lodTexel, lodCoarse) = (false, 0, 0, false); }
    }

    // ------------------------------------------------------------------ native recording
    // docs/renderer-native.md 3.3 and 7.5, step O: native-model programs (FoliageShaders' native variants: the same maths, the values in
    // FrameConstants / ViewConstants / push constants, the textures by bindless index), recorded into the pass VkGl has open
    // (BeginNativeInPass). Each draw method is split into Prepare (reads the textures and the zones' state, makes a draw list) and Record (the
    // list into one native segment: the sets and dynamic state once, per draw a push constant block and the mesh's own buffers).

    const int ColourKind = 0, DepthKind = 1, GrassKind = 2, MotionKind = 3, SolidKind = 4, PrepassKind = 5, PrepassSolidKind = 6, EqualKind = 7;
    /// <summary>The first location the per-instance rows (the batch matrices) take; the mesh's own inputs are below it.</summary>
    const int RowLocation = FoliageShaders.InstanceLocation;

    /// <summary>This call's batch matrices in the frame's constants (a batch at its <see cref="Batch.Offset"/>; the draws reach it by firstInstance).</summary>
    Transient instances;

    /// <summary>A mesh's native state for one program: its vertex layout and own vertex buffers, its indices, and the pipelines for the last
    /// two segment states (the reflection's multisampled target alternates with the scene's).</summary>
    struct NativeMesh
    {
        public VertexLayout? Layout;
        public GpuBufferBinding[] Vertices;
        public GpuBufferBinding Elements;
        public int SegA, SegB;
        public GpuPipeline? PipeA, PipeB;
    }

    /// <summary>The pipeline state a segment's draws share (everything of <see cref="GraphicsPipelineDesc"/> but the vertex layout).</summary>
    readonly record struct SegmentPipeline(GpuProgram Program, AttachmentFormats Formats, BlendState Blend, ColorMask Mask,
        PolygonKind Polygon, bool AlphaToCoverage, bool DepthClamp);

    readonly Dictionary<(int Kind, SegmentPipeline State), int> segmentByState = [];
    readonly SegmentPipeline[] lastSegment = new SegmentPipeline[8];
    readonly int[] segmentIds = new int[8];
    int segmentCount;

    /// <summary>A stable number for a segment's pipeline state, so a mesh compares one int per draw.</summary>
    int SegmentId(int kind, GpuProgram p, PassTargets t, DrawState s)
    {
        var segment = new SegmentPipeline(p, t.Formats, s.Blend, s.ColourMask, s.Polygon, s.AlphaToCoverage, s.DepthClamp);
        if (segmentIds[kind] != 0 && segment == lastSegment[kind]) return segmentIds[kind];
        if (!segmentByState.TryGetValue((kind, segment), out int id)) segmentByState[(kind, segment)] = id = ++segmentCount;
        lastSegment[kind] = segment;
        return segmentIds[kind] = id;
    }

    /// <summary>A mesh part's native state for <paramref name="p"/>, made on first use from its native buffers (they never change while the
    /// part lives). The batch matrices are four per-instance rows of 64 bytes at locations 7 to 10 (bound once per segment).</summary>
    static void Current(ref NativeMesh n, GpuPart part, NativeProg p)
    {
        if (n.Layout is not null) return;
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        part.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[RowLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32f, InstanceStride, true);
        n.Layout = p.Layout(attributes);
        n.Vertices = p.Buffers(attributes, p.Own);
        n.Elements = new GpuBufferBinding(part.Indices.Handle, 0, part.Indices.Size);
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    GpuPipeline PipelineFor(ref NativeMesh n, NativeProg p, int segment, DrawState state, AttachmentFormats formats, string label)
    {
        if (n.SegA == segment) return n.PipeA!.Value;
        if (n.SegB == segment) return n.PipeB!.Value;
        var pipeline = Gpu.Pipelines.Get(state.Pipeline(p.P, n.Layout!, PrimitiveType.Triangles, formats, label));
        (n.SegB, n.PipeB) = (n.SegA, n.PipeA);
        (n.SegA, n.PipeA) = (segment, pipeline);
        return pipeline;
    }

    /// <summary>The scene's near depth (<see cref="MotionTargets.NearDepth"/>) in the bindless 2D float array the shaders index: registered
    /// again (the old index freed after the frames in flight) only when the texture or its sampler changed.</summary>
    uint NearDepthIndex(in SampledTexture nearDepth)
    {
        if (nearDepthIndex is { } known && known.Texture == nearDepth) return known.Index;
        if (nearDepthIndex is { } old) Gpu.Bindless.Free(BindlessKind.Texture2D, old.Index);
        nearDepthIndex = (nearDepth, Gpu.Bindless.Register(BindlessKind.Texture2D, nearDepth));
        return nearDepthIndex.Value.Index;
    }

    (SampledTexture Texture, uint Index)? nearDepthIndex;

    /// <summary>Pushes <paramref name="value"/> when its bytes differ from what the segment pushed last (<paramref name="last"/>).</summary>
    static void Push<T>(IGpuCommandList cmd, GpuProgram p, in T value, ref T last, ref bool pushed) where T : unmanaged
    {
        if (pushed && System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in last)))) return;
        cmd.PushConstants(p, GpuStage.Vertex | GpuStage.Fragment, in value);
        last = value;
        pushed = true;
    }

    // ---- meshes ----

    /// <summary>What a mesh draw sets besides its textures.</summary>
    readonly record struct MaterialKey(bool Swizzled, bool HasDiffuse, bool Triplanar, float TileX, float TileY, int AlphaSource, float AlphaThreshold, bool Emissive, float Specular, uint Surface);

    struct MeshDraw
    {
        public GpuPart Part;
        public uint FirstInstance, Instances;
        /// <summary>The batch's place in <c>active</c> (the GPU cull's draw reaches its batch's chunks by it).</summary>
        public int Batch;
        public uint Diffuse, Normal, Diffuse2, Normal2;
        public MaterialKey Material;
        /// <summary>1: uHasNormal, 2: uHasDual, 4: uUseVertexColour.</summary>
        public int PartBits;
        public bool DoubleSided;
    }

    /// <summary>The segment's LOD bias (the upscaler's, <see cref="GpuContext.LodBias"/>, as VkGl applied it where the GL code bound the
    /// textures) and GL's stand-in for a texture not resident.</summary>
    float textureBias;
    uint textureStandIn;

    /// <summary>A new segment: the bias in effect now and the stand-in's bindless index (the 2D float array the shaders index).</summary>
    void NewTextureSegment()
    {
        textureBias = Gpu.LodBias;
        textureStandIn = Gpu.StandIn2D;
    }

    /// <summary>The bindless index of a texture by its <see cref="WorldTexture.Key"/> (0: the stand-in), sampled as the GL texture was.</summary>
    uint Texture(uint key) => key == 0 ? textureStandIn : textures.Index(key, textureBias, textureStandIn);

    readonly List<MeshDraw> meshDraws = [];

    static uint IdOf(WorldTexture? t) => t?.Key ?? 0;

    /// <summary>The batches' mesh parts as draws, in order, with the texture keys (reading a key counts as use: here, not in Record).</summary>
    void PrepareMeshes(WorldRenderOptions options, bool gpu)
    {
        meshDraws.Clear();
        foreach (var b in active)
        {
            if (gpu && !gpuBatchShown[b.Index]) continue;   // the GPU cull: no group of the batch can be in view (ShowBatches)
            var a = b.Asset;
            if (depthPass && a.HasBounds && a.Size < depthMinSize) continue;   // too small for this cascade's texels
            AddMesh(a.Main!, a.MainMaterial!, b, options);
            if (a.Leaves is not null) AddMesh(a.Leaves, a.LeavesMaterial!, b, options);
        }
    }

    void AddMesh(GpuMesh mesh, FoliageMaterial m, Batch b, WorldRenderOptions options)
    {
        uint diffuse = options.Textures ? IdOf(m.Diffuse) : 0;
        bool textured = diffuse != 0;
        uint normalId = textured ? IdOf(m.Normal) : 0;
        bool normal = normalId != 0;
        uint diffuse2 = textured ? IdOf(m.Diffuse2) : 0;
        bool dual = diffuse2 != 0;
        uint normal2 = dual ? IdOf(m.Normal2) : 0;
        // The cut-out mask is the normal map's alpha: sampled whenever the map exists (the lighting checks for tangents itself).
        bool cut = normal && m.AlphaThreshold > 0;
        var key = new MaterialKey(normal && m.Normal!.Swizzled, textured, textured && m.Triplanar, m.Tile.X, m.Tile.Y, cut ? 2 : 0,
            cut ? m.AlphaThreshold : 0f, textured && normal && m.Emissive, textured ? m.Specular : 0.3f, m.Surface);
        foreach (var gp in mesh.Parts)
        {
            int bits = (normal && options.NormalMaps || cut ? 1 : 0) | (dual && gp.HasColours ? 2 : 0) | (gp.HasColours ? 4 : 0);
            meshDraws.Add(new MeshDraw
            {
                Part = gp, FirstInstance = (uint)b.Offset, Instances = (uint)b.Count, Batch = b.Index, Diffuse = diffuse, Normal = normalId, Diffuse2 = diffuse2, Normal2 = normal2,
                Material = key, PartBits = bits, DoubleSided = m.DoubleSided,
            });
            DrawCalls++;
        }
    }

    /// <summary>What a mesh segment draws: the colour, a shadow cascade's depth, the main view's depth prepass (<see cref="FoliagePrepass"/>), or the colour after it (no discards, depth EQUAL, no depth write).</summary>
    enum MeshPass { Colour, Depth, Prepass, Equal }

    /// <summary>The mesh draw list in one native segment (<paramref name="pass"/>), back faces culled unless the material is double-sided.</summary>
    void RecordMeshes(NativeProg mp, MeshPass pass, Matrix4x4 viewProjection, Vector3 eye, Vector3 light, Vector3 fogColour, float fogDistance, bool coverage)
    {
        var p = mp.P;
        bool depth = pass == MeshPass.Depth, prepass = pass == MeshPass.Prepass, equal = pass == MeshPass.Equal;
        string label = depth ? "foliage mesh depth" : prepass ? "foliage mesh prepass" : equal ? "foliage meshes after prepass" : "foliage meshes";
        // Prepare (wave 4, docs/renderer-native.md 6.2): the pass state, the sets, and per draw everything resolved (pipeline, buffers, the push
        // block with its bindless indices), into a job that only records.
        var targets = Gpu.CurrentTargets();
        // The host's state, with what the GL version set around the meshes: back faces culled (GL's cull face on, its mode never changed from
        // BACK), alpha to coverage with a multisampled target.
        var sceneState = Gpu.CurrentState() with { Cull = CullMode.Back, AlphaToCoverage = coverage };
        // The prepass writes depth only; the colour after it draws exactly the fragments that left their depth there (the prepass has the discards, this has none).
        var state = prepass ? sceneState with { ColourMask = 0 } : equal ? sceneState with { Compare = DepthFunction.Equal, DepthWrite = false } : sceneState;
        int segment = SegmentId(depth ? DepthKind : prepass ? PrepassKind : equal ? EqualKind : ColourKind, p, targets, state);
        var job = meshJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Program, job.Count) = (this, targets, state, p, 0);
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance };
        if (!depth) InteriorMaskRenderer.Apply(ref view, InteriorCut);
        job.Frame = nativeFrame.Prepare(in view);
        // The GPU cull's compacted rows and one indirect draw per mesh draw (its batch's count and first instance), else this call's constants.
        bool indirect = !gpuResult.IsEmpty;
        for (int a = 0; a < 4; a++)
            job.Rows[a] = indirect ? new GpuBufferBinding(gpuResult.Rows, gpuResult.RowsOffset + (ulong)(16 * a)) : new GpuBufferBinding(instances.Handle, instances.Offset + (ulong)(16 * a));
        int drawIndex = 0;
        NewTextureSegment();
        // The constants the GL code set on every draw (Shaders.MeshFragment's uniforms the foliage does not vary).
        uint standIn = textureStandIn;
        var pc = new MeshPush
        {
            Tint = Vector3.One, TriplanarScale = 1f / 5000, AlphaChannel = 3, GreyChannel = -1, HeadDiffuse = standIn, HeadNormal = standIn,
            Coverage = coverage ? 1u : 0u,
        };
        var sided = CullMode.None;
        CullMode? side = null;
        // The split (EarlyDepth; GPU cull, colour views): the program without the discards draws the fully visible instances of the parts without a cut-out.
        bool splitView = indirect && gpuResult.Split && !depth && !equal;
        // The prepass splits the same way: the empty program for the fully visible instances of a part without a cut-out, the dither (and the cut-out) program for the rest.
        var solidMesh = prepass ? prepassSolidMesh : colourSolidMesh;
        int solidSegment = splitView ? SegmentId(prepass ? PrepassSolidKind : SolidKind, solidMesh.P, targets, state) : 0;
        foreach (ref readonly var d in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(meshDraws))
        {
            var k = d.Material;
            pc.NormalSwizzled = k.Swizzled ? 1u : 0u;
            pc.HasDiffuse = k.HasDiffuse ? 1u : 0u;
            pc.Triplanar = k.Triplanar ? 1u : 0u;
            pc.Tile = new Vector2(k.TileX, k.TileY);
            pc.AlphaSource = k.AlphaSource;
            pc.AlphaThreshold = k.AlphaThreshold;
            pc.Emissive = k.Emissive ? 1u : 0u;
            pc.Specular = k.Specular;
            pc.Spare = k.Surface;
            pc.HasNormal = (d.PartBits & 1) != 0 ? 1u : 0u;
            pc.HasDual = (d.PartBits & 2) != 0 ? 1u : 0u;
            pc.UseVertexColour = (d.PartBits & 4) != 0 ? 1u : 0u;
            pc.Diffuse = Texture(d.Diffuse);
            pc.Normal = Texture(d.Normal);
            pc.Diffuse2 = Texture(d.Diffuse2);
            pc.Normal2 = Texture(d.Normal2);
            var want = d.DoubleSided ? sided : state.Cull;
            bool raster = side != want;
            side = want;
            var part = d.Part;
            ref var n = ref (depth ? ref part.DepthNative : ref (prepass ? ref part.PrepassNative : ref (equal ? ref part.ColourSolidNative : ref part.ColourNative)));
            Current(ref n, part, mp);
            // A part without a cut-out is drawn twice: its fully visible instances (the cull lays them out first) with the solid program, the dithered
            // ones after them with the full one. A cut-out part keeps its one draw over both (it needs the discard anyway).
            bool split = splitView && pc.AlphaThreshold == 0f;
            if (split)
            {
                ref var sn = ref (prepass ? ref part.PrepassSolidNative : ref part.ColourSolidNative);
                Current(ref sn, part, solidMesh);
                job.Add(new MeshJob.Draw
                {
                    SetRaster = raster, Cull = want, Pipeline = sn.SegA == solidSegment ? sn.PipeA!.Value : PipelineFor(ref sn, solidMesh, solidSegment, state, targets.Formats, prepass ? "foliage mesh prepass solid" : "foliage meshes solid"),
                    Vertices = sn.Vertices, Elements = sn.Elements, Push = pc,
                    Args = gpuResult.Args, ArgsOffset = gpuResult.ArgsOffset + (ulong)(gpuResult.DrawCount + drawIndex) * 20,
                    FirstIndex = DrawFirst(part), IndexCount = DrawCount(part), Instances = d.Instances, FirstInstance = d.FirstInstance,
                });
                raster = false;
            }
            job.Add(new MeshJob.Draw
            {
                SetRaster = raster, Cull = want, Pipeline = n.SegA == segment ? n.PipeA!.Value : PipelineFor(ref n, mp, segment, state, targets.Formats, label),
                Vertices = n.Vertices, Elements = n.Elements, Push = pc,
                Args = indirect ? gpuResult.Args : default, ArgsOffset = gpuResult.ArgsOffset + (ulong)((split ? 2 * gpuResult.DrawCount : 0) + drawIndex) * 20,
                FirstIndex = DrawFirst(part), IndexCount = DrawCount(part), Instances = d.Instances, FirstInstance = d.FirstInstance,
            });
            drawIndex++;
        }
        // The billboards: in the colour segment as before, or (ImpostorPrepass) in the prepass and in the colour after it, the rocks' alone after that.
        bool billboardPrepass = (prepass || equal) && ImpostorPrepass.Enabled;
        if (impostorDraws.Count > 0 && !prepass && !equal) AddImpostorDraws(job, depth, viewProjection, targets, state, coverage, indirect, meshDraws.Count + rockDraws.Count);
        else if (impostorDraws.Count > 0 && billboardPrepass) AddImpostorDraws(job, false, viewProjection, targets, state, coverage, indirect, meshDraws.Count + rockDraws.Count, prepass ? ImpostorPass.Prepass : ImpostorPass.AfterPrepass);
        Gpu.Record(label, job);
        if (equal && impostorDraws.Count > 0 && (!billboardPrepass || impostorDraws.Exists(static d => d.Asset.RockOf is not null)))
        {
            // The billboards are not in the prepass (their fragment stage has its own discard), or only the rocks' are not: their own segment, with the scene's depth test and write.
            var billboards = meshJobs.Rent();
            (billboards.Owner, billboards.Targets, billboards.State, billboards.Program, billboards.Count) = (this, targets, sceneState, p, 0);
            billboards.Frame = job.Frame;
            for (int a = 0; a < 4; a++) billboards.Rows[a] = job.Rows[a];
            AddImpostorDraws(billboards, false, viewProjection, targets, sceneState, coverage, indirect, meshDraws.Count + rockDraws.Count, billboardPrepass ? ImpostorPass.RocksOnly : ImpostorPass.Scene);
            Gpu.Record("foliage billboards", billboards);
        }
    }

    readonly JobPool<MeshJob> meshJobs = new();

    /// <summary>One foliage mesh segment as prepared (<see cref="RecordMeshes"/>): recorded on any thread, the same commands as before wave 4.</summary>
    sealed class MeshJob : RecordJob
    {
        public struct Draw
        {
            public bool SetRaster;
            public CullMode Cull;
            public GpuPipeline Pipeline;
            public GpuBufferBinding[] Vertices;
            public GpuBufferBinding Elements;
            public MeshPush Push;
            /// <summary>Indirect: the arguments' buffer (else a plain indexed draw).</summary>
            public GpuBuffer Args;
            public ulong ArgsOffset;
            public uint IndexCount, FirstIndex, Instances, FirstInstance;
        }

        public FoliageRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public GpuProgram Program = default;
        public FrameBinding Frame;
        public readonly GpuBufferBinding[] Rows = new GpuBufferBinding[4];
        public Draw[] Draws = new Draw[64];
        public int Count;
        public override int Size => Count;

        public void Add(in Draw d)
        {
            if (Count == Draws.Length) Array.Resize(ref Draws, Draws.Length * 2);
            Draws[Count++] = d;
        }

        public override void Record(IGpuCommandList cmd)
        {
            var (targets, state) = (Targets, State);
            cmd.SetViewport(targets.Viewport);
            cmd.SetScissor(targets.Scissor);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            // Metal packs bindless arguments for each program's active resources.
            GpuProgram boundProgram = default;
            bool frameBound = false;
            cmd.BindVertexBuffers(RowLocation, Rows);
            MeshPush last = default;
            bool pushed = false;
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                if (d.SetRaster) cmd.SetRaster(d.Cull, state.Front);
                cmd.BindPipeline(d.Pipeline);
                var drawProgram = d.Pipeline.Desc.Program;
                if (!frameBound || drawProgram.Index != boundProgram.Index)
                {
                    NativeFrame.Record(cmd, drawProgram, in Frame);
                    boundProgram = drawProgram;
                    frameBound = true;
                }
                if (d.Vertices.Length > 0) cmd.BindVertexBuffers(0, d.Vertices);
                cmd.BindIndexBuffer(d.Elements, IndexKind.U32);
                PushTo(cmd, Program, in d.Push, ref last, ref pushed);
                if (!d.Args.IsNull) cmd.DrawIndexedIndirect(d.Args, d.ArgsOffset, 1);
                else cmd.DrawIndexed(d.IndexCount, d.Instances, d.FirstIndex, 0, d.FirstInstance);
            }
        }

        public override void Release()
        {
            Array.Clear(Draws, 0, Count);
            Count = 0;
            Owner.meshJobs.Return(this);
        }
    }

    /// <summary><see cref="Push{T}"/> by layout (a job keeps the layout, not the program).</summary>
    static void PushTo<T>(IGpuCommandList cmd, GpuProgram program, in T value, ref T last, ref bool pushed) where T : unmanaged
    {
        if (pushed && System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in last)))) return;
        cmd.PushConstants(program, GpuStage.Vertex | GpuStage.Fragment, in value);
        last = value;
        pushed = true;
    }

    // ---- grass ----

    struct GrassDraw
    {
        public GrassBuffer Buffer;
        public uint Vertices, Sprite, Colour;
        public float Distance;
        public GrassPush Push;
    }

    struct MotionDraw
    {
        public GrassBuffer Buffer;
        public uint Vertices, Sprite;
        public GrassPush Push;
    }

    readonly List<GrassDraw> grassDraws = [];
    readonly List<(float Distance, ZoneState State, int Key, GrassPage Page)> grassOrder = [];

    /// <summary>The blade buffer's patch textures (looked up by name once per patch, not per draw) and the blades the density setting shows.</summary>
    GrassBuffer Patch(GrassBuffer b, FoliageGrassPatch patch)
    {
        if (!ReferenceEquals(b.Patch, patch))
        {
            b.Patch = patch;
            b.Sprite = textures.Get(patch.Grass.Sprite, false);
            b.ColourMap = textures.Get(patch.Grass.ColourMap, false);
            b.ShownDensity = float.NaN;
        }
        else
        {
            // As textures.Get does on each call: counts as use, and brings an unloaded texture back.
            _ = b.Sprite?.Key;
            _ = b.ColourMap?.Key;
        }
        float density = Math.Min(GrassDensitySetting, MaxGrassDensity) / MaxGrassDensity;
        if (b.ShownDensity != density)
        {
            b.Shown = b.Count == 0 ? 0 : FoliageGrassField.PrefixCount(b.Prefixes, density);   // the density setting: the first blades of the page
            b.ShownDensity = density;
        }
        return b;
    }

    /// <summary>The blade draws of the pages in view, nearest page first (so the depth test rejects most of the hidden blades before they are shaded).</summary>
    void PrepareGrass(Vector3 eye, Vector4[] frustum, WorldRenderOptions options, bool coverage)
    {
        grassDraws.Clear();
        grassOrder.Clear();
        foreach (var st in zones.Values)
            foreach (var (k, pg) in st.Pages)
                if (pg.Buffers is not null)
                    grassOrder.Add((BoxDistance(st.X0 + k % PagesPerZone * PageSize, st.Z0 + k / PagesPerZone * PageSize, PageSize, eye), st, k, pg));
        grassOrder.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        foreach (var (d, state, key, page) in grassOrder)
        {
            float x0 = state.X0 + key % PagesPerZone * PageSize, z0 = state.Z0 + key / PagesPerZone * PageSize;
            float y = eye.Y;
            if (state.Ground is { } ground) y = ground.Height(x0 + PageSize / 2, z0 + PageSize / 2);
            if (!WorldCamera.Intersects(frustum, new Vector3(x0, y - 2000, z0), new Vector3(x0 + PageSize, y + 2000, z0 + PageSize))) continue;
            if (page.Buffers is null) continue;
            for (int i = 0; i < page.Buffers.Length && i < state.Patches.Count; i++)
            {
                var patch = state.Patches[i];
                var b = Patch(page.Buffers[i], patch);
                float range = GrassRange(patch);
                if (b.Shown == 0 || d >= range) continue;
                if (b.Sprite is not { Key: not 0 } sprite) continue;
                var g = patch.Grass;
                bool hasColour = options.Textures && b.ColourMap is { Key: not 0 };
                grassDraws.Add(new GrassDraw
                {
                    Buffer = b, Distance = d, Vertices = g.CrossQuads ? 12u : 6u, Sprite = options.Textures ? sprite.Key : 0, Colour = hasColour ? b.ColourMap!.Key : 0,
                    Push = new GrassPush
                    {
                        Size = new Vector4(g.QuadMinWidth, g.QuadMaxWidth, g.QuadMinHeight, g.QuadMaxHeight),
                        ColourBounds = new Vector4(patch.X0, patch.Z0, patch.X1, patch.Z1),
                        Sway = patch.Layer.Wind ? g.SwayLength : 0f, Range = range, Frequency = 2f, Cross = g.CrossQuads ? 1u : 0u,
                        HasColourMap = hasColour ? 1u : 0u, Coverage = coverage ? 1u : 0u, Wireframe = options.Wireframe == 2 ? 1u : 0u,
                    },
                });
                DrawCalls++;
                DrawnBlades += b.Shown;
            }
        }
    }

    /// <summary>The blade draw list in one native segment: no culling, instanced quads from the blade buffers.</summary>
    void RecordGrass(Matrix4x4 viewProjection, Vector3 eye, Vector3 light, Vector3 fogColour, float fogDistance, bool coverage)
    {
        var gp = grassProgram;
        var p = gp.P;
        // Prepare (wave 4, docs/renderer-native.md 6.2): the pass state, the sets, and per draw the push block with its bindless indices.
        var targets = Gpu.CurrentTargets();
        var state = GrassState(Gpu, coverage);
        int segment = SegmentId(GrassKind, p, targets, state);
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance, Time = SwayPhase() };
        if (!depthPass) InteriorMaskRenderer.Apply(ref view, InteriorCut);
        var job = grassJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Program, job.Count) = (this, targets, state, p, 0);
        job.Frame = nativeFrame.Prepare(in view);
        NewTextureSegment();
        // The blades of every page are one buffer (the grass arena): one pipeline and one vertex binding for the segment, the draw's first instance finds its page.
        job.Pipeline = PipelineFor(ref grassMeshCpu, gp, segment, state, targets.Formats, "foliage grass");
        job.Vertices = grassMeshCpu.Vertices;
        if (job.Draws.Length < grassDraws.Count) job.Draws = new GrassJob.Draw[Math.Max(grassDraws.Count, job.Draws.Length * 2)];
        foreach (ref readonly var d in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(grassDraws))
        {
            var pc = d.Push;
            pc.Sprite = Texture(d.Sprite);
            pc.ColourMap = Texture(d.Colour);
            var buffer = d.Buffer;
            job.Draws[job.Count++] = new GrassJob.Draw { Push = pc, Vertices = d.Vertices, Instances = (uint)buffer.Shown, FirstInstance = buffer.FirstBlade };
        }
        Gpu.Record("foliage grass", job);
    }

    /// <summary>The host's state for the grass, with what the GL version set around it: no culling (GL's cull face off), alpha to coverage
    /// with a multisampled target.</summary>
    static DrawState GrassState(IGpuContext gpu, bool coverage) =>
        gpu.CurrentState() with { Cull = CullMode.None, AlphaToCoverage = coverage };

    /// <summary>The motion pass's state (the host's: colour mask red and green, no depth test, no blending) without culling (GL's cull face off).</summary>
    static DrawState MotionState(IGpuContext gpu) => gpu.CurrentState() with { Cull = CullMode.None };

    readonly JobPool<GrassJob> grassJobs = new();

    /// <summary>The CPU-culled grass of one view as prepared (<see cref="RecordGrass"/>): recorded on any thread, the same commands as before wave 4.</summary>
    sealed class GrassJob : RecordJob
    {
        public struct Draw
        {
            public GrassPush Push;
            public uint Vertices, Instances, FirstInstance;
        }

        public FoliageRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public GpuProgram Program = default;
        public FrameBinding Frame;
        public GpuPipeline Pipeline = default;
        public GpuBufferBinding[] Vertices = [];
        public Draw[] Draws = new Draw[64];
        public int Count;
        public override int Size => Count;

        public override void Record(IGpuCommandList cmd)
        {
            var (targets, state) = (Targets, State);
            cmd.SetViewport(targets.Viewport);
            cmd.SetScissor(targets.Scissor);
            cmd.SetRaster(state.Cull, state.Front);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            NativeFrame.Record(cmd, Program, in Frame);
            GrassPush last = default;
            bool pushed = false;
            cmd.BindPipeline(Pipeline);
            cmd.BindVertexBuffers(0, Vertices);
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                PushTo(cmd, Program, in d.Push, ref last, ref pushed);
                cmd.Draw(d.Vertices, d.Instances, 0, d.FirstInstance);
            }
        }

        public override void Release()
        {
            Pipeline = default;
            Count = 0;
            Owner.grassJobs.Return(this);
        }
    }

    float SwayPhase() => (float)((SwaySeconds ?? clock.Elapsed.TotalSeconds) * 0.3 * Math.PI % (2 * Math.PI));

    // ---- grass motion (upscalers) ----

    Matrix4x4 motionViewProjection, motionUnjittered, previousUnjittered;
    Vector3 motionEye;
    Vector4[] motionFrustum = [];
    float? previousSwayPhase;
    /// <summary>This frame's <see cref="GrassMotionReach.Scale"/> for the motion pass (0: every blade in range).</summary>
    float motionScale;
    bool motionCamera, havePrevious;
    readonly List<MotionDraw> motionDraws = [];

    /// <summary>The near depth slice's camera for <see cref="DrawGrassMotion"/>: this frame's view-projection as drawn (jittered) and unjittered.</summary>
    public void SetMotionCamera(Matrix4x4 viewProjection, Matrix4x4 unjittered, Vector3 eye, Vector4[] frustum)
    {
        (motionViewProjection, motionUnjittered, motionEye, motionFrustum) = (viewProjection, unjittered, eye, frustum);
        motionCamera = true;
    }

    /// <summary>
    /// The swaying grass's own motion over the camera motion, for the upscalers (<see cref="IWorldPostChain.ObjectMotion"/> binds the motion target,
    /// red and green only, without depth test or blending): the near slice's blades of layers with wind, now and with last frame's sway phase
    /// and camera, written where they are what the depth buffer shows. Blades without sway move with the camera and need nothing.
    /// </summary>
    public void DrawGrassMotion(MotionTargets targets)
    {
        if (!motionCamera) return;
        motionCamera = false;
        if (!Enabled || !DrawGrass) { havePrevious = false; return; }
        float time = SwayPhase(), previousTime = previousSwayPhase ?? time;
        var previous = havePrevious ? previousUnjittered : motionUnjittered;
        (previousSwayPhase, previousUnjittered, havePrevious) = (time, motionUnjittered, true);
        var eye = motionEye;
        // grass-velocity: blades whose own motion stays under the threshold are left to the camera reprojection; with no phase step nothing moves at all.
        motionScale = 0;
        if (GrassVelocityCull && targets.FocalPixels > 0)
        {
            float step = GrassMotionReach.PhaseStep(time, previousTime);
            if (step == 0) return;
            motionScale = GrassMotionReach.Scale(step, targets.FocalPixels, GrassVelocityPixels);
        }
        if (GpuGrassActive)
        {
            DrawGrassMotionGpu(targets, eye, time, previousTime, previous);
            return;
        }
        PrepareMotion(eye);
        if (motionDraws.Count == 0) return;
        RecordMotion(targets, eye, time, previousTime, previous);
    }

    /// <summary>The motion draws of the pages in the near slice's view, in the zones' and pages' dictionary order (the CPU reference; the GPU path orders by distance, 5.6.2).</summary>
    void PrepareMotion(Vector3 eye)
    {
        motionDraws.Clear();
        foreach (var state in zones.Values)
            foreach (var (key, page) in state.Pages)
            {
                if (page.Buffers is null) continue;
                float x0 = state.X0 + key % PagesPerZone * PageSize, z0 = state.Z0 + key / PagesPerZone * PageSize;
                float d = BoxDistance(x0, z0, PageSize, eye);
                float y = state.Ground is { } ground ? ground.Height(x0 + PageSize / 2, z0 + PageSize / 2) : eye.Y;
                if (!WorldCamera.Intersects(motionFrustum, new Vector3(x0, y - 2000, z0), new Vector3(x0 + PageSize, y + 2000, z0 + PageSize))) continue;
                for (int i = 0; i < page.Buffers.Length && i < state.Patches.Count; i++)
                {
                    var patch = state.Patches[i];
                    var g = patch.Grass;
                    if (!patch.Layer.Wind || g.SwayLength == 0) continue;
                    var b = Patch(page.Buffers[i], patch);
                    float range = GrassRange(patch);
                    if (b.Shown == 0 || d >= GrassMotionReach.Reach(range, g.SwayLength, motionScale)) continue;
                    if (b.Sprite is not { Key: not 0 } sprite) continue;
                    motionDraws.Add(new MotionDraw
                    {
                        Buffer = b, Vertices = g.CrossQuads ? 12u : 6u, Sprite = sprite.Key,
                        Push = new GrassPush
                        {
                            Size = new Vector4(g.QuadMinWidth, g.QuadMaxWidth, g.QuadMinHeight, g.QuadMaxHeight), Sway = g.SwayLength, Range = range, Frequency = 2f,
                        },
                    });
                }
            }
    }

    void RecordMotion(MotionTargets targets, Vector3 eye, float time, float previousTime, Matrix4x4 previous)
    {
        var gp = grassMotionProgram;
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
        nativeFrame.Bind(cmd, p, in view);
        NewTextureSegment();
        uint nearDepth = NearDepthIndex(targets.NearDepth);
        GrassPush last = default;
        bool pushed = false;
        cmd.BindPipeline(PipelineFor(ref grassMeshMotionCpu, gp, segment, drawState, pass.Formats, "foliage grass motion"));
        cmd.BindVertexBuffers(0, grassMeshMotionCpu.Vertices);
        foreach (ref readonly var d in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(motionDraws))
        {
            var pc = d.Push;
            pc.Sprite = Texture(d.Sprite);
            pc.NearDepth = nearDepth;
            var buffer = d.Buffer;
            Push(cmd, p, in pc, ref last, ref pushed);
            cmd.Draw(d.Vertices, (uint)buffer.Shown, 0, buffer.FirstBlade);
        }
        Gpu.EndGuest(cmd);
    }

    // GPU timing with the frame's timestamps (QueryArena), written where the GL version wrote its timestamp queries (through the seam's
    // Interleave: into the frame, or a host's inline secondary), read once the frame's slot has been collected (a frame ring later).
    readonly record struct Timer(QuerySlot Start, QuerySlot End, bool Continuation);

    readonly List<Timer> timers = [];
    QuerySlot timerStart, pendingStamp;
    bool timerContinuation;
    Action<IGpuCommandList>? recordStamp;
    double gpuSum;
    int gpuCount;

    void Stamp(QuerySlot slot)
    {
        pendingStamp = slot;
        Gpu.Interleave(recordStamp ??= cmd => cmd.Timestamp(Gpu.Frame.Timestamps, pendingStamp));
    }

    int BeginTimer(bool continuation)
    {
        if (timers.Count >= 64) return -1;
        Stamp(timerStart = Gpu.Frame.Timestamps.Allocate());
        timerContinuation = continuation;
        return timerStart.IsValid ? 0 : -1;
    }

    void EndTimer(int index)
    {
        if (index < 0) return;
        var end = Gpu.Frame.Timestamps.Allocate();
        Stamp(end);
        if (end.IsValid) timers.Add(new Timer(timerStart, end, timerContinuation));
    }

    /// <summary>Collects finished timings into <see cref="GpuMs"/> (per <see cref="Update"/>, i.e. per frame). The timestamps are read once their
    /// frame's slot has been collected, never waited for (<paramref name="wait"/> is kept for the callers; it no longer blocks).</summary>
    public void PollTimers(bool wait)
    {
        _ = wait;
        var arena = Gpu.Frame.Timestamps;
        for (int i = 0; i < timers.Count; i++)
        {
            var t = timers[i];
            if (arena.TryRead(t.Start, out ulong start) && arena.TryRead(t.End, out ulong end))
            {
                gpuSum += (end - start) / 1e6;
                if (!t.Continuation) gpuCount++;
            }
            else if (Gpu.Frame.Number - t.Start.Frame <= 2 * Gpu.FramesInFlight) continue;   // not collected yet
            timers.RemoveAt(i--);
        }
        if (gpuCount > 0) { GpuMs = gpuSum / gpuCount; gpuSum = 0; gpuCount = 0; }
    }


    public void Dispose()
    {
        grassDisposing = true;   // the pages are freed with the store
        foreach (var z in zones.Values) { try { z.Job?.Wait(); } catch (AggregateException) { } foreach (var p in z.Pages.Values) { try { p.Job?.Wait(); } catch (AggregateException) { } FreePage(p); } }
        foreach (var t in lodTasks.Values) { try { t.Wait(); } catch (AggregateException) { } }
        foreach (var a in assetsByMesh.Values)
        {
            try { a.Job?.Wait(); } catch (AggregateException) { }
            a.Lod?.Dispose();
            foreach (var m in new[] { a.Main, a.Leaves })
                if (m is not null) DeleteMesh(m);
        }
        lock (allWorlds) foreach (var w in allWorlds) w.Dispose();
        textures.Dispose();
        colourMesh.Dispose();
        colourSolidMesh.Dispose();
        depthMesh.Dispose();
        prepassMesh.Dispose();
        prepassSolidMesh.Dispose();
        grassProgram.Dispose();
        grassMotionProgram.Dispose();
        DisposeGrassStore();
        gpuCull?.Dispose();
        DisposeImpostors();
        nativeFrame.Dispose();
    }
}

/// <summary>A presentation-only move of placed foliage meshes (<see cref="FoliageRenderer.Shift"/>): those named so by <paramref name="Moves"/> within
/// <paramref name="Radius"/> of <paramref name="Centre"/> (x, z) move by <paramref name="Offset"/> (x, z) and onto <paramref name="Ground"/> there.</summary>
public sealed record FoliageShift(Vector3 Centre, float Radius, Vector3 Offset, Func<string, bool> Moves, Func<float, float, float> Ground);
