using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;

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
public sealed unsafe class FoliageRenderer : IDisposable
{
    /// <summary>Grass pages per zone side (576 units each).</summary>
    const int PagesPerZone = 8;
    const float PageSize = WorldLayout.ZoneSize / (float)PagesPerZone;
    const int InstanceStride = 64;

    readonly IGl gl;
    readonly GameInstall install;
    readonly GameDatabase db;
    readonly WorldLevelData levels;
    readonly AssetLocator assets;
    readonly FoliageCatalog catalog;
    uint meshProgram;   // the depth program while DrawDepth runs
    readonly uint grassProgram;
    readonly uint grassMotionProgram;
    readonly Dictionary<(uint, string), int> uniforms = [];
    readonly WorldTextureCache textures;
    readonly uint instanceBuffer;
    readonly ConcurrentBag<FoliageWorld> worlds = [];
    readonly List<FoliageWorld> allWorlds = [];
    readonly Dictionary<ZoneCoordinate, ZoneState> zones = [];
    readonly Dictionary<FoliageMesh, MeshAsset> assetsByMesh = [];
    readonly List<MeshAsset> decoding = [];
    readonly Queue<Action> uploads = new();
    readonly Dictionary<MeshAsset, Batch> batches = [];
    readonly List<Batch> active = [];
    readonly List<(uint, int, Matrix4x4)> terrainDraws = [];
    readonly Stopwatch clock = Stopwatch.StartNew();
    /// <summary>Seconds that drive the grass sway; null uses a wall clock started with the renderer. Screenshots fix it (pictures repeat exactly) and the game sets its own clock.</summary>
    public double? SwaySeconds { get; set; }
    readonly int workers;
    /// <summary>MEITOU_FOLIAGE_DEBUG: "nograss" or "nomeshes" leaves that part out (to measure the other).</summary>
    readonly bool debugNoGrass = Environment.GetEnvironmentVariable("MEITOU_FOLIAGE_DEBUG") == "nograss", debugNoMeshes = Environment.GetEnvironmentVariable("MEITOU_FOLIAGE_DEBUG") == "nomeshes";
    long instanceBufferSize;
    /// <summary>Zone layouts in flight: whole ones and far-only ones, each limited to <see cref="workers"/> at a time.</summary>
    int runningWhole, runningFar;

