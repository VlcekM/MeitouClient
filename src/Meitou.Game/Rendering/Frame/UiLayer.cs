using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Post;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The interface's own picture: the overlays draw into <see cref="Texture"/> (transparent, display size, premultiplied alpha) instead of the
/// world's backbuffer, and <see cref="Composite"/> lays it over the backbuffer just before the present. The same colours come out as when the
/// overlays drew straight onto the backbuffer (a straight-alpha blend onto a cleared target, with the alpha channel accumulating
/// <c>a + dst.a * (1 - a)</c>, is the premultiplied "over" operator, and the composite is <c>ui + back * (1 - ui.a)</c>); the point is that
/// a later stage (frame generation) can take the world picture without the interface. The composite writes the finished picture into a texture
/// of its own, <see cref="Final"/>, top-down (row 0 is the top, like the swapchain image and like the depth and motion vectors frame generation
/// reads, which the post chain makes bottom-up in GL's convention but whose layout the FidelityFX inputs take as D3D's): the flip the present
/// blit made now happens here, in the sampling of the two textures.
/// </summary>
public sealed class UiLayer : IDisposable
{
    /// <summary>
    /// The composite's fragment shader: the interface over the world picture (<c>ui + back * (1 - ui.a)</c>), both read bottom-up and written
    /// top-down.
    /// </summary>
    const string Fragment = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uUi;
        uniform sampler2D uBack;
        void main()
        {
            // Both textures are bottom-up; the picture written is top-down.
            vec2 uv = vec2(vUv.x, 1.0 - vUv.y);
            vec4 ui = texture(uUi, uv);
            fragColour = vec4(ui.rgb + texture(uBack, uv).rgb * (1.0 - ui.a), 1.0);
        }
        """;

    /// <summary>
    /// The same composite for HDR output (docs/render-post.md "HDR output"): the picture's gamma undone, paper white placed, BT.2020 primaries, PQ.
    /// </summary>
    const string HdrFragment = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uUi;
        uniform sampler2D uBack;
        uniform float uPaperNits;
        vec3 pq(vec3 nits)
        {
            const float M1 = 0.1593017578125, M2 = 78.84375, C1 = 0.8359375, C2 = 18.8515625, C3 = 18.6875;
            vec3 y = pow(clamp(nits / 10000.0, 0.0, 1.0), vec3(M1));
            return pow((C1 + C2 * y) / (1.0 + C3 * y), vec3(M2));
        }
        void main()
        {
            vec2 uv = vec2(vUv.x, 1.0 - vUv.y);
            vec4 ui = texture(uUi, uv);
            vec3 c = ui.rgb + max(texture(uBack, uv).rgb, 0.0) * (1.0 - ui.a);
            vec3 linear709 = pow(max(c, 0.0), vec3(2.2));
            const mat3 TO_2020 = mat3(0.6274, 0.0691, 0.0164, 0.3293, 0.9195, 0.0880, 0.0433, 0.0114, 0.8956);
            fragColour = vec4(pq(TO_2020 * linear709 * uPaperNits), 1.0);
        }
        """;

    /// <summary>
    /// The device the textures and the composite run on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The composite program (<see cref="Fragment"/> on the full-screen post vertex shader).
    /// </summary>
    readonly LegacyProgram _program;

    /// <summary>
    /// The HDR composite program (<see cref="HdrFragment"/>), made at the first HDR frame.
    /// </summary>
    LegacyProgram? _hdrProgram;

    /// <summary>
    /// The composite's samplers: the interface (<c>uUi</c>) and the world picture (<c>uBack</c>).
    /// </summary>
    readonly SamplerSlot _samplerUi, _samplerBack;

    /// <summary>
    /// The nearest-filtered, edge-clamped sampler both textures are read with.
    /// </summary>
    readonly SampledTexture _pointSampler;

    /// <summary>
    /// The interface picture (<see cref="Texture"/>), the composite's output (<see cref="Final"/>) and the HDR composite's; null until first made,
    /// remade at a new size.
    /// </summary>
    GpuTexture? _texture, _final, _hdrFinal;

