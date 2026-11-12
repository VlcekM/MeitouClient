using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Impostors;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// Far billboards (docs/impostors.md "Drawing"; the <c>impostors</c> switch). A foliage mesh with an impostor atlas is drawn, from its transition
/// distance on, as one camera-facing quad per instance sampling the atlas (<see cref="ImpostorDraw"/>), chosen per instance by the same cull
/// that places the meshes (its kernels, or <see cref="FoliageCull.CullGroup"/> on the CPU), crossfaded with the mesh over a band before the
/// transition by complementary dither. Shadow cascades get the impostors as casters beyond the transition (a depth-only quad facing the sun).
/// Atlases are loaded from the disk cache or baked on first use (<see cref="ImpostorBaker"/>), on demand when a group of the mesh comes
/// within reach of its transition, and unloaded with the meshes when unused.
/// <para>
/// <b>Residency</b> (docs/impostors.md section 10): once a second the scan collects the atlases wanted (<see cref="_impostorWanted"/>: the nearest
/// ground at which any zone would draw each as an impostor) and <see cref="PlanImpostors"/> decides, nearest first, which fit the
/// <see cref="ImpostorLimitMb"/> budget (a share of the card's, <see cref="ImpostorBudget"/>) and at which mip: an atlas holds only the levels its
/// nearest instance can sample (<see cref="SkipFor"/>), refined (reloaded from the disk cache and swapped in) when an instance approaches, coarsened
/// when the budget needs it; when even the coarsest allowed mip does not fit, the farthest atlases stay meshes.
/// </para>
/// </summary>
public sealed partial class FoliageRenderer
{
    #region Settings and atlas state

    /// <summary>
    /// The default transition distance of small and medium meshes (units along the ground; the Tab slider "Impostor distance").
    /// </summary>
    public const float DefaultImpostorDistance = 4000;
    /// <summary>
    /// The default transition distance of the large size class (trees, rock stacks, hoodoos; the Tab slider "Large impostor distance"): their
    /// billboards look flat up close.
    /// </summary>
    public const float DefaultLargeImpostorDistance = 12000;
    /// <summary>
    /// The crossfade band before the transition, as a share of it.
    /// </summary>
    public const float ImpostorBand = 0.1f;
    /// <summary>
    /// How long an atlas the memory refused waits before it is asked for again, in seconds.
    /// </summary>
    const double ImpostorRetrySeconds = 2;
    /// <summary>
    /// The share of the limit a refine's new textures may take over it while the old ones are still drawn.
    /// </summary>
    const double ImpostorTransientShare = 1.1;
    /// <summary>
    /// Levels of safety on the mip an atlas needs: see <see cref="PlanImpostors"/>.
    /// </summary>
    const float FarMipMargin = 1f;
    /// <summary>
    /// The most levels beyond what an atlas's nearest instance needs the plan takes off an atlas that would not fit (a blurrier far crown instead of
    /// a mesh).
    /// </summary>
    const int ImpostorMaxExtraLevels = 3;
    /// <summary>
    /// A resident atlas counts as this much nearer in the plan's order, so a newcomer must be that much nearer to take its place (hysteresis).
    /// </summary>
    const float ImpostorStickiness = 0.8f;
    /// <summary>
    /// Seconds an atlas must hold a finer level than needed before it is coarsened without budget pressure (and by at least two levels).
    /// </summary>
    const double ImpostorCoarsenSeconds = 10;
    /// <summary>
    /// Refines (reload + upload + swap) in flight at once.
    /// </summary>
    const int ImpostorMaxRefines = 3;
    /// <summary>
    /// The seconds of the eye's motion the order of the waiting bakes looks ahead.
    /// </summary>
    const float ImpostorLookaheadSeconds = 3;
    /// <summary>
    /// The most shaded samples of a bake recorded in one frame (4 million; a row is <c>grid x (2 x frame)^2 x 3</c> of them; 4 since 2026-10-08 (it
    /// was 40, which cost 3 to 6 ms of GPU time in every frame with a bake step: the "other" spikes), docs/impostors.md section 8). A row is the
    /// least a step records: a large atlas (256 pixel frames, 9.4 million a row) takes one row a frame (twelve frames), a 128 pixel one (2.4 million)
    /// one row (twelve frames), a 64 pixel one (0.6 million) six rows (two frames).
    /// </summary>
    const double ImpostorBakeSamplesPerFrame = 4e6;
    /// <summary>
    /// The pooled bake memory (readback buffers, atlas levels, filtering scratch) is freed this long after the last bake.
    /// </summary>
    const long ImpostorBakeIdleMs = 10000;
    /// <summary>
    /// Atlas bytes uploaded per frame while settling; <see cref="_impostorUploadsWaiting"/>: more wait for the next frame.
    /// </summary>
    const long SettleUploadBytes = 64L << 20, UploadBytesPerFrame = 6L << 20;
    /// <summary>
    /// Milliseconds of a frame's update the atlas uploads may run for (besides the byte budget: the copy into staging runs from 1 to 9 GB/s, so 6 MB
    /// took 0.7 to 5 ms); the first step always runs.
    /// </summary>
    const double UploadMsPerFrame = 2;
    /// <summary>
    /// The limit on the resident atlases in force (<see cref="ImpostorLimitMb"/>), in bytes; set once a scan.
    /// </summary>
    long _impostorLimitBytes = (long)(ImpostorBudget.UnknownMb * 1048576);
    /// <summary>
    /// <see cref="Environment.TickCount64"/> at the last bake's start, for releasing the bake memory after <see cref="ImpostorBakeIdleMs"/>.
    /// </summary>
    long _impostorLastBake;
    /// <summary>
    /// Whether the baker's targets and the assembler's pools are held since a bake (released once idle).
    /// </summary>
    bool _impostorBakeMemoryHeld;
    /// <summary>
    /// The atlas disk cache.
    /// </summary>
    readonly ImpostorCache _impostorCache = new();
    /// <summary>
    /// Whether the first scan has started the cache's upkeep and made the programs (<see cref="PrepareImpostorDrawing"/>).
    /// </summary>
    bool _impostorCacheChecked;
    /// <summary>
    /// The baker, made at the first scan.
    /// </summary>
    ImpostorBaker? _impostorBaker;
    /// <summary>
    /// The bake in progress (null: none).
    /// </summary>
    ImpostorBakeJob? _impostorBake;
    /// <summary>
    /// Times this frame's atlas uploads against <see cref="UploadMsPerFrame"/>.
    /// </summary>
    readonly System.Diagnostics.Stopwatch _impostorWatch = new();
    /// <summary>
    /// While settling: uploads were left for the next frame (the loading screen keeps drawing frames).
    /// </summary>
    bool _impostorUploadsWaiting;
    /// <summary>
    /// The asset whose atlas <see cref="_impostorBake"/> bakes.
    /// </summary>
    FoliageMeshAsset? _impostorBakeAsset;
    /// <summary>
    /// The impostor programs, made at the first scan.
    /// </summary>
    ImpostorDraw? _impostorDraw;
    /// <summary>
    /// Assets waiting to be baked, in order.
    /// </summary>
    readonly List<FoliageMeshAsset> _impostorBakes = [];
    /// <summary>
    /// Assets whose atlas is loading from the cache or uploading.
    /// </summary>
    readonly List<FoliageMeshAsset> _impostorWork = [];
    /// <summary>
    /// Resident atlases being refined or coarsened (their new textures loading or uploading).
    /// </summary>
    readonly List<FoliageMeshAsset> _impostorRefining = [];
    /// <summary>
    /// The resident atlases' bytes, and <see cref="Environment.TickCount64"/> at the last scan.
    /// </summary>
    long _impostorBytes, _lastImpostorScan;
    /// <summary>
    /// Counts for the statistics line (<see cref="ImpostorDescription"/>): atlases baked, loaded from the cache, unloaded, refined, coarsened,
    /// refused by the memory and dropped by the plan.
    /// </summary>
    int _impostorsBaked, _impostorsLoaded, _impostorUnloads, _impostorRefined, _impostorCoarsened, _impostorRefused, _impostorDropped;
    /// <summary>
    /// What the last plan found (see <see cref="ImpostorPopIn"/>).
    /// </summary>
    int _impostorWantedCount, _impostorAdmittedCount, _impostorMissingCount, _impostorCoarseCount, _impostorExtraCount;
    /// <summary>
    /// The bytes the last plan admitted (see <see cref="ImpostorPopIn"/>).
    /// </summary>
    long _impostorPlannedBytes;
    /// <summary>
    /// The <c>impostors</c> switch (Enhancements): Meitou (default) draws far instances as impostors; Faithful never loads or draws one.
    /// </summary>
    public bool Impostors { get; set; } = true;
    /// <summary>
    /// The transition distance (units along the ground) of meshes below the large size class (the small impostor class scales it by size).
    /// </summary>
    public float ImpostorDistance { get; set; } = DefaultImpostorDistance;
    /// <summary>
    /// The transition distance of meshes of the large size class (<see cref="FoliageSizes.LargeFrom"/> and up), instead of
    /// <see cref="ImpostorDistance"/>.
    /// </summary>
    public float LargeImpostorDistance { get; set; } = DefaultLargeImpostorDistance;
    /// <summary>
    /// An explicit limit on the video memory the resident atlases may use, in megabytes (<c>--impostor-budget</c>);
    /// null: the default rule, a share of the card's budget (<see cref="ImpostorBudget"/>, <see cref="ImpostorLimitMb"/>).
    /// </summary>
    public double? ImpostorBudgetMb { get; set; }
    /// <summary>
    /// The limit in force as of the last scan (MB): the override or the default rule, three quarters of it under the guard's pressure.
    /// </summary>
    public double ImpostorLimitMb => _impostorLimitBytes / 1048576.0;
    /// <summary>
    /// The atlas disk cache (the Tab panel's delete button). Deleting its files leaves the resident atlases: they are baked again when next needed.
    /// </summary>
    public ImpostorCache ImpostorDiskCache => _impostorCache;
    /// <summary>
    /// The most the atlas disk cache may take, in megabytes (4096 by default; 0 is no cap): older files go
    /// first, and files of an old format or baker version at once (<see cref="ImpostorCache.Maintain"/>, which runs on a worker at the first use
    /// and after every 16 atlases saved).
    /// </summary>
    public double ImpostorCacheMb
    {
        get => _impostorCache.MaxBytes / 1048576.0;
        set => _impostorCache.MaxBytes = (long)(value * 1048576);
    }
    /// <summary>
    /// What the last scan's plan left unfinished, for the benchmark's pop-in line: atlases wanted that stay meshes because the plan left them out
    /// (<c>NotAdmitted</c>), admitted ones not resident yet (<c>Missing</c>: meshes are drawn until they are), resident ones held coarser than their
    /// nearest instance wants while a refine is on its way (<c>Coarse</c>: a blurrier crown for a moment), and the refines in flight.
    /// </summary>
    public (int NotAdmitted, int Missing, int Coarse, int Refining) ImpostorPopIn => (_impostorWantedCount - _impostorAdmittedCount,
        _impostorMissingCount, _impostorCoarseCount, _impostorRefining.Count);
    /// <summary>
    /// Atlases loading, waiting to bake, baking or uploading (part of <see cref="Pending"/>).
    /// </summary>
    int ImpostorPending => _impostorWork.Count + _impostorBakes.Count + (_impostorBake is null ? 0 : 1);
    /// <summary>
    /// The atlases a loading screen waits for: those read from the cache and uploading. Not the reads still queued (behind a long setting's far grass
    /// jobs on the workers they can wait half a minute) nor the bakes and their uploads: the meshes draw until each atlas is ready.
    /// </summary>
    int ImpostorUploadsAwaited
    {
        get
        {
            int n = 0;
            foreach (var a in _impostorWork) if (a.Impostor is { Stage: FoliageImpostorStage.Uploading, Baked: false }) n++;
            return n;
        }
    }
    /// <summary>
    /// The atlases' part of the statistics line: resident atlases and bytes against the limit, the counts and the last plan.
    /// </summary>
    string ImpostorDescription => $"{ImpostorAssets().Count(a => a.Impostor?.Stage == FoliageImpostorStage.Ready)} impostor atlases " +
        $"{_impostorBytes / 1048576.0:0} MB of {ImpostorLimitMb:0} MB " +
        $"({_impostorsLoaded} from the cache, {_impostorsBaked} baked, {_impostorUnloads} unloaded, {_impostorRefined} refined, " +
        $"{_impostorCoarsened} coarsened, {_impostorDropped} dropped by the plan, {_impostorRefused} refused; " +
        $"plan {_impostorAdmittedCount} of {_impostorWantedCount} wanted, {_impostorMissingCount} not resident, " +
        $"{_impostorExtraCount} blurrier than needed, {_impostorPlannedBytes / 1048576.0:0} MB planned)";

