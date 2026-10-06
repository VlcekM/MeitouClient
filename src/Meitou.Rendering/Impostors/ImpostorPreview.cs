using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// The impostor check (docs/impostors.md, "Preview"): draws instances of one mesh either as meshes, with the world's foliage program
/// (<see cref="FoliageShaders"/>, instanced, the same material uniforms as <see cref="FoliageRenderer"/>), or as impostors with
/// <see cref="ImpostorShaders.Vertex"/> / <see cref="ImpostorShaders.Fragment"/> on the same instance buffer, so the two can be compared
/// picture for picture. Lighting is the mesh shader's direct path (no world sky): both go through the same formula from the same inputs.
/// </summary>
internal sealed unsafe class ImpostorPreview : IDisposable
{
    const int InstanceStride = 64;
    readonly IGl gl;
    readonly WorldTextureCache textures;
    readonly uint meshProgram, impostorProgram, impostorDepthProgram, instanceBuffer, impostorVao;
    readonly Dictionary<(uint, string), int> uniforms = [];
    readonly List<(List<Part> Parts, Material Material)> meshes = [];
    ImpostorTextures? atlas;

    sealed class Part
    {
        public uint Vao, Vbo, Ebo;
        public int Count;
        public bool HasColours;
    }

    sealed record Material(WorldTexture? Diffuse, WorldTexture? Normal, WorldTexture? Diffuse2, WorldTexture? Normal2, ImpostorMaterial Settings);

    public ImpostorPreview(IGl gl, AssetLocator assets)
    {
        this.gl = gl;
        textures = new WorldTextureCache(gl, assets) { IdleSeconds = double.MaxValue, HighWaterMb = double.MaxValue };
        meshProgram = WorldGl.Program(gl, FoliageShaders.MeshVertex(), FoliageShaders.MeshFragment());
        impostorProgram = WorldGl.Program(gl, ImpostorShaders.Vertex, ImpostorShaders.Fragment);
        impostorDepthProgram = WorldGl.Program(gl, ImpostorShaders.Vertex, ImpostorShaders.FragmentWithDepth);
        instanceBuffer = gl.GenBuffer();
        impostorVao = gl.GenVertexArray();
        gl.BindVertexArray(impostorVao);
        InstanceAttributes();
        gl.BindVertexArray(0);
    }

    /// <summary>Parallax step in the impostor sampling.</summary>
    public bool Parallax { get; set; }
    /// <summary>Write the impostor's blended depth (<see cref="ImpostorShaders.FragmentWithDepth"/>).</summary>
    public bool DepthWrite { get; set; }

    int U(uint program, string name)
    {
        if (!uniforms.TryGetValue((program, name), out int location)) uniforms[(program, name)] = location = gl.GetUniformLocation(program, name);
        return location;
    }