    /// <summary>
    /// Builds the composite program and its point sampler on <paramref name="gpu"/>.
    /// </summary>
    public UiLayer(IGpuContext gpu)
    {
        this._gpu = gpu;
        _program = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, Fragment, "ui composite");
        _samplerUi = _program.Sampler("uUi");
        _samplerBack = _program.Sampler("uBack");
        _pointSampler = new(gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0)), default);
    }

    /// <summary>
    /// What the overlays draw into: valid after <see cref="Clear"/> of the frame.
    /// </summary>
    public GpuTexture? Texture => _texture;

    /// <summary>
    /// The finished picture (world and interface), top-down: valid after <see cref="Composite"/>. What the present shows and a screenshot reads.
    /// </summary>
    public GpuTexture? Final => _final;

    /// <summary>
    /// Makes the texture again when the size changed, and clears it to transparent (the first thing of a frame, before any overlay draws).
    /// </summary>
    public GpuTexture Clear(int width, int height)
    {
        if (_texture is null || _texture.Desc.Width != width || _texture.Desc.Height != height)
        {
            _texture?.Dispose();   // released after the frames in flight
            var desc = new TextureDesc(InternalFormat.Rgba8, width, height,
                Use: TextureUse.ColourTarget | TextureUse.Sampled | TextureUse.TransferSrc, Name: "ui layer");
            _texture = _gpu.CreateTexture(desc);
        }
        var cmd = _gpu.BeginNative("ui layer clear");
        cmd.BeginRendering(new RenderingDesc(new RenderTarget(_texture.Attachment(), LoadOp.Clear, default), default, width, height));
        cmd.EndRendering();
        _gpu.EndNative(cmd);
        return _texture;
    }

    /// <summary>
    /// Draws the layer over <paramref name="world"/> (the same size) into <see cref="Final"/>, flipped to top-down: <c>ui + world * (1 - ui.a)</c>.
    /// </summary>
    public GpuTexture? Composite(GpuTexture world)
    {
        if (_texture is null) return null;

        _final = Sized(_final, InternalFormat.Rgba8, world, "final");
        Draw(_program, _samplerUi, _samplerBack, _final, world);
        return _final;
    }

    /// <summary>
    /// As <see cref="Composite"/>, into a 10-bit PQ picture for an HDR swapchain. Screenshots still read <see cref="Composite"/>'s.
    /// </summary>
    public GpuTexture? CompositeHdr(GpuTexture world, float paperNits)
    {
        if (_texture is null) return null;

        var p = _hdrProgram ??= LegacyProgram.Create(_gpu, PostProcessShaders.Vertex, HdrFragment, "ui composite hdr");
        p.Set(p.Uniform("uPaperNits"), paperNits);
        _hdrFinal = Sized(_hdrFinal, InternalFormat.Rgb10A2, world, "final hdr");
        Draw(p, p.Sampler("uUi"), p.Sampler("uBack"), _hdrFinal, world);
        return _hdrFinal;
    }

    /// <summary>
    /// The texture, made again when its size is not the world's.
    /// </summary>
    GpuTexture Sized(GpuTexture? current, InternalFormat format, GpuTexture world, string name)
    {
        int width = world.Desc.Width, height = world.Desc.Height;
        if (current is not null && current.Desc.Width == width && current.Desc.Height == height) return current;

        current?.Dispose();   // released after the frames in flight
        return _gpu.CreateTexture(new TextureDesc(format, width, height,
            Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: name));
    }

    /// <summary>
    /// The composite with <paramref name="shader"/> (its samplers <paramref name="ui"/> and <paramref name="back"/>) over <paramref name="world"/>
    /// into <paramref name="target"/>.
    /// </summary>
    void Draw(LegacyProgram shader, SamplerSlot ui, SamplerSlot back, GpuTexture target, GpuTexture world)
    {
        int width = world.Desc.Width, height = world.Desc.Height;
        var t = PassTargets.Of(target, null).At(new PixelViewport(0, 0, width, height, 0, 1));
        var cmd = _gpu.BeginNative("ui composite");
        cmd.BeginRendering(t.Rendering);
        var state = DrawState.For(t.Formats, _gpu.DepthClamp);
        state.Record(cmd, t);
        var pipeline = state.Pipeline(shader.Program, shader.VertexLayout([]), PrimitiveType.Triangles, t.Formats, "ui composite");
        cmd.BindPipeline(_gpu.Pipelines.Get(pipeline));
        shader.Bind(ui, _pointSampler with { View = _texture!.View() });
        shader.Bind(back, _pointSampler with { View = world.View() });
        shader.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        _gpu.EndNative(cmd);
    }

    /// <summary>
    /// Releases the two textures (after the frames in flight).
    /// </summary>
    public void Dispose()
    {
        _texture?.Dispose();
        _final?.Dispose();
        _hdrFinal?.Dispose();
        _hdrProgram?.Dispose();
    }
}
