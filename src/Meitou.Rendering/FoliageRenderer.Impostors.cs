using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Impostors;

namespace Meitou.Rendering;

/// <summary>
/// Far billboards (docs/impostors.md "Drawing"; the <c>impostors</c> switch). A foliage mesh with an impostor atlas is drawn, from its transition
/// distance on, as one camera-facing quad per instance sampling the atlas (<see cref="ImpostorDraw"/>), chosen per instance by the same cull
/// that places the meshes (its kernels, or <see cref="FoliageCull.CullGroup"/> on the CPU), crossfaded with the mesh over a band before the
/// transition by complementary dither. Shadow cascades get the impostors as casters beyond the transition (a depth-only quad facing the sun).
/// Atlases are loaded from the disk cache or baked on first use (<see cref="ImpostorBaker"/>), on demand when a group of the mesh comes
/// within reach of its transition, and unloaded with the meshes when unused.
/// <para>
/// <b>Residency</b> (docs/impostors.md section 10): once a second the scan collects the atlases wanted (<see cref="impostorWanted"/>: the nearest
/// ground at which any zone would draw each as an impostor) and <see cref="PlanImpostors"/> decides, nearest first, which fit the
/// <see cref="ImpostorLimitMb"/> budget (a share of the card's, <see cref="ImpostorBudget"/>) and at which mip: an atlas holds only the levels its
/// nearest instance can sample (<see cref="SkipFor"/>), refined (reloaded from the disk cache and swapped in) when an instance approaches, coarsened
/// when the budget needs it; when even the coarsest allowed mip does not fit, the farthest atlases stay meshes.
/// </para>
/// </summary>
public sealed partial class FoliageRenderer
{
    /// <summary>The default transition distance of large meshes (units along the ground; the Tab slider "Impostor distance").</summary>
    public const float DefaultImpostorDistance = 4000;
    /// <summary>The crossfade band before the transition, as a share of it.</summary>
    public const float ImpostorBand = 0.1f;