    void InstanceAttributes()
    {
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, instanceBuffer);
        for (uint a = 0; a < 4; a++)
        {
            gl.EnableVertexAttribArray(FoliageShaders.InstanceLocation + a);
            gl.VertexAttribPointer(FoliageShaders.InstanceLocation + a, 4, VertexAttribPointerType.Float, false, InstanceStride, (void*)(16 * a));
            gl.VertexAttribDivisor(FoliageShaders.InstanceLocation + a, 1);
        }
    }

    public void SetMesh(ImpostorSource source, ImpostorMeshes model)
    {
        Material Resolve(ImpostorMaterial m) => new(textures.Get(m.Diffuse, false), textures.Get(m.Normal, false), textures.Get(m.Diffuse2, false), textures.Get(m.Normal2, false), m);
        meshes.Add((Upload(model.Main), Resolve(source.Main)));
        if (model.Leaves is not null && source.Leaves is not null) meshes.Add((Upload(model.Leaves), Resolve(source.Leaves)));
        while (textures.PendingCount > 0) textures.Pump(wait: true, max: 64);
    }

    public void SetAtlas(ImpostorAtlas baked)
    {
        atlas?.Dispose();
        atlas = new ImpostorTextures(gl, baked);
    }

    List<Part> Upload(Model model)
    {
        var parts = new List<Part>();
        foreach (var mp in model.Parts)
        {
            if (mp.Indices.Length == 0) continue;
            var p = new Part { Vao = gl.GenVertexArray(), Vbo = gl.GenBuffer(), Ebo = gl.GenBuffer(), Count = mp.Indices.Length, HasColours = mp.HasColours };
            gl.BindVertexArray(p.Vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, p.Vbo);
            gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer, mp.Vertices, BufferUsageARB.StaticDraw);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, p.Ebo);
            gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, mp.Indices, BufferUsageARB.StaticDraw);
            uint stride = (uint)Vertex.Size;
            void Attrib(uint index, int components, int offset)
            {
                gl.EnableVertexAttribArray(index);
                gl.VertexAttribPointer(index, components, VertexAttribPointerType.Float, false, stride, (void*)offset);
            }
            Attrib(0, 3, 0);
            Attrib(1, 3, 12);
            Attrib(2, 2, 24);
            Attrib(3, 4, 32);
            Attrib(4, 4, 48);
            InstanceAttributes();
            gl.BindVertexArray(0);
            parts.Add(p);
        }
        return parts;
    }

    void Instances(ReadOnlySpan<Matrix4x4> instances)
    {
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, instanceBuffer);
        gl.BufferData<Matrix4x4>(BufferTargetARB.ArrayBuffer, instances, BufferUsageARB.StreamDraw);
    }

    void Bind(int unit, uint texture)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    /// <summary>The instances as meshes (row 0 w of each matrix: the fade, 2 = whole).</summary>
    public void DrawMeshes(Matrix4x4 viewProjection, Vector3 eye, Vector3 light, ReadOnlySpan<Matrix4x4> instances, bool coverage)
    {
        Instances(instances);
        uint prog = meshProgram;
        gl.UseProgram(prog);
        WorldGl.Matrix(gl, U(prog, "uViewProjection"), viewProjection);
        gl.Uniform3(U(prog, "uEye"), eye.X, eye.Y, eye.Z);
        gl.Uniform3(U(prog, "uLightDir"), light.X, light.Y, light.Z);
        gl.Uniform1(U(prog, "uFogDistance"), 0f);
        gl.Uniform1(U(prog, "uTriplanarScale"), 1f / 5000);
        string[] samplers = ["uDiffuse", "uNormal", "uDiffuse2", "uNormal2", "uHeadDiffuse", "uHeadNormal"];
        for (int i = 0; i < samplers.Length; i++) gl.Uniform1(U(prog, samplers[i]), i);
        gl.Uniform1(U(prog, "uSkinned"), 0);
        gl.Uniform1(U(prog, "uHasHead"), 0);
        gl.Uniform1(U(prog, "uWireframe"), 0);
        gl.Uniform1(U(prog, "uCoverage"), coverage ? 1 : 0);
        if (coverage) gl.Enable(EnableCap.SampleAlphaToCoverage);
        foreach (var (parts, m) in meshes)
        {
            var s = m.Settings;
            bool textured = m.Diffuse is { Id: not 0 };
            Bind(0, textured ? m.Diffuse!.Id : 0);
            bool normal = textured && m.Normal is { Id: not 0 };
            Bind(1, normal ? m.Normal!.Id : 0);
            bool dual = textured && m.Diffuse2 is { Id: not 0 };
            Bind(2, dual ? m.Diffuse2!.Id : 0);
            Bind(3, dual && m.Normal2 is { Id: not 0 } ? m.Normal2!.Id : 0);
            gl.Uniform1(U(prog, "uNormalSwizzled"), normal && m.Normal!.Swizzled ? 1 : 0);
            gl.Uniform1(U(prog, "uHasDiffuse"), textured ? 1 : 0);
            gl.Uniform1(U(prog, "uTriplanar"), textured && s.Triplanar ? 1 : 0);
            gl.Uniform2(U(prog, "uTile"), s.Tile.X, s.Tile.Y);
            bool cut = normal && s.AlphaThreshold > 0;
            gl.Uniform1(U(prog, "uAlphaSource"), cut ? 2 : 0);
            gl.Uniform1(U(prog, "uAlphaChannel"), 3);
            gl.Uniform1(U(prog, "uGreyChannel"), -1);
            gl.Uniform3(U(prog, "uTint"), 1f, 1f, 1f);
            gl.Uniform1(U(prog, "uAlphaThreshold"), cut ? s.AlphaThreshold : 0f);
            gl.Uniform1(U(prog, "uEmissive"), 0);
            gl.Uniform1(U(prog, "uSpecular"), textured ? s.Specular : 0.3f);
            if (s.DoubleSided) gl.Disable(EnableCap.CullFace);
            else gl.Enable(EnableCap.CullFace);
            foreach (var p in parts)
            {
                gl.Uniform1(U(prog, "uHasNormal"), normal || cut ? 1 : 0);
                gl.Uniform1(U(prog, "uHasDual"), dual && p.HasColours ? 1 : 0);
                gl.Uniform1(U(prog, "uUseVertexColour"), p.HasColours ? 1 : 0);
                gl.BindVertexArray(p.Vao);
                gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)p.Count, DrawElementsType.UnsignedInt, (void*)0, (uint)instances.Length);
            }
        }
        if (coverage) gl.Disable(EnableCap.SampleAlphaToCoverage);
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(0);
        gl.ActiveTexture(TextureUnit.Texture0);
    }

    /// <summary>The instances as impostors: one billboard each (six vertices from <c>gl_VertexID</c>), the camera's up axis for the billboards' roll.</summary>
    public void DrawImpostors(Matrix4x4 viewProjection, Vector3 eye, Vector3 cameraUp, Vector3 light, ReadOnlySpan<Matrix4x4> instances, bool coverage)
    {
        if (atlas is null) return;
        Instances(instances);
        uint prog = DepthWrite ? impostorDepthProgram : impostorProgram;
        gl.UseProgram(prog);
        WorldGl.Matrix(gl, U(prog, "uViewProjection"), viewProjection);
        gl.Uniform3(U(prog, "uEye"), eye.X, eye.Y, eye.Z);
        gl.Uniform3(U(prog, "uCameraUp"), cameraUp.X, cameraUp.Y, cameraUp.Z);
        gl.Uniform3(U(prog, "uLightDir"), light.X, light.Y, light.Z);
        gl.Uniform1(U(prog, "uFogDistance"), 0f);
        var a = atlas.Atlas;
        gl.Uniform4(U(prog, "uImpostor"), a.Centre.X, a.Centre.Y, a.Centre.Z, a.Radius);
        gl.Uniform1(U(prog, "uImpostorGrid"), (float)a.Grid);
        gl.Uniform1(U(prog, "uImpostorParallax"), Parallax ? 1 : 0);
        gl.Uniform1(U(prog, "uImpostorDebug"), int.TryParse(Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_DEBUG"), out int debug) ? debug : 0);
        gl.Uniform1(U(prog, "uCoverage"), coverage ? 1 : 0);
        gl.Uniform1(U(prog, "uImpostorAlbedo"), 0);
        gl.Uniform1(U(prog, "uImpostorNormal"), 1);
        gl.Uniform1(U(prog, "uImpostorDepth"), 2);
        Bind(0, atlas.Albedo);
        Bind(1, atlas.Normal);
        Bind(2, atlas.Depth);
        if (coverage) gl.Enable(EnableCap.SampleAlphaToCoverage);
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(impostorVao);
        gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, (uint)instances.Length);
        if (coverage) gl.Disable(EnableCap.SampleAlphaToCoverage);
        gl.BindVertexArray(0);
        gl.ActiveTexture(TextureUnit.Texture0);
    }

    public void Dispose()
    {
        atlas?.Dispose();
        foreach (var (parts, _) in meshes)
            foreach (var p in parts)
            {
                gl.DeleteVertexArray(p.Vao);
                gl.DeleteBuffer(p.Vbo);
                gl.DeleteBuffer(p.Ebo);
            }
        gl.DeleteVertexArray(impostorVao);
        gl.DeleteBuffer(instanceBuffer);
        gl.DeleteProgram(meshProgram);
        gl.DeleteProgram(impostorProgram);
        gl.DeleteProgram(impostorDepthProgram);
        textures.Dispose();
    }
}
