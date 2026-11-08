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

    readonly IGpuContext _gpu;
    readonly LegacyProgram _program;
    readonly SamplerSlot _samplerUi, _samplerBack;
    readonly SampledTexture _pointSampler;
    GpuTexture? _texture, _final;

    public UiLayer(IGpuContext gpu)
    {
        this._gpu = gpu;
        _program = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, Fragment, "ui composite");
        _samplerUi = _program.Sampler("uUi");
        _samplerBack = _program.Sampler("uBack");
        _pointSampler = new(gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0)), default);
    }

    /// <summary>What the overlays draw into: valid after <see cref="Clear"/> of the frame.</summary>
    public GpuTexture? Texture => _texture;

    /// <summary>The finished picture (world and interface), top-down: valid after <see cref="Composite"/>. What the present shows and a screenshot reads.</summary>
    public GpuTexture? Final => _final;

    /// <summary>Makes the texture again when the size changed, and clears it to transparent (the first thing of a frame, before any overlay draws).</summary>
    public GpuTexture Clear(int width, int height)
    {
        if (_texture is null || _texture.Desc.Width != width || _texture.Desc.Height != height)
        {
            _texture?.Dispose();   // released after the frames in flight
            _texture = _gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height, Use: TextureUse.ColourTarget | TextureUse.Sampled | TextureUse.TransferSrc, Name: "ui layer"));
        }
        var cmd = _gpu.BeginNative("ui layer clear");
        cmd.BeginRendering(new RenderingDesc(new RenderTarget(_texture.Attachment(), LoadOp.Clear, default), default, width, height));
        cmd.EndRendering();
        _gpu.EndNative(cmd);
        return _texture;
    }

    /// <summary>Draws the layer over <paramref name="world"/> (the same size) into <see cref="Final"/>, flipped to top-down: <c>ui + world * (1 - ui.a)</c>.</summary>
    public GpuTexture? Composite(GpuTexture world)
    {
        if (_texture is null) return null;
        int width = world.Desc.Width, height = world.Desc.Height;
        if (_final is null || _final.Desc.Width != width || _final.Desc.Height != height)
        {
            _final?.Dispose();   // released after the frames in flight
            _final = _gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height,
                Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "final"));
        }
        var t = PassTargets.Of(_final, null).At(new PixelViewport(0, 0, width, height, 0, 1));
        var cmd = _gpu.BeginNative("ui composite");
        cmd.BeginRendering(t.Rendering);
        var state = DrawState.For(t.Formats, _gpu.DepthClamp);
        state.Record(cmd, t);
        cmd.BindPipeline(_gpu.Pipelines.Get(state.Pipeline(_program.Program, _program.VertexLayout([]), PrimitiveType.Triangles, t.Formats, "ui composite")));
        _program.Bind(_samplerUi, _pointSampler with { View = _texture.View() });
        _program.Bind(_samplerBack, _pointSampler with { View = world.View() });
        _program.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        _gpu.EndNative(cmd);
        return _final;
    }

    public void Dispose() { _texture?.Dispose(); _final?.Dispose(); }
}