    /// <summary>The <c>impostors</c> switch (Enhancements): Meitou (default) draws far instances as impostors; Faithful never loads or draws one.</summary>
    public bool Impostors { get; set; } = true;
    /// <summary>The transition distance (units along the ground) of the medium and large atlases.</summary>
    public float ImpostorDistance { get; set; } = DefaultImpostorDistance;
    /// <summary>
    /// An explicit limit on the video memory the resident atlases may use, in megabytes (<c>--impostor-budget</c>, <c>MEITOU_IMPOSTOR_BUDGET_MB</c>);
    /// null: the default rule, a share of the card's budget (<see cref="ImpostorBudget"/>, <see cref="ImpostorLimitMb"/>).
    /// </summary>
    public double? ImpostorBudgetMb { get; set; } = ImpostorBudget.FromEnvironment();
    /// <summary>The limit in force as of the last scan (MB): the override or the default rule, three quarters of it under the guard's pressure.</summary>
    public double ImpostorLimitMb => impostorLimitBytes / 1048576.0;
    long impostorLimitBytes = (long)(ImpostorBudget.UnknownMb * 1048576);
    const double ImpostorRetrySeconds = 2;
    /// <summary>The share of the limit a refine's new textures may take over it while the old ones are still drawn.</summary>
    const double ImpostorTransientShare = 1.1;
    /// <summary><c>MEITOU_IMPOSTOR_FARMIP=0</c>: every atlas keeps the levels its transition distance needs (the single mip rule of before): no refine, no coarsen.</summary>
    static readonly bool FarMips = Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_FARMIP") != "0";
    /// <summary>Levels of safety on the mip an atlas needs (<c>MEITOU_IMPOSTOR_MIP_MARGIN</c>): see <see cref="PlanImpostors"/>.</summary>
    static readonly float FarMipMargin = float.TryParse(Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_MIP_MARGIN"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out float marginLevels) && marginLevels >= 0 ? marginLevels : 1f;
    /// <summary>The most levels beyond what an atlas's nearest instance needs the plan takes off an atlas that would not fit (a blurrier far crown instead of a mesh).</summary>
    const int ImpostorMaxExtraLevels = 2;
    /// <summary>A resident atlas counts as this much nearer in the plan's order, so a newcomer must be that much nearer to take its place (hysteresis).</summary>
    const float ImpostorStickiness = 0.8f;
    /// <summary>Seconds an atlas must hold a finer level than needed before it is coarsened without budget pressure (and by at least two levels).</summary>
    const double ImpostorCoarsenSeconds = 10;
    /// <summary>Refines (reload + upload + swap) in flight at once.</summary>
    const int ImpostorMaxRefines = 3;
    /// <summary>MEITOU_IMPOSTOR_LOG=1: a line for every atlas loaded, baked, refused for the budget or unloaded.</summary>
    static readonly bool ImpostorLog = Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_LOG") == "1";
    /// <summary><c>MEITOU_IMPOSTOR_CASTERS=0</c>: the cascades keep the meshes (no impostor casters).</summary>
    static readonly bool ImpostorCasters = Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_CASTERS") != "0";

    enum ImpostorStage { Loading, Baking, Uploading, Ready, None }

    sealed class ImpostorState
    {
        public ImpostorStage Stage;
        public Task<(ImpostorSource? Source, ImpostorMeshes? Meshes, ImpostorClass? Class, ImpostorAtlas? Atlas)>? Load;
        public ImpostorSource? Source;
        public ImpostorMeshes? Meshes;
        public ImpostorClass Class;
        public ImpostorAtlas? Atlas;
        public ImpostorTextures? Textures;
        public long LastUsed;
        /// <summary>A refused atlas (<see cref="ImpostorStage.None"/>): the tick from which it may ask again; 0 for one that never can (too small, ineligible).</summary>
        public long RetryAt;
        /// <summary>The work list that last numbered this mesh's impostor batch, and its number among the impostor batches.</summary>
        public int Stamp, Index;
        /// <summary>How soon the nearest zone that wants this atlas is close (units, smaller first) as of the scan at <see cref="PriorityScan"/>: the order bakes run in.</summary>
        public float Priority;
        public long PriorityScan;
        /// <summary>The atlas's shape once its class is known (kept after the CPU copy is dropped): frame pixels, grid, levels, and the world radius of its largest instance.</summary>
        public int FramePixels, Grid, Levels;
        public float WorldRadius;
        /// <summary>The nearest ground distance (units) at which an instance of the mesh is drawn as this impostor, as of the last scan, and the levels
        /// the plan takes off beyond what that distance needs.</summary>
        public float Need;
        public int Extra;
        /// <summary>Top albedo levels left out of the resident textures (<see cref="ImpostorTextures.SkipOf"/>).</summary>
        public int Skip, WantSkip;
        /// <summary>Since when the atlas has held a finer level than the plan wants (tick; 0 when it does not).</summary>
        public long FinerSince;
        /// <summary>Scans in a row the plan left this atlas out.</summary>
        public int DropScans;
        /// <summary>A refine in flight: the cache read, the atlas, the new textures and the albedo skip they are made with.</summary>
        public Task<ImpostorAtlas?>? RefineLoad;
        public ImpostorAtlas? RefineAtlas;
        public ImpostorTextures? Next;
        public int RefineSkip;
        public long RefineRetryAt;
    }

    /// <summary>The seconds of the eye's motion the order of the waiting bakes looks ahead.</summary>
    const float ImpostorLookaheadSeconds = 3;
    /// <summary>
    /// The most shaded samples of a bake recorded in one frame (<c>MEITOU_IMPOSTOR_BAKE_MSAMPLES</c>, in millions; a row is
    /// <c>grid x (2 x frame)^2 x 3</c> of them; default 40, docs/impostors.md section 8). A large atlas (256 pixel frames, 9.4 million a row) takes 4 rows
    /// a frame (three frames in all), a 128 or 64 pixel one its twelve rows in one.
    /// </summary>
    static readonly double ImpostorBakeSamplesPerFrame = 1e6 * (double.TryParse(Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_BAKE_MSAMPLES"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out double msamples) && msamples > 0 ? msamples : 40);
    /// <summary>The pooled bake memory (readback buffers, atlas levels, filtering scratch) is freed this long after the last bake.</summary>
    const long ImpostorBakeIdleMs = 10000;
    long impostorLastBake;
    bool impostorBakeMemoryHeld;

    readonly ImpostorCache impostorCache = new();
    bool impostorCacheChecked;
    /// <summary>
    /// The most the atlas disk cache may take, in megabytes (<c>MEITOU_IMPOSTOR_CACHE_MB</c>, <c>--impostor-cache-mb</c>; 0 is no cap): older files go
    /// first, and files of an old format or baker version at once (<see cref="ImpostorCache.Maintain"/>, which runs on a worker at the first use
    /// and after every 16 atlases saved).
    /// </summary>
    public double ImpostorCacheMb
    {
        get => impostorCache.MaxBytes / 1048576.0;
        set => impostorCache.MaxBytes = (long)(value * 1048576);
    }
    ImpostorBaker? impostorBaker;
    ImpostorBakeJob? impostorBake;
    /// <summary>Atlas bytes uploaded per frame while settling; <see cref="impostorUploadsWaiting"/>: more wait for the next frame.</summary>
    const long SettleUploadBytes = 64L << 20, UploadBytesPerFrame = 6L << 20;
    bool impostorUploadsWaiting;
    MeshAsset? impostorBakeAsset;
    ImpostorDraw? impostorDraw;
    readonly List<MeshAsset> impostorBakes = [];
    readonly List<MeshAsset> impostorWork = [];
    /// <summary>Resident atlases being refined or coarsened (their new textures loading or uploading).</summary>
    readonly List<MeshAsset> impostorRefining = [];
    long impostorBytes, lastImpostorScan;
    int impostorsBaked, impostorsLoaded, impostorUnloads, impostorRefined, impostorCoarsened, impostorRefused, impostorDropped;

    /// <summary>What the last plan found (see <see cref="ImpostorPopIn"/>).</summary>
    int impostorWantedCount, impostorAdmittedCount, impostorMissingCount, impostorCoarseCount, impostorExtraCount;
    long impostorPlannedBytes;

    /// <summary>
    /// What the last scan's plan left unfinished, for the benchmark's pop-in line: atlases wanted that stay meshes because the plan left them out
    /// (<c>NotAdmitted</c>), admitted ones not resident yet (<c>Missing</c>: meshes are drawn until they are), resident ones held coarser than their
    /// nearest instance wants while a refine is on its way (<c>Coarse</c>: a blurrier crown for a moment), and the refines in flight.
    /// </summary>
    public (int NotAdmitted, int Missing, int Coarse, int Refining) ImpostorPopIn => (impostorWantedCount - impostorAdmittedCount, impostorMissingCount, impostorCoarseCount, impostorRefining.Count);

    /// <summary>Atlases loading, waiting to bake, baking or uploading (part of <see cref="Pending"/>).</summary>
    int ImpostorPending => impostorWork.Count + impostorBakes.Count + (impostorBake is null ? 0 : 1);

    string ImpostorDescription => $"{assetsByMesh.Values.Count(a => a.Impostor?.Stage == ImpostorStage.Ready)} impostor atlases {impostorBytes / 1048576.0:0} MB of {ImpostorLimitMb:0} MB " +
        $"({impostorsLoaded} from the cache, {impostorsBaked} baked, {impostorUnloads} unloaded, {impostorRefined} refined, {impostorCoarsened} coarsened, {impostorDropped} dropped by the plan, {impostorRefused} refused; " +
        $"plan {impostorAdmittedCount} of {impostorWantedCount} wanted, {impostorMissingCount} not resident, {impostorExtraCount} blurrier than needed, {impostorPlannedBytes / 1048576.0:0} MB planned)";

    /// <summary>The class of an atlas that is not loaded yet, estimated from the mesh's own bounds (the exact one comes with the load).</summary>
    static ImpostorClass? EstimateClass(MeshAsset a)
    {
        if (!a.ClassEstimated && a.HasBounds)
        {
            // The mesh's half-diagonal is never less than the farthest vertex from the box's centre (what the baker measures), so this is the class's
            // upper bound: a mesh it rejects can never get one, and one it accepts may still be rejected by the load.
            a.EstimatedClass = ImpostorClass.For(a.Radius * a.Mesh.MaxScale * 1.02f, a.Triangles);
            a.ClassEstimated = true;
        }
        return a.EstimatedClass;
    }

    /// <summary>The ground distance from which a mesh would be its impostor, for an atlas of class <paramref name="cls"/> (exact once loaded): the impostor
    /// distance for medium and large atlases, in proportion to the size for the small class.</summary>
    float TransitionFor(in ImpostorClass cls, float worldRadius) => cls.Transition(worldRadius, ImpostorDistance);

    /// <summary>The transition of an atlas not made yet, from the class estimated from the mesh's bounds (infinite when it has none).</summary>
    float EstimatedTransition(MeshAsset a) => EstimateClass(a) is { } c ? TransitionFor(c, a.Radius * a.Mesh.MaxScale) : float.PositiveInfinity;

    /// <summary>The ground distance from which a mesh is its impostor (infinite without a resident atlas or with the switch off).</summary>
    float TransitionOf(MeshAsset a) => Impostors && a.Impostor is { Stage: ImpostorStage.Ready } s ? TransitionFor(s.Class, s.WorldRadius) : float.PositiveInfinity;

    /// <summary>
    /// A group's range for a view with its impostor, and which parts the view draws (<see cref="FoliageCull.MeshPart"/>,
    /// <see cref="FoliageCull.ImpostorPart"/>): the meshes when the zone has ground before the transition, the impostors when it has ground in
    /// or beyond the crossfade band (with a margin for instances on the zone's edge). No impostor when the transition is not before the range's
    /// own fade band (the two fades never overlap) or the view takes none.
    /// </summary>
    (FoliageGroupRange Range, int Parts) WithImpostor(FoliageGroupRange r, MeshAsset a, float range, float band, ZoneState zone, Vector3 eye, bool view)
    {
        if (!view) return (r, FoliageCull.MeshPart);
        float t = TransitionOf(a);
        if (!(t <= range - band)) return (r, FoliageCull.MeshPart);
        float b = t * ImpostorBand;
        const float Edge = 64;
        float near = ZoneDistance(zone.Zone, eye), far = ZoneFarDistance(zone, eye);
        int parts = (near - Edge < t ? FoliageCull.MeshPart : 0) | (far + Edge >= t - b ? FoliageCull.ImpostorPart : 0);
        if (parts == 0) parts = FoliageCull.MeshPart;
        return (r.WithTransition(t, b), parts);
    }

    static float ZoneFarDistance(ZoneState z, Vector3 eye)
    {
        float dx = Math.Max(Math.Abs(eye.X - z.X0), Math.Abs(eye.X - (z.X0 + WorldLayout.ZoneSize)));
        float dz = Math.Max(Math.Abs(eye.Z - z.Z0), Math.Abs(eye.Z - (z.Z0 + WorldLayout.ZoneSize)));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    // ---- residency: wanted, plan, refine ----

    /// <summary>The atlases wanted this scan, with the nearest ground distance of any zone that would draw them as impostors (before the transition floor).</summary>
    readonly Dictionary<MeshAsset, float> impostorWanted = [];

    struct PlanItem
    {
        public MeshAsset Asset;
        public float Need, Key;
        public int Frame, Grid, Levels;
        public float Radius;
        public int BaseSkip, Extra;
        public bool Admit;
        public long Bytes;
    }

    readonly List<PlanItem> impostorPlan = [];

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

    /// <summary>The GPU bytes of an atlas of <paramref name="grid"/> frames of <paramref name="framePixels"/> with <paramref name="skip"/> top albedo levels (one more of the normal map) left out: BC1 and BC5, same as <see cref="ImpostorTextures.BytesFor"/>.</summary>
    internal static long AtlasBytes(int framePixels, int grid, int skip)
    {
        int levels = System.Numerics.BitOperations.Log2((uint)framePixels) - 1;
        skip = Math.Clamp(skip, 0, levels - 1);
        int normalSkip = Math.Clamp(skip + 1, 0, levels - 1);
        long side = (long)grid * framePixels, bytes = 0;
        for (int l = skip; l < levels; l++) bytes += (side >> l) * (side >> l) / 2;
        for (int l = normalSkip; l < levels; l++) bytes += (side >> l) * (side >> l);
        return bytes;
    }

    /// <summary>The limit from the current budget and pressure (once a scan).</summary>
    void UpdateImpostorLimit()
    {
        long budget = guard is { BudgetBytes: > 0 } g ? g.BudgetBytes : (long)Math.Min(Gpu.Device.VideoMemory().Budget, (ulong)long.MaxValue);
        impostorLimitBytes = (long)(ImpostorBudget.LimitMb(ImpostorBudgetMb, budget, Gpu.Device.IsIntegrated, guard is { Pressure: true }) * 1048576);
    }

    /// <summary>
    /// Decides, nearest first, which wanted atlases fit the limit and at which mip (<see cref="impostorWanted"/> in, <see cref="impostorPlan"/> out). A resident
    /// atlas counts as nearer (<see cref="ImpostorStickiness"/>). An atlas that does not fit at the mip its nearest instance needs may take up to
    /// <see cref="ImpostorMaxExtraLevels"/> more off; one that still does not fit stays a mesh.
    /// </summary>
    void PlanImpostors(long now)
    {
        impostorPlan.Clear();
        // The distance the mip is chosen for is the nearest instance's divided by 2^margin: the sampler's lod at the screen's edge is up to half a level
        // finer than the centre's (a ray off the axis crosses a frame plane at a larger angle) and the upscaler's texture bias (negative) makes it finer still.
        float margin = MathF.Pow(2, -(FarMipMargin + Math.Max(0, -Gpu.LodBias)));
        foreach (var (a, raw) in impostorWanted)
        {
            var s = a.Impostor;
            if (s is { Stage: ImpostorStage.None, RetryAt: 0 }) continue;   // never an impostor: ineligible, too small or unreadable
            int frame, grid, levels;
            float radius, t;
            if (s is { FramePixels: > 0 })
            {
                (frame, grid, levels, radius) = (s.FramePixels, s.Grid, s.Levels, s.WorldRadius);
                t = TransitionFor(s.Class, s.WorldRadius);
            }
            else
            {
                if (EstimateClass(a) is not { } est) continue;
                (frame, grid, levels, radius) = (est.FramePixels, est.Grid, est.Levels, a.Radius * a.Mesh.MaxScale);
                t = TransitionFor(est, radius);
            }
            // Far mips: the nearest instance's ground distance, never inside the transition (an impostor is not drawn nearer); without them the transition itself, as before.
            float need = FarMips ? Math.Max(raw * margin, t) : t;
            impostorPlan.Add(new PlanItem { Asset = a, Need = need, Key = need * (s is { Stage: ImpostorStage.Ready } ? ImpostorStickiness : 1f), Frame = frame, Grid = grid, Levels = levels, Radius = radius });
        }
        impostorPlan.Sort((x, y) => x.Key.CompareTo(y.Key));
        long limit = impostorLimitBytes, cum = 0;
        int admitted = 0, extras = 0;
        for (int i = 0; i < impostorPlan.Count; i++)
        {
            var it = impostorPlan[i];
            it.BaseSkip = SkipFor(it.Frame, it.Levels, it.Radius, it.Need);
            for (int extra = 0; extra <= (FarMips ? ImpostorMaxExtraLevels : 0); extra++)
            {
                int skip = Math.Min(it.BaseSkip + extra, it.Levels - 1);
                long bytes = AtlasBytes(it.Frame, it.Grid, skip);
                if (cum + bytes > limit) { if (skip == it.Levels - 1) break; continue; }
                (it.Admit, it.Extra, it.Bytes) = (true, extra, bytes);
                cum += bytes;
                admitted++;
                if (extra > 0) extras++;
                break;
            }
            impostorPlan[i] = it;
        }
        (impostorWantedCount, impostorAdmittedCount, impostorExtraCount, impostorPlannedBytes) = (impostorPlan.Count, admitted, extras, cum);
    }

    /// <summary>Applies the plan: drops what it left out, makes room by evicting atlases nobody wants when the real use is over the limit, requests the
    /// admitted ones, refines or coarsens the resident ones. Counts what is not yet in place.</summary>
    void ApplyImpostorPlan(long now)
    {
        long limit = impostorLimitBytes;
        impostorRefineQueue.Clear();
        // Bytes the admitted atlases that are not resident yet will take.
        long pending = 0;
        foreach (var it in impostorPlan)
            if (it.Admit && it.Asset.Impostor is not { Stage: ImpostorStage.Ready }) pending += it.Bytes;
        bool pressure = guard is { Pressure: true };
        // Atlases nobody wanted this scan go first (least recently used first) when the real use plus what is coming passes the limit.
        if (impostorBytes + pending > limit)
        {
            foreach (var a in assetsByMesh.Values
                .Where(a => a.Impostor is { Stage: ImpostorStage.Ready } s && now - s.LastUsed > 1000 && !impostorWanted.ContainsKey(a))
                .OrderBy(a => a.Impostor!.LastUsed)
                .ToList())
            {
                if (ImpostorLog) Console.WriteLine($"impostor  evict {a.Mesh.Name} ({a.Impostor!.Textures!.Bytes / 1048576.0:0.0} MB, unused {(now - a.Impostor.LastUsed) / 1000.0:0.0} s) for the budget");
                UnloadImpostor(a);
                if (impostorBytes + pending <= limit) break;
            }
        }
        bool over = impostorBytes + pending > limit;
        int missing = 0, coarse = 0;
        foreach (var it in impostorPlan)
        {
            var a = it.Asset;
            var s = a.Impostor;
            if (!it.Admit)
            {
                if (s is { Stage: ImpostorStage.Ready } && (++s.DropScans >= 2 || pressure))
                {
                    if (ImpostorLog) Console.WriteLine($"impostor  drop {a.Mesh.Name} ({s.Textures!.Bytes / 1048576.0:0.0} MB, needed from {it.Need:0} units) over the limit of {limit / 1048576.0:0} MB");
                    impostorDropped++;
                    UnloadImpostor(a);
                }
                else if (s is { Stage: ImpostorStage.None, RetryAt: > 0 } refused) refused.LastUsed = now;
                continue;
            }
            if (s is null)
            {
                RequestImpostor(a, now);
                s = a.Impostor;
            }
            else if (s is { Stage: ImpostorStage.None, RetryAt: > 0 } refused && now >= refused.RetryAt)
            {
                a.Impostor = null;   // the plan says it fits now
                RequestImpostor(a, now);
                s = a.Impostor;
            }
            if (s is null) continue;
            s.DropScans = 0;
            s.LastUsed = now;
            (s.Need, s.Extra) = (it.Need, it.Extra);
            if (s.PriorityScan != now) (s.Priority, s.PriorityScan) = (impostorWanted[a], now);
            if (s.Stage != ImpostorStage.Ready) { if (s.Stage != ImpostorStage.None) missing++; continue; }
            if (!FarMips || s.FramePixels == 0) continue;
            int want = Math.Clamp(SkipFor(s.FramePixels, s.Levels, s.WorldRadius, it.Need) + it.Extra, 0, s.Levels - 1);
            s.WantSkip = want;
            if (want < s.Skip) coarse++;
            // How long it has held a finer level than wanted (a candidate for coarsening).
            if (want > s.Skip) { if (s.FinerSince == 0) s.FinerSince = now; }
            else s.FinerSince = 0;
            // Finer: an instance is coming near (refine at once); coarser: only when the budget needs it, or after holding more than needed for a while.
            if (want < s.Skip || (want > s.Skip && (over || (want >= s.Skip + 2 && now - s.FinerSince > ImpostorCoarsenSeconds * 1000)))) impostorRefineQueue.Add(a);
        }
        (impostorMissingCount, impostorCoarseCount) = (missing, coarse);
        StartQueuedRefines(now);
    }

    /// <summary>Atlases the plan wants remade (finer or coarser), from the last scan: <see cref="StartQueuedRefines"/> starts them as slots free up.</summary>
    readonly List<MeshAsset> impostorRefineQueue = [];

    /// <summary>Starts refines from the queue while fewer than <see cref="ImpostorMaxRefines"/> run: finer ones first (an instance is about to need them), nearest first.</summary>
    void StartQueuedRefines(long now)
    {
        while (impostorRefining.Count < ImpostorMaxRefines && impostorRefineQueue.Count > 0)
        {
            int best = -1;
            float bestKey = float.PositiveInfinity;
            for (int i = 0; i < impostorRefineQueue.Count; i++)
            {
                if (impostorRefineQueue[i].Impostor is not { Stage: ImpostorStage.Ready, Next: null, RefineLoad: null } s || s.WantSkip == s.Skip || now < s.RefineRetryAt || (s.WantSkip < s.Skip && guard is { Pressure: true }))
                {
                    impostorRefineQueue.RemoveAt(i--);
                    continue;
                }
                float key = (s.WantSkip < s.Skip ? 0 : 1e9f) + s.Need;
                if (key < bestKey) (best, bestKey) = (i, key);
            }
            if (best < 0) return;
            var a = impostorRefineQueue[best];
            impostorRefineQueue.RemoveAt(best);
            StartRefine(a, a.Impostor!.WantSkip);
        }
    }

    /// <summary>Reloads the atlas from the cache on a worker to remake its textures with <paramref name="skip"/> top levels left out (finer or coarser than now).</summary>
    void StartRefine(MeshAsset a, int skip)
    {
        var s = a.Impostor!;
        if (s.Source is not { } source) return;
        var cache = impostorCache;
        int frame = s.FramePixels, grid = s.Grid;
        s.RefineSkip = skip;
        s.RefineLoad = BackgroundWork.Run<ImpostorAtlas?>(() =>
        {
            var atlas = cache.TryLoad(source);
            return atlas is not null && atlas.FramePixels == frame && atlas.Grid == grid ? atlas : null;
        });
        impostorRefining.Add(a);
    }

    /// <summary>Steps the refines in flight: the loaded atlas's new textures are made and uploaded within the frame's budget, then swapped in (the old ones are drawn until then).</summary>
    void StepImpostorRefines(bool settling, ref long uploaded)
    {
        for (int i = 0; i < impostorRefining.Count; i++)
        {
            var a = impostorRefining[i];
            var s = a.Impostor;
            if (s is not { Stage: ImpostorStage.Ready, Textures: not null }) { impostorRefining.RemoveAt(i--); continue; }   // unloaded meanwhile (UnloadImpostor frees the rest)
            if (s.RefineLoad is { } load)
            {
                if (!load.IsCompleted) continue;
                s.RefineAtlas = load.IsCompletedSuccessfully ? load.Result : null;
                s.RefineLoad = null;
                if (s.RefineAtlas is null) { s.RefineRetryAt = Environment.TickCount64 + 8000; impostorRefining.RemoveAt(i--); continue; }
            }
            var atlas = s.RefineAtlas!;
            if (s.Next is null)
            {
                long need = ImpostorTextures.BytesFor(atlas, s.RefineSkip, s.RefineSkip + 1);
                bool finer = s.RefineSkip < s.Skip;
                if (finer && (impostorBytes + need > impostorLimitBytes * ImpostorTransientShare || guard is { } g && !g.Allows((ulong)need))) { Abandon(s, impostorRefining, i--); continue; }   // no room beside the old textures: the plan is asked again at the next scan
                using var first = Gpu.Uploads.Begin();
                s.Next = new ImpostorTextures(Gpu, atlas, first, s.RefineSkip, s.RefineSkip + 1);
                impostorBytes += s.Next.Bytes;
            }
            long budget = settling ? SettleUploadBytes : UploadBytesPerFrame;
            if (uploaded >= budget) { impostorUploadsWaiting = settling; continue; }
            using (var batch = Gpu.Uploads.Begin())
            {
                do
                {
                    uploaded += s.Next.NextStepBytes;
                    s.Next.UploadStep(batch);
                } while (!s.Next.Complete && uploaded + s.Next.NextStepBytes <= budget);
            }
            if (!s.Next.Complete) { impostorUploadsWaiting = settling; continue; }
            var old = s.Textures!;
            (s.Textures, s.Next) = (s.Next, null);
            impostorBytes -= old.Bytes;
            old.Dispose();
            if (s.RefineSkip < s.Skip) impostorRefined++; else impostorCoarsened++;
            if (ImpostorLog) Console.WriteLine($"impostor  {(s.RefineSkip < s.Skip ? "refined" : "coarsened")} {a.Mesh.Name}: skip {s.Skip} -> {s.RefineSkip}, {s.Textures.Bytes / 1048576.0:0.0} MB ({impostorBytes / 1048576.0:0.0} of {ImpostorLimitMb:0} MB resident)");
            s.Skip = s.RefineSkip;
            (s.RefineAtlas, s.FinerSince) = (null, 0);
            impostorRefining.RemoveAt(i--);
            residentStamp++;
        }

        void Abandon(ImpostorState s, List<MeshAsset> list, int index)
        {
            s.RefineAtlas = null;
            s.RefineRetryAt = Environment.TickCount64 + 2000;
            list.RemoveAt(index);
        }
    }

    /// <summary>
    /// Once a second (every update while settling): asks for the atlases of meshes with a group within reach of their transition, plans which fit
    /// the budget and at which mip (<see cref="PlanImpostors"/>), unloads those unused for <see cref="IdleSeconds"/>; then steps the loads, the one
    /// bake in progress, the uploads and the refines.
    /// </summary>
    void UpdateImpostors(Vector3 eye, bool settling)
    {
        long now = Environment.TickCount64;
        if (Impostors && (settling || now - lastImpostorScan >= 1000))
        {
            lastImpostorScan = now;
            if (!impostorCacheChecked)
            {
                impostorCacheChecked = true;
                impostorCache.MaintainInBackground();
                // The baker's programs compile here, with the loading, not at the first bake
                // (the first bake of a cold cache was a 60 ms frame). The drawing programs and the quad follow below.
                Gpu.EnsureFrame();
                impostorBaker ??= new ImpostorBaker(Gpu, textures);
                // The drawing programs and the quad too, here in the update (a frame is open, no pass is): not at the first impostor in view
                // (a 7 ms hitch: both programs and the quad's upload).
                impostorDraw ??= new ImpostorDraw(Gpu, nativeFrame);
            }
            UpdateImpostorLimit();
            // The nearest any transition is: the small class's smallest (a zone ending before it has no impostor ground for any mesh).
            float shortest = ImpostorDistance * (ImpostorClass.SmallEnabled ? Math.Min(1, ImpostorClass.SmallMinimumRadius / ImpostorClass.MinimumRadius) : 1) * (1 - ImpostorBand);
            // Bakes wait in order of how soon their zones are close: from where the eye is now or will be in a few seconds of its motion.
            var lead = settling ? Vector2.Zero : velocity * ImpostorLookaheadSeconds;
            if (lead.Length() > MaxLookahead) lead = Vector2.Normalize(lead) * MaxLookahead;
            var soonEye = eye + new Vector3(lead.X, 0, lead.Y);
            impostorWanted.Clear();
            foreach (var state in zones.Values)
            {
                if (!state.Ready) continue;
                float near = ZoneDistance(state.Zone, eye), far = ZoneFarDistance(state, eye);
                if (far < shortest) continue;
                float urgency = Math.Min(near, ZoneDistance(state.Zone, soonEye));
                foreach (var g in state.Groups)
                {
                    var a = g.Asset;
                    // Only meshes that can be impostors here: the group reaches beyond the transition (its range ends after it and its
                    // fade band, as WithImpostor requires) and this zone has ground from the band on.
                    var (range, band) = RangeOf(g);
                    if (!a.Resident || !a.HasBounds || near > range) continue;
                    if (a.Impostor is { Stage: ImpostorStage.None, RetryAt: 0 }) continue;
                    // Its own transition (the exact one once the atlas is loaded; before that estimated from the mesh's bounds, which also rules out
                    // a mesh that could never have an atlas: the load would only find that out).
                    float t = a.Impostor is { FramePixels: > 0 } loaded ? TransitionFor(loaded.Class, loaded.WorldRadius) : EstimatedTransition(a);
                    if (range - band < t || far < t * (1 - ImpostorBand)) continue;
                    impostorWanted[a] = impostorWanted.TryGetValue(a, out float known) ? Math.Min(known, urgency) : urgency;
                }
            }
            PlanImpostors(now);
            ApplyImpostorPlan(now);
        }
        if (now - lastImpostorScan < 2000 || settling)
            foreach (var a in assetsByMesh.Values)
                if (a.Impostor is { Stage: ImpostorStage.Ready } s && (now - s.LastUsed) / 1000.0 > ImpostorIdleSeconds) UnloadImpostor(a);
        if (impostorBakeMemoryHeld && impostorBake is null && impostorBakes.Count == 0 && now - impostorLastBake > ImpostorBakeIdleMs)
        {
            impostorBaker?.Trim();
            ImpostorAssembler.ReleasePool();
            impostorBakeMemoryHeld = false;
        }
        StartQueuedRefines(now);
        if (impostorWork.Count == 0 && impostorBakes.Count == 0 && impostorBake is null && impostorRefining.Count == 0) return;
        Gpu.EnsureFrame();
        long uploaded = 0;
        impostorUploadsWaiting = false;
        for (int i = 0; i < impostorWork.Count; i++)
        {
            var a = impostorWork[i];
            var s = a.Impostor!;
            if (s.Stage == ImpostorStage.Loading)
            {
                if (!s.Load!.IsCompleted) continue;
                var (source, meshes, cls, atlas) = s.Load.IsCompletedSuccessfully ? s.Load.Result : default;
                s.Load = null;
                if (source is null || meshes is null || cls is not { } c) { s.Stage = ImpostorStage.None; impostorWork.RemoveAt(i--); continue; }
                (s.Source, s.Meshes, s.Class) = (source, meshes, c);
                (s.FramePixels, s.Grid, s.Levels, s.WorldRadius) = (c.FramePixels, c.Grid, c.Levels, meshes.Radius * a.Mesh.MaxScale);
                if (atlas is null) { s.Stage = ImpostorStage.Baking; impostorBakes.Add(a); impostorWork.RemoveAt(i--); continue; }
                impostorsLoaded++;
                s.Atlas = atlas;
                s.Stage = ImpostorStage.Uploading;
            }
            if (s.Stage != ImpostorStage.Uploading) continue;
            // At most a budget of atlas levels a frame, over all atlases: the copy into the staging is render-thread time (a flight that
            // reaches a new biome asks for dozens at once), and while settling, when no frames end, the staging lives in the frame's
            // constant chunks, which are kept for good.
            long budget = settling ? SettleUploadBytes : UploadBytesPerFrame;
            if (uploaded >= budget) { impostorUploadsWaiting = settling; continue; }
            if (UploadImpostor(a, budget, ref uploaded)) impostorWork.RemoveAt(i--);
        }
        StepImpostorRefines(settling, ref uploaded);
        StepBake(settling);
    }

    void RequestImpostor(MeshAsset a, long now)
    {
        if (a.Impostor is not null) return;
        if (a.Terrain || ImpostorSource.Ineligible(a.Mesh) is not null) { a.Impostor = new ImpostorState { Stage = ImpostorStage.None }; return; }
        var mesh = a.Mesh;
        var locator = assets;
        var cache = impostorCache;
        var s = a.Impostor = new ImpostorState { Stage = ImpostorStage.Loading, LastUsed = now };
        var messages = Messages;
        s.Load = BackgroundWork.Run<(ImpostorSource?, ImpostorMeshes?, ImpostorClass?, ImpostorAtlas?)>(() =>
        {
            var source = ImpostorSource.From(mesh, locator);
            if (source is null) return default;
            var meshes = ImpostorMeshes.Load(source, messages);
            if (meshes is null) return (source, null, null, null);
            var cls = ImpostorClass.For(meshes.Radius * mesh.MaxScale, meshes.Triangles);
            if (cls is not { } c) return (source, meshes, null, null);
            var atlas = cache.TryLoad(source);
            if (atlas is not null && (atlas.FramePixels != c.FramePixels || atlas.Grid != c.Grid)) atlas = null;
            return (source, meshes, c, atlas);
        });
        impostorWork.Add(a);
    }

    /// <summary>The picture height the atlas resolution is chosen for (<see cref="SkipFor"/>): 1080 lines (above it the nearest impostors are slightly softer).</summary>
    const float ImpostorReferenceHeight = 1080;
    /// <summary>Impostor atlases unused this long are unloaded (shorter than the meshes' <see cref="IdleSeconds"/>: an atlas is 9 to 36 MB).</summary>
    const double ImpostorIdleSeconds = 20;

    /// <summary>The top levels an atlas never needs at a transition distance (<see cref="SkipFor"/> with the atlas's own radius).</summary>
    internal static int LevelsToSkip(ImpostorAtlas atlas, float maxScale, float transition) => SkipFor(atlas.FramePixels, atlas.Levels, atlas.Radius * maxScale, transition);

    /// <summary>The atlas's textures, level by level within the frame's upload budget; true when resident.</summary>
    bool UploadImpostor(MeshAsset a, long budget, ref long uploaded)
    {
        var s = a.Impostor!;
        if (s.Textures is null)
        {
            // The albedo's top levels its nearest instance never samples are left out, and the normal map's one level more: it shapes the lighting,
            // which varies slowly over a crown.
            int levels = Math.Clamp(SkipFor(s.Atlas!.FramePixels, s.Atlas.Levels, s.Atlas.Radius * a.Mesh.MaxScale, FarMips ? Math.Max(s.Need, TransitionFor(s.Class, s.WorldRadius)) : TransitionFor(s.Class, s.WorldRadius))
                + (FarMips ? s.Extra : 0), 0, s.Atlas.Levels - 1);
            long need = ImpostorTextures.BytesFor(s.Atlas!, levels, levels + 1);
            if (!MakeImpostorRoom(need, a) || guard is { } g && !g.Allows((ulong)need))
            {
                impostorRefused++;
                if (ImpostorLog) Console.WriteLine($"impostor  refused {a.Mesh.Name}: {need / 1048576.0:0.0} MB would pass the budget ({impostorBytes / 1048576.0:0.0} of {ImpostorLimitMb:0} MB resident)");
                (s.Stage, s.RetryAt, s.Atlas, s.Meshes) = (ImpostorStage.None, Environment.TickCount64 + (long)(ImpostorRetrySeconds * 1000), null, null);
                return true;
            }
            using var first = Gpu.Uploads.Begin();
            s.Textures = new ImpostorTextures(Gpu, s.Atlas!, first, levels, levels + 1);
            s.Skip = levels;
            impostorBytes += s.Textures.Bytes;
        }
        using var batch = Gpu.Uploads.Begin();
        // At least one step (a level larger than the budget still goes), then while the budget lasts.
        do
        {
            uploaded += s.Textures.NextStepBytes;
            s.Textures.UploadStep(batch);
        } while (!s.Textures.Complete && uploaded + s.Textures.NextStepBytes <= budget);
        if (!s.Textures.Complete) return false;
        s.Stage = ImpostorStage.Ready;
        if (ImpostorLog) Console.WriteLine($"impostor  ready {a.Mesh.Name}: {s.Atlas!.Grid}x{s.Atlas.Grid} frames of {s.Atlas.FramePixels}, skip {s.Skip}, {s.Textures.Bytes / 1048576.0:0.0} MB ({impostorBytes / 1048576.0:0.0} of {ImpostorLimitMb:0} MB resident)");
        s.Atlas = null;   // the CPU copy is not needed any more (a reload reads the cache again)
        s.Meshes = null;
        residentStamp++;
        return true;
    }

    /// <summary>The bake in progress, a step a frame (rows recorded, read back, filtered and encoded on workers); the next one when it is done.</summary>
    void StepBake(bool settling)
    {
        if (impostorBake is null)
        {
            if (impostorBakes.Count == 0) return;
            // The one whose zones are closest (now or soon) first.
            int best = 0;
            for (int i = 1; i < impostorBakes.Count; i++)
                if (impostorBakes[i].Impostor!.Priority < impostorBakes[best].Impostor!.Priority) best = i;
            var next = impostorBakes[best];
            impostorBakes.RemoveAt(best);
            var s = next.Impostor!;
            impostorBaker ??= new ImpostorBaker(Gpu, textures);
            impostorBake = impostorBaker.Begin(s.Source!, s.Meshes!, s.Class);
            impostorBakeAsset = next;
        }
        var size = impostorBake.Size;
        double rowSamples = size.Grid * 3.0 * (2.0 * size.FramePixels) * (2.0 * size.FramePixels);
        impostorBake.RowsPerStep = settling ? size.Grid : Math.Clamp((int)(ImpostorBakeSamplesPerFrame / rowSamples), 1, size.Grid);
        (impostorLastBake, impostorBakeMemoryHeld) = (Environment.TickCount64, true);
        if (!impostorBake.Step()) return;
        var a = impostorBakeAsset!;
        var st = a.Impostor!;
        if (impostorBake.Result is { } atlas)
        {
            impostorsBaked++;
            var source = st.Source!;
            var cache = impostorCache;
            BackgroundWork.Run(() =>
            {
                try { cache.Save(source, atlas); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            });
            st.Atlas = atlas;
            st.Stage = ImpostorStage.Uploading;
            impostorWork.Add(a);
        }
        else
        {
            Messages.Add($"impostor {a.Mesh.Name}: {impostorBake.Error?.Message}");
            st.Stage = ImpostorStage.None;
        }
        impostorBake.Dispose();
        (impostorBake, impostorBakeAsset) = (null, null);
    }

    /// <summary>
    /// Safety net at an atlas's first upload: room for <paramref name="need"/> more bytes within the limit (and the transient share a refine may use):
    /// atlases the last scans did not ask for (unused for 3 s) go, least recently used first. The plan has already counted what fits, so this
    /// only acts when refines or coarsenings are still in flight. False when it does not fit.
    /// </summary>
    bool MakeImpostorRoom(long need, MeshAsset except)
    {
        long limit = (long)(impostorLimitBytes * ImpostorTransientShare);
        if (impostorBytes + need <= limit) return true;
        long now = Environment.TickCount64;
        foreach (var a in assetsByMesh.Values
            .Where(a => a != except && a.Impostor is { Stage: ImpostorStage.Ready } s && now - s.LastUsed > 3000)
            .OrderBy(a => a.Impostor!.LastUsed)
            .ToList())
        {
            if (ImpostorLog) Console.WriteLine($"impostor  evict {a.Mesh.Name} ({a.Impostor!.Textures!.Bytes / 1048576.0:0.0} MB, unused {(now - a.Impostor.LastUsed) / 1000.0:0.0} s) for the budget");
            UnloadImpostor(a);
            if (impostorBytes + need <= limit) return true;
        }
        return false;
    }

    void UnloadImpostor(MeshAsset a)
    {
        var s = a.Impostor!;
        if (s.Next is not null)
        {
            impostorBytes -= s.Next.Bytes;
            s.Next.Dispose();
            s.Next = null;
        }
        if (s.RefineLoad is { } load) { try { load.Wait(); } catch (AggregateException) { } s.RefineLoad = null; }
        if (s.Textures is not null)
        {
            impostorBytes -= s.Textures.Bytes;
            s.Textures.Dispose();
        }
        a.Impostor = null;   // asked for again (from the cache) when needed
        impostorUnloads++;
        residentStamp++;
    }

    void DisposeImpostors()
    {
        impostorBake?.Dispose();
        foreach (var a in assetsByMesh.Values)
        {
            if (a.Impostor is not { } s) continue;
            try { s.Load?.Wait(); } catch (AggregateException) { }
            try { s.RefineLoad?.Wait(); } catch (AggregateException) { }
            s.Textures?.Dispose();
            s.Next?.Dispose();
        }
        impostorBaker?.Dispose();
        impostorDraw?.Dispose();
    }

    // ---- drawing ----

    /// <summary>The impostor batches of the current view, in work-list order (the GPU path: after the mesh and rock batches).</summary>
    readonly List<MeshAsset> gpuImpostorOrder = [];
    /// <summary>The CPU path's impostor batches (the cull's emission order), and its batches by mesh.</summary>
    readonly List<Batch> impostorActive = [];
    readonly Dictionary<MeshAsset, Batch> impostorBatches = [];

    struct ImpostorDrawItem
    {
        public MeshAsset Asset;
        public int Batch;
        public uint FirstInstance, Instances;
    }

    /// <summary>The current view's impostor draws: one per impostor batch shown (the GPU path) or with instances (the CPU path).</summary>
    readonly List<ImpostorDrawItem> impostorDraws = [];

    Batch ImpostorBatchOf(MeshAsset a)
    {
        if (!impostorBatches.TryGetValue(a, out var batch)) impostorBatches[a] = batch = new Batch { Asset = a };
        if (batch.Stamp != drawStamp)
        {
            batch.Stamp = drawStamp;
            batch.Count = 0;
            batch.Index = impostorActive.Count;
            impostorActive.Add(batch);
        }
        return batch;
    }

    void EmitImpostors(MeshAsset a, ReadOnlySpan<Matrix4x4> visible)
    {
        if (visible.Length == 0) return;
        DrawnInstances += visible.Length;
        var batch = ImpostorBatchOf(a);
        if (batch.Count + visible.Length > batch.Data.Length) Array.Resize(ref batch.Data, Math.Max(batch.Data.Length * 2, batch.Count + visible.Length));
        visible.CopyTo(batch.Data.AsSpan(batch.Count));
        batch.Count += visible.Length;
    }

    /// <summary>The impostor draws: per impostor batch shown (GPU) or not empty (CPU).</summary>
    void PrepareImpostors(bool gpu)
    {
        impostorDraws.Clear();
        if (gpu)
        {
            int first = gpuOrder.Count + gpuRockOrder.Count;
            for (int i = 0; i < gpuImpostorOrder.Count; i++)
                if (gpuBatchShown[first + i]) impostorDraws.Add(new ImpostorDrawItem { Asset = gpuImpostorOrder[i], Batch = first + i });
        }
        else
            foreach (var b in impostorActive)
                if (b.Count > 0) impostorDraws.Add(new ImpostorDrawItem { Asset = b.Asset, Batch = b.Index, FirstInstance = (uint)b.Offset, Instances = (uint)b.Count });
        DrawCalls += impostorDraws.Count;
    }

    /// <summary>
    /// Makes the impostor pipeline of the host's current state and formats ahead of the first impostor of a pass (the same key
    /// <see cref="AddImpostorDraws"/> asks for: the host's state with the foliage's alpha to coverage), so a tree that comes into view does not
    /// compile one. Called by every foliage pass that draws no impostor; a pipeline is made once per combination. Nothing before the programs
    /// exist (the switch off, or before the first foliage update).
    /// </summary>
    void WarmImpostorPipeline(bool depth, bool coverage)
    {
        if (impostorDraw is null || !Impostors) return;
        var state = Gpu.CurrentState() with { Cull = Silk.NET.Vulkan.CullModeFlags.None, AlphaToCoverage = coverage };
        impostorDraw.Pipeline(depth ? ImpostorProgram.Caster : ImpostorProgram.Plain, state, Gpu.CurrentTargets().Formats);
    }

    /// <summary>The impostor draws appended to a mesh segment's job (after its mesh draws; no culling, the rows already bound): a colour view's
    /// lit program, or a cascade's caster facing the sun (the direction towards it from the cascade's matrix).</summary>
    void AddImpostorDraws(MeshJob job, bool depth, Matrix4x4 viewProjection, PassTargets targets, DrawState state, bool coverage, bool indirect, int argsFirst)
    {
        impostorDraw ??= new ImpostorDraw(Gpu, nativeFrame);
        var drawState = state with { Cull = Silk.NET.Vulkan.CullModeFlags.None };
        var program = depth ? ImpostorProgram.Caster : ImpostorProgram.Plain;
        var pipeline = impostorDraw.Pipeline(program, drawState, targets.Formats);
        var up = new Vector3(viewProjection.M12, viewProjection.M22, viewProjection.M32);
        up = up.LengthSquared() > 0 ? Vector3.Normalize(up) : Vector3.UnitY;
        var forward = new Vector3(viewProjection.M13, viewProjection.M23, viewProjection.M33);
        var view = depth && forward.LengthSquared() > 0 ? new Vector4(-Vector3.Normalize(forward), 1) : Vector4.Zero;
        var quad = impostorDraw.Quad;
        BufferBinding[] none = [];
        for (int k = 0; k < impostorDraws.Count; k++)
        {
            var d = impostorDraws[k];
            var t = d.Asset.Impostor!.Textures!;
            var atlas = t.Atlas;
            var push = new ImpostorPush
            {
                Sphere = new Vector4(atlas.Centre, atlas.Radius), CameraUp = up, Grid = atlas.Grid,
                Albedo = t.Index(0, textureBias), Normal = t.Index(1, textureBias), Gloss = atlas.Gloss,
                Coverage = coverage ? 1u : 0u, View = view,
            };
            MeshPush bytes = default;
            System.Runtime.CompilerServices.Unsafe.As<MeshPush, ImpostorPush>(ref bytes) = push;
            job.Add(new MeshJob.Draw
            {
                SetRaster = k == 0, Cull = drawState.Cull, Pipeline = pipeline, Vertices = none, Elements = quad, Push = bytes,
                Args = indirect ? gpuResult.Args : default, ArgsOffset = gpuResult.ArgsOffset + (ulong)(argsFirst + k) * 20,
                IndexCount = 6, Instances = d.Instances, FirstInstance = d.FirstInstance,
            });
        }
    }
}
