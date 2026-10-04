using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

/// <summary>
/// Draws the placed meshes of a <see cref="WorldObjects"/> with the model viewer's shaders and material resolution:
/// one GPU copy per mesh file (loaded when an instance first comes within the object distance), one material set
/// per (mesh, chosen MATERIAL_SPEC) or (mesh, record) pair, instances beyond the distance or outside the frustum skipped. TERRAIN-mode map features
/// are drawn with the terrain material instead (<see cref="TerrainRenderer.DrawMeshes"/>).
/// </summary>
public sealed unsafe class WorldObjectRenderer : IDisposable
{
    readonly GL gl;
    readonly uint program;
    readonly Dictionary<string, int> uniforms = [];
    readonly WorldTextureCache textureCache;
    readonly MaterialResolver resolver;
    readonly AssetLocator assets;
    readonly List<Instance> instances = [];
    readonly Dictionary<string, GpuMesh?> meshes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string, string, string), PartMaterial[]> materials = [];

    sealed class GpuMesh
    {
        public required List<GpuPart> Parts;
        public Vector3 Center;
        public float Radius;
    }

    sealed class GpuPart
    {
        public required ModelPart Part;
        public required OgreSubMesh SubMesh;
        public uint Vao, Vbo, Ebo;
    }

    sealed record PartMaterial(SurfaceMaterial? Material, WorldTexture? DiffuseMap, WorldTexture? NormalMap, WorldTexture? Diffuse2Map, WorldTexture? Normal2Map)
    {
        public uint Diffuse => DiffuseMap?.Id ?? 0;
        public uint Normal => NormalMap?.Id ?? 0;
        public uint Diffuse2 => Diffuse2Map?.Id ?? 0;
        public uint Normal2 => Normal2Map?.Id ?? 0;
        public bool Swizzled => NormalMap?.Swizzled ?? false;
    }

    sealed class Instance
    {
        public required PlacedMesh Placed;
        public required bool TerrainMode;
        public Vector3 Center;
        public float Radius = 500;
        public bool Resolved;
        public GpuMesh? Mesh;
        public PartMaterial[]? Materials;
    }

    internal WorldObjectRenderer(GL gl, AssetLocator assets, WorldObjects objects)
    {
        this.gl = gl;
        this.assets = assets;
        program = WorldGl.Program(gl, Shaders.MeshVertex, Shaders.MeshFragment);
        textureCache = new WorldTextureCache(gl, assets);
        var library = OgreMaterialLibrary.LoadConfigured(objects.Install, out _);
        resolver = new MaterialResolver(objects.Database, library, assets);
        foreach (var p in objects.Items)
            instances.Add(new Instance
            {
                Placed = p,
                TerrainMode = p.Kind == PlacedKind.MapFeature && p.Source.GetInt("texture mode") == 2,
                Center = p.Transform.Translation,
            });
    }

    /// <summary>Meshes loaded per frame in interactive use (0 = no limit, for screenshots).</summary>
    public int LoadBudget { get; set; }
    public int DrawnInstances { get; private set; }
    public long DrawnTriangles { get; private set; }
    public float ObjectDistance { get; set; } = 12000;
    public List<string> Messages { get; } = [];

    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, Vector3 light, Vector3 fogColour, float fogDistance, TerrainRenderer terrain)
    {
        int loads = 0;
        var visible = new List<Instance>();
        foreach (var inst in instances)
        {
            if (Vector3.Distance(inst.Center, eye) - inst.Radius > ObjectDistance) continue;
            if (!inst.Resolved)
            {
                if (LoadBudget > 0 && loads >= LoadBudget) continue;
                Resolve(inst, ref loads);
            }
            if (inst.Mesh is null) continue;
            var r = new Vector3(inst.Radius);
            if (!WorldCamera.Intersects(frustum, inst.Center - r, inst.Center + r)) continue;
            visible.Add(inst);
        }
        textureCache.Pump(wait: LoadBudget == 0);
        lock (textureCache.Messages)
        {
            foreach (var m in Messages.Concat(textureCache.Messages).Take(30)) Console.WriteLine($"warning   {m}");
            Messages.Clear();
            textureCache.Messages.Clear();
        }

        gl.UseProgram(program);
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
        gl.Uniform3(U("uFlatColour"), 0.1f, 0.1f, 0.1f);
        DrawnInstances = 0;
        DrawnTriangles = 0;
        var terrainMeshes = new List<(uint, int, Matrix4x4)>();
        foreach (var inst in visible)
        {
            DrawnInstances++;
            if (inst.TerrainMode && options.Textures)
            {
                foreach (var gp in inst.Mesh!.Parts) terrainMeshes.Add((gp.Vao, gp.Part.Indices.Length, inst.Placed.Transform));
                continue;
            }
            WorldGl.Matrix(gl, U("uModel"), inst.Placed.Transform);
            for (int i = 0; i < inst.Mesh!.Parts.Count; i++) DrawPart(inst.Mesh.Parts[i], inst.Materials![i], options);
        }
        foreach (var (_, n, _) in terrainMeshes) DrawnTriangles += n / 3;
        if (terrainMeshes.Count > 0) terrain.DrawMeshes(terrainMeshes);
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(0);
    }

    void DrawPart(GpuPart gp, PartMaterial pm, WorldRenderOptions options)
    {
        var m = pm.Material;
        bool textured = options.Textures && pm.Diffuse != 0;
        // Back faces stay: many building meshes are open or single-sided walls seen from inside.
        gl.Disable(EnableCap.CullFace);
        Bind(0, textured ? pm.Diffuse : 0);
        bool normal = options.NormalMaps && textured && pm.Normal != 0 && gp.Part.HasTangents;
        Bind(1, textured ? pm.Normal : 0);
        bool dual = textured && pm.Diffuse2 != 0 && gp.Part.HasColours;
        Bind(2, dual ? pm.Diffuse2 : 0);
        Bind(3, dual ? pm.Normal2 : 0);
        gl.Uniform1(U("uNormalSwizzled"), pm.Swizzled ? 1 : 0);
        gl.Uniform1(U("uWireframe"), 0);
        gl.Uniform1(U("uHasDiffuse"), textured ? 1 : 0);
        gl.Uniform1(U("uHasNormal"), normal ? 1 : 0);
        gl.Uniform1(U("uHasDual"), dual ? 1 : 0);
        gl.Uniform1(U("uTriplanar"), textured && (m?.Triplanar ?? false) ? 1 : 0);
        var tile = m?.Tile ?? Vector2.One;
        gl.Uniform2(U("uTile"), tile.X, tile.Y);
        var alpha = m?.Alpha ?? AlphaSource.None;
        if (!textured || alpha == AlphaSource.NormalAlpha && pm.Normal == 0) alpha = AlphaSource.None;
        gl.Uniform1(U("uAlphaSource"), (int)alpha);
        gl.Uniform1(U("uAlphaChannel"), Math.Clamp(m?.AlphaChannel ?? 3, 0, 3));
        gl.Uniform1(U("uGreyChannel"), textured ? m?.GreyChannel ?? -1 : -1);
        var tint = m?.Tint ?? Vector3.One;
        gl.Uniform3(U("uTint"), tint.X, tint.Y, tint.Z);
        gl.Uniform1(U("uAlphaThreshold"), alpha == AlphaSource.None ? 0f : m!.AlphaThreshold);
        gl.Uniform1(U("uEmissive"), textured && pm.Normal != 0 && (m?.Emissive ?? false) ? 1 : 0);
        gl.Uniform1(U("uUseVertexColour"), gp.Part.HasColours && (m?.VertexColours ?? false) ? 1 : 0);
        gl.Uniform1(U("uSpecular"), textured ? m?.SpecularMult ?? 1 : 0.3f);
        gl.BindVertexArray(gp.Vao);
        gl.DrawElements(PrimitiveType.Triangles, (uint)gp.Part.Indices.Length, DrawElementsType.UnsignedInt, (void*)0);
        DrawnTriangles += gp.Part.Indices.Length / 3;
    }

    void Bind(int unit, uint texture)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    void Resolve(Instance inst, ref int loads)
    {
        inst.Resolved = true;
        var key = inst.Placed.MeshPath;
        if (!meshes.TryGetValue(key, out var mesh))
        {
            loads++;
            meshes[key] = mesh = LoadMesh(key);
        }
        if (mesh is null) return;
        inst.Mesh = mesh;
        var t = inst.Placed.Transform;
        inst.Center = Vector3.Transform(mesh.Center, t);
        float scale = MathF.Sqrt(Math.Max(new Vector3(t.M11, t.M12, t.M13).LengthSquared(), Math.Max(new Vector3(t.M21, t.M22, t.M23).LengthSquared(), new Vector3(t.M31, t.M32, t.M33).LengthSquared())));
        inst.Radius = mesh.Radius * scale;
        // A part's look comes from the material the layout chose (which differs per town), else from the resolver's candidates.
        var mkey = inst.Placed.Material is { } spec
            ? (key.ToLowerInvariant(), "spec", spec.StringId)
            : (key.ToLowerInvariant(), inst.Placed.Source.StringId, inst.Placed.Owner.StringId);
        if (!materials.TryGetValue(mkey, out var set)) materials[mkey] = set = Materials(inst, mesh);
        inst.Materials = set;
    }

    GpuMesh? LoadMesh(string name)
    {
        var path = assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        if (path is null) { Messages.Add($"mesh not found: {name}"); return null; }
        OgreMesh mesh;
        try { mesh = OgreMeshReader.ReadFile(path); }
        catch (Exception e) when (e is OgreFormatException or EndOfStreamException or IOException or InvalidDataException)
        {
            Messages.Add($"mesh {name}: {e.Message}");
            return null;
        }
        var model = Model.Build(mesh, null);
        var gm = new GpuMesh { Parts = [], Center = model.Center, Radius = model.Radius };
        foreach (var part in model.Parts)
        {
            var gp = new GpuPart { Part = part, SubMesh = mesh.SubMeshes[part.SubMeshIndex], Vao = gl.GenVertexArray(), Vbo = gl.GenBuffer(), Ebo = gl.GenBuffer() };
            gl.BindVertexArray(gp.Vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gp.Vbo);
            gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer, part.Vertices.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gp.Ebo);
            gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, part.Indices.AsSpan(), BufferUsageARB.StaticDraw);
            uint stride = (uint)Vertex.Size;
            void Attrib(uint index, int size, int offset)
            {
                gl.EnableVertexAttribArray(index);
                gl.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, stride, (void*)offset);
            }
            // Same layout as Renderer.Upload; bones and weights stay zero (no skinning in the world view).
            Attrib(0, 3, 0);
            Attrib(1, 3, 12);
            Attrib(2, 2, 24);
            Attrib(3, 4, 32);
            Attrib(4, 4, 48);
            gl.EnableVertexAttribArray(5);
            gl.VertexAttribIPointer(5, 4, VertexAttribIType.UnsignedByte, stride, (void*)64);
            Attrib(6, 4, 68);
            gm.Parts.Add(gp);
        }
        gl.BindVertexArray(0);
        return gm;
    }

    /// <summary>One material per part: the resolver's candidate that names the placing record (or its building) best.</summary>
    PartMaterial[] Materials(Instance inst, GpuMesh mesh)
    {
        var result = new PartMaterial[mesh.Parts.Count];
        string source = $"'{inst.Placed.Source.Name}'", owner = $"'{inst.Placed.Owner.Name}'";
        var chosen = inst.Placed.Material is { } spec ? BuildingMaterial.FromSpec(spec) : null;
        for (int i = 0; i < mesh.Parts.Count; i++)
        {
            var m = chosen ?? resolver.Candidates(inst.Placed.MeshPath, mesh.Parts[i].SubMesh)
                .OrderByDescending(c => (c.Description.Contains(owner, StringComparison.Ordinal) ? 2 : 0) + (c.Description.Contains(source, StringComparison.Ordinal) ? 1 : 0))
                .FirstOrDefault();
            bool border = m?.BorderAddressing ?? false;
            result[i] = new PartMaterial(m, textureCache.Get(m?.Diffuse, border), textureCache.Get(m?.Normal, border),
                textureCache.Get(m?.Diffuse2, border), textureCache.Get(m?.Normal2, border));
        }
        return result;
    }

    int U(string name)
    {
        if (!uniforms.TryGetValue(name, out int location)) uniforms[name] = location = gl.GetUniformLocation(program, name);
        return location;
    }

    public void Dispose()
    {
        foreach (var m in meshes.Values)
            if (m is not null)
                foreach (var gp in m.Parts)
                {
                    gl.DeleteVertexArray(gp.Vao);
                    gl.DeleteBuffer(gp.Vbo);
                    gl.DeleteBuffer(gp.Ebo);
                }
        textureCache.Dispose();
        gl.DeleteProgram(program);
    }
}
