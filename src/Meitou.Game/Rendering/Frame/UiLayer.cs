using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

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

    readonly IGpuContext gpu;
    readonly LegacyProgram program;
    readonly SamplerSlot samplerUi, samplerBack;
    readonly SampledTexture pointSampler;
    GpuTexture? texture, final;

    public UiLayer(IGpuContext gpu)
    {
        this.gpu = gpu;
        program = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, Fragment, "ui composite");
        samplerUi = program.Sampler("uUi");
        samplerBack = program.Sampler("uBack");
        pointSampler = new(gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0)), default);
    }

    /// <summary>What the overlays draw into: valid after <see cref="Clear"/> of the frame.</summary>
    public GpuTexture? Texture => texture;

    /// <summary>The finished picture (world and interface), top-down: valid after <see cref="Composite"/>. What the present shows and a screenshot reads.</summary>
    public GpuTexture? Final => final;

    /// <summary>Makes the texture again when the size changed, and clears it to transparent (the first thing of a frame, before any overlay draws).</summary>
    public GpuTexture Clear(int width, int height)
    {
        if (texture is null || texture.Desc.Width != width || texture.Desc.Height != height)
        {
            texture?.Dispose();   // released after the frames in flight
            texture = gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height, Use: TextureUse.ColourTarget | TextureUse.Sampled | TextureUse.TransferSrc, Name: "ui layer"));
        }
        var cmd = gpu.BeginNative("ui layer clear");
        cmd.BeginRendering(new RenderingDesc(new RenderTarget(texture.Attachment(), LoadOp.Clear, default), default, width, height));
        cmd.EndRendering();
        gpu.EndNative(cmd);
        return texture;
    }

    /// <summary>Draws the layer over <paramref name="world"/> (the same size) into <see cref="Final"/>, flipped to top-down: <c>ui + world * (1 - ui.a)</c>.</summary>
    public GpuTexture? Composite(GpuTexture world)
    {
        if (texture is null) return null;
        int width = world.Desc.Width, height = world.Desc.Height;
        if (final is null || final.Desc.Width != width || final.Desc.Height != height)
        {
            final?.Dispose();   // released after the frames in flight
            final = gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height,
                Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "final"));
        }
        var t = PassTargets.Of(final, null).At(new PixelViewport(0, 0, width, height, 0, 1));
        var cmd = gpu.BeginNative("ui composite");
        cmd.BeginRendering(t.Rendering);
        var state = DrawState.For(t.Formats, gpu.DepthClamp);
        state.Record(cmd, t);
        cmd.BindPipeline(gpu.Pipelines.Get(state.Pipeline(program.Program, program.VertexLayout([]), PrimitiveType.Triangles, t.Formats, "ui composite")));
        program.Bind(samplerUi, pointSampler with { View = texture.View() });
        program.Bind(samplerBack, pointSampler with { View = world.View() });
        program.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        gpu.EndNative(cmd);
        return final;
    }

    public void Dispose() { texture?.Dispose(); final?.Dispose(); }
}
