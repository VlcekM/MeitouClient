using System.Diagnostics;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;
using SamplerInfo = Meitou.Rendering.Vulkan.Shaders.SamplerInfo;

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
    readonly IGl gl;
    /// <summary>The native GPU API next to <c>gl</c> (docs/renderer-native.md 7.1 step 8); ports use it instead of looking it up.</summary>
    public GpuContext Gpu { get; }
    readonly NativeFrame nativeFrame;
    readonly ObjProg colourProg, depthProg;   // the colour and the depth (shadow caster) programs, native
    readonly WorldTextureCache textureCache;
    readonly MaterialResolver resolver;
    readonly UploadQueue uploads = new();
    readonly ObjectMeshCache meshes;
    readonly ObjectStreamer streamer;
    readonly WorldObjects objects;
    readonly uint instanceBuffer;
    readonly Dictionary<(string, string, string), ObjectMaterialSet> materialSets = [];
    readonly List<TownDraw> towns = [];
    readonly ObjectMaterialSet distantMaterial;
    readonly Dictionary<(GpuObjectMesh, ObjectMaterialSet, int, bool), Batch> batchMap = [];
    readonly List<Batch> active = [];
    readonly List<(uint, int, Matrix4x4)> terrainMeshes = [];   // the TERRAIN-mode instances of a draw (TerrainRenderer.DrawMeshes)
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

    internal WorldObjectRenderer(IGl gl, GpuContext gpu, AssetLocator assets, WorldObjects objects)
    {
        this.gl = gl;
        Gpu = gpu;
        this.objects = objects;
        // Both native programs are made here with every handle resolved, never inside a draw (the GL programs they replace are not made).
        nativeFrame = new NativeFrame(gpu);
        colourProg = new ObjProg(gpu, nativeFrame, BuildingLodShaders.VertexNative(), BuildingLodShaders.FragmentNative(), "objects");
        depthProg = new ObjProg(gpu, nativeFrame, BuildingLodShaders.VertexNative(), BuildingLodShaders.DepthNative(), "objects depth");
        textureCache = new WorldTextureCache(gl, assets);
        var library = OgreMaterialLibrary.LoadConfigured(objects.Install, out _);
        resolver = new MaterialResolver(objects.Database, library, assets);
        instanceBuffer = gl.GenBuffer();
        meshes = new ObjectMeshCache(gl, assets, uploads, instanceBuffer);
        meshes.Unloaded = gpu => { unloadedMeshes.Add(gpu); RemoveBatches(gpu); };
        streamer = new ObjectStreamer(objects, meshes);
        var distant = new SurfaceMaterial { Description = "DistantTown (vertex colour x texture x 1.5)", Diffuse = DistantTowns.DiffuseTexture, VertexColours = true, SpecularMult = 0 };
        distantMaterial = new ObjectMaterialSet([new ObjectPartMaterial(distant, textureCache.Get(distant.Diffuse, false), null, null, null)]);
        foreach (var t in objects.DistantTowns)
            towns.Add(new TownDraw { Town = t, Mesh = meshes.Get(t.MeshPath, distant: true), Centre = t.Position });
    }

    /// <summary>Interactive use (non-zero): bounded upload work per frame. 0 (screenshots): no limit.</summary>
    public int LoadBudget { get; set; }

    /// <summary>Instances drawn by the last <see cref="Draw"/>.</summary>
    public int DrawnInstances { get; private set; }
    public long DrawnTriangles { get; private set; }
    public int DrawCalls { get; private set; }
    public double LastDrawCpuMs { get; private set; }
    public double LastUpdateMs { get; private set; }

    /// <summary>
    /// Real (full detail) objects are drawn up to this distance (<see cref="MeshLod.Value"/>-like: camera to bounds centre minus radius);
    /// buildings' parts have their own limits too (<see cref="ObjectRanges.PartRenderingDistance"/>). Beyond it distant towns take over.
    /// </summary>
    public float ObjectDistance { get; set; } = 12000;

    /// <summary>Ogre's camera LOD bias as a distance factor: the LOD value is multiplied by it, so above 1 coarser levels come sooner, below 1 later.</summary>
    public float LodBias { get; set; } = 1;

    /// <summary>Distant towns and buildings' distant meshes are drawn up to this distance (the game: its <c>distant town range</c> in zones).</summary>
    public float DistantRange { get; set; } = ObjectRanges.MaxDistantTownRangeZones * WorldLayout.ZoneSize;

    /// <summary>Draw nothing but the real objects (no distant meshes): the game with distant towns off.</summary>
    public bool NoDistant { get; set; }

    /// <summary>Work in flight: zones being laid out, meshes decoding or uploading, textures decoding.</summary>
    public int Pending => streamer.Pending + meshes.Pending + uploads.Count + textureCache.PendingCount + unresolvedInRange;

    int unresolvedInRange;
    public List<string> Messages { get; } = [];

    /// <summary>A line about what is loaded, for the log.</summary>
    /// <summary>GPU memory held by streamed meshes and textures, for the stats line and the window title.</summary>
    public long ResidentBytes => meshes.Bytes + textureCache.ResidentBytes;
    public string ResidentDescription => $"{meshes.Bytes / 1048576.0:0} MB in {meshes.Resident} meshes ({meshes.Unloads} unloaded, {meshes.Reloads} reloaded), {textureCache.Describe()}";
    public string Describe() =>
        $"{streamer.Loaded} zones, {streamer.Instances:N0} instances ({streamer.Resolved:N0} resolved), {meshes.Resident}/{meshes.Total} meshes requested or resident, resident: {ResidentDescription}, " +
        $"{towns.Count(t => t.Mesh.Status == ObjectMesh.State.Resident)}/{towns.Count} distant towns; {objects.Describe()}";

    // ------------------------------------------------------------------ streaming

    /// <summary>Render-thread time per frame spent on uploads and resolving.</summary>
    public double BudgetMs { get; set; } = 2;

    /// <summary>Follows the eye: lays out zones and loads meshes and textures around it, within <see cref="BudgetMs"/> of render-thread time.</summary>
    public void Update(Vector3 eye) => Update(eye, BudgetMs);

    static readonly bool Log = Environment.GetEnvironmentVariable("MEITOU_STREAM_LOG") == "1";

    void Update(Vector3 eye, double budgetMs)
    {
        var watch = Stopwatch.StartNew();
        bool unlimited = budgetMs > 1e8;
        float streamRange = (NoDistant ? ObjectDistance : Math.Max(ObjectDistance, DistantRange)) + WorldLayout.ZoneSize * 0.5f;
        streamer.Update(eye, streamRange, unlimited ? 64 : 1);
        double tZones = watch.Elapsed.TotalMilliseconds;
        ResolveTowns(eye);
        unresolvedInRange = streamer.Scan(eye, ObjectDistance, DistantRange, NoDistant, Resolve, unlimited ? int.MaxValue : 40);
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
        lastMark = now;
        markStamp++;
        void Mark(ObjectStreamer.Instance inst, float range)
        {
            if (inst.Gpu is null || Vector3.Distance(eye, inst.Centre) - inst.Radius >= Math.Min(range, inst.Limit)) return;
            inst.Mesh.LastUsed = now;
            if (inst.Materials is { } set && set.Stamp != markStamp) { set.Stamp = markStamp; set.Touch(); }
        }
        foreach (var zone in streamer.AllZones)
        {
            foreach (var inst in zone.Real) Mark(inst, ObjectDistance);
            if (!NoDistant) foreach (var inst in zone.Stand) Mark(inst, DistantRange);
        }
        if (NoDistant) return;
        foreach (var t in towns)
            if (Vector3.Distance(eye, t.Centre) - t.Radius < DistantRange) t.Mesh.LastUsed = now;
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
            if (t.Mesh.Status == ObjectMesh.State.None && d - t.Radius < DistantRange) meshes.Request(t.Mesh, d);
        }
    }

    /// <summary>Waits until everything wanted around <paramref name="eye"/> is laid out, decoded, uploaded and textured (offscreen rendering).</summary>
    public void Settle(Vector3 eye, int timeoutMs = 300000)
    {
        var watch = Stopwatch.StartNew();
        int rounds = 0;
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            Update(eye, 1e9);
            if (++rounds > 2 && Pending == 0) return;
            Thread.Sleep(1);
        }
        Console.WriteLine($"warning   object streaming did not finish in {timeoutMs / 1000} s");
    }

    // ------------------------------------------------------------------ materials

    /// <summary>Makes the instance drawable once its mesh is resident: bounds, draw distance, material set.</summary>
    void Resolve(ObjectStreamer.Instance inst)
    {
        var gpu = inst.Mesh.Gpu!;
        var t = inst.Placed.Transform;
        inst.Gpu = gpu;
        inst.Centre = Vector3.Transform(gpu.Centre, t);
        float scale = MathF.Sqrt(Math.Max(new Vector3(t.M11, t.M12, t.M13).LengthSquared(), Math.Max(new Vector3(t.M21, t.M22, t.M23).LengthSquared(), new Vector3(t.M31, t.M32, t.M33).LengthSquared())));
        inst.Radius = gpu.Radius * scale;
        inst.Limit = inst.Placed.Kind == PlacedKind.BuildingPart
            ? ObjectRanges.PartRenderingDistance(gpu.Radius, inst.Placed.Owner.GetInt("function"))
            : float.MaxValue;
        if (inst.Stand) { inst.Materials = distantMaterial; return; }
        // A part's look comes from the material the layout chose (which differs per town), else from the resolver's candidates.
        var key = inst.Placed.MeshPath;
        var mkey = inst.Placed.Material is { } spec
            ? (key.ToLowerInvariant(), "spec", spec.StringId)
            : (key.ToLowerInvariant(), inst.Placed.Source.StringId, inst.Placed.Owner.StringId);
        if (!materialSets.TryGetValue(mkey, out var set)) materialSets[mkey] = set = MakeMaterials(inst.Placed, gpu);
        inst.Materials = set;
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
            result[i] = new ObjectPartMaterial(m, textureCache.Get(m?.Diffuse, border), textureCache.Get(m?.Normal, border),
                textureCache.Get(m?.Diffuse2, border), textureCache.Get(m?.Normal2, border));
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
        float real = ObjectDistance;
        DrawnInstances = 0;
        DrawnTriangles = 0;
        DrawCalls = 0;
        callLoopMs = 0;
        foreach (var b in batchMap.Values) b.Count = 0;
        active.Clear();
        terrainMeshes.Clear();

        // 1. Cull and choose levels: every instance into the batch of its (mesh, materials, level) with its dither range.
        float realBand = Math.Clamp(real * 0.1f, 50, 1500);
        float distantBand = Math.Clamp(DistantRange * 0.15f, 500, 4000);
        foreach (var zone in streamer.ZonesNear(eye, Math.Max(real, NoDistant ? 0 : DistantRange), frustum))
        {
            if (ObjectStreamer.ZoneDistance(zone.X0, zone.Z0, eye) <= real)
                foreach (var inst in zone.Real)
                {
                    if (inst.Gpu is not { } gpu) continue;
                    if (!ReferenceEquals(inst.Mesh.Gpu, gpu)) { Unresolve(zone, inst); continue; }
                    float value = Vector3.Distance(inst.Centre, eye) - inst.Radius;
                    float limit = Math.Min(real, inst.Limit);
                    if (value >= limit || !SphereVisible(frustum, inst.Centre, inst.Radius)) continue;
                    float w = ObjectRanges.EdgeWeight(value, limit, Math.Clamp(limit * 0.1f, 50, 1500));
                    if (w <= 0) continue;
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
                                terrainMeshes.Add((meshes.PlainVao(gp, lv), gp.Count[lv], inst.Transform));
                                DrawnTriangles += gp.Count[lv] / 3;
                            }
                        continue;
                    }
                    Emit(inst, gpu, value, w);
                }
            if (NoDistant) continue;
            foreach (var inst in zone.Stand)
            {
                if (inst.Gpu is not { } gpu) continue;
                if (!ReferenceEquals(inst.Mesh.Gpu, gpu)) { Unresolve(zone, inst); continue; }
                float value = Vector3.Distance(inst.Centre, eye) - inst.Radius;
                if (value >= DistantRange || !SphereVisible(frustum, inst.Centre, inst.Radius)) continue;
                float w = ObjectRanges.RiseWeight(value, real - realBand, realBand) * ObjectRanges.EdgeWeight(value, DistantRange, distantBand);
                if (w <= 0) continue;
                inst.Mesh.LastUsed = now;
                DrawnInstances++;
                Emit(inst, gpu, value, w);
            }
        }
        if (!NoDistant)
            foreach (var t in towns)
            {
                if (t.Mesh.Gpu is not { } gpu) continue;
                float value = Vector3.Distance(t.Centre, eye) - t.Radius;
                if (value >= DistantRange || !SphereVisible(frustum, t.Centre, t.Radius)) continue;
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

        // 3. Draw: Prepare reads the textures (WorldTexture.Id) and makes the draw list, Record puts it into a native segment of VkGl's pass.
        var prog = depthPass ? depthProg : colourProg;
        gl.Disable(EnableCap.CullFace);   // the objects are drawn double-sided (the export's cull mode is None while GL's cull face is off)
        int wireMode = debugLevels == 2 ? 2 : options.Wireframe;
        double recordMs = 0;
        if (active.Count > 0)
        {
            var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance };
            var fadeRange = new Vector4(real - realBand, real, DistantRange - distantBand, DistantRange);
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
                gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
                gl.Enable(EnableCap.PolygonOffsetLine);
                gl.PolygonOffset(-1, -1);
                long r0 = ObjTiming ? Stopwatch.GetTimestamp() : 0;
                RecordDraws(prog, wire: true, in view, fadeRange);
                if (ObjTiming) recordMs += (Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency;
                gl.Disable(EnableCap.PolygonOffsetLine);
                gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
            }
        }
        SkyRenderer.Active?.BindUnits();   // the atmosphere's texture units, as Apply left them for the GL code that follows
        gl.BindVertexArray(0);
        double tBatches = cpu.Elapsed.TotalMilliseconds;
        int batchDraws = DrawCalls;
        if (terrainMeshes.Count > 0) DrawCalls += terrain.DrawMeshes(terrainMeshes, depthPass);
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(0);
        LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds;
        if (ObjTiming) ObjAccount(depthPass ? 1 : 0, tCull, tUpload, tBatches, LastDrawCpuMs, batchDraws, DrawCalls - batchDraws, recordMs);
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
    // (BeginNativeInPass). Draw is split into Prepare (cull, levels, batches, then PrepareDraws: the texture ids through WorldTexture.Id and
    // the draw list, every IGl call that can flush or start a reload) and Record (the list into one native segment: the sets and dynamic state
    // once, per draw a push of the changed bytes and the part's own buffers). Nothing but commands in the loop: no VertexArray call (the
    // stamp), no Bindless call (indices cached per segment by texture id), no hidden allocation.

    const int InstanceStride = 64;
    /// <summary>The first location the per-instance rows (the batch matrices) take; the mesh's own inputs are below it.</summary>
    const int RowLocation = BuildingLodShaders.InstanceLocation;

    /// <summary>This call's batch matrices in the frame's constants (a batch at its <see cref="Batch.Offset"/>; the draws reach it by firstInstance).</summary>
    Transient instances;

    /// <summary>One part draw, in the renderer-owned list: the part, its level and instance range, the GL texture ids (their bindless indices
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
        readonly Meitou.Rendering.Vulkan.Shaders.ScalarKind[] kinds;
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

    /// <summary>A part's native state for <paramref name="p"/>, current at <paramref name="stamp"/>: fetched again only when the stamp moved
    /// (<see cref="IGlInterop.VertexArrayStamp"/>), rebuilt only when the export changed. The batch matrices are four per-instance rows of 64
    /// bytes at locations 7 to 10 (bound once per segment).</summary>
    static void Current(ref ObjectNativeMesh n, IGlInterop interop, uint vao, ObjProg p, long stamp)
    {
        if (n.Stamp == stamp && n.Source is not null) return;
        var va = interop.VertexArray(vao);
        n.Stamp = stamp;
        if (ReferenceEquals(n.Source, va)) return;
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        va.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[RowLocation + a] = new LegacyProgram.Attribute(default, Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, InstanceStride, true);
        n.Layout = p.Layout(attributes);
        n.Vertices = p.Buffers(attributes, p.Own);
        n.Elements = va.Elements;
        n.Source = va;
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

    /// <summary>The bindless indices of the segment being recorded, by GL texture id (an entry counts while its segment number is
    /// <see cref="textureSegment"/>): a texture's view or sampler may change between segments (mip streaming, the upscaler's bias), never
    /// inside one, so each texture is looked up once per segment.</summary>
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

    /// <summary>The bindless index of a GL texture VkGl owns (<see cref="IGlInterop.Bindless"/>): what <c>Sampled</c> would bind now, in the 2D
    /// float array the shaders index (0: GL's stand-in).</summary>
    static uint Index2D(IGlInterop interop, uint glTexture)
    {
        var h = interop.Bindless(glTexture);
        if (h.Kind != BindlessKind.Texture2D) throw new InvalidOperationException($"object texture {glTexture} is in the bindless {h.Kind} array, the shaders read textures2D");
        return h.Index;
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

    /// <summary>The batches' parts as draws, in order, with the texture ids the GL code bound (reading an id counts as use: here, not in Record).</summary>
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
                else Material(ref d, gp, b.Materials.Parts[Math.Min(i, b.Materials.Parts.Length - 1)], options);
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
    }

    /// <summary>The draw list in one native segment of VkGl's open pass (colour or depth, solid or wireframe).</summary>
    void RecordDraws(ObjProg prog, bool wire, in ViewConstants view, Vector4 fadeRange)
    {
        if (drawCount == 0) return;
        var interop = Gpu.Interop!;
        string label = depthPass ? "objects depth" : wire ? "objects wire" : "objects";
        int kind = (depthPass ? 1 : 0) + (wire ? 2 : 0);
        // Prepare (wave 4, docs/renderer-native.md 6.2): the pass state, the sets, and per draw everything resolved (pipeline, buffers, the push
        // block with its bindless indices), into a job that only records.
        var targets = interop.CurrentTargets();
        var state = interop.CurrentState();
        int segment = SegmentId(kind, prog.P, targets, state);
        long stamp = interop.VertexArrayStamp;
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
            pc.Diffuse = Texture(interop, d.Diffuse);
            pc.Normal = Texture(interop, d.Normal);
            pc.Diffuse2 = Texture(interop, d.Diffuse2);
            pc.Normal2 = Texture(interop, d.Normal2);
            var part = d.Part;
            ref var n = ref (depthPass ? ref part.DepthNative : ref part.ColourNative);
            Current(ref n, interop, part.Vao, prog, stamp);
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
        gl.BindVertexArray(0);
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

    public void Dispose()
    {
        ReportObjectTiming();
        colourProg.Dispose();
        depthProg.Dispose();
        nativeFrame.Dispose();
        streamer.Dispose();
        meshes.Dispose();
        textureCache.Dispose();
        gl.DeleteBuffer(instanceBuffer);
        objects.Dispose();
    }
}
