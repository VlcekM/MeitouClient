using System.Numerics;
using Meitou.Data.Textures;
using Silk.NET.OpenGL;

using Meitou.Rendering;
using Meitou.Rendering.Gpu;

namespace Meitou.ModelViewer;

public sealed class RenderOptions
{
    public bool Textures { get; set; } = true;
    public bool NormalMaps { get; set; } = true;
    /// <summary>Apply vertex colours where the material asks for them (key C toggles).</summary>
    public bool VertexColours { get; set; } = true;
    /// <summary>Apply vertex colours on every part that has them (--vertex-colours).</summary>
    public bool ForceVertexColours { get; set; }
    public bool BackfaceCulling { get; set; } = true;
    /// <summary>0 solid, 1 solid + wireframe, 2 wireframe only.</summary>
    public int Wireframe { get; set; }
    public bool Skeleton { get; set; }
    public bool Grid { get; set; } = true;
    /// <summary>World units per triplanar texture repeat at tile 1 (Kenshi triplanar.hlsl divides world position by 5000).</summary>
    public float TriplanarSize { get; set; } = 5000;
}

/// <summary>Uploads a <see cref="Model"/> and draws it with <see cref="Shaders"/>.</summary>
public sealed unsafe class Renderer : IDisposable
{
    readonly GL gl;
    readonly uint meshProgram, lineProgram;
    readonly Dictionary<string, int> meshUniforms = [], lineUniforms = [];
    readonly List<GpuPart> parts = [];
    readonly Dictionary<string, uint> textures = new(StringComparer.OrdinalIgnoreCase);
    readonly AssetLocator assets;
    uint lineVao, lineVbo;

    sealed class GpuPart
    {
        public required ModelPart Part;
        public uint Vao, Vbo, Ebo;
        public SurfaceMaterial? Material;
        public uint Diffuse, Normal, Diffuse2, Normal2, HeadDiffuse, HeadNormal;
        public bool NormalSwizzled;
    }

    public Renderer(GL gl, AssetLocator assets)
    {
        this.gl = gl;
        this.assets = assets;
        meshProgram = Program(Shaders.MeshVertex, Shaders.MeshFragment);
        lineProgram = Program(Shaders.LineVertex, Shaders.LineFragment);
        lineVao = gl.GenVertexArray();
        lineVbo = gl.GenBuffer();
        gl.BindVertexArray(lineVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, lineVbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 24, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 24, (void*)12);
        gl.BindVertexArray(0);
    }

    /// <summary>Texture loading problems, for the console.</summary>
    public List<string> Messages { get; } = [];

