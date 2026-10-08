using System.Diagnostics;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;
using SamplerInfo = Meitou.Rendering.Gpu.Shaders.SamplerInfo;

namespace Meitou.Rendering;

/// <summary>
/// Draws the world's placed objects (docs/viewer.md, "Objects"): buildings, map features and distant towns.
/// <list type="bullet">
/// <item>Zones around the camera are laid out on worker threads (<see cref="ObjectStreamer"/>); meshes decode on workers and upload in
/// steps (<see cref="ObjectMeshCache"/>); textures decode on workers (<see cref="WorldTextureCache"/>). <see cref="Update"/> drives it all.</item>
/// <item>Each instance picks a mesh LOD level by Kenshi's rule (<see cref="MeshLod"/>) and fades between levels and at the draw
/// distance with a dithered cross-fade; instances of one (mesh, material set, level) are drawn with one instanced call per part.</item>
/// <item>Towns with a baked distant mesh, and buildings' own distant meshes elsewhere, take over beyond <see cref="ObjectDistance"/>.</item>
/// <item>TERRAIN-mode map features go through the terrain shader (<see cref="TerrainRenderer.DrawMeshes"/>), one draw each, at their LOD level.</item>
/// </list>
/// <see cref="Draw"/> can be called with any camera (e.g. the water reflection's) between <see cref="Update"/> calls.
/// </summary>
public sealed unsafe class WorldObjectRenderer : IDisposable
{
    /// <summary>The native GPU API (docs/renderer-native.md 7.1 step 8; since phase 8 stage 1 the only one this renderer uses).</summary>
    public GpuContext Gpu { get; }
    readonly NativeFrame nativeFrame;
    readonly ObjProg colourProg, depthProg;   // the colour and the depth (shadow caster) programs, native
    readonly WorldTextureCache textureCache;
    readonly MaterialResolver resolver;
    readonly UploadQueue uploads = new();
    readonly ObjectMeshCache meshes;
    readonly ObjectStreamer streamer;
    readonly WorldObjects objects;
    readonly Dictionary<(string, string, string), ObjectMaterialSet> materialSets = [];
    readonly List<TownDraw> towns = [];
    readonly ObjectMaterialSet distantMaterial;
    readonly Dictionary<(GpuObjectMesh, ObjectMaterialSet, int, bool), Batch> batchMap = [];
    readonly List<Batch> active = [];
    readonly List<(MeshBindings, int, Matrix4x4)> terrainMeshes = [];   // the TERRAIN-mode instances of a draw (TerrainRenderer.DrawMeshes)
    /// <summary>MEITOU_LOD_DEBUG: 1 colours solid surfaces by LOD level, 2 draws wireframe only coloured by level (green 0, yellow 1, orange 2, red 3, magenta manual, blue distant stand-ins).</summary>
    readonly int debugLevels = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_LOD_DEBUG"), out int dl) ? dl : 0;

    /// <summary>One batch of instances: the same (mesh, materials, LOD level), drawn with one call per part.</summary>
    sealed class Batch
    {
        public required GpuObjectMesh Mesh;
        public required ObjectMaterialSet Materials;
        public required int Level;
        public required bool Town;
        public bool Manual;
        public Matrix4x4[] Data = new Matrix4x4[16];
        public int Count, Offset;

        public void Add(in Matrix4x4 m)
        {
            if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
            Data[Count++] = m;
        }
    }

    sealed class TownDraw
    {
        public required DistantTown Town;
        public required ObjectMesh Mesh;
        public Vector3 Centre;
        public float Radius = 2500;
        public bool Resolved;
    }

    /// <summary>The landmarks being collected (all zones laid out once on a worker, about a second), the class that tells them, and what they cost.</summary>
    Task<List<(PlacedMesh Placed, float Radius)>>? landmarkTask;
    readonly LandmarkClass? landmarkClass;
    double landmarkMs;

    /// <param name="landmarks">Collect the world's landmarks (<see cref="LandmarkClass"/>) and keep them apart from the zones, to be drawn to <see cref="LandmarkDistance"/>.
    /// Without it (Faithful) every placement stays in its zone and the object distance rules all.</param>
    internal WorldObjectRenderer(GpuContext gpu, AssetLocator assets, WorldObjects objects, bool landmarks = false)
    {
        Gpu = gpu;
        this.objects = objects;
        // Both native programs are made here with every handle resolved, never inside a draw (the GL programs they replace are not made).
        nativeFrame = new NativeFrame(gpu);
        colourProg = new ObjProg(gpu, nativeFrame, BuildingLodShaders.VertexNative(), BuildingLodShaders.FragmentNative(), "objects");
        depthProg = new ObjProg(gpu, nativeFrame, BuildingLodShaders.VertexNative(), BuildingLodShaders.DepthNative(), "objects depth");
        textureCache = new WorldTextureCache(gpu, assets, "object textures");
        var library = OgreMaterialLibrary.LoadConfigured(objects.Install, out _);
        resolver = new MaterialResolver(objects.Database, library, assets);
        meshes = new ObjectMeshCache(gpu, assets, uploads);
        meshes.Unloaded = gpu => { unloadedMeshes.Add(gpu); RemoveBatches(gpu); };
        streamer = new ObjectStreamer(objects, meshes);
        if (landmarks)
        {
            landmarkClass = new LandmarkClass(assets);
            streamer.WaitForLandmarks = true;
            var collectWatch = Stopwatch.StartNew();
            landmarkTask = Task.Run(() => { var found = landmarkClass.Collect(objects); landmarkMs = collectWatch.Elapsed.TotalMilliseconds; return found; });
        }
        var distant = new SurfaceMaterial { Description = "DistantTown (vertex colour x texture x 1.5)", Diffuse = DistantTowns.DiffuseTexture, VertexColours = true, SpecularMult = 0 };
        var distantDiffuse = textureCache.Get(distant.Diffuse, false);
        if (distantDiffuse is not null) distantDiffuse.KeepAllMips = true;   // its coordinates are the towns' own, not the mesh's: mip streaming leaves it
        distantMaterial = new ObjectMaterialSet([new ObjectPartMaterial(distant, distantDiffuse, null, null, null)]);
        foreach (var t in objects.DistantTowns)
            towns.Add(new TownDraw { Town = t, Mesh = meshes.Get(t.MeshPath, distant: true), Centre = t.Position });
    }

    /// <summary>Interactive use (non-zero): bounded upload work per frame. 0 (screenshots): no limit.</summary>
    public int LoadBudget { get; set; }

    /// <summary>Instances drawn by the last <see cref="Draw"/>.</summary>
    public int DrawnInstances { get; private set; }
    public long DrawnTriangles { get; private set; }
    public int DrawCalls { get; private set; }
    /// <summary>Real instances the last <see cref="Draw"/> left out because of their part distance (<see cref="ObjectRanges.PartRenderingDistance"/>)
    /// although within the object distance (in or out of view; a benchmark statistic).</summary>
    public int PartLimited { get; private set; }

    /// <summary>Sums over every <see cref="Draw"/> since the start, [0, ...] colour (scene slices and reflection), [1, ...] depth (shadow cascades):
    /// instances, triangles, draw calls, part-limited instances, calls of Draw (a benchmark statistic; the caller divides by its frames).</summary>
    public readonly long[,] Totals = new long[2, 5];