    /// <summary>
    /// Turns impostors off and releases every atlas already resident (the Metal host's workaround while the atlas path is broken). A later
    /// <see cref="Impostors"/> true can load them again.
    /// </summary>
    public void DisableImpostors()
    {
        Impostors = false;
        foreach (var a in ImpostorAssets().ToArray())
            if (a.Impostor is not null) UnloadImpostor(a);
        DisposeImpostors();
    }

    /// <summary>
    /// Whether this frame may upload more atlas levels: always while settling, else until <see cref="UploadMsPerFrame"/> went on it.
    /// </summary>
    bool UploadTimeLeft(bool settling) => settling || _impostorWatch.Elapsed.TotalMilliseconds < UploadMsPerFrame;

    #endregion

    #region TERRAIN-mode rocks (docs/impostors.md section 13)

    /// <summary>
    /// A TERRAIN-mode mesh whose triangles cover less than this share of its bounding sphere's surface gets no impostor.
    /// </summary>
    const float RockMinSurface = 0.06f;
    /// <summary>
    /// The nearest a <c>screen-lod</c> transition goes, whatever the sizes: ground units.
    /// </summary>
    const float ScreenLodMinTransition = 600;
    /// <summary>
    /// The terrain of the last <see cref="Draw"/> (the rock bake's material; null before the first), and whether it draws textured (rocks are
    /// TERRAIN-mode only then).
    /// </summary>
    TerrainRenderer? _rockTerrain;
    /// <summary>
    /// The frame's options' textures switch (rock impostors need it), set at each update.
    /// </summary>
    bool _drawTextures = true;
    /// <summary>
    /// The frame's options' material distance, which the rocks' far fade starts from, set at each update.
    /// </summary>
    float _drawMaterialDistance = 30000;
    /// <summary>
    /// The holders of the rocks' per-biome impostors (see <see cref="FoliageMeshAsset.RockVariants"/>), for the loops that go over every atlas.
    /// </summary>
    readonly List<FoliageMeshAsset> _rockHolders = [];
    /// <summary>
    /// The terrain the TERRAIN-mode rocks are textured by and baked with; set by the world frame (so the offscreen settle, which updates before it
    /// draws, has it), and by every <see cref="Draw"/>.
    /// </summary>
    public TerrainRenderer? Terrain { get => _rockTerrain; set => _rockTerrain = value; }
    /// <summary>
    /// Rock impostors are on: the impostors Enhancement, the GPU cull (the CPU cull has no rock impostors) and textures.
    /// </summary>
    bool RockImpostorsActive => Impostors && GpuCull && _gpuCull is not null && _drawTextures && _rockTerrain is not null;

    /// <summary>
    /// Every asset that can hold an atlas: the meshes and the rocks' per-biome holders.
    /// </summary>
    IEnumerable<FoliageMeshAsset> ImpostorAssets() => _assetsByMesh.Values.Concat(_rockHolders);

    /// <summary>
    /// The holder of a rock's impostor for the biome of terrain row <paramref name="row"/> (made on first ask).
    /// </summary>
    FoliageMeshAsset RockVariant(FoliageMeshAsset rock, int row)
    {
        rock.RockVariants ??= [];
        if (!rock.RockVariants.TryGetValue(row, out var v))
        {
            rock.RockVariants[row] = v = new FoliageMeshAsset
            {
                Mesh = rock.Mesh,
                RockOf = rock,
                RockRow = row,
                Centre = rock.Centre,
                Radius = rock.Radius,
                HasBounds = rock.HasBounds,
                SizeClass = rock.SizeClass,
                Triangles = rock.Triangles,
                Resident = true,
            };
            _rockHolders.Add(v);
        }
        return v;
    }

    /// <summary>
    /// The holder of a rock's impostor for terrain row <paramref name="row"/> when its atlas is resident, else null.
    /// </summary>
    static FoliageMeshAsset? ReadyRock(FoliageMeshAsset rock, int row) =>
        row >= 0 && rock.RockVariants is { } vs && vs.TryGetValue(row, out var v) && v.Impostor is { Stage: FoliageImpostorStage.Ready } ? v : null;

    /// <summary>
    /// A mesh's name for the log; a rock's per-biome holder adds its terrain row.
    /// </summary>
    static string Label(FoliageMeshAsset a) => a.RockOf is null ? a.Mesh.Name : $"{a.Mesh.Name} [biome row {a.RockRow}]";

    /// <summary>
    /// The class of a mesh's atlas from its largest instance's radius: TERRAIN-mode rocks have their own largest frame.
    /// </summary>
    static ImpostorClass? ClassFor(FoliageMeshAsset a, float worldRadius, int triangles) => a.Terrain ? ImpostorClass.ForRock(worldRadius,
        triangles) : ImpostorClass.For(worldRadius, triangles);

    /// <summary>
    /// The class of an atlas that is not loaded yet, estimated from the mesh's own bounds (the exact one comes with the load).
    /// </summary>
    static ImpostorClass? EstimateClass(FoliageMeshAsset a)
    {
        if (!a.ClassEstimated && a.HasBounds)
        {
            // The mesh's half-diagonal is never less than the farthest vertex from the box's centre (what the baker measures), so this is the class's
            // upper bound: a mesh it rejects can never get one, and one it accepts may still be rejected by the load.
            a.EstimatedClass = ClassFor(a, a.Radius * a.Mesh.MaxScale * 1.02f, a.Triangles);
            a.ClassEstimated = true;
        }
        return a.EstimatedClass;
    }

    /// <summary>
    /// The ground distance from which a mesh would be its impostor, for an atlas of class <paramref name="cls"/> (exact once loaded): the impostor
    /// distance for medium and large atlases, in proportion to the size for the small class.
    /// </summary>
    float TransitionFor(FoliageMeshAsset a, in ImpostorClass cls, float worldRadius, FoliageGroup? g = null)
    {
        float t = cls.Transition(worldRadius, DistanceFor(a));
        return g is null || !ScreenLod ? t : ScreenTransition(a, cls, t, g);
    }

