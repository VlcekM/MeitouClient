using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;

using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Impostors;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Foliage;

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
    #region Setup and statistics

    /// <summary>
    /// Grass pages per zone side (576 units each).
    /// </summary>
    internal const int PagesPerZone = 8;
    internal const float PageSize = WorldLayout.ZoneSize / (float)PagesPerZone;
    const int InstanceStride = 64;

    /// <summary>
    /// The largest piece of a buffer written in one upload step (meshes, grass and generated levels).
    /// </summary>
    internal const int SlabBytes = 512 << 10;

    /// <summary>
    /// The meshes' allocation name (the F12 VRAM pie groups by it).
    /// </summary>
    const string MeshAllocationName = "foliage meshes";

    /// <summary>
    /// The game's install (the layout workers' worlds read it).
    /// </summary>
    readonly GameInstall _install;

    /// <summary>
    /// The game's database (the layout workers' worlds read it).
    /// </summary>
    readonly GameDatabase _db;

    /// <summary>
    /// The levels' data (the layout workers' worlds read it).
    /// </summary>
    readonly WorldLevelData _levels;

    /// <summary>
    /// Finds the meshes and textures.
    /// </summary>
    readonly AssetLocator _assets;

    /// <summary>
    /// The foliage layers by biome.
    /// </summary>
    readonly FoliageCatalog _catalog;

    // The native programs (docs/renderer-native.md 3.3, step O: the native model, the same maths), made with the renderer so a draw never compiles.
    /// <summary>
    /// The native frame of the mesh and CPU grass programs.
    /// </summary>
    readonly NativeFrame _nativeFrame;

    /// <summary>
    /// The mesh programs (colour, colour without discards, depth, prepass with and without discards) and the CPU path's grass programs.
    /// colourSolidMesh: colourMesh without the discards (EarlyDepth).
    /// </summary>
    readonly NativeProg _colourMesh, _colourSolidMesh, _depthMesh, _prepassMesh, _prepassSolidMesh, _grassProgram, _grassMotionProgram;

    /// <summary>
    /// The foliage textures (meshes, leaves, grass sprites and colour maps).
    /// </summary>
    readonly WorldTextureCache _textures;

    /// <summary>
    /// The layout workers' worlds not in use (one per layout in flight).
    /// </summary>
    readonly ConcurrentBag<FoliageWorld> _worlds = [];

    /// <summary>
    /// Every layout world made (disposed with the renderer).
    /// </summary>
    readonly List<FoliageWorld> _allWorlds = [];

    /// <summary>
    /// The zones laid out or being laid out.
    /// </summary>
    readonly Dictionary<ZoneCoordinate, FoliageZoneState> _zones = [];

    /// <summary>
    /// The meshes by their record.
    /// </summary>
    readonly Dictionary<FoliageMesh, FoliageMeshAsset> _assetsByMesh = [];

    /// <summary>
    /// The meshes being decoded.
    /// </summary>
    readonly List<FoliageMeshAsset> _decoding = [];

    /// <summary>
    /// The upload steps waiting for the render thread (within a frame budget).
    /// </summary>
    readonly Queue<Action> _uploads = new();

    /// <summary>
    /// What each kind of upload step costs (the budget's estimate).
    /// </summary>
    readonly StepCosts _stepCosts = new();

    /// <summary>
    /// The CPU path's instance batches by mesh, and those used by the current draw.
    /// </summary>
    readonly Dictionary<FoliageMeshAsset, Batch> _batches = [];

    /// <summary>
    /// The batches the current draw filled.
    /// </summary>
    readonly List<Batch> _active = [];

    /// <summary>
    /// The CPU path's TERRAIN-mode rock draws of the current draw.
    /// </summary>
    readonly List<(MeshBindings, int, Matrix4x4)> _terrainDraws = [];

    /// <summary>
    /// The CPU path's TERRAIN-mode rock draws over an interior mask (<see cref="InteriorCut"/>).
    /// </summary>
    readonly List<(MeshBindings, int, Matrix4x4)> _terrainDrawsCut = [];   // those over an interior mask (InteriorCut)

    /// <summary>
    /// The wall clock started with the renderer (the sway without <see cref="SwaySeconds"/>, the eye's velocity).
    /// </summary>
    readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>
    /// The field behind <see cref="Shift"/>.
    /// </summary>
    FoliageShift? _shift;

    /// <summary>
    /// Layouts in flight at most, of each kind (a quarter of the cores, one to three).
    /// </summary>
    readonly int _workers;

    /// <summary>
    /// Zone layouts in flight: whole ones and far-only ones, each limited to <see cref="_workers"/> at a time.
    /// </summary>
    int _runningWhole, _runningFar;

    /// <summary>
    /// The upload budget a frame under the loading screen (<see cref="Covered"/>).
    /// </summary>
    const double CoveredBudgetMs = 16;

    /// <summary>
    /// The GPU cull (null where it is off: Metal without <c>MEITOU_METAL_GPU_CULL</c>).
    /// </summary>
    readonly FoliageGpuCull? _gpuCull;

    /// <summary>
    /// The "Grass draw distance x" setting's default.
    /// </summary>
    public const float DefaultGrassRange = 8;

    /// <summary>
    /// With <see cref="WorldFrame.DetailedStats"/>: the last depth draw's and the frame's main draws' CPU time by step (culling, upload, meshes,
    /// grass, TERRAIN-mode rocks).
    /// </summary>
    internal string DepthDetail = "", MainDetail = "";

    /// <summary>
    /// The field behind <see cref="Guard"/>.
    /// </summary>
    VramGuard? _guard;

    /// <summary>
    /// The native GPU API (docs/renderer-native.md 7.1 step 8; since phase 8 stage 1 the only one this renderer uses).
    /// </summary>
    public IGpuContext Gpu { get; }
    /// <summary>
    /// The zone layout cache (null with <c>--no-load-cache</c>); see <see cref="FoliageLayoutCache"/>.
    /// </summary>
    public FoliageLayoutCache? LayoutCache { get; }
    /// <summary>
    /// Seconds that drive the grass sway; null uses a wall clock started with the renderer. Screenshots fix it (pictures repeat exactly) and the game
    /// sets its own clock.
    /// </summary>
    public double? SwaySeconds { get; set; }

    /// <summary>
    /// A presentation-only move of some placed meshes (the Meitou title's staging): every instance of a mesh whose name
    /// <see cref="FoliageShift.Moves"/> within <see cref="FoliageShift.Radius"/> of <see cref="FoliageShift.Centre"/> (x, z) is carried by
    /// <see cref="FoliageShift.Offset"/> and set on the ground there. Applied as zones are accepted; setting or clearing it lays the zones it touches
    /// out again. Null, the game's placement, in play.
    /// </summary>
    public FoliageShift? Shift
    {
        get => _shift;
        set
        {
            var touched = new[] { _shift, value };
            _shift = value;
            _dropping.Clear();
            foreach (var state in _zones.Values)
                if (state.Job is null && touched.Any(s => s is not null && BoxDistance(state.X0, state.Z0, WorldLayout.ZoneSize,
                        s.Centre with { Y = 0 }) <= s.Radius))
                    _dropping.Add(state);
            foreach (var state in _dropping) Drop(state);
        }
    }

    void ApplyShift(FoliageMesh mesh, FoliageInstanceRecord[] instances)
    {
        if (_shift is not { } s || !s.Moves(mesh.Name)) return;
        for (int i = 0; i < instances.Length; i++)
        {
            ref var r = ref instances[i];
            float x = r.Transform.M41, z = r.Transform.M43;
            if ((x - s.Centre.X) * (x - s.Centre.X) + (z - s.Centre.Z) * (z - s.Centre.Z) > s.Radius * s.Radius)
                continue;
            float nx = x + s.Offset.X, nz = z + s.Offset.Z;
            r.Transform.M41 = nx;
            r.Transform.M42 += s.Ground(nx, nz) - s.Ground(x, z);
            r.Transform.M43 = nz;
            (r.Ground.X, r.Ground.Y) = (nx, nz);
        }
    }
    /// <summary>
    /// Draw the grass / the meshes and impostors (not the TERRAIN-mode rocks) (the bench's `grass` and `foliage-meshes` switches).
    /// </summary>
    public bool DrawGrass { get; set; } = true;
    public bool DrawMeshes { get; set; } = true;
    /// <summary>
    /// The view is under the loading screen (the host sets it each frame): its frames need not be smooth, so enough layouts are in flight to keep
    /// every streaming thread busy between two frames (four per <see cref="BackgroundWork.Threads"/>, not <see cref="_workers"/>: one from the cache
    /// takes a few milliseconds, and finished ones are taken once a frame) and uploads take up to <see cref="CoveredBudgetMs"/> a frame. A town's
    /// thousands of zones out to the Meitou range, three at a time, held its loading screen up seconds with the workers idle.
    /// </summary>
    public bool Covered { get; set; }

    public FoliageRenderer(IGpuContext gpu, GameInstall install, GameDatabase db, WorldLevelData levels, AssetLocator assets)
    {
        Gpu = gpu;
        this._install = install;
        this._db = db;
        this._levels = levels;
        this._assets = assets;
        var watch = Stopwatch.StartNew();
        _catalog = FoliageCatalog.Load(db);
        if (!LoadCaches.Disabled)
        {
            try { LayoutCache = new FoliageLayoutCache(install, _catalog, FoliageWorld.ReadTowns(db, levels)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LayoutCache = null; }
        }
        _nativeFrame = new NativeFrame(gpu);
        _colourMesh = new NativeProg(gpu, _nativeFrame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshFragmentNative(), "foliage meshes");
        _colourSolidMesh = new NativeProg(gpu, _nativeFrame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshFragmentNative(solid: true),
            "foliage meshes solid");
        _depthMesh = new NativeProg(gpu, _nativeFrame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshDepthNative(), "foliage mesh depth");
        _prepassMesh = new NativeProg(gpu, _nativeFrame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshPrepassNative(),
            "foliage mesh prepass");
        _prepassSolidMesh = new NativeProg(gpu, _nativeFrame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshPrepassNative(solid: true),
            "foliage mesh prepass solid");
        _grassProgram = new NativeProg(gpu, _nativeFrame, FoliageShaders.GrassVertexNative(), FoliageShaders.GrassFragmentNative(), "foliage grass");
        _grassMotionProgram = new NativeProg(gpu, _nativeFrame, FoliageShaders.GrassMotionVertexNative(), FoliageShaders.GrassMotionFragmentNative(),
            "foliage grass motion");
        _textures = new WorldTextureCache(gpu, assets, "foliage textures") { Share = TextureShare.For(gpu) };
        _rays = new FoliageRayScene(gpu, _textures, _zones);
        _lodJobs = new FoliageLodJobs(assets, Messages, Decode);

        // Metal's GPU cull and its separate pre-frame command buffer are opt-in after the 2026-10-23 progress
        // timeouts; the CPU cull is the stable reference. Vulkan keeps the GPU path.
        bool metalGpuCull = Environment.GetEnvironmentVariable("MEITOU_METAL_GPU_CULL") is "1" or "true";
        _gpuCull = gpu is GpuContext || metalGpuCull ? new FoliageGpuCull(gpu) : null;
        _grass = new FoliageGrassStore(gpu, _grassProgram, _grassMotionProgram, _uploads, GrassRangeSetting, _areas);
        _workers = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
        var used = _catalog.ByBiome.Values.SelectMany(l => l).Distinct().ToList();
        MeshRange = used.Where(l => !l.IsGrass).Select(l => l.Range).DefaultIfEmpty(0).Max();
        NearMeshRange = used.Where(l => !l.IsGrass && !FoliageLayout.IsFarLayer(l)).Select(l => l.Range).DefaultIfEmpty(0).Max();
        GrassMaxRange = used.Where(l => l.IsGrass).Select(l => l.Range).DefaultIfEmpty(0).Max();
        LoadMs = watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Set by the world frame around the main view's draw only: the meshes may be drawn with the depth prepass (<see cref="FoliagePrepass"/>); the
    /// reflection and the shadow cascades never are.
    /// </summary>
    public bool PrepassView { get; set; }

    /// <summary>
    /// Foliage drawn at all (the F key).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The mesh instances culled on the GPU (docs/renderer-native.md 5.3 to 5.6, step A2): per view, compute kernels write the visible instances and
    /// the indirect draw arguments, the CPU walks only zones and groups. Off is the A1 CPU cull (<see cref="FoliageCull"/>), the reference of the
    /// tests. The TERRAIN-mode rocks go with it: the same dispatch, drawn through <see cref="TerrainRenderer.DrawMeshesIndirect"/> (5.6.1).
    /// </summary>
    public bool GpuCull { get; set; } = true;
    /// <summary>
    /// settings.cfg <c>foliage range</c>, <c>grass range</c> and <c>grass density</c> (the game's defaults are 1; the viewer draws foliage 4x and
    /// grass 8x as far by default).
    /// </summary>
    public float RangeSetting { get; set; } = Env("MEITOU_FOLIAGE_RANGE", 4);
    public float GrassRangeSetting { get; set; } = Env("MEITOU_GRASS_RANGE", DefaultGrassRange);
    public float GrassDensitySetting { get; set; } = 1;
    /// <summary>
    /// The Meitou <c>grass-velocity</c> switch (docs/render-foliage.md "Grass motion vectors"): the motion pass redraws only the swaying blades near
    /// enough to move by <see cref="GrassVelocityPixels"/> a frame (<see cref="GrassMotionReach"/>); off redraws all of them.
    /// </summary>
    public bool GrassVelocityCull { get; set; } = true;
    /// <summary>
    /// Pixels of a blade's own motion a frame under which the camera reprojection stands for it.
    /// </summary>
    public float GrassVelocityPixels { get; set; } = GrassMotionReach.DefaultThresholdPixels;
    /// <summary>
    /// MEITOU_FOLIAGE_RANGE and MEITOU_GRASS_RANGE start the two range settings at other values (offscreen measurements).
    /// </summary>
    static float Env(string name, float fallback) => float.TryParse(Environment.GetEnvironmentVariable(name),
        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>
    /// The longest mesh layer range in the catalog at setting 1 (zones are laid out up to it).
    /// </summary>
    public float MeshRange { get; }
    /// <summary>
    /// The longest grass layer range in the catalog at setting 1.
    /// </summary>
    public float GrassMaxRange { get; }
    public double LoadMs { get; }
    public int DrawnInstances { get; private set; }
    public int DrawnBlades { get; private set; }
    public int DrawCalls { get; private set; }
    public double LastDrawCpuMs { get; private set; }
    /// <summary>
    /// The groups the last CPU cull tested (diagnostics for the parallel path).
    /// </summary>
    public int LastCullGroups { get; private set; }
    /// <summary>
    /// Views the GPU cull dispatched in all (diagnostic; 0 while the CPU path or the Metal GPU cull is off).
    /// </summary>
    public long GpuCullDispatches => _gpuCull?.Dispatched ?? 0;
    /// <summary>
    /// GPU time of the foliage draws of one frame (the main camera's draw, grass included), from timestamp queries a frame or two old.
    /// </summary>
    public double GpuMs { get; private set; }
    public double LastUpdateMs { get; private set; }
    /// <summary>
    /// Streaming diagnostics (the fly benchmark), ground distances from the eye, infinity when none: the nearest zone within the
    /// <see cref="NearReach"/> not laid out whole yet, the nearest within the <see cref="FarReach"/> not laid out at all, and the nearest zone with a
    /// group in range whose mesh is not resident (since the last <see cref="Update"/>).
    /// </summary>
    public float NearestIncompleteZone { get; private set; } = float.PositiveInfinity;
    public float NearestUnlaidZone { get; private set; } = float.PositiveInfinity;
    public float NearestMissingMesh { get; private set; } = float.PositiveInfinity;
    /// <summary>
    /// The nearest grass page of a laid-out zone within four fifths of its range (before the blades sink) that is not uploaded yet.
    /// </summary>
    public float NearestMissingGrass { get; private set; } = float.PositiveInfinity;

    /// <summary>
    /// The <c>range</c> switch (Enhancements): Meitou (true, the default) draws each mesh to the range of its size class
    /// (<see cref="SmallRange"/>, <see cref="MediumRange"/>, <see cref="LargeRange"/>; <see cref="FoliageSizes"/>) instead of its layer's
    /// range × <see cref="RangeSetting"/>. Large meshes of FAR layers keep the longer of the two, so the landmark formations still reach
    /// 8000 × the setting. False: the game's per-layer ranges.
    /// </summary>
    public bool MeitouRange { get; set; } = true;
    /// <summary>
    /// How far small, medium and large meshes are drawn with <see cref="MeitouRange"/> (units along the ground; the Tab sliders).
    /// </summary>
    public float SmallRange { get; set; } = FoliageSizes.DefaultSmallRange;
    public float MediumRange { get; set; } = FoliageSizes.DefaultMediumRange;
    public float LargeRange { get; set; } = FoliageSizes.DefaultLargeRange;
    /// <summary>
    /// The foliage's texture cache (the VRAM dump lists it).
    /// </summary>
    public WorldTextureCache Textures => _textures;

    /// <summary>
    /// The memory-pressure guard (<see cref="VramGuard"/>): every range here (and the reaches that decide which zones are laid out) is multiplied by
    /// its scale, nothing new is started while it is under pressure, and idle meshes are evicted sooner. Null: no clamp.
    /// </summary>
    public VramGuard? Guard
    {
        get => _guard;
        set
        {
            _guard = value;
            _textures.Guard = value;
            if (_gpuCull is not null)
            {
                _gpuCull.MayGrow = value is null ? null : value.Allows;
                _gpuCull.Scratch.MayGrow = value is null ? null
                    : Gpu is GpuContext vk ? (Func<ulong, bool>)(bytes => vk.Device.Allocator.FitsInFreeSpace(bytes) || value.AllowsPriority(bytes))
                    : value.Allows;
            }
        }
    }
    float Scale => _guard?.RangeScale ?? 1f;
    /// <summary>
    /// Seconds an unused mesh stays under pressure (instead of <see cref="IdleSeconds"/>).
    /// </summary>
    public double GuardIdleSeconds { get; set; } = 2;
    float LongestClassRange => Math.Max(Math.Max(SmallRange, MediumRange), LargeRange) * Scale;
    float ClassRange(FoliageSizeClass c) => (c switch { FoliageSizeClass.Small => SmallRange, FoliageSizeClass.Medium => MediumRange,
        _ => LargeRange }) * Scale;

    /// <summary>
    /// Zones within this ground distance of the eye are laid out whole: the longest MEDIUM / CLOSE mesh layer range (with <see cref="MeitouRange"/>:
    /// the longest class range, as any class can be in a MEDIUM layer) or grass range at the current settings.
    /// </summary>
    public float NearReach => Math.Max(MeitouRange ? LongestClassRange : NearMeshRange * RangeSetting * Scale,
        GrassMaxRange * GrassRangeSetting * Scale);
    /// <summary>
    /// Zones within this distance are laid out at least for their far layers (FAR, 8000 x the setting).
    /// </summary>
    public float FarReach => Math.Max(MeshRange * RangeSetting * Scale, NearReach);
    /// <summary>
    /// The longest range of a mesh layer that is not a far layer (<see cref="FoliageLayout.IsFarLayer"/>), at setting 1.
    /// </summary>
    public float NearMeshRange { get; }
    /// <summary>
    /// How far ahead (seconds of the eye's current motion) the layout looks; <see cref="MaxLookahead"/> caps the distance.
    /// </summary>
    public float LookaheadSeconds { get; set; } = 1.5f;
    public List<string> Messages { get; } = [];

    /// <summary>
    /// Requested work: layouts, grass, meshes, textures and impostors, including used textures deferred by memory pressure.
    /// </summary>
    public int Pending =>
        _runningWhole + _runningFar + _zonesWaiting + _grassWaiting + _decoding.Count + _uploads.Count + _lodJobs.Running + _lodJobs.Waiting
        + _textures.AwaitedCount + _textures.UnresidentUsedCount + _zones.Values.Sum(z => z.GrassRunning) + ImpostorPending;

    /// <summary>
    /// <see cref="Pending"/> as a loading screen waits for it: the grass only within the range the default grass distance gives
    /// (<see cref="DefaultGrassRange"/>). A longer setting's farther pages (four times the grass at twice the distance, a minute and more of
    /// generation) grow in, nearest first, once the screen lifts. Of the impostor atlases only the uploads of those read from the cache are waited
    /// for (<see cref="ImpostorUploadsAwaited"/>): the meshes draw until each atlas is ready, and after a mod list change hundreds are baked, 20 s
    /// and more.
    /// </summary>
    public int LoadingPending =>
        Pending - _grassWaiting - _zones.Values.Sum(z => z.GrassRunning) + _grassNear - ImpostorPending + ImpostorUploadsAwaited;

    /// <summary>
    /// What <see cref="Pending"/> is made of, for the loading log.
    /// </summary>
    public string PendingDetail =>
        $"layouts {_runningWhole + _runningFar + _zonesWaiting}, grass {_grassWaiting + _zones.Values.Sum(z => z.GrassRunning)} ({_grassNear} near), "
        + $"meshes {_decoding.Count} decoding {_uploads.Count} upload steps, lods {_lodJobs.Running + _lodJobs.Waiting}, "
        + $"textures {_textures.AwaitedCount} loading {_textures.UnresidentUsedCount} deferred, "
        + $"impostors {ImpostorPending} ({ImpostorUploadsAwaited} cache uploads)";

    /// <summary>
    /// GPU memory held by foliage meshes, textures and grass pages.
    /// </summary>
    public long ResidentBytes => _residentMeshBytes + _textures.ResidentBytes + GrassBytes() + _impostorBytes;
    public string ResidentDescription =>
        $"{_residentMeshBytes / 1048576.0:0} MB in {_assetsByMesh.Values.Count(a => a.Resident)} meshes ({_meshUnloads} unloaded, " +
        $"{_meshReloads} reloaded), {_textures.Describe()}, {GrassBytes() / 1048576.0:0} MB of grass pages, {ImpostorDescription}";
    /// <summary>
    /// The GPU cull's and the grass kernels' per-view memory (<see cref="FrameScratch"/>): held now, the need per frame (mean, peak, largest demand),
    /// what was refused. For the benchmark and the statistics.
    /// </summary>
    public string ScratchDescription
    {
        get
        {
            static string One(string name, FrameScratch s) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{name} {s.AllocatedBytes / 1048576.0:0.0} MB held, need per frame mean {s.MeanNeed / 1048576.0:0.0} " +
                $"/ peak {s.PeakNeed / 1048576.0:0.0} MB (demand peak {s.PeakDemand / 1048576.0:0.0}), cap {s.Cap / 1048576.0:0} MB per slot, " +
                $"{s.Overflows} refused in {s.OverflowFrames} frames, {s.Rebuilds} rebuilds ({s.KeptOnRefusal} kept for a refusal)");
            return (_gpuCull is { } g
                    ? One("cull", g.Scratch) + $"; arena {g.ArenaUsed / 1048576.0:0.0} of {g.ArenaBytes / 1048576.0:0} MB " +
                        $"at {FoliageInstanceRecord.GpuSize} B a record ({g.PackMismatches} not lossless)"
                    : "no GPU cull")
                + "; " + (_grass.Store.Scratch is { } grassScratch ? One("grass", grassScratch) : "grass store (CPU)");
        }
    }

    public void ResetDrawTally() => _gpuCull?.ResetTally();

    /// <summary>
    /// The indirect draws per frame by instance count (0, 1-2, 3-15, 16+) for the colour views, the shadow cascades (all cascades summed) and the
    /// reflection, from the tally.
    /// </summary>
    public IEnumerable<(string Name, double Value)>? DrawHistogram()
    {
        if (_gpuCull is not { TallyFrames: > 0 } g) return null;
        double n = g.TallyFrames;
        string[] kinds = ["colour", "shadow", "reflection"], buckets = ["0", "1-2", "3-15", "16+"];
        return Enumerable.Range(0, 3).SelectMany(k => Enumerable.Range(0, 4).Select(b => ($"hist {kinds[k]} draws {buckets[b]} inst",
            g.TallyHistogram[k, b] / n)));
    }

    /// <summary>
    /// The main colour views' foliage triangles per frame since <see cref="ResetDrawTally"/> (meshes, TERRAIN-mode rocks, impostor quads as two),
    /// from the GPU cull's indirect arguments (<see cref="FoliageGpuCull.CountTriangles"/>); null before a tallied frame has been read back.
    /// </summary>
    public double? ColourTrianglesPerFrame =>
        _gpuCull is { TallyFrames: > 0 } g ? (g.Tally[0, 0] + g.Tally[0, 2] + 2.0 * g.Tally[0, 4]) / g.TallyFrames : null;

    public string Describe() =>
        $"{_zones.Values.Count(z => z.Ready)} zones laid out ({_zones.Values.Where(z => z.Ready).Sum(z => z.Instances):N0} meshes, " +
        $"{_zones.Values.Sum(z => z.Pages.Count):N0} grass pages), {_assetsByMesh.Count} foliage meshes " +
        $"({_assetsByMesh.Values.Count(a => a.Resident)} resident), catalog of {_catalog.Layers.Count} layers"
        + (Lod && _lodJobs.AnyMade ? $"; generated levels: {LodSummary}" : "");

    #endregion

    #region Streaming

    /// <summary>
    /// The longest look ahead along the eye's motion (<see cref="LookaheadSeconds"/>), and the longest step taken for a motion (a jump is not one).
    /// </summary>
    const float MaxLookahead = 2 * WorldLayout.ZoneSize;

    /// <summary>
    /// Whole layouts reach this much beyond the near reach even at rest, so a start at full speed (or a turn) finds the next ring ready.
    /// </summary>
    const float PrefetchMargin = WorldLayout.ZoneSize / 2;

    /// <summary>
    /// The eye at the last <see cref="Update(Vector3)"/>, when it was there, and its smoothed ground velocity.
    /// </summary>
    Vector3? _lastEye;
    double _lastEyeTime;
    Vector2 _velocity;

    /// <summary>
    /// This update's wanted layouts, the zones dropped and the zones looked at (kept to reuse their storage).
    /// </summary>
    readonly List<(ZoneCoordinate Zone, FoliageZoneTier Tier, float Urgency, bool ForKept)> _wanted = [];
    readonly List<FoliageZoneState> _dropping = [];
    readonly HashSet<ZoneCoordinate> _candidates = [];

    /// <summary>
    /// The field behind <see cref="Areas"/>.
    /// </summary>
    int _areas = 1;

    /// <summary>
    /// Follows the eye: lays out zones, decodes meshes, makes grass pages and uploads, within a frame budget.
    /// </summary>
    public void Update(Vector3 eye) => Update(eye, settling: false);

    /// <summary>
    /// The arrival eye of a character flight (docs/render-objects.md "Arrival prefetch"), or null: zones within the near reach of it are laid out and
    /// kept as from the eye, and ahead of everything else, so the landing view's foliage loads during the flight (the grass pages wait for the eye).
    /// </summary>
    public Vector3? Ahead { get; set; }

    /// <summary>
    /// Points kept loaded besides the eye's (the player's characters, "Keep textures at characters", docs/render-objects.md "Kept points"): zones
    /// within the near reach of one are laid out and kept, after the eye's, and their meshes and textures stay loaded, and grass pages are made there
    /// (<see cref="UpdateGrass"/>).
    /// </summary>
    public IReadOnlyList<Vector3> Kept { get; set; } = [];

    /// <summary>
    /// The camera's distance to its target: a kept point's texture needs count from at least this far (the view a jump there would have).
    /// </summary>
    public float KeptViewDistance { get; set; }

    /// <summary>
    /// Places kept loaded (<see cref="WorldTextureCache.Areas"/>): the foliage textures' mark scales with it, and the grass store
    /// (<see cref="GrassStoreScale"/>) within its caps.
    /// </summary>
    public int Areas { get => _areas; set { _areas = Math.Max(value, 1); _textures.Areas = _areas; } }

    float NearKeptZone(ZoneCoordinate zone)
    {
        float d = float.PositiveInfinity;
        var kept = Kept;
        for (int i = 0; i < kept.Count; i++) d = MathF.Min(d, ZoneDistance(zone, kept[i]));
        return d;
    }

    /// <summary>
    /// The ground's height under the eye (the host sets it each frame before <see cref="Update(Vector3)"/>; unknown: none). Which zones and grass
    /// pages are laid out and kept counts the eye's height above the game's highest camera (<see cref="Load"/>), so a free camera high above the land
    /// loads only what can be seen from there; drawing still measures across the ground.
    /// </summary>
    public float GroundUnderEye { get; set; } = float.NegativeInfinity;

    /// <summary>
    /// A ground distance from the eye as the loading measures it: with the eye's height above <see cref="GroundUnderEye"/> beyond the game's camera
    /// heights (<see cref="Meitou.Data.World.KenshiCamera.MaxHeightAbovePivot"/>) added as a third side, so those heights load as before.
    /// </summary>
    float Load(float groundDistance, Vector3 eye)
    {
        return EyeLift.Apply(groundDistance, EyeLift.Of(eye, GroundUnderEye));
    }

    void Update(Vector3 eye, bool settling)
    {
        _updates++;
        var watch = Stopwatch.StartNew();

        // Uploads get 2 ms a frame (texture decodes are never waited for) except while settling for a screenshot.
        double budget = settling ? 1e9 : Covered ? CoveredBudgetMs : 2.0;
        int layouts = Covered ? Math.Max(_workers, 4 * BackgroundWork.Threads) : _workers;

        // Two tiers (docs/render-foliage.md): within the near reach (the MEDIUM layers and the grass) a zone is laid out whole; beyond it, out
        // to the far reach, only its FAR layers, which skips the grass coverage, nine tenths of a zone's layout. A far zone is laid out whole once
        // it comes within the near reach (its far instances are the same either way, FoliageLayout.Place).
        // With the Meitou range switch a third tier sits between them (FoliageZoneTier.Meshes): beyond the whole reach (the longest of the small,
        // medium and grass ranges) and within the near reach only the large meshes can be drawn, so such a zone keeps those and nothing else.
        float nearReach = NearReach, farReach = FarReach, wholeReach = WholeReach;
        AcceptLayouts();
        DropFarZones(eye, settling, nearReach, farReach);

        var predicted = PredictEye(eye, settling, out var ahead);
        var arrival = settling ? null : Ahead;
        float arrivalReach = nearReach + PrefetchMargin;
        var keptPoints = settling ? [] : Kept;
        GatherCandidates(eye, farReach + ahead.Length(), arrival, arrivalReach, keptPoints);
        RankWanted(eye, predicted, arrival, arrivalReach, keptPoints, (nearReach, farReach, wholeReach));
        StartLayouts(layouts);
        NearestMissingMesh = float.PositiveInfinity;

        UpdateGrass(eye, keptPoints, settling);
        PumpMeshes();
        PumpLods();
        Prune();
        TrimResident(eye, force: settling);
        _textures.Pump(wait: settling, max: settling ? 64 : Covered ? 8 : 1);
        RunUploads(watch, budget);
        FlushMessages();
        UpdateImpostors(eye, settling);
        PollTimers(wait: false);
        if (!settling && watch.Elapsed.TotalMilliseconds > 8)
            SpikeLog.Note("slow foliage update", ms: watch.Elapsed.TotalMilliseconds);
        LastUpdateMs = watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// The finished layout jobs into their zones.
    /// </summary>
    void AcceptLayouts()
    {
        foreach (var state in _zones.Values)
        {
            if (state.Job is not { IsCompleted: true } job) continue;

            if (state.JobTier != FoliageZoneTier.Far) _runningWhole--;
            else
                _runningFar--;
            state.Job = null;
            try
            {
                Accept(state, job.Result, state.JobTier);
            }
            catch (AggregateException e)
            {
                Messages.Add($"foliage zone {state.Zone}: {e.InnerException?.Message ?? e.Message}");
                state.Ready = state.Complete = state.MeshLayers = true;
            }
            _grass.Changed();
        }
    }

    /// <summary>
    /// Drops the zones beyond the far reach (and the arrival's and the kept points' reach) that no job is laying out.
    /// </summary>
    void DropFarZones(Vector3 eye, bool settling, float nearReach, float farReach)
    {
        _dropping.Clear();
        foreach (var state in _zones.Values)
            if (state.Job is null && Load(ZoneDistance(state.Zone, eye),
                    eye) > farReach + WorldLayout.ZoneSize && !(Ahead is { } arrivalAt && ZoneDistance(state.Zone,
                    arrivalAt) <= nearReach + WorldLayout.ZoneSize)
                && !(!settling && Kept.Count > 0 && NearKeptZone(state.Zone) <= nearReach + WorldLayout.ZoneSize))
                _dropping.Add(state);
        foreach (var state in _dropping) Drop(state);
    }

    /// <summary>
    /// Look ahead along the motion: the work is ordered by how soon a zone becomes needed from where the eye will be in a second
    /// (<see cref="LookaheadSeconds"/>), and whole layouts are started for zones that will be within the near reach by then. A jump (a camera code,
    /// a teleport) is not a motion: the step is capped. Returns where the eye will be (the eye itself while settling) and the step there.
    /// </summary>
    Vector3 PredictEye(Vector3 eye, bool settling, out Vector2 ahead)
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (_lastEye is { } previous && now > _lastEyeTime)
        {
            var step = new Vector2(eye.X - previous.X, eye.Z - previous.Z);
            float dt = (float)(now - _lastEyeTime);
            var v = step.Length() > MaxLookahead ? Vector2.Zero : step / dt;
            float k = Math.Clamp(dt / 0.25f, 0, 1);   // smoothed over about a quarter second
            _velocity = Vector2.Lerp(_velocity, v, k);
        }
        (_lastEye, _lastEyeTime) = (eye, now);
        ahead = _velocity * LookaheadSeconds;
        if (ahead.Length() > MaxLookahead) ahead = Vector2.Normalize(ahead) * MaxLookahead;
        return settling ? eye : eye + new Vector3(ahead.X, 0, ahead.Y);
    }

    /// <summary>
    /// The zones that could be wanted: those within <paramref name="eyeReach"/> of the eye's zone, and around the arrival and the kept points.
    /// </summary>
    void GatherCandidates(Vector3 eye, float eyeReach, Vector3? arrival, float arrivalReach, IReadOnlyList<Vector3> keptPoints)
    {
        _wanted.Clear();
        var centre = WorldLayout.ZoneOf(eye.X, eye.Z);
        int reach = (int)Math.Ceiling(eyeReach / WorldLayout.ZoneSize) + 1;
        NearestIncompleteZone = NearestUnlaidZone = float.PositiveInfinity;
        _candidates.Clear();
        for (int dz = -reach; dz <= reach; dz++)
            for (int dx = -reach; dx <= reach; dx++)
                _candidates.Add(new ZoneCoordinate(centre.X + dx, centre.Y + dz));
        if (arrival is { } at)
        {
            var landing = WorldLayout.ZoneOf(at.X, at.Z);
            int around = (int)Math.Ceiling(arrivalReach / WorldLayout.ZoneSize) + 1;
            for (int dz = -around; dz <= around; dz++)
                for (int dx = -around; dx <= around; dx++)
                    _candidates.Add(new ZoneCoordinate(landing.X + dx, landing.Y + dz));
        }
        foreach (var point in keptPoints)
        {
            var home = WorldLayout.ZoneOf(point.X, point.Z);
            int around = (int)Math.Ceiling(arrivalReach / WorldLayout.ZoneSize) + 1;
            for (int dz = -around; dz <= around; dz++)
                for (int dx = -around; dx <= around; dx++)
                    _candidates.Add(new ZoneCoordinate(home.X + dx, home.Y + dz));
        }
    }

    /// <summary>
    /// The candidates that lack what their tier needs into <see cref="_wanted"/>, most urgent first, and the nearest incomplete and unlaid zones.
    /// </summary>
    void RankWanted(Vector3 eye, Vector3 predicted, Vector3? arrival, float arrivalReach, IReadOnlyList<Vector3> keptPoints,
        (float Near, float Far, float Whole) reaches)
    {
        var (nearReach, farReach, wholeReach) = reaches;
        bool meitou = MeitouRange;
        FoliageZoneTier TierAt(float soon) => soon <= wholeReach + PrefetchMargin ? FoliageZoneTier.Whole
            : meitou && soon <= nearReach + PrefetchMargin ? FoliageZoneTier.Meshes
            : FoliageZoneTier.Far;
        foreach (var z in _candidates)
        {
            if (!z.IsInsideGrid) continue;

            float d = Load(ZoneDistance(z, eye), eye), dp = Load(ZoneDistance(z, predicted), eye), da = arrival is { } a ? ZoneDistance(z,
                a) : float.PositiveInfinity;
            bool landing = da <= arrivalReach;
            float soon = Math.Min(Math.Min(d, dp), da);
            float dk = keptPoints.Count > 0 ? NearKeptZone(z) : float.PositiveInfinity;
            bool atKept = dk <= arrivalReach;
            if (d > farReach && !landing && !atKept) continue;   // the prediction orders and adds whole layouts, it never adds zones

            var need = TierAt(Math.Min(soon, dk));

            // Wanted more for a kept point than for the eye or the arrival: ranked after all of theirs.
            bool forKept = atKept && (d > farReach && !landing || TierAt(soon) != need);
            _zones.TryGetValue(z, out var state);
            if (state is not null && Satisfies(state, need, meitou)) continue;   // has what it needs

            if (d <= wholeReach && !forKept) NearestIncompleteZone = Math.Min(NearestIncompleteZone, d);
            if (state is not { Ready: true } && !forKept) NearestUnlaidZone = Math.Min(NearestUnlaidZone, d);
            if (state?.Job is not null) continue;   // on its way

            // Whole layouts first (what is missing there is in view near the eye), then the large-only ones, then the far ones, each nearest
            // first (from the eye or where it will be, whichever is nearer).
            // During a flight the arrival's zones go first, then the flight path's (the eye's), each in the same order.
            float tierBase = need == FoliageZoneTier.Whole ? 0 : need == FoliageZoneTier.Meshes ? nearReach : farReach;
            _wanted.Add((z, need, forKept ? (arrival is null ? 2 : 4) * farReach + tierBase + dk
                : arrival is null ? tierBase + soon : landing ? tierBase + da : 2 * farReach + tierBase + Math.Min(d, dp), forKept));
        }
        _wanted.Sort((a, b) => a.Urgency.CompareTo(b.Urgency));
    }

    /// <summary>
    /// Starts the wanted layouts, most urgent first, while fewer than <paramref name="layouts"/> of a kind run; counts those left waiting.
    /// </summary>
    void StartLayouts(int layouts)
    {
        int started = 0, keptWaiting = 0;
        foreach (var w in _wanted) if (w.ForKept) keptWaiting++;
        foreach (var (zone, tier, _, forKept) in _wanted)
        {
            if (_guard is { Streaming: false }) break;   // memory pressure: no new layout is started

            bool whole = tier != FoliageZoneTier.Far;   // a Meshes layout is a whole one whose far-away groups are left out (Accept)
            if (whole ? _runningWhole >= layouts : _runningFar >= layouts) continue;

            if (!_zones.TryGetValue(zone, out var state))
            {
                var (x0, z0) = WorldLayout.ZoneOrigin(zone);
                _zones[zone] = state = new FoliageZoneState { Zone = zone, X0 = (float)x0, Z0 = (float)z0 };
            }
            if (whole) _runningWhole++;
            else
                _runningFar++;
            state.JobTier = tier;
            started++;
            if (forKept) keptWaiting--;
            bool farOnly = !whole;
            Func<GroupedFoliageZone> job = () =>
            {
                if (!_worlds.TryTake(out var world))
                {
                    world = new FoliageWorld(_install, _db, _levels, _catalog, cache: LayoutCache);
                    lock (_allWorlds) _allWorlds.Add(world);
                }
                try
                {
                    // Grouped on the worker (thousands of instances cost 15 to 20 ms on the render thread), straight from the cache file's records
                    // when it has the zone.
                    return world.LoadGrouped(zone, farOnly);
                }
                finally
                {
                    _worlds.Add(world);
                }
            };

            // Whole layouts go ahead of the grass pages and texture decodes queued meanwhile (a dozen grass pages could hold every worker).
            state.Job = whole ? BackgroundWork.RunUrgent(job) : BackgroundWork.Run(job);
        }

        // Needed but not started yet (the workers are busy); a kept point's zones not counted, so a far squad member does not hold up the loading
        // screen.
        _zonesWaiting = _wanted.Count - started - keptWaiting;
    }

    /// <summary>
    /// The queued upload steps within the frame's budget. The budget counts from here: the work before can take it all on a slower machine, and the
    /// uploads would then never run.
    /// </summary>
    void RunUploads(Stopwatch watch, double budget)
    {
        int ranSteps = 0;
        double uploadStart = watch.Elapsed.TotalMilliseconds;
        while (_uploads.Count > 0 && (ranSteps == 0 || watch.Elapsed.TotalMilliseconds - uploadStart < budget))
        {
            // The first step always runs; a later one waits for the next frame when steps of its kind have cost more than what is left plus
            // StepCosts.Slack.
            if (ranSteps > 0 && !_stepCosts.Fits(_uploads.Peek().Method, watch.Elapsed.TotalMilliseconds - uploadStart, budget))
                break;

            var step = _uploads.Dequeue();
            var one = Stopwatch.StartNew();
            step();
            double stepMs = one.Elapsed.TotalMilliseconds;
            _stepCosts.Learn(step.Method, stepMs);
            ranSteps++;
            if (stepMs > 3) SpikeLog.Note($"slow foliage step {step.Method.Name}", ms: stepMs);
        }
    }

    /// <summary>
    /// The update's and the texture cache's messages to the console (20 at most), then cleared.
    /// </summary>
    void FlushMessages()
    {
        lock (_textures.Messages)
        {
            foreach (var m in Messages.Concat(_textures.Messages).Distinct().Take(20)) Console.WriteLine($"warning   {m}");
            Messages.Clear();
            _textures.Messages.Clear();
        }
    }

    /// <summary>
    /// The last <see cref="Settle"/> in numbers: when each kind of work was last still going.
    /// </summary>
    public string SettleTimeline { get; private set; } = "";

    /// <summary>
    /// Waits until everything wanted around <paramref name="eye"/> is laid out, decoded, uploaded and textured (offscreen rendering).
    /// </summary>
    public void Settle(Vector3 eye, int timeoutMs = 300000)
    {
        var watch = Stopwatch.StartNew();
        int rounds = 0;
        double? pressureSince = null;
        var busyUntil = new double[6];
        void Timeline() => SettleTimeline = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"busy until (ms): zone layouts {busyUntil[0]:0}, grass pages {busyUntil[1]:0}, mesh decode and upload {busyUntil[2]:0}, " +
            $"textures {busyUntil[3]:0}, impostors and generated levels {busyUntil[4]:0}");
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            _guard?.Tick();
            Update(eye, settling: true);
            {
                double t = watch.Elapsed.TotalMilliseconds;
                if (_runningWhole + _runningFar + _zonesWaiting > 0) busyUntil[0] = t;
                if (_grassWaiting > 0 || _zones.Values.Sum(z => z.GrassRunning) > 0) busyUntil[1] = t;
                if (_decoding.Count + _uploads.Count > 0) busyUntil[2] = t;
                if (_textures.PendingCount > 0) busyUntil[3] = t;
                if (ImpostorPending + _lodJobs.Running + _lodJobs.Waiting > 0) busyUntil[4] = t;
            }
            if (++rounds > 2 && Pending == 0)
            {
                Timeline();
                return;
            }

            // Under memory pressure the guard holds the loading back: what is missing will not come until it ends, so do not wait for it.
            pressureSince = _guard is { Pressure: true } ? pressureSince ?? watch.Elapsed.TotalSeconds : null;
            if (pressureSince is { } since && watch.Elapsed.TotalSeconds - since > 5)
            {
                Console.WriteLine("warning   foliage streaming stopped waiting: memory pressure (VramGuard)");
                return;
            }

            // An impostor bake reads its rows back after the frame they were recorded into, and atlas uploads are spread over frames: end it.
            // A live host owns its open frame (Metal cannot end and reopen it here): leave the remaining bakes to its later frames.
            if (_impostorBake is { NeedsFrame: true } || _impostorUploadsWaiting)
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

    /// <summary>
    /// A finished layout: its groups replace the zone's (a whole layout after a far one brings the same far instances back with the rest).
    /// </summary>
    void Accept(FoliageZoneState state, GroupedFoliageZone prepared, FoliageZoneTier tier)
    {
        var (zone, ground) = (prepared.Zone, prepared.Ground);

        // With the Meitou range switch a zone beyond the whole reach can only show large meshes (the small and medium ranges and the grass end
        // before it): the other groups would be instances, meshes and textures held for nothing. A group whose mesh is not decoded yet has no
        // size class; it is kept and the prune pass (TrimResident) drops it when the size is known.
        bool filter = MeitouRange && tier != FoliageZoneTier.Whole;
        var groups = new List<FoliageGroup>(prepared.Groups.Count);
        bool unknown = false;
        foreach (var g in prepared.Groups)
        {
            var asset = AssetFor(g.Mesh);
            ApplyShift(g.Mesh, g.Instances);
            if (filter && asset.HasBounds && asset.SizeClass != FoliageSizeClass.Large) continue;
            if (filter && !asset.HasBounds) unknown = true;
            groups.Add(new FoliageGroup
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
            state.PatchTextures[i] = (_textures.Get(p.Grass.Sprite, false), _textures.Get(p.Grass.ColourMap,
                false));   // start decoding now, not at the first draw
        }
        state.Ready = true;
        state.Complete = zone.Complete && !filter;
        state.MeshLayers = zone.Complete || tier != FoliageZoneTier.Far;
        state.Filtered = filter;
        state.NeedsPrune = unknown;
    }

    /// <summary>
    /// Whether a zone as laid out already holds what <paramref name="need"/> asks for (nothing to load again).
    /// </summary>
    static bool Satisfies(FoliageZoneState s, FoliageZoneTier need, bool meitou) => need switch
    {
        FoliageZoneTier.Whole => s.Complete,
        FoliageZoneTier.Meshes => s.Complete || s.MeshLayers,
        _ => s.Ready && (!s.Filtered || meitou),
    };

    /// <summary>
    /// Zones within this ground distance of the eye are laid out whole: with <see cref="MeitouRange"/> the longer of the small and medium ranges and
    /// the grass range (beyond it only large meshes can be drawn, see <see cref="Tier.Meshes"/>), else <see cref="NearReach"/>.
    /// </summary>
    public float WholeReach => MeitouRange ? Math.Max(Math.Max(ClassRange(FoliageSizeClass.Small), ClassRange(FoliageSizeClass.Medium)),
        GrassMaxRange * GrassRangeSetting * Scale) : NearReach;

    /// <summary>
    /// Drops the groups that cannot be drawn in a filtered zone once their mesh sizes are known (they were kept while the mesh was not decoded).
    /// </summary>
    void Prune()
    {
        foreach (var state in _zones.Values)
        {
            if (!state.NeedsPrune || state.Job is not null) continue;
            bool unknown = false;
            foreach (var g in state.Groups)
            {
                if (!g.Asset.HasBounds && !g.Asset.Failed)
                {
                    unknown = true;
                    break;
                }
            }
            if (unknown) continue;
            var keep = new List<FoliageGroup>(state.Groups.Count);
            var drop = new List<FoliageGroup>();
            foreach (var g in state.Groups) (g.Asset.SizeClass == FoliageSizeClass.Large || !g.Asset.HasBounds ? keep : drop).Add(g);
            FreeArena(drop);
            if (drop.Count > 0)
            {
                state.Groups = keep;
                state.MarginStamp = -1;
            }
            state.NeedsPrune = false;
        }
    }

    /// <summary>
    /// The range a group is drawn to at the current <see cref="RangeSetting"/>, and its fade band: the game's transition is 10 units (100 for wind
    /// layers), too short to see, so a tenth of the range instead. With <see cref="MeitouRange"/> the range is the mesh's size class's (the longest
    /// class range until the mesh is decoded and its size known), or for a large mesh of a FAR layer the longer of that and the layer's; the band is
    /// the same rule.
    /// </summary>
    (float Range, float Band) RangeOf(FoliageGroup g)
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

    /// <summary>
    /// The groups' instances leave the GPU cull's arena (after the frames in flight).
    /// </summary>
    void FreeArena(List<FoliageGroup> groups)
    {
        if (_gpuCull is null) return;
        foreach (var g in groups)
        {
            _gpuCull?.Free(g.Arena, g.ArenaGeneration);
            (g.Arena, g.ArenaGeneration) = (default, 0);
        }
    }

    void Drop(FoliageZoneState state)
    {
        FreeArena(state.Groups);
        foreach (var page in state.Pages.Values) _grass.FreePage(page);
        state.Pages.Clear();
        _grass.ReleaseZone(state);
        _zones.Remove(state.Zone);
        _grass.Changed();
    }

    #endregion

    #region Grass pages

    /// <summary>
    /// The highest "Grass density x" the pages are generated for (the settings slider's top): lower settings draw a prefix of the blades.
    /// </summary>
    public const float MaxGrassDensity = 2f;
    /// <summary>
    /// Grass pages generating at once, over all zones; under the loading screen (<see cref="Covered"/>) enough to keep every streaming thread busy
    /// between two frames (a page takes a millisecond or two, and finished ones are only collected once a frame).
    /// </summary>
    const int MaxGrassJobs = 12;
    static readonly int CoveredGrassJobs = Math.Max(MaxGrassJobs, 4 * BackgroundWork.Threads);
    readonly List<(float Distance, FoliageZoneState State, int Key)> _grassWanted = [];
    /// <summary>
    /// An eye page waited for room in the grass store last frame (<see cref="UpdateGrass"/>): kept pages are not queued meanwhile.
    /// </summary>
    bool _grassEyeWaited;
    readonly (float Distance, FoliageZoneState State, int Key)[] _grassPicks = new (float, FoliageZoneState, int)[CoveredGrassJobs];
    int _zonesWaiting, _grassWaiting, _grassNear;

    /// <summary>
    /// The last page walk left nothing in progress (no job, no blades waiting for room, no page without its buffers, none wanted), and what it was
    /// made from: while those stay within <see cref="GrassWalkSlack"/> the walk would decide the same, so it is skipped.
    /// </summary>
    bool _grassSettled;
    (Vector3 Eye, float Ground, float Range, bool Streaming, int Version) _grassWalk;
    Vector3[] _grassWalkKept = [];
    /// <summary>
    /// How far the eye or a kept point may move before the page walk runs again: a quarter page, well inside the half page by which a page is wanted
    /// before its grass range and the two by which it is freed after.
    /// </summary>
    const float GrassWalkSlack = PageSize / 4;

    /// <summary>
    /// A grass layer's range now (its data's, times the range setting and the memory guard's scale).
    /// </summary>
    float GrassRange(FoliageGrassPatch patch) => patch.Layer.Range * GrassRangeSetting * Scale;

    /// <summary>
    /// Whether the last settled page walk still stands (<see cref="_grassSettled"/>): nothing it read has changed beyond the slack.
    /// </summary>
    bool GrassWalkCurrent(Vector3 eye, IReadOnlyList<Vector3> kept)
    {
        var w = _grassWalk;
        if (w.Version != _grass.Version || w.Range != GrassRangeSetting * Scale || w.Streaming != _guard is not { Streaming: false })
            return false;
        if (Vector3.DistanceSquared(eye, w.Eye) > GrassWalkSlack * GrassWalkSlack) return false;
        if (GroundUnderEye != w.Ground && !(MathF.Abs(GroundUnderEye - w.Ground) <= GrassWalkSlack)) return false;
        if (kept.Count != _grassWalkKept.Length) return false;
        for (int i = 0; i < kept.Count; i++) if (Vector3.DistanceSquared(kept[i], _grassWalkKept[i]) > GrassWalkSlack * GrassWalkSlack) return false;
        return true;
    }

    /// <summary>
    /// What one page walk of <see cref="UpdateGrass"/> found: the page jobs running, whether anything is still in progress, whether an eye page waits
    /// for room, and the kept page to free for it (the one farthest from its point).
    /// </summary>
    struct GrassWalkTally
    {
        /// <summary>
        /// The page jobs running (one less for each finished in the walk).
        /// </summary>
        public int InFlight;

        /// <summary>
        /// A page has a job, blades waiting for room or no buffers yet.
        /// </summary>
        public bool Unsettled;

        /// <summary>
        /// A page the eye wants waits for room in the store.
        /// </summary>
        public bool EyeWaiting;

        /// <summary>
        /// The kept page to free for a waiting eye page, its zone and key.
        /// </summary>
        public FoliageGrassPage? Victim;

        /// <summary>
        /// The zone of <see cref="Victim"/>.
        /// </summary>
        public FoliageZoneState? VictimState;

        /// <summary>
        /// The key of <see cref="Victim"/> in its zone.
        /// </summary>
        public int VictimKey;

        /// <summary>
        /// The distance of <see cref="Victim"/> from its kept point.
        /// </summary>
        public float VictimDistance;
    }

    /// <summary>
    /// Makes and frees grass pages round the eye and the kept points (<see cref="Kept"/>). A kept point's pages rank after every page the eye wants,
    /// and they give way to the eye's: while an eye page waits for room in the full store, the kept page farthest from its point is freed (one a
    /// frame).
    /// </summary>
    void UpdateGrass(Vector3 eye, IReadOnlyList<Vector3> kept, bool settling)
    {
        _grass.Grow(GrassRangeSetting, _areas, _guard, _zones);

        // Nothing moving and nothing in progress since the last walk: it would decide the same (its results, NearestMissingGrass and the counts,
        // stand).
        if (!settling && _grassSettled && GrassWalkCurrent(eye, kept)) return;

        _grassWanted.Clear();
        _grassNear = 0;
        _grass.TriedFull = false;
        NearestMissingGrass = float.PositiveInfinity;
        var walk = new GrassWalkTally { VictimDistance = float.NegativeInfinity };
        foreach (var state in _zones.Values) walk.InFlight += state.GrassRunning;
        foreach (var state in _zones.Values) WalkZonePages(state, eye, kept, ref walk);

        _grassEyeWaited = walk.EyeWaiting;
        _grassSettled = !walk.Unsettled && !walk.EyeWaiting && _grassWanted.Count == 0 && walk.InFlight == 0;
        if (_grassSettled)
        {
            _grassWalk = (eye, GroundUnderEye, GrassRangeSetting * Scale, _guard is not { Streaming: false }, _grass.Version);
            if (_grassWalkKept.Length != kept.Count) _grassWalkKept = new Vector3[kept.Count];
            for (int i = 0; i < kept.Count; i++) _grassWalkKept[i] = kept[i];
        }
        if (walk.EyeWaiting && _grass.Full && walk.Victim is not null)
        {
            _grass.FreePage(walk.Victim);
            walk.VictimState!.Pages.Remove(walk.VictimKey);
            _grass.Full = false;   // the waiting page tries the room next frame
        }
        StartGrassPages(walk.InFlight);
    }

    /// <summary>
    /// One zone's pages in the walk: wanted, freed, finished or queued again (a zone without grass, or with no page and none in reach, is skipped).
    /// </summary>
    void WalkZonePages(FoliageZoneState state, Vector3 eye, IReadOnlyList<Vector3> kept, ref GrassWalkTally walk)
    {
        if (!state.Ready || state.Ground is null || state.Patches.Count == 0) return;

        float range = state.Patches.Max(GrassRange), near = range * Math.Min(1, DefaultGrassRange / GrassRangeSetting);
        float keptZone = float.PositiveInfinity;
        foreach (var k in kept) keptZone = MathF.Min(keptZone, ZoneDistance(state.Zone, k));
        if (state.Pages.Count == 0 && Load(ZoneDistance(state.Zone, eye), eye) > range + PageSize && keptZone > range + PageSize)
            return;

        for (int pz = 0; pz < PagesPerZone; pz++)
            for (int px = 0; px < PagesPerZone; px++)
                WalkPage(state, pz * PagesPerZone + px, (range, near, keptZone), eye, kept, ref walk);
    }

    /// <summary>
    /// One page of the walk at <paramref name="key"/> (<paramref name="zone"/>: the zone's grass range, its near part and the nearest kept point).
    /// </summary>
    void WalkPage(FoliageZoneState state, int key, (float Range, float Near, float Kept) zone, Vector3 eye, IReadOnlyList<Vector3> kept,
        ref GrassWalkTally walk)
    {
        var (range, near, keptZone) = zone;
        int px = key % PagesPerZone, pz = key / PagesPerZone;
        float x0 = state.X0 + px * PageSize, z0 = state.Z0 + pz * PageSize;
        float d = Load(BoxDistance(x0, z0, PageSize, eye), eye), dk = float.PositiveInfinity;
        if (keptZone <= range + PageSize * 2) foreach (var k in kept) dk = MathF.Min(dk, BoxDistance(x0, z0, PageSize, k));
        bool eyeWants = d < range + PageSize * 0.5f;
        state.Pages.TryGetValue(key, out var page);

        // not sinking yet: would be seen
        if (page?.Buffers is null && d < range * 0.8f) NearestMissingGrass = Math.Min(NearestMissingGrass, d);
        if (page is null)
        {
            if (eyeWants)
            {
                _grassWanted.Add((d, state, key));
                if (d < near) _grassNear++;
            }
            else if (dk < range + PageSize * 0.5f) _grassWanted.Add((2 * range + PageSize + dk, state, key));   // after all of the eye's
            return;
        }

        if (eyeWants && page.PendingBlades is not null) walk.EyeWaiting = true;
        if (page.Job is not null || page.PendingBlades is not null || page.Buffers is null)
            walk.Unsettled = true;
        if (!eyeWants && page.Buffers is not null && page.Job is null && dk > walk.VictimDistance && dk <= range + PageSize * 2)
            (walk.Victim, walk.VictimState, walk.VictimKey, walk.VictimDistance) = (page, state, key, dk);
        if (page.Job is null && MathF.Min(d, dk) > range + PageSize * 2)
        {
            _grass.FreePage(page);
            state.Pages.Remove(key);
        }
        else if (page.Job is { IsCompleted: false } && d < near) _grassNear++;
        else if (page.Job is { IsCompleted: true } job)
        {
            state.GrassRunning--;
            walk.InFlight--;   // its slot is free for this frame's picks
            page.Job = null;
            var result = job.Result;

            // While an eye page waits for room, a kept page's blades wait too: the room a freed kept page leaves goes to the eye's.
            if (!eyeWants && _grassEyeWaited)
                (page.PendingBlades, page.PendingFloats, page.PendingPrefixes) = (result.Blades, result.Floats, result.Prefixes);
            else _grass.TryQueuePage(state, key, page, result.Blades, result.Floats, result.Prefixes);
        }
        else if (page.PendingBlades is not null && (eyeWants || !_grassEyeWaited)) _grass.TryQueuePage(state, key, page, page.PendingBlades,
                page.PendingFloats!, page.PendingPrefixes!);   // no room last time
    }

    /// <summary>
    /// Missing pages nearest first, a bounded number at a time. Pages hold the blades of the highest density setting, so changing the setting never
    /// regenerates them (Draw shows a prefix). Only the nearest free jobs' worth is picked: sorting every missing page (tens of thousands at a long
    /// grass range) took milliseconds a frame.
    /// </summary>
    void StartGrassPages(int inFlight)
    {
        _grassWaiting = _grassWanted.Count;
        int free = _guard is { Streaming: false } ? 0 : Math.Max(0, (Covered ? CoveredGrassJobs : MaxGrassJobs) - inFlight), picked = 0;
        foreach (var wanted in _grassWanted)
        {
            if (picked == free && (free == 0 || wanted.Distance >= _grassPicks[picked - 1].Distance)) continue;

            int at = picked < free ? picked++ : picked - 1;
            for (; at > 0 && _grassPicks[at - 1].Distance > wanted.Distance; at--) _grassPicks[at] = _grassPicks[at - 1];
            _grassPicks[at] = wanted;
        }
        for (int i = 0; i < picked; i++)
        {
            var (_, state, key) = _grassPicks[i];
            _grassPicks[i] = default;
            StartGrassPage(state, key);
        }
    }

    /// <summary>
    /// A new page whose blades are made on a worker.
    /// </summary>
    void StartGrassPage(FoliageZoneState state, int key)
    {
        float x0 = state.X0 + key % PagesPerZone * PageSize, z0 = state.Z0 + key / PagesPerZone * PageSize;
        var page = state.Pages[key] = new FoliageGrassPage();
        var patches = state.Patches;
        var ground = state.Ground!;
        state.GrassRunning++;
        _grassWaiting--;
        page.Job = BackgroundWork.Run(() =>
        {
            var blades = new float[patches.Count][];
            var floats = new int[patches.Count];
            var prefixes = new int[patches.Count][];
            for (int i = 0; i < blades.Length; i++)
            {
                blades[i] = FoliageGrassField.RentBladesWithPrefixes(patches[i], ground, x0, z0, PageSize, out prefixes[i], out floats[i],
                        MaxGrassDensity);
            }
            return (blades, floats, prefixes);
        });
    }

    /// <summary>
    /// GPU bytes of the grass pages (blades and the instance data).
    /// </summary>
    long GrassBytes() => _zones.Values.Sum(z => z.Pages.Values.Sum(p => p.Buffers?.Sum(b => (long)b.Count * FoliageGrassField.Stride * 4) ?? 0));

    #endregion

    #region Meshes and materials

    /// <summary>
    /// GPU bytes of the resident meshes (and their levels), and when <see cref="TrimResident"/> last ran (tick).
    /// </summary>
    long _residentMeshBytes, _lastTrim;

    /// <summary>
    /// Counts meshes becoming resident (the zones' cull margins are recomputed after it changes).
    /// </summary>
    int _residentStamp;

    /// <summary>
    /// Meshes unloaded, and loaded again after an unload (the statistics).
    /// </summary>
    int _meshUnloads, _meshReloads;

    FoliageMeshAsset AssetFor(FoliageMesh mesh)
    {
        if (_assetsByMesh.TryGetValue(mesh, out var a)) return a;
        a = _assetsByMesh[mesh] = new FoliageMeshAsset { Mesh = mesh };

        // MapFeatureMode: UV_MAPPED, TRIPLANAR, TERRAIN, DUAL_TEXTURE, FOLIAGE, DUAL_TRIPLANAR, EMISSIVE (the map-feature builder's rules,
        // docs/formats/runtime-materials.md); FOLIAGE cuts out on the normal map's alpha at "alpha threshold" / 255, double-sided.
        int mode = mesh.MaterialType;
        bool dual = mode is 3 or 5;
        a.MainMaterial = new FoliageMaterial(_textures.Get(mesh.Texture, false, deferred: true), _textures.Get(mesh.Normal, false, deferred: true),
            dual ? _textures.Get(mesh.Texture2, false, deferred: true) : null, dual ? _textures.Get(mesh.Normal2, false, deferred: true) : null,
            mode == 4 ? mesh.AlphaThreshold / 255f : 0, mode == 4, mode is 1 or 5, new Vector2(mesh.TileX, mesh.TileY), mode == 6, mesh.SpecularMult,
            (mode is 1 or 5 ? MeshSurface.TriplanarDust : 0u) | (mode == 4 ? MeshSurface.Foliage : 0u));

        // The leaves: their own texture pair, transparent and double-sided, cut out at "leaves alpha threshold" / 255.
        if (mesh.LeavesMesh is not null)
        {
            a.LeavesMaterial = new FoliageMaterial(_textures.Get(mesh.LeavesTexture, false, deferred: true), _textures.Get(mesh.LeavesNormal, false,
                    deferred: true), null, null,
                mesh.LeavesAlphaThreshold / 255f, true, false, Vector2.One, false, 0, MeshSurface.Foliage);
        }
        string main = mesh.MeshPath, leaves = mesh.LeavesMesh ?? "";
        bool weld = a.Weld = MeshWeld.Enabled;
        a.Job = BackgroundWork.Run(() => (DecodeTrimmed(main, mesh, false, weld), leaves.Length > 0 ? DecodeTrimmed(leaves, mesh, true,
            weld) : null));
        _decoding.Add(a);
        return a;
    }

    Model? Decode(string name, bool weld)
    {
        var path = _assets.Find(name) ?? _assets.Find(Path.GetFileName(name.Replace('\\', '/')));
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
        for (int i = 0; i < _decoding.Count; i++)
        {
            var a = _decoding[i];
            if (!a.Job!.IsCompleted) continue;
            _decoding.RemoveAt(i--);
            (Model? main, Model? leaves) = (null, null);
            try { (main, leaves) = a.Job.Result; }
            catch (AggregateException e) { Messages.Add($"foliage mesh {a.Mesh.MeshPath}: {e.InnerException?.Message ?? e.Message}"); }
            a.Job = null;
            if (main is null)
            {
                a.Failed = true;
                continue;
            }
            var min = main.Min;
            var max = main.Max;
            if (leaves is not null)
            {
                min = Vector3.Min(min, leaves.Min);
                max = Vector3.Max(max, leaves.Max);
            }
            a.Centre = (min + max) / 2;
            a.HalfExtent = (max - min) / 2;
            a.Radius = Math.Max((max - min).Length() / 2, 1);
            a.Size = FoliageSizes.Size(a.Radius, a.Mesh);
            a.SizeClass = FoliageSizes.Classify(a.Size);
            a.LargeBillboard = FoliageSizes.LargeBillboard(a.Radius, a.Mesh);
            a.Triangles = main.Parts.Sum(p => p.Indices.Length / 3) + (leaves?.Parts.Sum(p => p.Indices.Length / 3) ?? 0);
            a.HasBounds = true;
            a.TriExtent = FoliageScreenLod.TriangleExtent(Triangles(main).Concat(leaves is null ? [] : Triangles(leaves)));
            a.MainTexel = a.Terrain ? float.PositiveInfinity : TexelScale(main, a.MainMaterial);
            a.LeavesTexel = leaves is null ? float.PositiveInfinity : TexelScale(leaves, a.LeavesMaterial);
            QueueMesh(a, main, leaves);
        }
    }

    /// <summary>
    /// Mip streaming: the world length one unit of the coordinates <paramref name="material"/> samples spans on <paramref name="model"/>, before the
    /// instance's scale: the most stretched part's <see cref="MeshTexelScale"/> over the material's tiling, as for the objects. Infinity (every mip
    /// kept) for a triplanar material, whose footprint has no such bound, and for a part without a usable mapping.
    /// </summary>
    static float TexelScale(Model model, FoliageMaterial? material)
    {
        if (material is null || material.Triplanar) return float.PositiveInfinity;
        float tile = Math.Min(material.Tile.X, material.Tile.Y);
        if (!(tile > 0)) return float.PositiveInfinity;
        float uv = 0;
        foreach (var part in model.Parts) if (part.Indices.Length > 0) uv = Math.Max(uv, MeshTexelScale.Of(part));
        return uv > 0 ? uv / tile : float.PositiveInfinity;
    }

    /// <summary>
    /// The areas of a model's triangles, in mesh units squared.
    /// </summary>
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

    /// <summary>
    /// A mesh's upload as steps of about 512 KB (per part: allocate and set up the vertex arrays, fill the buffers in slabs); the mesh becomes
    /// resident with the last step.
    /// </summary>
    void QueueMesh(FoliageMeshAsset a, Model main, Model? leaves)
    {
        a.Uploading = true;
        var gpuMain = new FoliageGpuMesh();
        var gpuLeaves = leaves is null ? null : new FoliageGpuMesh();
        var bytesTotal = new long[1];
        QueueModel(gpuMain, main, bytesTotal);
        if (leaves is not null) QueueModel(gpuLeaves!, leaves, bytesTotal);
        _uploads.Enqueue(() =>
        {
            a.Main = gpuMain;
            a.Leaves = gpuLeaves;
            a.Bytes = bytesTotal[0];
            a.LastUsed = Environment.TickCount64;
            a.Resident = true;
            a.Uploading = false;
            _residentStamp++;
            _residentMeshBytes += a.Bytes;
        });
    }

    void QueueModel(FoliageGpuMesh mesh, Model model, long[] tally)
    {
        foreach (var part in model.Parts)
        {
            if (part.Indices.Length == 0) continue;
            var p = part;
            FoliageGpuPart? gp = null;

            // The trimmed list (card trimming) follows the original one in the same buffer: the draws pick a range (FoliageRenderer.Trim.cs).
            var allIndices = p.TrimmedIndices is null ? p.Indices : [.. p.Indices, .. p.TrimmedIndices];
            var packed = GpuVertex.PackObject(p.Vertices);
            int vertexBytes = packed.Length, indexBytes = allIndices.Length * sizeof(uint);
            tally[0] += vertexBytes + indexBytes;
            _uploads.Enqueue(() =>
            {
                gp = new FoliageGpuPart
                {
                    Vertices = DeviceBuffer.Create(Gpu, (ulong)vertexBytes, BufferUse.Vertex | _rays.Input, MeshAllocationName),
                    Indices = DeviceBuffer.Create(Gpu, (ulong)indexBytes, BufferUse.Index | _rays.Input, MeshAllocationName),
                    Count = p.Indices.Length,
                    TrimCount = p.TrimmedIndices?.Length ?? 0,
                    HasTangents = p.HasTangents,
                    HasColours = p.HasColours,
                };
                gp.Attributes = VertexAttributes(gp.Vertices);
                mesh.Parts.Add(gp);
            });
            for (int at = 0; at < vertexBytes; at += SlabBytes)
            {
                int start = at, length = Math.Min(SlabBytes, vertexBytes - at);
                _uploads.Enqueue(() =>
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
                _uploads.Enqueue(() =>
                {
                    Gpu.EnsureFrame();
                    {
                        using var batch = Gpu.Uploads.Begin();
                        batch.Write(gp!.Indices, (ulong)start, MemoryMarshal.AsBytes(allIndices.AsSpan()).Slice(start, length));
                    }
                });
            }
        }
    }

    /// <summary>
    /// Meshes not needed (no loaded zone has a group of them within its range) for this long are unloaded.
    /// </summary>
    public double IdleSeconds { get; set; } = 60;

    /// <summary>
    /// Once a second: marks the meshes (and their textures) of every zone group within its range as in use, whatever the camera looks at, and
    /// unloads the GPU meshes unused for <see cref="IdleSeconds"/>. A mesh needed again is decoded and uploaded afresh (<see cref="Reload"/>).
    /// The same pass gives the textures their mip streaming need: the group's nearest instance over the texel size at its largest scale, offered
    /// before the keys are read so a first load already drops what the view cannot sample.
    /// </summary>
    void TrimResident(Vector3 eye, bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastTrim < 1000) return;
        _lastTrim = now;
        foreach (var state in _zones.Values)
        {
            if (!state.Ready) continue;

            // In range of the eye or of a kept point; a kept point's texture needs count from at least the camera's distance (KeptViewDistance).
            float eyeZone = ZoneDistance(state.Zone, eye), keptZone = Kept.Count > 0 ? NearKeptZone(state.Zone) : float.PositiveInfinity;
            float zoneDistance = MathF.Min(eyeZone, keptZone);
            foreach (var g in state.Groups)
            {
                if (zoneDistance > RangeOf(g).Range + WorldLayout.ZoneSize * 0.5f) continue;
                var a = g.Asset;
                a.LastUsed = now;
                if (!a.Resident) Reload(a);   // in range but unloaded (or never loaded after an unload): decode it again, wherever the camera looks
                if (!a.HasBounds) continue;
                float near = g.BoundsReady ? BoxDistance(g.BoundMin, g.BoundMax, eye) : eyeZone, scale = Math.Max(g.MaxScale, 1e-6f);
                if (float.IsFinite(keptZone))
                {
                    float kept = keptZone;
                    if (g.BoundsReady) { kept = float.PositiveInfinity; foreach (var k in Kept) kept = MathF.Min(kept, BoxDistance(g.BoundMin,
                            g.BoundMax, k)); }
                    near = MathF.Min(near, MathF.Max(kept, KeptViewDistance));
                }
                Offer(a.MainMaterial, near / (a.MainTexel * scale));
                Offer(a.LeavesMaterial, near / (a.LeavesTexel * scale));
                foreach (var m in (ReadOnlySpan<FoliageMaterial?>)[a.MainMaterial, a.LeavesMaterial])
                {
                    // reading a key counts as use (WorldTexture.Key)
                    if (m is not null) _ = (m.Diffuse?.Key, m.Normal?.Key, m.Diffuse2?.Key, m.Normal2?.Key);
                }
            }
        }
        _textures.CommitNeeds();
        _textures.Rebalance();
        foreach (var a in _assetsByMesh.Values)
        {
            if (!a.Resident || (now - a.LastUsed) / 1000.0 <= (_guard is { Pressure: true } ? Math.Min(IdleSeconds, GuardIdleSeconds) : IdleSeconds))
                continue;
            foreach (var m in new[] { a.Main, a.Leaves })
                if (m is not null) DeleteMesh(m);
            a.Main = a.Leaves = null;
            DropLods(a);
            a.Resident = false;
            _residentMeshBytes -= a.Bytes;
            a.Bytes = 0;
            _meshUnloads++;
        }
    }

    /// <summary>
    /// A user of the material's textures at <paramref name="need"/> (<see cref="WorldTexture.OfferNow"/>: a texture loaded this pass takes it at
    /// once).
    /// </summary>
    static void Offer(FoliageMaterial? m, float need)
    {
        if (m is null) return;
        m.Diffuse?.OfferNow(need);
        m.Normal?.OfferNow(need);
        m.Diffuse2?.OfferNow(need);
        m.Normal2?.OfferNow(need);
    }

    /// <summary>
    /// The distance from <paramref name="eye"/> to the box (0 inside).
    /// </summary>
    static float BoxDistance(Vector3 min, Vector3 max, Vector3 eye) => Vector3.Distance(eye, Vector3.Clamp(eye, min, max));

    /// <summary>
    /// Tells the foliage textures' mip streaming the camera: the render size in pixels and the vertical field of view (the upscaler's LOD bias is the
    /// context's).
    /// </summary>
    public void SetView(int width, int height, float fieldOfView) => _textures.Mips.SetView(width, height, fieldOfView, Gpu.LodBias);

    void DeleteMesh(FoliageGpuMesh m)
    {
        _rays.MeshDeleted();   // the traced scene drops what it built from these buffers
        foreach (var gp in m.Parts)
        {
            gp.Vertices.Dispose();   // freed after the frames in flight
            gp.Indices.Dispose();
        }
    }

    /// <summary>
    /// Decodes an unloaded mesh again (from <see cref="Draw"/>, when something in range needs it).
    /// </summary>
    void Reload(FoliageMeshAsset a)
    {
        if (a.Resident || a.Failed || a.Job is not null || a.Uploading || _guard is { Streaming: false }) return;
        string main = a.Mesh.MeshPath, leaves = a.Mesh.LeavesMesh ?? "";
        a.LastUsed = Environment.TickCount64;
        bool weld = a.Weld = MeshWeld.Enabled;
        a.Job = BackgroundWork.Run(() => (DecodeTrimmed(main, a.Mesh, false, weld), leaves.Length > 0 ? DecodeTrimmed(leaves, a.Mesh, true,
            weld) : null));
        _decoding.Add(a);
        _meshReloads++;
    }

    /// <summary>
    /// The attributes at locations 0 to 4 (position, normal, texture coordinates, tangent, colour) of the object layout (<see cref="GpuVertex"/>).
    /// </summary>
    internal static LegacyProgram.Attribute?[] VertexAttributes(DeviceBuffer vertices) => GpuVertex.ObjectAttributes(vertices);

    /// <summary>
    /// The part for the terrain's mesh path (the attributes at 0 to 4 and the index buffer); made on first use.
    /// </summary>
    static MeshBindings PlainMesh(FoliageGpuPart gp) => gp.Plain ??= MeshBindings.Of(VertexAttributes(gp.Vertices), gp.Indices);

    #endregion

    #region Drawing

    sealed class Batch
    {
        public required FoliageMeshAsset Asset;
        public Matrix4x4[] Data = new Matrix4x4[64];
        public int Count, Offset;
        /// <summary>
        /// The <see cref="Draw"/> call that last put this batch into <c>active</c> (<see cref="BatchOf"/>), and its place there.
        /// </summary>
        public long Stamp = -1;
        public int Index;

        public void Add(in Matrix4x4 m)
        {
            if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
            Data[Count++] = m;
        }
    }

    /// <summary>
    /// An instance in range of the shadow cascades' eye (its group's records and index), kept for the frame's further cascades (see
    /// <see cref="Draw"/>).
    /// </summary>
    readonly record struct ShadowCandidate(FoliageMeshAsset Asset, FoliageInstanceRecord[] Instances, int Index, float Mesh, float Impostor);
    readonly List<ShadowCandidate> _shadowCandidates = [];
    /// <summary>
    /// The meshes of the recording pass's work list, in order: the further cascades' batch order (<see cref="CullCandidates"/>).
    /// </summary>
    readonly List<(FoliageMeshAsset Asset, int Parts)> _shadowCandidateOrder = [];
    /// <summary>
    /// Counts <see cref="Draw"/> calls (<see cref="Batch.Stamp"/>).
    /// </summary>
    long _drawStamp;
    readonly FoliageCullView _cullView = new();
    long _shadowCandidatesFrame = -1, _updates;
    Vector3 _shadowCandidatesEye;
    float _shadowCandidatesRange;

    /// <summary>
    /// One visible instance: into its mesh's batch, or the terrain's mesh path for TERRAIN-mode rocks.
    /// </summary>
    void Emit(FoliageMeshAsset a, in Matrix4x4 t, float w, WorldRenderOptions options)
    {
        DrawnInstances++;
        if (a.Terrain && options.Textures)
        {
            if (w < 0.5f) return;   // the terrain shader has no dither: the instance goes at the middle of the fade
            // Over an interior mask: drawn with the cut (docs/render-interior-cut.md); the sphere is the mesh's bounds placed.
            var into = _terrainDraws;
            if (!_depthPass && InteriorCut is { Active: true } mask)
            {
                float scale = MathF.Sqrt(MathF.Max(new Vector3(t.M11, t.M12, t.M13).LengthSquared(), MathF.Max(new Vector3(t.M21, t.M22,
                    t.M23).LengthSquared(), new Vector3(t.M31, t.M32, t.M33).LengthSquared())));
                if (mask.Overlaps(Vector3.Transform(a.Centre, t), a.Radius * scale)) into = _terrainDrawsCut;
            }
            foreach (var gp in a.Main!.Parts) into.Add((PlainMesh(gp), gp.Count, t));
            return;
        }
        BatchOf(a).Add(FoliageCull.Packed(t, w));
    }
    /// <summary>
    /// The fog cull (set by the world frame around the main camera's draw only): zones wholly hidden by the fog the eye is in are skipped (meshes and
    /// impostors; the grass is range-limited well inside the hide distance).
    /// </summary>
    internal FogVolumes? FogCull { get; set; }
    /// <summary>
    /// The interior cut (docs/render-interior-cut.md; set by the world frame around its scene colour draw, null otherwise): the meshes and grass over
    /// a mask take it.
    /// </summary>
    internal InteriorMaskRenderer? InteriorCut { get; set; }
    /// <summary>
    /// The last frame's depth pyramid for the occlusion cull (empty: none; set by the world frame around the main camera's draw only, like
    /// <see cref="FogCull"/>).
    /// </summary>
    internal OcclusionView Occlusion { get; set; }
    /// <summary>
    /// Foliage instances the occlusion cull left out of the main colour views (a frame ring late, summed over the frame's views).
    /// </summary>
    public int OccludedInstances { get; private set; }

    /// <summary>
    /// Draws the foliage seen from <paramref name="eye"/> through <paramref name="frustum"/>. Without <paramref name="grass"/> only the meshes (e.g.
    /// for a reflection). <paramref name="maxRange"/> caps every layer's range (the reflection).
    /// </summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, Vector3 light, Vector3 fogColour,
        float fogDistance, TerrainRenderer terrain, bool grass = true, float maxRange = float.PositiveInfinity)
    {
        DrawnInstances = 0;
        DrawnBlades = 0;
        DrawCalls = 0;
        if (!_depthPass) MainDetail = "";
        if (!Enabled) return;

        (_rockTerrain, _drawTextures, _drawMaterialDistance) = (terrain, options.Textures, options.MaterialDistance);
        int timer = grass ? BeginTimer() : -1;
        var cpu = Stopwatch.StartNew();
        _drawStamp++;
        _active.Clear();
        _impostorActive.Clear();
        _terrainDraws.Clear();
        _terrainDrawsCut.Clear();

        // 1. Cull: each instance by its distance along the ground (the game's pages) and its bounding sphere, the groups (a mesh in a zone)
        // in parallel, each into its own list, merged into the batches in order (the same instance order as one pass would give). The shadow
        // cascades of a frame share one pass over the zones (DrawDepth with the same eye and range): it keeps every instance in range, and
        // each cascade then only tests those spheres against its own frustum. The batches are drawn in the order of their mesh's first
        // group in the work list, whatever is visible (step A1 of docs/renderer-native.md: an order a GPU cull can reproduce), minus the empty ones.
        // With the GPU cull (step A2) the CPU walks the same zones and groups into the same work list, and the instances are tested by
        // compute kernels recorded ahead of the frame (FoliageGpuCull); every candidate batch is drawn, in the same order, with the visible
        // count the GPU wrote (0 draws nothing). The shadow cascades of a frame share one work list as they share the candidates above.
        bool gpu = GpuCull && _gpuCull is not null;
        bool cachedDepth = CullForDraw(eye, frustum, options, terrain, maxRange, gpu);
        double tCull = cpu.Elapsed.TotalMilliseconds;
        StageClock.Sub("fol cull");

        // 2. The instances: each batch's matrices contiguous, in this frame's constants (one copy; the draws reach a batch by firstInstance).
        UploadInstances(gpu);
        double tUpload = cpu.Elapsed.TotalMilliseconds;
        StageClock.Sub("fol upload");

        // Alpha to coverage with a multisampled target (the GL version enabled SAMPLE_ALPHA_TO_COVERAGE around the meshes and the grass).
        bool coverage = Gpu.CurrentTargets().Formats.Samples > 1;

        // 3. Meshes.
        DrawMeshesStep((viewProjection, eye, light, fogColour, fogDistance), options, terrain, gpu, coverage);
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
                if (_grassDraws.Count > 0) RecordGrass(viewProjection, eye, light, fogColour, fogDistance, coverage);
            }
        }
        double tGrass = cpu.Elapsed.TotalMilliseconds;
        StageClock.Sub("fol grass");

        // 5. TERRAIN-mode rocks.
        DrawRocks(terrain, gpu);
        StageClock.Sub("fol rocks");
        int rocks = gpu ? _rockDraws.Count : _terrainDraws.Count + _terrainDrawsCut.Count;
        if (WorldFrame.DetailedStats) NoteDrawDetail(cpu, (tCull, tUpload, tMeshes, tGrass), cachedDepth, gpu, rocks);
        EndTimer(timer);
        LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// <see cref="Draw"/> step 1: the batches of this view, from the shadow candidates (a further cascade), the zones (the CPU cull) or the GPU
    /// cull's work list (made again unless a cascade of this frame shares it). Returns whether the shadow candidates were reused.
    /// </summary>
    bool CullForDraw(Vector3 eye, Vector4[] frustum, WorldRenderOptions options, TerrainRenderer terrain, float maxRange, bool gpu)
    {
        bool cachedDepth = _depthPass && _shadowCandidatesFrame == _updates && _shadowCandidatesEye == eye && _shadowCandidatesRange == maxRange;
        _cullView.Set(frustum);

        // With the zone box test in the cascades (ShadowZoneBox) a list is this cascade's alone: the planes are part of the key (each cascade passes
        // its own array).
        bool zoneBox = gpu && _depthPass && ShadowZoneBox.Enabled;
        bool reuseWork = gpu && _depthPass && _gpuWorkFrame == Gpu.Frame.Number && _gpuWorkEye == eye && _gpuWorkRange == maxRange
                         && _gpuWorkTexel == (_lodCoarse ? _lodTexel : 0)
                         && (zoneBox ? ReferenceEquals(_gpuWorkPlanes, frustum) : _gpuWorkPlanes is null);
        if (gpu)
        {
            if (!reuseWork)
            {
                CollectWork(eye, maxRange, boxTest: !_depthPass || zoneBox);
                BuildGpuWork(options, terrain);
            }
            foreach (var a in _gpuOrder) BatchOf(a);
        }
        else
        {
            if (cachedDepth) CullCandidates(options);
            else CullZones(eye, options, maxRange, record: _depthPass);
            _active.RemoveAll(static b => b.Count == 0);
        }
        if (gpu && !reuseWork)
        {
            (_gpuWorkFrame, _gpuWorkEye, _gpuWorkRange, _gpuWorkTexel, _gpuWorkPlanes) = _depthPass ? (Gpu.Frame.Number, eye, maxRange,
                    _lodCoarse ? _lodTexel : 0, zoneBox ? frustum : null) : (-1, default, 0, 0, null);
        }
        _lodView = gpu ? CurrentLodView(eye.Y) : default;
        if (gpu) ShowBatches(eye);
        return cachedDepth;
    }

    /// <summary>
    /// <see cref="Draw"/> step 2: the batches' offsets, and on the CPU path their matrices (and the impostors') into this frame's constants.
    /// </summary>
    void UploadInstances(bool gpu)
    {
        int total = 0;
        foreach (var b in _active)
        {
            b.Offset = total;
            total += b.Count;
        }
        if (!gpu)
        {
            foreach (var b in _impostorActive)
            {
                b.Offset = total;
                total += b.Count;
            }
        }
        if (total <= 0 || gpu) return;

        _instances = Gpu.Frame.AllocateConstants((ulong)(total * InstanceStride), 16);
        var target = _instances.Bytes;
        foreach (var b in _active)
            MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * InstanceStride)..]);
        foreach (var b in _impostorActive)
            MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * InstanceStride)..]);
    }

    /// <summary>
    /// <see cref="Draw"/> step 3, the meshes: Prepare reads the textures (WorldTexture.Key) and makes the draw list, Record puts it into a native
    /// segment of VkGl's pass; the GPU cull is dispatched first when it runs.
    /// </summary>
    void DrawMeshesStep((Matrix4x4 ViewProjection, Vector3 Eye, Vector3 Light, Vector3 FogColour, float FogDistance) view,
        WorldRenderOptions options, TerrainRenderer terrain, bool gpu, bool coverage)
    {
        var (viewProjection, eye, light, fogColour, fogDistance) = view;
        if (_active.Count > 0 && DrawMeshes) PrepareMeshes(options, gpu);
        else _meshDraws.Clear();

        // The GPU path's TERRAIN-mode rocks: one draw per part of each rock batch in view, culled by the same dispatch (after the meshes' draws).
        if (gpu) PrepareRocks();
        else _rockDraws.Clear();
        if (DrawMeshes) PrepareImpostors(gpu);
        else _impostorDraws.Clear();
        if (_impostorDraws.Count == 0) WarmImpostorPipeline(_depthPass, coverage);
        _gpuResult = default;
        if (_meshDraws.Count == 0 && _rockDraws.Count == 0 && _impostorDraws.Count == 0) return;

        if (gpu)
        {
            _gpuResult = DispatchGpuCull(new Vector2(eye.X, eye.Z), terrain, coverage);
            if (_gpuResult.IsEmpty)   // the scratch memory is over its cap: this view has no foliage this frame
            {
                _meshDraws.Clear();
                _rockDraws.Clear();
            }
        }
        if (_meshDraws.Count == 0 && _impostorDraws.Count == 0) return;

        // The depth prepass (FoliagePrepass): the main colour view with the GPU cull only (the reflection and the multisampled targets keep
        // the single colour pass).
        bool prepass = FoliagePrepass.Enabled && PrepassView && !_depthPass && !coverage && !_gpuResult.IsEmpty
                       && (_meshDraws.Count > 0 || ImpostorPrepass.Enabled && _impostorDraws.Count > 0);
        if (prepass)
        {
            RecordMeshes(_prepassMesh, MeshPass.Prepass, viewProjection, eye, light, fogColour, fogDistance, coverage);
            RecordMeshes(_colourSolidMesh, MeshPass.Equal, viewProjection, eye, light, fogColour, fogDistance, coverage);
        }
        else RecordMeshes(_depthPass ? _depthMesh : _colourMesh, _depthPass ? MeshPass.Depth : MeshPass.Colour, viewProjection, eye, light,
                fogColour, fogDistance, coverage);
    }

    /// <summary>
    /// <see cref="Draw"/> step 5: TERRAIN-mode rocks through the terrain's own mesh path: the GPU's placements and arguments, or the CPU's
    /// placements.
    /// </summary>
    void DrawRocks(TerrainRenderer terrain, bool gpu)
    {
        if (gpu)
        {
            if (_rockDraws.Count > 0 && _gpuResult.Split && !_depthPass)
            {
                // The split: the rocks that are not mid-transition with their impostor use the program without the dither's discard; a rock fades
                // only into its impostor.
                var rocksNow = CollectionsMarshal.AsSpan(_rockDraws);
                DrawCalls += terrain.DrawMeshesIndirect(rocksNow, _gpuResult.Rows, _gpuResult.RowsOffset, _gpuResult.Args,
                    _gpuResult.ArgsOffset + (ulong)(_gpuResult.DrawCount + _meshDraws.Count) * 20, false, solid: true);

                // The rest: mid-transition rocks and, with an interior mask drawn, those over it (the cull lays them out with the fading ones), with
                // the cut on.
                if (RockImpostorsActive || InteriorCut is { Active: true })
                {
                    DrawCalls += terrain.DrawMeshesIndirect(rocksNow, _gpuResult.Rows, _gpuResult.RowsOffset, _gpuResult.Args,
                        _gpuResult.ArgsOffset + (ulong)(2 * _gpuResult.DrawCount + _meshDraws.Count) * 20, false, solid: false, cut: InteriorCut);
                }
            }
            else if (_rockDraws.Count > 0)
            {
                DrawCalls += terrain.DrawMeshesIndirect(CollectionsMarshal.AsSpan(_rockDraws), _gpuResult.Rows, _gpuResult.RowsOffset,
                    _gpuResult.Args, _gpuResult.ArgsOffset + (ulong)_meshDraws.Count * 20, _depthPass, cut: _depthPass ? null : InteriorCut);
            }
        }
        else if (_terrainDraws.Count > 0) DrawCalls += terrain.DrawMeshes(_terrainDraws, _depthPass);
        if (!gpu && _terrainDrawsCut.Count > 0) DrawCalls += terrain.DrawMeshes(_terrainDrawsCut, cut: InteriorCut);
    }

    /// <summary>
    /// The detailed stats line of a <see cref="Draw"/>: each step's CPU time (<paramref name="times"/>: when the cull, the upload, the meshes and the
    /// grass ended).
    /// </summary>
    void NoteDrawDetail(Stopwatch cpu, (double Cull, double Upload, double Meshes, double Grass) times, bool cachedDepth, bool gpu, int rocks)
    {
        var (tCull, tUpload, tMeshes, tGrass) = times;
        double tEnd = cpu.Elapsed.TotalMilliseconds;
        string steps = $"cull{(cachedDepth ? " (cached)" : "")} {tCull:0.00}, upload {tUpload - tCull:0.00}, meshes {tMeshes - tUpload:0.00} " +
            $"in {_active.Count} batches, grass {tGrass - tMeshes:0.00}, TERRAIN-mode rocks {rocks}{(gpu ? " draws" : "")} {tEnd - tGrass:0.00}";
        if (_depthPass) DepthDetail = steps;
        else MainDetail += $" [{steps}]";
    }

    #endregion

    #region Culling (Draw step 1)

    /// <summary>
    /// A further cascade's share of the candidates: the visible ones as batch matrices, in runs of one mesh.
    /// </summary>
    sealed class CandidateOutput
    {
        public Matrix4x4[] Visible = new Matrix4x4[64];
        public int Count;
        public readonly List<(FoliageMeshAsset Asset, bool Impostor, int Start, int Count)> Runs = [];
    }

    /// <summary>
    /// The groups a culling pass tests, in order (the batch order follows it), with their range for this view.
    /// </summary>
    readonly List<(FoliageGroup Group, FoliageGroupRange Range, int Parts)> _cullWork = [];
    readonly List<FoliageCullOutput> _cullOutputs = [];

    /// <summary>
    /// Shadow candidates per chunk of a further cascade's parallel cull.
    /// </summary>
    const int CandidateChunk = 2048;

    /// <summary>
    /// The chunks' outputs (kept to reuse their storage).
    /// </summary>
    readonly List<CandidateOutput> _candidateOutputs = [];

    /// <summary>
    /// How far beyond its instances' positions a zone's meshes can reach: the largest (bounds radius + bounds centre offset) x the largest scale
    /// over its groups with decoded meshes (only those are drawn), at least 10 units. Cached until another mesh becomes resident.
    /// </summary>
    float ZoneMargin(FoliageZoneState state)
    {
        if (state.MarginStamp == _residentStamp && ReferenceEquals(state.MarginGroups, state.Groups))
            return state.Margin;
        float margin = 10;
        foreach (var g in state.Groups)
            if (g.Asset.Resident) margin = Math.Max(margin, (g.Asset.Radius + g.Asset.Centre.Length()) * g.MaxScale);
        (state.Margin, state.MarginStamp, state.MarginGroups) = (margin, _residentStamp, state.Groups);
        return margin;
    }

    void CullZones(Vector3 eye, WorldRenderOptions options, float maxRange, bool record)
    {
        if (record) { _shadowCandidates.Clear(); _shadowCandidateOrder.Clear(); (_shadowCandidatesFrame, _shadowCandidatesEye,
                _shadowCandidatesRange) = (_updates, eye, maxRange); }
        CollectWork(eye, maxRange, boxTest: !record);
        CullWork(eye, options, record);
    }

    /// <summary>
    /// The zones and groups in range into <see cref="_cullWork"/>, on this thread (reloads start here). <paramref name="boxTest"/>: each zone's box
    /// against the frustum first (not while recording for the cascades: each has its own frustum).
    /// </summary>
    void CollectWork(Vector3 eye, float maxRange, bool boxTest)
    {
        var frustum = _cullView.Planes;

        // Impostors in colour views, and as casters in the cascades (docs/impostors.md "Shadows").
        bool impostorView = Impostors;
        _cullWork.Clear();
        foreach (var state in _zones.Values)
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
                if (!WorldCamera.Intersects(frustum, new Vector3(state.X0 - m, state.MinY - m, state.Z0 - m),
                        new Vector3(state.X0 + WorldLayout.ZoneSize + m, state.MaxY + m, state.Z0 + WorldLayout.ZoneSize + m)))
                    continue;
                if (FogCull?.Hidden(new Vector3(state.X0 - m, state.MinY - m, state.Z0 - m), new Vector3(state.X0 + WorldLayout.ZoneSize + m,
                        state.MaxY + m, state.Z0 + WorldLayout.ZoneSize + m), FogVolumes.CullKind.Foliage) == true)
                    continue;
            }
            foreach (var g in state.Groups)
            {
                var (full, fullBand) = RangeOf(g);
                float range = Math.Min(full, maxRange), band = range < full ? Math.Min(fullBand,
                    range * 0.25f) : fullBand;   // maxRange: the reflection draws a shorter range
                if (zoneDistance > range) continue;
                if (!g.Asset.Resident)
                {
                    Reload(g.Asset);
                    if (!g.Asset.Failed) NearestMissingMesh = Math.Min(NearestMissingMesh, zoneDistance);
                    continue;
                }
                var (withImpostor, parts) = WithImpostor(FoliageGroupRange.Of(range, band), g, range, band, state, eye, impostorView);
                _cullWork.Add((g, withImpostor, parts));
            }
        }
    }

    /// <summary>
    /// The CPU cull of <see cref="_cullWork"/> (step A1): every instance tested, the visible ones emitted in order.
    /// </summary>
    void CullWork(Vector3 eye, WorldRenderOptions options, bool record)
    {
        // The batch order: each mesh's first group in the work list (TERRAIN-mode rocks have no batch).
        foreach (var (g, _, parts) in _cullWork)
        {
            if (record) _shadowCandidateOrder.Add((g.Asset, parts));
            if ((parts & FoliageCull.MeshPart) != 0 && !(g.Asset.Terrain && options.Textures)) BatchOf(g.Asset);
            if ((parts & FoliageCull.ImpostorPart) != 0) ImpostorBatchOf(g.Asset);
        }
        while (_cullOutputs.Count < _cullWork.Count) _cullOutputs.Add(new FoliageCullOutput());
        LastCullGroups = _cullWork.Count;
        var eyeXz = new Vector2(eye.X, eye.Z);
        var view = _cullView;

        // The Hub has well over a thousand groups; one work item per group spends more time in the job queue than in the
        // short cull. Each chunk writes its groups' own outputs, so the order and results are unchanged.
        const int cullChunk = 16;
        int chunks = (_cullWork.Count + cullChunk - 1) / cullChunk;
        RenderJobs.For(chunks, chunk =>
        {
            int start = chunk * cullChunk, end = Math.Min(start + cullChunk, _cullWork.Count);
            for (int k = start; k < end; k++)
            {
                var (g, range, parts) = _cullWork[k];
                if (!g.SpheresReady)
                {
                    FoliageCull.FillSpheres(g.Instances, g.Asset.Centre,
                        g.Asset.Radius);   // the mesh's bounds are known once it is resident, and never change
                    g.SpheresReady = true;
                }
                FoliageCull.CullGroup(g.Instances, range, eyeXz, view, record, _cullOutputs[k], parts, record ? null : FogCull);
            }
        });
        for (int k = 0; k < _cullWork.Count; k++)
        {
            var output = _cullOutputs[k];
            var g = _cullWork[k].Group;
            if (record)
                for (int i = 0; i < output.InRangeCount; i++)
                {
                    _shadowCandidates.Add(new ShadowCandidate(g.Asset, g.Instances, output.InRange[i], output.InRangeMesh[i],
                            output.InRangeImpostor[i]));
                }
            EmitAll(g.Asset, output.Visible.AsSpan(0, output.Count), options);
            EmitImpostors(g.Asset, output.ImpostorVisible.AsSpan(0, output.ImpostorCount));
            if (output.FogCulled > 0) FogCull?.AddCulled(FogVolumes.CullKind.FoliageInstances, output.FogCulled);
        }
    }

    /// <summary>
    /// A further shadow cascade: the frame's recorded candidates against this cascade's frustum (tested in parallel, emitted in order).
    /// </summary>
    void CullCandidates(WorldRenderOptions options)
    {
        foreach (var (a, parts) in _shadowCandidateOrder)
        {
            if ((parts & FoliageCull.MeshPart) != 0 && !(a.Terrain && options.Textures)) BatchOf(a);
            if ((parts & FoliageCull.ImpostorPart) != 0) ImpostorBatchOf(a);
        }
        int count = _shadowCandidates.Count, chunks = (count + CandidateChunk - 1) / CandidateChunk;
        while (_candidateOutputs.Count < chunks) _candidateOutputs.Add(new CandidateOutput());
        var list = _shadowCandidates;
        var view = _cullView;

        // Each chunk keeps its visible candidates as batch matrices, in runs of one mesh (the candidates were recorded group by group).
        RenderJobs.For(chunks, k =>
        {
            var span = CollectionsMarshal.AsSpan(list);
            var output = _candidateOutputs[k];
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
                    if (output.Runs.Count == 0 || output.Runs[^1].Asset != c.Asset || output.Runs[^1].Impostor != (kind == 1))
                        output.Runs.Add((c.Asset, kind == 1, output.Count, 0));
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
            var output = _candidateOutputs[k];
            foreach (var (asset, impostor, start, n) in output.Runs)
                if (impostor) EmitImpostors(asset, output.Visible.AsSpan(start, n));
                else EmitAll(asset, output.Visible.AsSpan(start, n), options);
        }
    }

    #endregion

    #region The GPU cull (docs/renderer-native.md 5.3 to 5.6, step A2; the TERRAIN-mode rocks, 5.6.1)

    /// <summary>
    /// The GPU work list: its meshes in batch order (each mesh's first group in <see cref="_cullWork"/>, as the CPU's batches), then its TERRAIN-mode
    /// rock batches (<see cref="_gpuRockOrder"/>), its chunks grouped by batch (each batch's groups in work-list order, each group's instances in
    /// order: the CPU's emission order), and each batch's chunk range [<c>gpuBatchStart[b]</c>, <c>gpuBatchStart[b + 1]</c>); rock batch r is batch
    /// <c>gpuOrder.Count + r</c>.
    /// </summary>
    readonly List<FoliageMeshAsset> _gpuOrder = [];
    /// <summary>
    /// The rock batches: a TERRAIN-mode mesh's placements that keep the winding, and those that turn it round (two batches, as
    /// <see cref="TerrainRenderer.DrawMeshes"/> groups them by (mesh part, mirroring)), numbered by their first candidate in the work list.
    /// The CPU's terrain path orders its groups by their first visible placement instead; the order only matters between two different
    /// rocks at exactly the same depth in colour (depth passes keep the nearest whatever the order), as for A1's foliage batches.
    /// </summary>
    readonly List<(FoliageMeshAsset Asset, bool Mirrored, int Level)> _gpuRockOrder = [];
    FoliageCullChunk[] _gpuChunks = new FoliageCullChunk[1024];
    int _gpuChunkCount, _gpuInstances;
    int[] _gpuBatchStart = new int[65], _gpuCursor = new int[64];
    /// <summary>
    /// The chunks in the frame's constants, written once a frame per work list (<see cref="_gpuChunkFrame"/>).
    /// </summary>
    Transient _gpuChunkData;
    long _gpuChunkFrame = -1;
    /// <summary>
    /// The shadow cascades' shared work list: the frame, eye and range it was built for (as the CPU's candidates).
    /// </summary>
    long _gpuWorkFrame = -1;
    Vector3 _gpuWorkEye;
    float _gpuWorkRange;
    /// <summary>
    /// The coarsened cascade's texel the work list was built for (0: not coarsened): its impostor transitions depend on it
    /// (<see cref="ShadowLod.ImpostorTransition"/>).
    /// </summary>
    float _gpuWorkTexel;
    /// <summary>
    /// The cascade's planes the depth work list was box-tested against (<see cref="ShadowZoneBox"/>); null: the list holds every zone in range.
    /// </summary>
    Vector4[]? _gpuWorkPlanes;
    int _gpuWorkStamp;
    FoliageCullDraw[] _gpuDraws = new FoliageCullDraw[64];
    /// <summary>
    /// The current view's cull (rows and indirect arguments), read by <see cref="RecordMeshes"/> and the rocks' draws.
    /// </summary>
    FoliageCullResult _gpuResult;
    /// <summary>
    /// The current view's rock draws (one per part of each rock batch it shows), their batches, in order: their arguments follow the mesh draws'
    /// (<see cref="DispatchGpuCull"/>).
    /// </summary>
    readonly List<TerrainRenderer.IndirectMesh> _rockDraws = [];
    readonly List<int> _rockDrawBatch = [];
    /// <summary>
    /// Each rock draw's first index (a generated level's place in its part's level buffer; 0 for the original).
    /// </summary>
    readonly List<int> _rockDrawFirst = [];

    /// <summary>
    /// The work list's groups as (batch, group), in work-list order (a rock group once per batch it has), and per batch whether this view draws it
    /// (<see cref="ShowBatches"/>).
    /// </summary>
    readonly List<FoliageGpuEntry> _gpuEntries = [];

    /// <summary>
    /// Per batch of the work list: some entry of it can have instances in this view (<see cref="ShowBatches"/>).
    /// </summary>
    bool[] _gpuBatchShown = new bool[64];

    static bool IsRock(FoliageMeshAsset a, WorldRenderOptions options) => a.Terrain && options.Textures;

    /// <summary>
    /// A rock group's records for the GPU: <c>Ground.W</c> = <see cref="FoliageCull.RockBits"/> (the mirroring test and the biome map row,
    /// the same calls the terrain's mesh path makes per frame), made once per biome source (the residency stays per view:
    /// <see cref="FoliageRockView"/>). A group made again leaves the arena, to be placed again with the new bits.
    /// </summary>
    void PrepareRock(FoliageGroup g, TerrainRenderer terrain)
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
        _gpuCull!.Free(g.Arena, g.ArenaGeneration);
        (g.Arena, g.ArenaGeneration) = (default, 0);
    }

    /// <summary>
    /// The work list of <see cref="_cullWork"/> for the GPU: each group's records in the arena, the batch order, the chunks.
    /// </summary>
    void BuildGpuWork(WorldRenderOptions options, TerrainRenderer terrain)
    {
        PlaceWorkGroups(options, terrain);
        OrderGpuBatches(options);
        int meshBatches = _gpuOrder.Count, impostorFirst = meshBatches + _gpuRockOrder.Count, batches = impostorFirst + _gpuImpostorOrder.Count;
        ListGpuEntries(options, meshBatches, impostorFirst);
        BuildGpuChunks(meshBatches, impostorFirst, batches);
    }

    /// <summary>
    /// Each work group's spheres, bounds and (a rock) segments, and its records placed in the arena (all again when the arena grew).
    /// </summary>
    void PlaceWorkGroups(WorldRenderOptions options, TerrainRenderer terrain)
    {
        var cull = _gpuCull!;
        bool placed;
        do
        {
            placed = true;
            for (int k = 0; k < _cullWork.Count; k++)
            {
                var g = _cullWork[k].Group;
                if (!g.SpheresReady)
                {
                    FoliageCull.FillSpheres(g.Instances, g.Asset.Centre,
                        g.Asset.Radius);   // as CullWork does: the mesh is resident, its bounds known
                    g.SpheresReady = true;
                }
                if (!g.BoundsReady) g.ComputeBounds();
                if (IsRock(g.Asset, options)) PrepareRock(g, terrain);

                // A grown arena (full) moved every group: place them all again before a chunk names one.
                if (!cull.Place(ref g.Arena, ref g.ArenaGeneration, g.Instances))
                {
                    placed = false;
                    break;
                }

                // the arena is full and the memory is short (VramGuard): left out of this frame
                if (g.Arena.IsEmpty && g.Instances.Length > 0) _cullWork.RemoveAt(k--);
            }
        } while (!placed);
    }

    /// <summary>
    /// The batch order of this work list: the meshes' batches, the rocks' (per placement kind and level) and the impostors'.
    /// </summary>
    void OrderGpuBatches(WorldRenderOptions options)
    {
        bool wantLods = LodWanted;
        int stamp = ++_gpuWorkStamp;
        _gpuOrder.Clear();
        _gpuRockOrder.Clear();
        _gpuImpostorOrder.Clear();
        foreach (var (g, _, parts) in _cullWork)
        {
            var a = g.Asset;
            bool rockSegments = IsRock(a, options) && g.RockSorted;
            if ((parts & FoliageCull.ImpostorPart) != 0)
            {
                if (rockSegments)
                {
                    // A rock's impostor batches are those of the biome rows its segments have an atlas for.
                    foreach (var (row, _, _) in g.RockSegments)
                        if (ReadyRock(a, row) is { } v && v.Impostor!.Stamp != stamp) { (v.Impostor.Stamp, v.Impostor.Index) = (stamp,
                                _gpuImpostorOrder.Count); _gpuImpostorOrder.Add(v); }
                }
                else if (a.Impostor is { } imp && imp.Stamp != stamp) { (imp.Stamp, imp.Index) = (stamp,
                        _gpuImpostorOrder.Count); _gpuImpostorOrder.Add(a); }
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
                        index = _gpuRockOrder.Count;
                        for (int level = 0; level < (a.WorkLod?.Levels ?? 1); level++) _gpuRockOrder.Add((a, m, level));
                    }
                }
                continue;
            }
            if (a.WorkStamp != stamp)
            {
                (a.WorkStamp, a.WorkIndex) = (stamp, _gpuOrder.Count);
                _gpuOrder.Add(a);
            }
        }
    }

    /// <summary>
    /// The work list's entries (<see cref="_gpuEntries"/>): each group once per batch and part it has, a rock's per biome row.
    /// </summary>
    void ListGpuEntries(WorldRenderOptions options, int meshBatches, int impostorFirst)
    {
        _gpuEntries.Clear();
        foreach (var (g, _, parts) in _cullWork)
        {
            var a = g.Asset;
            if (IsRock(a, options) && g.RockSorted && (parts & FoliageCull.ImpostorPart) != 0)
            {
                // Per biome row: the rock's mesh chunks (flagged for the transition when the row has an atlas) and, for a row with one, its impostor
                // chunks.
                foreach (var (row, start, length) in g.RockSegments)
                {
                    var v = ReadyRock(a, row);
                    if (v is null || (parts & FoliageCull.MeshPart) != 0)
                    {
                        if (g.RockPlain)
                            AddRockEntries(meshBatches + a.RockPlainIndex, a, g, start, length, v is not null);
                        if (g.RockMirrored)
                            AddRockEntries(meshBatches + a.RockMirroredIndex, a, g, start, length, v is not null);
                    }
                    if (v is not null)
                        _gpuEntries.Add(new FoliageGpuEntry(impostorFirst + v.Impostor!.Index, g, start, length, false));
                }
                continue;
            }
            if ((parts & FoliageCull.ImpostorPart) != 0)
            {
                if ((parts & FoliageCull.MeshPart) != 0)
                    _gpuEntries.Add(new FoliageGpuEntry(a.WorkIndex, g, 0, g.Instances.Length, false));
                _gpuEntries.Add(new FoliageGpuEntry(impostorFirst + a.Impostor!.Index, g, 0, g.Instances.Length, false));
                continue;
            }
            if (!IsRock(a, options))
            {
                _gpuEntries.Add(new FoliageGpuEntry(a.WorkIndex, g, 0, g.Instances.Length, false));
                continue;
            }
            if (g.RockPlain) AddRockEntries(meshBatches + a.RockPlainIndex, a, g, 0, g.Instances.Length, false);
            if (g.RockMirrored) AddRockEntries(meshBatches + a.RockMirroredIndex, a, g, 0, g.Instances.Length, false);
        }
    }

    /// <summary>
    /// The cull's chunks per batch, in batch order, from the entries.
    /// </summary>
    void BuildGpuChunks(int meshBatches, int impostorFirst, int batches)
    {
        if (_gpuBatchStart.Length < batches + 1)
        {
            _gpuBatchStart = new int[batches * 2 + 1];
            _gpuCursor = new int[batches * 2];
        }
        Array.Clear(_gpuBatchStart, 0, batches + 1);
        const int chunk = FoliageShaders.CullChunk;

        // Chunks per batch: a mesh group's instances once; a rock group's once per batch it has (a group with both kinds of placement is
        // tested twice, each run keeping only its own kind).
        foreach (var e in _gpuEntries)
            _gpuBatchStart[e.Batch + 1] += (e.Length + chunk - 1) / chunk;
        for (int b = 0; b < batches; b++) _gpuBatchStart[b + 1] += _gpuBatchStart[b];
        int n = _gpuBatchStart[batches];
        if (_gpuChunks.Length < n) _gpuChunks = new FoliageCullChunk[Math.Max(n, _gpuChunks.Length * 2)];
        Array.Copy(_gpuBatchStart, _gpuCursor, batches);
        int entry = 0;
        foreach (var (g, range, _) in _cullWork)
        {
            // gpuEntries lists cullWork's groups in order, a rock group once per batch it has, a group with an impostor once per part.
            for (; entry < _gpuEntries.Count && ReferenceEquals(_gpuEntries[entry].Group, g); entry++)
            {
                var e = _gpuEntries[entry];
                int b = e.Batch, length = e.Length;
                uint flags = b < meshBatches ? (range.HasImpostor ? FoliageCullChunk.ImpostorMesh : 0)
                    : b >= impostorFirst ? FoliageCullChunk.Impostor
                    : FoliageCullChunk.Rock | (_gpuRockOrder[b - meshBatches].Mirrored ? FoliageCullChunk.Mirrored : 0)
                        | (e.ImpostorMesh ? FoliageCullChunk.ImpostorMesh : 0);
                float lodError = 0, lodNext = float.PositiveInfinity;
                if (b >= meshBatches && b < impostorFirst && _gpuRockOrder[b - meshBatches].Asset.WorkLod is { } workLod)
                {
                    flags |= FoliageCullChunk.Lod;
                    var levelErrors = LevelErrors(workLod);
                    lodError = levelErrors[e.Level];
                    lodNext = e.Level + 1 < workLod.Levels ? levelErrors[e.Level + 1] : float.PositiveInfinity;
                }
                uint first = FoliageGpuCull.FirstOf(g.Arena) + (uint)e.Start;
                for (int at = 0; at < length; at += chunk)
                {
                    _gpuChunks[_gpuCursor[b]++] = new FoliageCullChunk
                    {
                        First = first + (uint)at,
                        Count = (uint)Math.Min(chunk, length - at),
                        Range = range.Range,
                        RangeSquared = range.RangeSquared,
                        InverseBand = range.InverseBand,
                        Flags = flags,
                        Transition = range.HasImpostor ? range.Transition : 0,
                        InverseTransitionBand = range.InverseTransitionBand,
                        LodError = lodError,
                        LodNextError = lodNext,
                    };
                }
            }
        }
        for (int b = 0; b < batches; b++)
            for (int c = _gpuBatchStart[b]; c < _gpuBatchStart[b + 1]; c++) (_gpuChunks[c].BatchStart,
                    _gpuChunks[c].BatchEnd) = ((uint)_gpuBatchStart[b], (uint)_gpuBatchStart[b + 1]);   // the split lays a batch out whole
        _gpuChunkCount = n;
        _gpuInstances = 0;
        // a generated level's entry repeats the instances; each is drawn at one level
        foreach (var e in _gpuEntries) if (e.Level == 0) _gpuInstances += e.Length;
        _gpuChunkFrame = -1;
    }

    /// <summary>
    /// A rock run's entries: one per generated level in use (<see cref="MeshAsset.WorkLod"/>), the batches of the levels consecutive from
    /// <paramref name="batch"/>.
    /// </summary>
    void AddRockEntries(int batch, FoliageMeshAsset a, FoliageGroup g, int start, int length, bool impostorMesh)
    {
        for (int level = 0; level < (a.WorkLod?.Levels ?? 1); level++)
            _gpuEntries.Add(new FoliageGpuEntry(batch + level, g, start, length, impostorMesh, level));
    }

    /// <summary>
    /// A rock group whose work list has impostors needs its mesh chunks too when some of its rows have no atlas (those stay meshes whatever the
    /// zone's distance).
    /// </summary>
    static bool RockMeshWanted(FoliageGroup g, int parts)
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
        int n = _gpuOrder.Count + _gpuRockOrder.Count + _gpuImpostorOrder.Count;
        if (_gpuBatchShown.Length < n) _gpuBatchShown = new bool[n * 2];
        Array.Clear(_gpuBatchShown, 0, n);
        var planes = _cullView.Planes;
        foreach (var e in _gpuEntries)
            if (!_gpuBatchShown[e.Batch] && WorldCamera.Intersects(planes, e.Group.BoundMin, e.Group.BoundMax) && LevelNeeded(e, eye))
                _gpuBatchShown[e.Batch] = true;
    }

    /// <summary>
    /// The rock draws of this view: per rock batch shown, one draw per part of its mesh (<see cref="Emit"/>'s placements, one per part).
    /// </summary>
    void PrepareRocks()
    {
        _rockDraws.Clear();
        _rockDrawBatch.Clear();
        _rockDrawFirst.Clear();
        int meshBatches = _gpuOrder.Count;
        for (int r = 0; r < _gpuRockOrder.Count; r++)
        {
            if (!_gpuBatchShown[meshBatches + r]) continue;
            var (a, mirrored, level) = _gpuRockOrder[r];

            // this view picks no generated level (a shadow cascade of unknown texel): the batch would draw nothing
            if (level > 0 && !_lodActive) continue;
            for (int p = 0; p < a.Main!.Parts.Count; p++)
            {
                var gp = a.Main.Parts[p];
                if (level == 0)
                {
                    _rockDraws.Add(new TerrainRenderer.IndirectMesh(PlainMesh(gp), gp.Count, mirrored));
                    _rockDrawFirst.Add(0);
                }
                else
                {
                    var pl = a.WorkLod!.Parts[p];
                    _rockDraws.Add(new TerrainRenderer.IndirectMesh(pl.Bindings, pl.Count[level], mirrored));
                    _rockDrawFirst.Add(pl.First[level]);
                }
                _rockDrawBatch.Add(meshBatches + r);
            }
        }
    }

    /// <summary>
    /// Records this view's cull ahead of the frame: the chunks (once a frame per work list), one draw per mesh draw (its batch's chunks), then one
    /// per rock draw.
    /// </summary>
    FoliageCullResult DispatchGpuCull(Vector2 eye, TerrainRenderer terrain, bool coverage)
    {
        var cull = _gpuCull!;
        var frame = Gpu.Frame;
        if (_gpuChunkFrame != frame.Number)
        {
            _gpuChunkData = frame.WriteConstants(MemoryMarshal.AsBytes(_gpuChunks.AsSpan(0, _gpuChunkCount)), 256);
            _gpuChunkFrame = frame.Number;
        }
        int meshes = _meshDraws.Count, rocks = meshes + _rockDraws.Count, count = rocks + _impostorDraws.Count;
        if (_gpuDraws.Length < count) _gpuDraws = new FoliageCullDraw[count * 2];
        for (int i = 0; i < meshes; i++)
        {
            var d = _meshDraws[i];
            _gpuDraws[i] = new FoliageCullDraw { FirstIndex = DrawFirst(d.Part), IndexCount = DrawCount(d.Part),
                ChunkStart = (uint)_gpuBatchStart[d.Batch], ChunkEnd = (uint)_gpuBatchStart[d.Batch + 1] };
        }
        for (int k = 0; k < _rockDraws.Count; k++)
        {
            int b = _rockDrawBatch[k];
            _gpuDraws[meshes + k] = new FoliageCullDraw { IndexCount = (uint)_rockDraws[k].IndexCount, FirstIndex = (uint)_rockDrawFirst[k],
                ChunkStart = (uint)_gpuBatchStart[b], ChunkEnd = (uint)_gpuBatchStart[b + 1] };
        }
        for (int k = 0; k < _impostorDraws.Count; k++)
        {
            int b = _impostorDraws[k].Batch;
            _gpuDraws[rocks + k] = new FoliageCullDraw { IndexCount = 6, ChunkStart = (uint)_gpuBatchStart[b],
                ChunkEnd = (uint)_gpuBatchStart[b + 1] };
        }
        var draws = frame.WriteConstants(MemoryMarshal.AsBytes(_gpuDraws.AsSpan(0, count)), 256);

        // What the rocks' row 0 w carries, as the terrain's mesh path writes it: the biome row in colour (resident ones, now), 0 in depth.
        var rock = new FoliageRockView { BiomeRows = !_depthPass };
        if (!_depthPass && _gpuRockOrder.Count > 0) terrain.FeatureResidentBiomes(rock.Resident);
        if (FoliageGpuCull.CountTriangles) cull.TallyView = (_depthPass ? 1 : coverage ? 2 : 0, meshes, rocks);

        // The fog cull rides in the main colour view's dispatch only (FogCull is set around that draw alone): per instance, in the cull kernel.
        Span<Vector4> fog = stackalloc Vector4[FogVolumes.CullVectors];
        bool fogOn = !_depthPass && FogCull is { } fogCull && fogCull.WriteCull(fog);
        bool occlusionOn = !_depthPass && !Occlusion.IsEmpty;
        cull.TimingClass = _depthPass || coverage ? 0 : occlusionOn ? 2 : 1;

        // The split (EarlyDepth): in the colour views each batch's dithered instances come after its fully visible ones, drawn with the other
        // program.
        bool split = !_depthPass && EarlyDepth.Enabled;

        // The interior cut (main colour view): with the split, the fully visible instances over a mask go with the fading ones, to the program with
        // the cut.
        var interior = split && InteriorCut is { Active: true } mask ? mask.Boxes : null;
        var result = cull.Dispatch(new FoliageCullWork(_gpuChunkData, _gpuChunkCount, draws, count) { Instances = _gpuInstances }, _cullView, eye,
            in rock, 0.999f, fogOn ? fog : default, occlusionOn ? Occlusion : default, _lodView, split, interior);
        if (occlusionOn && cull.OccludedIsFresh) OccludedInstances = cull.LateOccluded;
        DrawnInstances += cull.LateVisible;
        if (fogOn && cull.LateFogCulled > 0)
            FogCull!.AddCulled(FogVolumes.CullKind.FoliageInstances, cull.LateFogCulled);
        return result;
    }

    /// <summary>
    /// The mesh's batch, put into <c>active</c> (emptied) by the first call of this <see cref="Draw"/>.
    /// </summary>
    Batch BatchOf(FoliageMeshAsset a)
    {
        if (!_batches.TryGetValue(a, out var batch)) _batches[a] = batch = new Batch { Asset = a };
        if (batch.Stamp != _drawStamp)
        {
            batch.Stamp = _drawStamp;
            batch.Count = 0;
            batch.Index = _active.Count;
            _active.Add(batch);
        }
        return batch;
    }

    /// <summary>
    /// A group's visible instances (batch matrices) into its mesh's batch, or the terrain's mesh path for TERRAIN-mode rocks.
    /// </summary>
    void EmitAll(FoliageMeshAsset a, ReadOnlySpan<Matrix4x4> visible, WorldRenderOptions options)
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
        if (batch.Count + visible.Length > batch.Data.Length)
            Array.Resize(ref batch.Data, Math.Max(batch.Data.Length * 2, batch.Count + visible.Length));
        visible.CopyTo(batch.Data.AsSpan(batch.Count));
        batch.Count += visible.Length;
    }

    #endregion

    #region Depth only (the sun's shadow map, ShadowPass)

    bool _depthPass;
    /// <summary>
    /// The cascade being drawn skips meshes smaller than this (world units, <see cref="MeshAsset.Size"/>): their shadow would cover a few texels.
    /// </summary>
    float _depthMinSize;

    /// <summary>
    /// Draws the foliage meshes' depth for a shadow cascade: <see cref="Draw"/>'s culling and ranges (measured from the camera's
    /// <paramref name="eye"/>) with <see cref="ShadowShaders.MeshDepthFragment"/>, so the leaves' cut-out holds; no grass, no mesh smaller than
    /// <paramref name="minSize"/>. <paramref name="texel"/> is the cascade's texel (world units; 0: unknown) for the generated levels.
    /// <paramref name="coarse"/>: a far cascade drawn with coarser casters (<see cref="ShadowLod"/>: the generated levels allow more texels). Leaves
    /// the draw counters describing this call.
    /// </summary>
    public void DrawDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, TerrainRenderer terrain,
        float maxRange = float.PositiveInfinity, float minSize = 0, float texel = 0, bool coarse = false)
    {
        (_depthPass, _depthMinSize, _lodTexel, _lodCoarse) = (true, minSize, texel, coarse);
        try { Draw(viewProjection, eye, frustum, options, Vector3.UnitY, Vector3.Zero, 0, terrain, grass: false, maxRange: maxRange); }
        finally { (_depthPass, _depthMinSize, _lodTexel, _lodCoarse) = (false, 0, 0, false); }
    }

    #endregion

    #region Native recording

    // docs/renderer-native.md 3.3 and 7.5, step O: native-model programs (FoliageShaders' native variants: the same maths, the values in
    // FrameConstants / ViewConstants / push constants, the textures by bindless index), recorded into the pass VkGl has open
    // (BeginNativeInPass). Each draw method is split into Prepare (reads the textures and the zones' state, makes a draw list) and Record (the
    // list into one native segment: the sets and dynamic state once, per draw a push constant block and the mesh's own buffers).

    const int ColourKind = 0, DepthKind = 1, GrassKind = 2, MotionKind = 3, SolidKind = 4, PrepassKind = 5, PrepassSolidKind = 6, EqualKind = 7;
    /// <summary>
    /// The first location the per-instance rows (the batch matrices) take; the mesh's own inputs are below it.
    /// </summary>
    const int RowLocation = FoliageShaders.InstanceLocation;

    /// <summary>
    /// This call's batch matrices in the frame's constants (a batch at its <see cref="Batch.Offset"/>; the draws reach it by firstInstance).
    /// </summary>
    Transient _instances;

    /// <summary>
    /// The pipeline state a segment's draws share (everything of <see cref="GraphicsPipelineDesc"/> but the vertex layout).
    /// </summary>
    readonly record struct SegmentPipeline(GpuProgram Program, AttachmentFormats Formats, BlendState Blend, ColorMask Mask,
        PolygonKind Polygon, bool AlphaToCoverage, bool DepthClamp);

    readonly Dictionary<(int Kind, SegmentPipeline State), int> _segmentByState = [];
    readonly SegmentPipeline[] _lastSegment = new SegmentPipeline[8];
    readonly int[] _segmentIds = new int[8];
    int _segmentCount;

    /// <summary>
    /// The near depth texture registered last and its bindless index (<see cref="NearDepthIndex"/>).
    /// </summary>
    (SampledTexture Texture, uint Index)? _nearDepthIndex;

    /// <summary>
    /// A stable number for a segment's pipeline state, so a mesh compares one int per draw.
    /// </summary>
    int SegmentId(int kind, GpuProgram p, PassTargets t, DrawState s)
    {
        var segment = new SegmentPipeline(p, t.Formats, s.Blend, s.ColourMask, s.Polygon, s.AlphaToCoverage, s.DepthClamp);
        if (_segmentIds[kind] != 0 && segment == _lastSegment[kind]) return _segmentIds[kind];
        if (!_segmentByState.TryGetValue((kind, segment), out int id))
            _segmentByState[(kind, segment)] = id = ++_segmentCount;
        _lastSegment[kind] = segment;
        return _segmentIds[kind] = id;
    }

    /// <summary>
    /// A mesh part's native state for <paramref name="p"/>, made on first use from its native buffers (they never change while the part lives). The
    /// batch matrices are four per-instance rows of 64 bytes at locations 7 to 10 (bound once per segment).
    /// </summary>
    static void Current(ref FoliageNativeMesh n, FoliageGpuPart part, NativeProg p)
    {
        if (n.Layout is not null) return;
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        part.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[RowLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32F, InstanceStride, true);
        n.Layout = p.Layout(attributes);
        n.Vertices = p.Buffers(attributes, p.Own);
        n.Elements = new GpuBufferBinding(part.Indices.Handle, 0, part.Indices.Size);
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    GpuPipeline PipelineFor(ref FoliageNativeMesh n, NativeProg p, int segment, DrawState state, AttachmentFormats formats, string label)
    {
        if (n.SegA == segment) return n.PipeA!.Value;
        if (n.SegB == segment) return n.PipeB!.Value;
        var pipeline = Gpu.Pipelines.Get(state.Pipeline(p.P, n.Layout!, PrimitiveType.Triangles, formats, label));
        (n.SegB, n.PipeB) = (n.SegA, n.PipeA);
        (n.SegA, n.PipeA) = (segment, pipeline);
        return pipeline;
    }

    /// <summary>
    /// The scene depth (<see cref="MotionTargets.Depth"/>) in the bindless 2D float array the shaders index: registered again (the old index freed
    /// after the frames in flight) only when the texture or its sampler changed.
    /// </summary>
    uint NearDepthIndex(in SampledTexture nearDepth)
    {
        if (_nearDepthIndex is { } known && known.Texture == nearDepth) return known.Index;
        if (_nearDepthIndex is { } old) Gpu.Bindless.Free(BindlessKind.Texture2D, old.Index);
        _nearDepthIndex = (nearDepth, Gpu.Bindless.Register(BindlessKind.Texture2D, nearDepth));
        return _nearDepthIndex.Value.Index;
    }

    /// <summary>
    /// Pushes <paramref name="value"/> when its bytes differ from what the segment pushed last (<paramref name="last"/>).
    /// </summary>
    static void Push<T>(IGpuCommandList cmd, GpuProgram p, in T value, ref T last, ref bool pushed) where T : unmanaged
    {
        if (pushed && MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in last))))
            return;
        cmd.PushConstants(p, GpuStage.Vertex | GpuStage.Fragment, in value);
        last = value;
        pushed = true;
    }

    #endregion

    #region Mesh recording

    /// <summary>
    /// What a mesh draw sets besides its textures.
    /// </summary>
    readonly record struct MaterialKey(bool Swizzled, bool HasDiffuse, bool Triplanar, float TileX, float TileY, int AlphaSource,
        float AlphaThreshold, bool Emissive, float Specular, uint Surface);

    struct MeshDraw
    {
        public FoliageGpuPart Part;
        public uint FirstInstance, Instances;
        /// <summary>
        /// The batch's place in <c>active</c> (the GPU cull's draw reaches its batch's chunks by it).
        /// </summary>
        public int Batch;
        public uint Diffuse, Normal, Diffuse2, Normal2;
        public MaterialKey Material;
        /// <summary>
        /// 1: uHasNormal, 2: uHasDual, 4: uUseVertexColour.
        /// </summary>
        public int PartBits;
        public bool DoubleSided;
    }

    /// <summary>
    /// The segment's LOD bias (the upscaler's, <see cref="GpuContext.LodBias"/>, as VkGl applied it where the GL code bound the textures) and GL's
    /// stand-in for a texture not resident.
    /// </summary>
    float _textureBias;
    uint _textureStandIn;

    /// <summary>
    /// The current mesh segment's draws.
    /// </summary>
    readonly List<MeshDraw> _meshDraws = [];

    /// <summary>
    /// The mesh segments' recording jobs, reused.
    /// </summary>
    readonly JobPool<MeshJob> _meshJobs = new();

    /// <summary>
    /// A new segment: the bias in effect now and the stand-in's bindless index (the 2D float array the shaders index).
    /// </summary>
    void NewTextureSegment()
    {
        _textureBias = Gpu.LodBias;
        _textureStandIn = Gpu.StandIn2D;
    }

    /// <summary>
    /// The bindless index of a texture by its <see cref="WorldTexture.Key"/> (0: the stand-in), sampled as the GL texture was.
    /// </summary>
    uint Texture(uint key) => key == 0 ? _textureStandIn : _textures.Index(key, _textureBias, _textureStandIn);

    static uint IdOf(WorldTexture? t) => t?.Key ?? 0;

    /// <summary>
    /// The batches' mesh parts as draws, in order, with the texture keys (reading a key counts as use: here, not in Record).
    /// </summary>
    void PrepareMeshes(WorldRenderOptions options, bool gpu)
    {
        _meshDraws.Clear();
        foreach (var b in _active)
        {
            if (gpu && !_gpuBatchShown[b.Index]) continue;   // the GPU cull: no group of the batch can be in view (ShowBatches)
            var a = b.Asset;
            if (_depthPass && a.HasBounds && a.Size < _depthMinSize) continue;   // too small for this cascade's texels
            AddMesh(a.Main!, a.MainMaterial!, b, options);
            if (a.Leaves is not null) AddMesh(a.Leaves, a.LeavesMaterial!, b, options);
        }
    }

    void AddMesh(FoliageGpuMesh mesh, FoliageMaterial m, Batch b, WorldRenderOptions options)
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
            _meshDraws.Add(new MeshDraw
            {
                Part = gp,
                FirstInstance = (uint)b.Offset,
                Instances = (uint)b.Count,
                Batch = b.Index,
                Diffuse = diffuse,
                Normal = normalId,
                Diffuse2 = diffuse2,
                Normal2 = normal2,
                Material = key,
                PartBits = bits,
                DoubleSided = m.DoubleSided,
            });
            DrawCalls++;
        }
    }

    /// <summary>
    /// What a mesh segment draws: the colour, a shadow cascade's depth, the main view's depth prepass (<see cref="FoliagePrepass"/>), or the colour
    /// after it (no discards, depth EQUAL, no depth write).
    /// </summary>
    enum MeshPass { Colour, Depth, Prepass, Equal }

    /// <summary>
    /// The mesh draw list in one native segment (<paramref name="pass"/>), back faces culled unless the material is double-sided.
    /// </summary>
    void RecordMeshes(NativeProg mp, MeshPass pass, Matrix4x4 viewProjection, Vector3 eye, Vector3 light, Vector3 fogColour, float fogDistance,
        bool coverage)
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

        // The prepass writes depth only; the colour after it draws exactly the fragments that left their depth there (the prepass has the discards,
        // this has none).
        var state = prepass ? sceneState with { ColourMask = 0 }
            : equal ? sceneState with { Compare = DepthFunction.Equal, DepthWrite = false }
            : sceneState;
        int segment = SegmentId(depth ? DepthKind : prepass ? PrepassKind : equal ? EqualKind : ColourKind, p, targets, state);
        var job = _meshJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Program, job.Count) = (this, targets, state, p, 0);
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour,
            FogDistance = fogDistance };
        if (!depth) InteriorMaskRenderer.Apply(ref view, InteriorCut);
        job.Frame = _nativeFrame.Prepare(in view);

        // The GPU cull's compacted rows and one indirect draw per mesh draw (its batch's count and first instance), else this call's constants.
        bool indirect = !_gpuResult.IsEmpty;
        for (int a = 0; a < 4; a++)
        {
            job.Rows[a] = indirect
                ? new GpuBufferBinding(_gpuResult.Rows, _gpuResult.RowsOffset + (ulong)(16 * a))
                : new GpuBufferBinding(_instances.Handle, _instances.Offset + (ulong)(16 * a));
        }
        AddMeshDraws(job, mp, pass, (targets, state, segment), indirect, coverage, label);
        RecordBillboards(job, pass, viewProjection, (targets, state, sceneState), indirect, coverage, label);
    }

    /// <summary>
    /// The mesh draw list into <paramref name="job"/>: per draw its material's push block and its pipeline, a part without a cut-out twice in the
    /// split (<see cref="AddSolidDraw"/>).
    /// </summary>
    void AddMeshDraws(MeshJob job, NativeProg mp, MeshPass pass, (PassTargets Targets, DrawState State, int Segment) at, bool indirect, bool coverage,
        string label)
    {
        var (targets, state, segment) = at;
        bool depth = pass == MeshPass.Depth, prepass = pass == MeshPass.Prepass, equal = pass == MeshPass.Equal;
        int drawIndex = 0;
        NewTextureSegment();

        // The constants the GL code set on every draw (Shaders.MeshFragment's uniforms the foliage does not vary).
        var pc = BaseMeshPush(coverage);
        var sided = CullMode.None;
        CullMode? side = null;

        // The split (EarlyDepth; GPU cull, colour views): the program without the discards draws the fully visible instances of the parts without a
        // cut-out.
        bool splitView = indirect && _gpuResult.Split && !depth && !equal;

        // The prepass splits the same way: the empty program for the fully visible instances of a part without a cut-out, the dither (and the
        // cut-out) program for the rest.
        var solidMesh = prepass ? _prepassSolidMesh : _colourSolidMesh;
        int solidSegment = splitView ? SegmentId(prepass ? PrepassSolidKind : SolidKind, solidMesh.P, targets, state) : 0;
        foreach (ref readonly var d in CollectionsMarshal.AsSpan(_meshDraws))
        {
            SetDrawPush(ref pc, in d);
            var want = d.DoubleSided ? sided : state.Cull;
            bool raster = side != want;
            side = want;
            var part = d.Part;
            ref var n = ref (depth ? ref part.DepthNative
                : ref (prepass ? ref part.PrepassNative : ref (equal ? ref part.ColourSolidNative : ref part.ColourNative)));
            Current(ref n, part, mp);

            // A part without a cut-out is drawn twice: its fully visible instances (the cull lays them out first) with the solid program, the
            // dithered ones after them with the full one. A cut-out part keeps its one draw over both (it needs the discard anyway).
            bool split = splitView && pc.AlphaThreshold == 0f;
            if (split)
            {
                AddSolidDraw(job, in d, in pc, (want, raster), (solidMesh, solidSegment), (state, targets), prepass, drawIndex);
                raster = false;
            }
            job.Add(new MeshJob.Draw
            {
                SetRaster = raster,
                Cull = want,
                Pipeline = n.SegA == segment ? n.PipeA!.Value : PipelineFor(ref n, mp, segment, state, targets.Formats, label),
                Vertices = n.Vertices,
                Elements = n.Elements,
                Push = pc,
                Args = indirect ? _gpuResult.Args : default,
                ArgsOffset = _gpuResult.ArgsOffset + (ulong)((split ? 2 * _gpuResult.DrawCount : 0) + drawIndex) * 20,
                FirstIndex = DrawFirst(part),
                IndexCount = DrawCount(part),
                Instances = d.Instances,
                FirstInstance = d.FirstInstance,
            });
            drawIndex++;
        }
    }

    /// <summary>
    /// The push constants every mesh draw starts from (the head textures the stand-in of this texture segment).
    /// </summary>
    MeshPush BaseMeshPush(bool coverage)
    {
        uint standIn = _textureStandIn;
        return new MeshPush
        {
            Tint = Vector3.One,
            TriplanarScale = 1f / 5000,
            AlphaChannel = 3,
            GreyChannel = -1,
            HeadDiffuse = standIn,
            HeadNormal = standIn,
            Coverage = coverage ? 1u : 0u,
        };
    }

    /// <summary>
    /// A mesh draw's material and part bits and its textures' bindless indices into <paramref name="pc"/>.
    /// </summary>
    void SetDrawPush(ref MeshPush pc, in MeshDraw d)
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
    }

    /// <summary>
    /// The split's first draw of a part without a cut-out: its fully visible instances with the solid program (<paramref name="cull"/>: the side
    /// culled and whether the raster state is set; <paramref name="solid"/>: the solid program and its segment).
    /// </summary>
    void AddSolidDraw(MeshJob job, in MeshDraw d, in MeshPush pc, (CullMode Want, bool Raster) cull, (NativeProg Mesh, int Segment) solid,
        (DrawState State, PassTargets Targets) at, bool prepass, int drawIndex)
    {
        var (want, raster) = cull;
        var (solidMesh, solidSegment) = solid;
        var (state, targets) = at;
        var part = d.Part;
        ref var sn = ref (prepass ? ref part.PrepassSolidNative : ref part.ColourSolidNative);
        Current(ref sn, part, solidMesh);
        job.Add(new MeshJob.Draw
        {
            SetRaster = raster,
            Cull = want,
            Pipeline = sn.SegA == solidSegment ? sn.PipeA!.Value : PipelineFor(ref sn, solidMesh, solidSegment, state, targets.Formats,
                    prepass ? "foliage mesh prepass solid" : "foliage meshes solid"),
            Vertices = sn.Vertices,
            Elements = sn.Elements,
            Push = pc,
            Args = _gpuResult.Args,
            ArgsOffset = _gpuResult.ArgsOffset + (ulong)(_gpuResult.DrawCount + drawIndex) * 20,
            FirstIndex = DrawFirst(part),
            IndexCount = DrawCount(part),
            Instances = d.Instances,
            FirstInstance = d.FirstInstance,
        });
    }

    /// <summary>
    /// The billboards of a mesh pass, then <paramref name="job"/> recorded, and after the prepass's colour a segment of their own for those it left
    /// out.
    /// </summary>
    void RecordBillboards(MeshJob job, MeshPass pass, Matrix4x4 viewProjection, (PassTargets Targets, DrawState State, DrawState Scene) at,
        bool indirect, bool coverage, string label)
    {
        var (targets, state, sceneState) = at;
        var p = job.Program;
        bool depth = pass == MeshPass.Depth, prepass = pass == MeshPass.Prepass, equal = pass == MeshPass.Equal;

        // The billboards: in the colour segment as before, or (ImpostorPrepass) in the prepass and in the colour after it, the rocks' alone after
        // that.
        bool billboardPrepass = (prepass || equal) && ImpostorPrepass.Enabled;
        if (_impostorDraws.Count > 0 && !prepass && !equal)
            AddImpostorDraws(job, depth, viewProjection, targets, state, coverage, indirect, _meshDraws.Count + _rockDraws.Count);
        else if (_impostorDraws.Count > 0 && billboardPrepass)
        {
            AddImpostorDraws(job, false, viewProjection, targets, state, coverage, indirect, _meshDraws.Count + _rockDraws.Count,
                    prepass ? ImpostorPass.Prepass : ImpostorPass.AfterPrepass);
        }
        Gpu.Record(label, job);
        if (equal && _impostorDraws.Count > 0 && (!billboardPrepass || _impostorDraws.Exists(static d => d.Asset.RockOf is not null)))
        {
            // The billboards are not in the prepass (their fragment stage has its own discard), or only the rocks' are not: their own segment, with
            // the scene's depth test and write.
            var billboards = _meshJobs.Rent();
            (billboards.Owner, billboards.Targets, billboards.State, billboards.Program, billboards.Count) = (this, targets, sceneState, p, 0);
            billboards.Frame = job.Frame;
            for (int a = 0; a < 4; a++) billboards.Rows[a] = job.Rows[a];
            AddImpostorDraws(billboards, false, viewProjection, targets, sceneState, coverage, indirect, _meshDraws.Count + _rockDraws.Count,
                billboardPrepass ? ImpostorPass.RocksOnly : ImpostorPass.Scene);
            Gpu.Record("foliage billboards", billboards);
        }
    }

    /// <summary>
    /// One foliage mesh segment as prepared (<see cref="RecordMeshes"/>): recorded on any thread, the same commands as before wave 4.
    /// </summary>
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
            /// <summary>
            /// Indirect: the arguments' buffer (else a plain indexed draw).
            /// </summary>
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
            Owner._meshJobs.Return(this);
        }
    }

    /// <summary>
    /// <see cref="Push{T}"/> by layout (a job keeps the layout, not the program).
    /// </summary>
    static void PushTo<T>(IGpuCommandList cmd, GpuProgram program, in T value, ref T last, ref bool pushed) where T : unmanaged
    {
        if (pushed && MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in last))))
            return;
        cmd.PushConstants(program, GpuStage.Vertex | GpuStage.Fragment, in value);
        last = value;
        pushed = true;
    }

    #endregion

    #region Grass recording

    struct GrassDraw
    {
        public FoliageGrassBuffer Buffer;
        public uint Vertices, Sprite, Colour;
        public float Distance;
        public GrassPush Push;
    }

    struct MotionDraw
    {
        public FoliageGrassBuffer Buffer;
        public uint Vertices, Sprite;
        public GrassPush Push;
    }

    readonly List<GrassDraw> _grassDraws = [];
    readonly List<(float Distance, FoliageZoneState State, int Key, FoliageGrassPage Page)> _grassOrder = [];

    /// <summary>
    /// The CPU path's grass recording jobs, reused.
    /// </summary>
    readonly JobPool<GrassJob> _grassJobs = new();

    /// <summary>
    /// The blade buffer's patch textures (looked up by name once per patch, not per draw) and the blades the density setting shows.
    /// </summary>
    FoliageGrassBuffer Patch(FoliageGrassBuffer b, FoliageGrassPatch patch)
    {
        if (!ReferenceEquals(b.Patch, patch))
        {
            b.Patch = patch;
            b.Sprite = _textures.Get(patch.Grass.Sprite, false);
            b.ColourMap = _textures.Get(patch.Grass.ColourMap, false);
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

    /// <summary>
    /// The blade draws of the pages in view, nearest page first (so the depth test rejects most of the hidden blades before they are shaded).
    /// </summary>
    void PrepareGrass(Vector3 eye, Vector4[] frustum, WorldRenderOptions options, bool coverage)
    {
        _grassDraws.Clear();
        _grassOrder.Clear();
        foreach (var st in _zones.Values)
        {
            if (st.Pages.Count == 0 || st.Patches.Count == 0) continue;
            float reach = st.Patches.Max(GrassRange);   // pages kept round a far character (Kept) are not sorted and tested each frame
            foreach (var (k, pg) in st.Pages)
                if (pg.Buffers is not null && BoxDistance(st.X0 + k % PagesPerZone * PageSize, st.Z0 + k / PagesPerZone * PageSize, PageSize,
                        eye) is var d && d < reach)
                    _grassOrder.Add((d, st, k, pg));
        }
        _grassOrder.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        foreach (var (d, state, key, page) in _grassOrder)
        {
            float x0 = state.X0 + key % PagesPerZone * PageSize, z0 = state.Z0 + key / PagesPerZone * PageSize;
            float y = eye.Y;
            if (state.Ground is { } ground) y = ground.Height(x0 + PageSize / 2, z0 + PageSize / 2);
            if (!WorldCamera.Intersects(frustum, new Vector3(x0, y - 2000, z0), new Vector3(x0 + PageSize, y + 2000, z0 + PageSize)))
                continue;
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
                _grassDraws.Add(new GrassDraw
                {
                    Buffer = b,
                    Distance = d,
                    Vertices = g.CrossQuads ? 12u : 6u,
                    Sprite = options.Textures ? sprite.Key : 0,
                    Colour = hasColour ? b.ColourMap!.Key : 0,
                    Push = new GrassPush
                    {
                        Size = new Vector4(g.QuadMinWidth, g.QuadMaxWidth, g.QuadMinHeight, g.QuadMaxHeight),
                        ColourBounds = new Vector4(patch.X0, patch.Z0, patch.X1, patch.Z1),
                        Sway = patch.Layer.Wind ? g.SwayLength : 0f,
                        Range = range,
                        Frequency = 2f,
                        Cross = g.CrossQuads ? 1u : 0u,
                        HasColourMap = hasColour ? 1u : 0u,
                        Coverage = coverage ? 1u : 0u,
                        Wireframe = options.Wireframe == 2 ? 1u : 0u,
                    },
                });
                DrawCalls++;
                DrawnBlades += b.Shown;
            }
        }
    }

    /// <summary>
    /// The blade draw list in one native segment: no culling, instanced quads from the blade buffers.
    /// </summary>
    void RecordGrass(Matrix4x4 viewProjection, Vector3 eye, Vector3 light, Vector3 fogColour, float fogDistance, bool coverage)
    {
        var gp = _grassProgram;
        var p = gp.P;

        // Prepare (wave 4, docs/renderer-native.md 6.2): the pass state, the sets, and per draw the push block with its bindless indices.
        var targets = Gpu.CurrentTargets();
        var state = GrassState(Gpu, coverage);
        int segment = SegmentId(GrassKind, p, targets, state);
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance,
            Time = SwayPhase() };
        if (!_depthPass) InteriorMaskRenderer.Apply(ref view, InteriorCut);
        var job = _grassJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Program, job.Count) = (this, targets, state, p, 0);
        job.Frame = _nativeFrame.Prepare(in view);
        NewTextureSegment();

        // The blades of every page are one buffer (the grass arena): one pipeline and one vertex binding for the segment, the draw's first instance
        // finds its page.
        job.Pipeline = PipelineFor(ref _grass.MeshCpu, gp, segment, state, targets.Formats, "foliage grass");
        job.Vertices = _grass.MeshCpu.Vertices;
        if (job.Draws.Length < _grassDraws.Count)
            job.Draws = new GrassJob.Draw[Math.Max(_grassDraws.Count, job.Draws.Length * 2)];
        foreach (ref readonly var d in CollectionsMarshal.AsSpan(_grassDraws))
        {
            var pc = d.Push;
            pc.Sprite = Texture(d.Sprite);
            pc.ColourMap = Texture(d.Colour);
            var buffer = d.Buffer;
            job.Draws[job.Count++] = new GrassJob.Draw { Push = pc, Vertices = d.Vertices, Instances = (uint)buffer.Shown,
                FirstInstance = buffer.FirstBlade };
        }
        Gpu.Record("foliage grass", job);
    }

    /// <summary>
    /// The host's state for the grass, with what the GL version set around it: no culling (GL's cull face off), alpha to coverage with a multisampled
    /// target.
    /// </summary>
    static DrawState GrassState(IGpuContext gpu, bool coverage) =>
        gpu.CurrentState() with { Cull = CullMode.None, AlphaToCoverage = coverage };

    /// <summary>
    /// The motion pass's state (the host's: colour mask red and green, no depth test, no blending) without culling (GL's cull face off).
    /// </summary>
    static DrawState MotionState(IGpuContext gpu) => gpu.CurrentState() with { Cull = CullMode.None };

    /// <summary>
    /// The CPU-culled grass of one view as prepared (<see cref="RecordGrass"/>): recorded on any thread, the same commands as before wave 4.
    /// </summary>
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
            Owner._grassJobs.Return(this);
        }
    }

    float SwayPhase() => (float)((SwaySeconds ?? _clock.Elapsed.TotalSeconds) * 0.3 * Math.PI % (2 * Math.PI));

    #endregion

    #region Grass motion (upscalers)

    Matrix4x4 _motionViewProjection, _motionUnjittered, _previousUnjittered;
    Vector3 _motionEye;
    Vector4[] _motionFrustum = [];
    float? _previousSwayPhase;
    /// <summary>
    /// This frame's <see cref="GrassMotionReach.Scale"/> for the motion pass (0: every blade in range).
    /// </summary>
    float _motionScale;
    bool _motionCamera, _havePrevious;
    readonly List<MotionDraw> _motionDraws = [];

    /// <summary>
    /// The scene camera for <see cref="DrawGrassMotion"/>: this frame's view-projection as drawn (jittered) and unjittered.
    /// </summary>
    public void SetMotionCamera(Matrix4x4 viewProjection, Matrix4x4 unjittered, Vector3 eye, Vector4[] frustum)
    {
        (_motionViewProjection, _motionUnjittered, _motionEye, _motionFrustum) = (viewProjection, unjittered, eye, frustum);
        _motionCamera = true;
    }

    /// <summary>
    /// The swaying grass's own motion over the camera motion, for the upscalers (<see cref="IWorldPostChain.ObjectMotion"/> binds the motion target,
    /// red and green only, without depth test or blending): the scene's blades of layers with wind, now and with last frame's sway phase
    /// and camera, written where they are what the depth buffer shows. Blades without sway move with the camera and need nothing.
    /// </summary>
    public void DrawGrassMotion(MotionTargets targets)
    {
        if (!_motionCamera) return;
        _motionCamera = false;
        if (!Enabled || !DrawGrass)
        {
            _havePrevious = false;
            return;
        }
        float time = SwayPhase(), previousTime = _previousSwayPhase ?? time;
        var previous = _havePrevious ? _previousUnjittered : _motionUnjittered;
        (_previousSwayPhase, _previousUnjittered, _havePrevious) = (time, _motionUnjittered, true);
        var eye = _motionEye;

        // grass-velocity: blades whose own motion stays under the threshold are left to the camera reprojection; with no phase step nothing moves at
        // all.
        _motionScale = 0;
        if (GrassVelocityCull && targets.FocalPixels > 0)
        {
            float step = GrassMotionReach.PhaseStep(time, previousTime);
            if (step == 0) return;
            _motionScale = GrassMotionReach.Scale(step, targets.FocalPixels, GrassVelocityPixels);
        }
        if (GpuGrassActive)
        {
            DrawGrassMotionGpu(targets, eye, time, previousTime, previous);
            return;
        }
        PrepareMotion(eye);
        if (_motionDraws.Count == 0) return;
        RecordMotion(targets, eye, time, previousTime, previous);
    }

    /// <summary>
    /// The motion draws of the pages in the scene camera's view, in the zones' and pages' dictionary order (the CPU reference; the GPU path orders by
    /// distance, 5.6.2).
    /// </summary>
    void PrepareMotion(Vector3 eye)
    {
        _motionDraws.Clear();
        foreach (var state in _zones.Values)
            foreach (var (key, page) in state.Pages)
            {
                if (page.Buffers is null) continue;
                float x0 = state.X0 + key % PagesPerZone * PageSize, z0 = state.Z0 + key / PagesPerZone * PageSize;
                float d = BoxDistance(x0, z0, PageSize, eye);
                float y = state.Ground is { } ground ? ground.Height(x0 + PageSize / 2, z0 + PageSize / 2) : eye.Y;
                if (!WorldCamera.Intersects(_motionFrustum, new Vector3(x0, y - 2000, z0), new Vector3(x0 + PageSize, y + 2000, z0 + PageSize)))
                    continue;
                for (int i = 0; i < page.Buffers.Length && i < state.Patches.Count; i++)
                {
                    var patch = state.Patches[i];
                    var g = patch.Grass;
                    if (!patch.Layer.Wind || g.SwayLength == 0) continue;
                    var b = Patch(page.Buffers[i], patch);
                    float range = GrassRange(patch);
                    if (b.Shown == 0 || d >= GrassMotionReach.Reach(range, g.SwayLength, _motionScale)) continue;
                    if (b.Sprite is not { Key: not 0 } sprite) continue;
                    _motionDraws.Add(new MotionDraw
                    {
                        Buffer = b,
                        Vertices = g.CrossQuads ? 12u : 6u,
                        Sprite = sprite.Key,
                        Push = new GrassPush
                        {
                            Size = new Vector4(g.QuadMinWidth, g.QuadMaxWidth, g.QuadMinHeight, g.QuadMaxHeight),
                            Sway = g.SwayLength,
                            Range = range,
                            Frequency = 2f,
                        },
                    });
                }
            }
    }

    void RecordMotion(MotionTargets targets, Vector3 eye, float time, float previousTime, Matrix4x4 previous)
    {
        var gp = _grassMotionProgram;
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
            ViewProjection = _motionViewProjection,
            PreviousViewProjection = previous,
            Eye = eye,
            Time = time,
            PreviousTime = previousTime,
            NearPlanes = targets.Planes,
            JitterNdc = targets.JitterNdc,
        };
        _nativeFrame.Bind(cmd, p, in view);
        NewTextureSegment();
        uint nearDepth = NearDepthIndex(targets.Depth);
        GrassPush last = default;
        bool pushed = false;
        cmd.BindPipeline(PipelineFor(ref _grass.MeshMotionCpu, gp, segment, drawState, pass.Formats, "foliage grass motion"));
        cmd.BindVertexBuffers(0, _grass.MeshMotionCpu.Vertices);
        foreach (ref readonly var d in CollectionsMarshal.AsSpan(_motionDraws))
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

    #endregion

    #region GPU timing

    // GPU timing with the frame's timestamps (QueryArena), written where the GL version wrote its timestamp queries (through the seam's
    // Interleave: into the frame, or a host's inline secondary), read once the frame's slot has been collected (a frame ring later).
    readonly record struct Timer(QuerySlot Start, QuerySlot End);

    readonly List<Timer> _timers = [];
    QuerySlot _timerStart, _pendingStamp;
    Action<IGpuCommandList>? _recordStamp;
    double _gpuSum;
    int _gpuCount;

    void Stamp(QuerySlot slot)
    {
        _pendingStamp = slot;
        Gpu.Interleave(_recordStamp ??= cmd => cmd.Timestamp(Gpu.Frame.Timestamps, _pendingStamp));
    }

    int BeginTimer()
    {
        if (_timers.Count >= 64) return -1;
        Stamp(_timerStart = Gpu.Frame.Timestamps.Allocate());
        return _timerStart.IsValid ? 0 : -1;
    }

    void EndTimer(int index)
    {
        if (index < 0) return;
        var end = Gpu.Frame.Timestamps.Allocate();
        Stamp(end);
        if (end.IsValid) _timers.Add(new Timer(_timerStart, end));
    }

    /// <summary>
    /// Collects finished timings into <see cref="GpuMs"/> (per <see cref="Update"/>, i.e. per frame). The timestamps are read once their frame's slot
    /// has been collected, never waited for (<paramref name="wait"/> is kept for the callers; it no longer blocks).
    /// </summary>
    public void PollTimers(bool wait)
    {
        _ = wait;
        var arena = Gpu.Frame.Timestamps;
        for (int i = 0; i < _timers.Count; i++)
        {
            var t = _timers[i];
            if (arena.TryRead(t.Start, out ulong start) && arena.TryRead(t.End, out ulong end))
            {
                _gpuSum += (end - start) / 1e6;
                _gpuCount++;
            }
            else if (Gpu.Frame.Number - t.Start.Frame <= 2 * Gpu.FramesInFlight) continue;   // not collected yet
            _timers.RemoveAt(i--);
        }
        if (_gpuCount > 0)
        {
            GpuMs = _gpuSum / _gpuCount;
            _gpuSum = 0;
            _gpuCount = 0;
        }
    }

    #endregion

    #region Traced scene (docs/render-gi.md "Foliage")

    /// <summary>
    /// The foliage in the ray-traced scene of the global illumination: the meshes as it takes them.
    /// </summary>
    readonly FoliageRayScene _rays;

    /// <summary>
    /// Counts the foliage meshes deleted: a traced scene built before a change holds structures and records of freed buffers.
    /// </summary>
    internal int RayGeneration => _rays.Generation;

    /// <summary>
    /// Adds every laid-out instance whose bounding radius is at least <paramref name="minRadius"/> and whose bounding sphere comes within
    /// <paramref name="range"/> of <paramref name="eye"/> (<see cref="FoliageRayScene.Instances"/>). Render thread.
    /// </summary>
    internal void RayInstances(Vector3 eye, float range, float minRadius, List<(FoliageRayMesh Mesh, Matrix4x4 Transform, float Distance)> into) =>
        _rays.Instances(eye, range, minRadius, into);

    /// <summary>
    /// Sets <paramref name="mesh"/>'s textures for this frame (<see cref="FoliageRayScene.RefreshTextures"/>). Render thread.
    /// </summary>
    internal void RefreshRayTextures(FoliageRayMesh mesh) => _rays.RefreshTextures(mesh);

    #endregion

    #region Lifetime

    /// <summary>
    /// Waits for the layout, grass and level jobs, then frees the pages, the meshes, the textures and the programs.
    /// </summary>
    public void Dispose()
    {
        _grass.BeginDispose();   // the pages are freed with the store
        foreach (var z in _zones.Values)
        {
            Wait(z.Job);
            foreach (var p in z.Pages.Values)
            {
                Wait(p.Job);
                _grass.FreePage(p);
            }
        }
        foreach (var t in _lodJobs.Tasks) Wait(t);
        foreach (var a in _assetsByMesh.Values)
        {
            Wait(a.Job);
            a.Lod?.Dispose();
            foreach (var m in new[] { a.Main, a.Leaves })
                if (m is not null) DeleteMesh(m);
        }
        lock (_allWorlds) foreach (var w in _allWorlds) w.Dispose();
        _textures.Dispose();
        _colourMesh.Dispose();
        _colourSolidMesh.Dispose();
        _depthMesh.Dispose();
        _prepassMesh.Dispose();
        _prepassSolidMesh.Dispose();
        _grassProgram.Dispose();
        _grassMotionProgram.Dispose();
        _grass.Dispose();
        _gpuCull?.Dispose();
        DisposeImpostors();
        _nativeFrame.Dispose();
    }

    /// <summary>
    /// Waits for a job if there is one; its failure was reported (or does not matter) at shutdown.
    /// </summary>
    static void Wait(Task? job)
    {
        try
        {
            job?.Wait();
        }
        catch (AggregateException)
        {
        }
    }

    #endregion
}

/// <summary>
/// A presentation-only move of placed foliage meshes (<see cref="FoliageRenderer.Shift"/>): those named so by <paramref name="Moves"/> within
/// <paramref name="Radius"/> of <paramref name="Centre"/> (x, z) move by <paramref name="Offset"/> (x, z) and onto <paramref name="Ground"/> there.
/// </summary>
public sealed record FoliageShift(Vector3 Centre, float Radius, Vector3 Offset, Func<string, bool> Moves, Func<float, float, float> Ground);