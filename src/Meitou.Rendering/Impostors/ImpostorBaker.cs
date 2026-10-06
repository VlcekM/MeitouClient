using System.Diagnostics;
using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Impostors;

/// <summary>The decoded meshes of a source and the sphere its frames cover.</summary>
public sealed class ImpostorMeshes
{
    public required Model Main { get; init; }
    public Model? Leaves { get; init; }
    /// <summary>The bounding sphere in object space: the bounding box's centre and the farthest vertex from it.</summary>
    public required Vector3 Centre { get; init; }
    public required float Radius { get; init; }

    /// <summary>Decodes the source's meshes (any thread); null when the main mesh cannot be read.</summary>
    public static ImpostorMeshes? Load(ImpostorSource source, List<string>? messages = null)
    {
        Model? Read(string? path)
        {
            if (path is null) return null;
            try { return Model.Build(OgreMeshReader.ReadFile(path), null); }
            catch (Exception e) when (e is OgreFormatException or EndOfStreamException or IOException or InvalidDataException)
            {
                if (messages is not null) lock (messages) messages.Add($"impostor {source.Name}: {Path.GetFileName(path)}: {e.Message}");
                return null;
            }
        }
        var main = Read(source.MeshPath);
        if (main is null || main.Parts.Count == 0) return null;
        return From(main, Read(source.LeavesPath));
    }

    /// <summary>The meshes with their bounding sphere (from the vertices of both).</summary>
    public static ImpostorMeshes From(Model main, Model? leaves)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var model in (ReadOnlySpan<Model?>)[main, leaves])
            if (model is not null)
                foreach (var part in model.Parts)
                    foreach (var v in part.Vertices) { min = Vector3.Min(min, v.Position); max = Vector3.Max(max, v.Position); }
        var centre = (min + max) / 2;
        float r2 = 0;
        foreach (var model in (ReadOnlySpan<Model?>)[main, leaves])
            if (model is not null)
                foreach (var part in model.Parts)
                    foreach (var v in part.Vertices) r2 = MathF.Max(r2, Vector3.DistanceSquared(v.Position, centre));
        return new ImpostorMeshes { Main = main, Leaves = leaves, Centre = centre, Radius = MathF.Max(MathF.Sqrt(r2) * 1.01f, 1e-3f) };
    }
}

/// <summary>How long the last bake took, by step (milliseconds).</summary>
public readonly record struct ImpostorBakeTimes(double Upload, double Render, double Filter, double Encode)
{
    public double Total => Upload + Render + Filter + Encode;
}

/// <summary>
/// Bakes impostor atlases through <see cref="IGl"/> (docs/impostors.md, "Baking"): every frame of the grid is rendered orthographically along
/// its direction with the shared mesh shader (<see cref="ImpostorShaders.BakeFragment"/>, the materials as <see cref="FoliageRenderer"/> sets
/// them), at twice the frame size, one row of frames and one map at a time into an RGBA8 target that is read back; <see cref="ImpostorAssembler"/>
/// filters and encodes on the CPU. Runs on the GL thread, outside a frame's passes. Leaves framebuffer 0 bound, depth testing on, culling off.
/// </summary>
public sealed unsafe class ImpostorBaker : IDisposable
{
    readonly IGl gl;
    readonly WorldTextureCache textures;
    readonly uint program;
    readonly Dictionary<string, int> uniforms = [];

    /// <param name="assets">Where texture names are looked up; null only when no source names a texture (tests).</param>
    public ImpostorBaker(IGl gl, AssetLocator? assets)
    {
        this.gl = gl;
        textures = new WorldTextureCache(gl, assets!) { IdleSeconds = double.MaxValue, HighWaterMb = double.MaxValue };
        program = WorldGl.Program(gl, Shaders.MeshVertex, ImpostorShaders.BakeFragment());
    }

    public List<string> Messages => textures.Messages;
    public ImpostorBakeTimes LastTimes { get; private set; }
    /// <summary>Encode the maps (BC3 / BC5); false keeps them RGBA8 (debugging).</summary>
    public bool Compress { get; set; } = true;

    int U(string name)
    {
        if (!uniforms.TryGetValue(name, out int location)) uniforms[name] = location = gl.GetUniformLocation(program, name);
        return location;
    }

    sealed class Part
    {
        public uint Vao, Vbo, Ebo;
        public int Count;
        public bool HasColours;
    }

    sealed record Material(WorldTexture? Diffuse, WorldTexture? Normal, WorldTexture? Diffuse2, WorldTexture? Normal2, ImpostorMaterial Settings);