    /// <summary>
    /// The transition of a group under the <c>screen-lod</c> rule (<see cref="FoliageScreenLod.Transition"/>): the billboard replaces the mesh where
    /// the mesh's mean triangle (at the coarsest level it can be drawn at) is under <see cref="ScreenLodTriPixels"/> square pixels for the group's
    /// largest instance, but not nearer than its atlas frame can be magnified (the atlas was made for the largest instance at
    /// <paramref name="baseTransition"/>, so smaller instances of the group, and meshes with tiny triangles, switch nearer at the same size on the
    /// screen).
    /// </summary>
    float ScreenTransition(FoliageMeshAsset a, in ImpostorClass cls, float baseTransition, FoliageGroup g)
    {
        if (!(ScreenLodTriPixels > 0) || !a.HasBounds) return baseTransition;
        float ppr = ScreenLodPixelsPerRadian;
        int triangles = a.Lod is { } lod ? lod.Set.Triangles[lod.Levels - 1] : a.Triangles;
        var key = (ppr, ScreenLodTriPixels, triangles, cls.FramePixels);
        if (a.ScreenKey != key)
        {
            float extent = FoliageScreenLod.LevelExtent(a.TriExtent, a.Triangles, triangles) / a.Radius;
            a.ScreenPerRadian = extent > 0 ? FoliageScreenLod.TransitionPerRadius(extent, ppr, ScreenLodTriPixels) : 0;
            a.ScreenFloor = FoliageScreenLod.FloorPerRadius(cls.FramePixels, ppr, ImpostorClass.Magnification);
            a.ScreenKey = key;
        }
        if (!(a.ScreenPerRadian > 0)) return baseTransition;
        float radius = g.BoundsReady && g.MaxRadius > 0 ? g.MaxRadius : a.Radius * a.Mesh.MaxScale;
        return Math.Min(baseTransition, Math.Max(FoliageScreenLod.Transition(baseTransition, radius, a.ScreenPerRadian, a.ScreenFloor),
            ScreenLodMinTransition));
    }

    /// <summary>
    /// The impostor distance of a mesh:<see cref="LargeImpostorDistance"/> for large meshes other than trees (rocks, rock stacks, hoodoos, ruins;
    /// <see cref="FoliageSizes.LargeBillboard"/>), else <see cref="ImpostorDistance"/>.
    /// </summary>
    float DistanceFor(FoliageMeshAsset a) => a.HasBounds && a.LargeBillboard ? LargeImpostorDistance : ImpostorDistance;

    /// <summary>
    /// The transition of an atlas not made yet, from the class estimated from the mesh's bounds (infinite when it has none).
    /// </summary>
    float EstimatedTransition(FoliageMeshAsset a, FoliageGroup? g = null) => EstimateClass(a) is { } c ? TransitionFor(a, c, a.Radius * a.Mesh.MaxScale,
        g) : float.PositiveInfinity;

    /// <summary>
    /// The ground distance from which a mesh is its impostor (infinite without a resident atlas or with the switch off).
    /// </summary>
    float TransitionOf(FoliageMeshAsset a, FoliageGroup? g = null)
    {
        if (!Impostors) return float.PositiveInfinity;

        // A rock has atlases per biome, one class for all of them (it follows from the mesh): the transition of any that is resident.
        if (a.Terrain)
        {
            if (!RockImpostorsActive || a.RockVariants is not { } variants) return float.PositiveInfinity;
            foreach (var v in variants.Values)
                if (v.Impostor is { Stage: FoliageImpostorStage.Ready } r) return TransitionFor(a, r.Class, r.WorldRadius, g);
            return float.PositiveInfinity;
        }
        return a.Impostor is { Stage: FoliageImpostorStage.Ready } s ? TransitionFor(a, s.Class, s.WorldRadius, g) : float.PositiveInfinity;
    }

    /// <summary>
    /// A group's range for a view with its impostor, and which parts the view draws (<see cref="FoliageCull.MeshPart"/>,
    /// <see cref="FoliageCull.ImpostorPart"/>): the meshes when the zone has ground before the transition, the impostors when it has ground in
    /// or beyond the crossfade band (with a margin for instances on the zone's edge). No impostor when the transition is not before the range's
    /// own fade band (the two fades never overlap) or the view takes none.
    /// </summary>
    (FoliageGroupRange Range, int Parts) WithImpostor(FoliageGroupRange r, FoliageGroup g, float range, float band, FoliageZoneState zone,
        Vector3 eye, bool view)
    {
        var a = g.Asset;
        if (!view) return (r, FoliageCull.MeshPart);
        if (a.Terrain && !RockImpostorsActive) return (r, FoliageCull.MeshPart);
        float t = TransitionOf(a, g);
        if (_depthPass && _lodCoarse && !a.Terrain && a.Impostor is { Stage: FoliageImpostorStage.Ready } ready)
            t = Math.Min(t, ShadowLod.ImpostorTransition(ready.WorldRadius, _lodTexel));   // a far cascade: the flat caster for what is small in texels
        if (!(t <= range - band)) return (r, FoliageCull.MeshPart);
        float b = t * ImpostorBand;
        const float edge = 64;
        float near = ZoneDistance(zone.Zone, eye), far = ZoneFarDistance(zone, eye);
        int parts = (near - edge < t ? FoliageCull.MeshPart : 0) | (far + edge >= t - b ? FoliageCull.ImpostorPart : 0);
        if (parts == 0) parts = FoliageCull.MeshPart;
        return (r.WithTransition(t, b), parts);
    }

