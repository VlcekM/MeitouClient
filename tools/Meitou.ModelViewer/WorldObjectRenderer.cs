using System.Diagnostics;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

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
    readonly GL gl;
    uint program;   // the main program; the depth program while DrawDepth runs
    readonly Dictionary<(uint, string), int> uniforms = [];
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
    readonly Vector4[] uniformCache = new Vector4[256];
    readonly bool[] uniformKnown = new bool[256];
    readonly uint[] boundTextures = new uint[4];
    /// <summary>MEITOU_LOD_DEBUG: 1 colours solid surfaces by LOD level, 2 draws wireframe only coloured by level (green 0, yellow 1, orange 2, red 3, magenta manual, blue distant stand-ins).</summary>
    readonly int debugLevels = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_LOD_DEBUG"), out int dl) ? dl : 0;
    long instanceBufferSize;

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

    internal WorldObjectRenderer(GL gl, AssetLocator assets, WorldObjects objects)
    {
        this.gl = gl;
        this.objects = objects;
        program = WorldGl.Program(gl, BuildingLodShaders.Vertex(), BuildingLodShaders.Fragment());
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
        var terrainMeshes = new List<(uint, int, Matrix4x4)>();

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

        // 2. Upload the instances: one buffer, each batch's matrices contiguous.
        int total = 0;
        foreach (var b in active) { b.Offset = total; total += b.Count; }
        long bytes = Math.Max(total, 1) * 64L;
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, instanceBuffer);
        instanceBufferSize = Math.Max(bytes, instanceBufferSize);
        gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)instanceBufferSize, null, BufferUsageARB.StreamDraw);
        foreach (var b in active)
            fixed (Matrix4x4* p = b.Data)
                gl.BufferSubData(BufferTargetARB.ArrayBuffer, (nint)(b.Offset * 64L), (nuint)(b.Count * 64L), p);

        // 3. Draw.
        gl.UseProgram(program);
        Array.Clear(uniformKnown);
        Array.Fill(boundTextures, uint.MaxValue);
        WorldGl.Matrix(gl, U("uViewProjection"), viewProjection);
        gl.Uniform3(U("uEye"), eye.X, eye.Y, eye.Z);
        gl.Uniform3(U("uLightDir"), light.X, light.Y, light.Z);
        gl.Uniform3(U("uFogColour"), fogColour.X, fogColour.Y, fogColour.Z);
        gl.Uniform1(U("uFogDistance"), fogDistance);
        gl.Uniform1(U("uTriplanarScale"), 1f / 5000);
        string[] samplers = ["uDiffuse", "uNormal", "uDiffuse2", "uNormal2", "uHeadDiffuse", "uHeadNormal"];
        for (int i = 0; i < samplers.Length; i++) gl.Uniform1(U(samplers[i]), i);
        gl.Uniform1(U("uSkinned"), 0);
        gl.Uniform1(U("uHasHead"), 0);
        gl.Uniform3(U("uFadeEye"), eye.X, eye.Y, eye.Z);
        gl.Uniform4(U("uFadeRange"), real - realBand, real, DistantRange - distantBand, DistantRange);
        SkyRenderer.Active?.Apply(program);   // the atmosphere's uniforms (aerial perspective, sky light), as the terrain does
        gl.Disable(EnableCap.CullFace);
        int wireMode = debugLevels == 2 ? 2 : options.Wireframe;
        if (wireMode != 2 && active.Count > 0) DrawBatches(options, wire: false);
        if (wireMode != 0 && active.Count > 0) DrawBatches(options, wire: true);
        gl.BindVertexArray(0);
        if (terrainMeshes.Count > 0) terrain.DrawMeshes(terrainMeshes, depthPass);
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(0);
        LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds;
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

    void DrawBatches(WorldRenderOptions options, bool wire)
    {
        Set1(U("uWireframe"), 0);
        if (wire)
        {
            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            gl.Enable(EnableCap.PolygonOffsetLine);
            gl.PolygonOffset(-1, -1);
        }
        int mode = -1;
        foreach (var b in active)
        {
            if (mode != (b.Town ? 1 : 0))
            {
                mode = b.Town ? 1 : 0;
                gl.Uniform1(U("uFadeMode"), mode);
            }
            for (int i = 0; i < b.Mesh.Parts.Count; i++)
            {
                var gp = b.Mesh.Parts[i];
                if (gp.Count[b.Level] == 0) continue;
                if (wire || debugLevels == 1)
                {
                    Set1(U("uWireframe"), 1);
                    Set3(U("uFlatColour"), LevelColour(b.Level, b.Manual, ReferenceEquals(b.Materials, distantMaterial)));
                }
                else SetMaterial(gp, b.Materials.Parts[Math.Min(i, b.Materials.Parts.Length - 1)], options);
                gl.BindVertexArray(gp.Vao);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, instanceBuffer);
                for (uint a = 0; a < 4; a++)
                    gl.VertexAttribPointer(BuildingLodShaders.InstanceLocation + a, 4, VertexAttribPointerType.Float, false, 64, (void*)(b.Offset * 64L + 16 * a));
                gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)gp.Count[b.Level], DrawElementsType.UnsignedInt, (void*)(gp.Offset[b.Level] * 4L), (uint)b.Count);
                DrawCalls++;
                DrawnTriangles += (long)gp.Count[b.Level] / 3 * b.Count;
            }
        }
        if (wire)
        {
            gl.Disable(EnableCap.PolygonOffsetLine);
            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        }
    }

    void SetMaterial(GpuObjectPart gp, ObjectPartMaterial pm, WorldRenderOptions options)
    {
        var m = pm.Material;
        bool textured = options.Textures && pm.Diffuse != 0;
        Bind(0, textured ? pm.Diffuse : 0);
        bool normal = options.NormalMaps && textured && pm.Normal != 0 && gp.HasTangents;
        Bind(1, textured ? pm.Normal : 0);
        bool dual = textured && pm.Diffuse2 != 0 && gp.HasColours;
        Bind(2, dual ? pm.Diffuse2 : 0);
        Bind(3, dual ? pm.Normal2 : 0);
        Set1(U("uNormalSwizzled"), pm.Swizzled ? 1 : 0);
        Set1(U("uWireframe"), 0);
        Set1(U("uHasDiffuse"), textured ? 1 : 0);
        Set1(U("uHasNormal"), normal ? 1 : 0);
        Set1(U("uHasDual"), dual ? 1 : 0);
        Set1(U("uTriplanar"), textured && (m?.Triplanar ?? false) ? 1 : 0);
        Set2(U("uTile"), m?.Tile ?? Vector2.One);
        var alpha = m?.Alpha ?? AlphaSource.None;
        if (!textured || alpha == AlphaSource.NormalAlpha && pm.Normal == 0) alpha = AlphaSource.None;
        Set1(U("uAlphaSource"), (int)alpha);
        Set1(U("uAlphaChannel"), Math.Clamp(m?.AlphaChannel ?? 3, 0, 3));
        Set1(U("uGreyChannel"), textured ? m?.GreyChannel ?? -1 : -1);
        Set3(U("uTint"), m?.Tint ?? Vector3.One);
        Set1(U("uAlphaThreshold"), alpha == AlphaSource.None ? 0f : m!.AlphaThreshold);
        Set1(U("uEmissive"), textured && pm.Normal != 0 && (m?.Emissive ?? false) ? 1 : 0);
        Set1(U("uUseVertexColour"), gp.HasColours && (m?.VertexColours ?? false) ? 1 : 0);
        Set1(U("uSpecular"), textured ? m?.SpecularMult ?? 1 : 0.3f);
    }

    // Uniform setters that skip a call when the value is the one already set (the program is only used here).
    void Set1(int loc, float v) { if (Changed(loc, new Vector4(v, 0, 0, 0))) gl.Uniform1(loc, v); }
    void Set1(int loc, int v) { if (Changed(loc, new Vector4(v, 0, 0, 0))) gl.Uniform1(loc, v); }
    void Set2(int loc, Vector2 v) { if (Changed(loc, new Vector4(v, 0, 0))) gl.Uniform2(loc, v.X, v.Y); }
    void Set3(int loc, Vector3 v) { if (Changed(loc, new Vector4(v, 0))) gl.Uniform3(loc, v.X, v.Y, v.Z); }

    bool Changed(int loc, Vector4 value)
    {
        if (loc < 0) return false;
        if (loc >= uniformCache.Length) return true;
        if (uniformKnown[loc] && uniformCache[loc] == value) return false;
        uniformCache[loc] = value;
        uniformKnown[loc] = true;
        return true;
    }

    void Bind(int unit, uint texture)
    {
        if (boundTextures[unit] == texture) return;
        boundTextures[unit] = texture;
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    int U(string name)
    {
        if (!uniforms.TryGetValue((program, name), out int location)) uniforms[(program, name)] = location = gl.GetUniformLocation(program, name);
        return location;
    }

    // ---- depth only (the sun's shadow map, ShadowPass) ----
    uint depthProgram;
    bool depthPass;

    /// <summary>
    /// Draws the objects' depth for a shadow cascade: <see cref="Draw"/>'s culling, levels and batches (chosen by the camera's
    /// <paramref name="eye"/>) with <see cref="ShadowShaders.MeshDepthFragment"/> (the materials' cut-outs and the caster bias), and the
    /// TERRAIN-mode meshes through the terrain's depth path. Leaves the draw counters describing this call.
    /// </summary>
    public void DrawDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, TerrainRenderer terrain)
    {
        if (depthProgram == 0) depthProgram = WorldGl.Program(gl, BuildingLodShaders.Vertex(), ShadowShaders.MeshDepthFragment);
        uint main = program;
        program = depthProgram;
        depthPass = true;
        try { Draw(viewProjection, eye, frustum, options, Vector3.UnitY, Vector3.Zero, 0, terrain); }
        finally { program = main; depthPass = false; }
    }

    public void Dispose()
    {
        if (depthProgram != 0) gl.DeleteProgram(depthProgram);
        streamer.Dispose();
        meshes.Dispose();
        textureCache.Dispose();
        gl.DeleteBuffer(instanceBuffer);
        gl.DeleteProgram(program);
        objects.Dispose();
    }
}