    /// <summary><c>MEITOU_OBJECT_PART_RANGE=&lt;u&gt;</c>: the game's <c>objects view range</c> the small building parts stop at (default 3000,
    /// <see cref="ObjectRanges.ObjectsViewRange"/>); for benchmarks of longer object ranges (docs/render-distance-benchmark.md).</summary>
    static readonly float PartViewRange = float.TryParse(Environment.GetEnvironmentVariable("MEITOU_OBJECT_PART_RANGE"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out float partRange) && partRange > 0 ? partRange : ObjectRanges.ObjectsViewRange;

    /// <summary>A benchmark statistic: the resolved real instances' bounding radii (world units, scaled) as percentiles, and how many have the
    /// part distance of <see cref="ObjectRanges.ObjectsViewRange"/> (small building parts) rather than none.</summary>
    public string SizeSurvey()
    {
        var radii = streamer.AllZones.SelectMany(z => z.Real).Where(i => i.Gpu is not null).Select(i => i.Radius).Order().ToArray();
        if (radii.Length == 0) return "no resolved instances";
        int limited = streamer.AllZones.SelectMany(z => z.Real).Count(i => i.Gpu is not null && i.Limit < float.MaxValue / 2);
        float P(double q) => radii[Math.Min((int)(radii.Length * q), radii.Length - 1)];
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{radii.Length} resolved real instances, radius p10 {P(0.1):0} / p25 {P(0.25):0} / p50 {P(0.5):0} / p75 {P(0.75):0} / p90 {P(0.9):0} / max {radii[^1]:0}; {limited} with a part distance (<= {PartViewRange:0} units)");
    }
    public double LastDrawCpuMs { get; private set; }
    public double LastUpdateMs { get; private set; }

    /// <summary>
    /// Real (full detail) objects are drawn up to this distance (<see cref="MeshLod.Value"/>-like: camera to bounds centre minus radius);
    /// buildings' parts have their own limits too (<see cref="ObjectRanges.PartRenderingDistance"/>). Beyond it distant towns take over.
    /// </summary>
    public float ObjectDistance { get; set; } = 12000;

    /// <summary>Landmarks (<see cref="LandmarkClass"/>) are drawn up to this distance instead of <see cref="ObjectDistance"/> (0: none, they use the object distance).</summary>
    public float LandmarkDistance { get; set; }

    /// <summary>Ogre's camera LOD bias as a distance factor: the LOD value is multiplied by it, so above 1 coarser levels come sooner, below 1 later.</summary>
    public float LodBias { get; set; } = 1;

    /// <summary>Distant towns and buildings' distant meshes are drawn up to this distance (the game: its <c>distant town range</c> in zones).</summary>
    public float DistantRange { get; set; } = ObjectRanges.MaxDistantTownRangeZones * WorldLayout.ZoneSize;

    /// <summary>The memory-pressure guard (<see cref="VramGuard"/>): its range scale shortens <see cref="ObjectDistance"/> and <see cref="DistantRange"/>
    /// while the video memory is nearly used up, and the caches stop loading and evict. Null: no clamp.</summary>
    public VramGuard? Guard
    {
        get => guard;
        set { guard = value; textureCache.Guard = value; meshes.Guard = value; }
    }
    VramGuard? guard;
    /// <summary>The ranges as drawn and streamed: the settings times the guard's scale (exactly the settings while it is 1).</summary>
    float RealRange => ObjectDistance * (guard?.RangeScale ?? 1f);
    /// <summary>How far a landmark is drawn and streamed: <see cref="LandmarkDistance"/> times the guard's scale, never less than <see cref="RealRange"/> (0: the object distance).</summary>
    float LandmarkReach => Math.Max(RealRange, LandmarkDistance * (guard?.RangeScale ?? 1f));
    /// <summary>Whether the landmarks are kept apart from the zones (the renderer was made with them).</summary>
    public bool HasLandmarks => landmarkClass is not null;
    float DistantReach => DistantRange * (guard?.RangeScale ?? 1f);

    /// <summary>Draw nothing but the real objects (no distant meshes): the game with distant towns off.</summary>
    public bool NoDistant { get; set; }

    /// <summary>Work in flight: zones being laid out, meshes decoding or uploading, textures decoding.</summary>
    public int Pending => streamer.Pending + meshes.Pending + uploads.Count + textureCache.PendingCount + unresolvedInRange + (landmarkTask is not null ? 1 : 0);

    int unresolvedInRange;
    public List<string> Messages { get; } = [];

    /// <summary>A line about what is loaded, for the log.</summary>
    /// <summary>GPU memory held by streamed meshes and textures, for the stats line and the window title.</summary>
    public long ResidentBytes => meshes.Bytes + textureCache.ResidentBytes;
    public string ResidentDescription => $"{meshes.Bytes / 1048576.0:0} MB in {meshes.Resident} meshes ({meshes.Unloads} unloaded, {meshes.Reloads} reloaded, {meshes.Refined} remade finer, {meshes.Coarsened} coarser), {textureCache.Describe()}";
    /// <summary><c>MEITOU_LANDMARK_LOG=1</c>: one line per resolved landmark, nearest first: its distance, LOD levels, the finest level the mesh holds, its size.</summary>
    string LandmarkDetail() =>
        Environment.GetEnvironmentVariable("MEITOU_LANDMARK_LOG") == "1" && streamer.LandmarkZone is { } zone
            ? "\n" + string.Join("\n", zone.Real.Where(i => i.Gpu is not null).OrderBy(i => Vector3.Distance(eyeNow, i.Centre)).Select(i => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"landmark  {Path.GetFileName(i.Mesh.Key),-40} {Vector3.Distance(eyeNow, i.Centre),8:0} away, radius {i.Radius,6:0}, {i.Gpu!.LevelCount} levels, finest held {i.Gpu.MinLevel}, {i.Mesh.Bytes / 1024} KB")))
            : "";

    public string Describe() =>
        $"{streamer.Loaded} zones, {streamer.Instances:N0} instances ({streamer.Resolved:N0} resolved{(streamer.LandmarkZone is { } lz ? $", of them {lz.Real.Count(i => i.Gpu is not null)} of {lz.Real.Count} landmarks" : "")}), {meshes.Resident}/{meshes.Total} meshes requested or resident, resident: {ResidentDescription}, " +
        $"{towns.Count(t => t.Mesh.Status == ObjectMesh.State.Resident)}/{towns.Count} distant towns; {objects.Describe()}{LandmarkDetail()}";

    // ------------------------------------------------------------------ streaming

    /// <summary>Render-thread time per frame spent on uploads and resolving.</summary>
    public double BudgetMs { get; set; } = 2;

    /// <summary>Follows the eye: lays out zones and loads meshes and textures around it, within <see cref="BudgetMs"/> of render-thread time.</summary>
    public void Update(Vector3 eye) => Update(eye, BudgetMs);

    static readonly bool Log = Environment.GetEnvironmentVariable("MEITOU_STREAM_LOG") == "1";

    /// <summary>Colour draws of parts whose diffuse map was not resident (drawn grey) and, at the same moment, textures waiting for a finer image,
    /// the largest of each since <see cref="TakePopStats"/> (the benchmark's pop-in lines).</summary>
    int untexturedDraws, untexturedMax, heldNear, coarseDraws, coarseMax;
    /// <summary>Meshes drawn in a reduced form while the full one loads (<see cref="ObjectMeshCache"/>).</summary>
    public int MeshesAwaitingDetail => meshes.Upgrading;
    public (int Untextured, int Refining, int Held, int Coarse) TakePopStats()
    {
        var result = (Math.Max(untexturedMax, untexturedDraws), textureCache.Refining, heldNear, Math.Max(coarseMax, coarseDraws));
        untexturedDraws = untexturedMax = heldNear = coarseDraws = coarseMax = 0;
        return result;
    }