    /// <summary>The atlas of <paramref name="source"/> at the size class <paramref name="size"/> (<see cref="ImpostorClass.For"/>).</summary>
    public ImpostorAtlas Bake(ImpostorSource source, ImpostorMeshes meshes, ImpostorClass size)
    {
        var watch = Stopwatch.StartNew();
        Material Resolve(ImpostorMaterial m) => new(textures.Get(m.Diffuse, false), textures.Get(m.Normal, false), textures.Get(m.Diffuse2, false), textures.Get(m.Normal2, false), m);
        var main = Resolve(source.Main);
        var leaves = source.Leaves is { } l ? Resolve(l) : null;
        while (textures.PendingCount > 0) textures.Pump(wait: true, max: 64);
        var mainParts = Upload(meshes.Main);
        var leavesParts = meshes.Leaves is null || leaves is null ? [] : Upload(meshes.Leaves);
        double upload = watch.Elapsed.TotalMilliseconds;

        int grid = size.Grid, frame = size.FramePixels, samples = 2 * frame, width = grid * samples;
        uint fbo = gl.GenFramebuffer(), colour = gl.GenTexture(), depth = gl.GenRenderbuffer();
        gl.BindTexture(TextureTarget.Texture2D, colour);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)samples, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)width, (uint)samples);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colour, 0);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete) throw new InvalidOperationException("impostor bake target incomplete");
        // The half-size target the row is box-filtered into (a linear blit at exactly 2:1 averages 2 × 2 samples), which is read back.
        int small = grid * frame;
        uint smallFbo = gl.GenFramebuffer(), smallColour = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, smallColour);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)small, (uint)frame, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, smallFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, smallColour, 0);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);

        gl.UseProgram(program);
        string[] samplers = ["uDiffuse", "uNormal", "uDiffuse2", "uNormal2", "uHeadDiffuse", "uHeadNormal"];
        for (int i = 0; i < samplers.Length; i++) gl.Uniform1(U(samplers[i]), i);
        gl.Uniform1(U("uSkinned"), 0);
        gl.Uniform1(U("uHasHead"), 0);
        gl.Uniform1(U("uWireframe"), 0);
        gl.Uniform1(U("uFogDistance"), 0f);
        gl.Uniform1(U("uTriplanarScale"), 1f / 5000);
        gl.Uniform3(U("uLightDir"), 0f, 1f, 0f);
        WorldGl.Matrix(gl, U("uModel"), Matrix4x4.Identity);
        var c = meshes.Centre;
        float r = meshes.Radius;
        gl.Uniform4(U("uImpostorSphere"), c.X, c.Y, c.Z, r);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Less);
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
        gl.ColorMask(true, true, true, true);

        var assembler = new ImpostorAssembler(grid, frame, size.Levels);
        int rowBytes = small * frame * 4;
        var buffers = new[] { new byte[rowBytes], new byte[rowBytes], new byte[rowBytes] };
        double render = 0, filter = 0;
        Task? previous = null;
        var spare = new[] { new byte[rowBytes], new byte[rowBytes], new byte[rowBytes] };
        for (int row = 0; row < grid; row++)
        {
            var t0 = watch.Elapsed.TotalMilliseconds;
            for (int pass = 0; pass < 3; pass++)
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                gl.UseProgram(program);
                gl.Uniform1(U("uImpostorPass"), pass);
                gl.Viewport(0, 0, (uint)width, (uint)samples);
                gl.ClearColor(0, 0, 0, 0);
                gl.ClearDepth(1);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                for (int column = 0; column < grid; column++)
                {
                    var d = ImpostorLayout.FrameDirection(column, row, grid);
                    ImpostorLayout.Basis(d, out var right, out var up);
                    var view = Matrix4x4.CreateLookAt(c + d * (2 * r), c, up);
                    var projection = Matrix4x4.CreateOrthographic(2 * r, 2 * r, 0.5f * r, 3.5f * r);
                    gl.Viewport(column * samples, 0, (uint)samples, (uint)samples);
                    gl.UseProgram(program);
                    WorldGl.Matrix(gl, U("uViewProjection"), view * projection);
                    var eye = c + d * (2 * r);
                    gl.Uniform3(U("uEye"), eye.X, eye.Y, eye.Z);
                    gl.Uniform3(U("uImpostorDir"), d.X, d.Y, d.Z);
                    gl.Uniform3(U("uImpostorRight"), right.X, right.Y, right.Z);
                    gl.Uniform3(U("uImpostorUp"), up.X, up.Y, up.Z);
                    Draw(mainParts, main);
                    if (leaves is not null) Draw(leavesParts, leaves);
                }
                gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
                gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, smallFbo);
                gl.BlitFramebuffer(0, 0, width, samples, 0, 0, small, frame, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
                gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, smallFbo);
                gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
                gl.ReadPixels<byte>(0, 0, (uint)small, (uint)frame, PixelFormat.Rgba, PixelType.UnsignedByte, buffers[pass].AsSpan());
            }
            render += watch.Elapsed.TotalMilliseconds - t0;
            // Filter this row on the worker threads while the next one renders.
            var t1 = watch.Elapsed.TotalMilliseconds;
            previous?.Wait();
            filter += watch.Elapsed.TotalMilliseconds - t1;
            (buffers, spare) = (spare, buffers);
            var rowBuffers = spare;
            int rowIndex = row;
            previous = Task.Run(() => assembler.AddRow(rowIndex, rowBuffers[0], rowBuffers[1], rowBuffers[2]));
        }
        var t2 = watch.Elapsed.TotalMilliseconds;
        previous?.Wait();
        filter += watch.Elapsed.TotalMilliseconds - t2;

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.DeleteFramebuffer(fbo);
        gl.DeleteTexture(colour);
        gl.DeleteFramebuffer(smallFbo);
        gl.DeleteTexture(smallColour);
        gl.DeleteRenderbuffer(depth);
        foreach (var p in mainParts.Concat(leavesParts))
        {
            gl.DeleteVertexArray(p.Vao);
            gl.DeleteBuffer(p.Vbo);
            gl.DeleteBuffer(p.Ebo);
        }
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(0);
        gl.UseProgram(0);

        var t3 = watch.Elapsed.TotalMilliseconds;
        var atlas = assembler.Finish($"{source.Name} ({Path.GetFileName(source.MeshPath)})", c, r, Compress);
        LastTimes = new ImpostorBakeTimes(upload, render, filter, watch.Elapsed.TotalMilliseconds - t3);
        return atlas;
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
            gl.BindVertexArray(0);
            parts.Add(p);
        }
        return parts;
    }

    void Bind(int unit, uint texture)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    /// <summary>The material uniforms exactly as <c>FoliageRenderer.DrawMesh</c> sets them (textures and normal maps on).</summary>
    void Draw(List<Part> parts, Material m)
    {
        var s = m.Settings;
        bool textured = m.Diffuse is { Id: not 0 };
        Bind(0, textured ? m.Diffuse!.Id : 0);
        bool normal = textured && m.Normal is { Id: not 0 };
        Bind(1, normal ? m.Normal!.Id : 0);
        bool dual = textured && m.Diffuse2 is { Id: not 0 };
        Bind(2, dual ? m.Diffuse2!.Id : 0);
        Bind(3, dual && m.Normal2 is { Id: not 0 } ? m.Normal2!.Id : 0);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.Uniform1(U("uNormalSwizzled"), normal && m.Normal!.Swizzled ? 1 : 0);
        gl.Uniform1(U("uHasDiffuse"), textured ? 1 : 0);
        gl.Uniform1(U("uTriplanar"), textured && s.Triplanar ? 1 : 0);
        gl.Uniform2(U("uTile"), s.Tile.X, s.Tile.Y);
        bool cut = normal && s.AlphaThreshold > 0;
        gl.Uniform1(U("uAlphaSource"), cut ? 2 : 0);
        gl.Uniform1(U("uAlphaChannel"), 3);
        gl.Uniform1(U("uGreyChannel"), -1);
        gl.Uniform3(U("uTint"), 1f, 1f, 1f);
        gl.Uniform1(U("uAlphaThreshold"), cut ? s.AlphaThreshold : 0f);
        gl.Uniform1(U("uEmissive"), 0);
        gl.Uniform1(U("uSpecular"), textured ? s.Specular : 0.3f);
        if (s.DoubleSided) gl.Disable(EnableCap.CullFace);
        else gl.Enable(EnableCap.CullFace);
        foreach (var p in parts)
        {
            gl.Uniform1(U("uHasNormal"), normal || cut ? 1 : 0);
            gl.Uniform1(U("uHasDual"), dual && p.HasColours ? 1 : 0);
            gl.Uniform1(U("uUseVertexColour"), p.HasColours ? 1 : 0);
            gl.BindVertexArray(p.Vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)p.Count, DrawElementsType.UnsignedInt, (void*)0);
        }
    }

    public void Dispose()
    {
        textures.Dispose();
        gl.DeleteProgram(program);
    }
}
