using System.Numerics;
using Meitou.Data.Textures;

using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using SamplerInfo = Meitou.Rendering.Vulkan.Shaders.SamplerInfo;

namespace Meitou.ModelViewer;

/// <summary>
/// The viewer's native mesh drawing (docs/renderer-native.md 3.2 and 7.5, step P): a <see cref="LegacyProgram"/> with VkGl's SPIR-V and layout, and per
/// part a <see cref="Mesh"/> that keeps what a draw needs (the vertex array's export, the layout, the pipelines) while the export is the same object.
/// <see cref="Renderer"/> and <see cref="CharacterRenderer"/> record into the pass VkGl has open (<c>BeginNativeInPass</c>), one segment per call.
/// </summary>
internal sealed class NativeMeshProgram
{
    public readonly LegacyProgram P;
    readonly GpuContext gpu;
    readonly int own;
    /// <summary>The samplers of the program that read a GL unit or a frame global (those of <paramref name="direct"/> are the draw's own).</summary>
    readonly string[] direct;
    (int Version, (SamplerSlot Slot, int Unit, SamplerInfo Info)[] Samplers)? units;

    /// <summary>A part's native state: the vertex-array export it came from, its layout and vertex buffers, its element buffer, and the pipelines
    /// of the last two segment states.</summary>
    public struct Mesh
    {
        public VertexArrayBindings? Source;
        public VertexLayout? Layout;
        public BufferBinding[] Vertices;
        public BufferBinding Elements;
        public int SegA, SegB;
        public GraphicsPipeline? PipeA, PipeB;
    }

    public NativeMeshProgram(GpuContext gpu, string vertex, string fragment, string name, string[] direct)
    {
        this.gpu = gpu;
        this.direct = direct;
        P = LegacyProgram.Create(gpu, vertex, fragment, name);
        own = P.InputLocations.DefaultIfEmpty(-1).Max() + 1;
    }

    /// <summary>The samplers that are not the draw's own: the frame globals (atmosphere and shadow units) or what GL has on their units, as a GL program reads them.</summary>
    public void BindUnitSamplers()
    {
        var interop = gpu.Interop!;
        if (units is not { } u || u.Version != gpu.Globals.Version)
            units = u = (gpu.Globals.Version, [.. P.SamplerNames.Where(n => Array.IndexOf(direct, n) < 0 && gpu.Globals.Texture(n) is null)
                .Select(n => (P.Sampler(n), 0, P.SamplerInfo(P.Sampler(n))))]);
        foreach (var (slot, unit, info) in u.Samplers) P.Bind(slot, interop.SampledUnit(unit, info));
    }

    readonly record struct SegmentKey(AttachmentFormats Formats, BlendState Blend, Silk.NET.Vulkan.ColorComponentFlags Mask, Silk.NET.Vulkan.PolygonMode Polygon,
        bool AlphaToCoverage, bool DepthClamp);
    readonly Dictionary<SegmentKey, int> segments = [];
    SegmentKey last;
    int lastId;

    /// <summary>A stable number for a segment's pipeline state, so a part compares one int per draw.</summary>
    public int Segment(PassTargets t, DrawState s)
    {
        var key = new SegmentKey(t.Formats, s.Blend, s.ColourMask, s.Polygon, s.AlphaToCoverage, s.DepthClamp);
        if (lastId != 0 && key == last) return lastId;
        if (!segments.TryGetValue(key, out int id)) segments[key] = id = segments.Count + 1;
        last = key;
        return lastId = id;
    }

    /// <summary>The program's texture slot for a sampler, with its reflected kind (null when the program does not read it).</summary>
    public (SamplerSlot Slot, SamplerInfo? Info) Sampler(string name)
    {
        var slot = P.Sampler(name);
        return (slot, slot.IsValid ? P.SamplerInfo(slot) : null);
    }

    /// <summary>Binds the GL texture <paramref name="id"/> (0: the stand-in) to a sampler slot.</summary>
    public void Bind(IGlInterop interop, (SamplerSlot Slot, SamplerInfo? Info) sampler, uint id)
    {
        if (sampler.Info is { } info) P.Bind(sampler.Slot, interop.Sampled(id, info));
    }

