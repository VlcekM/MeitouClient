using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;
using Meitou.Rendering.Impostors;

namespace Meitou.Rendering;

/// <summary>
/// Kenshi's foliage around the camera (docs/viewer.md, "Foliage"; the rules in docs/formats/foliage.md): trees, bushes, rocks
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
    public GpuContext Gpu { get; }
    /// <summary>The meshes' allocation name (the F12 VRAM pie groups by it).</summary>
    const string MeshAllocationName = "foliage meshes";
    readonly GameInstall install;
    readonly GameDatabase db;
    readonly WorldLevelData levels;
    readonly AssetLocator assets;
    readonly FoliageCatalog catalog;
    // The native programs (docs/renderer-native.md 3.3, step O: the native model, the same maths), made with the renderer so a draw never compiles.
    readonly NativeFrame nativeFrame;
    readonly NativeProg colourMesh, depthMesh, grassProgram, grassMotionProgram;
    readonly WorldTextureCache textures;
    readonly ConcurrentBag<FoliageWorld> worlds = [];
    readonly List<FoliageWorld> allWorlds = [];
    readonly Dictionary<ZoneCoordinate, ZoneState> zones = [];
    readonly Dictionary<FoliageMesh, MeshAsset> assetsByMesh = [];
    readonly List<MeshAsset> decoding = [];
    readonly Queue<Action> uploads = new();
    readonly Dictionary<MeshAsset, Batch> batches = [];
    readonly List<Batch> active = [];
    readonly List<(MeshBindings, int, Matrix4x4)> terrainDraws = [];
    readonly Stopwatch clock = Stopwatch.StartNew();
    /// <summary>Seconds that drive the grass sway; null uses a wall clock started with the renderer. Screenshots fix it (pictures repeat exactly) and the game sets its own clock.</summary>
    public double? SwaySeconds { get; set; }
    readonly int workers;
    /// <summary>MEITOU_FOLIAGE_DEBUG: "nograss" or "nomeshes" leaves that part out (to measure the other).</summary>
    readonly bool debugNoGrass = Environment.GetEnvironmentVariable("MEITOU_FOLIAGE_DEBUG") == "nograss", debugNoMeshes = Environment.GetEnvironmentVariable("MEITOU_FOLIAGE_DEBUG") == "nomeshes";
    /// <summary>Zone layouts in flight: whole ones and far-only ones, each limited to <see cref="workers"/> at a time.</summary>
    int runningWhole, runningFar;

    public FoliageRenderer(GpuContext gpu, GameInstall install, GameDatabase db, WorldLevelData levels, AssetLocator assets)
    {
        Gpu = gpu;
        this.install = install;
        this.db = db;
        this.levels = levels;
        this.assets = assets;
        var watch = Stopwatch.StartNew();
        catalog = FoliageCatalog.Load(db);
        nativeFrame = new NativeFrame(gpu);
        colourMesh = new NativeProg(gpu, nativeFrame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshFragmentNative(), "foliage meshes");
        depthMesh = new NativeProg(gpu, nativeFrame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshDepthNative(), "foliage mesh depth");
        grassProgram = new NativeProg(gpu, nativeFrame, FoliageShaders.GrassVertexNative(), FoliageShaders.GrassFragmentNative(), "foliage grass");
        grassMotionProgram = new NativeProg(gpu, nativeFrame, FoliageShaders.GrassMotionVertexNative(), FoliageShaders.GrassMotionFragmentNative(), "foliage grass motion");
        textures = new WorldTextureCache(gpu, assets, "foliage textures");
        gpuCull = new FoliageGpuCull(gpu);
        InitGrassStore(gpu);
        workers = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
        var used = catalog.ByBiome.Values.SelectMany(l => l).Distinct().ToList();
        MeshRange = used.Where(l => !l.IsGrass).Select(l => l.Range).DefaultIfEmpty(0).Max();
        NearMeshRange = used.Where(l => !l.IsGrass && !FoliageLayout.IsFarLayer(l)).Select(l => l.Range).DefaultIfEmpty(0).Max();
        GrassMaxRange = used.Where(l => l.IsGrass).Select(l => l.Range).DefaultIfEmpty(0).Max();
        LoadMs = watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Foliage drawn at all (the F key).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The mesh instances culled on the GPU (docs/renderer-native.md 5.3 to 5.6, step A2; the default): per view, compute kernels write the
    /// visible instances and the indirect draw arguments, the CPU walks only zones and groups. <c>MEITOU_GPU_CULL=0</c> starts with the A1 CPU
    /// cull (<see cref="FoliageCull"/>) instead, for A/B comparisons. The TERRAIN-mode rocks go with it: the same dispatch, drawn through <see cref="TerrainRenderer.DrawMeshesIndirect"/> (5.6.1).
    /// </summary>
    public bool GpuCull { get; set; } = Environment.GetEnvironmentVariable("MEITOU_GPU_CULL") != "0";
    /// <summary><c>MEITOU_GPU_CULL_VERIFY=1</c> (with the GPU cull): every view is culled on the CPU too, the GPU's lists are read back a frame
    /// ring later and compared (visible sets, order, fade bits); the summary is printed when the renderer is disposed (5.6).</summary>
    static readonly bool GpuCullVerify = Environment.GetEnvironmentVariable("MEITOU_GPU_CULL_VERIFY") == "1";
    readonly FoliageGpuCull? gpuCull;
    /// <summary>settings.cfg <c>foliage range</c>, <c>grass range</c> and <c>grass density</c> (the game's defaults are 1; the viewer draws
    /// foliage and grass 4x as far by default).</summary>
    public float RangeSetting { get; set; } = Env("MEITOU_FOLIAGE_RANGE", 4);
    public float GrassRangeSetting { get; set; } = Env("MEITOU_GRASS_RANGE", 4);
    public float GrassDensitySetting { get; set; } = Env("MEITOU_GRASS_DENSITY", 1);
    /// <summary>MEITOU_FOLIAGE_RANGE, MEITOU_GRASS_RANGE and MEITOU_GRASS_DENSITY start the three settings at other values (offscreen measurements).</summary>
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
            if (gpuCull is not null) { gpuCull.MayGrow = value is null ? null : value.Allows; gpuCull.Scratch.MayGrow = value is null ? null : bytes => Gpu.Device.Allocator.FitsInFreeSpace(bytes) || value.AllowsPriority(bytes); }
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
    public List<string> Messages { get; } = [];

    /// <summary>Work in flight: zones being laid out, grass pages, meshes decoding or uploading, textures decoding.</summary>
    public int Pending => runningWhole + runningFar + zonesWaiting + grassWaiting + decoding.Count + uploads.Count + textures.PendingCount + zones.Values.Sum(z => z.GrassRunning) + ImpostorPending;

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
            return (gpuCull is { } g ? One("cull", g.Scratch) + $"; arena {g.ArenaUsed / 1048576.0:0.0} of {g.ArenaBytes / 1048576.0:0} MB at {FoliageInstanceRecord.GpuSize} B a record ({g.PackMismatches} not lossless)" : "no GPU cull") + "; " + One("grass", grassStore.Scratch);
        }
    }

    /// <summary><c>MEITOU_FOLIAGE_TRIS=1</c>: the GPU cull's indirect draws per frame (means since <see cref="ResetDrawTally"/>) by view kind:
    /// mesh triangles and instance-draws (one per mesh part), TERRAIN-mode rock triangles and instance-draws, impostor quads, views.</summary>
    public string DrawTallyDescription
    {
        get
        {
            if (gpuCull is not { } g || !FoliageGpuCull.DrawTally) return "off (MEITOU_FOLIAGE_TRIS=1)";
            double n = Math.Max(g.TallyFrames, 1);
            string One(int k, string name) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{name}: mesh {g.Tally[k, 0] / n / 1e3:0.0}k tri in {g.Tally[k, 1] / n:0} inst-draws, rocks {g.Tally[k, 2] / n / 1e3:0.0}k tri in {g.Tally[k, 3] / n:0}, impostors {g.Tally[k, 4] / n:0}, views {g.Tally[k, 5] / n:0.0}");
            return $"{g.TallyFrames} frames; {One(0, "colour")}; {One(1, "shadow")}; {One(2, "reflection")}";
        }
    }

    public void ResetDrawTally() => gpuCull?.ResetTally();

    /// <summary><c>MEITOU_FOLIAGE_TRIS=1</c>: the colour views' meshes that drew the most triangles (and the impostors that drew the most quads),
    /// per frame since <see cref="ResetDrawTally"/>.</summary>
    public string DrawTallyTop(int n)
    {
        if (gpuCull is not { } g || !FoliageGpuCull.DrawTally || g.TallyByName.Count == 0) return "";
        double f = Math.Max(g.TallyFrames, 1);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var meshes = g.TallyByName.Where(e => !e.Key.EndsWith("(impostor)", StringComparison.Ordinal)).OrderByDescending(e => e.Value.Triangles).Take(n)
            .Select(e => string.Create(inv, $"{e.Key} {e.Value.Triangles / f / 1e3:0}k tri / {e.Value.Instances / f:0} inst"));
        var impostors = g.TallyByName.Where(e => e.Key.EndsWith("(impostor)", StringComparison.Ordinal)).OrderByDescending(e => e.Value.Instances).Take(5)
            .Select(e => string.Create(inv, $"{e.Key} {e.Value.Instances / f:0}"));
        return $"meshes by triangles: {string.Join("; ", meshes)}\n          impostors by quads: {string.Join("; ", impostors)}";
    }

    public string Describe() =>
        $"{zones.Values.Count(z => z.Ready)} zones laid out ({zones.Values.Where(z => z.Ready).Sum(z => z.Instances):N0} meshes, " +
        $"{zones.Values.Sum(z => z.Pages.Count):N0} grass pages), {assetsByMesh.Count} foliage meshes ({assetsByMesh.Values.Count(a => a.Resident)} resident), catalog of {catalog.Layers.Count} layers";

    // ------------------------------------------------------------------ streaming

    /// <summary>What a zone's layout holds (docs/viewer.md, "Foliage"). <see cref="Far"/>: the FAR layers only (no grass coverage, 9/10 of the cost).
    /// <see cref="Meshes"/>: every mesh layer, and, with the Meitou <c>range</c> switch, only the large meshes (the zone is farther than the medium
    /// and small ranges and the grass, so nothing else can be drawn there). <see cref="Whole"/>: every layer and the grass.</summary>
    enum Tier { Far, Meshes, Whole }

    sealed class ZoneState
    {
        public required ZoneCoordinate Zone;
        public float X0, Z0;
        public Task<(FoliageZone Zone, FoliageGround? Ground, PreparedZone Prepared)>? Job;
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

    /// <summary>A zone's instances grouped per (mesh, layer) with their arrays built, made on a worker.</summary>
    sealed record PreparedGroup(FoliageMesh Mesh, FoliageLayer Layer, FoliageInstanceRecord[] Instances, float MaxScale);
    sealed record PreparedZone(List<PreparedGroup> Groups, float MinY, float MaxY);

    static PreparedZone Prepare(FoliageZone zone)
    {
        var groups = new List<PreparedGroup>();
        foreach (var g in zone.Instances.GroupBy(i => (i.Mesh, i.Layer)))
        {
            var list = g.ToList();
            int index = groups.Count;
            var records = new FoliageInstanceRecord[list.Count];
            for (int i = 0; i < records.Length; i++)
                records[i] = new FoliageInstanceRecord { Transform = list[i].Transform, Ground = new Vector4(list[i].Position.X, list[i].Position.Z, list[i].Scale, index) };
            groups.Add(new PreparedGroup(g.Key.Mesh, g.Key.Layer, records, list.Max(i => i.Scale)));
        }
        return new PreparedZone(groups, zone.Instances.Count == 0 ? 0 : zone.Instances.Min(i => i.Position.Y), zone.Instances.Count == 0 ? 0 : zone.Instances.Max(i => i.Position.Y));
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
            foreach (ref readonly var r in Instances.AsSpan())
            {
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

    void Update(Vector3 eye, bool settling)
    {
        updates++;
        var watch = Stopwatch.StartNew();
        // Uploads get 2 ms a frame (texture decodes are never waited for) except while settling for a screenshot.
        double budget = settling ? 1e9 : 2.0;
        // Two tiers (docs/viewer.md, "Foliage"): within the near reach (the MEDIUM layers and the grass) a zone is laid out whole; beyond it, out
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
            if (state.Job is null && ZoneDistance(state.Zone, eye) > farReach + WorldLayout.ZoneSize) dropping.Add(state);
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
        for (int dz = -reach; dz <= reach; dz++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                var z = new ZoneCoordinate(centre.X + dx, centre.Y + dz);
                if (!z.IsInsideGrid) continue;
                float d = ZoneDistance(z, eye), dp = ZoneDistance(z, predicted), soon = Math.Min(d, dp);
                if (d > farReach) continue;   // the prediction orders and adds whole layouts, it never adds zones
                bool meitou = MeitouRange;
                var need = soon <= wholeReach + PrefetchMargin ? Tier.Whole : meitou && soon <= nearReach + PrefetchMargin ? Tier.Meshes : Tier.Far;
                zones.TryGetValue(z, out var state);
                if (state is not null && Satisfies(state, need, meitou)) continue;   // has what it needs
                if (d <= wholeReach) NearestIncompleteZone = Math.Min(NearestIncompleteZone, d);
                if (state is not { Ready: true }) NearestUnlaidZone = Math.Min(NearestUnlaidZone, d);
                if (state?.Job is not null) continue;   // on its way
                // Whole layouts first (what is missing there is in view near the eye), then the large-only ones, then the far ones, each nearest
                // first (from the eye or where it will be, whichever is nearer).
                wanted.Add((z, need, need == Tier.Whole ? soon : need == Tier.Meshes ? nearReach + soon : farReach + soon));
            }
        wanted.Sort((a, b) => a.Urgency.CompareTo(b.Urgency));
        int started = 0;
        foreach (var (zone, tier, _) in wanted)
        {
            if (guard is { Streaming: false }) break;   // memory pressure: no new layout is started
            bool whole = tier != Tier.Far;   // a Meshes layout is a whole one whose far-away groups are left out (Accept)
            if (whole ? runningWhole >= workers : runningFar >= workers) continue;
            if (!zones.TryGetValue(zone, out var state))
            {
                var (x0, z0) = WorldLayout.ZoneOrigin(zone);
                zones[zone] = state = new ZoneState { Zone = zone, X0 = (float)x0, Z0 = (float)z0 };
            }
            if (whole) runningWhole++; else runningFar++;
            state.JobTier = tier;
            started++;
            bool farOnly = !whole;
            Func<(FoliageZone, FoliageGround?, PreparedZone)> job = () =>
            {
                if (!worlds.TryTake(out var world))
                {
                    world = new FoliageWorld(install, db, levels, catalog);
                    lock (allWorlds) allWorlds.Add(world);
                }
                try
                {
                    var (laid, ground) = world.Load(zone, farOnly);
                    return (laid, ground, Prepare(laid));   // grouped on the worker: thousands of instances cost 15 to 20 ms on the render thread
                }
                finally { worlds.Add(world); }
            };
            // Whole layouts go ahead of the grass pages and texture decodes queued meanwhile (a dozen grass pages could hold every worker).
            state.Job = whole ? BackgroundWork.RunUrgent(job) : BackgroundWork.Run(job);
        }
        zonesWaiting = wanted.Count - started;   // needed but not started yet (the workers are busy)
        NearestMissingMesh = float.PositiveInfinity;

        double t0 = watch.Elapsed.TotalMilliseconds;
        UpdateGrass(eye);
        double t1 = watch.Elapsed.TotalMilliseconds;
        PumpMeshes();
        Prune();
        TrimResident(eye, force: settling);
        double t2 = watch.Elapsed.TotalMilliseconds;
        textures.Pump(wait: settling, max: settling ? 64 : 1);
        double t3 = watch.Elapsed.TotalMilliseconds;
        while (uploads.Count > 0 && watch.Elapsed.TotalMilliseconds < budget)
        {
            var step = uploads.Dequeue();
            var one = Stopwatch.StartNew();
            step();
            if (StreamLog && one.Elapsed.TotalMilliseconds > 3) Console.WriteLine($"slow foliage step {step.Method.Name}: {one.Elapsed.TotalMilliseconds:0.0} ms");
        }
        lock (textures.Messages)
        {
            foreach (var m in Messages.Concat(textures.Messages).Distinct().Take(20)) Console.WriteLine($"warning   {m}");
            Messages.Clear();
            textures.Messages.Clear();
        }
        UpdateImpostors(eye, settling);
        double t4 = watch.Elapsed.TotalMilliseconds;
        PollTimers(wait: false);
        if (!settling && watch.Elapsed.TotalMilliseconds > 8 && Environment.GetEnvironmentVariable("MEITOU_STREAM_LOG") == "1") Console.WriteLine($"slow foliage update {watch.Elapsed.TotalMilliseconds:0.0}: zones {t0:0.0}, grass {t1 - t0:0.0}, meshes {t2 - t1:0.0}, textures {t3 - t2:0.0}, uploads {t4 - t3:0.0}");
        LastUpdateMs = watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Waits until everything wanted around <paramref name="eye"/> is laid out, decoded, uploaded and textured (offscreen rendering).</summary>
    public void Settle(Vector3 eye, int timeoutMs = 300000)
    {
        var watch = Stopwatch.StartNew();
        int rounds = 0;
        double? pressureSince = null;
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            guard?.Tick();
            Update(eye, settling: true);
            if (++rounds > 2 && Pending == 0) return;
            // Under memory pressure the guard holds the loading back: what is missing will not come until it ends, so do not wait for it.
            pressureSince = guard is { Pressure: true } ? pressureSince ?? watch.Elapsed.TotalSeconds : null;
            if (pressureSince is { } since && watch.Elapsed.TotalSeconds - since > 5) { Console.WriteLine("warning   foliage streaming stopped waiting: memory pressure (VramGuard)"); return; }
            // An impostor bake reads its rows back after the frame they were recorded into, and atlas uploads are spread over frames: end it.
            if (impostorBake is { NeedsFrame: true } || impostorUploadsWaiting) Gpu.Finish();
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
    void Accept(ZoneState state, (FoliageZone Zone, FoliageGround? Ground, PreparedZone Prepared) result, Tier tier)
    {
        var (zone, ground, prepared) = result;
        // With the Meitou range switch a zone beyond the whole reach can only show large meshes (the small and medium ranges and the grass end
        // before it): the other groups would be instances, meshes and textures held for nothing. A group whose mesh is not decoded yet has no
        // size class; it is kept and the prune pass (TrimResident) drops it when the size is known.
        bool filter = MeitouRange && tier != Tier.Whole;
        var groups = new List<Group>(prepared.Groups.Count);
        bool unknown = false;
        foreach (var g in prepared.Groups)
        {
            var asset = AssetFor(g.Mesh);
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
        state.Instances = zone.Instances.Count;
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
            gpuCull.Free(g.Arena, g.ArenaGeneration);
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
            if (b.Slot >= 0) grassStore.FreeSlot(b.Slot);
            if (!b.Range.IsEmpty) grassStore.FreeBlades(b.Range);
            b.Slot = -1;
            b.Range = default;
        }
    }

    float GrassRange(FoliageGrassPatch patch) => patch.Layer.Range * GrassRangeSetting * Scale;

    /// <summary>The highest "Grass density x" the pages are generated for (the settings slider's top): lower settings draw a prefix of the blades.</summary>
    public const float MaxGrassDensity = 2f;
    /// <summary>Grass pages generating at once, over all zones.</summary>
    const int MaxGrassJobs = 12;
    readonly List<(float Distance, ZoneState State, int Key)> grassWanted = [];
    int zonesWaiting, grassWaiting;

    void UpdateGrass(Vector3 eye)
    {
        grassWanted.Clear();
        NearestMissingGrass = float.PositiveInfinity;
        int inFlight = 0;
        foreach (var state in zones.Values) inFlight += state.GrassRunning;
        foreach (var state in zones.Values)
        {
            if (!state.Ready || state.Ground is null || state.Patches.Count == 0) continue;
            float range = state.Patches.Max(GrassRange);
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
                        if (d < range + PageSize * 0.5f) grassWanted.Add((d, state, key));
                        continue;
                    }
                    if (page.Job is null && d > range + PageSize * 2)
                    {
                        FreePage(page);
                        state.Pages.Remove(key);
                    }
                    else if (page.Job is { IsCompleted: true } job)
                    {
                        state.GrassRunning--;
                        page.Job = null;
                        var result = job.Result;
                        QueuePage(state, key, page, result.Blades, result.Prefixes);
                    }
                    else if (page.PendingBlades is not null) QueuePage(state, key, page, page.PendingBlades, page.PendingPrefixes!);   // no room last time
                }
        }
        // Missing pages nearest first, a bounded number at a time. Pages hold the blades of the highest density setting,
        // so changing the setting never regenerates them (Draw shows a prefix).
        grassWanted.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        grassWaiting = grassWanted.Count;
        foreach (var (_, state, key) in grassWanted)
        {
            if (inFlight >= MaxGrassJobs || guard is { Streaming: false }) break;
            float x0 = state.X0 + key % PagesPerZone * PageSize, z0 = state.Z0 + key / PagesPerZone * PageSize;
            var page = state.Pages[key] = new GrassPage();
            var patches = state.Patches;
            var ground = state.Ground!;
            state.GrassRunning++;
            inFlight++;
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
        public Task<(Model? Main, Model? Leaves)>? Job;
        public GpuMesh? Main, Leaves;
        public bool Resident, Failed, Uploading;
        public long LastUsed, Bytes;
        public FoliageMaterial? MainMaterial, LeavesMaterial;
        public bool Terrain => Mesh.MaterialType == 2;
        public Vector3 Centre;
        public float Radius = 1;
        /// <summary><see cref="Centre"/>, <see cref="Radius"/> and <see cref="SizeClass"/> are known (the mesh was decoded once; they never change).</summary>
        public bool HasBounds;
        public FoliageSizeClass SizeClass;
        /// <summary>The triangles of the mesh and its leaves mesh (known with <see cref="HasBounds"/>): the small impostor class is for meshes with many.</summary>
        public int Triangles;
        /// <summary>The impostor class estimated from the mesh's own bounds, once (<see cref="FoliageRenderer.EstimateClass"/>).</summary>
        public ImpostorClass? EstimatedClass;
        public bool ClassEstimated;
        /// <summary>The GPU work list that last numbered this mesh's batch (<see cref="BuildGpuWork"/>), and the batch's number in it.</summary>
        public int WorkStamp, WorkIndex;
        /// <summary>For a TERRAIN-mode mesh: the work list that last numbered its rock batches, and their numbers among the rock batches
        /// (plain placements, mirroring placements; -1: none in the work list).</summary>
        public int RockStamp, RockPlainIndex, RockMirroredIndex;
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
        public NativeMesh ColourNative, DepthNative;
        public int Count;
        /// <summary>Blades after each 1/64 of the candidates (<see cref="FoliageGrassField.BladesWithPrefixes"/>): the density setting draws a prefix.</summary>
        public int[] Prefixes = [];
        public bool HasTangents, HasColours;
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
        a.Job = BackgroundWork.Run(() => (Decode(main), leaves.Length > 0 ? Decode(leaves) : null));
        decoding.Add(a);
        return a;
    }

    Model? Decode(string name)
    {
        var path = assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        if (path is null) { lock (Messages) Messages.Add($"foliage mesh not found: {name}"); return null; }
        try { return Model.Build(OgreMeshReader.ReadFile(path), null); }
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
            a.Radius = Math.Max((max - min).Length() / 2, 1);
            a.SizeClass = FoliageSizes.Classify(FoliageSizes.Size(a.Radius, a.Mesh));
            a.Triangles = main.Parts.Sum(p => p.Indices.Length / 3) + (leaves?.Parts.Sum(p => p.Indices.Length / 3) ?? 0);
            a.HasBounds = true;
            QueueMesh(a, main, leaves);
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
            int vertexBytes = p.Vertices.Length * Vertex.Size, indexBytes = p.Indices.Length * sizeof(uint);
            tally[0] += vertexBytes + indexBytes;
            uploads.Enqueue(() =>
            {
                gp = new GpuPart
                {
                    Vertices = DeviceBuffer.Create(Gpu, (ulong)vertexBytes, BufferUse.Vertex, MeshAllocationName),
                    Indices = DeviceBuffer.Create(Gpu, (ulong)indexBytes, BufferUse.Index, MeshAllocationName),
                    Count = p.Indices.Length, HasTangents = p.HasTangents, HasColours = p.HasColours,
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
                    Gpu.Uploads.Write(gp!.Vertices, (ulong)start, System.Runtime.InteropServices.MemoryMarshal.AsBytes(p.Vertices.AsSpan()).Slice(start, length));
                });
            }
            for (int at = 0; at < indexBytes; at += SlabBytes)
            {
                int start = at, length = Math.Min(SlabBytes, indexBytes - at);
                uploads.Enqueue(() =>
                {
                    Gpu.EnsureFrame();
                    Gpu.Uploads.Write(gp!.Indices, (ulong)start, System.Runtime.InteropServices.MemoryMarshal.AsBytes(p.Indices.AsSpan()).Slice(start, length));
                });
            }
        }
    }

    static readonly bool StreamLog = Environment.GetEnvironmentVariable("MEITOU_STREAM_LOG") == "1";
    long residentMeshBytes, lastTrim;
    /// <summary>Counts meshes becoming resident (the zones' cull margins are recomputed after it changes).</summary>
    int residentStamp;
    int meshUnloads, meshReloads;

    /// <summary>Meshes not needed (no loaded zone has a group of them within its range) for this long are unloaded.</summary>
    public double IdleSeconds { get; set; } = StreamingTuning.IdleSeconds;

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
            a.Resident = false;
            residentMeshBytes -= a.Bytes;
            a.Bytes = 0;
            meshUnloads++;
        }
    }

    void DeleteMesh(GpuMesh m)
    {
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
        a.Job = BackgroundWork.Run(() => (Decode(main), leaves.Length > 0 ? Decode(leaves) : null));
        decoding.Add(a);
        meshReloads++;
    }

    /// <summary>The mesh layout: position, normal, texture coordinates, tangent, colour of <see cref="Vertex"/> (locations 0 to 4).</summary>
    internal static readonly MeshAttribute[] MeshLayout = [new(0, 3, 0), new(1, 3, 12), new(2, 2, 24), new(3, 4, 32), new(4, 4, 48)];

    /// <summary>The attributes at locations 0 to 4 as VkGl exported the GL vertex array (one binding per attribute, at its offset).</summary>
    internal static LegacyProgram.Attribute?[] VertexAttributes(DeviceBuffer vertices)
    {
        var result = new LegacyProgram.Attribute?[MeshLayout.Length];
        foreach (var a in MeshLayout)
            result[a.Location] = new LegacyProgram.Attribute(new BufferBinding(vertices.Handle, (ulong)a.Offset),
                GlConventions.VertexFormat(GLEnum.Float, a.Size, false, false), (uint)Vertex.Size, false);
        return result;
    }

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
            foreach (var gp in a.Main!.Parts) terrainDraws.Add((PlainMesh(gp), gp.Count, t));
            return;
        }
        BatchOf(a).Add(FoliageCull.Packed(t, w));
    }
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
        if (GpuCullVerify && verifyPending.Count > 0) CheckVerify(all: false);
        if (GpuCullVerify) CheckGrassVerify(all: false);
        int callsBefore = DrawCalls;
        active.Clear();
        impostorActive.Clear();
        terrainDraws.Clear();

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
        bool reuseWork = gpu && depthPass && gpuWorkFrame == Gpu.Frame.Number && gpuWorkEye == eye && gpuWorkRange == maxRange;
        if (gpu && !GpuCullVerify)
        {
            if (!reuseWork)
            {
                CollectWork(eye, maxRange, boxTest: !depthPass);
                BuildGpuWork(options, terrain);
            }
            foreach (var a in gpuOrder) BatchOf(a);
        }
        else
        {
            if (cachedDepth) CullCandidates(options);
            else CullZones(eye, options, maxRange, record: depthPass);
            if (!gpu) active.RemoveAll(static b => b.Count == 0);
            else if (!reuseWork) BuildGpuWork(options, terrain);   // the verify mode: the GPU's work from the list the CPU just culled
        }
        if (gpu && !reuseWork) (gpuWorkFrame, gpuWorkEye, gpuWorkRange) = depthPass ? (Gpu.Frame.Number, eye, maxRange) : (-1, default, 0);
        if (gpu) ShowBatches();
        double tCull = cpu.Elapsed.TotalMilliseconds;
        StageClock.Sub("fol cull");

        // 2. The instances: each batch's matrices contiguous, in this frame's constants (one copy; the draws reach a batch by firstInstance).
        int total = 0;
        foreach (var b in active) { b.Offset = total; total += b.Count; }
        if (!gpu) foreach (var b in impostorActive) { b.Offset = total; total += b.Count; }
        if (total > 0 && !gpu)
        {
            instances = Gpu.Frame.Constants.Allocate((ulong)total * InstanceStride, 16);
            var target = new Span<byte>(instances.Pointer, total * InstanceStride);
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
        double recMeshes = 0, recGrass = 0, dispatchMs = 0;
        if (active.Count > 0 && !debugNoMeshes) PrepareMeshes(options, gpu);
        else meshDraws.Clear();
        // The GPU path's TERRAIN-mode rocks: one draw per part of each rock batch in view, culled by the same dispatch (after the meshes' draws).
        if (gpu) PrepareRocks();
        else rockDraws.Clear();
        if (!debugNoMeshes) PrepareImpostors(gpu);
        else impostorDraws.Clear();
        if (impostorDraws.Count == 0) WarmImpostorPipeline(depthPass, coverage);
        gpuResult = default;
        if (meshDraws.Count > 0 || rockDraws.Count > 0 || impostorDraws.Count > 0)
        {
            long r0 = FolTiming ? Stopwatch.GetTimestamp() : 0;
            if (gpu)
            {
                gpuResult = DispatchGpuCull(new Vector2(eye.X, eye.Z), terrain);
                if (gpuResult.IsEmpty) { meshDraws.Clear(); rockDraws.Clear(); }   // the scratch memory is over its cap: this view has no foliage this frame
                if (FolTiming) { long r1 = Stopwatch.GetTimestamp(); dispatchMs = (r1 - r0) * 1000.0 / Stopwatch.Frequency; r0 = r1; }
            }
            if (meshDraws.Count > 0 || impostorDraws.Count > 0)
            {
                RecordMeshes(depthPass ? depthMesh : colourMesh, depthPass, viewProjection, eye, light, fogColour, fogDistance, coverage);
                if (FolTiming) recMeshes = (Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency;
            }
        }

        double tMeshes = cpu.Elapsed.TotalMilliseconds;
        int callsMeshes = DrawCalls;
        StageClock.Sub("fol meshes");
        // 4. Grass.
        if (grass && !debugNoGrass)
        {
            if (GpuGrassActive)
            {
                long r0 = FolTiming ? Stopwatch.GetTimestamp() : 0;
                DrawGrassGpu(viewProjection, eye, frustum, options, light, fogColour, fogDistance, coverage);
                if (FolTiming) recGrass = (Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency;
            }
            else
            {
                PrepareGrass(eye, frustum, options, coverage);
                if (grassDraws.Count > 0)
                {
                    long r0 = FolTiming ? Stopwatch.GetTimestamp() : 0;
                    RecordGrass(viewProjection, eye, light, fogColour, fogDistance, coverage);
                    if (FolTiming) recGrass = (Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency;
                }
            }
        }
        double tGrass = cpu.Elapsed.TotalMilliseconds;
        int callsGrass = DrawCalls;
        StageClock.Sub("fol grass");

        // 5. TERRAIN-mode rocks through the terrain's own mesh path: the GPU's placements and arguments (the verify mode's CPU placements are
        // only compared), or the CPU's placements.
        if (gpu)
        {
            if (rockDraws.Count > 0)
                DrawCalls += terrain.DrawMeshesIndirect(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rockDraws), gpuResult.Rows, gpuResult.RowsOffset,
                    gpuResult.Args, gpuResult.ArgsOffset + (ulong)meshDraws.Count * 20, depthPass);
        }
        else if (terrainDraws.Count > 0)
        {
            DrawCalls += terrain.DrawMeshes(terrainDraws, depthPass);
        }
        StageClock.Sub("fol rocks");
        int rocks = gpu ? rockDraws.Count : terrainDraws.Count;
        if (FolTiming) FolAccount(depthPass ? 1 : 0, tCull, tUpload, tMeshes, tGrass, cpu.Elapsed.TotalMilliseconds, callsMeshes - callsBefore, callsGrass - callsMeshes, rocks, recMeshes, recGrass, dispatchMs);
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

    /// <summary><c>MEITOU_FOLIAGE_TIMING=1</c>: the CPU time of <see cref="Draw"/>'s steps summed over the run by kind (colour or depth), with the
    /// draws each step issued, printed when the renderer is disposed (docs/renderer-native.md 7.1, wave 3 foliage probe).</summary>
    static readonly bool FolTiming = Environment.GetEnvironmentVariable("MEITOU_FOLIAGE_TIMING") == "1";
    readonly double[,] folMs = new double[2, 5], folRecordMs = new double[2, 2];
    readonly double[] folDispatchMs = new double[2];
    readonly long[] folSeen = new long[2];
    static readonly int FolSkip = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_FOLIAGE_TIMING_SKIP"), out int skip) ? skip : 160;
    readonly long[] folCalls = new long[2], folMeshDraws = new long[2], folGrassDraws = new long[2], folRocks = new long[2];

    void FolAccount(int k, double tCull, double tUpload, double tMeshes, double tGrass, double tEnd, int meshDraws, int grassDraws, int rocks, double recMeshes, double recGrass, double dispatchMs)
    {
        if (folSeen[k]++ < FolSkip) return;   // the first calls are cold (pipelines, streaming)
        folCalls[k]++;
        folMs[k, 0] += tCull; folMs[k, 1] += tUpload - tCull; folMs[k, 2] += tMeshes - tUpload; folMs[k, 3] += tGrass - tMeshes; folMs[k, 4] += tEnd - tGrass;
        folMeshDraws[k] += meshDraws; folGrassDraws[k] += grassDraws; folRocks[k] += rocks;
        folRecordMs[k, 0] += recMeshes; folRecordMs[k, 1] += recGrass;
        folDispatchMs[k] += dispatchMs;
    }

    void ReportFoliageTiming()
    {
        if (!FolTiming) return;
        string[] names = ["colour", "depth"];
        for (int k = 0; k < 2; k++)
        {
            if (folCalls[k] == 0) continue;
            double perDraw(int step, long n) => n > 0 ? folMs[k, step] * 1000 / n : 0;
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"" +
                $"foliage timing {names[k]}: {folCalls[k]} calls, per call us: cull {folMs[k, 0] * 1000 / folCalls[k]:F1}, upload {folMs[k, 1] * 1000 / folCalls[k]:F1}, meshes {folMs[k, 2] * 1000 / folCalls[k]:F1}, grass {folMs[k, 3] * 1000 / folCalls[k]:F1}, rocks {folMs[k, 4] * 1000 / folCalls[k]:F1}; " +
                $"draws per call: meshes {(double)folMeshDraws[k] / folCalls[k]:F1}, grass {(double)folGrassDraws[k] / folCalls[k]:F1}, TERRAIN-mode rocks {(double)folRocks[k] / folCalls[k]:F1} (the CPU cull: placements, one per part; the GPU cull: indirect draws); " +
                $"us per draw: meshes {perDraw(2, folMeshDraws[k]):F2}, grass {perDraw(3, folGrassDraws[k]):F2}; record only (native): meshes {(folMeshDraws[k] > 0 ? folRecordMs[k, 0] * 1000 / folMeshDraws[k] : 0):F2}, grass {(folGrassDraws[k] > 0 ? folRecordMs[k, 1] * 1000 / folGrassDraws[k] : 0):F2}; " +
                $"cull {(GpuCull && gpuCull is not null ? "gpu" : "cpu")}, dispatch recording us per call {folDispatchMs[k] * 1000 / folCalls[k]:F1}"));
        }
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"foliage grass: {(GpuGrassActive ? "gpu" : "cpu")}, arena {grassStore.ArenaUsed / 1048576.0:F1} of {grassStore.ArenaBytes / 1048576.0:F0} MB, slots {grassStore.SlotsUsed} (high {grassStore.SlotHigh}) of {grassStore.SlotCapacity}, " +
            $"{grassStore.Misses} pages without room; {grassStore.Dispatched} views, gpu us per view {(grassStore.GpuTimedViews > 0 ? grassStore.GpuMicroseconds / grassStore.GpuTimedViews : 0):F1}"));
        if (gpuCull is { } g)
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"foliage gpu cull: {g.Dispatched} views, gpu us per view {(g.GpuTimedViews > 0 ? g.GpuMicroseconds / g.GpuTimedViews : 0):F1} ({g.GpuTimedViews} timed), " +
                $"arena {g.ArenaUsed / 1048576.0:F1} of {g.ArenaBytes / 1048576.0:F0} MB ({g.Grows} grown), {g.UploadedInstances:N0} instances uploaded; work lists: {gpuWorkBuilds} built, {gpuWorkGroups / Math.Max(gpuWorkBuilds, 1):F0} groups and {gpuWorkCandidates / Math.Max(gpuWorkBuilds, 1):F0} candidates (chunk slots; {gpuWorkRockCandidates / Math.Max(gpuWorkBuilds, 1):F0} rock instances) each on average"));
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
        bool impostorView = Impostors && (!depthPass || ImpostorCasters);
        cullWork.Clear();
        foreach (var state in zones.Values)
        {
            if (!state.Ready) continue;
            float zoneDistance = ZoneDistance(state.Zone, eye);
            // The zone's box against the frustum first: most of a far, wide ring is behind the camera. Its margin is how far the bounds of
            // the zone's meshes reach from their instances' positions (ruins and rock pillars reach hundreds to thousands of units; a fixed
            // margin of 300 culled such a mesh whose zone was out of view while the mesh itself was in it). Not while recording for the
            // cascades: each has its own frustum.
            if (boxTest)
            {
                float m = ZoneMargin(state);
                if (!WorldCamera.Intersects(frustum, new Vector3(state.X0 - m, state.MinY - m, state.Z0 - m), new Vector3(state.X0 + WorldLayout.ZoneSize + m, state.MaxY + m, state.Z0 + WorldLayout.ZoneSize + m))) continue;
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
                var (withImpostor, parts) = WithImpostor(FoliageGroupRange.Of(range, band), g.Asset, range, band, state, eye, impostorView);
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
        var eyeXz = new Vector2(eye.X, eye.Z);
        var view = cullView;
        RenderJobs.For(cullWork.Count, k =>
        {
            var (g, range, parts) = cullWork[k];
            if (!g.SpheresReady)
            {
                FoliageCull.FillSpheres(g.Instances, g.Asset.Centre, g.Asset.Radius);   // the mesh's bounds are known once it is resident, and never change
                g.SpheresReady = true;
            }
            FoliageCull.CullGroup(g.Instances, range, eyeXz, view, record, cullOutputs[k], parts);
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
    readonly List<(MeshAsset Asset, bool Mirrored)> gpuRockOrder = [];
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
    int gpuWorkStamp;
    long gpuWorkBuilds, gpuWorkGroups, gpuWorkCandidates, gpuWorkRockCandidates;
    FoliageCullDraw[] gpuDraws = new FoliageCullDraw[64];
    /// <summary>The current view's cull (rows and indirect arguments), read by <see cref="RecordMeshes"/> and the rocks' draws.</summary>
    FoliageCullResult gpuResult;
    /// <summary>The current view's rock draws (one per part of each rock batch it shows), their batches, in order: their arguments follow
    /// the mesh draws' (<see cref="DispatchGpuCull"/>).</summary>
    readonly List<TerrainRenderer.IndirectMesh> rockDraws = [];
    readonly List<int> rockDrawBatch = [];

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
                if (a.RockStamp != stamp) (a.RockStamp, a.RockPlainIndex, a.RockMirroredIndex) = (stamp, -1, -1);
                // The group's first placement's batch first (the order its runs are emitted in).
                for (int pass = 0; pass < 2; pass++)
                {
                    bool m = pass == 0 ? g.RockFirstMirrored : !g.RockFirstMirrored;
                    if (!(m ? g.RockMirrored : g.RockPlain)) continue;
                    ref int index = ref m ? ref a.RockMirroredIndex : ref a.RockPlainIndex;
                    if (index < 0) { index = gpuRockOrder.Count; gpuRockOrder.Add((a, m)); }
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
                        if (g.RockPlain) gpuEntries.Add(new GpuEntry(meshBatches + a.RockPlainIndex, g, start, length, v is not null));
                        if (g.RockMirrored) gpuEntries.Add(new GpuEntry(meshBatches + a.RockMirroredIndex, g, start, length, v is not null));
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
            if (g.RockPlain) gpuEntries.Add(new GpuEntry(meshBatches + a.RockPlainIndex, g, 0, g.Instances.Length, false));
            if (g.RockMirrored) gpuEntries.Add(new GpuEntry(meshBatches + a.RockMirroredIndex, g, 0, g.Instances.Length, false));
        }
        if (gpuBatchStart.Length < batches + 1) { gpuBatchStart = new int[batches * 2 + 1]; gpuCursor = new int[batches * 2]; }
        Array.Clear(gpuBatchStart, 0, batches + 1);
        const int Chunk = FoliageShaders.CullChunk;
        int groups = 0;
        long rockCandidates = 0;
        // Chunks per batch: a mesh group's instances once; a rock group's once per batch it has (a group with both kinds of placement is
        // tested twice, each run keeping only its own kind).
        foreach (var e in gpuEntries)
        {
            int chunks = (e.Length + Chunk - 1) / Chunk;
            gpuBatchStart[e.Batch + 1] += chunks;
            groups++;
            if (e.Batch >= meshBatches && e.Batch < impostorFirst) rockCandidates += e.Length;
        }
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
                uint first = FoliageGpuCull.FirstOf(g.Arena) + (uint)e.Start;
                for (int at = 0; at < length; at += Chunk)
                    gpuChunks[gpuCursor[b]++] = new FoliageCullChunk
                    {
                        First = first + (uint)at, Count = (uint)Math.Min(Chunk, length - at),
                        Range = range.Range, RangeSquared = range.RangeSquared, InverseBand = range.InverseBand, Flags = flags,
                        Transition = range.HasImpostor ? range.Transition : 0, InverseTransitionBand = range.InverseTransitionBand,
                    };
            }
        }
        gpuChunkCount = n;
        gpuInstances = 0;
        foreach (var e in gpuEntries) gpuInstances += e.Length;
        gpuChunkFrame = -1;
        gpuWorkBuilds++;
        gpuWorkGroups += groups;
        gpuWorkCandidates += n * (long)Chunk;
        gpuWorkRockCandidates += rockCandidates;
    }

    /// <summary>The work list's groups as (batch, group), in work-list order (a rock group once per batch it has), and per batch whether this
    /// view draws it (<see cref="ShowBatches"/>).</summary>
    readonly List<GpuEntry> gpuEntries = [];
    bool[] gpuBatchShown = new bool[64];

    /// <summary>One group's records (<c>Start</c> and <c>Length</c> in <c>Group.Instances</c>: all of them, or a rock's run of one biome row) in a batch; <c>ImpostorMesh</c>:
    /// a rock's mesh run whose row has an atlas (its chunks carry the transition).</summary>
    readonly record struct GpuEntry(int Batch, Group Group, int Start, int Length, bool ImpostorMesh);

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
    /// visible instance (every sphere of its groups is outside a plane), so the picture is the same; the GPU still culls its groups, and the
    /// verify mode checks that it found none there.
    /// </summary>
    void ShowBatches()
    {
        int n = gpuOrder.Count + gpuRockOrder.Count + gpuImpostorOrder.Count;
        if (gpuBatchShown.Length < n) gpuBatchShown = new bool[n * 2];
        Array.Clear(gpuBatchShown, 0, n);
        var planes = cullView.Planes;
        foreach (var e in gpuEntries)
            if (!gpuBatchShown[e.Batch] && WorldCamera.Intersects(planes, e.Group.BoundMin, e.Group.BoundMax)) gpuBatchShown[e.Batch] = true;
    }

    /// <summary>The rock draws of this view: per rock batch shown, one draw per part of its mesh (<see cref="Emit"/>'s placements, one per part).</summary>
    void PrepareRocks()
    {
        rockDraws.Clear();
        rockDrawBatch.Clear();
        int meshBatches = gpuOrder.Count;
        for (int r = 0; r < gpuRockOrder.Count; r++)
        {
            if (!gpuBatchShown[meshBatches + r]) continue;
            var (a, mirrored) = gpuRockOrder[r];
            foreach (var gp in a.Main!.Parts)
            {
                rockDraws.Add(new TerrainRenderer.IndirectMesh(PlainMesh(gp), gp.Count, mirrored));
                rockDrawBatch.Add(meshBatches + r);
            }
        }
    }

    /// <summary>Records this view's cull ahead of the frame: the chunks (once a frame per work list), one draw per mesh draw (its batch's
    /// chunks), then one per rock draw.</summary>
    FoliageCullResult DispatchGpuCull(Vector2 eye, TerrainRenderer terrain)
    {
        var cull = gpuCull!;
        var frame = Gpu.Frame;
        if (gpuChunkFrame != frame.Number)
        {
            gpuChunkData = frame.Constants.Write<FoliageCullChunk>(gpuChunks.AsSpan(0, gpuChunkCount), 256);
            gpuChunkFrame = frame.Number;
        }
        int meshes = meshDraws.Count, rocks = meshes + rockDraws.Count, count = rocks + impostorDraws.Count;
        if (gpuDraws.Length < count) gpuDraws = new FoliageCullDraw[count * 2];
        for (int i = 0; i < meshes; i++)
        {
            var d = meshDraws[i];
            gpuDraws[i] = new FoliageCullDraw { IndexCount = (uint)d.Part.Count, ChunkStart = (uint)gpuBatchStart[d.Batch], ChunkEnd = (uint)gpuBatchStart[d.Batch + 1] };
        }
        for (int k = 0; k < rockDraws.Count; k++)
        {
            int b = rockDrawBatch[k];
            gpuDraws[meshes + k] = new FoliageCullDraw { IndexCount = (uint)rockDraws[k].IndexCount, ChunkStart = (uint)gpuBatchStart[b], ChunkEnd = (uint)gpuBatchStart[b + 1] };
        }
        for (int k = 0; k < impostorDraws.Count; k++)
        {
            int b = impostorDraws[k].Batch;
            gpuDraws[rocks + k] = new FoliageCullDraw { IndexCount = 6, ChunkStart = (uint)gpuBatchStart[b], ChunkEnd = (uint)gpuBatchStart[b + 1] };
        }
        var draws = frame.Constants.Write<FoliageCullDraw>(gpuDraws.AsSpan(0, count), 256);
        // What the rocks' row 0 w carries, as the terrain's mesh path writes it: the biome row in colour (resident ones, now), 0 in depth.
        var rock = new FoliageRockView { BiomeRows = !depthPass };
        if (!depthPass && gpuRockOrder.Count > 0) terrain.FeatureResidentBiomes(rock.Resident);
        if (FoliageGpuCull.DrawTally)
        {
            cull.TallyView = (depthPass ? 1 : Gpu.CurrentTargets().Formats.Samples > 1 ? 2 : 0, meshes, rocks);
            // Which mesh each draw is, for the per-mesh tally of the colour views (the benchmark's top list).
            var names = new string[count];
            static string State(MeshAsset a) => a.Impostor switch
            {
                null => EstimateClass(a) is null ? "no impostor class: too small or too few triangles" : "no atlas asked",
                { Stage: ImpostorStage.Ready } => "atlas",
                { Stage: ImpostorStage.None, RetryAt: 0 } => global::Meitou.Rendering.Impostors.ImpostorSource.Ineligible(a.Mesh) is { } why ? $"ineligible: {why}" : "no impostor class (under radius 2, or under 100 triangles below radius 48) or load failed",
                { Stage: ImpostorStage.None } => "atlas refused",
                _ => "atlas pending",
            };
            for (int i = 0; i < meshes; i++) { var a = gpuOrder[meshDraws[i].Batch]; names[i] = $"{a.Mesh.Name} ({a.SizeClass}, {State(a)})"; }
            for (int k = 0; k < rockDraws.Count; k++) { var (a, _) = gpuRockOrder[rockDrawBatch[k] - gpuOrder.Count]; names[meshes + k] = $"{a.Mesh.Name} (rock, {a.SizeClass})"; }
            for (int k = 0; k < impostorDraws.Count; k++) names[rocks + k] = $"{impostorDraws[k].Asset.Mesh.Name} (impostor)";
            cull.TallyNames = names;
        }
        var result = cull.Dispatch(new FoliageCullWork(gpuChunkData, gpuChunkCount, draws, count) { Instances = gpuInstances }, cullView, eye, in rock);
        DrawnInstances += cull.LateVisible;
        if (GpuCullVerify) QueueVerify(result, terrain);
        return result;
    }

    // ---- the verify mode (MEITOU_GPU_CULL_VERIFY=1) ----

    sealed class VerifyCall
    {
        public long Frame;
        public required ReadbackBuffer Buffer;
        public FoliageCullResult Result;
        public required string Kind;
        /// <summary>The CPU's instances per batch (the mesh batches, then the impostor batches; rocks are <see cref="RockExpected"/>).</summary>
        public readonly List<(string Mesh, int Batch, Matrix4x4[] Expected)> Batches = [];
        public int[] BatchStart = [];
        public bool[] Shown = [];
        public FoliageCullDraw[] Draws = [];
        public int[] DrawBatch = [];
        /// <summary>The rock draws (their arguments from <see cref="RockArgs"/> on), and the CPU's placements as the terrain's mesh path
        /// groups and writes them (by mesh part and mirroring, in order; row 0 w the biome row in colour).</summary>
        public TerrainRenderer.IndirectMesh[] RockDraws = [];
        public int RockArgs;
        public readonly Dictionary<TerrainRenderer.IndirectMesh, List<Matrix4x4>> RockExpected = [];
    }

    readonly List<VerifyCall> verifyPending = [];
    long verifyCalls, verifyInstances, verifySets, verifyOrder, verifyArgs, verifyBatchOrder, verifyRocks;
    /// <summary>Fades compared by ulp distance: 0, 1, 2, more.</summary>
    readonly long[] verifyFade = new long[4];
    int verifyLines;

    void VerifyNote(string line)
    {
        if (verifyLines++ < 30) Console.WriteLine($"foliage gpu cull verify: {line}");
    }

    /// <summary>Keeps the CPU's batches of this view (the A1 cull that ran in this Draw) and copies the GPU's lists for reading a frame ring later.</summary>
    void QueueVerify(in FoliageCullResult r, TerrainRenderer terrain)
    {
        bool sameOrder = active.Count == gpuOrder.Count && impostorActive.Count == gpuImpostorOrder.Count;
        for (int i = 0; sameOrder && i < active.Count; i++) sameOrder = ReferenceEquals(active[i].Asset, gpuOrder[i]);
        for (int i = 0; sameOrder && i < impostorActive.Count; i++) sameOrder = ReferenceEquals(impostorActive[i].Asset, gpuImpostorOrder[i]);
        if (!sameOrder)
        {
            verifyBatchOrder++;
            VerifyNote($"frame {Gpu.Frame.Number}: the CPU's batch order differs from the work list's ({active.Count} against {gpuOrder.Count} batches, " +
                $"{impostorActive.Count} against {gpuImpostorOrder.Count} impostor batches)");
            return;
        }
        var v = new VerifyCall { Frame = Gpu.Frame.Number, Result = r, Kind = depthPass ? "depth" : "colour", Buffer = ReadbackBuffer.Create(Gpu, FoliageGpuCull.ReadbackBytes(r), "foliage cull verify") };
        int impostorFirst = gpuOrder.Count + gpuRockOrder.Count, batches = impostorFirst + gpuImpostorOrder.Count;
        for (int i = 0; i < active.Count; i++) v.Batches.Add((active[i].Asset.Mesh.MeshPath, i, active[i].Data.AsSpan(0, active[i].Count).ToArray()));
        for (int i = 0; i < impostorActive.Count; i++)
            v.Batches.Add((impostorActive[i].Asset.Mesh.MeshPath + " impostor", impostorFirst + i, impostorActive[i].Data.AsSpan(0, impostorActive[i].Count).ToArray()));
        v.BatchStart = gpuBatchStart.AsSpan(0, batches + 1).ToArray();
        v.Shown = gpuBatchShown.AsSpan(0, batches).ToArray();
        v.Draws = gpuDraws.AsSpan(0, meshDraws.Count + rockDraws.Count + impostorDraws.Count).ToArray();
        v.DrawBatch = [.. meshDraws.Select(d => d.Batch), .. rockDrawBatch, .. impostorDraws.Select(d => d.Batch)];
        v.RockDraws = [.. rockDraws];
        v.RockArgs = meshDraws.Count;
        // The CPU's rocks (this Draw's terrainDraws) as DrawMeshes would group and write them.
        foreach (var (mesh, indexCount, model) in terrainDraws)
        {
            var key = new TerrainRenderer.IndirectMesh(mesh, indexCount, model.GetDeterminant() < 0);
            if (!v.RockExpected.TryGetValue(key, out var list)) v.RockExpected[key] = list = [];
            var placed = model;
            if (!depthPass) placed.M14 = terrain.FeatureBiomeRow(placed.M41, placed.M43);
            list.Add(placed);
        }
        gpuCull!.CopyForReadback(r, v.Buffer);
        verifyPending.Add(v);
    }

    /// <summary>Compares the lists of every finished frame (<paramref name="all"/>: every pending one; the GPU must be idle).</summary>
    void CheckVerify(bool all)
    {
        for (int i = 0; i < verifyPending.Count; i++)
        {
            var v = verifyPending[i];
            if (!all && !ReadbackBuffer.Completed(Gpu, v.Frame)) continue;
            Compare(v);
            v.Buffer.Dispose();
            verifyPending.RemoveAt(i--);
        }
    }

    static int Ulps(float a, float b)
    {
        int ia = BitConverter.SingleToInt32Bits(a), ib = BitConverter.SingleToInt32Bits(b);
        if (ia < 0) ia = int.MinValue - ia;
        if (ib < 0) ib = int.MinValue - ib;
        return (int)Math.Min(Math.Abs((long)ia - ib), int.MaxValue);
    }

    void Compare(VerifyCall v)
    {
        verifyCalls++;
        var r = v.Result;
        int n = r.ChunkCount;
        var offsets = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(v.Buffer.Read(0, (ulong)(n + 1) * 4));
        ulong argsAt = FoliageGpuCull.Align16((ulong)(n + 1) * 4);
        var args = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(v.Buffer.Read(argsAt, (ulong)r.DrawCount * 20));
        ulong rowsAt = argsAt + FoliageGpuCull.Align16((ulong)r.DrawCount * 20);
        uint total = offsets[n];
        if ((ulong)total * 64 > r.RowsBytes) { verifySets++; VerifyNote($"frame {v.Frame} {v.Kind}: total {total} beyond the {r.RowsBytes / 64} candidates"); return; }
        var rows = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(v.Buffer.Read(rowsAt, (ulong)total * 64));
        foreach (var (mesh, b, expected) in v.Batches)
        {
            uint first = offsets[v.BatchStart[b]], count = offsets[v.BatchStart[b + 1]] - first;
            verifyInstances += expected.Length;
            if (!v.Shown[b] && (count > 0 || expected.Length > 0))
            {
                verifySets++;
                VerifyNote($"frame {v.Frame} {v.Kind}: {mesh}: not drawn (no group's zone in view), yet {count} visible on the GPU, {expected.Length} on the CPU");
                continue;
            }
            if (count != expected.Length)
            {
                verifySets++;
                VerifyNote($"frame {v.Frame} {v.Kind}: {mesh}: {count} visible on the GPU, {expected.Length} on the CPU");
                continue;
            }
            var cpu = System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(expected.AsSpan());
            var gpu = rows.Slice((int)first * 16, (int)count * 16);
            for (int j = 0; j < expected.Length; j++)
            {
                bool same = true;
                for (int e = 0; e < 16; e++)
                    if (e != 3 && BitConverter.SingleToInt32Bits(cpu[j * 16 + e]) != BitConverter.SingleToInt32Bits(gpu[j * 16 + e])) same = false;
                if (!same)
                {
                    verifyOrder++;
                    VerifyNote($"frame {v.Frame} {v.Kind}: {mesh}: instance {j} of {count} is another one (order or set)");
                    continue;
                }
                int ulps = Ulps(cpu[j * 16 + 3], gpu[j * 16 + 3]);
                verifyFade[Math.Min(ulps, 3)]++;
                if (ulps > 0) VerifyNote($"frame {v.Frame} {v.Kind}: {mesh}: instance {j}: fade {cpu[j * 16 + 3]:R} on the CPU, {gpu[j * 16 + 3]:R} on the GPU ({ulps} ulp)");
            }
        }
        for (int d = 0; d < v.Draws.Length; d++)
        {
            int b = v.DrawBatch[d];
            uint first = offsets[v.BatchStart[b]], count = offsets[v.BatchStart[b + 1]] - first;
            if (args[d * 5] != v.Draws[d].IndexCount || args[d * 5 + 1] != count || args[d * 5 + 2] != 0 || args[d * 5 + 3] != 0 || args[d * 5 + 4] != first)
            {
                verifyArgs++;
                VerifyNote($"frame {v.Frame} {v.Kind}: draw {d}: arguments ({args[d * 5]}, {args[d * 5 + 1]}, {args[d * 5 + 2]}, {args[d * 5 + 3]}, {args[d * 5 + 4]}), expected ({v.Draws[d].IndexCount}, {count}, 0, 0, {first})");
            }
        }
        // The rocks: each draw's placements against the CPU's group of the same (mesh part, mirroring), every float bit for bit.
        var drawn = new HashSet<TerrainRenderer.IndirectMesh>();
        for (int k = 0; k < v.RockDraws.Length; k++)
        {
            var key = v.RockDraws[k];
            int d = v.RockArgs + k;
            uint first = args[d * 5 + 4], count = args[d * 5 + 1];
            var expected = v.RockExpected.TryGetValue(key, out var list) ? list : [];
            if (!drawn.Add(key)) { verifySets++; VerifyNote($"frame {v.Frame} {v.Kind}: rock mesh {key.Mesh.GetHashCode():x} drawn twice"); continue; }
            verifyRocks += expected.Count;
            if (count != expected.Count)
            {
                verifySets++;
                VerifyNote($"frame {v.Frame} {v.Kind}: rock mesh {key.Mesh.GetHashCode():x}{(key.Mirrored ? " mirrored" : "")}: {count} placements on the GPU, {expected.Count} on the CPU");
                continue;
            }
            if ((first + count) * 16 > (uint)rows.Length) { verifySets++; VerifyNote($"frame {v.Frame} {v.Kind}: rock draw {k} beyond the rows"); continue; }
            var cpu = System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(expected));
            var gpu = rows.Slice((int)first * 16, (int)count * 16);
            for (int j = 0; j < expected.Count; j++)
                for (int e = 0; e < 16; e++)
                    if (BitConverter.SingleToInt32Bits(cpu[j * 16 + e]) != BitConverter.SingleToInt32Bits(gpu[j * 16 + e]))
                    {
                        verifyOrder++;
                        VerifyNote($"frame {v.Frame} {v.Kind}: rock mesh {key.Mesh.GetHashCode():x}: placement {j} of {count}, float {e}: {cpu[j * 16 + e]:R} on the CPU, {gpu[j * 16 + e]:R} on the GPU");
                        break;
                    }
        }
        foreach (var (key, list) in v.RockExpected)
            if (!drawn.Contains(key) && list.Count > 0)
            {
                verifySets++;
                verifyRocks += list.Count;
                VerifyNote($"frame {v.Frame} {v.Kind}: rock mesh {key.Mesh.GetHashCode():x}: not drawn (no group in view), yet {list.Count} placements on the CPU");
            }
    }

    void ReportVerify()
    {
        if (!GpuCullVerify || gpuCull is null) return;
        if (!Gpu.Device.Frames.InFrame) { Gpu.Device.Frames.WaitAll(); CheckVerify(all: true); }
        else CheckVerify(all: false);   // a frame is open (VkGl opens one for a late upload): the views of the finished frames
        Console.WriteLine($"foliage gpu cull verify: {verifyCalls} views compared, {verifyInstances:N0} visible instances and {verifyRocks:N0} TERRAIN-mode rock placements; visible-set differences {verifySets}, " +
            $"order differences {verifyOrder}, argument differences {verifyArgs}, batch-order differences {verifyBatchOrder}; fades: {verifyFade[0]:N0} equal, " +
            $"{verifyFade[1]:N0} 1 ulp apart, {verifyFade[2]:N0} 2 ulp, {verifyFade[3]:N0} more; {verifyPending.Count} not read");
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

    /// <summary>
    /// Draws the foliage meshes' depth for a shadow cascade: <see cref="Draw"/>'s culling and ranges (measured from the camera's
    /// <paramref name="eye"/>) with <see cref="ShadowShaders.MeshDepthFragment"/>, so the leaves' cut-out holds; no grass. Leaves the draw
    /// counters describing this call.
    /// </summary>
    public void DrawDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, TerrainRenderer terrain, float maxRange = float.PositiveInfinity)
    {
        depthPass = true;
        try { Draw(viewProjection, eye, frustum, options, Vector3.UnitY, Vector3.Zero, 0, terrain, grass: false, maxRange: maxRange); }
        finally { depthPass = false; }
    }

    // ------------------------------------------------------------------ native recording
    // docs/renderer-native.md 3.3 and 7.5, step O: native-model programs (FoliageShaders' native variants: the same maths, the values in
    // FrameConstants / ViewConstants / push constants, the textures by bindless index), recorded into the pass VkGl has open
    // (BeginNativeInPass). Each draw method is split into Prepare (reads the textures and the zones' state, makes a draw list) and Record (the
    // list into one native segment: the sets and dynamic state once, per draw a push constant block and the mesh's own buffers).

    const int ColourKind = 0, DepthKind = 1, GrassKind = 2, MotionKind = 3;
    /// <summary>The first location the per-instance rows (the batch matrices) take; the mesh's own inputs are below it.</summary>
    const int RowLocation = FoliageShaders.InstanceLocation;

    /// <summary>This call's batch matrices in the frame's constants (a batch at its <see cref="Batch.Offset"/>; the draws reach it by firstInstance).</summary>
    Transient instances;

    /// <summary>A mesh's native state for one program: its vertex layout and own vertex buffers, its indices, and the pipelines for the last
    /// two segment states (the reflection's multisampled target alternates with the scene's).</summary>
    struct NativeMesh
    {
        public VertexLayout? Layout;
        public BufferBinding[] Vertices;
        public BufferBinding Elements;
        public int SegA, SegB;
        public GraphicsPipeline? PipeA, PipeB;
    }

    /// <summary>The pipeline state a segment's draws share (everything of <see cref="GraphicsPipelineDesc"/> but the vertex layout).</summary>
    readonly record struct SegmentPipeline(ShaderProgram Program, AttachmentFormats Formats, BlendState Blend, Silk.NET.Vulkan.ColorComponentFlags Mask,
        Silk.NET.Vulkan.PolygonMode Polygon, bool AlphaToCoverage, bool DepthClamp);

    readonly Dictionary<(int Kind, SegmentPipeline State), int> segmentByState = [];
    readonly SegmentPipeline[] lastSegment = new SegmentPipeline[4];
    readonly int[] segmentIds = new int[4];
    int segmentCount;

    /// <summary>A stable number for a segment's pipeline state, so a mesh compares one int per draw.</summary>
    int SegmentId(int kind, ShaderProgram p, PassTargets t, DrawState s)
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
            attributes[RowLocation + a] = new LegacyProgram.Attribute(default, Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, InstanceStride, true);
        n.Layout = p.Layout(attributes);
        n.Vertices = p.Buffers(attributes, p.Own);
        n.Elements = new BufferBinding(part.Indices.Handle, 0, part.Indices.Size);
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    GraphicsPipeline PipelineFor(ref NativeMesh n, NativeProg p, int segment, DrawState state, AttachmentFormats formats, string label)
    {
        if (n.SegA == segment) return n.PipeA!;
        if (n.SegB == segment) return n.PipeB!;
        var pipeline = Gpu.Pipelines.Get(state.Pipeline(p.P, n.Layout!, Silk.NET.Vulkan.PrimitiveTopology.TriangleList, formats, label));
        (n.SegB, n.PipeB) = (n.SegA, n.PipeA);
        (n.SegA, n.PipeA) = (segment, pipeline);
        return pipeline;
    }

    /// <summary>The scene's near depth (<see cref="PostProcess.MotionTargets.NearDepth"/>) in the bindless 2D float array the shaders index: registered
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
    static void Push<T>(CommandList cmd, ShaderProgram p, in T value, ref T last, ref bool pushed) where T : unmanaged
    {
        if (pushed && System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in last)))) return;
        cmd.PushConstants(p.Layout, Silk.NET.Vulkan.ShaderStageFlags.VertexBit | Silk.NET.Vulkan.ShaderStageFlags.FragmentBit, in value);
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

    /// <summary>The mesh draw list in one native segment (colour or depth), back faces culled unless the material is double-sided.</summary>
    void RecordMeshes(NativeProg mp, bool depth, Matrix4x4 viewProjection, Vector3 eye, Vector3 light, Vector3 fogColour, float fogDistance, bool coverage)
    {
        var p = mp.P;
        string label = depth ? "foliage mesh depth" : "foliage meshes";
        // Prepare (wave 4, docs/renderer-native.md 6.2): the pass state, the sets, and per draw everything resolved (pipeline, buffers, the push
        // block with its bindless indices), into a job that only records.
        var targets = Gpu.CurrentTargets();
        // The host's state, with what the GL version set around the meshes: back faces culled (GL's cull face on, its mode never changed from
        // BACK), alpha to coverage with a multisampled target.
        var state = Gpu.CurrentState() with { Cull = Silk.NET.Vulkan.CullModeFlags.BackBit, AlphaToCoverage = coverage };
        int segment = SegmentId(depth ? DepthKind : ColourKind, p, targets, state);
        var job = meshJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Layout, job.Count) = (this, targets, state, p.Layout, 0);
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance };
        job.Frame = nativeFrame.Prepare(in view);
        // The GPU cull's compacted rows and one indirect draw per mesh draw (its batch's count and first instance), else this call's constants.
        bool indirect = !gpuResult.IsEmpty;
        for (int a = 0; a < 4; a++)
            job.Rows[a] = indirect ? new BufferBinding(gpuResult.Rows, gpuResult.RowsOffset + (ulong)(16 * a)) : new BufferBinding(instances.Handle, instances.Offset + (ulong)(16 * a));
        int drawIndex = 0;
        NewTextureSegment();
        // The constants the GL code set on every draw (Shaders.MeshFragment's uniforms the foliage does not vary).
        uint standIn = textureStandIn;
        var pc = new MeshPush
        {
            Tint = Vector3.One, TriplanarScale = 1f / 5000, AlphaChannel = 3, GreyChannel = -1, HeadDiffuse = standIn, HeadNormal = standIn,
            Coverage = coverage ? 1u : 0u,
        };
        var sided = Silk.NET.Vulkan.CullModeFlags.None;
        Silk.NET.Vulkan.CullModeFlags? side = null;
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
            ref var n = ref (depth ? ref part.DepthNative : ref part.ColourNative);
            Current(ref n, part, mp);
            job.Add(new MeshJob.Draw
            {
                SetRaster = raster, Cull = want, Pipeline = n.SegA == segment ? n.PipeA! : PipelineFor(ref n, mp, segment, state, targets.Formats, label),
                Vertices = n.Vertices, Elements = n.Elements, Push = pc,
                Args = indirect ? gpuResult.Args : default, ArgsOffset = gpuResult.ArgsOffset + (ulong)drawIndex * 20,
                IndexCount = (uint)part.Count, Instances = d.Instances, FirstInstance = d.FirstInstance,
            });
            drawIndex++;
        }
        if (impostorDraws.Count > 0) AddImpostorDraws(job, depth, viewProjection, targets, state, coverage, indirect, meshDraws.Count + rockDraws.Count);
        Gpu.Record(label, job);
    }

    readonly JobPool<MeshJob> meshJobs = new();

    /// <summary>One foliage mesh segment as prepared (<see cref="RecordMeshes"/>): recorded on any thread, the same commands as before wave 4.</summary>
    sealed class MeshJob : RecordJob
    {
        public struct Draw
        {
            public bool SetRaster;
            public Silk.NET.Vulkan.CullModeFlags Cull;
            public GraphicsPipeline Pipeline;
            public BufferBinding[] Vertices;
            public BufferBinding Elements;
            public MeshPush Push;
            /// <summary>Indirect: the arguments' buffer (else a plain indexed draw).</summary>
            public Silk.NET.Vulkan.Buffer Args;
            public ulong ArgsOffset;
            public uint IndexCount, Instances, FirstInstance;
        }

        public FoliageRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public Silk.NET.Vulkan.PipelineLayout Layout;
        public FrameBinding Frame;
        public readonly BufferBinding[] Rows = new BufferBinding[4];
        public Draw[] Draws = new Draw[64];
        public int Count;
        public override int Size => Count;

        public void Add(in Draw d)
        {
            if (Count == Draws.Length) Array.Resize(ref Draws, Draws.Length * 2);
            Draws[Count++] = d;
        }

        public override void Record(CommandList cmd)
        {
            var (targets, state, layout) = (Targets, State, Layout);
            cmd.SetViewport(targets.Viewport);
            cmd.SetScissor(targets.Scissor);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            NativeFrame.Record(cmd, layout, in Frame);
            cmd.BindVertexBuffers(RowLocation, Rows);
            MeshPush last = default;
            bool pushed = false;
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                if (d.SetRaster) cmd.SetRaster(d.Cull, state.Front);
                cmd.BindPipeline(d.Pipeline);
                if (d.Vertices.Length > 0) cmd.BindVertexBuffers(0, d.Vertices);
                cmd.BindIndexBuffer(d.Elements, Silk.NET.Vulkan.IndexType.Uint32);
                PushTo(cmd, layout, in d.Push, ref last, ref pushed);
                if (d.Args.Handle != 0) cmd.DrawIndexedIndirect(d.Args, d.ArgsOffset, 1);
                else cmd.DrawIndexed(d.IndexCount, d.Instances, 0, 0, d.FirstInstance);
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
    static void PushTo<T>(CommandList cmd, Silk.NET.Vulkan.PipelineLayout layout, in T value, ref T last, ref bool pushed) where T : unmanaged
    {
        if (pushed && System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in last)))) return;
        cmd.PushConstants(layout, Silk.NET.Vulkan.ShaderStageFlags.VertexBit | Silk.NET.Vulkan.ShaderStageFlags.FragmentBit, in value);
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
        var job = grassJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Layout, job.Count) = (this, targets, state, p.Layout, 0);
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
    static DrawState GrassState(GpuContext gpu, bool coverage) =>
        gpu.CurrentState() with { Cull = Silk.NET.Vulkan.CullModeFlags.None, AlphaToCoverage = coverage };

    /// <summary>The motion pass's state (the host's: colour mask red and green, no depth test, no blending) without culling (GL's cull face off).</summary>
    static DrawState MotionState(GpuContext gpu) => gpu.CurrentState() with { Cull = Silk.NET.Vulkan.CullModeFlags.None };

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
        public Silk.NET.Vulkan.PipelineLayout Layout;
        public FrameBinding Frame;
        public GraphicsPipeline Pipeline = null!;
        public BufferBinding[] Vertices = [];
        public Draw[] Draws = new Draw[64];
        public int Count;
        public override int Size => Count;

        public override void Record(CommandList cmd)
        {
            var (targets, state, layout) = (Targets, State, Layout);
            cmd.SetViewport(targets.Viewport);
            cmd.SetScissor(targets.Scissor);
            cmd.SetRaster(state.Cull, state.Front);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            NativeFrame.Record(cmd, layout, in Frame);
            GrassPush last = default;
            bool pushed = false;
            cmd.BindPipeline(Pipeline);
            cmd.BindVertexBuffers(0, Vertices);
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                PushTo(cmd, layout, in d.Push, ref last, ref pushed);
                cmd.Draw(d.Vertices, d.Instances, 0, d.FirstInstance);
            }
        }

        public override void Release()
        {
            Pipeline = null!;
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
    bool motionCamera, havePrevious;
    readonly List<MotionDraw> motionDraws = [];

    /// <summary>The near depth slice's camera for <see cref="DrawGrassMotion"/>: this frame's view-projection as drawn (jittered) and unjittered.</summary>
    public void SetMotionCamera(Matrix4x4 viewProjection, Matrix4x4 unjittered, Vector3 eye, Vector4[] frustum)
    {
        (motionViewProjection, motionUnjittered, motionEye, motionFrustum) = (viewProjection, unjittered, eye, frustum);
        motionCamera = true;
    }

    /// <summary>
    /// The swaying grass's own motion over the camera motion, for the upscalers (<see cref="PostProcess.ObjectMotion"/> binds the motion target,
    /// red and green only, without depth test or blending): the near slice's blades of layers with wind, now and with last frame's sway phase
    /// and camera, written where they are what the depth buffer shows. Blades without sway move with the camera and need nothing.
    /// </summary>
    public void DrawGrassMotion(PostProcess.MotionTargets targets)
    {
        if (!motionCamera) return;
        motionCamera = false;
        if (!Enabled || debugNoGrass) { havePrevious = false; return; }
        float time = SwayPhase(), previousTime = previousSwayPhase ?? time;
        var previous = havePrevious ? previousUnjittered : motionUnjittered;
        (previousSwayPhase, previousUnjittered, havePrevious) = (time, motionUnjittered, true);
        var eye = motionEye;
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
                    if (b.Shown == 0 || d >= range) continue;
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

    void RecordMotion(PostProcess.MotionTargets targets, Vector3 eye, float time, float previousTime, Matrix4x4 previous)
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
        nativeFrame.Bind(cmd, p.Layout, in view);
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
    Action<CommandList>? recordStamp;
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
            else if (Gpu.Frame.Number - t.Start.Frame <= 2 * Gpu.Device.Frames.Count) continue;   // not collected yet
            timers.RemoveAt(i--);
        }
        if (gpuCount > 0) { GpuMs = gpuSum / gpuCount; gpuSum = 0; gpuCount = 0; }
    }


    public void Dispose()
    {
        grassDisposing = true;   // the pages are freed with the store
        ReportFoliageTiming();
        ReportVerify();
        ReportGrassVerify();
        foreach (var z in zones.Values) { try { z.Job?.Wait(); } catch (AggregateException) { } foreach (var p in z.Pages.Values) { try { p.Job?.Wait(); } catch (AggregateException) { } FreePage(p); } }
        foreach (var a in assetsByMesh.Values)
        {
            try { a.Job?.Wait(); } catch (AggregateException) { }
            foreach (var m in new[] { a.Main, a.Leaves })
                if (m is not null) DeleteMesh(m);
        }
        lock (allWorlds) foreach (var w in allWorlds) w.Dispose();
        textures.Dispose();
        colourMesh.Dispose();
        depthMesh.Dispose();
        grassProgram.Dispose();
        grassMotionProgram.Dispose();
        DisposeGrassStore();
        gpuCull?.Dispose();
        DisposeImpostors();
        nativeFrame.Dispose();
    }
}