    /// <summary>Tells the object textures' mip streaming (<see cref="MipStreaming"/>) the camera: the render size in pixels and the vertical field of view.</summary>
    public void SetView(int width, int height, float fieldOfView) => SetView(width, height, fieldOfView, Gpu.LodBias);
    public void SetView(int width, int height, float fieldOfView, float lodBias) => textureCache.Mips.SetView(width, height, fieldOfView, lodBias);

    /// <summary>The eye of the last <see cref="Update"/>, and how far ahead of it (distance) the textures are kept ready for (the camera's speed over half a second).</summary>
    Vector3 eyeNow;
    float needMargin;
    /// <summary>How far ahead a mesh is kept ready for: three times <see cref="needMargin"/> (the camera's speed over 1.5 s).</summary>
    float MeshMargin => needMargin * 3;

    void Update(Vector3 eye, double budgetMs)
    {
        var watch = Stopwatch.StartNew();
        eyeNow = eye;
        meshes.LodBias = LodBias;
        meshes.LookAhead = MeshMargin;
        bool unlimited = budgetMs > 1e8;
        float streamRange = (NoDistant ? RealRange : Math.Max(RealRange, DistantReach)) + WorldLayout.ZoneSize * 0.5f;
        streamer.Paused = guard is { Streaming: false };
        if (landmarkTask is { IsCompleted: true } collected)
        {
            landmarkTask = null;
            if (collected.IsCompletedSuccessfully)
            {
                streamer.Landmarks = landmarkClass;
                streamer.SetLandmarks(collected.Result);
                Console.WriteLine($"landmarks {collected.Result.Count} placements of radius {LandmarkClass.MinRadius:0}+ kept apart from the zones ({landmarkClass!.Probes} mesh bounds read, {landmarkClass.WholeReads} of them whole files; {landmarkMs:0} ms on the workers)");
            }
            else
            {
                streamer.WaitForLandmarks = false;   // the zones keep them: drawn to the object distance
                Console.WriteLine($"warning   landmarks: {collected.Exception?.GetBaseException().Message}");
            }
        }
        streamer.Update(eye, streamRange, unlimited ? 64 : 1);
        double tZones = watch.Elapsed.TotalMilliseconds;
        ResolveTowns(eye);
        unresolvedInRange = streamer.Scan(eye, RealRange, DistantReach, NoDistant, Resolve, unlimited ? int.MaxValue : 40);
        unresolvedInRange += streamer.ScanLandmarks(eye, LandmarkReach, Resolve, unlimited ? int.MaxValue : 10);
        double tScan = watch.Elapsed.TotalMilliseconds;
        meshes.Pump();
        MarkInRange(eye, force: unlimited);
        meshes.Trim();
        if (unloadedMeshes.Count > 0)
        {
            // Instances that resolved to a mesh just unloaded wait for it again (Scan asks for it once they are in range).
            foreach (var zone in streamer.AllZones)
                foreach (var inst in zone.Real.Concat(zone.Stand))
                    if (inst.Gpu is { } resolved && unloadedMeshes.Contains(resolved)) Unresolve(zone, inst);
            if (streamer.LandmarkZone is { } landmarkZone)
                foreach (var inst in landmarkZone.Real)
                    if (inst.Gpu is { } resolved && unloadedMeshes.Contains(resolved)) Unresolve(landmarkZone, inst);
            unloadedMeshes.Clear();
        }
        textureCache.Pump(wait: unlimited, max: unlimited ? 16 : 8, budgetMs: Math.Max(budgetMs * 0.5, 0.5));
        lock (textureCache.Messages)
        {
            foreach (var m in Messages.Concat(meshes.Messages).Concat(textureCache.Messages).Take(30)) Console.WriteLine($"warning   {m}");
            Messages.Clear();
            meshes.Messages.Clear();
            textureCache.Messages.Clear();
        }
        uploads.Run(budgetMs);
        LastUpdateMs = watch.Elapsed.TotalMilliseconds;
        if (Log && !unlimited && LastUpdateMs > 6)
            Console.WriteLine($"slow objects update {LastUpdateMs:0.0} ms: zones {tZones:0.0}, scan {tScan - tZones:0.0}, uploads [{uploads.LastRun}]");
    }

    readonly HashSet<GpuObjectMesh> unloadedMeshes = [];
    readonly List<ObjectMaterialSet> markedSets = [];
    readonly List<ObjectMesh> markedMeshes = [];
    Vector3 lastMarkEye;
    long lastMarkTime;
    long lastMark;
    int markStamp;