    /// <summary>One indexed draw of a part: its pipeline, vertex buffers and indices (from <paramref name="firstIndex"/>), the program's blocks.</summary>
    public void Draw(IGlInterop interop, CommandList cmd, ref Mesh m, uint vao, int segment, DrawState state, PassTargets t, string label, uint indexCount, uint firstIndex)
    {
        var va = interop.VertexArray(vao);
        if (!ReferenceEquals(m.Source, va))
        {
            Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
            va.Attributes.AsSpan().CopyTo(attributes);
            m.Layout = P.VertexLayout(attributes);
            m.Vertices = P.VertexBuffers(attributes, 0, own);
            m.Elements = va.Elements;
            m.Source = va;
            m.SegA = m.SegB = 0;
            m.PipeA = m.PipeB = null;
        }
        GraphicsPipeline pipeline;
        if (m.SegA == segment) pipeline = m.PipeA!;
        else if (m.SegB == segment) pipeline = m.PipeB!;
        else
        {
            pipeline = gpu.Pipelines.Get(state.Pipeline(P.Program, m.Layout!, Silk.NET.Vulkan.PrimitiveTopology.TriangleList, t.Formats, label));
            (m.SegB, m.PipeB) = (m.SegA, m.PipeA);
            (m.SegA, m.PipeA) = (segment, pipeline);
        }
        cmd.BindPipeline(pipeline);
        cmd.BindVertexBuffers(0, m.Vertices);
        cmd.BindIndexBuffer(new BufferBinding(m.Elements.Buffer, m.Elements.Offset + (ulong)firstIndex * 4), Silk.NET.Vulkan.IndexType.Uint32);
        P.Flush(cmd);
        cmd.DrawIndexed(indexCount);
    }
}

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
    readonly IGl gl;
    /// <summary>The native GPU API next to <c>gl</c> (docs/renderer-native.md 7.1 step 8); ports use it instead of looking it up.</summary>
    public GpuContext Gpu { get; }
    /// <summary>The GL mesh program is kept only for what making it does (the sampler units and the shadow blocks it publishes as frame globals,
    /// which the native program below reads); the parts are drawn by <see cref="native"/>.</summary>
    readonly uint meshProgram, lineProgram;
    readonly Dictionary<string, int> lineUniforms = [];
    readonly NativeMeshProgram native;
    readonly UniformHandle uViewProjection, uModel, uEye, uLightDir, uTriplanarScale, uBones, uHasHead, uNormalSwizzled, uWireframe, uFlatColour, uSkinned,
        uHasDiffuse, uHasNormal, uHasDual, uTriplanar, uTile, uAlphaSource, uAlphaChannel, uGreyChannel, uTint, uAlphaThreshold, uEmissive, uUseVertexColour, uSpecular;
    /// <summary>uDiffuse, uNormal, uDiffuse2, uNormal2, uHeadDiffuse, uHeadNormal (GL units 0 to 5).</summary>
    readonly (SamplerSlot Slot, Meitou.Rendering.Vulkan.Shaders.SamplerInfo? Info)[] samplers;
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
        public NativeMeshProgram.Mesh Native;
    }

    static readonly string[] SamplerNames = ["uDiffuse", "uNormal", "uDiffuse2", "uNormal2", "uHeadDiffuse", "uHeadNormal"];

    public Renderer(IGl gl, GpuContext gpu, AssetLocator assets)
    {
        this.gl = gl;
        Gpu = gpu;
        this.assets = assets;
        meshProgram = Program(Shaders.MeshVertex, Shaders.MeshFragment);
        native = new NativeMeshProgram(gpu, Shaders.MeshVertex, Shaders.MeshFragment, "viewer mesh", SamplerNames);
        var np = native.P;
        uViewProjection = np.Uniform("uViewProjection"); uModel = np.Uniform("uModel"); uEye = np.Uniform("uEye"); uLightDir = np.Uniform("uLightDir");
        uTriplanarScale = np.Uniform("uTriplanarScale"); uBones = np.Uniform("uBones"); uHasHead = np.Uniform("uHasHead");
        uNormalSwizzled = np.Uniform("uNormalSwizzled"); uWireframe = np.Uniform("uWireframe"); uFlatColour = np.Uniform("uFlatColour");
        uSkinned = np.Uniform("uSkinned"); uHasDiffuse = np.Uniform("uHasDiffuse"); uHasNormal = np.Uniform("uHasNormal"); uHasDual = np.Uniform("uHasDual");
        uTriplanar = np.Uniform("uTriplanar"); uTile = np.Uniform("uTile"); uAlphaSource = np.Uniform("uAlphaSource"); uAlphaChannel = np.Uniform("uAlphaChannel");
        uGreyChannel = np.Uniform("uGreyChannel"); uTint = np.Uniform("uTint"); uAlphaThreshold = np.Uniform("uAlphaThreshold"); uEmissive = np.Uniform("uEmissive");
        uUseVertexColour = np.Uniform("uUseVertexColour"); uSpecular = np.Uniform("uSpecular");
        samplers = [.. SamplerNames.Select(native.Sampler)];
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

        var np = native.P;
        np.Set(uViewProjection, in viewProjection);
        var identity = Matrix4x4.Identity;
        np.Set(uModel, in identity);
        var eye = camera.Eye;
        np.Set(uEye, eye.X, eye.Y, eye.Z);
        var light = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.35f));
        np.Set(uLightDir, light.X, light.Y, light.Z);
        np.Set(uTriplanarScale, 1f / options.TriplanarSize);
        if (bones is not null)
        {
            int count = Math.Min(bones.Length, Shaders.MaxBones);
            np.Set(uBones, System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(bones.AsSpan(0, count)), 4, 4);
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

    /// <summary>The parts in one native segment of VkGl's open pass (step P): dynamic state once, per part the uniforms, textures and the draw.</summary>
    void DrawParts(RenderOptions options, bool skinned, bool wire)
    {
        if (parts.Count == 0) return;
        var interop = Gpu.Interop!;
        var p = native.P;
        native.BindUnitSamplers();
        gl.Enable(EnableCap.CullFace);   // the state export reports the cull mode only while the cull face is on
        gl.CullFace(TriangleFace.Back);
        var cmd = interop.BeginNativeInPass("viewer mesh");
        var targets = interop.CurrentTargets();
        var state = interop.CurrentState();
        int segment = native.Segment(targets, state);
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
        Silk.NET.Vulkan.CullModeFlags? side = null;
        foreach (var gp in parts)
        {
            var m = gp.Material;
            bool textured = options.Textures && gp.Diffuse != 0;
            bool doubleSided = m?.DoubleSided ?? false;
            var want = options.BackfaceCulling && !doubleSided && !wire ? state.Cull : Silk.NET.Vulkan.CullModeFlags.None;
            if (side != want) { cmd.SetRaster(want, state.Front); side = want; }
            native.Bind(interop, samplers[0], textured ? gp.Diffuse : 0);
            bool normal = options.NormalMaps && options.Textures && gp.Normal != 0 && gp.Part.HasTangents;
            native.Bind(interop, samplers[1], options.Textures ? gp.Normal : 0);
            bool dual = textured && gp.Diffuse2 != 0 && gp.Part.HasColours;
            native.Bind(interop, samplers[2], dual ? gp.Diffuse2 : 0);
            native.Bind(interop, samplers[3], dual ? gp.Normal2 : 0);
            bool head = textured && gp.HeadDiffuse != 0;
            native.Bind(interop, samplers[4], head ? gp.HeadDiffuse : 0);
            native.Bind(interop, samplers[5], head ? gp.HeadNormal : 0);
            p.Set(uHasHead, head ? 1 : 0);
            p.Set(uNormalSwizzled, gp.NormalSwizzled ? 1 : 0);
            p.Set(uWireframe, wire ? 1 : 0);
            p.Set(uFlatColour, 0.95f, 0.75f, 0.2f);
            p.Set(uSkinned, skinned && gp.Part.Skinned ? 1 : 0);
            p.Set(uHasDiffuse, textured ? 1 : 0);
            p.Set(uHasNormal, normal ? 1 : 0);
            p.Set(uHasDual, dual ? 1 : 0);
            p.Set(uTriplanar, textured && (m?.Triplanar ?? false) ? 1 : 0);
            var tile = m?.Tile ?? Vector2.One;
            p.Set(uTile, tile.X, tile.Y);
            // Alpha tests need the texture holding the alpha; without it, draw opaque.
            var alpha = m?.Alpha ?? AlphaSource.None;
            if (!textured || alpha == AlphaSource.NormalAlpha && gp.Normal == 0) alpha = AlphaSource.None;
            p.Set(uAlphaSource, (int)alpha);
            p.Set(uAlphaChannel, Math.Clamp(m?.AlphaChannel ?? 3, 0, 3));
            p.Set(uGreyChannel, textured ? m?.GreyChannel ?? -1 : -1);
            var tint = m?.Tint ?? Vector3.One;
            p.Set(uTint, tint.X, tint.Y, tint.Z);
            p.Set(uAlphaThreshold, alpha == AlphaSource.None ? 0f : m!.AlphaThreshold);
            p.Set(uEmissive, textured && gp.Normal != 0 && (m?.Emissive ?? false) ? 1 : 0);
            p.Set(uUseVertexColour, options.VertexColours && gp.Part.HasColours && (options.ForceVertexColours || (m?.VertexColours ?? false)) ? 1 : 0);
            p.Set(uSpecular, textured ? m?.SpecularMult ?? 1 : 0.3f);
            native.Draw(interop, cmd, ref gp.Native, gp.Vao, segment, state, targets, "viewer mesh", (uint)gp.Part.Indices.Length, 0);
        }
        interop.EndNative(cmd);
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(0);
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
        SkyRenderer.AssignSamplerUnits(gl, program);   // the atmosphere's cube samplers off unit 0
        ShadowShaders.Bind(gl, program);               // the shadow blocks, zero-filled (shadows off) without a ShadowPass
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