    public FoliageRenderer(IGl gl, GameInstall install, GameDatabase db, WorldLevelData levels, AssetLocator assets)
    {
        this.gl = gl;
        this.install = install;
        this.db = db;
        this.levels = levels;
        this.assets = assets;
        var watch = Stopwatch.StartNew();
        catalog = FoliageCatalog.Load(db);
        meshProgram = WorldGl.Program(gl, FoliageShaders.MeshVertex(), FoliageShaders.MeshFragment());
        grassProgram = WorldGl.Program(gl, FoliageShaders.GrassVertex, FoliageShaders.GrassFragment);
        grassMotionProgram = WorldGl.Program(gl, FoliageShaders.GrassMotionVertex, FoliageShaders.GrassMotionFragment);
        textures = new WorldTextureCache(gl, assets);
        instanceBuffer = gl.GenBuffer();
        workers = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
        var used = catalog.ByBiome.Values.SelectMany(l => l).Distinct().ToList();
        MeshRange = used.Where(l => !l.IsGrass).Select(l => l.Range).DefaultIfEmpty(0).Max();
        NearMeshRange = used.Where(l => !l.IsGrass && !FoliageLayout.IsFarLayer(l)).Select(l => l.Range).DefaultIfEmpty(0).Max();
        GrassMaxRange = used.Where(l => l.IsGrass).Select(l => l.Range).DefaultIfEmpty(0).Max();
        LoadMs = watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Foliage drawn at all (the F key).</summary>
    public bool Enabled { get; set; } = true;
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

    /// <summary>Zones within this ground distance of the eye are laid out whole: the longest MEDIUM / CLOSE mesh layer range or grass range at the current settings.</summary>
    public float NearReach => Math.Max(NearMeshRange * RangeSetting, GrassMaxRange * GrassRangeSetting);
    /// <summary>Zones within this distance are laid out at least for their far layers (FAR, 8000 x the setting).</summary>
    public float FarReach => Math.Max(MeshRange * RangeSetting, NearReach);
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
    readonly List<(ZoneCoordinate Zone, bool Whole, float Urgency)> wanted = [];
    readonly List<ZoneState> dropping = [];
    public List<string> Messages { get; } = [];

    /// <summary>Work in flight: zones being laid out, grass pages, meshes decoding or uploading, textures decoding.</summary>
    public int Pending => runningWhole + runningFar + zonesWaiting + grassWaiting + decoding.Count + uploads.Count + textures.PendingCount + zones.Values.Sum(z => z.GrassRunning);

    /// <summary>GPU memory held by foliage meshes, textures and grass pages.</summary>
    public long ResidentBytes => residentMeshBytes + textures.ResidentBytes + GrassBytes();
    public string ResidentDescription =>
        $"{residentMeshBytes / 1048576.0:0} MB in {assetsByMesh.Values.Count(a => a.Resident)} meshes ({meshUnloads} unloaded, {meshReloads} reloaded), {textures.Describe()}, {GrassBytes() / 1048576.0:0} MB of grass pages";
    public string Describe() =>
        $"{zones.Values.Count(z => z.Ready)} zones laid out ({zones.Values.Where(z => z.Ready).Sum(z => z.Instances):N0} meshes, " +
        $"{zones.Values.Sum(z => z.Pages.Count):N0} grass pages), {assetsByMesh.Count} foliage meshes ({assetsByMesh.Values.Count(a => a.Resident)} resident), catalog of {catalog.Layers.Count} layers";

    // ------------------------------------------------------------------ streaming

    sealed class ZoneState
    {
        public required ZoneCoordinate Zone;
        public float X0, Z0;
        public Task<(FoliageZone Zone, FoliageGround? Ground, PreparedZone Prepared)>? Job;
        /// <summary>Laid out (at least the far layers); <see cref="Complete"/>: every layer, grass included. <see cref="JobWhole"/>: the job in flight is a whole layout.</summary>
        public bool Ready, Complete, JobWhole;
        public List<Group> Groups = [];
        public FoliageGround? Ground;
        public List<FoliageGrassPatch> Patches = [];
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
    sealed record PreparedGroup(FoliageMesh Mesh, FoliageLayer Layer, Matrix4x4[] Transforms, Vector3[] Positions, float[] Scales, float MaxScale);
    sealed record PreparedZone(List<PreparedGroup> Groups, float MinY, float MaxY);

    static PreparedZone Prepare(FoliageZone zone)
    {
        var groups = new List<PreparedGroup>();
        foreach (var g in zone.Instances.GroupBy(i => (i.Mesh, i.Layer)))
        {
            var list = g.ToList();
            groups.Add(new PreparedGroup(g.Key.Mesh, g.Key.Layer, [.. list.Select(i => i.Transform)], [.. list.Select(i => i.Position)], [.. list.Select(i => i.Scale)], list.Max(i => i.Scale)));
        }
        return new PreparedZone(groups, zone.Instances.Count == 0 ? 0 : zone.Instances.Min(i => i.Position.Y), zone.Instances.Count == 0 ? 0 : zone.Instances.Max(i => i.Position.Y));
    }

    /// <summary>The instances of one mesh in one zone, with the range of the layer that placed them.</summary>
    sealed class Group
    {
        public required MeshAsset Asset;
        /// <summary>The layer's range at setting 1 and its transition: the drawn range follows the current <see cref="RangeSetting"/> (<see cref="RangeOf"/>).</summary>
        public required float BaseRange, Transition;
        /// <summary>The largest instance scale: how far a mesh's bounds can reach from its instance's position (the zone's cull box).</summary>
        public required float MaxScale;
        public required Matrix4x4[] Transforms;
        public required Vector3[] Positions;
        public required float[] Scales;
    }

    sealed class GrassPage
    {
        public Task<(float[][] Blades, int[][] Prefixes)>? Job;
        public GrassBuffer[]? Buffers;
        public bool Dropped;
    }

    sealed class GrassBuffer
    {
        public uint Vao, Vbo;
        public int Count;
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
        float nearReach = NearReach, farReach = FarReach;
        foreach (var state in zones.Values)
        {
            if (state.Job is not { IsCompleted: true } job) continue;
            if (state.JobWhole) runningWhole--; else runningFar--;
            state.Job = null;
            try { Accept(state, job.Result); }
            catch (AggregateException e) { Messages.Add($"foliage zone {state.Zone}: {e.InnerException?.Message ?? e.Message}"); state.Ready = state.Complete = true; }
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
                bool whole = soon <= nearReach + PrefetchMargin;
                zones.TryGetValue(z, out var state);
                if (state is not null && (state.Complete || !whole && state.Ready)) continue;   // has what it needs
                if (d <= nearReach) NearestIncompleteZone = Math.Min(NearestIncompleteZone, d);
                if (state is not { Ready: true }) NearestUnlaidZone = Math.Min(NearestUnlaidZone, d);
                if (state?.Job is not null) continue;   // on its way
                // Whole layouts first (what is missing there is in view near the eye), then the far ones, each nearest first (from the eye
                // or where it will be, whichever is nearer).
                wanted.Add((z, whole, whole ? soon : farReach + soon));
            }
        wanted.Sort((a, b) => a.Urgency.CompareTo(b.Urgency));
        int started = 0;
        foreach (var (zone, whole, _) in wanted)
        {
            if (whole ? runningWhole >= workers : runningFar >= workers) continue;
            if (!zones.TryGetValue(zone, out var state))
            {
                var (x0, z0) = WorldLayout.ZoneOrigin(zone);
                zones[zone] = state = new ZoneState { Zone = zone, X0 = (float)x0, Z0 = (float)z0 };
            }
            if (whole) runningWhole++; else runningFar++;
            state.JobWhole = whole;
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
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            Update(eye, settling: true);
            if (++rounds > 2 && Pending == 0) return;
            Thread.Sleep(2);
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
    void Accept(ZoneState state, (FoliageZone Zone, FoliageGround? Ground, PreparedZone Prepared) result)
    {
        var (zone, ground, prepared) = result;
        var groups = new List<Group>(prepared.Groups.Count);
        foreach (var g in prepared.Groups)
            groups.Add(new Group
            {
                Asset = AssetFor(g.Mesh),
                BaseRange = g.Layer.Range,
                Transition = g.Layer.Transition,
                MaxScale = g.MaxScale,
                Transforms = g.Transforms,
                Positions = g.Positions,
                Scales = g.Scales,
            });
        state.Groups = groups;
        state.Instances = zone.Instances.Count;
        state.MinY = prepared.MinY;
        state.MaxY = prepared.MaxY;
        state.Ground = ground;
        state.Patches = zone.Grass.Where(p => p.Grass.Sprite is not null && p.Density.Any(d => d != 0)).ToList();
        foreach (var p in state.Patches)
        {
            textures.Get(p.Grass.Sprite, false);   // start decoding now, not at the first draw
            textures.Get(p.Grass.ColourMap, false);
        }
        state.Ready = true;
        state.Complete = zone.Complete;
    }

    /// <summary>The range a group is drawn to at the current <see cref="RangeSetting"/>, and its fade band: the game's transition is 10 units
    /// (100 for wind layers), too short to see, so a tenth of the range instead.</summary>
    (float Range, float Band) RangeOf(Group g)
    {
        float range = g.BaseRange * RangeSetting;
        return (range, Math.Max(g.Transition, range * 0.1f));
    }

    void Drop(ZoneState state)
    {
        foreach (var page in state.Pages.Values) FreePage(page);
        state.Pages.Clear();
        zones.Remove(state.Zone);
    }

    void FreePage(GrassPage page)
    {
        page.Dropped = true;   // an upload still queued for it deletes what it made instead of attaching it
        if (page.Buffers is null) return;
        foreach (var b in page.Buffers)
        {
            if (b.Vao != 0) gl.DeleteVertexArray(b.Vao);
            if (b.Vbo != 0) gl.DeleteBuffer(b.Vbo);
        }
        page.Buffers = null;
    }

    float GrassRange(FoliageGrassPatch patch) => patch.Layer.Range * GrassRangeSetting;

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
                        var p = page;
                        QueuePage(p, result.Blades, result.Prefixes);
                    }
                }
        }
        // Missing pages nearest first, a bounded number at a time. Pages hold the blades of the highest density setting,
        // so changing the setting never regenerates them (Draw shows a prefix).
        grassWanted.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        grassWaiting = grassWanted.Count;
        foreach (var (_, state, key) in grassWanted)
        {
            if (inFlight >= MaxGrassJobs) break;
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

    /// <summary>A grass page's upload as steps of about 512 KB (allocate, slabs, then the page takes the buffers), so no frame holds a whole page.</summary>
    void QueuePage(GrassPage page, float[][] blades, int[][] prefixes)
    {
        var buffers = new GrassBuffer[blades.Length];
        for (int i = 0; i < blades.Length; i++)
        {
            var b = buffers[i] = new GrassBuffer { Count = blades[i].Length / FoliageGrassField.Stride, Prefixes = prefixes[i] };
            if (b.Count == 0) continue;
            var data = blades[i];
            int bytes = data.Length * sizeof(float);
            uploads.Enqueue(() =>
            {
                b.Vao = gl.GenVertexArray();
                b.Vbo = gl.GenBuffer();
                gl.BindVertexArray(b.Vao);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, b.Vbo);
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)bytes, null, BufferUsageARB.StaticDraw);
                uint stride = FoliageGrassField.Stride * 4;
                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(0, 4, VertexAttribPointerType.Float, false, stride, (void*)0);
                gl.VertexAttribDivisor(0, 1);
                gl.EnableVertexAttribArray(1);
                gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, (void*)16);
                gl.VertexAttribDivisor(1, 1);
                gl.BindVertexArray(0);
            });
            for (int at = 0; at < bytes; at += SlabBytes)
            {
                int start = at, length = Math.Min(SlabBytes, bytes - at);
                uploads.Enqueue(() =>
                {
                    gl.BindBuffer(BufferTargetARB.ArrayBuffer, b.Vbo);
                    fixed (float* p = data) gl.BufferSubData(BufferTargetARB.ArrayBuffer, start, (nuint)length, (byte*)p + start);
                });
            }
        }
        uploads.Enqueue(() =>
        {
            page.Buffers = buffers;
            if (page.Dropped) FreePage(page);   // the page went out of range while its buffers were being filled
        });
    }

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
    }

    sealed class GpuMesh
    {
        public List<GpuPart> Parts = [];
    }

    sealed class GpuPart
    {
        public uint Vao, PlainVao, Vbo, Ebo;
        public int Count;
        /// <summary>Blades after each 1/64 of the candidates (<see cref="FoliageGrassField.BladesWithPrefixes"/>): the density setting draws a prefix.</summary>
        public int[] Prefixes = [];
        public bool HasTangents, HasColours;
    }

    sealed record FoliageMaterial(WorldTexture? Diffuse, WorldTexture? Normal, WorldTexture? Diffuse2, WorldTexture? Normal2,
        float AlphaThreshold, bool DoubleSided, bool Triplanar, Vector2 Tile, bool Emissive, float Specular);

    MeshAsset AssetFor(FoliageMesh mesh)
    {
        if (assetsByMesh.TryGetValue(mesh, out var a)) return a;
        a = assetsByMesh[mesh] = new MeshAsset { Mesh = mesh };
        // MapFeatureMode: UV_MAPPED, TRIPLANAR, TERRAIN, DUAL_TEXTURE, FOLIAGE, DUAL_TRIPLANAR, EMISSIVE (the map-feature builder's rules,
        // docs/formats/runtime-materials.md); FOLIAGE cuts out on the normal map's alpha at "alpha threshold" / 255, double-sided.
        int mode = mesh.MaterialType;
        bool dual = mode is 3 or 5;
        a.MainMaterial = new FoliageMaterial(textures.Get(mesh.Texture, false), textures.Get(mesh.Normal, false),
            dual ? textures.Get(mesh.Texture2, false) : null, dual ? textures.Get(mesh.Normal2, false) : null,
            mode == 4 ? mesh.AlphaThreshold / 255f : 0, mode == 4, mode is 1 or 5, new Vector2(mesh.TileX, mesh.TileY), mode == 6, mesh.SpecularMult);
        // The leaves: their own texture pair, transparent and double-sided, cut out at "leaves alpha threshold" / 255.
        if (mesh.LeavesMesh is not null)
            a.LeavesMaterial = new FoliageMaterial(textures.Get(mesh.LeavesTexture, false), textures.Get(mesh.LeavesNormal, false), null, null,
                mesh.LeavesAlphaThreshold / 255f, true, false, Vector2.One, false, 0);
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
                gp = new GpuPart { Vbo = gl.GenBuffer(), Ebo = gl.GenBuffer(), Count = p.Indices.Length, HasTangents = p.HasTangents, HasColours = p.HasColours };
                gp.Vao = Vao(gp, instanced: true);
                gp.PlainVao = Vao(gp, instanced: false);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, gp.Vbo);
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)vertexBytes, null, BufferUsageARB.StaticDraw);
                gl.BindVertexArray(gp.PlainVao);   // the element buffer binding belongs to the vertex array
                gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)indexBytes, null, BufferUsageARB.StaticDraw);
                gl.BindVertexArray(0);
                mesh.Parts.Add(gp);
            });
            for (int at = 0; at < vertexBytes; at += SlabBytes)
            {
                int start = at, length = Math.Min(SlabBytes, vertexBytes - at);
                uploads.Enqueue(() =>
                {
                    gl.BindBuffer(BufferTargetARB.ArrayBuffer, gp!.Vbo);
                    fixed (Vertex* v = p.Vertices) gl.BufferSubData(BufferTargetARB.ArrayBuffer, start, (nuint)length, (byte*)v + start);
                });
            }
            for (int at = 0; at < indexBytes; at += SlabBytes)
            {
                int start = at, length = Math.Min(SlabBytes, indexBytes - at);
                uploads.Enqueue(() =>
                {
                    gl.BindVertexArray(gp!.PlainVao);
                    fixed (uint* v = p.Indices) gl.BufferSubData(BufferTargetARB.ElementArrayBuffer, start, (nuint)length, (byte*)v + start);
                    gl.BindVertexArray(0);
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
                    if (m is not null) _ = (m.Diffuse?.Id, m.Normal?.Id, m.Diffuse2?.Id, m.Normal2?.Id);   // reading an id counts as use (WorldTexture.Id)
            }
        }
        foreach (var a in assetsByMesh.Values)
        {
            if (!a.Resident || (now - a.LastUsed) / 1000.0 <= IdleSeconds) continue;
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
            gl.DeleteVertexArray(gp.Vao);
            gl.DeleteVertexArray(gp.PlainVao);
            gl.DeleteBuffer(gp.Vbo);
            gl.DeleteBuffer(gp.Ebo);
        }
    }

    /// <summary>Decodes an unloaded mesh again (from <see cref="Draw"/>, when something in range needs it).</summary>
    void Reload(MeshAsset a)
    {
        if (a.Resident || a.Failed || a.Job is not null || a.Uploading) return;
        string main = a.Mesh.MeshPath, leaves = a.Mesh.LeavesMesh ?? "";
        a.LastUsed = Environment.TickCount64;
        a.Job = BackgroundWork.Run(() => (Decode(main), leaves.Length > 0 ? Decode(leaves) : null));
        decoding.Add(a);
        meshReloads++;
    }

    uint Vao(GpuPart gp, bool instanced)
    {
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, gp.Vbo);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gp.Ebo);
        uint stride = (uint)Vertex.Size;
        void Attrib(uint index, int size, int offset)
        {
            gl.EnableVertexAttribArray(index);
            gl.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, stride, (void*)offset);
        }
        Attrib(0, 3, 0);
        Attrib(1, 3, 12);
        Attrib(2, 2, 24);
        Attrib(3, 4, 32);
        Attrib(4, 4, 48);
        if (instanced)
        {
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, instanceBuffer);
            for (uint a = 0; a < 4; a++)
            {
                gl.EnableVertexAttribArray(FoliageShaders.InstanceLocation + a);
                gl.VertexAttribPointer(FoliageShaders.InstanceLocation + a, 4, VertexAttribPointerType.Float, false, InstanceStride, (void*)(16 * a));
                gl.VertexAttribDivisor(FoliageShaders.InstanceLocation + a, 1);
            }
        }
        gl.BindVertexArray(0);
        return vao;
    }

    // ------------------------------------------------------------------ drawing

    sealed class Batch
    {
        public required MeshAsset Asset;
        public Matrix4x4[] Data = new Matrix4x4[64];
        public int Count, Offset;

        public void Add(in Matrix4x4 m)
        {
            if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
            Data[Count++] = m;
        }
    }


    /// <summary>An instance in range of the shadow cascades' eye, kept for the frame's further cascades (see <see cref="Draw"/>).</summary>
    readonly record struct ShadowCandidate(MeshAsset Asset, Matrix4x4 Transform, Vector3 Centre, float Radius, float Weight);
    readonly List<ShadowCandidate> shadowCandidates = [];
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
            foreach (var gp in a.Main!.Parts) terrainDraws.Add((gp.PlainVao, gp.Count, t));
            return;
        }
        if (!batches.TryGetValue(a, out var batch)) batches[a] = batch = new Batch { Asset = a };
        if (batch.Count == 0) active.Add(batch);
        var m = t;
        m.M14 = w >= 0.999f ? 2 : w;
        batch.Add(m);
    }
    /// <summary>Draws the foliage seen from <paramref name="eye"/> through <paramref name="frustum"/>. Without <paramref name="grass"/> only the meshes (e.g. for a reflection).
    /// <paramref name="continuation"/>: a further depth slice of the same frame, adding to the counts and the GPU time. <paramref name="maxRange"/> caps every layer's range (the reflection).</summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, Vector3 light, Vector3 fogColour, float fogDistance, TerrainRenderer terrain, bool grass = true, bool continuation = false, float maxRange = float.PositiveInfinity)
    {
        if (!continuation) { DrawnInstances = 0; DrawnBlades = 0; DrawCalls = 0; if (!depthPass) MainDetail = ""; }
        if (!Enabled) return;
        int timer = grass ? BeginTimer(continuation) : -1;
        var cpu = Stopwatch.StartNew();
        foreach (var b in batches.Values) b.Count = 0;
        active.Clear();
        terrainDraws.Clear();

        // 1. Cull: each instance by its distance along the ground (the game's pages) and its bounding sphere, the groups (a mesh in a zone)
        // in parallel, each into its own list, merged into the batches in order (the same instance order as one pass would give). The shadow
        // cascades of a frame share one pass over the zones (DrawDepth with the same eye and range): it keeps every instance in range, and
        // each cascade then only tests those spheres against its own frustum.
        bool cachedDepth = depthPass && shadowCandidatesFrame == updates && shadowCandidatesEye == eye && shadowCandidatesRange == maxRange;
        if (cachedDepth) CullCandidates(frustum, options);
        else CullZones(eye, frustum, options, maxRange, record: depthPass);
        double tCull = cpu.Elapsed.TotalMilliseconds;

        // 2. Upload the instances: one buffer, each batch's matrices contiguous.
        int total = 0;
        foreach (var b in active) { b.Offset = total; total += b.Count; }
        if (total > 0)
        {
            long bytes = total * (long)InstanceStride;
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, instanceBuffer);
            instanceBufferSize = Math.Max(bytes, instanceBufferSize);
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)instanceBufferSize, null, BufferUsageARB.StreamDraw);
            foreach (var b in active)
                fixed (Matrix4x4* p = b.Data)
                    gl.BufferSubData(BufferTargetARB.ArrayBuffer, (nint)(b.Offset * (long)InstanceStride), (nuint)(b.Count * (long)InstanceStride), p);
        }

        double tUpload = cpu.Elapsed.TotalMilliseconds;
        gl.GetInteger(GLEnum.Samples, out int samples);
        bool coverage = samples > 1;
        if (coverage) gl.Enable(EnableCap.SampleAlphaToCoverage);

        // 3. Meshes.
        if (active.Count > 0 && !debugNoMeshes)
        {
            uint prog = meshProgram;
            gl.UseProgram(prog);
            WorldGl.Matrix(gl, U(prog, "uViewProjection"), viewProjection);
            gl.Uniform3(U(prog, "uEye"), eye.X, eye.Y, eye.Z);
            gl.Uniform3(U(prog, "uLightDir"), light.X, light.Y, light.Z);
            gl.Uniform3(U(prog, "uFogColour"), fogColour.X, fogColour.Y, fogColour.Z);
            gl.Uniform1(U(prog, "uFogDistance"), fogDistance);
            gl.Uniform1(U(prog, "uTriplanarScale"), 1f / 5000);
            string[] samplers = ["uDiffuse", "uNormal", "uDiffuse2", "uNormal2", "uHeadDiffuse", "uHeadNormal"];
            for (int i = 0; i < samplers.Length; i++) gl.Uniform1(U(prog, samplers[i]), i);
            gl.Uniform1(U(prog, "uSkinned"), 0);
            gl.Uniform1(U(prog, "uHasHead"), 0);
            gl.Uniform1(U(prog, "uWireframe"), 0);
            gl.Uniform1(U(prog, "uCoverage"), coverage ? 1 : 0);
            SkyRenderer.Active?.Apply(prog);   // the atmosphere's uniforms, as the objects and terrain get them
            gl.UseProgram(prog);
            foreach (var b in active)
            {
                var a = b.Asset;
                DrawMesh(a.Main!, a.MainMaterial!, b, options);
                if (a.Leaves is not null) DrawMesh(a.Leaves, a.LeavesMaterial!, b, options);
            }
            gl.Enable(EnableCap.CullFace);
            gl.BindVertexArray(0);
        }

        double tMeshes = cpu.Elapsed.TotalMilliseconds;
        // 4. Grass.
        if (grass && !debugNoGrass) DrawGrass(viewProjection, eye, frustum, options, light, fogColour, fogDistance, coverage);
        double tGrass = cpu.Elapsed.TotalMilliseconds;
        if (coverage) gl.Disable(EnableCap.SampleAlphaToCoverage);
        gl.Disable(EnableCap.CullFace);
        gl.ActiveTexture(TextureUnit.Texture0);

        // 5. TERRAIN-mode rocks through the terrain's own mesh path.
        if (terrainDraws.Count > 0)
        {
            DrawCalls += terrain.DrawMeshes(terrainDraws, depthPass);
        }
        gl.Disable(EnableCap.CullFace);
        if (WorldFrame.DetailedStats)
        {
            double tEnd = cpu.Elapsed.TotalMilliseconds;
            string steps = $"cull{(cachedDepth ? " (cached)" : "")} {tCull:0.00}, upload {tUpload - tCull:0.00}, meshes {tMeshes - tUpload:0.00} in {active.Count} batches, " +
                $"grass {tGrass - tMeshes:0.00}, TERRAIN-mode rocks {terrainDraws.Count} {tEnd - tGrass:0.00}";
            if (depthPass) DepthDetail = steps;
            else MainDetail += $" [{steps}]";
        }
        EndTimer(timer);
        LastDrawCpuMs = (continuation ? LastDrawCpuMs : 0) + cpu.Elapsed.TotalMilliseconds;
    }

    // ---- culling (Draw step 1) ----

    /// <summary>One group's share of a culling pass: the instances it keeps, as batch matrices (row 0 w = the fade), and the shadow candidates when recording.</summary>
    sealed class CullOutput
    {
        public Matrix4x4[] Visible = new Matrix4x4[64];
        public int Count;
        public ShadowCandidate[] Candidates = new ShadowCandidate[64];
        public int CandidateCount;
        /// <summary>Runs of <see cref="Visible"/> that share a mesh (the shadow candidates' chunks).</summary>
        public readonly List<(MeshAsset Asset, int Start, int Count)> Runs = [];
    }

    readonly List<(Group Group, float Range, float Band)> cullWork = [];
    readonly List<CullOutput> cullOutputs = [];

    /// <summary>The fade packed into a batch matrix: row 0 w, 2 = fully visible (see <see cref="FoliageShaders.MeshVertex"/>).</summary>
    static Matrix4x4 Packed(in Matrix4x4 t, float w) { var m = t; m.M14 = w >= 0.999f ? 2 : w; return m; }

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

    void CullZones(Vector3 eye, Vector4[] frustum, WorldRenderOptions options, float maxRange, bool record)
    {
        if (record) { shadowCandidates.Clear(); (shadowCandidatesFrame, shadowCandidatesEye, shadowCandidatesRange) = (updates, eye, maxRange); }
        // The zones and groups in range, on this thread (reloads start here).
        cullWork.Clear();
        foreach (var state in zones.Values)
        {
            if (!state.Ready) continue;
            float zoneDistance = ZoneDistance(state.Zone, eye);
            // The zone's box against the frustum first: most of a far, wide ring is behind the camera. Its margin is how far the bounds of
            // the zone's meshes reach from their instances' positions (ruins and rock pillars reach hundreds to thousands of units; a fixed
            // margin of 300 culled such a mesh whose zone was out of view while the mesh itself was in it). Not while recording for the
            // cascades: each has its own frustum.
            if (!record)
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
                cullWork.Add((g, range, band));
            }
        }
        while (cullOutputs.Count < cullWork.Count) cullOutputs.Add(new CullOutput());
        var eyeXz = new Vector2(eye.X, eye.Z);
        RenderJobs.For(cullWork.Count, k =>
        {
            var (g, range, band) = cullWork[k];
            var output = cullOutputs[k];
            output.Count = output.CandidateCount = 0;
            var a = g.Asset;
            for (int i = 0; i < g.Positions.Length; i++)
            {
                var p = g.Positions[i];
                float d = Vector2.Distance(new Vector2(p.X, p.Z), eyeXz);
                if (d >= range) continue;
                ref var t = ref g.Transforms[i];
                var centre = Vector3.Transform(a.Centre, t);
                float radius = a.Radius * g.Scales[i];
                float w = Math.Clamp((range - d) / band, 0, 1);
                if (record)
                {
                    if (output.CandidateCount == output.Candidates.Length) Array.Resize(ref output.Candidates, output.CandidateCount * 2);
                    output.Candidates[output.CandidateCount++] = new ShadowCandidate(a, t, centre, radius, w);
                }
                if (!SphereVisible(frustum, centre, radius)) continue;
                if (output.Count == output.Visible.Length) Array.Resize(ref output.Visible, output.Count * 2);
                output.Visible[output.Count++] = Packed(t, w);
            }
        });
        for (int k = 0; k < cullWork.Count; k++)
        {
            var output = cullOutputs[k];
            if (record) shadowCandidates.AddRange(output.Candidates.AsSpan(0, output.CandidateCount));
            EmitAll(cullWork[k].Group.Asset, output.Visible.AsSpan(0, output.Count), options);
        }
    }

    /// <summary>A further shadow cascade: the frame's recorded candidates against this cascade's frustum (tested in parallel, emitted in order).</summary>
    void CullCandidates(Vector4[] frustum, WorldRenderOptions options)
    {
        int count = shadowCandidates.Count, chunks = (count + CandidateChunk - 1) / CandidateChunk;
        while (candidateOutputs.Count < chunks) candidateOutputs.Add(new CullOutput());
        var list = shadowCandidates;
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
                if (!SphereVisible(frustum, c.Centre, c.Radius)) continue;
                if (output.Runs.Count == 0 || output.Runs[^1].Asset != c.Asset) output.Runs.Add((c.Asset, output.Count, 0));
                if (output.Count == output.Visible.Length) Array.Resize(ref output.Visible, output.Count * 2);
                output.Visible[output.Count++] = Packed(c.Transform, c.Weight);
                var run = output.Runs[^1];
                output.Runs[^1] = run with { Count = run.Count + 1 };
            }
        });
        for (int k = 0; k < chunks; k++)
        {
            var output = candidateOutputs[k];
            foreach (var (asset, start, n) in output.Runs) EmitAll(asset, output.Visible.AsSpan(start, n), options);
        }
    }

    const int CandidateChunk = 2048;
    readonly List<CullOutput> candidateOutputs = [];

    Batch BatchOf(MeshAsset a)
    {
        if (!batches.TryGetValue(a, out var batch)) batches[a] = batch = new Batch { Asset = a };
        if (batch.Count == 0) active.Add(batch);
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
    uint depthProgram;
    bool depthPass;

    /// <summary>
    /// Draws the foliage meshes' depth for a shadow cascade: <see cref="Draw"/>'s culling and ranges (measured from the camera's
    /// <paramref name="eye"/>) with <see cref="ShadowShaders.MeshDepthFragment"/>, so the leaves' cut-out holds; no grass. Leaves the draw
    /// counters describing this call.
    /// </summary>
    public void DrawDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, TerrainRenderer terrain, float maxRange = float.PositiveInfinity)
    {
        if (depthProgram == 0) depthProgram = WorldGl.Program(gl, FoliageShaders.MeshVertex(), ShadowShaders.MeshDepthFragment);
        uint main = meshProgram;
        meshProgram = depthProgram;
        depthPass = true;
        try { Draw(viewProjection, eye, frustum, options, Vector3.UnitY, Vector3.Zero, 0, terrain, grass: false, maxRange: maxRange); }
        finally { meshProgram = main; depthPass = false; }
    }

    void DrawMesh(GpuMesh mesh, FoliageMaterial m, Batch b, WorldRenderOptions options)
    {
        uint prog = meshProgram;
        bool textured = options.Textures && m.Diffuse is { Id: not 0 };
        Bind(0, textured ? m.Diffuse!.Id : 0);
        bool normal = textured && m.Normal is { Id: not 0 };
        Bind(1, normal ? m.Normal!.Id : 0);
        bool dual = textured && m.Diffuse2 is { Id: not 0 };
        Bind(2, dual ? m.Diffuse2!.Id : 0);
        Bind(3, dual && m.Normal2 is { Id: not 0 } ? m.Normal2!.Id : 0);
        gl.Uniform1(U(prog, "uNormalSwizzled"), normal && m.Normal!.Swizzled ? 1 : 0);
        gl.Uniform1(U(prog, "uHasDiffuse"), textured ? 1 : 0);
        gl.Uniform1(U(prog, "uTriplanar"), textured && m.Triplanar ? 1 : 0);
        gl.Uniform2(U(prog, "uTile"), m.Tile.X, m.Tile.Y);
        // The cut-out mask is the normal map's alpha: sampled whenever the map exists (the lighting checks for tangents itself).
        bool cut = normal && m.AlphaThreshold > 0;
        gl.Uniform1(U(prog, "uAlphaSource"), cut ? 2 : 0);
        gl.Uniform1(U(prog, "uAlphaChannel"), 3);
        gl.Uniform1(U(prog, "uGreyChannel"), -1);
        gl.Uniform3(U(prog, "uTint"), 1f, 1f, 1f);
        gl.Uniform1(U(prog, "uAlphaThreshold"), cut ? m.AlphaThreshold : 0f);
        gl.Uniform1(U(prog, "uEmissive"), textured && normal && m.Emissive ? 1 : 0);
        gl.Uniform1(U(prog, "uSpecular"), textured ? m.Specular : 0.3f);
        if (m.DoubleSided) gl.Disable(EnableCap.CullFace);
        else gl.Enable(EnableCap.CullFace);
        foreach (var gp in mesh.Parts)
        {
            gl.Uniform1(U(prog, "uHasNormal"), normal && options.NormalMaps || cut ? 1 : 0);
            gl.Uniform1(U(prog, "uHasDual"), dual && gp.HasColours ? 1 : 0);
            gl.Uniform1(U(prog, "uUseVertexColour"), gp.HasColours ? 1 : 0);
            gl.BindVertexArray(gp.Vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, instanceBuffer);
            for (uint a = 0; a < 4; a++)
                gl.VertexAttribPointer(FoliageShaders.InstanceLocation + a, 4, VertexAttribPointerType.Float, false, InstanceStride, (void*)(b.Offset * (long)InstanceStride + 16 * a));
            gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)gp.Count, DrawElementsType.UnsignedInt, (void*)0, (uint)b.Count);
            DrawCalls++;
        }
    }

    readonly List<(float Distance, ZoneState State, int Key, GrassPage Page)> grassOrder = [];

    void DrawGrass(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, Vector3 light, Vector3 fogColour, float fogDistance, bool coverage)
    {
        uint prog = grassProgram;
        bool set = false;
        float time = SwayPhase();
        // Nearest pages first, so the depth test rejects most of the hidden blades before they are shaded.
        grassOrder.Clear();
        foreach (var st in zones.Values)
            foreach (var (k, pg) in st.Pages)
                if (pg.Buffers is not null)
                    grassOrder.Add((BoxDistance(st.X0 + k % PagesPerZone * PageSize, st.Z0 + k / PagesPerZone * PageSize, PageSize, eye), st, k, pg));
        grassOrder.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        {
            foreach (var (d, state, key, page) in grassOrder)
            {
                float x0 = state.X0 + key % PagesPerZone * PageSize, z0 = state.Z0 + key / PagesPerZone * PageSize;
                float y = eye.Y;
                if (state.Ground is { } ground) y = ground.Height(x0 + PageSize / 2, z0 + PageSize / 2);
                if (!WorldCamera.Intersects(frustum, new Vector3(x0, y - 2000, z0), new Vector3(x0 + PageSize, y + 2000, z0 + PageSize))) continue;
                if (page.Buffers is null) continue;
                for (int i = 0; i < page.Buffers.Length && i < state.Patches.Count; i++)
                {
                    var b = page.Buffers[i];
                    var patch = state.Patches[i];
                    float range = GrassRange(patch);
                    int shown = b.Count == 0 ? 0 : FoliageGrassField.PrefixCount(b.Prefixes, Math.Min(GrassDensitySetting, MaxGrassDensity) / MaxGrassDensity);   // the density setting: the first blades of the page
                    if (shown == 0 || d >= range) continue;
                    var sprite = textures.Get(patch.Grass.Sprite, false);
                    if (sprite is not { Id: not 0 }) continue;
                    if (!set)
                    {
                        set = true;
                        gl.UseProgram(prog);
                        WorldGl.Matrix(gl, U(prog, "uViewProjection"), viewProjection);
                        gl.Uniform3(U(prog, "uEye"), eye.X, eye.Y, eye.Z);
                        gl.Uniform3(U(prog, "uLightDir"), light.X, light.Y, light.Z);
                        gl.Uniform3(U(prog, "uFogColour"), fogColour.X, fogColour.Y, fogColour.Z);
                        gl.Uniform1(U(prog, "uFogDistance"), fogDistance);
                        gl.Uniform1(U(prog, "uTime"), time);
                        gl.Uniform1(U(prog, "uFrequency"), 2f);
                        gl.Uniform1(U(prog, "uSprite"), 0);
                        gl.Uniform1(U(prog, "uColourMap"), 1);
                        gl.Uniform1(U(prog, "uCoverage"), coverage ? 1 : 0);
                        gl.Uniform1(U(prog, "uWireframe"), options.Wireframe == 2 ? 1 : 0);
                        SkyRenderer.Active?.Apply(prog);
                        gl.UseProgram(prog);
                        gl.Disable(EnableCap.CullFace);
                    }
                    var g = patch.Grass;
                    Bind(0, options.Textures ? sprite.Id : 0);
                    var colourMap = textures.Get(g.ColourMap, false);
                    bool hasColour = options.Textures && colourMap is { Id: not 0 };
                    Bind(1, hasColour ? colourMap!.Id : 0);
                    gl.Uniform1(U(prog, "uHasColourMap"), hasColour ? 1 : 0);
                    gl.Uniform4(U(prog, "uColourBounds"), patch.X0, patch.Z0, patch.X1, patch.Z1);
                    gl.Uniform4(U(prog, "uSize"), g.QuadMinWidth, g.QuadMaxWidth, g.QuadMinHeight, g.QuadMaxHeight);
                    gl.Uniform1(U(prog, "uCross"), g.CrossQuads ? 1 : 0);
                    gl.Uniform1(U(prog, "uSway"), patch.Layer.Wind ? g.SwayLength : 0f);
                    gl.Uniform1(U(prog, "uRange"), range);
                    gl.BindVertexArray(b.Vao);
                    gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, g.CrossQuads ? 12u : 6u, (uint)shown);
                    DrawCalls++;
                    DrawnBlades += shown;
                }
            }
        }
        gl.BindVertexArray(0);
    }

    float SwayPhase() => (float)((SwaySeconds ?? clock.Elapsed.TotalSeconds) * 0.3 * Math.PI % (2 * Math.PI));

    // ---- grass motion (upscalers) ----

    Matrix4x4 motionViewProjection, motionUnjittered, previousUnjittered;
    Vector3 motionEye;
    Vector4[] motionFrustum = [];
    float? previousSwayPhase;
    bool motionCamera, havePrevious;

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
        uint prog = grassMotionProgram;
        bool set = false;
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
                    var b = page.Buffers[i];
                    var patch = state.Patches[i];
                    var g = patch.Grass;
                    if (!patch.Layer.Wind || g.SwayLength == 0) continue;
                    float range = GrassRange(patch);
                    int shown = b.Count == 0 ? 0 : FoliageGrassField.PrefixCount(b.Prefixes, Math.Min(GrassDensitySetting, MaxGrassDensity) / MaxGrassDensity);
                    if (shown == 0 || d >= range) continue;
                    var sprite = textures.Get(g.Sprite, false);
                    if (sprite is not { Id: not 0 }) continue;
                    if (!set)
                    {
                        set = true;
                        gl.UseProgram(prog);
                        WorldGl.Matrix(gl, U(prog, "uViewProjection"), motionViewProjection);
                        WorldGl.Matrix(gl, U(prog, "uPreviousViewProjection"), previous);
                        gl.Uniform3(U(prog, "uEye"), eye.X, eye.Y, eye.Z);
                        gl.Uniform1(U(prog, "uTime"), time);
                        gl.Uniform1(U(prog, "uPreviousTime"), previousTime);
                        gl.Uniform1(U(prog, "uFrequency"), 2f);
                        gl.Uniform2(U(prog, "uNearPlanes"), targets.NearPlanes.X, targets.NearPlanes.Y);
                        gl.Uniform2(U(prog, "uJitterNdc"), targets.JitterNdc.X, targets.JitterNdc.Y);
                        gl.Uniform1(U(prog, "uSprite"), 0);
                        gl.Uniform1(U(prog, "uNearDepth"), 1);
                        Bind(1, targets.NearDepth);
                        gl.Disable(EnableCap.CullFace);
                    }
                    Bind(0, sprite.Id);
                    gl.Uniform4(U(prog, "uSize"), g.QuadMinWidth, g.QuadMaxWidth, g.QuadMinHeight, g.QuadMaxHeight);
                    gl.Uniform1(U(prog, "uSway"), g.SwayLength);
                    gl.Uniform1(U(prog, "uRange"), range);
                    gl.BindVertexArray(b.Vao);
                    gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, g.CrossQuads ? 12u : 6u, (uint)shown);
                }
            }
        if (set) { gl.BindVertexArray(0); Bind(1, 0); gl.ActiveTexture(TextureUnit.Texture0); }
    }

    void Bind(int unit, uint texture)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    static bool SphereVisible(Vector4[] planes, Vector3 centre, float radius)
    {
        foreach (var p in planes)
            if (p.X * centre.X + p.Y * centre.Y + p.Z * centre.Z + p.W < -radius * MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z)) return false;
        return true;
    }

    // GPU timing with timestamps (the frame itself may be inside a TimeElapsed query, which cannot nest).
    sealed class Timer
    {
        public uint Start, End;
        public bool Pending, Continuation;
    }

    readonly List<Timer> timers = [];
    double gpuSum;
    int gpuCount;

    int BeginTimer(bool continuation)
    {
        int index = timers.FindIndex(t => !t.Pending);
        if (index < 0)
        {
            if (timers.Count >= 16) return -1;
            timers.Add(new Timer { Start = gl.GenQuery(), End = gl.GenQuery() });
            index = timers.Count - 1;
        }
        timers[index].Continuation = continuation;
        gl.QueryCounter(timers[index].Start, QueryCounterTarget.Timestamp);
        return index;
    }

    void EndTimer(int index)
    {
        if (index < 0) return;
        gl.QueryCounter(timers[index].End, QueryCounterTarget.Timestamp);
        timers[index].Pending = true;
    }

    /// <summary>Collects finished timings into <see cref="GpuMs"/> (per <see cref="Update"/>, i.e. per frame).</summary>
    public void PollTimers(bool wait)
    {
        foreach (var t in timers)
        {
            if (!t.Pending) continue;
            gl.GetQueryObject(t.End, QueryObjectParameterName.ResultAvailable, out int available);
            if (available == 0 && !wait) continue;
            gl.GetQueryObject(t.Start, QueryObjectParameterName.Result, out ulong start);
            gl.GetQueryObject(t.End, QueryObjectParameterName.Result, out ulong end);
            gpuSum += (end - start) / 1e6;
            if (!t.Continuation) gpuCount++;
            t.Pending = false;
        }
        if (gpuCount > 0) { GpuMs = gpuSum / gpuCount; gpuSum = 0; gpuCount = 0; }
    }

    int U(uint program, string name)
    {
        if (!uniforms.TryGetValue((program, name), out int location)) uniforms[(program, name)] = location = gl.GetUniformLocation(program, name);
        return location;
    }

    public void Dispose()
    {
        foreach (var z in zones.Values) { try { z.Job?.Wait(); } catch (AggregateException) { } foreach (var p in z.Pages.Values) { try { p.Job?.Wait(); } catch (AggregateException) { } FreePage(p); } }
        foreach (var a in assetsByMesh.Values)
        {
            try { a.Job?.Wait(); } catch (AggregateException) { }
            foreach (var m in new[] { a.Main, a.Leaves })
                if (m is not null)
                    foreach (var gp in m.Parts)
                    {
                        gl.DeleteVertexArray(gp.Vao);
                        gl.DeleteVertexArray(gp.PlainVao);
                        gl.DeleteBuffer(gp.Vbo);
                        gl.DeleteBuffer(gp.Ebo);
                    }
        }
        lock (allWorlds) foreach (var w in allWorlds) w.Dispose();
        textures.Dispose();
        gl.DeleteBuffer(instanceBuffer);
        gl.DeleteProgram(meshProgram);
        if (depthProgram != 0) gl.DeleteProgram(depthProgram);
        gl.DeleteProgram(grassProgram);
        gl.DeleteProgram(grassMotionProgram);
        foreach (var q in timers) { gl.DeleteQuery(q.Start); gl.DeleteQuery(q.End); }
    }
}
