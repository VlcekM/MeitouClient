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
    /// <summary>The native GPU API next to <c>gl</c> (docs/renderer-native.md 7.1 step 8); ports use it instead of looking it up.</summary>
    public GpuContext Gpu { get; }
    readonly GameInstall install;
    readonly GameDatabase db;
    readonly WorldLevelData levels;
    readonly AssetLocator assets;
    readonly FoliageCatalog catalog;
    // The native programs (docs/renderer-native.md 3.3, step O: the native model, the same maths), made with the renderer so a draw never compiles.
    readonly NativeFrame nativeFrame;
    readonly NativeProg colourMesh, depthMesh, grassProgram, grassMotionProgram;
    readonly WorldTextureCache textures;
    /// <summary>The GL buffer the vertex arrays name for the per-instance rows (locations 7 to 10). Never given storage: native draws bind the
    /// frame's constants there (<see cref="instances"/>), so a vertex array's export stays valid from frame to frame.</summary>
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
    /// <summary>Zone layouts in flight: whole ones and far-only ones, each limited to <see cref="workers"/> at a time.</summary>
    int runningWhole, runningFar;

    public FoliageRenderer(IGl gl, GpuContext gpu, GameInstall install, GameDatabase db, WorldLevelData levels, AssetLocator assets)
    {
        this.gl = gl;
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
    float LongestClassRange => Math.Max(Math.Max(SmallRange, MediumRange), LargeRange);
    float ClassRange(FoliageSizeClass c) => c switch { FoliageSizeClass.Small => SmallRange, FoliageSizeClass.Medium => MediumRange, _ => LargeRange };

    /// <summary>Zones within this ground distance of the eye are laid out whole: the longest MEDIUM / CLOSE mesh layer range (with
    /// <see cref="MeitouRange"/>: the longest class range, as any class can be in a MEDIUM layer) or grass range at the current settings.</summary>
    public float NearReach => Math.Max(MeitouRange ? LongestClassRange : NearMeshRange * RangeSetting, GrassMaxRange * GrassRangeSetting);
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
        /// <summary>The instances (<see cref="FoliageCull"/>'s records), their bounding spheres filled once the mesh's bounds are known (<see cref="SpheresReady"/>).</summary>
        public required FoliageInstanceRecord[] Instances;
        public bool SpheresReady;
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
        /// <summary>What a native draw needs, per program (<see cref="NativeMesh"/>).</summary>
        public NativeMesh GrassNative, MotionNative;
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
                Far = FoliageLayout.IsFarLayer(g.Layer),
                MaxScale = g.MaxScale,
                Instances = g.Instances,
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
    /// (100 for wind layers), too short to see, so a tenth of the range instead.
    /// With <see cref="MeitouRange"/> the range is the mesh's size class's (the longest class range until the mesh is decoded and its size known),
    /// or for a large mesh of a FAR layer the longer of that and the layer's; the band is the same rule.</summary>
    (float Range, float Band) RangeOf(Group g)
    {
        float range = g.BaseRange * RangeSetting;
        if (MeitouRange)
        {
            var a = g.Asset;
            float size = a.HasBounds ? ClassRange(a.SizeClass) : LongestClassRange;
            range = g.Far && (!a.HasBounds || a.SizeClass == FoliageSizeClass.Large) ? Math.Max(range, size) : size;
        }
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
        /// <summary><see cref="Centre"/>, <see cref="Radius"/> and <see cref="SizeClass"/> are known (the mesh was decoded once; they never change).</summary>
        public bool HasBounds;
        public FoliageSizeClass SizeClass;
    }

    sealed class GpuMesh
    {
        public List<GpuPart> Parts = [];
    }

    sealed class GpuPart
    {
        public uint Vao, PlainVao, Vbo, Ebo;
        /// <summary>What a native draw needs, per program (<see cref="NativeMesh"/>).</summary>
        public NativeMesh ColourNative, DepthNative;
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
            a.SizeClass = FoliageSizes.Classify(FoliageSizes.Size(a.Radius, a.Mesh));
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
        /// <summary>The <see cref="Draw"/> call that last put this batch into <c>active</c> (<see cref="BatchOf"/>).</summary>
        public long Stamp = -1;

        public void Add(in Matrix4x4 m)
        {
            if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
            Data[Count++] = m;
        }
    }


    /// <summary>An instance in range of the shadow cascades' eye (its group's records and index), kept for the frame's further cascades (see <see cref="Draw"/>).</summary>
    readonly record struct ShadowCandidate(MeshAsset Asset, FoliageInstanceRecord[] Instances, int Index, float Weight);
    readonly List<ShadowCandidate> shadowCandidates = [];
    /// <summary>The meshes of the recording pass's work list, in order: the further cascades' batch order (<see cref="CullCandidates"/>).</summary>
    readonly List<MeshAsset> shadowCandidateOrder = [];
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
            foreach (var gp in a.Main!.Parts) terrainDraws.Add((gp.PlainVao, gp.Count, t));
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
        int timer = grass ? BeginTimer(continuation) : -1;
        var cpu = Stopwatch.StartNew();
        drawStamp++;
        int callsBefore = DrawCalls;
        active.Clear();
        terrainDraws.Clear();

        // 1. Cull: each instance by its distance along the ground (the game's pages) and its bounding sphere, the groups (a mesh in a zone)
        // in parallel, each into its own list, merged into the batches in order (the same instance order as one pass would give). The shadow
        // cascades of a frame share one pass over the zones (DrawDepth with the same eye and range): it keeps every instance in range, and
        // each cascade then only tests those spheres against its own frustum. The batches are drawn in the order of their mesh's first
        // group in the work list, whatever is visible (step A1 of docs/renderer-native.md: an order a GPU cull can reproduce), minus the empty ones.
        bool cachedDepth = depthPass && shadowCandidatesFrame == updates && shadowCandidatesEye == eye && shadowCandidatesRange == maxRange;
        cullView.Set(frustum);
        if (cachedDepth) CullCandidates(options);
        else CullZones(eye, options, maxRange, record: depthPass);
        active.RemoveAll(static b => b.Count == 0);
        double tCull = cpu.Elapsed.TotalMilliseconds;
        StageClock.Sub("fol cull");

        // 2. The instances: each batch's matrices contiguous, in this frame's constants (one copy; the draws reach a batch by firstInstance).
        int total = 0;
        foreach (var b in active) { b.Offset = total; total += b.Count; }
        if (total > 0)
        {
            instances = Gpu.Frame.Constants.Allocate((ulong)total * InstanceStride, 16);
            var target = new Span<byte>(instances.Pointer, total * InstanceStride);
            foreach (var b in active)
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * InstanceStride)..]);
        }

        double tUpload = cpu.Elapsed.TotalMilliseconds;
        StageClock.Sub("fol upload");
        gl.GetInteger(GLEnum.Samples, out int samples);
        bool coverage = samples > 1;
        if (coverage) gl.Enable(EnableCap.SampleAlphaToCoverage);

        // 3. Meshes: Prepare reads the textures (WorldTexture.Id) and makes the draw list, Record puts it into a native segment of VkGl's pass.
        bool drew = false;
        double recMeshes = 0, recGrass = 0;
        if (active.Count > 0 && !debugNoMeshes)
        {
            PrepareMeshes(options);
            if (meshDraws.Count > 0)
            {
                long r0 = FolTiming ? Stopwatch.GetTimestamp() : 0;
                RecordMeshes(depthPass ? depthMesh : colourMesh, depthPass, viewProjection, eye, light, fogColour, fogDistance, coverage);
                if (FolTiming) recMeshes = (Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency;
                drew = true;
            }
        }

        double tMeshes = cpu.Elapsed.TotalMilliseconds;
        int callsMeshes = DrawCalls;
        StageClock.Sub("fol meshes");
        // 4. Grass.
        if (grass && !debugNoGrass)
        {
            PrepareGrass(eye, frustum, options, coverage);
            if (grassDraws.Count > 0)
            {
                long r0 = FolTiming ? Stopwatch.GetTimestamp() : 0;
                RecordGrass(viewProjection, eye, light, fogColour, fogDistance);
                if (FolTiming) recGrass = (Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency;
                drew = true;
            }
        }
        if (drew) SkyRenderer.Active?.BindUnits();   // the atmosphere's texture units, as Apply leaves them for the GL code that follows
        double tGrass = cpu.Elapsed.TotalMilliseconds;
        int callsGrass = DrawCalls;
        StageClock.Sub("fol grass");
        if (coverage) gl.Disable(EnableCap.SampleAlphaToCoverage);
        gl.Disable(EnableCap.CullFace);
        gl.ActiveTexture(TextureUnit.Texture0);

        // 5. TERRAIN-mode rocks through the terrain's own mesh path.
        if (terrainDraws.Count > 0)
        {
            DrawCalls += terrain.DrawMeshes(terrainDraws, depthPass);
        }
        gl.Disable(EnableCap.CullFace);
        StageClock.Sub("fol rocks");
        if (FolTiming) FolAccount(depthPass ? 1 : 0, tCull, tUpload, tMeshes, tGrass, cpu.Elapsed.TotalMilliseconds, callsMeshes - callsBefore, callsGrass - callsMeshes, terrainDraws.Count, recMeshes, recGrass);
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

    /// <summary><c>MEITOU_FOLIAGE_TIMING=1</c>: the CPU time of <see cref="Draw"/>'s steps summed over the run by kind (colour or depth), with the
    /// draws each step issued, printed when the renderer is disposed (docs/renderer-native.md 7.1, wave 3 foliage probe).</summary>
    static readonly bool FolTiming = Environment.GetEnvironmentVariable("MEITOU_FOLIAGE_TIMING") == "1";
    readonly double[,] folMs = new double[2, 5], folRecordMs = new double[2, 2];
    readonly long[] folSeen = new long[2];
    static readonly int FolSkip = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_FOLIAGE_TIMING_SKIP"), out int skip) ? skip : 160;
    readonly long[] folCalls = new long[2], folMeshDraws = new long[2], folGrassDraws = new long[2], folRocks = new long[2];

    void FolAccount(int k, double tCull, double tUpload, double tMeshes, double tGrass, double tEnd, int meshDraws, int grassDraws, int rocks, double recMeshes, double recGrass)
    {
        if (folSeen[k]++ < FolSkip) return;   // the first calls are cold (pipelines, streaming)
        folCalls[k]++;
        folMs[k, 0] += tCull; folMs[k, 1] += tUpload - tCull; folMs[k, 2] += tMeshes - tUpload; folMs[k, 3] += tGrass - tMeshes; folMs[k, 4] += tEnd - tGrass;
        folMeshDraws[k] += meshDraws; folGrassDraws[k] += grassDraws; folRocks[k] += rocks;
        folRecordMs[k, 0] += recMeshes; folRecordMs[k, 1] += recGrass;
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
                $"draws per call: meshes {(double)folMeshDraws[k] / folCalls[k]:F1}, grass {(double)folGrassDraws[k] / folCalls[k]:F1}, rock groups {(double)folRocks[k] / folCalls[k]:F1}; " +
                $"us per draw: meshes {perDraw(2, folMeshDraws[k]):F2}, grass {perDraw(3, folGrassDraws[k]):F2}; record only (native): meshes {(folMeshDraws[k] > 0 ? folRecordMs[k, 0] * 1000 / folMeshDraws[k] : 0):F2}, grass {(folGrassDraws[k] > 0 ? folRecordMs[k, 1] * 1000 / folGrassDraws[k] : 0):F2}"));
        }
    }

    // ---- culling (Draw step 1) ----


    /// <summary>A further cascade's share of the candidates: the visible ones as batch matrices, in runs of one mesh.</summary>
    sealed class CandidateOutput
    {
        public Matrix4x4[] Visible = new Matrix4x4[64];
        public int Count;
        public readonly List<(MeshAsset Asset, int Start, int Count)> Runs = [];
    }

    /// <summary>The groups a culling pass tests, in order (the batch order follows it), with their range for this view.</summary>
    readonly List<(Group Group, FoliageGroupRange Range)> cullWork = [];
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
        var frustum = cullView.Planes;
        if (record) { shadowCandidates.Clear(); shadowCandidateOrder.Clear(); (shadowCandidatesFrame, shadowCandidatesEye, shadowCandidatesRange) = (updates, eye, maxRange); }
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
                cullWork.Add((g, FoliageGroupRange.Of(range, band)));
            }
        }
        // The batch order: each mesh's first group in the work list (TERRAIN-mode rocks have no batch).
        foreach (var (g, _) in cullWork)
        {
            if (record) shadowCandidateOrder.Add(g.Asset);
            if (!(g.Asset.Terrain && options.Textures)) BatchOf(g.Asset);
        }
        while (cullOutputs.Count < cullWork.Count) cullOutputs.Add(new FoliageCullOutput());
        var eyeXz = new Vector2(eye.X, eye.Z);
        var view = cullView;
        RenderJobs.For(cullWork.Count, k =>
        {
            var (g, range) = cullWork[k];
            if (!g.SpheresReady)
            {
                FoliageCull.FillSpheres(g.Instances, g.Asset.Centre, g.Asset.Radius);   // the mesh's bounds are known once it is resident, and never change
                g.SpheresReady = true;
            }
            FoliageCull.CullGroup(g.Instances, range, eyeXz, view, record, cullOutputs[k]);
        });
        for (int k = 0; k < cullWork.Count; k++)
        {
            var output = cullOutputs[k];
            var g = cullWork[k].Group;
            if (record)
                for (int i = 0; i < output.InRangeCount; i++)
                    shadowCandidates.Add(new ShadowCandidate(g.Asset, g.Instances, output.InRange[i], output.InRangeFade[i]));
            EmitAll(g.Asset, output.Visible.AsSpan(0, output.Count), options);
        }
    }

    /// <summary>A further shadow cascade: the frame's recorded candidates against this cascade's frustum (tested in parallel, emitted in order).</summary>
    void CullCandidates(WorldRenderOptions options)
    {
        foreach (var a in shadowCandidateOrder)
            if (!(a.Terrain && options.Textures)) BatchOf(a);
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
                if (output.Runs.Count == 0 || output.Runs[^1].Asset != c.Asset) output.Runs.Add((c.Asset, output.Count, 0));
                if (output.Count == output.Visible.Length) Array.Resize(ref output.Visible, output.Count * 2);
                output.Visible[output.Count++] = FoliageCull.Packed(r.Transform, c.Weight);
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
    readonly List<CandidateOutput> candidateOutputs = [];

    /// <summary>The mesh's batch, put into <c>active</c> (emptied) by the first call of this <see cref="Draw"/>.</summary>
    Batch BatchOf(MeshAsset a)
    {
        if (!batches.TryGetValue(a, out var batch)) batches[a] = batch = new Batch { Asset = a };
        if (batch.Stamp != drawStamp)
        {
            batch.Stamp = drawStamp;
            batch.Count = 0;
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

    /// <summary>A mesh's native state for one program: the export it came from and the <see cref="IGlInterop.VertexArrayStamp"/> it was
    /// current at, its vertex layout and own vertex buffers, its indices, and the pipelines for the last two segment states (the reflection's
    /// multisampled target alternates with the scene's).</summary>
    struct NativeMesh
    {
        public VertexArrayBindings? Source;
        public long Stamp;
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

    /// <summary>
    /// A native program of the foliage with its vertex inputs from the reflection: the layout and buffers a GL vertex array feeds it, as VkGl
    /// feeds a GL program (<see cref="LegacyProgram.VertexLayout"/>: a disabled attribute reads GL's constant through a stride-0 binding).
    /// </summary>
    sealed class NativeProg : IDisposable
    {
        readonly GpuContext ctx;
        public readonly ShaderProgram P;
        readonly int[] locations;
        readonly Meitou.Rendering.Vulkan.Shaders.ScalarKind[] kinds;
        /// <summary>The program's own input locations (below the instance rows): 0 .. Own − 1.</summary>
        public readonly int Own;
        VertexLayout? last;

        public NativeProg(GpuContext ctx, NativeFrame frame, string vertex, string fragment, string name)
        {
            this.ctx = ctx;
            P = frame.Program(vertex, fragment, name);
            var inputs = P.VertexReflection!.Inputs;
            locations = [.. inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
            kinds = [.. inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
            Own = locations.Where(l => l < RowLocation).DefaultIfEmpty(-1).Max() + 1;
        }

        public VertexLayout Layout(ReadOnlySpan<LegacyProgram.Attribute?> byLocation)
        {
            Span<VertexInput> inputs = stackalloc VertexInput[locations.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                int loc = locations[i];
                inputs[i] = loc < byLocation.Length && byLocation[loc] is { } a
                    ? new VertexInput((uint)loc, a.Format, a.Stride, a.PerInstance)
                    : new VertexInput((uint)loc, GlConventions.DummyVertexFormat(kinds[i]), 0, false);
            }
            if (last is { } l && inputs.SequenceEqual(l.Inputs)) return l;
            return last = new VertexLayout(inputs.ToArray());
        }

        /// <summary>Locations 0 .. <paramref name="count"/> − 1 as one array for one bind: each attribute's buffer, GL's constant where none.</summary>
        public BufferBinding[] Buffers(ReadOnlySpan<LegacyProgram.Attribute?> byLocation, int count)
        {
            var result = new BufferBinding[count];
            for (int loc = 0; loc < count; loc++)
            {
                int input = Array.IndexOf(locations, loc);
                result[loc] = loc < byLocation.Length && byLocation[loc] is { } a
                    ? a.Buffer
                    : new BufferBinding(ctx.Defaults.DummyVertex.Buffer, GlConventions.DummyVertexOffset(input >= 0 ? kinds[input] : Meitou.Rendering.Vulkan.Shaders.ScalarKind.Float));
            }
            return result;
        }

        public void Dispose()
        {
            ctx.Pipelines.Forget(P);
            P.Dispose();
        }
    }

    /// <summary>A mesh's (or blade buffer's) native state for <paramref name="p"/>, current at <paramref name="stamp"/>: fetched again only when
    /// the stamp moved (<see cref="IGlInterop.VertexArrayStamp"/>), rebuilt only when the export changed. With <paramref name="rows"/> the batch
    /// matrices are four per-instance rows of 64 bytes at locations 7 to 10 (bound once per segment).</summary>
    static void Current(ref NativeMesh n, IGlInterop interop, uint vao, NativeProg p, long stamp, bool rows)
    {
        if (n.Stamp == stamp) return;
        var va = interop.VertexArray(vao);
        n.Stamp = stamp;
        if (ReferenceEquals(n.Source, va)) return;
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        va.Attributes.AsSpan().CopyTo(attributes);
        if (rows)
            for (int a = 0; a < 4; a++)
                attributes[RowLocation + a] = new LegacyProgram.Attribute(default, Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, InstanceStride, true);
        n.Layout = p.Layout(attributes);
        n.Vertices = p.Buffers(attributes, p.Own);
        n.Elements = va.Elements;
        n.Source = va;
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

    /// <summary>The bindless index of a GL texture VkGl owns (<see cref="IGlInterop.Bindless"/>): what <c>Sampled</c> would bind now, in the 2D
    /// float array the shaders index (0: GL's stand-in).</summary>
    static uint Index2D(IGlInterop interop, uint glTexture)
    {
        var h = interop.Bindless(glTexture);
        if (h.Kind != BindlessKind.Texture2D) throw new InvalidOperationException($"foliage texture {glTexture} is in the bindless {h.Kind} array, the shaders read textures2D");
        return h.Index;
    }

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
    readonly record struct MaterialKey(bool Swizzled, bool HasDiffuse, bool Triplanar, float TileX, float TileY, int AlphaSource, float AlphaThreshold, bool Emissive, float Specular);

    struct MeshDraw
    {
        public GpuPart Part;
        public uint FirstInstance, Instances;
        public uint Diffuse, Normal, Diffuse2, Normal2;
        public MaterialKey Material;
        /// <summary>1: uHasNormal, 2: uHasDual, 4: uUseVertexColour.</summary>
        public int PartBits;
        public bool DoubleSided;
    }

    /// <summary>The bindless indices of the segment being recorded, by GL texture id (an entry counts while its segment number is
    /// <see cref="textureSegment"/>): a texture's view or sampler may change between segments (mip streaming, the upscaler's bias), never
    /// inside one, so each texture is looked up once per segment (the grass alternates between a few sprites draw by draw).</summary>
    (uint Index, int Segment)[] textureIndices = new (uint, int)[1024];
    /// <summary>GL names (a counter shared by every GL object) beyond the array's cap of <see cref="TextureArrayCap"/> (2 MB).</summary>
    readonly Dictionary<uint, (uint Index, int Segment)> textureIndicesFar = [];
    const uint TextureArrayCap = 1 << 18;
    int textureSegment;

    /// <summary>A new segment: forget the indices of the last one.</summary>
    void NewTextureSegment()
    {
        textureSegment++;
        if (textureIndicesFar.Count > 4096) textureIndicesFar.Clear();   // only this segment's entries count anyway
    }

    uint Texture(IGlInterop interop, uint id)
    {
        if (id >= TextureArrayCap)
        {
            ref var far = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(textureIndicesFar, id, out bool exists);
            if (!exists || far.Segment != textureSegment) far = (Index2D(interop, id), textureSegment);
            return far.Index;
        }
        if (id < (uint)textureIndices.Length)
        {
            ref var e = ref textureIndices[id];
            if (e.Segment == textureSegment) return e.Index;
        }
        else Array.Resize(ref textureIndices, (int)Math.Min(Math.Max(id + 1, (uint)textureIndices.Length * 2), TextureArrayCap));
        uint index = Index2D(interop, id);
        textureIndices[id] = (index, textureSegment);
        return index;
    }

    readonly List<MeshDraw> meshDraws = [];

    static uint IdOf(WorldTexture? t) => t?.Id ?? 0;

    /// <summary>The batches' mesh parts as draws, in order, with the texture ids the GL code bound (reading an id counts as use: here, not in Record).</summary>
    void PrepareMeshes(WorldRenderOptions options)
    {
        meshDraws.Clear();
        foreach (var b in active)
        {
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
            cut ? m.AlphaThreshold : 0f, textured && normal && m.Emissive, textured ? m.Specular : 0.3f);
        foreach (var gp in mesh.Parts)
        {
            int bits = (normal && options.NormalMaps || cut ? 1 : 0) | (dual && gp.HasColours ? 2 : 0) | (gp.HasColours ? 4 : 0);
            meshDraws.Add(new MeshDraw
            {
                Part = gp, FirstInstance = (uint)b.Offset, Instances = (uint)b.Count, Diffuse = diffuse, Normal = normalId, Diffuse2 = diffuse2, Normal2 = normal2,
                Material = key, PartBits = bits, DoubleSided = m.DoubleSided,
            });
            DrawCalls++;
        }
    }

    /// <summary>The mesh draw list in one native segment (colour or depth), back faces culled unless the material is double-sided.</summary>
    void RecordMeshes(NativeProg mp, bool depth, Matrix4x4 viewProjection, Vector3 eye, Vector3 light, Vector3 fogColour, float fogDistance, bool coverage)
    {
        var p = mp.P;
        gl.Enable(EnableCap.CullFace);   // the state export reports the cull face's mode only while it is on
        var interop = Gpu.Interop!;
        string label = depth ? "foliage mesh depth" : "foliage meshes";
        var cmd = interop.BeginNativeInPass(label);
        var targets = interop.CurrentTargets();
        var state = interop.CurrentState();
        int segment = SegmentId(depth ? DepthKind : ColourKind, p, targets, state);
        long stamp = interop.VertexArrayStamp;
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance };
        nativeFrame.Bind(cmd, p.Layout, in view);
        Span<BufferBinding> rows = stackalloc BufferBinding[4];
        for (int a = 0; a < 4; a++) rows[a] = new BufferBinding(instances.Handle, instances.Offset + (ulong)(16 * a));
        cmd.BindVertexBuffers(RowLocation, rows);
        NewTextureSegment();
        // The constants the GL code set on every draw (Shaders.MeshFragment's uniforms the foliage does not vary).
        uint standIn = Texture(interop, 0);
        var pc = new MeshPush
        {
            Tint = Vector3.One, TriplanarScale = 1f / 5000, AlphaChannel = 3, GreyChannel = -1, HeadDiffuse = standIn, HeadNormal = standIn,
            Coverage = coverage ? 1u : 0u,
        };
        MeshPush last = default;
        bool pushed = false;
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
            pc.HasNormal = (d.PartBits & 1) != 0 ? 1u : 0u;
            pc.HasDual = (d.PartBits & 2) != 0 ? 1u : 0u;
            pc.UseVertexColour = (d.PartBits & 4) != 0 ? 1u : 0u;
            pc.Diffuse = Texture(interop, d.Diffuse);
            pc.Normal = Texture(interop, d.Normal);
            pc.Diffuse2 = Texture(interop, d.Diffuse2);
            pc.Normal2 = Texture(interop, d.Normal2);
            var want = d.DoubleSided ? sided : state.Cull;
            if (side != want) { cmd.SetRaster(want, state.Front); side = want; }
            var part = d.Part;
            ref var n = ref (depth ? ref part.DepthNative : ref part.ColourNative);
            Current(ref n, interop, part.Vao, mp, stamp, rows: true);
            cmd.BindPipeline(n.SegA == segment ? n.PipeA! : PipelineFor(ref n, mp, segment, state, targets.Formats, label));
            cmd.BindVertexBuffers(0, n.Vertices);
            cmd.BindIndexBuffer(n.Elements, Silk.NET.Vulkan.IndexType.Uint32);
            Push(cmd, p, in pc, ref last, ref pushed);
            cmd.DrawIndexed((uint)part.Count, d.Instances, 0, 0, d.FirstInstance);
        }
        interop.EndNative(cmd);
        gl.Enable(EnableCap.CullFace);   // as the GL version leaves it after the meshes
        gl.BindVertexArray(0);
    }

    // ---- grass ----

    struct GrassDraw
    {
        public GrassBuffer Buffer;
        public uint Vertices, Sprite, Colour;
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
            _ = b.Sprite?.Id;
            _ = b.ColourMap?.Id;
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
                if (b.Sprite is not { Id: not 0 } sprite) continue;
                var g = patch.Grass;
                bool hasColour = options.Textures && b.ColourMap is { Id: not 0 };
                grassDraws.Add(new GrassDraw
                {
                    Buffer = b, Vertices = g.CrossQuads ? 12u : 6u, Sprite = options.Textures ? sprite.Id : 0, Colour = hasColour ? b.ColourMap!.Id : 0,
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
    void RecordGrass(Matrix4x4 viewProjection, Vector3 eye, Vector3 light, Vector3 fogColour, float fogDistance)
    {
        var gp = grassProgram;
        var p = gp.P;
        gl.Disable(EnableCap.CullFace);
        var interop = Gpu.Interop!;
        var cmd = interop.BeginNativeInPass("foliage grass");
        var targets = interop.CurrentTargets();
        var state = interop.CurrentState();
        int segment = SegmentId(GrassKind, p, targets, state);
        long stamp = interop.VertexArrayStamp;
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance, Time = SwayPhase() };
        nativeFrame.Bind(cmd, p.Layout, in view);
        NewTextureSegment();
        GrassPush last = default;
        bool pushed = false;
        foreach (ref readonly var d in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(grassDraws))
        {
            var pc = d.Push;
            pc.Sprite = Texture(interop, d.Sprite);
            pc.ColourMap = Texture(interop, d.Colour);
            var buffer = d.Buffer;
            ref var n = ref buffer.GrassNative;
            Current(ref n, interop, buffer.Vao, gp, stamp, rows: false);
            cmd.BindPipeline(n.SegA == segment ? n.PipeA! : PipelineFor(ref n, gp, segment, state, targets.Formats, "foliage grass"));
            cmd.BindVertexBuffers(0, n.Vertices);
            Push(cmd, p, in pc, ref last, ref pushed);
            cmd.Draw(d.Vertices, (uint)buffer.Shown);
        }
        interop.EndNative(cmd);
        gl.BindVertexArray(0);
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
        // Prepare
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
                    if (b.Sprite is not { Id: not 0 } sprite) continue;
                    motionDraws.Add(new MotionDraw
                    {
                        Buffer = b, Vertices = g.CrossQuads ? 12u : 6u, Sprite = sprite.Id,
                        Push = new GrassPush
                        {
                            Size = new Vector4(g.QuadMinWidth, g.QuadMaxWidth, g.QuadMinHeight, g.QuadMaxHeight), Sway = g.SwayLength, Range = range, Frequency = 2f,
                        },
                    });
                }
            }
        if (motionDraws.Count == 0) return;
        // Record
        var gp = grassMotionProgram;
        var p = gp.P;
        gl.Disable(EnableCap.CullFace);
        var interop = Gpu.Interop!;
        var cmd = interop.BeginNativeInPass("foliage grass motion");
        var pass = interop.CurrentTargets();
        var drawState = interop.CurrentState();   // the host's colour mask (red and green), no depth test, no blending
        int segment = SegmentId(MotionKind, p, pass, drawState);
        long stamp = interop.VertexArrayStamp;
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
        uint nearDepth = Index2D(interop, targets.NearDepth);
        GrassPush last = default;
        bool pushed = false;
        foreach (ref readonly var d in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(motionDraws))
        {
            var pc = d.Push;
            pc.Sprite = Texture(interop, d.Sprite);
            pc.NearDepth = nearDepth;
            var buffer = d.Buffer;
            ref var n = ref buffer.MotionNative;
            Current(ref n, interop, buffer.Vao, gp, stamp, rows: false);
            cmd.BindPipeline(n.SegA == segment ? n.PipeA! : PipelineFor(ref n, gp, segment, drawState, pass.Formats, "foliage grass motion"));
            cmd.BindVertexBuffers(0, n.Vertices);
            Push(cmd, p, in pc, ref last, ref pushed);
            cmd.Draw(d.Vertices, (uint)buffer.Shown);
        }
        interop.EndNative(cmd);
        gl.BindVertexArray(0);
        Bind(1, 0);
        gl.ActiveTexture(TextureUnit.Texture0);
    }

    void Bind(int unit, uint texture)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, texture);
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


    public void Dispose()
    {
        ReportFoliageTiming();
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
        colourMesh.Dispose();
        depthMesh.Dispose();
        grassProgram.Dispose();
        grassMotionProgram.Dispose();
        nativeFrame.Dispose();
        foreach (var q in timers) { gl.DeleteQuery(q.Start); gl.DeleteQuery(q.End); }
    }
}
