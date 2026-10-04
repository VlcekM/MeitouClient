using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

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

    readonly GL gl;
    readonly GameInstall install;
    readonly GameDatabase db;
    readonly WorldLevelData levels;
    readonly AssetLocator assets;
    readonly FoliageCatalog catalog;
    readonly uint meshProgram, grassProgram;
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
    readonly int workers;
    /// <summary>MEITOU_FOLIAGE_DEBUG: "nograss" or "nomeshes" leaves that part out (to measure the other).</summary>
    readonly bool debugNoGrass = Environment.GetEnvironmentVariable("MEITOU_FOLIAGE_DEBUG") == "nograss", debugNoMeshes = Environment.GetEnvironmentVariable("MEITOU_FOLIAGE_DEBUG") == "nomeshes";
    long instanceBufferSize;
    int running;

    public FoliageRenderer(GL gl, GameInstall install, GameDatabase db, WorldLevelData levels, AssetLocator assets)
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
        textures = new WorldTextureCache(gl, assets);
        instanceBuffer = gl.GenBuffer();
        workers = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
        var used = catalog.ByBiome.Values.SelectMany(l => l).Distinct().ToList();
        MeshRange = used.Where(l => !l.IsGrass).Select(l => l.Range).DefaultIfEmpty(0).Max();
        LoadMs = watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Foliage drawn at all (the F key).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>settings.cfg <c>foliage range</c>, <c>grass range</c> and <c>grass density</c> (the game's defaults are 1).</summary>
    public float RangeSetting { get; set; } = 1;
    public float GrassRangeSetting { get; set; } = 1;
    public float GrassDensitySetting { get; set; } = 1;

    /// <summary>The longest mesh layer range in the catalog at setting 1 (zones are laid out up to it).</summary>
    public float MeshRange { get; }
    public double LoadMs { get; }
    public int DrawnInstances { get; private set; }
    public int DrawnBlades { get; private set; }
    public int DrawCalls { get; private set; }
    public double LastDrawCpuMs { get; private set; }
    /// <summary>GPU time of the foliage draws of one frame (the main camera's draw, grass included), from timestamp queries a frame or two old.</summary>
    public double GpuMs { get; private set; }
    public double LastUpdateMs { get; private set; }
    public List<string> Messages { get; } = [];

    /// <summary>Work in flight: zones being laid out, grass pages, meshes decoding or uploading, textures decoding.</summary>
    public int Pending => running + decoding.Count + uploads.Count + textures.PendingCount + zones.Values.Sum(z => z.GrassRunning);

    public string Describe() =>
        $"{zones.Values.Count(z => z.Ready)} zones laid out ({zones.Values.Where(z => z.Ready).Sum(z => z.Instances):N0} meshes, " +
        $"{zones.Values.Sum(z => z.Pages.Count):N0} grass pages), {assetsByMesh.Count} foliage meshes ({assetsByMesh.Values.Count(a => a.Resident)} resident), catalog of {catalog.Layers.Count} layers";

    // ------------------------------------------------------------------ streaming

    sealed class ZoneState
    {
        public required ZoneCoordinate Zone;
        public float X0, Z0;
        public Task<(FoliageZone, FoliageGround?)>? Job;
        public bool Ready;
        public List<Group> Groups = [];
        public FoliageGround? Ground;
        public List<FoliageGrassPatch> Patches = [];
        public readonly Dictionary<int, GrassPage> Pages = [];
        public int GrassRunning;
        public int Instances;
    }

    /// <summary>The instances of one mesh in one zone, with the range of the layer that placed them.</summary>
    sealed class Group
    {
        public required MeshAsset Asset;
        public required float Range, Band;
        public required Matrix4x4[] Transforms;
        public required Vector3[] Positions;
        public required float[] Scales;
    }

    sealed class GrassPage
    {
        public Task<float[][]>? Job;
        public GrassBuffer[]? Buffers;
    }

    sealed class GrassBuffer
    {
        public uint Vao, Vbo;
        public int Count;
    }

    /// <summary>Follows the eye: lays out zones, decodes meshes, makes grass pages and uploads, within a frame budget.</summary>
    public void Update(Vector3 eye) => Update(eye, settling: false);

    void Update(Vector3 eye, bool settling)
    {
        var watch = Stopwatch.StartNew();
        // Uploads get 2 ms a frame (texture decodes are never waited for) except while settling for a screenshot.
        double budget = settling ? 1e9 : 2.0;
        float meshRange = MeshRange * RangeSetting;

        // Zones in range: start the nearest missing ones, drop far ones.
        var wanted = new List<(ZoneCoordinate Zone, float Distance)>();
        var centre = WorldLayout.ZoneOf(eye.X, eye.Z);
        int reach = (int)Math.Ceiling(meshRange / WorldLayout.ZoneSize) + 1;
        for (int dz = -reach; dz <= reach; dz++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                var z = new ZoneCoordinate(centre.X + dx, centre.Y + dz);
                if (!z.IsInsideGrid) continue;
                float d = ZoneDistance(z, eye);
                if (d <= meshRange) wanted.Add((z, d));
            }
        foreach (var (zone, _) in wanted.OrderBy(w => w.Distance))
        {
            if (zones.ContainsKey(zone)) continue;
            if (running >= workers) break;
            var state = new ZoneState { Zone = zone };
            var (x0, z0) = WorldLayout.ZoneOrigin(zone);
            (state.X0, state.Z0) = ((float)x0, (float)z0);
            running++;
            state.Job = Task.Run(() =>
            {
                if (!worlds.TryTake(out var world))
                {
                    world = new FoliageWorld(install, db, levels, catalog);
                    lock (allWorlds) allWorlds.Add(world);
                }
                try { return world.Load(zone); }
                finally { worlds.Add(world); }
            });
            zones[zone] = state;
        }
        foreach (var state in zones.Values.ToList())
        {
            if (state.Job is { IsCompleted: true } job)
            {
                running--;
                state.Job = null;
                try { Accept(state, job.Result); }
                catch (AggregateException e) { Messages.Add($"foliage zone {state.Zone}: {e.InnerException?.Message ?? e.Message}"); state.Ready = true; }
            }
            if (state.Job is null && ZoneDistance(state.Zone, eye) > meshRange + WorldLayout.ZoneSize) Drop(state);
        }

        UpdateGrass(eye);
        PumpMeshes();
        textures.Pump(wait: settling, max: settling ? 64 : 1);
        while (uploads.Count > 0 && watch.Elapsed.TotalMilliseconds < budget) uploads.Dequeue()();
        lock (textures.Messages)
        {
            foreach (var m in Messages.Concat(textures.Messages).Distinct().Take(20)) Console.WriteLine($"warning   {m}");
            Messages.Clear();
            textures.Messages.Clear();
        }
        PollTimers(wait: false);
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

    void Accept(ZoneState state, (FoliageZone Zone, FoliageGround? Ground) result)
    {
        var (zone, ground) = result;
        foreach (var g in zone.Instances.GroupBy(i => (i.Mesh, i.Layer)))
        {
            var asset = AssetFor(g.Key.Mesh);
            var list = g.ToList();
            float range = g.Key.Layer.Range * RangeSetting;
            state.Groups.Add(new Group
            {
                Asset = asset,
                Range = range,
                // The game's transition is 10 units (100 for wind layers): too short to see; a tenth of the range instead.
                Band = Math.Max(g.Key.Layer.Transition, range * 0.1f),
                Transforms = [.. list.Select(i => i.Transform)],
                Positions = [.. list.Select(i => i.Position)],
                Scales = [.. list.Select(i => i.Scale)],
            });
        }
        state.Instances = zone.Instances.Count;
        state.Ground = ground;
        state.Patches = zone.Grass.Where(p => p.Grass.Sprite is not null && p.Density.Any(d => d != 0)).ToList();
        foreach (var p in state.Patches)
        {
            textures.Get(p.Grass.Sprite, false);   // start decoding now, not at the first draw
            textures.Get(p.Grass.ColourMap, false);
        }
        state.Ready = true;
    }

    void Drop(ZoneState state)
    {
        foreach (var page in state.Pages.Values) FreePage(page);
        state.Pages.Clear();
        zones.Remove(state.Zone);
    }

    void FreePage(GrassPage page)
    {
        if (page.Buffers is null) return;
        foreach (var b in page.Buffers)
        {
            if (b.Vao != 0) gl.DeleteVertexArray(b.Vao);
            if (b.Vbo != 0) gl.DeleteBuffer(b.Vbo);
        }
        page.Buffers = null;
    }

    float GrassRange(FoliageGrassPatch patch) => patch.Layer.Range * GrassRangeSetting;

    void UpdateGrass(Vector3 eye)
    {
        foreach (var state in zones.Values)
        {
            if (!state.Ready || state.Ground is null || state.Patches.Count == 0) continue;
            float range = state.Patches.Max(GrassRange);
            for (int pz = 0; pz < PagesPerZone; pz++)
                for (int px = 0; px < PagesPerZone; px++)
                {
                    int key = pz * PagesPerZone + px;
                    float x0 = state.X0 + px * PageSize, z0 = state.Z0 + pz * PageSize;
                    float d = BoxDistance(x0, z0, PageSize, eye);
                    state.Pages.TryGetValue(key, out var page);
                    if (page is null && d < range + PageSize * 0.5f && state.GrassRunning < 4)
                    {
                        page = state.Pages[key] = new GrassPage();
                        var patches = state.Patches;
                        var ground = state.Ground;
                        float density = GrassDensitySetting;
                        state.GrassRunning++;
                        page.Job = Task.Run(() => patches.Select(p => FoliageGrassField.Blades(p, ground, x0, z0, PageSize, density)).ToArray());
                    }
                    else if (page is not null && page.Job is null && d > range + PageSize * 2)
                    {
                        FreePage(page);
                        state.Pages.Remove(key);
                    }
                    if (page?.Job is { IsCompleted: true } job)
                    {
                        state.GrassRunning--;
                        page.Job = null;
                        var blades = job.Result;
                        var p = page;
                        uploads.Enqueue(() => UploadPage(p, blades));
                    }
                }
        }
    }

    void UploadPage(GrassPage page, float[][] blades)
    {
        var buffers = new GrassBuffer[blades.Length];
        for (int i = 0; i < blades.Length; i++)
        {
            var b = buffers[i] = new GrassBuffer { Count = blades[i].Length / FoliageGrassField.Stride };
            if (b.Count == 0) continue;
            b.Vao = gl.GenVertexArray();
            b.Vbo = gl.GenBuffer();
            gl.BindVertexArray(b.Vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, b.Vbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, blades[i].AsSpan(), BufferUsageARB.StaticDraw);
            uint stride = FoliageGrassField.Stride * 4;
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 4, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.VertexAttribDivisor(0, 1);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, (void*)16);
            gl.VertexAttribDivisor(1, 1);
        }
        gl.BindVertexArray(0);
        page.Buffers = buffers;
    }

    // ------------------------------------------------------------------ meshes and materials

    sealed class MeshAsset
    {
        public required FoliageMesh Mesh;
        public Task<(Model? Main, Model? Leaves)>? Job;
        public GpuMesh? Main, Leaves;
        public bool Resident, Failed;
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
        a.Job = Task.Run(() => (Decode(main), leaves.Length > 0 ? Decode(leaves) : null));
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
            uploads.Enqueue(() =>
            {
                a.Main = Upload(main);
                if (leaves is not null) a.Leaves = Upload(leaves);
                a.Resident = true;
            });
        }
    }

    GpuMesh Upload(Model model)
    {
        var mesh = new GpuMesh();
        foreach (var part in model.Parts)
        {
            if (part.Indices.Length == 0) continue;
            var gp = new GpuPart { Vbo = gl.GenBuffer(), Ebo = gl.GenBuffer(), Count = part.Indices.Length, HasTangents = part.HasTangents, HasColours = part.HasColours };
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gp.Vbo);
            gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer, part.Vertices.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gp.Ebo);
            gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, part.Indices.AsSpan(), BufferUsageARB.StaticDraw);
            gp.Vao = Vao(gp, instanced: true);
            gp.PlainVao = Vao(gp, instanced: false);
            mesh.Parts.Add(gp);
        }
        gl.BindVertexArray(0);
        return mesh;
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

    /// <summary>Draws the foliage seen from <paramref name="eye"/> through <paramref name="frustum"/>. Without <paramref name="grass"/> only the meshes (e.g. for a reflection).</summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, Vector3 light, Vector3 fogColour, float fogDistance, TerrainRenderer terrain, bool grass = true)
    {
        DrawnInstances = 0;
        DrawnBlades = 0;
        DrawCalls = 0;
        if (!Enabled) return;
        int timer = grass ? BeginTimer() : -1;
        var cpu = Stopwatch.StartNew();
        foreach (var b in batches.Values) b.Count = 0;
        active.Clear();
        terrainDraws.Clear();

        // 1. Cull: each instance by its distance along the ground (the game's pages) and its bounding sphere.
        var eyeXz = new Vector2(eye.X, eye.Z);
        foreach (var state in zones.Values)
        {
            if (!state.Ready) continue;
            float zoneDistance = ZoneDistance(state.Zone, eye);
            foreach (var g in state.Groups)
            {
                var a = g.Asset;
                if (!a.Resident || zoneDistance > g.Range) continue;
                for (int i = 0; i < g.Positions.Length; i++)
                {
                    var p = g.Positions[i];
                    float d = Vector2.Distance(new Vector2(p.X, p.Z), eyeXz);
                    if (d >= g.Range) continue;
                    ref var t = ref g.Transforms[i];
                    var centre = Vector3.Transform(a.Centre, t);
                    if (!SphereVisible(frustum, centre, a.Radius * g.Scales[i])) continue;
                    float w = Math.Clamp((g.Range - d) / g.Band, 0, 1);
                    DrawnInstances++;
                    if (a.Terrain && options.Textures)
                    {
                        if (w < 0.5f) continue;   // the terrain shader has no dither: the instance goes at the middle of the fade
                        foreach (var gp in a.Main!.Parts) terrainDraws.Add((gp.PlainVao, gp.Count, t));
                        continue;
                    }
                    if (!batches.TryGetValue(a, out var batch)) batches[a] = batch = new Batch { Asset = a };
                    if (batch.Count == 0) active.Add(batch);
                    var m = t;
                    m.M14 = w >= 0.999f ? 2 : w;
                    batch.Add(m);
                }
            }
        }

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

        // 4. Grass.
        if (grass && !debugNoGrass) DrawGrass(viewProjection, eye, frustum, options, light, fogColour, fogDistance, coverage);
        if (coverage) gl.Disable(EnableCap.SampleAlphaToCoverage);
        gl.Disable(EnableCap.CullFace);
        gl.ActiveTexture(TextureUnit.Texture0);

        // 5. TERRAIN-mode rocks through the terrain's own mesh path.
        if (terrainDraws.Count > 0)
        {
            terrain.DrawMeshes(terrainDraws);
            DrawCalls += terrainDraws.Count;
        }
        gl.Disable(EnableCap.CullFace);
        EndTimer(timer);
        LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds;
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
        float time = (float)(clock.Elapsed.TotalSeconds * 0.3 * Math.PI % (2 * Math.PI));
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
                    if (b.Count == 0 || d >= range) continue;
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
                    gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, g.CrossQuads ? 12u : 6u, (uint)b.Count);
                    DrawCalls++;
                    DrawnBlades += b.Count;
                }
            }
        }
        gl.BindVertexArray(0);
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
        public bool Pending;
    }

    readonly List<Timer> timers = [];
    double gpuSum;
    int gpuCount;

    int BeginTimer()
    {
        int index = timers.FindIndex(t => !t.Pending);
        if (index < 0)
        {
            if (timers.Count >= 16) return -1;
            timers.Add(new Timer { Start = gl.GenQuery(), End = gl.GenQuery() });
            index = timers.Count - 1;
        }
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
            gpuCount++;
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
        gl.DeleteProgram(grassProgram);
        foreach (var q in timers) { gl.DeleteQuery(q.Start); gl.DeleteQuery(q.End); }
    }
}
