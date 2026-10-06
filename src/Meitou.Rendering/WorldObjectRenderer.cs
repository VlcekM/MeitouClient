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
        colourProg = new ObjProg(gpu, BuildingLodShaders.Vertex(), BuildingLodShaders.Fragment(), "objects");
        depthProg = new ObjProg(gpu, BuildingLodShaders.Vertex(), ShadowShaders.MeshDepthFragment, "objects depth");
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
            SetFrameUniforms(prog, viewProjection, eye, light, fogColour, fogDistance, real - realBand, real, DistantRange - distantBand, DistantRange);
            if (wireMode != 2)
            {
                PrepareDraws(options, wire: false);
                long r0 = ObjTiming ? Stopwatch.GetTimestamp() : 0;
                RecordDraws(prog, wire: false);
                if (ObjTiming) recordMs += (Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency;
            }
            if (wireMode != 0)
            {
                PrepareDraws(options, wire: true);
                gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
                gl.Enable(EnableCap.PolygonOffsetLine);
                gl.PolygonOffset(-1, -1);
                long r0 = ObjTiming ? Stopwatch.GetTimestamp() : 0;
                RecordDraws(prog, wire: true);
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
    readonly double[] objRecordMs = new double[2];
    readonly long[] objSeen = new long[2], objCalls = new long[2], objBatchDraws = new long[2], objTerrainDraws = new long[2];

    void ObjAccount(int k, double tCull, double tUpload, double tBatches, double tEnd, int batchDraws, int terrainDraws, double recordMs = 0)
    {
        if (objSeen[k]++ < ObjSkip) return;   // the first calls are cold (pipelines, streaming)
        objCalls[k]++;
        objRecordMs[k] += recordMs;
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
                $"us per batch draw {(objBatchDraws[k] > 0 ? objMs[k, 2] * 1000 / objBatchDraws[k] : 0):F2} (record only {(objBatchDraws[k] > 0 ? objRecordMs[k] * 1000 / objBatchDraws[k] : 0):F2})"));
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
    // docs/renderer-native.md 3.2 and 7.5, step P: VkGl's SPIR-V and layout through LegacyProgram, recorded into the pass VkGl has open
    // (BeginNativeInPass). Draw is split into Prepare (cull, levels, batches, then PrepareDraws: the texture ids through WorldTexture.Id and
    // the draw list, every IGl call that can flush or start a reload) and Record (the list into one native segment: dynamic state once, per
    // draw only what differs, pipelines and vertex buffers resolved once per mesh part).

    const int InstanceStride = 64;
    /// <summary>The first location the per-instance rows (the batch matrices) take; the mesh's own inputs are below it.</summary>
    const int RowLocation = BuildingLodShaders.InstanceLocation;

    /// <summary>This call's batch matrices in the frame's constants (a batch at its <see cref="Batch.Offset"/>; the draws reach it by firstInstance).</summary>
    Transient instances;

    /// <summary>What a part draw sets besides its textures (<see cref="ObjProg"/> keeps the last, so a batch of the same material sets nothing).</summary>
    readonly record struct ObjMaterial(bool Swizzled, bool HasDiffuse, bool HasNormal, bool HasDual, bool Triplanar, float TileX, float TileY, int AlphaSource,
        int AlphaChannel, int GreyChannel, Vector3 Tint, float AlphaThreshold, bool Emissive, bool UseVertexColour, float Specular);

    struct ObjDraw
    {
        public GpuObjectPart Part;
        public int Level;
        public uint FirstInstance, Instances;
        public uint Diffuse, Normal, Diffuse2, Normal2;
        public ObjMaterial Material;
        /// <summary>Wireframe or LOD-debug colouring: <see cref="FlatColour"/> instead of the material.</summary>
        public bool Flat;
        public Vector3 FlatColour;
        /// <summary>uFadeMode: 1 for the distant towns (the fade comes per vertex), else 0.</summary>
        public int Fade;
    }

    /// <summary>The objects' mesh shader (colour or depth fragment), with every handle resolved at load. The constants GL set on every draw are
    /// set once: a program's default block keeps its values (docs/renderer-native.md 3.2).</summary>
    sealed class ObjProg
    {
        public readonly LegacyProgram P;
        public readonly UniformHandle ViewProjection, Eye, LightDir, FogColour, FogDistance, FadeEye, FadeRange, FadeMode, Wireframe, FlatColour, NormalSwizzled,
            HasDiffuse, HasNormal, HasDual, Triplanar, Tile, AlphaSource, AlphaChannel, GreyChannel, Tint, AlphaThreshold, Emissive, UseVertexColour, Specular;
        public readonly SamplerSlot[] Slots;
        public readonly SamplerInfo?[] Infos;
        public readonly int Own;
        public UnitSamplers? Units;
        public ObjMaterial Material;
        public bool HasMaterial, HasFlat;
        public Vector3 Flat;
        public int Wire = -1, Fade = -1;
        public readonly uint[] Bound = new uint[4];

        public ObjProg(GpuContext gpu, string vertex, string fragment, string name)
        {
            P = LegacyProgram.Create(gpu, vertex, fragment, name);
            ViewProjection = P.Uniform("uViewProjection"); Eye = P.Uniform("uEye"); LightDir = P.Uniform("uLightDir"); FogColour = P.Uniform("uFogColour");
            FogDistance = P.Uniform("uFogDistance"); FadeEye = P.Uniform("uFadeEye"); FadeRange = P.Uniform("uFadeRange"); FadeMode = P.Uniform("uFadeMode");
            Wireframe = P.Uniform("uWireframe"); FlatColour = P.Uniform("uFlatColour"); NormalSwizzled = P.Uniform("uNormalSwizzled");
            HasDiffuse = P.Uniform("uHasDiffuse"); HasNormal = P.Uniform("uHasNormal"); HasDual = P.Uniform("uHasDual"); Triplanar = P.Uniform("uTriplanar");
            Tile = P.Uniform("uTile"); AlphaSource = P.Uniform("uAlphaSource"); AlphaChannel = P.Uniform("uAlphaChannel"); GreyChannel = P.Uniform("uGreyChannel");
            Tint = P.Uniform("uTint"); AlphaThreshold = P.Uniform("uAlphaThreshold"); Emissive = P.Uniform("uEmissive");
            UseVertexColour = P.Uniform("uUseVertexColour"); Specular = P.Uniform("uSpecular");
            Slots = [P.Sampler("uDiffuse"), P.Sampler("uNormal"), P.Sampler("uDiffuse2"), P.Sampler("uNormal2")];
            Infos = [.. Slots.Select(s => s.IsValid ? P.SamplerInfo(s) : (SamplerInfo?)null)];
            Own = P.InputLocations.Where(l => l < RowLocation).DefaultIfEmpty(-1).Max() + 1;
            P.Set(P.Uniform("uTriplanarScale"), 1f / 5000);
            P.Set(P.Uniform("uSkinned"), 0);
            P.Set(P.Uniform("uHasHead"), 0);
        }
    }

    /// <summary>The samplers of a program that read a GL unit (not a frame global, not one of the draw's own textures), resolved once per
    /// <see cref="FrameGlobals.Version"/>.</summary>
    sealed record UnitSamplers(int GlobalsVersion, (SamplerSlot Slot, int Unit, SamplerInfo Info)[] Samplers);

    /// <summary>The units the GL code pointed the shared mesh shader's head textures at (nothing is bound there; the stand-ins are read).</summary>
    static readonly Dictionary<string, int> UnitOf = new() { ["uHeadDiffuse"] = 4, ["uHeadNormal"] = 5 };
    static readonly string[] Direct = ["uDiffuse", "uNormal", "uDiffuse2", "uNormal2"];

    void BindUnitSamplers(ObjProg prog)
    {
        var p = prog.P;
        var interop = Gpu.Interop!;
        if (prog.Units is null || prog.Units.GlobalsVersion != Gpu.Globals.Version)
            prog.Units = new UnitSamplers(Gpu.Globals.Version, [.. p.SamplerNames.Where(n => Array.IndexOf(Direct, n) < 0 && Gpu.Globals.Texture(n) is null)
                .Select(n => (p.Sampler(n), UnitOf.GetValueOrDefault(n, 0), p.SamplerInfo(p.Sampler(n))))]);
        foreach (var (slot, unit, info) in prog.Units.Samplers) p.Bind(slot, interop.SampledUnit(unit, info));
    }

    /// <summary>The pipeline state a segment's draws share (everything of <see cref="GraphicsPipelineDesc"/> but the vertex layout).</summary>
    readonly record struct SegmentPipeline(LegacyProgram Program, AttachmentFormats Formats, BlendState Blend, Silk.NET.Vulkan.ColorComponentFlags Mask,
        Silk.NET.Vulkan.PolygonMode Polygon, bool AlphaToCoverage, bool DepthClamp);

    readonly Dictionary<(int Kind, SegmentPipeline State), int> segmentByState = [];
    readonly SegmentPipeline[] lastSegment = new SegmentPipeline[4];
    readonly int[] segmentIds = new int[4];
    int segmentCount;

    /// <summary>A stable number for a segment's pipeline state, so a part compares one int per draw.</summary>
    int SegmentId(int kind, LegacyProgram p, PassTargets t, DrawState s)
    {
        var segment = new SegmentPipeline(p, t.Formats, s.Blend, s.ColourMask, s.Polygon, s.AlphaToCoverage, s.DepthClamp);
        if (segmentIds[kind] != 0 && segment == lastSegment[kind]) return segmentIds[kind];
        if (!segmentByState.TryGetValue((kind, segment), out int id)) segmentByState[(kind, segment)] = id = ++segmentCount;
        lastSegment[kind] = segment;
        return segmentIds[kind] = id;
    }

    /// <summary>A part's vertex layout and buffers for <paramref name="prog"/> from its vertex array's export, with the batch matrices as four
    /// per-instance rows of 64 bytes at locations 7 to 10 (bound once per segment).</summary>
    static void ResolveVertices(ref ObjectNativeMesh n, ObjProg prog, VertexArrayBindings va)
    {
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        va.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[RowLocation + a] = new LegacyProgram.Attribute(default, Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, InstanceStride, true);
        n.Layout = prog.P.VertexLayout(attributes);
        n.Vertices = prog.P.VertexBuffers(attributes, 0, prog.Own);
        n.Elements = va.Elements;
        n.Source = va;
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    GraphicsPipeline PipelineFor(ref ObjectNativeMesh n, LegacyProgram p, int segment, DrawState state, AttachmentFormats formats, string label)
    {
        if (n.SegB == segment) return n.PipeB!;
        var pipeline = Gpu.Pipelines.Get(state.Pipeline(p.Program, n.Layout!, Silk.NET.Vulkan.PrimitiveTopology.TriangleList, formats, label));
        (n.SegB, n.PipeB) = (n.SegA, n.PipeA);
        (n.SegA, n.PipeA) = (segment, pipeline);
        return pipeline;
    }

    readonly List<ObjDraw> draws = [];

    /// <summary>The call's per-call uniforms, as the GL version set them before its draws (the atmosphere's through the frame globals).</summary>
    static void SetFrameUniforms(ObjProg prog, in Matrix4x4 viewProjection, Vector3 eye, Vector3 light, Vector3 fogColour, float fogDistance, float r0, float r1, float r2, float r3)
    {
        var p = prog.P;
        p.Set(prog.ViewProjection, in viewProjection);
        p.Set(prog.Eye, eye.X, eye.Y, eye.Z);
        p.Set(prog.LightDir, light.X, light.Y, light.Z);
        p.Set(prog.FogColour, fogColour.X, fogColour.Y, fogColour.Z);
        p.Set(prog.FogDistance, fogDistance);
        p.Set(prog.FadeEye, eye.X, eye.Y, eye.Z);
        p.Set(prog.FadeRange, r0, r1, r2, r3);
        p.ApplyGlobals();   // the atmosphere's uniforms (SkyRenderer.Apply's), through the frame globals
    }

    /// <summary>The batches' parts as draws, in order, with the texture ids the GL code bound (reading an id counts as use: here, not in Record).</summary>
    void PrepareDraws(WorldRenderOptions options, bool wire)
    {
        draws.Clear();
        foreach (var b in active)
        {
            int fade = b.Town ? 1 : 0;
            for (int i = 0; i < b.Mesh.Parts.Count; i++)
            {
                var gp = b.Mesh.Parts[i];
                if (gp.Count[b.Level] == 0) continue;
                var d = new ObjDraw { Part = gp, Level = b.Level, FirstInstance = (uint)b.Offset, Instances = (uint)b.Count, Fade = fade };
                if (wire || debugLevels == 1)
                {
                    d.Flat = true;
                    d.FlatColour = LevelColour(b.Level, b.Manual, ReferenceEquals(b.Materials, distantMaterial));
                }
                else Material(ref d, gp, b.Materials.Parts[Math.Min(i, b.Materials.Parts.Length - 1)], options);
                draws.Add(d);
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
        d.Material = new ObjMaterial(pm.Swizzled, textured, normal, dual, textured && (m?.Triplanar ?? false), tile.X, tile.Y, (int)alpha,
            Math.Clamp(m?.AlphaChannel ?? 3, 0, 3), textured ? m?.GreyChannel ?? -1 : -1, m?.Tint ?? Vector3.One, alpha == AlphaSource.None ? 0f : m!.AlphaThreshold,
            textured && normalId != 0 && (m?.Emissive ?? false), gp.HasColours && (m?.VertexColours ?? false), textured ? m?.SpecularMult ?? 1 : 0.3f);
    }

    /// <summary>The draw list in one native segment of VkGl's open pass (colour or depth, solid or wireframe).</summary>
    void RecordDraws(ObjProg prog, bool wire)
    {
        if (draws.Count == 0) return;
        var p = prog.P;
        BindUnitSamplers(prog);
        var interop = Gpu.Interop!;
        string label = depthPass ? "objects depth" : wire ? "objects wire" : "objects";
        int kind = (depthPass ? 1 : 0) + (wire ? 2 : 0);
        var cmd = interop.BeginNativeInPass(label);
        var targets = interop.CurrentTargets();
        var state = interop.CurrentState();
        int segment = SegmentId(kind, p, targets, state);
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
        Span<BufferBinding> rows = stackalloc BufferBinding[4];
        for (int a = 0; a < 4; a++) rows[a] = new BufferBinding(instances.Handle, instances.Offset + (ulong)(16 * a));
        cmd.BindVertexBuffers(RowLocation, rows);
        Array.Fill(prog.Bound, uint.MaxValue);   // what a texture is (its view, sampler) can change between segments
        foreach (ref readonly var d in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(draws))
        {
            if (prog.Fade != d.Fade) { p.Set(prog.FadeMode, d.Fade); prog.Fade = d.Fade; }
            if (d.Flat)
            {
                if (prog.Wire != 1) { p.Set(prog.Wireframe, 1); prog.Wire = 1; }
                if (!prog.HasFlat || prog.Flat != d.FlatColour) { p.Set(prog.FlatColour, d.FlatColour); prog.Flat = d.FlatColour; prog.HasFlat = true; }
            }
            else
            {
                if (prog.Wire != 0) { p.Set(prog.Wireframe, 0); prog.Wire = 0; }
                if (!prog.HasMaterial || prog.Material != d.Material)
                {
                    var k = d.Material;
                    p.Set(prog.NormalSwizzled, k.Swizzled ? 1 : 0);
                    p.Set(prog.HasDiffuse, k.HasDiffuse ? 1 : 0);
                    p.Set(prog.HasNormal, k.HasNormal ? 1 : 0);
                    p.Set(prog.HasDual, k.HasDual ? 1 : 0);
                    p.Set(prog.Triplanar, k.Triplanar ? 1 : 0);
                    p.Set(prog.Tile, k.TileX, k.TileY);
                    p.Set(prog.AlphaSource, k.AlphaSource);
                    p.Set(prog.AlphaChannel, k.AlphaChannel);
                    p.Set(prog.GreyChannel, k.GreyChannel);
                    p.Set(prog.Tint, k.Tint);
                    p.Set(prog.AlphaThreshold, k.AlphaThreshold);
                    p.Set(prog.Emissive, k.Emissive ? 1 : 0);
                    p.Set(prog.UseVertexColour, k.UseVertexColour ? 1 : 0);
                    p.Set(prog.Specular, k.Specular);
                    prog.Material = k;
                    prog.HasMaterial = true;
                }
                BindMaterialTexture(interop, prog, 0, d.Diffuse);
                BindMaterialTexture(interop, prog, 1, d.Normal);
                BindMaterialTexture(interop, prog, 2, d.Diffuse2);
                BindMaterialTexture(interop, prog, 3, d.Normal2);
            }
            var part = d.Part;
            var va = interop.VertexArray(part.Vao);
            ref var n = ref (depthPass ? ref part.DepthNative : ref part.ColourNative);
            if (!ReferenceEquals(n.Source, va)) ResolveVertices(ref n, prog, va);
            cmd.BindPipeline(n.SegA == segment ? n.PipeA! : PipelineFor(ref n, p, segment, state, targets.Formats, label));
            cmd.BindVertexBuffers(0, n.Vertices);
            cmd.BindIndexBuffer(new BufferBinding(n.Elements.Buffer, n.Elements.Offset + (ulong)part.Offset[d.Level] * 4), Silk.NET.Vulkan.IndexType.Uint32);
            p.Flush(cmd);
            cmd.DrawIndexed((uint)part.Count[d.Level], d.Instances, 0, 0, d.FirstInstance);
        }
        interop.EndNative(cmd);
        gl.BindVertexArray(0);
    }

    void BindMaterialTexture(IGlInterop interop, ObjProg prog, int slot, uint id)
    {
        if (prog.Bound[slot] == id) return;
        prog.Bound[slot] = id;
        if (prog.Infos[slot] is { } info) prog.P.Bind(prog.Slots[slot], interop.Sampled(id, info));
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
        colourProg.P.Dispose();
        depthProg.P.Dispose();
        streamer.Dispose();
        meshes.Dispose();
        textureCache.Dispose();
        gl.DeleteBuffer(instanceBuffer);
        objects.Dispose();
    }
}