    public void Upload(Model model)
    {
        foreach (var part in model.Parts)
        {
            var gp = new GpuPart { Part = part, Vao = gl.GenVertexArray(), Vbo = gl.GenBuffer(), Ebo = gl.GenBuffer() };
            gl.BindVertexArray(gp.Vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gp.Vbo);
            gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer, part.Vertices.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gp.Ebo);
            gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, part.Indices.AsSpan(), BufferUsageARB.StaticDraw);
            uint stride = (uint)Vertex.Size;
            Attrib(0, 3, 0);
            Attrib(1, 3, 12);
            Attrib(2, 2, 24);
            Attrib(3, 4, 32);
            Attrib(4, 4, 48);
            gl.EnableVertexAttribArray(5);
            gl.VertexAttribIPointer(5, 4, VertexAttribIType.UnsignedByte, stride, (void*)64);
            Attrib(6, 4, 68);
            void Attrib(uint index, int size, int offset)
            {
                gl.EnableVertexAttribArray(index);
                gl.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, stride, (void*)offset);
            }
            parts.Add(gp);
        }
        gl.BindVertexArray(0);
    }

    /// <summary>Assigns a material to a part (by index in the model), loading its textures.</summary>
    public void SetMaterial(int partIndex, SurfaceMaterial? material)
    {
        var gp = parts[partIndex];
        gp.Material = material;
        bool border = material?.BorderAddressing ?? false;
        gp.Diffuse = Texture(material?.Diffuse, border);
        gp.Normal = Texture(material?.Normal, border);
        gp.Diffuse2 = Texture(material?.Diffuse2, border);
        gp.Normal2 = Texture(material?.Normal2, border);
        gp.HeadDiffuse = Texture(material?.HeadDiffuse, border);
        gp.HeadNormal = Texture(material?.HeadNormal, border);
        gp.NormalSwizzled = material?.Normal is { } n && swizzled.Contains(n);
    }

    /// <summary>A texture from the cache (loaded on first use; 0 if missing), for other renderers sharing it (CharacterRenderer).</summary>
    public uint LoadTexture(string? name, bool border) => Texture(name, border);

    /// <summary>
    /// Loads a texture (cached per name and addressing). Also notes "swizzled" normal maps: Kenshi stores some normal maps
    /// with X in alpha and Y in green (R = G = B), which shows as a mean blue far below the ~250 of an ordinary normal map.
    /// </summary>
    uint Texture(string? name, bool border)
    {
        if (name is null) return 0;
        string key = border ? name + "|border" : name;
        if (textures.TryGetValue(key, out var cached)) return cached;
        uint id = 0;
        // The game reduces texture fields to the bare file name (runtime-materials.md); a path still works for --texture.
        var path = assets.Find(Path.GetFileName(name.Replace('\\', '/'))) ?? assets.Find(name);
        if (path is null) Messages.Add($"texture not found: {name}");
        else
        {
            try
            {
                var tex = TextureLoader.LoadFile(path);
                if (LooksSwizzled(tex.Levels.FirstOrDefault(l => l.Width <= 256 && l.Height <= 256) ?? tex.Levels[0])) swizzled.Add(name);
                id = gl.GenTexture();
                gl.BindTexture(TextureTarget.Texture2D, id);
                gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                for (int level = 0; level < tex.Levels.Count; level++)
                {
                    var img = tex.Levels[level];
                    gl.TexImage2D<byte>(TextureTarget.Texture2D, level, InternalFormat.Rgba8, (uint)img.Width, (uint)img.Height, 0,
                        PixelFormat.Rgba, PixelType.UnsignedByte, img.Pixels.AsSpan());
                }
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, tex.Levels.Count - 1);
                if (tex.Levels.Count == 1 || tex.Levels[^1].Width > 1 || tex.Levels[^1].Height > 1)
                {
                    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 1000);
                    gl.GenerateMipmap(TextureTarget.Texture2D);
                }
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                var wrap = border ? TextureWrapMode.ClampToBorder : TextureWrapMode.Repeat;
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
                float[] transparent = [0, 0, 0, 0];
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, transparent.AsSpan());
                gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, 8f); // max anisotropy (GL 4.6 / EXT)
                gl.GetError(); // ignore if anisotropy is unsupported
            }
            catch (Exception e) when (e is DdsFormatException or InvalidOperationException or IOException or ArgumentException)
            {
                Messages.Add($"texture {name}: {e.Message}");
                id = 0;
            }
        }
        textures[key] = id;
        return id;
    }

    readonly HashSet<string> swizzled = new(StringComparer.OrdinalIgnoreCase);

    static bool LooksSwizzled(RgbaImage image)
    {
        long r = 0, g = 0, b = 0;
        var p = image.Pixels;
        for (int i = 0; i < p.Length; i += 4) { r += p[i]; g += p[i + 1]; b += p[i + 2]; }
        long n = p.Length / 4;
        return b / n < 180 && Math.Abs(r - b) / n < 8 && Math.Abs(g - b) / n < 8;
    }

    /// <summary>Whether the normal map of a part was detected as swizzled (for --info).</summary>
    public bool IsSwizzled(string name) => swizzled.Contains(name);

    public void Draw(Camera camera, int width, int height, RenderOptions options, Matrix4x4[]? bones, Animator? animator)
    {
        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.ClearColor(0.16f, 0.17f, 0.19f, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.FrontFace(FrontFaceDirection.Ccw); // Ogre's front faces are counter-clockwise, like GL's default

        var viewProjection = camera.View * camera.Projection(width / (float)Math.Max(height, 1));
        if (options.Grid) DrawGrid(viewProjection, camera);

        gl.UseProgram(meshProgram);
        Matrix(meshUniforms, meshProgram, "uViewProjection", viewProjection);
        Matrix(meshUniforms, meshProgram, "uModel", Matrix4x4.Identity);
        var eye = camera.Eye;
        gl.Uniform3(U(meshUniforms, meshProgram, "uEye"), eye.X, eye.Y, eye.Z);
        var light = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.35f));
        gl.Uniform3(U(meshUniforms, meshProgram, "uLightDir"), light.X, light.Y, light.Z);
        gl.Uniform1(U(meshUniforms, meshProgram, "uTriplanarScale"), 1f / options.TriplanarSize);
        gl.Uniform1(U(meshUniforms, meshProgram, "uDiffuse"), 0);
        gl.Uniform1(U(meshUniforms, meshProgram, "uNormal"), 1);
        gl.Uniform1(U(meshUniforms, meshProgram, "uDiffuse2"), 2);
        gl.Uniform1(U(meshUniforms, meshProgram, "uNormal2"), 3);
        gl.Uniform1(U(meshUniforms, meshProgram, "uHeadDiffuse"), 4);
        gl.Uniform1(U(meshUniforms, meshProgram, "uHeadNormal"), 5);
        if (bones is not null)
        {
            int count = Math.Min(bones.Length, Shaders.MaxBones);
            fixed (Matrix4x4* p = bones)
                gl.UniformMatrix4(U(meshUniforms, meshProgram, "uBones"), (uint)count, false, (float*)p);
        }

        if (options.Wireframe != 2) DrawParts(options, bones is not null, wire: false);
        if (options.Wireframe != 0)
        {
            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            gl.Enable(EnableCap.PolygonOffsetLine);
            gl.PolygonOffset(-1, -1);
            DrawParts(options, bones is not null, wire: true);
            gl.Disable(EnableCap.PolygonOffsetLine);
            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        }
        if (options.Skeleton && animator is not null) DrawSkeleton(viewProjection, animator);
        gl.BindVertexArray(0);
    }

    void DrawParts(RenderOptions options, bool skinned, bool wire)
    {
        foreach (var gp in parts)
        {
            var m = gp.Material;
            bool textured = options.Textures && gp.Diffuse != 0;
            bool doubleSided = m?.DoubleSided ?? false;
            if (options.BackfaceCulling && !doubleSided && !wire) gl.Enable(EnableCap.CullFace); else gl.Disable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);
            Bind(0, textured ? gp.Diffuse : 0);
            bool normal = options.NormalMaps && options.Textures && gp.Normal != 0 && gp.Part.HasTangents;
            Bind(1, options.Textures ? gp.Normal : 0);
            bool dual = textured && gp.Diffuse2 != 0 && gp.Part.HasColours;
            Bind(2, dual ? gp.Diffuse2 : 0);
            Bind(3, dual ? gp.Normal2 : 0);
            bool head = textured && gp.HeadDiffuse != 0;
            Bind(4, head ? gp.HeadDiffuse : 0);
            Bind(5, head ? gp.HeadNormal : 0);
            gl.Uniform1(U(meshUniforms, meshProgram, "uHasHead"), head ? 1 : 0);
            gl.Uniform1(U(meshUniforms, meshProgram, "uNormalSwizzled"), gp.NormalSwizzled ? 1 : 0);
            gl.Uniform1(U(meshUniforms, meshProgram, "uWireframe"), wire ? 1 : 0);
            gl.Uniform3(U(meshUniforms, meshProgram, "uFlatColour"), 0.95f, 0.75f, 0.2f);
            gl.Uniform1(U(meshUniforms, meshProgram, "uSkinned"), skinned && gp.Part.Skinned ? 1 : 0);
            gl.Uniform1(U(meshUniforms, meshProgram, "uHasDiffuse"), textured ? 1 : 0);
            gl.Uniform1(U(meshUniforms, meshProgram, "uHasNormal"), normal ? 1 : 0);
            gl.Uniform1(U(meshUniforms, meshProgram, "uHasDual"), dual ? 1 : 0);
            gl.Uniform1(U(meshUniforms, meshProgram, "uTriplanar"), textured && (m?.Triplanar ?? false) ? 1 : 0);
            var tile = m?.Tile ?? Vector2.One;
            gl.Uniform2(U(meshUniforms, meshProgram, "uTile"), tile.X, tile.Y);
            // Alpha tests need the texture holding the alpha; without it, draw opaque.
            var alpha = m?.Alpha ?? AlphaSource.None;
            if (!textured || alpha == AlphaSource.NormalAlpha && gp.Normal == 0) alpha = AlphaSource.None;
            gl.Uniform1(U(meshUniforms, meshProgram, "uAlphaSource"), (int)alpha);
            gl.Uniform1(U(meshUniforms, meshProgram, "uAlphaChannel"), Math.Clamp(m?.AlphaChannel ?? 3, 0, 3));
            gl.Uniform1(U(meshUniforms, meshProgram, "uGreyChannel"), textured ? m?.GreyChannel ?? -1 : -1);
            var tint = m?.Tint ?? Vector3.One;
            gl.Uniform3(U(meshUniforms, meshProgram, "uTint"), tint.X, tint.Y, tint.Z);
            gl.Uniform1(U(meshUniforms, meshProgram, "uAlphaThreshold"), alpha == AlphaSource.None ? 0f : m!.AlphaThreshold);
            gl.Uniform1(U(meshUniforms, meshProgram, "uEmissive"), textured && gp.Normal != 0 && (m?.Emissive ?? false) ? 1 : 0);
            gl.Uniform1(U(meshUniforms, meshProgram, "uUseVertexColour"), options.VertexColours && gp.Part.HasColours && (options.ForceVertexColours || (m?.VertexColours ?? false)) ? 1 : 0);
            gl.Uniform1(U(meshUniforms, meshProgram, "uSpecular"), textured ? m?.SpecularMult ?? 1 : 0.3f);
            gl.BindVertexArray(gp.Vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)gp.Part.Indices.Length, DrawElementsType.UnsignedInt, (void*)0);
        }
        gl.Disable(EnableCap.CullFace);
    }

    void Bind(int unit, uint texture)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    void DrawGrid(Matrix4x4 viewProjection, Camera camera)
    {
        // A grid on the ground plane (y = 0), sized to the model, plus the X (red) and Z (blue) axes.
        float step = MathF.Pow(10, MathF.Floor(MathF.Log10(Math.Max(camera.ModelRadius, 1e-3f) / 2)));
        int n = 10;
        var lines = new List<float>();
        for (int i = -n; i <= n; i++)
        {
            var c = i == 0 ? new Vector3(0.45f) : new Vector3(0.27f);
            Line(lines, new Vector3(i * step, 0, -n * step), new Vector3(i * step, 0, n * step), i == 0 ? new Vector3(0.3f, 0.4f, 0.9f) : c);
            Line(lines, new Vector3(-n * step, 0, i * step), new Vector3(n * step, 0, i * step), i == 0 ? new Vector3(0.9f, 0.3f, 0.3f) : c);
        }
        DrawLines(viewProjection, lines, depth: true);
    }

    void DrawSkeleton(Matrix4x4 viewProjection, Animator animator)
    {
        var lines = new List<float>();
        for (int h = 0; h < animator.BoneCount; h++)
            if (animator.BoneParent(h) is { } p && p < animator.BoneCount)
                Line(lines, animator.BonePosition(p), animator.BonePosition(h), new Vector3(0.2f, 1f, 0.4f));
        DrawLines(viewProjection, lines, depth: false);
    }

    static void Line(List<float> lines, Vector3 a, Vector3 b, Vector3 c) =>
        lines.AddRange([a.X, a.Y, a.Z, c.X, c.Y, c.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z]);

    void DrawLines(Matrix4x4 viewProjection, List<float> lines, bool depth)
    {
        if (lines.Count == 0) return;
        if (!depth) gl.Disable(EnableCap.DepthTest);
        gl.UseProgram(lineProgram);
        Matrix(lineUniforms, lineProgram, "uViewProjection", viewProjection);
        Matrix(lineUniforms, lineProgram, "uModel", Matrix4x4.Identity);
        gl.BindVertexArray(lineVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, lineVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, lines.ToArray().AsSpan(), BufferUsageARB.StreamDraw);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)(lines.Count / 6));
        gl.Enable(EnableCap.DepthTest);
        gl.UseProgram(meshProgram);
    }

    void Matrix(Dictionary<string, int> cache, uint program, string name, Matrix4x4 m)
    {
        // System.Numerics is row-major with row vectors; GL reads it column-major, which is the column-vector form.
        gl.UniformMatrix4(U(cache, program, name), 1, false, (float*)&m);
    }

    int U(Dictionary<string, int> cache, uint program, string name)
    {
        if (!cache.TryGetValue(name, out int location)) cache[name] = location = gl.GetUniformLocation(program, name);
        return location;
    }

    uint Program(string vertex, string fragment)
    {
        uint vs = Compile(ShaderType.VertexShader, vertex), fs = Compile(ShaderType.FragmentShader, fragment);
        uint program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("Shader link failed: " + gl.GetProgramInfoLog(program));
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        SkyRenderer.AssignSamplerUnits(new GlPassthrough(gl), program);   // the atmosphere's cube samplers off unit 0
        return program;
    }

    uint Compile(ShaderType type, string source)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"{type} compile failed: " + gl.GetShaderInfoLog(shader));
        return shader;
    }

    public void Dispose()
    {
        foreach (var gp in parts)
        {
            gl.DeleteVertexArray(gp.Vao);
            gl.DeleteBuffer(gp.Vbo);
            gl.DeleteBuffer(gp.Ebo);
        }
        foreach (var t in textures.Values) if (t != 0) gl.DeleteTexture(t);
        gl.DeleteVertexArray(lineVao);
        gl.DeleteBuffer(lineVbo);
        gl.DeleteProgram(meshProgram);
        gl.DeleteProgram(lineProgram);
    }
}