    /// <summary>
    /// Once a second, before <see cref="ObjectMeshCache.Trim"/>: everything within its draw range counts as in use, whether or not the camera looks at
    /// it (instances reading the mesh, materials the textures), so only what is out of range gets unloaded, after the idle time.
    /// </summary>
    void MarkInRange(Vector3 eye, bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now - lastMark < 1000) return;
        // How far the camera goes in the half second a finer texture takes to arrive: nearer than that counts as nearer.
        if (!force && lastMarkTime != 0) needMargin = Math.Min(Vector3.Distance(eye, lastMarkEye) / Math.Max((now - lastMarkTime) / 1000f, 0.05f) * 0.5f, 30000f);
        else if (force) needMargin = 0;
        (lastMarkEye, lastMarkTime) = (eye, now);
        lastMark = now;
        markStamp++;
        markedSets.Clear();
        markedMeshes.Clear();
        void Mark(ObjectStreamer.Instance inst, float range)
        {
            if (inst.Gpu is null) return;
            float distance = Vector3.Distance(eye, inst.Centre), value = distance - inst.Radius, limit = Math.Min(range, inst.Limit);
            if (value >= limit)
            {
                // Not drawn, but still within what Scan asks meshes for: kept, or it would be unloaded and asked for again.
                if (distance - ObjectStreamer.RequestMargin <= limit) inst.Mesh.LastUsed = now;
                return;
            }
            inst.Mesh.LastUsed = now;
            if (inst.Materials is not { } set) return;
            if (set.Stamp != markStamp) { set.Stamp = markStamp; set.Touch(); }
            if (inst.Stand) return;
            if (set.NeedStamp != markStamp) { set.NeedStamp = markStamp; set.Near = set.NearScaled = float.PositiveInfinity; markedSets.Add(set); }
            float near = Math.Max(value - needMargin, 0);
            if (near < set.Near) set.Near = near;
            // A mesh is kept ready for where the camera will be by the next pass and the remake (1.5 s of its speed), a texture for half a second (its two levels of margin do the rest).
            float meshNear = Math.Max(value - MeshMargin, 0);
            var mesh = inst.Mesh;
            if (mesh.NearStamp != markStamp) { mesh.NearStamp = markStamp; mesh.NearPass = meshNear; markedMeshes.Add(mesh); }
            else if (meshNear < mesh.NearPass) mesh.NearPass = meshNear;
            float scaled = near / Math.Max(inst.Radius / Math.Max(inst.Gpu.Radius, 1e-6f), 1e-6f);
            if (scaled < set.NearScaled) set.NearScaled = scaled;
        }
        foreach (var zone in streamer.AllZones)
        {
            foreach (var inst in zone.Real) Mark(inst, RealRange);
            if (!NoDistant) foreach (var inst in zone.Stand) Mark(inst, DistantReach);
        }
        if (streamer.LandmarkZone is { } landmarkZone)
        {
            float reach = LandmarkReach;
            foreach (var inst in landmarkZone.Real) Mark(inst, reach);
        }
        // Mip streaming: each texture's nearest user, over how large a texel is there.
        foreach (var set in markedSets)
            foreach (var part in set.Parts)
                part.Offer(part.Triplanar ? set.Near : set.NearScaled);
        textureCache.CommitNeeds();
        textureCache.Rebalance();
        // Distance-based streaming of the finest LOD level: each mesh in range, remade when its nearest user needs finer levels (or, after a while, no longer needs them).
        foreach (var mesh in markedMeshes) meshes.Rebalance(mesh, mesh.NearPass);
        if (NoDistant) return;
        foreach (var t in towns)
            if (Vector3.Distance(eye, t.Centre) - t.Radius < DistantReach) t.Mesh.LastUsed = now;
        _ = distantMaterial.Parts[0].Diffuse;
    }

    /// <summary>A mesh was unloaded under an instance that had resolved to it: the instance waits for it again (<see cref="ObjectStreamer.Scan"/> asks for it once in range).</summary>
    static void Unresolve(ObjectStreamer.Zone zone, ObjectStreamer.Instance inst)
    {
        inst.Gpu = null;
        zone.Unresolved.Add(inst);
    }

    /// <summary>Forgets the batches of an unloaded mesh (their key would keep the deleted buffers' wrapper alive).</summary>
    void RemoveBatches(GpuObjectMesh gpu)
    {
        List<(GpuObjectMesh, ObjectMaterialSet, int, bool)>? keys = null;
        foreach (var key in batchMap.Keys)
            if (ReferenceEquals(key.Item1, gpu)) (keys ??= []).Add(key);
        if (keys is not null) foreach (var key in keys) batchMap.Remove(key);
    }

    void ResolveTowns(Vector3 eye)
    {
        if (NoDistant) return;
        foreach (var t in towns)
        {
            if (!t.Resolved && t.Mesh.Status == ObjectMesh.State.Resident)
            {
                var g = t.Mesh.Gpu!;
                t.Centre = t.Town.Position + g.Centre;
                t.Radius = g.Radius;
                t.Resolved = true;
            }
            float d = Vector3.Distance(eye, t.Centre);
            if (t.Mesh.Status == ObjectMesh.State.None && d - t.Radius < DistantReach) meshes.Request(t.Mesh, d);
        }
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
            Update(eye, 1e9);
            if (++rounds > 2 && Pending == 0) return;
            // Under memory pressure the guard holds the loading back: what is missing will not come until it ends, so do not wait for it.
            pressureSince = guard is { Pressure: true } ? pressureSince ?? watch.Elapsed.TotalSeconds : null;
            if (pressureSince is { } since && watch.Elapsed.TotalSeconds - since > 5) { Console.WriteLine("warning   object streaming stopped waiting: memory pressure (VramGuard)"); return; }
            Thread.Sleep(1);
        }
        Console.WriteLine($"warning   object streaming did not finish in {timeoutMs / 1000} s");
    }

    // ------------------------------------------------------------------ materials

    /// <summary>Makes the instance drawable once its mesh is resident: bounds, draw distance, material set. False when the mesh holds only coarser
    /// levels than this instance can need (it was loaded for farther ones): it is asked to be remade and the instance waits.</summary>
    bool Resolve(ObjectStreamer.Instance inst)
    {
        var gpu = inst.Mesh.Gpu!;
        var t = inst.Placed.Transform;
        var centre = Vector3.Transform(gpu.Centre, t);
        float scale = MathF.Sqrt(Math.Max(new Vector3(t.M11, t.M12, t.M13).LengthSquared(), Math.Max(new Vector3(t.M21, t.M22, t.M23).LengthSquared(), new Vector3(t.M31, t.M32, t.M33).LengthSquared())));
        float radius = gpu.Radius * scale;
        if (!inst.Stand && gpu.MinLevel > 0 && inst.TerrainMode)
        {
            meshes.Retarget(inst.Mesh, 0);   // the terrain shader wants every level
            return false;
        }
        if (!inst.Stand && gpu.MinLevel > 0)
        {
            float value = Math.Max(Vector3.Distance(eyeNow, centre) - radius - MeshMargin, 0);
            if (ObjectMeshCache.LevelFor(gpu.Distances, value * LodBias) < gpu.MinLevel)
            {
                if (value < 3000) heldNear++;   // an instance that close waiting for its mesh to be remade would be seen (the benchmark counts it)
                meshes.Retarget(inst.Mesh, value * 0.7f);
                return false;
            }
        }
        inst.Gpu = gpu;
        inst.Centre = centre;
        inst.Radius = radius;
        inst.Limit = inst.Placed.Kind == PlacedKind.BuildingPart
            ? ObjectRanges.PartRenderingDistance(gpu.Radius, inst.Placed.Owner.GetInt("function"), PartViewRange)
            : float.MaxValue;
        if (inst.Stand) { inst.Materials = distantMaterial; return true; }
        // A part's look comes from the material the layout chose (which differs per town), else from the resolver's candidates.
        var key = inst.Placed.MeshPath;
        var mkey = inst.Placed.Material is { } spec
            ? (key.ToLowerInvariant(), "spec", spec.StringId)
            : (key.ToLowerInvariant(), inst.Placed.Source.StringId, inst.Placed.Owner.StringId);
        bool made = !materialSets.TryGetValue(mkey, out var set);
        if (made) materialSets[mkey] = set = MakeMaterials(inst.Placed, gpu);
        inst.Materials = set!;
        // Mip streaming: this instance's distance is a use of the textures from now on (before the pass that would find it), and a new set's
        // textures are loaded now, knowing it (they were made without loading: deferred).
        float near = Math.Max(Vector3.Distance(eyeNow, inst.Centre) - inst.Radius - needMargin, 0);
        foreach (var part in set!.Parts) part.OfferNow(part.Triplanar ? near : near / Math.Max(scale, 1e-6f));
        if (made) set.Touch();
        return true;
    }

    /// <summary>One material per part: the resolver's candidate that names the placing record (or its building) best.</summary>
    ObjectMaterialSet MakeMaterials(PlacedMesh placed, GpuObjectMesh gpu)
    {
        var result = new ObjectPartMaterial[gpu.Parts.Count];
        string source = $"'{placed.Source.Name}'", owner = $"'{placed.Owner.Name}'";
        var chosen = placed.Material is { } spec ? BuildingMaterial.FromSpec(spec) : null;
        for (int i = 0; i < gpu.Parts.Count; i++)
        {
            var m = chosen ?? resolver.Candidates(placed.MeshPath, new OgreSubMesh { MaterialName = gpu.Parts[i].MaterialName })
                .OrderByDescending(c => (c.Description.Contains(owner, StringComparison.Ordinal) ? 2 : 0) + (c.Description.Contains(source, StringComparison.Ordinal) ? 1 : 0))
                .FirstOrDefault();
            bool border = m?.BorderAddressing ?? false;
            // A map nothing can read is not loaded (<see cref="Material"/> never reads it): the normal map only with tangents (or for the
            // emissive flag, which looks at its key), the second set only with vertex colours (its blend weight).
            var part = gpu.Parts[i];
            bool wantNormal = part.HasTangents || (m?.Emissive ?? false);
            bool wantSecond = part.HasColours;
            // The textures are made without loading (<c>deferred</c>): <see cref="Resolve"/> tells them how near they are used, then reads their keys.
            var tile = m?.Tile ?? Vector2.One;
            float minTile = Math.Min(tile.X, tile.Y);
            bool triplanar = m?.Triplanar ?? false;
            float texel = minTile > 0 && !triplanar ? part.UvScale / minTile : float.PositiveInfinity;   // triplanar: a surface edge-on to a projection axis has a footprint smaller than a pixel on that axis, so no bound
            result[i] = new ObjectPartMaterial(m, textureCache.Get(m?.Diffuse, border, deferred: true), wantNormal ? textureCache.Get(m?.Normal, border, deferred: true) : null,
                wantSecond ? textureCache.Get(m?.Diffuse2, border, deferred: true) : null, wantSecond && part.HasTangents ? textureCache.Get(m?.Normal2, border, deferred: true) : null,
                texel, triplanar) { Dust = placed.Kind == PlacedKind.BuildingPart };
        }
        return new ObjectMaterialSet(result);
    }

    // ------------------------------------------------------------------ drawing

    static Vector3 LevelColour(int level, bool manual, bool stand) =>
        stand ? new Vector3(0.2f, 0.6f, 1f) : manual ? new Vector3(0.9f, 0.2f, 0.9f) : level switch
        {
            0 => new Vector3(0.1f, 0.85f, 0.2f),
            1 => new Vector3(0.95f, 0.9f, 0.1f),
            2 => new Vector3(0.95f, 0.5f, 0.1f),
            _ => new Vector3(0.95f, 0.15f, 0.1f),
        };

    /// <summary>Draws the objects seen from <paramref name="eye"/> through <paramref name="frustum"/>.</summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, Vector3 light, Vector3 fogColour, float fogDistance, TerrainRenderer terrain)
    {
        var cpu = Stopwatch.StartNew();
        long now = Environment.TickCount64;
        float real = RealRange;
        DrawnInstances = 0;
        PartLimited = 0;
        untexturedMax = Math.Max(untexturedMax, untexturedDraws);
        untexturedDraws = 0;
        coarseMax = Math.Max(coarseMax, coarseDraws);
        coarseDraws = 0;
        DrawnTriangles = 0;
        DrawCalls = 0;
        callLoopMs = 0;
        foreach (var b in batchMap.Values) b.Count = 0;
        active.Clear();
        terrainMeshes.Clear();

        // 1. Cull and choose levels: every instance into the batch of its (mesh, materials, level) with its dither range.
        float realBand = Math.Clamp(real * 0.1f, 50, 1500);
        float distantBand = Math.Clamp(DistantReach * 0.15f, 500, 4000);
        foreach (var zone in landmarksOnly ? [] : streamer.ZonesNear(eye, Math.Max(real, NoDistant ? 0 : DistantReach), frustum))
        {
            if (ObjectStreamer.ZoneDistance(zone.X0, zone.Z0, eye) <= real)
                foreach (var inst in zone.Real) DrawReal(zone, inst, real, eye, frustum, options, now);
            if (NoDistant) continue;
            foreach (var inst in zone.Stand)
            {
                if (inst.Gpu is not { } gpu) continue;
                if (!ReferenceEquals(inst.Mesh.Gpu, gpu)) { Unresolve(zone, inst); continue; }
                float value = Vector3.Distance(inst.Centre, eye) - inst.Radius;
                if (value >= DistantReach || !SphereVisible(frustum, inst.Centre, inst.Radius)) continue;
                float w = ObjectRanges.RiseWeight(value, real - realBand, realBand) * ObjectRanges.EdgeWeight(value, DistantReach, distantBand);
                if (w <= 0) continue;
                inst.Mesh.LastUsed = now;
                DrawnInstances++;
                Emit(inst, gpu, value, w);
            }
        }
        // The landmarks (a few hundred, kept apart from the zones): the same test with their own reach, the part distance still applying.
        if (streamer.LandmarkZone is { } landmarkZone)
        {
            float reach = LandmarkReach;
            foreach (var inst in landmarkZone.Real) DrawReal(landmarkZone, inst, reach, eye, frustum, options, now);
        }
        if (!NoDistant && !landmarksOnly)
            foreach (var t in towns)
            {
                if (t.Mesh.Gpu is not { } gpu) continue;
                float value = Vector3.Distance(t.Centre, eye) - t.Radius;
                if (value >= DistantReach || !SphereVisible(frustum, t.Centre, t.Radius)) continue;
                t.Mesh.LastUsed = now;
                var batch = BatchFor(gpu, distantMaterial, 0, town: true);
                if (batch.Count == 0) active.Add(batch);
                batch.Add(Matrix4x4.CreateTranslation(t.Town.Position));
                DrawnInstances++;
            }

        double tCull = cpu.Elapsed.TotalMilliseconds;

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

        // 3. Draw: Prepare reads the textures (WorldTexture.Key) and makes the draw list, Record puts it into a native segment of VkGl's pass.
        var prog = depthPass ? depthProg : colourProg;
        int wireMode = debugLevels == 2 ? 2 : options.Wireframe;
        double recordMs = 0;
        if (active.Count > 0)
        {
            var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance };
            var fadeRange = new Vector4(real - realBand, real, DistantReach - distantBand, DistantReach);
            if (wireMode != 2)
            {
                PrepareDraws(options, wire: false);
                long r0 = ObjTiming ? Stopwatch.GetTimestamp() : 0;
                RecordDraws(prog, wire: false, in view, fadeRange);
                if (ObjTiming) recordMs += (Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency;
            }
            if (wireMode != 0)
            {
                PrepareDraws(options, wire: true);
                long r0 = ObjTiming ? Stopwatch.GetTimestamp() : 0;
                RecordDraws(prog, wire: true, in view, fadeRange);
                if (ObjTiming) recordMs += (Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency;
            }
        }
        double tBatches = cpu.Elapsed.TotalMilliseconds;
        int batchDraws = DrawCalls;
        if (terrainMeshes.Count > 0) DrawCalls += terrain.DrawMeshes(terrainMeshes, depthPass);
        LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds;
        int kind = depthPass ? 1 : 0;
        (Totals[kind, 0], Totals[kind, 1], Totals[kind, 2], Totals[kind, 3], Totals[kind, 4]) =
            (Totals[kind, 0] + DrawnInstances, Totals[kind, 1] + DrawnTriangles, Totals[kind, 2] + DrawCalls, Totals[kind, 3] + PartLimited, Totals[kind, 4] + 1);
        if (ObjTiming) ObjAccount(depthPass ? 1 : 0, tCull, tUpload, tBatches, LastDrawCpuMs, batchDraws, DrawCalls - batchDraws, recordMs);
    }


    /// <summary>
    /// One real instance of <see cref="Draw"/>: culled by its range (<paramref name="range"/>, the object distance or, for a landmark, the landmark reach,
    /// and its part distance) and the frustum, then its LOD level and batches. The range is the one <see cref="MarkInRange"/> and the streamer's scans use.
    /// </summary>
    void DrawReal(ObjectStreamer.Zone zone, ObjectStreamer.Instance inst, float range, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, long now)
    {
        if (inst.Gpu is not { } gpu) return;
        if (!ReferenceEquals(inst.Mesh.Gpu, gpu)) { Unresolve(zone, inst); return; }
        float value = Vector3.Distance(inst.Centre, eye) - inst.Radius;
        float limit = Math.Min(range, inst.Limit);
        if (value >= limit || !SphereVisible(frustum, inst.Centre, inst.Radius))
        {
            if (value >= inst.Limit && value < range) PartLimited++;   // stopped by the game's part distance, not the object distance (benchmark)
            return;
        }
        float w = ObjectRanges.EdgeWeight(value, limit, Math.Clamp(limit * 0.1f, 50, 1500));
        if (w <= 0) return;
        inst.Mesh.LastUsed = now;
        DrawnInstances++;
        if (inst.TerrainMode && options.Textures)
        {
            // The terrain shader's path: one draw each, no fading, the level the game would pick.
            int level = MeshLod.Select(gpu.Distances, value * LodBias);
            var g = gpu.Manual[level] ?? gpu;
            int lv = gpu.Manual[level] is null ? level : 0;
            foreach (var gp in g.Parts)
                if (gp.Count[lv] > 0)
                {
                    terrainMeshes.Add((meshes.PlainMesh(gp, lv), gp.Count[lv], inst.Transform));
                    DrawnTriangles += gp.Count[lv] / 3;
                }
            return;
        }
        Emit(inst, gpu, value, w);
    }

    /// <summary><c>MEITOU_OBJECT_TIMING=1</c>: the CPU time of <see cref="Draw"/>'s steps summed over the run by kind (colour or depth), with the
    /// draws each issued, printed when the renderer is disposed (docs/renderer-native.md 7.1, wave 3 objects).</summary>
    static readonly bool ObjTiming = Environment.GetEnvironmentVariable("MEITOU_OBJECT_TIMING") == "1";
    static readonly int ObjSkip = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_OBJECT_TIMING_SKIP"), out int skip) ? skip : 160;
    readonly double[,] objMs = new double[2, 4];
    readonly double[] objRecordMs = new double[2], objLoopMs = new double[2];
    double callLoopMs;   // this call's record loops (the part of recordMs between the segment's setup and its end)
    readonly long[] objSeen = new long[2], objCalls = new long[2], objBatchDraws = new long[2], objTerrainDraws = new long[2];

    void ObjAccount(int k, double tCull, double tUpload, double tBatches, double tEnd, int batchDraws, int terrainDraws, double recordMs = 0)
    {
        if (objSeen[k]++ < ObjSkip) return;   // the first calls are cold (pipelines, streaming)
        objCalls[k]++;
        objRecordMs[k] += recordMs;
        objLoopMs[k] += callLoopMs;
        objMs[k, 0] += tCull; objMs[k, 1] += tUpload - tCull; objMs[k, 2] += tBatches - tUpload; objMs[k, 3] += tEnd - tBatches;
        objBatchDraws[k] += batchDraws; objTerrainDraws[k] += terrainDraws;
    }

    void ReportObjectTiming()
    {
        if (!ObjTiming) return;
        string[] names = ["colour", "depth"];
        for (int k = 0; k < 2; k++)
        {
            if (objCalls[k] == 0) continue;
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"" +
                $"objects timing {names[k]}: {objCalls[k]} calls, per call us: cull {objMs[k, 0] * 1000 / objCalls[k]:F1}, upload {objMs[k, 1] * 1000 / objCalls[k]:F1}, batches {objMs[k, 2] * 1000 / objCalls[k]:F1}, terrain meshes {objMs[k, 3] * 1000 / objCalls[k]:F1}; " +
                $"draws per call: batches {(double)objBatchDraws[k] / objCalls[k]:F1}, terrain {(double)objTerrainDraws[k] / objCalls[k]:F1}; " +
                $"us per batch draw {(objBatchDraws[k] > 0 ? objMs[k, 2] * 1000 / objBatchDraws[k] : 0):F2} (record only {(objBatchDraws[k] > 0 ? objRecordMs[k] * 1000 / objBatchDraws[k] : 0):F2}, loop only {(objBatchDraws[k] > 0 ? objLoopMs[k] * 1000 / objBatchDraws[k] : 0):F3}; segment setup + end per call us {(objRecordMs[k] - objLoopMs[k]) * 1000 / objCalls[k]:F1})"));
        }
    }

    /// <summary>Adds an instance at its LOD level: two batches while blending levels, the upper level taking [0, t·w) of the dither range and the lower [t·w, w).</summary>
    void Emit(ObjectStreamer.Instance inst, GpuObjectMesh gpu, float value, float weight)
    {
        var blend = MeshLod.Blend(gpu.Distances, value * LodBias);
        // A mesh held without its finest levels (made for farther users, see ObjectMeshCache.Retarget) draws its finest instead, until it is remade.
        if (gpu.MinLevel > 0 && blend.Lower < gpu.MinLevel) coarseDraws++;
        if (gpu.MinLevel > 0) blend = new LodBlend(Math.Max(blend.Lower, gpu.MinLevel), Math.Max(blend.Upper, gpu.MinLevel), blend.T);
        var m = inst.Transform;
        if (!blend.IsBlending)
        {
            Add(gpu, inst.Materials!, blend.Lower, m, 0, weight >= 0.999f ? 2 : weight);
            return;
        }
        Add(gpu, inst.Materials!, blend.Upper, m, 0, blend.T * weight);
        Add(gpu, inst.Materials!, blend.Lower, m, blend.T * weight, weight);
    }

    void Add(GpuObjectMesh gpu, ObjectMaterialSet materials, int level, Matrix4x4 m, float lo, float hi)
    {
        var manual = gpu.Manual[level];
        var batch = BatchFor(manual ?? gpu, materials, manual is null ? level : 0, town: false);
        batch.Manual = manual is not null;
        if (batch.Count == 0) active.Add(batch);
        m.M14 = lo;
        m.M24 = hi;
        batch.Add(m);
    }

    Batch BatchFor(GpuObjectMesh gpu, ObjectMaterialSet materials, int level, bool town)
    {
        var key = (gpu, materials, level, town);
        if (!batchMap.TryGetValue(key, out var batch))
            batchMap[key] = batch = new Batch { Mesh = gpu, Materials = materials, Level = level, Town = town };
        return batch;
    }

    static bool SphereVisible(Vector4[] planes, Vector3 centre, float radius)
    {
        foreach (var p in planes)
            if (p.X * centre.X + p.Y * centre.Y + p.Z * centre.Z + p.W < -radius * MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z)) return false;
        return true;
    }

    // ------------------------------------------------------------------ native recording
    // docs/renderer-native.md 3.3 and 7.5, step O: native-model programs (BuildingLodShaders' native variants: the same maths, the values in
    // FrameConstants / ViewConstants / push constants, the textures by bindless index), recorded into the pass VkGl has open
    // (BeginNativeInPass). Draw is split into Prepare (cull, levels, batches, then PrepareDraws: the texture keys through WorldTexture.Key,
    // which can start a reload, and the draw list) and Record (the list into one native segment: the sets and dynamic state once, per draw a
    // push of the changed bytes and the part's own buffers). Since phase 8 stage 1 the meshes and textures are native (ObjectMeshCache,
    // WorldTextureCache): the vertex layout is made once per part, the bindless index kept on the texture per LOD bias. Nothing but commands in
    // the loop, no hidden allocation.

    const int InstanceStride = 64;
    /// <summary>The first location the per-instance rows (the batch matrices) take; the mesh's own inputs are below it.</summary>
    const int RowLocation = BuildingLodShaders.InstanceLocation;

    /// <summary>This call's batch matrices in the frame's constants (a batch at its <see cref="Batch.Offset"/>; the draws reach it by firstInstance).</summary>
    Transient instances;

    /// <summary>One part draw, in the renderer-owned list: the part, its level and instance range, the texture keys (their bindless indices
    /// are patched into <see cref="Push"/> per segment) and the push constants it needs.</summary>
    struct ObjDraw
    {
        public GpuObjectPart Part;
        public int Level;
        public uint FirstInstance, Instances;
        public uint Diffuse, Normal, Diffuse2, Normal2;
        public ObjectPush Push;
    }

    ObjDraw[] draws = new ObjDraw[256];
    int drawCount;

    /// <summary>A native program of the objects with its vertex inputs from the reflection: the layout and buffers a GL vertex array feeds
    /// it, as VkGl feeds a GL program (<see cref="LegacyProgram.VertexLayout"/>: a disabled attribute reads GL's constant through a stride-0 binding).</summary>
    sealed class ObjProg : IDisposable
    {
        readonly GpuContext ctx;
        public readonly ShaderProgram P;
        readonly int[] locations;
        readonly Meitou.Rendering.Gpu.Shaders.ScalarKind[] kinds;
        /// <summary>The program's own input locations (below the instance rows): 0 .. Own − 1.</summary>
        public readonly int Own;
        VertexLayout? last;

        public ObjProg(GpuContext ctx, NativeFrame frame, string vertex, string fragment, string name)
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
                    : new BufferBinding(ctx.Defaults.DummyVertex.Buffer, GlConventions.DummyVertexOffset(input >= 0 ? kinds[input] : Meitou.Rendering.Gpu.Shaders.ScalarKind.Float));
            }
            return result;
        }

        public void Dispose()
        {
            ctx.Pipelines.Forget(P);
            P.Dispose();
        }
    }

    /// <summary>The pipeline state a segment's draws share (everything of <see cref="GraphicsPipelineDesc"/> but the vertex layout).</summary>
    readonly record struct SegmentPipeline(ShaderProgram Program, AttachmentFormats Formats, BlendState Blend, Silk.NET.Vulkan.ColorComponentFlags Mask,
        Silk.NET.Vulkan.PolygonMode Polygon, bool AlphaToCoverage, bool DepthClamp);

    readonly Dictionary<(int Kind, SegmentPipeline State), int> segmentByState = [];
    readonly SegmentPipeline[] lastSegment = new SegmentPipeline[4];
    readonly int[] segmentIds = new int[4];
    int segmentCount;

    /// <summary>A stable number for a segment's pipeline state, so a part compares one int per draw.</summary>
    int SegmentId(int kind, ShaderProgram p, PassTargets t, DrawState s)
    {
        var segment = new SegmentPipeline(p, t.Formats, s.Blend, s.ColourMask, s.Polygon, s.AlphaToCoverage, s.DepthClamp);
        if (segmentIds[kind] != 0 && segment == lastSegment[kind]) return segmentIds[kind];
        if (!segmentByState.TryGetValue((kind, segment), out int id)) segmentByState[(kind, segment)] = id = ++segmentCount;
        lastSegment[kind] = segment;
        return segmentIds[kind] = id;
    }

    /// <summary>A part's native state for <paramref name="p"/>, made on first use from its native buffers (they never change while the part
    /// lives). The batch matrices are four per-instance rows of 64 bytes at locations 7 to 10 (bound once per segment).</summary>
    static void Current(ref ObjectNativeMesh n, GpuObjectPart part, ObjProg p)
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

    GraphicsPipeline PipelineFor(ref ObjectNativeMesh n, ObjProg p, int segment, DrawState state, AttachmentFormats formats, string label)
    {
        if (n.SegB == segment) return n.PipeB!;
        var pipeline = Gpu.Pipelines.Get(state.Pipeline(p.P, n.Layout!, Silk.NET.Vulkan.PrimitiveTopology.TriangleList, formats, label));
        (n.SegB, n.PipeB) = (n.SegA, n.PipeA);
        (n.SegA, n.PipeA) = (segment, pipeline);
        return pipeline;
    }

    /// <summary>The segment's LOD bias (the upscaler's, <see cref="GpuContext.LodBias"/>) and GL's stand-in for a texture not resident.</summary>
    float textureBias;
    uint textureStandIn;

    /// <summary>A new segment: the bias in effect now and the stand-in's bindless index (the 2D float array the shaders index).</summary>
    void NewTextureSegment()
    {
        textureBias = Gpu.LodBias;
        textureStandIn = Gpu.StandIn2D;
    }

    /// <summary>The bindless index of a texture by its <see cref="WorldTexture.Key"/> (0: the stand-in), as the GL texture was sampled.</summary>
    uint Texture(uint key) => key == 0 ? textureStandIn : textureCache.Index(key, textureBias, textureStandIn);

    /// <summary>The batches' parts as draws, in order, with the texture keys (reading a key counts as use: here, not in Record).</summary>
    void PrepareDraws(WorldRenderOptions options, bool wire)
    {
        drawCount = 0;
        foreach (var b in active)
        {
            int fade = b.Town ? 1 : 0;
            for (int i = 0; i < b.Mesh.Parts.Count; i++)
            {
                var gp = b.Mesh.Parts[i];
                if (gp.Count[b.Level] == 0) continue;
                if (drawCount == draws.Length) Array.Resize(ref draws, drawCount * 2);
                ref var d = ref draws[drawCount++];
                d = default;
                d.Part = gp; d.Level = b.Level; d.FirstInstance = (uint)b.Offset; d.Instances = (uint)b.Count;
                d.Push.TriplanarScale = 1f / 5000;
                d.Push.FadeMode = fade;
                if (wire || debugLevels == 1)
                {
                    d.Push.Wireframe = 1;
                    d.Push.FlatColour = LevelColour(b.Level, b.Manual, ReferenceEquals(b.Materials, distantMaterial));
                }
                else
                {
                    var pm = b.Materials.Parts[Math.Min(i, b.Materials.Parts.Length - 1)];
                    Material(ref d, gp, pm, options);
                    // A part whose diffuse map is not resident yet is drawn plain grey (pop-in the benchmark counts).
                    if (!depthPass && options.Textures && d.Diffuse == 0 && pm.WantsDiffuse) untexturedDraws++;
                }
                DrawCalls++;
                DrawnTriangles += (long)gp.Count[b.Level] / 3 * b.Count;
            }
        }
    }

    static void Material(ref ObjDraw d, GpuObjectPart gp, ObjectPartMaterial pm, WorldRenderOptions options)
    {
        var m = pm.Material;
        uint diffuse = pm.Diffuse;
        bool textured = options.Textures && diffuse != 0;
        uint normalId = textured ? pm.Normal : 0;
        bool normal = options.NormalMaps && textured && normalId != 0 && gp.HasTangents;
        uint diffuse2 = textured ? pm.Diffuse2 : 0;
        bool dual = textured && diffuse2 != 0 && gp.HasColours;
        d.Diffuse = textured ? diffuse : 0;
        d.Normal = normalId;
        d.Diffuse2 = dual ? diffuse2 : 0;
        d.Normal2 = dual ? pm.Normal2 : 0;
        var alpha = m?.Alpha ?? AlphaSource.None;
        if (!textured || alpha == AlphaSource.NormalAlpha && normalId == 0) alpha = AlphaSource.None;
        var tile = m?.Tile ?? Vector2.One;
        ref var pc = ref d.Push;
        pc.NormalSwizzled = pm.Swizzled ? 1u : 0u;
        pc.HasDiffuse = textured ? 1u : 0u;
        pc.HasNormal = normal ? 1u : 0u;
        pc.HasDual = dual ? 1u : 0u;
        pc.Triplanar = textured && (m?.Triplanar ?? false) ? 1u : 0u;
        pc.Tile = tile;
        pc.AlphaSource = (int)alpha;
        pc.AlphaChannel = Math.Clamp(m?.AlphaChannel ?? 3, 0, 3);
        pc.GreyChannel = textured ? m?.GreyChannel ?? -1 : -1;
        pc.Tint = m?.Tint ?? Vector3.One;
        pc.AlphaThreshold = alpha == AlphaSource.None ? 0f : m!.AlphaThreshold;
        pc.Emissive = textured && normalId != 0 && (m?.Emissive ?? false) ? 1u : 0u;
        pc.UseVertexColour = gp.HasColours && (m?.VertexColours ?? false) ? 1u : 0u;
        pc.Specular = textured ? m?.SpecularMult ?? 1 : 0.3f;
        pc.Spare = (pm.Dust ? MeshSurface.Dust : 0u) | (m?.Foliage ?? false ? MeshSurface.Foliage : 0u) | (m?.Dust ?? false ? MeshSurface.TriplanarDust : 0u);
    }

    /// <summary>The draw list in one native segment of VkGl's open pass (colour or depth, solid or wireframe).</summary>
    void RecordDraws(ObjProg prog, bool wire, in ViewConstants view, Vector4 fadeRange)
    {
        if (drawCount == 0) return;
        string label = depthPass ? "objects depth" : wire ? "objects wire" : "objects";
        int kind = (depthPass ? 1 : 0) + (wire ? 2 : 0);
        // Prepare (wave 4, docs/renderer-native.md 6.2): the pass state, the sets, and per draw everything resolved (pipeline, buffers, the push
        // block with its bindless indices), into a job that only records.
        var targets = Gpu.CurrentTargets();
        // The objects are drawn double-sided (the GL version turned the cull face off); the wireframe as lines pulled forward
        // (GL's PolygonMode LINE with POLYGON_OFFSET_LINE and PolygonOffset(-1, -1)).
        var state = Gpu.CurrentState() with { Cull = Silk.NET.Vulkan.CullModeFlags.None };
        if (wire) state = state with { Polygon = Silk.NET.Vulkan.PolygonMode.Line, BiasEnable = true, BiasConstant = -1, BiasSlope = -1 };
        int segment = SegmentId(kind, prog.P, targets, state);
        var job = drawJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Layout, job.Count) = (this, targets, state, prog.P.Layout, 0);
        job.Frame = nativeFrame.Prepare(in view);
        for (int a = 0; a < 4; a++) job.Rows[a] = new BufferBinding(instances.Handle, instances.Offset + (ulong)(16 * a));
        NewTextureSegment();
        long loop0 = ObjTiming ? Stopwatch.GetTimestamp() : 0;
        if (job.Draws.Length < drawCount) job.Draws = new DrawJob.Draw[Math.Max(drawCount, job.Draws.Length * 2)];
        var list = draws;
        for (int i = 0; i < drawCount; i++)
        {
            ref var d = ref list[i];
            ref var pc = ref d.Push;
            pc.FadeRange = fadeRange;
            pc.Diffuse = Texture(d.Diffuse);
            pc.Normal = Texture(d.Normal);
            pc.Diffuse2 = Texture(d.Diffuse2);
            pc.Normal2 = Texture(d.Normal2);
            var part = d.Part;
            ref var n = ref (depthPass ? ref part.DepthNative : ref part.ColourNative);
            Current(ref n, part, prog);
            job.Draws[i] = new DrawJob.Draw
            {
                Pipeline = n.SegA == segment ? n.PipeA! : PipelineFor(ref n, prog, segment, state, targets.Formats, label),
                Vertices = n.Vertices, Elements = new BufferBinding(n.Elements.Buffer, n.Elements.Offset + (ulong)part.Offset[d.Level] * 4),
                Push = pc, IndexCount = (uint)part.Count[d.Level], Instances = d.Instances, FirstInstance = d.FirstInstance,
            };
        }
        job.Count = drawCount;
        if (ObjTiming) callLoopMs += (Stopwatch.GetTimestamp() - loop0) * 1000.0 / Stopwatch.Frequency;
        Gpu.Record(label, job);
    }

    readonly JobPool<DrawJob> drawJobs = new();

    /// <summary>One objects segment as prepared (<see cref="RecordDraws"/>): recorded on any thread, the same commands as before wave 4.</summary>
    sealed class DrawJob : RecordJob
    {
        public struct Draw
        {
            public GraphicsPipeline Pipeline;
            public BufferBinding[] Vertices;
            public BufferBinding Elements;
            public ObjectPush Push;
            public uint IndexCount, Instances, FirstInstance;
        }

        public WorldObjectRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public Silk.NET.Vulkan.PipelineLayout Layout;
        public FrameBinding Frame;
        public readonly BufferBinding[] Rows = new BufferBinding[4];
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
            cmd.BindVertexBuffers(RowLocation, Rows);
            var stages = Silk.NET.Vulkan.ShaderStageFlags.VertexBit | Silk.NET.Vulkan.ShaderStageFlags.FragmentBit;
            ObjectPush last = default;
            bool pushed = false;
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                cmd.BindPipeline(d.Pipeline);
                cmd.BindVertexBuffers(0, d.Vertices);
                cmd.BindIndexBuffer(d.Elements, Silk.NET.Vulkan.IndexType.Uint32);
                if (!pushed || !System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<ObjectPush>(in d.Push)).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<ObjectPush>(in last))))
                {
                    cmd.PushConstants(layout, stages, in d.Push);
                    last = d.Push;
                    pushed = true;
                }
                cmd.DrawIndexed(d.IndexCount, d.Instances, 0, 0, d.FirstInstance);
            }
        }

        public override void Release()
        {
            Array.Clear(Draws, 0, Count);
            Count = 0;
            Owner.drawJobs.Return(this);
        }
    }

    // ---- depth only (the sun's shadow map, ShadowPass) ----
    bool depthPass;

    /// <summary>
    /// Draws the objects' depth for a shadow cascade: <see cref="Draw"/>'s culling, levels and batches (chosen by the camera's
    /// <paramref name="eye"/>) with <see cref="ShadowShaders.MeshDepthFragment"/> (the materials' cut-outs and the caster bias), and the
    /// TERRAIN-mode meshes through the terrain's depth path. Leaves the draw counters describing this call.
    /// </summary>
    public void DrawDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, TerrainRenderer terrain)
    {
        depthPass = true;
        try { Draw(viewProjection, eye, frustum, options, Vector3.UnitY, Vector3.Zero, 0, terrain); }
        finally { depthPass = false; }
    }

    bool landmarksOnly;

    /// <summary><see cref="DrawDepth"/> for the landmarks alone (the landmark shadow map, ShadowPass.Meitou): the same reach, levels and batches.</summary>
    public void DrawLandmarksDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, TerrainRenderer terrain)
    {
        landmarksOnly = true;
        try { DrawDepth(viewProjection, eye, frustum, options, terrain); }
        finally { landmarksOnly = false; }
    }

    /// <summary>
    /// The landmarks <see cref="Draw"/> would draw from <paramref name="eye"/> looking any way (resolved, within the landmark reach and their part
    /// distance): their bounding spheres (xyz centre, w radius) into <paramref name="spheres"/>. Returns a key that changes when that set does.
    /// </summary>
    public long LandmarkCasters(Vector3 eye, List<Vector4> spheres)
    {
        spheres.Clear();
        if (streamer.LandmarkZone is not { } zone) return 0;
        float reach = LandmarkReach;
        long key = 17;
        for (int i = 0; i < zone.Real.Count; i++)
        {
            var inst = zone.Real[i];
            if (inst.Gpu is not { } gpu || !ReferenceEquals(inst.Mesh.Gpu, gpu)) continue;
            if (Vector3.Distance(inst.Centre, eye) - inst.Radius >= Math.Min(reach, inst.Limit)) continue;
            spheres.Add(new Vector4(inst.Centre, inst.Radius));
            key = key * 31 + i + 1;
        }
        return key;
    }

    public void Dispose()
    {
        ReportObjectTiming();
        colourProg.Dispose();
        depthProg.Dispose();
        nativeFrame.Dispose();
        streamer.Dispose();
        meshes.Dispose();
        textureCache.Dispose();
        objects.Dispose();
    }
}