    /// <summary>
    /// The ground distance from <paramref name="eye"/> to the farthest corner of the zone.
    /// </summary>
    static float ZoneFarDistance(FoliageZoneState z, Vector3 eye)
    {
        float dx = Math.Max(Math.Abs(eye.X - z.X0), Math.Abs(eye.X - (z.X0 + WorldLayout.ZoneSize)));
        float dz = Math.Max(Math.Abs(eye.Z - z.Z0), Math.Abs(eye.Z - (z.Z0 + WorldLayout.ZoneSize)));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    #endregion

    #region Residency: wanted, plan, refine

    /// <summary>
    /// The atlases wanted this scan, with the nearest ground distance of any zone that would draw them as impostors (before the transition floor).
    /// </summary>
    readonly Dictionary<FoliageMeshAsset, float> _impostorWanted = [];
    /// <summary>
    /// The last plan's atlases, nearest first (<see cref="PlanImpostors"/>).
    /// </summary>
    readonly List<PlanItem> _impostorPlan = [];
    /// <summary>
    /// Atlases the plan wants remade (finer or coarser), from the last scan: <see cref="StartQueuedRefines"/> starts them as slots free up.
    /// </summary>
    readonly List<FoliageMeshAsset> _impostorRefineQueue = [];
    /// <summary>
    /// The picture height the atlas resolution is chosen for (<see cref="SkipFor"/>): 1080 lines (above it the nearest impostors are slightly
    /// softer).
    /// </summary>
    const float ImpostorReferenceHeight = 1080;
    /// <summary>
    /// Impostor atlases unused this long are unloaded (shorter than the meshes' <see cref="IdleSeconds"/>: an atlas is 9 to 36 MB).
    /// </summary>
    const double ImpostorIdleSeconds = 20;
    /// <summary>
    /// The eye moved at least this far between two scans: a jump, after which only what the plan admitted stays resident.
    /// </summary>
    const float ImpostorJumpDistance = 10000;
    /// <summary>
    /// The eye of the last scan (null before the first), to tell a jump of over <see cref="ImpostorJumpDistance"/>.
    /// </summary>
    Vector3? _impostorScanEye;

    /// <summary>
    /// One wanted atlas in the plan: its mip need and sort key, its shape and what the plan gave it.
    /// </summary>
    struct PlanItem
    {
        /// <summary>
        /// The asset whose atlas this is.
        /// </summary>
        public FoliageMeshAsset Asset;
        /// <summary>
        /// The ground distance the mip is chosen for (never inside the transition), and the sort key (a resident atlas's scaled by
        /// <see cref="ImpostorStickiness"/>).
        /// </summary>
        public float Need, Key;
        /// <summary>
        /// The atlas's frame pixels, grid and levels (the baked atlas's, else the estimated class's).
        /// </summary>
        public int Frame, Grid, Levels;
        /// <summary>
        /// The largest instance's radius, in world units.
        /// </summary>
        public float Radius;
        /// <summary>
        /// The top levels the need leaves out, and the extra ones the limit took off.
        /// </summary>
        public int BaseSkip, Extra;
        /// <summary>
        /// Whether the plan keeps the atlas (else it stays a mesh).
        /// </summary>
        public bool Admit;
        /// <summary>
        /// The atlas's bytes at the plan's mip.
        /// </summary>
        public long Bytes;
    }

    /// <summary>
    /// The top albedo levels an atlas never needs when its nearest instance is <paramref name="distance"/> units away (docs/impostors.md 7, "VRAM"):
    /// there the instance's sphere (<paramref name="worldRadius"/> = radius × the mesh's largest scale) spans r·H / (tan(fov / 2)·d) pixels at the
    /// reference height and the camera's 50° field of view; a level whose frames are at least that big is kept, larger ones are left out.
    /// </summary>
    internal static int SkipFor(int framePixels, int levels, float worldRadius, float distance)
    {
        float pixels = worldRadius * ImpostorReferenceHeight / (MathF.Tan(25 * MathF.PI / 180) * Math.Max(distance, 1));
        int skip = 0;
        while (skip < levels - 1 && (framePixels >> (skip + 1)) >= pixels) skip++;
        return skip;
    }

    /// <summary>
    /// The GPU bytes of an atlas of <paramref name="grid"/> frames of <paramref name="framePixels"/> with <paramref name="skip"/> top albedo levels
    /// (one more of the normal map) left out: BC1 and BC5, same as <see cref="ImpostorTextures.BytesFor"/>.
    /// </summary>
    internal static long AtlasBytes(int framePixels, int grid, int skip)
    {
        int levels = BitOperations.Log2((uint)framePixels) - 1;
        skip = Math.Clamp(skip, 0, levels - 1);
        int normalSkip = Math.Clamp(skip + 1, 0, levels - 1);
        long side = (long)grid * framePixels, bytes = 0;
        for (int l = skip; l < levels; l++) bytes += (side >> l) * (side >> l) / 2;
        for (int l = normalSkip; l < levels; l++) bytes += (side >> l) * (side >> l);
        return bytes;
    }

    /// <summary>
    /// The limit from the current budget and pressure (once a scan).
    /// </summary>
    void UpdateImpostorLimit()
    {
        long budget = _guard is { BudgetBytes: > 0 } g ? g.BudgetBytes : (long)Math.Min(Gpu.VideoMemoryBudget, (ulong)long.MaxValue);
        _impostorLimitBytes = (long)(ImpostorBudget.LimitMb(ImpostorBudgetMb, budget, Gpu.IsIntegrated, _guard is { Pressure: true }) * 1048576);
    }

    /// <summary>
    /// Decides, nearest first, which wanted atlases fit the limit and at which mip (<see cref="_impostorWanted"/> in, <see cref="_impostorPlan"/>
    /// out). A resident atlas counts as nearer (<see cref="ImpostorStickiness"/>). An atlas that does not fit at the mip its nearest instance needs
    /// may take up to <see cref="ImpostorMaxExtraLevels"/> more off; one that still does not fit stays a mesh.
    /// </summary>
    void PlanImpostors(long now)
    {
        _impostorPlan.Clear();

        // The distance the mip is chosen for is the nearest instance's divided by 2^margin: the sampler's lod at the screen's edge is up to half a
        // level finer than the centre's (a ray off the axis crosses a frame plane at a larger angle) and the upscaler's texture bias (negative) makes
        // it finer still.
        float margin = MathF.Pow(2, -(FarMipMargin + Math.Max(0, -Gpu.LodBias)));
        foreach (var (a, raw) in _impostorWanted)
        {
            var s = a.Impostor;
            if (s is { Stage: FoliageImpostorStage.None, RetryAt: 0 }) continue;   // never an impostor: ineligible, too small or unreadable
            int frame, grid, levels;
            float radius, t;
            if (s is { FramePixels: > 0 })
            {
                (frame, grid, levels, radius) = (s.FramePixels, s.Grid, s.Levels, s.WorldRadius);
                t = TransitionFor(a, s.Class, s.WorldRadius);
            }
            else
            {
                if (EstimateClass(a) is not { } est) continue;
                (frame, grid, levels, radius) = (est.FramePixels, est.Grid, est.Levels, a.Radius * a.Mesh.MaxScale);
                t = TransitionFor(a, est, radius);
            }

            // Far mips: the nearest instance's ground distance, never inside the transition (an impostor is not drawn nearer).
            float need = Math.Max(raw * margin, t);
            _impostorPlan.Add(new PlanItem
            {
                Asset = a,
                Need = need,
                Key = need * (s is { Stage: FoliageImpostorStage.Ready } ? ImpostorStickiness : 1f),
                Frame = frame,
                Grid = grid,
                Levels = levels,
                Radius = radius,
            });
        }
        _impostorPlan.Sort((x, y) => x.Key.CompareTo(y.Key));
        long limit = _impostorLimitBytes, total = 0;
        for (int i = 0; i < _impostorPlan.Count; i++)
        {
            var it = _impostorPlan[i];
            it.BaseSkip = SkipFor(it.Frame, it.Levels, it.Radius, it.Need);
            (it.Admit, it.Extra, it.Bytes) = (true, 0, AtlasBytes(it.Frame, it.Grid, it.BaseSkip));
            total += it.Bytes;
            _impostorPlan[i] = it;
        }

        // Over the limit: first the farthest atlases take one level more off (a blurrier far crown costs no frame time; a mesh does), then the next
        // farthest, and so on, a pass at a time up to the most levels allowed; only then are the farthest left out, one by one.
        for (int pass = 1; pass <= ImpostorMaxExtraLevels && total > limit; pass++)
            for (int i = _impostorPlan.Count - 1; i >= 0 && total > limit; i--)
            {
                var it = _impostorPlan[i];
                if (it.Extra >= pass || it.BaseSkip + it.Extra >= it.Levels - 1) continue;
                long bytes = AtlasBytes(it.Frame, it.Grid, it.BaseSkip + pass);
                total += bytes - it.Bytes;
                (it.Extra, it.Bytes) = (pass, bytes);
                _impostorPlan[i] = it;
            }
        for (int i = _impostorPlan.Count - 1; i >= 0 && total > limit; i--)
        {
            var it = _impostorPlan[i];
            total -= it.Bytes;
            it.Admit = false;
            _impostorPlan[i] = it;
        }
        int admitted = 0, extras = 0;
        foreach (var it in _impostorPlan)
        {
            if (!it.Admit) continue;
            admitted++;
            if (it.Extra > 0) extras++;
        }
        (_impostorWantedCount, _impostorAdmittedCount, _impostorExtraCount, _impostorPlannedBytes) = (_impostorPlan.Count, admitted, extras, total);
    }

    /// <summary>
    /// Applies the plan: drops what it left out, makes room by evicting atlases nobody wants when the real use is over the limit, requests the
    /// admitted ones, refines or coarsens the resident ones. Counts what is not yet in place.
    /// </summary>
    void ApplyImpostorPlan(long now)
    {
        long limit = _impostorLimitBytes;
        _impostorRefineQueue.Clear();

        // Bytes the admitted atlases that are not resident yet will take.
        long pending = 0;
        foreach (var it in _impostorPlan)
            if (it.Admit && it.Asset.Impostor is not { Stage: FoliageImpostorStage.Ready }) pending += it.Bytes;
        bool pressure = _guard is { Pressure: true };
        if (pressure && SpikeLog.Enabled) SpikeLog.Note("vram guard pressure (impostor plan)");

        // Atlases nobody wanted this scan go first (least recently used first) when the real use plus what is coming passes the limit.
        if (_impostorBytes + pending > limit)
        {
            foreach (var a in ImpostorAssets()
                .Where(a => a.Impostor is { Stage: FoliageImpostorStage.Ready } s && now - s.LastUsed > 1000 && !_impostorWanted.ContainsKey(a))
                .OrderBy(a => a.Impostor!.LastUsed)
                .ToList())
            {
                UnloadImpostor(a);
                if (_impostorBytes + pending <= limit) break;
            }
        }
        bool over = _impostorBytes + pending > limit;
        int missing = 0, coarse = 0;
        foreach (var it in _impostorPlan)
        {
            var a = it.Asset;
            var s = a.Impostor;
            if (!it.Admit)
            {
                if (s is { Stage: FoliageImpostorStage.Ready } && (++s.DropScans >= 2 || pressure))
                {
                    _impostorDropped++;
                    UnloadImpostor(a);
                }
                else if (s is { Stage: FoliageImpostorStage.None, RetryAt: > 0 } refused) refused.LastUsed = now;
                continue;
            }
            if (s is null)
            {
                RequestImpostor(a, now, Math.Max(it.BaseSkip + it.Extra - 1, 0));
                s = a.Impostor;
            }
            else if (s is { Stage: FoliageImpostorStage.None, RetryAt: > 0 } refused && now >= refused.RetryAt)
            {
                a.Impostor = null;   // the plan says it fits now
                RequestImpostor(a, now, Math.Max(it.BaseSkip + it.Extra - 1, 0));
                s = a.Impostor;
            }
            if (s is null) continue;
            s.DropScans = 0;
            s.LastUsed = now;
            (s.Need, s.Extra) = (it.Need, it.Extra);
            if (s.PriorityScan != now) (s.Priority, s.PriorityScan) = (_impostorWanted[a], now);
            if (s.Stage != FoliageImpostorStage.Ready)
            {
                if (s.Stage != FoliageImpostorStage.None) missing++;
                continue;
            }
            if (s.FramePixels == 0) continue;
            int want = Math.Clamp(SkipFor(s.FramePixels, s.Levels, s.WorldRadius, it.Need) + it.Extra, 0, s.Levels - 1);
            s.WantSkip = want;
            if (want < s.Skip) coarse++;

            // How long it has held a finer level than wanted (a candidate for coarsening).
            if (want > s.Skip) { if (s.FinerSince == 0) s.FinerSince = now; }
            else s.FinerSince = 0;

            // Finer: an instance is coming near (refine at once); coarser: only when the budget needs it, or after holding more than needed for a
            // while.
            if (want < s.Skip || (want > s.Skip && (over || (want >= s.Skip + 2 && now - s.FinerSince > ImpostorCoarsenSeconds * 1000))))
                _impostorRefineQueue.Add(a);
        }
        (_impostorMissingCount, _impostorCoarseCount) = (missing, coarse);
        StartQueuedRefines(now);
    }

    /// <summary>
    /// Starts refines from the queue while fewer than <see cref="ImpostorMaxRefines"/> run: finer ones first (an instance is about to need them),
    /// nearest first.
    /// </summary>
    void StartQueuedRefines(long now)
    {
        while (_impostorRefining.Count < ImpostorMaxRefines && _impostorRefineQueue.Count > 0)
        {
            int best = -1;
            float bestKey = float.PositiveInfinity;
            for (int i = 0; i < _impostorRefineQueue.Count; i++)
            {
                // Not one already refining (its atlas loaded, its textures being made): a second refine would list it twice and free the atlas the
                // first still uses.
                if (_impostorRefineQueue[i].Impostor is not { Stage: FoliageImpostorStage.Ready, Next: null, RefineLoad: null, RefineAtlas: null,
                        Making: null } s || s.WantSkip == s.Skip || now < s.RefineRetryAt || (s.WantSkip < s.Skip && _guard is { Pressure: true }))
                {
                    _impostorRefineQueue.RemoveAt(i--);
                    continue;
                }
                float key = (s.WantSkip < s.Skip ? 0 : 1e9f) + s.Need;
                if (key < bestKey) (best, bestKey) = (i, key);
            }
            if (best < 0) return;
            var a = _impostorRefineQueue[best];
            _impostorRefineQueue.RemoveAt(best);
            StartRefine(a, a.Impostor!.WantSkip);
        }
    }

    /// <summary>
    /// Reloads the atlas from the cache on a worker to remake its textures with <paramref name="skip"/> top levels left out (finer or coarser than
    /// now).
    /// </summary>
    void StartRefine(FoliageMeshAsset a, int skip)
    {
        var s = a.Impostor!;
        if (s.Source is not { } source) return;
        var cache = _impostorCache;
        int frame = s.FramePixels, grid = s.Grid;
        s.RefineSkip = skip;
        s.RefineLoad = BackgroundWork.Run<ImpostorAtlas?>(() =>
        {
            var atlas = cache.TryLoad(source,
                skip);   // only the levels the new textures keep are read (the albedo's from skip, the normal map's from skip + 1)
            if (atlas is not null && (atlas.FramePixels != frame || atlas.Grid != grid)) Free(ref atlas);
            return atlas;
        });
        _impostorRefining.Add(a);
    }

    /// <summary>
    /// Steps the refines in flight: the loaded atlas's new textures are made and uploaded within the frame's budget, then swapped in (the old ones
    /// are drawn until then).
    /// </summary>
    void StepImpostorRefines(bool settling, ref long uploaded)
    {
        for (int i = 0; i < _impostorRefining.Count; i++)
        {
            var a = _impostorRefining[i];
            var s = a.Impostor;
            // unloaded meanwhile (UnloadImpostor frees the rest)
            if (s is not { Stage: FoliageImpostorStage.Ready, Textures: not null })
            {
                _impostorRefining.RemoveAt(i--);
                continue;
            }
            if (s.RefineLoad is { } load)
            {
                if (!load.IsCompleted) continue;
                s.RefineAtlas = load.IsCompletedSuccessfully ? load.Result : null;
                s.RefineLoad = null;
                if (s.RefineAtlas is null)
                {
                    s.RefineRetryAt = Environment.TickCount64 + 8000;
                    _impostorRefining.RemoveAt(i--);
                    continue;
                }
            }
            // abandoned or finished under another entry
            if (s.RefineAtlas is not { } atlas)
            {
                _impostorRefining.RemoveAt(i--);
                continue;
            }
            if (s.Next is null)
            {
                if (s.Making is null)
                {
                    long need = ImpostorTextures.BytesFor(atlas, s.RefineSkip, s.RefineSkip + 1);
                    bool finer = s.RefineSkip < s.Skip;
                    bool noRoom = finer && (_impostorBytes + need > _impostorLimitBytes * ImpostorTransientShare
                        || _guard is { } g && !g.Allows((ulong)need));
                    if (noRoom)
                    {
                        // no room beside the old textures: the plan is asked again at the next scan
                        Abandon(s, _impostorRefining, i--);
                        continue;
                    }
                    StartMaking(s, atlas, s.RefineSkip);
                    continue;
                }
                if (!s.Making.IsCompleted) continue;
                using var first = Gpu.Uploads.Begin();
                s.Next = new ImpostorTextures(Gpu, atlas, first, s.RefineSkip, s.RefineSkip + 1, TakeMade(s));
                _impostorBytes += s.Next.Bytes;
            }
            long budget = settling ? SettleUploadBytes : UploadBytesPerFrame;
            if (uploaded >= budget || !UploadTimeLeft(settling))
            {
                _impostorUploadsWaiting = settling;
                continue;
            }
            UploadRefineSteps(s.Next, budget, settling, ref uploaded);
            if (!s.Next.Complete)
            {
                _impostorUploadsWaiting = settling;
                continue;
            }
            SwapInRefined(a, s);
            _impostorRefining.RemoveAt(i--);
            _residentStamp++;
        }

        static void Abandon(FoliageImpostorState s, List<FoliageMeshAsset> list, int index)
        {
            Free(ref s.RefineAtlas);
            s.RefineRetryAt = Environment.TickCount64 + 2000;
            list.RemoveAt(index);
        }
    }

    /// <summary>
    /// Uploads steps of a refine's textures in one batch while the frame's budget and time last (at least one step).
    /// </summary>
    void UploadRefineSteps(ImpostorTextures next, long budget, bool settling, ref long uploaded)
    {
        using (var batch = Gpu.Uploads.Begin())
        {
            do
            {
                uploaded += next.NextStepBytes;
                next.UploadStep(batch);
            } while (!next.Complete && uploaded + next.NextStepBytes <= budget && UploadTimeLeft(settling));
        }
    }

    /// <summary>
    /// A refine's uploaded textures replace the old ones (freed), counted as refined or coarsened, at the refine's skip.
    /// </summary>
    void SwapInRefined(FoliageMeshAsset a, FoliageImpostorState s)
    {
        var old = s.Textures!;
        (s.Textures, s.Next) = (s.Next, null);
        _impostorBytes -= old.Bytes;
        old.Dispose();
        if (s.RefineSkip < s.Skip) _impostorRefined++;
        else if (s.RefineSkip > s.Skip) _impostorCoarsened++;
        if (SpikeLog.Enabled)
            SpikeLog.Note($"impostor {(s.RefineSkip < s.Skip ? "refined" : "coarsened")} {Label(a)} skip {s.Skip}->{s.RefineSkip}");
        s.Skip = s.RefineSkip;
        Free(ref s.RefineAtlas);
        s.FinerSince = 0;
    }

    /// <summary>
    /// Once a second (every update while settling): asks for the atlases of meshes with a group within reach of their transition, plans which fit
    /// the budget and at which mip (<see cref="PlanImpostors"/>), unloads those unused for <see cref="IdleSeconds"/>; then steps the loads, the one
    /// bake in progress, the uploads and the refines.
    /// </summary>
    void UpdateImpostors(Vector3 eye, bool settling)
    {
        long now = Environment.TickCount64;
        if (Impostors && (settling || now - _lastImpostorScan >= 1000)) ScanImpostors(eye, settling, now);
        if (now - _lastImpostorScan < 2000 || settling)
            foreach (var a in ImpostorAssets())
                if (a.Impostor is { Stage: FoliageImpostorStage.Ready } s && (now - s.LastUsed) / 1000.0 > ImpostorIdleSeconds)
                    UnloadImpostor(a);
        if (_impostorBakeMemoryHeld && _impostorBake is null && _impostorBakes.Count == 0 && now - _impostorLastBake > ImpostorBakeIdleMs)
        {
            _impostorBaker?.Trim();
            ImpostorAssembler.ReleasePool();
            _impostorBakeMemoryHeld = false;
        }
        StartQueuedRefines(now);
        if (_impostorWork.Count == 0 && _impostorBakes.Count == 0 && _impostorBake is null && _impostorRefining.Count == 0)
            return;
        Gpu.EnsureFrame();
        long uploaded = 0;
        var impWatch = _impostorWatch;
        impWatch.Restart();
        _impostorUploadsWaiting = false;
        StepImpostorWork(settling, impWatch, ref uploaded);
        StepImpostorRefines(settling, ref uploaded);
        StepBake(settling);
        if (impWatch.Elapsed.TotalMilliseconds > 3)
            SpikeLog.Note("slow impostors", ms: impWatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// The scan: which meshes want an atlas, from where the eye is and soon will be; the plan of what fits and its application; after a jump the
    /// old place's atlases dropped.
    /// </summary>
    void ScanImpostors(Vector3 eye, bool settling, long now)
    {
        _lastImpostorScan = now;
        if (!_impostorCacheChecked) PrepareImpostorDrawing();
        UpdateImpostorLimit();

        // The nearest any transition is: the small class's smallest (a zone ending before it has no impostor ground for any mesh).
        float shortest = Math.Min(ImpostorDistance, LargeImpostorDistance) * Math.Min(1,
            ImpostorClass.SmallMinimumRadius / ImpostorClass.MinimumRadius) * (1 - ImpostorBand);

        // Bakes wait in order of how soon their zones are close: from where the eye is now or will be in a few seconds of its motion.
        var lead = settling ? Vector2.Zero : _velocity * ImpostorLookaheadSeconds;
        if (ScreenLod) shortest = Math.Min(shortest, ScreenLodMinTransition * (1 - ImpostorBand));
        if (lead.Length() > MaxLookahead) lead = Vector2.Normalize(lead) * MaxLookahead;
        var soonEye = eye + new Vector3(lead.X, 0, lead.Y);
        CollectWantedImpostors(eye, soonEye, shortest);
        PlanImpostors(now);
        ApplyImpostorPlan(now);

        // A jump (a save loaded from the menu's scene, a teleport): the atlases the old place wanted go now, not after the idle time,
        // so they do not hold the new place's to the limit meanwhile.
        if (_impostorScanEye is { } last && Vector3.Distance(last, eye) > ImpostorJumpDistance)
            foreach (var a in ImpostorAssets())
                if (a.Impostor is { Stage: FoliageImpostorStage.Ready } s && s.LastUsed != now) UnloadImpostor(a);
        _impostorScanEye = eye;
    }

    /// <summary>
    /// The first scan: the cache's upkeep started, and the baker's and the drawing programs made with the loading, not at the first bake or
    /// impostor in view.
    /// </summary>
    void PrepareImpostorDrawing()
    {
        _impostorCacheChecked = true;
        _impostorCache.MaintainInBackground();

        // The baker's programs compile here, with the loading, not at the first bake
        // (the first bake of a cold cache was a 60 ms frame). The drawing programs and the quad follow below.
        Gpu.EnsureFrame();
        _impostorBaker ??= new ImpostorBaker(Gpu, _textures);

        // The drawing programs and the quad too, here in the update (a frame is open, no pass is): not at the first impostor in view
        // (a 7 ms hitch: both programs and the quad's upload).
        _impostorDraw ??= new ImpostorDraw(Gpu, _nativeFrame);
    }

    /// <summary>
    /// Fills <see cref="_impostorWanted"/>: each mesh (or rock holder) that can be an impostor in a ready zone, with how soon its nearest zone is
    /// close.
    /// </summary>
    void CollectWantedImpostors(Vector3 eye, Vector3 soonEye, float shortest)
    {
        _impostorWanted.Clear();
        foreach (var state in _zones.Values)
        {
            if (!state.Ready) continue;
            float near = ZoneDistance(state.Zone, eye), far = ZoneFarDistance(state, eye);
            if (far < shortest) continue;
            float urgency = Math.Min(near, ZoneDistance(state.Zone, soonEye));
            foreach (var g in state.Groups) WantImpostor(g, near, far, urgency);
        }
    }

    /// <summary>
    /// Wants a group's mesh as an impostor when the group reaches beyond its transition from this zone.
    /// </summary>
    void WantImpostor(FoliageGroup g, float near, float far, float urgency)
    {
        var a = g.Asset;

        // Only meshes that can be impostors here: the group reaches beyond the transition (its range ends after it and its
        // fade band, as WithImpostor requires) and this zone has ground from the band on.
        var (range, band) = RangeOf(g);
        if (!a.Resident || !a.HasBounds || near > range) return;
        if (a.Terrain)
        {
            WantRockImpostors(g, a, range, band, far, urgency);
            return;
        }
        if (a.Impostor is { Stage: FoliageImpostorStage.None, RetryAt: 0 }) return;

        // Its own transition (the exact one once the atlas is loaded; before that estimated from the mesh's bounds, which also rules out
        // a mesh that could never have an atlas: the load would only find that out).
        float t = a.Impostor is { FramePixels: > 0 } loaded ? TransitionFor(a, loaded.Class, loaded.WorldRadius, g) : EstimatedTransition(a, g);
        if (range - band < t || far < t * (1 - ImpostorBand)) return;
        _impostorWanted[a] = _impostorWanted.TryGetValue(a, out float known) ? Math.Min(known, urgency) : urgency;
    }

    /// <summary>
    /// A TERRAIN-mode rock: one atlas per biome row its instances have here (docs/impostors.md section 13).
    /// </summary>
    void WantRockImpostors(FoliageGroup g, FoliageMeshAsset a, float range, float band, float far, float urgency)
    {
        if (!RockImpostorsActive) return;
        float rt = EstimatedTransition(a, g);
        if (range - band < rt || far < rt * (1 - ImpostorBand)) return;
        PrepareRock(g, _rockTerrain!);   // returns at once when its records are already sorted and numbered for this terrain
        foreach (var (row, _, _) in g.RockSegments)
        {
            if (row < 0) continue;
            var holder = RockVariant(a, row);
            if (holder.Impostor is { Stage: FoliageImpostorStage.None, RetryAt: 0 }) continue;

            // its biome's textures are not resident: nothing to bake with
            if (holder.Impostor is null && !_rockTerrain!.CanBakeRock(row)) continue;
            _impostorWanted[holder] = _impostorWanted.TryGetValue(holder, out float seen) ? Math.Min(seen, urgency) : urgency;
        }
    }

    /// <summary>
    /// Steps the atlases in work: a finished load becomes an upload (or a bake when the cache had none), and uploads go on within the frame's
    /// budget.
    /// </summary>
    void StepImpostorWork(bool settling, System.Diagnostics.Stopwatch impWatch, ref long uploaded)
    {
        for (int i = 0; i < _impostorWork.Count; i++)
        {
            var a = _impostorWork[i];
            var s = a.Impostor!;
            if (s.Stage == FoliageImpostorStage.Loading)
            {
                if (!s.Load!.IsCompleted) continue;
                var (source, meshes, cls, atlas) = s.Load.IsCompletedSuccessfully ? s.Load.Result : default;
                s.Load = null;
                if (source is null || meshes is null || cls is not { } c)
                {
                    s.Stage = FoliageImpostorStage.None;
                    _impostorWork.RemoveAt(i--);
                    continue;
                }
                (s.Source, s.Meshes, s.Class) = (source, meshes, c);
                (s.FramePixels, s.Grid, s.Levels, s.WorldRadius) = (c.FramePixels, c.Grid, c.Levels, meshes.Radius * a.Mesh.MaxScale);
                if (atlas is null)
                {
                    s.Stage = FoliageImpostorStage.Baking;
                    _impostorBakes.Add(a);
                    _impostorWork.RemoveAt(i--);
                    continue;
                }
                _impostorsLoaded++;
                if (SpikeLog.Enabled) SpikeLog.Note($"impostor cache hit {Label(a)}");
                s.Atlas = atlas;
                s.Stage = FoliageImpostorStage.Uploading;
            }
            if (s.Stage != FoliageImpostorStage.Uploading) continue;

            // At most a budget of atlas levels a frame, over all atlases: the copy into the staging is render-thread time (a flight that
            // reaches a new biome asks for dozens at once), and while settling, when no frames end, the staging lives in the frame's
            // constant chunks, which are kept for good.
            long budget = settling ? SettleUploadBytes : UploadBytesPerFrame;
            if (uploaded >= budget || !UploadTimeLeft(settling))
            {
                _impostorUploadsWaiting = settling;
                continue;
            }
            double tOne = impWatch.Elapsed.TotalMilliseconds;
            bool done = UploadImpostor(a, budget, ref uploaded);
            double uploadMs = impWatch.Elapsed.TotalMilliseconds - tOne;
            if (uploadMs > 3) SpikeLog.Note($"slow impostor upload {Label(a)}", ms: uploadMs);
            if (done) _impostorWork.RemoveAt(i--);
        }
    }

    /// <summary>
    /// Starts loading the atlas of <paramref name="a"/> on a worker: its source, meshes and class, then the atlas from the disk cache; an asset that
    /// cannot have one is marked with no atlas (a rock retries after 5 seconds).
    /// </summary>
    /// <param name="firstLevel">The finest level worth reading from the cache: the albedo level the plan wants now, one finer for a nearer approach
    /// before the atlas arrives. An atlas read from it has nothing above (<see cref="ImpostorAtlas.FirstLevel"/>): the top level alone is three
    /// quarters of the bytes, and most far atlases never upload it. If the camera got closer than that meanwhile, the textures are made from the
    /// first level there is (<see cref="UploadImpostor"/>) and the plan refines them.</param>
    void RequestImpostor(FoliageMeshAsset a, long now, int firstLevel)
    {
        if (a.Impostor is not null) return;

        // A TERRAIN-mode rock's atlas (a holder of one biome: FoliageMeshAsset.RockOf) is baked with that biome's terrain material; the mesh itself
        // is never asked.
        bool rock = a.RockOf is not null;
        string? material = rock ? _rockTerrain?.RockBiomeKey(a.RockRow) : null;
        if (rock ? material is null : a.Terrain || ImpostorSource.Ineligible(a.Mesh) is not null)
        {
            a.Impostor = new FoliageImpostorState { Stage = FoliageImpostorStage.None, RetryAt = rock ? now + 5000 : 0 };
            return;
        }
        var mesh = a.Mesh;
        var locator = _assets;
        var cache = _impostorCache;
        var s = a.Impostor = new FoliageImpostorState { Stage = FoliageImpostorStage.Loading, LastUsed = now };
        var messages = Messages;
        int rockRow = a.RockRow;
        s.Load = BackgroundWork.Run<(ImpostorSource?, ImpostorMeshes?, ImpostorClass?, ImpostorAtlas?)>(() =>
        {
            var source = rock ? ImpostorSource.ForRock(mesh, locator, rockRow, material!) : ImpostorSource.From(mesh, locator);
            if (source is null) return default;
            var meshes = ImpostorMeshes.Load(source, messages);
            if (meshes is null) return (source, null, null, null);
            var cls = rock ? ImpostorClass.ForRock(meshes.Radius * mesh.MaxScale, meshes.Triangles) : ImpostorClass.For(meshes.Radius * mesh.MaxScale,
                meshes.Triangles);
            if (rock && cls is not null)
            {
                float share = meshes.SurfaceShare;
                if (share < RockMinSurface) cls = null;   // sticks and the like: parts a billboard cannot hold, they stay meshes
            }
            if (cls is not { } c) return (source, meshes, null, null);
            var atlas = cache.TryLoad(source, firstLevel);
            if (atlas is not null && (atlas.FramePixels != c.FramePixels || atlas.Grid != c.Grid)) Free(ref atlas);
            return (source, meshes, c, atlas);
        });
        _impostorWork.Add(a);
    }

    /// <summary>
    /// The top levels an atlas never needs at a transition distance (<see cref="SkipFor"/> with the atlas's own radius).
    /// </summary>
    internal static int LevelsToSkip(ImpostorAtlas atlas, float maxScale, float transition) => SkipFor(atlas.FramePixels, atlas.Levels,
        atlas.Radius * maxScale, transition);

    /// <summary>
    /// Makes the atlas's textures with <paramref name="skip"/> on a worker (<see cref="ImpostorTextures.MakeOffThread"/>); <see cref="TakeMade"/>
    /// takes them.
    /// </summary>
    void StartMaking(FoliageImpostorState s, ImpostorAtlas atlas, int skip)
    {
        var gpu = Gpu;
        (s.Making, s.MakingSkip) = (BackgroundWork.Run(() => ImpostorTextures.MakeOffThread(gpu, atlas, skip, skip + 1)), skip);
    }

    /// <summary>
    /// The textures made on a worker for <paramref name="s"/> (null when it failed or none), forgetting the task.
    /// </summary>
    static GpuTexture?[]? TakeMade(FoliageImpostorState s)
    {
        var made = s.Making is { IsCompletedSuccessfully: true } making ? making.Result : null;
        s.Making = null;
        return made;
    }

    /// <summary>
    /// The atlas's textures (made on a worker first), level by level within the frame's upload budget; true when resident.
    /// </summary>
    bool UploadImpostor(FoliageMeshAsset a, long budget, ref long uploaded)
    {
        var s = a.Impostor!;
        if (s.Textures is null)
        {
            if (s.Making is null)
            {
                // The albedo's top levels its nearest instance never samples are left out, and the normal map's one level more: it shapes the
                // lighting, which varies slowly over a crown.
                int levels = Math.Clamp(SkipFor(s.Atlas!.FramePixels, s.Atlas.Levels, s.Atlas.Radius * a.Mesh.MaxScale, Math.Max(s.Need,
                    TransitionFor(a, s.Class, s.WorldRadius)))
                    + s.Extra, 0, s.Atlas.Levels - 1);
                long need = ImpostorTextures.BytesFor(s.Atlas!, levels, levels + 1);
                if (!MakeImpostorRoom(need, a) || _guard is { } g && !g.Allows((ulong)need))
                {
                    _impostorRefused++;
                    Free(ref s.Atlas);
                    (s.Stage, s.RetryAt, s.Meshes) = (FoliageImpostorStage.None, Environment.TickCount64 + (long)(ImpostorRetrySeconds * 1000), null);
                    return true;
                }
                StartMaking(s, s.Atlas!, levels);
                return false;
            }
            if (!s.Making.IsCompleted) return false;
            int skip = s.MakingSkip;
            using var first = Gpu.Uploads.Begin();
            s.Textures = new ImpostorTextures(Gpu, s.Atlas!, first, skip, skip + 1, TakeMade(s));
            s.Skip = s.Textures.SkipOf(0);   // levels, or the atlas's first level when it was read without the finer ones (the plan then refines)
            _impostorBytes += s.Textures.Bytes;
        }
        using var batch = Gpu.Uploads.Begin();

        // At least one step (a level larger than the budget still goes), then while the budget lasts.
        do
        {
            uploaded += s.Textures.NextStepBytes;
            s.Textures.UploadStep(batch);
        } while (!s.Textures.Complete && uploaded + s.Textures.NextStepBytes <= budget && UploadTimeLeft(budget >= SettleUploadBytes));
        if (!s.Textures.Complete) return false;
        s.Stage = FoliageImpostorStage.Ready;
        if (SpikeLog.Enabled) SpikeLog.Note($"impostor ready {Label(a)} skip {s.Skip}");
        Free(ref s.Atlas);   // the CPU copy is not needed any more (a reload reads the cache again)
        s.Meshes = null;
        _residentStamp++;
        return true;
    }

    /// <summary>
    /// The bake in progress, a step a frame (rows recorded, read back, filtered and encoded on workers); the next one when it is done.
    /// </summary>
    void StepBake(bool settling)
    {
        if (_impostorBake is null)
        {
            if (_impostorBakes.Count == 0) return;

            // The one whose zones are closest (now or soon) first.
            int best = 0;
            for (int i = 1; i < _impostorBakes.Count; i++)
                if (_impostorBakes[i].Impostor!.Priority < _impostorBakes[best].Impostor!.Priority) best = i;
            var next = _impostorBakes[best];
            _impostorBakes.RemoveAt(best);
            var s = next.Impostor!;
            _impostorBaker ??= new ImpostorBaker(Gpu, _textures);
            _impostorBaker.RockTerrain = _rockTerrain;
            _impostorBake = _impostorBaker.Begin(s.Source!, s.Meshes!, s.Class);
            _impostorBakeAsset = next;
            if (SpikeLog.Enabled) SpikeLog.Note($"impostor bake start {Label(next)} ({s.Class.FramePixels} px, grid {s.Class.Grid})");
        }
        var size = _impostorBake.Size;
        double rowSamples = size.Grid * 3.0 * (2.0 * size.FramePixels) * (2.0 * size.FramePixels);
        _impostorBake.RowsPerStep = settling ? size.Grid : Math.Clamp((int)(ImpostorBakeSamplesPerFrame / rowSamples), 1, size.Grid);
        (_impostorLastBake, _impostorBakeMemoryHeld) = (Environment.TickCount64, true);
        if (SpikeLog.Enabled) SpikeLog.Note("impostor bake step");
        if (!_impostorBake.Step()) return;
        var a = _impostorBakeAsset!;
        var st = a.Impostor!;
        if (_impostorBake.Result is { } atlas)
        {
            _impostorsBaked++;
            var source = st.Source!;
            var cache = _impostorCache;
            BackgroundWork.Run(() =>
            {
                try { cache.Save(source, atlas); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            });
            st.Atlas = atlas;
            st.Stage = FoliageImpostorStage.Uploading;
            st.Baked = true;
            _impostorWork.Add(a);
        }
        else
        {
            Messages.Add($"impostor {Label(a)}: {_impostorBake.Error?.Message}");
            st.Stage = FoliageImpostorStage.None;

            // a rock's biome that was not there: asked again once it is wanted again
            if (a.RockOf is not null) st.RetryAt = Environment.TickCount64 + 10000;
        }
        _impostorBake.Dispose();
        (_impostorBake, _impostorBakeAsset) = (null, null);
    }

    /// <summary>
    /// Safety net at an atlas's first upload: room for <paramref name="need"/> more bytes within the limit (and the transient share a refine may
    /// use): atlases the last scans did not ask for (unused for 3 s) go, least recently used first. The plan has already counted what fits, so this
    /// only acts when refines or coarsenings are still in flight. False when it does not fit.
    /// </summary>
    bool MakeImpostorRoom(long need, FoliageMeshAsset except)
    {
        long limit = (long)(_impostorLimitBytes * ImpostorTransientShare);
        if (_impostorBytes + need <= limit) return true;
        long now = Environment.TickCount64;
        foreach (var a in ImpostorAssets()
            .Where(a => a != except && a.Impostor is { Stage: FoliageImpostorStage.Ready } s && now - s.LastUsed > 3000)
            .OrderBy(a => a.Impostor!.LastUsed)
            .ToList())
        {
            UnloadImpostor(a);
            if (_impostorBytes + need <= limit) return true;
        }
        return false;
    }

    /// <summary>
    /// Frees the asset's atlas, its refine and its loads and takes their bytes off the resident total.
    /// </summary>
    void UnloadImpostor(FoliageMeshAsset a)
    {
        var s = a.Impostor!;
        if (s.Next is not null)
        {
            _impostorBytes -= s.Next.Bytes;
            s.Next.Dispose();
            s.Next = null;
        }
        FreeLoads(s);
        if (s.Textures is not null)
        {
            _impostorBytes -= s.Textures.Bytes;
            s.Textures.Dispose();
        }
        if (SpikeLog.Enabled) SpikeLog.Note($"impostor unload {Label(a)}");
        a.Impostor = null;   // asked for again (from the cache) when needed
        _impostorUnloads++;
        _residentStamp++;
    }

    /// <summary>
    /// Frees the bake, every atlas and load, the baker and the programs.
    /// </summary>
    void DisposeImpostors()
    {
        _impostorBake?.Dispose();
        foreach (var a in ImpostorAssets())
        {
            if (a.Impostor is not { } s) continue;
            s.Next?.Dispose();
            FreeLoads(s);
            s.Textures?.Dispose();
        }
        _impostorBaker?.Dispose();
        _impostorDraw?.Dispose();
    }

    /// <summary>
    /// Frees a read atlas's native levels (docs/engine.md, "Native buffers") and clears the reference: wherever the state lets go of one.
    /// </summary>
    static void Free(ref ImpostorAtlas? atlas)
    {
        atlas?.Dispose();
        atlas = null;
    }

    /// <summary>
    /// Waits for the state's loads in flight and frees every atlas it holds (after its <see cref="ImpostorState.Next"/> textures, which read the
    /// refine's), and the textures made for it on a worker that were never used.
    /// </summary>
    static void FreeLoads(FoliageImpostorState s)
    {
        if (s.Making is { } making)
        {
            try
            {
                making.Wait();
                foreach (var t in making.Result) t?.Dispose();
            }
            catch (AggregateException)
            {
            }
            s.Making = null;
        }
        if (s.Load is { } load)
        {
            try
            {
                load.Wait();
                load.Result.Atlas?.Dispose();
            }
            catch (AggregateException)
            {
            }
            s.Load = null;
        }
        if (s.RefineLoad is { } refine)
        {
            try
            {
                refine.Wait();
                refine.Result?.Dispose();
            }
            catch (AggregateException)
            {
            }
            s.RefineLoad = null;
        }
        Free(ref s.RefineAtlas);
        Free(ref s.Atlas);
    }

    #endregion

    #region Drawing

    /// <summary>
    /// The impostor batches of the current view, in work-list order (the GPU path: after the mesh and rock batches).
    /// </summary>
    readonly List<FoliageMeshAsset> _gpuImpostorOrder = [];
    /// <summary>
    /// The CPU path's impostor batches (the cull's emission order), and its batches by mesh.
    /// </summary>
    readonly List<Batch> _impostorActive = [];
    /// <summary>
    /// The CPU path's batch of each mesh (see <see cref="_impostorActive"/>).
    /// </summary>
    readonly Dictionary<FoliageMeshAsset, Batch> _impostorBatches = [];
    /// <summary>
    /// The current view's impostor draws: one per impostor batch shown (the GPU path) or with instances (the CPU path).
    /// </summary>
    readonly List<ImpostorDrawItem> _impostorDraws = [];

    /// <summary>
    /// One impostor draw of the view.
    /// </summary>
    struct ImpostorDrawItem
    {
        /// <summary>
        /// The mesh whose atlas is drawn.
        /// </summary>
        public FoliageMeshAsset Asset;
        /// <summary>
        /// The batch's index: in the GPU work list, or the CPU path's.
        /// </summary>
        public int Batch;
        /// <summary>
        /// The CPU path's instance range in the rows (0 for the GPU path, whose arguments hold it).
        /// </summary>
        public uint FirstInstance, Instances;
    }

    /// <summary>
    /// Which billboards an <see cref="AddImpostorDraws"/> call draws and with which program: the scene's (all, the colour program with its discards),
    /// the depth prepass (the plain ones), the colour after it (the plain ones, no discards) or the rocks' alone.
    /// </summary>
    enum ImpostorPass
    {
        /// <summary>
        /// The scene's: every billboard, the colour program with its discards.
        /// </summary>
        Scene,
        /// <summary>
        /// The depth prepass: the plain billboards.
        /// </summary>
        Prepass,
        /// <summary>
        /// The colour after the prepass: the plain billboards, no discards.
        /// </summary>
        AfterPrepass,
        /// <summary>
        /// The rocks' billboards alone.
        /// </summary>
        RocksOnly
    }

    /// <summary>
    /// The CPU path's batch of <paramref name="a"/>, added to this draw's active batches when first used in it.
    /// </summary>
    Batch ImpostorBatchOf(FoliageMeshAsset a)
    {
        if (!_impostorBatches.TryGetValue(a, out var batch)) _impostorBatches[a] = batch = new Batch { Asset = a };
        if (batch.Stamp != _drawStamp)
        {
            batch.Stamp = _drawStamp;
            batch.Count = 0;
            batch.Index = _impostorActive.Count;
            _impostorActive.Add(batch);
        }
        return batch;
    }

    /// <summary>
    /// Appends the CPU cull's visible instances of <paramref name="a"/> to its impostor batch.
    /// </summary>
    void EmitImpostors(FoliageMeshAsset a, ReadOnlySpan<Matrix4x4> visible)
    {
        if (visible.Length == 0) return;
        DrawnInstances += visible.Length;
        var batch = ImpostorBatchOf(a);
        if (batch.Count + visible.Length > batch.Data.Length)
            Array.Resize(ref batch.Data, Math.Max(batch.Data.Length * 2, batch.Count + visible.Length));
        visible.CopyTo(batch.Data.AsSpan(batch.Count));
        batch.Count += visible.Length;
    }

    /// <summary>
    /// The impostor draws: per impostor batch shown (GPU) or not empty (CPU).
    /// </summary>
    void PrepareImpostors(bool gpu)
    {
        _impostorDraws.Clear();
        if (gpu)
        {
            int first = _gpuOrder.Count + _gpuRockOrder.Count;
            for (int i = 0; i < _gpuImpostorOrder.Count; i++)
                if (_gpuBatchShown[first + i]) _impostorDraws.Add(new ImpostorDrawItem { Asset = _gpuImpostorOrder[i], Batch = first + i });
        }
        else
            foreach (var b in _impostorActive)
                if (b.Count > 0) _impostorDraws.Add(new ImpostorDrawItem { Asset = b.Asset, Batch = b.Index, FirstInstance = (uint)b.Offset,
                        Instances = (uint)b.Count });
        DrawCalls += _impostorDraws.Count;
    }

    /// <summary>
    /// Makes the impostor pipeline of the host's current state and formats ahead of the first impostor of a pass (the same key
    /// <see cref="AddImpostorDraws"/> asks for: the host's state with the foliage's alpha to coverage), so a tree that comes into view does not
    /// compile one. Called by every foliage pass that draws no impostor; a pipeline is made once per combination. Nothing before the programs
    /// exist (the switch off, or before the first foliage update).
    /// </summary>
    void WarmImpostorPipeline(bool depth, bool coverage)
    {
        if (_impostorDraw is null || !Impostors) return;
        var state = Gpu.CurrentState() with { Cull = CullMode.None, AlphaToCoverage = coverage };
        _impostorDraw.Pipeline(depth ? ImpostorProgram.Caster : ImpostorProgram.Plain, state, Gpu.CurrentTargets().Formats);
        if (!depth && RockImpostorsActive)
            _impostorDraw.Pipeline(ImpostorProgram.Rock, state, Gpu.CurrentTargets().Formats);
        if (!depth && PrepassView && ImpostorPrepass.Enabled && FoliagePrepass.Enabled && !coverage)
        {
            _impostorDraw.Pipeline(ImpostorProgram.Prepass, state with { ColourMask = 0 }, Gpu.CurrentTargets().Formats);
            _impostorDraw.Pipeline(ImpostorProgram.AfterPrepass, state, Gpu.CurrentTargets().Formats);
        }
    }

    /// <summary>
    /// The impostor draws appended to a mesh segment's job (after its mesh draws; no culling, the rows already bound): a colour view's lit program,
    /// or a cascade's caster facing the sun (the direction towards it from the cascade's matrix).
    /// </summary>
    void AddImpostorDraws(MeshJob job, bool depth, Matrix4x4 viewProjection, PassTargets targets, DrawState state, bool coverage, bool indirect,
        int argsFirst, ImpostorPass pass = ImpostorPass.Scene)
    {
        _impostorDraw ??= new ImpostorDraw(Gpu, _nativeFrame);
        var drawState = state with { Cull = CullMode.None };
        var program = depth ? ImpostorProgram.Caster
            : pass == ImpostorPass.Prepass ? ImpostorProgram.Prepass
            : pass == ImpostorPass.AfterPrepass ? ImpostorProgram.AfterPrepass
            : ImpostorProgram.Plain;
        var pipeline = _impostorDraw.Pipeline(program, drawState, targets.Formats);
        var up = new Vector3(viewProjection.M12, viewProjection.M22, viewProjection.M32);
        up = up.LengthSquared() > 0 ? Vector3.Normalize(up) : Vector3.UnitY;
        var forward = new Vector3(viewProjection.M13, viewProjection.M23, viewProjection.M33);
        var view = depth && forward.LengthSquared() > 0 ? new Vector4(-Vector3.Normalize(forward), 1) : Vector4.Zero;
        var quad = _impostorDraw.Quad;
        GpuBufferBinding[] none = [];

        // A TERRAIN-mode rock's colour draws fade to the ground colour with distance as the terrain's mesh shader does (its own program, the
        // terrain's maps in the push).
        GpuPipeline? rockPipeline = null;
        (Vector4 Region, uint Ground, uint WorldColour, uint Colour, float HalfWorld, float FarStart, float FarEnd, uint Flags) far = default;
        bool firstDraw = true;
        for (int k = 0; k < _impostorDraws.Count; k++)
        {
            var d = _impostorDraws[k];
            bool rockDraw = d.Asset.RockOf is not null && !depth && _rockTerrain is not null;
            bool skipDraw = pass is ImpostorPass.Prepass or ImpostorPass.AfterPrepass ? rockDraw : pass == ImpostorPass.RocksOnly && !rockDraw;
            if (skipDraw) continue;   // the prepass draws the plain billboards, the rocks' go after it
            var push = ImpostorPushFor(d, up, view, coverage, depth);
            MeshPush bytes = default;
            var drawPipeline = pipeline;
            if (rockDraw)
            {
                if (rockPipeline is null)
                {
                    rockPipeline = _impostorDraw.Pipeline(ImpostorProgram.Rock, drawState, targets.Formats);
                    far = _rockTerrain!.RockFarView(_drawMaterialDistance);
                }
                var rockPush = RockPush(push, far);
                System.Runtime.CompilerServices.Unsafe.As<MeshPush, ImpostorRockPush>(ref bytes) = rockPush;
                drawPipeline = rockPipeline!.Value;
            }
            else System.Runtime.CompilerServices.Unsafe.As<MeshPush, ImpostorPush>(ref bytes) = push;
            job.Add(new MeshJob.Draw
            {
                SetRaster = firstDraw,
                Cull = drawState.Cull,
                Pipeline = drawPipeline,
                Vertices = none,
                Elements = quad,
                Push = bytes,
                Args = indirect ? _gpuResult.Args : default,
                ArgsOffset = _gpuResult.ArgsOffset + (ulong)(argsFirst + k) * 20,
                IndexCount = 6,
                Instances = d.Instances,
                FirstInstance = d.FirstInstance,
            });
            firstDraw = false;
        }
    }

    /// <summary>
    /// An impostor draw's push: the atlas's sphere, grid and textures, the camera's up, the view (a cascade's), and the packed quad when it fits.
    /// </summary>
    ImpostorPush ImpostorPushFor(in ImpostorDrawItem d, Vector3 up, Vector4 view, bool coverage, bool depth)
    {
        var t = d.Asset.Impostor!.Textures!;
        var atlas = t.Atlas;
        return new ImpostorPush
        {
            Sphere = new Vector4(atlas.Centre, atlas.Radius),
            CameraUp = up,
            Grid = atlas.Grid,
            Albedo = t.Index(0, _textureBias),
            Normal = t.Index(1, _textureBias),
            Gloss = atlas.Gloss,
            Coverage = coverage ? 1u : 0u,
            View = view,
            Spare = depth || !ImpostorQuad.Enabled || Vector3.DistanceSquared(d.Asset.Centre, atlas.Centre) > 1e-4f * atlas.Radius * atlas.Radius
                ? 0
                : ImpostorQuad.Pack(d.Asset.HalfExtent, atlas.Radius),
        };
    }

    /// <summary>
    /// A TERRAIN-mode rock's push: the impostor's, with the terrain's maps and distances it fades to the ground colour by.
    /// </summary>
    static ImpostorRockPush RockPush(in ImpostorPush push,
        in (Vector4 Region, uint Ground, uint WorldColour, uint Colour, float HalfWorld, float FarStart, float FarEnd, uint Flags) far) => new()
    {
        Sphere = push.Sphere,
        CameraUp = push.CameraUp,
        Grid = push.Grid,
        Albedo = push.Albedo,
        Normal = push.Normal,
        Gloss = push.Gloss,
        Coverage = push.Coverage,
        View = push.View,
        Region = far.Region,
        Ground = far.Ground,
        WorldColour = far.WorldColour,
        Colour = far.Colour,
        HalfWorld = far.HalfWorld,
        FarStart = far.FarStart,
        FarEnd = far.FarEnd,
        Flags = far.Flags,
    };

    #endregion
}