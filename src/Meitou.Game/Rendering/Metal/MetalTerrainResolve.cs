#if MEITOU_METAL
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Post;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// The terrain's resolved colour on Metal (docs/render-terrain.md "Resolved colour"; <c>TerrainRenderer.Resolve</c>): the prepass's own target and
/// the relief pass's two, at the scene's size, with their bindless registrations; the Vulkan chain's are <c>PostProcess.TerrainResolveTargets</c>.
/// </summary>
sealed class MetalTerrainResolve(IGpuContext ctx) : IDisposable
{
    /// <summary>
    /// A target and its bindless registration (0 when none).
    /// </summary>
    struct Held
    {
        /// <summary>
        /// The target.
        /// </summary>
        public GpuTexture? Texture;

        /// <summary>
        /// Its registration in the bindless 2D table.
        /// </summary>
        public uint Index;
    }

    /// <summary>
    /// The prepass's target (RGBA32F: the undisplaced point less the drawn one, the fragment's depth) and the relief pass's (RGBA16F).
    /// </summary>
    Held _g, _reliefA, _reliefB;

    /// <summary>
    /// The scene depth registered, and the texture it was registered for.
    /// </summary>
    Held _depth;

    /// <summary>
    /// This frame's targets for the resolved colour as <c>PostProcess.TerrainResolveTargets</c> gives them: the prepass's (its target and
    /// <paramref name="scene"/>'s depth), the colour pass's (<paramref name="scene"/> without depth), the relief pass's when <paramref name="relief"/>
    /// and the bindless indices the colour pass reads.
    /// </summary>
    public void Targets(PassTargets scene, GpuTexture depth, bool relief, out PassTargets prepass, out PassTargets colour,
        out PassTargets? reliefTargets, out TerrainResolveIndices inputs)
    {
        int width = scene.Width, height = scene.Height;
        var g = Sized(ref _g, InternalFormat.Rgba32F, width, height, "terrain resolve");
        inputs = new TerrainResolveIndices(_g.Index, Registered(ref _depth, depth), 0, 0);
        prepass = PassTargets.Of(g, depth);
        colour = scene with { Depth = default, Formats = scene.Formats with { Depth = InternalFormat.None } };
        reliefTargets = null;
        if (!relief)
        {
            Release(ref _reliefA);
            Release(ref _reliefB);
            return;
        }

        var a = Sized(ref _reliefA, InternalFormat.Rgba16F, width, height, "terrain relief a");
        var b = Sized(ref _reliefB, InternalFormat.Rgba16F, width, height, "terrain relief b");
        inputs = inputs with { ReliefA = _reliefA.Index, ReliefB = _reliefB.Index };
        reliefTargets = PassTargets.Of(a, null, b);
    }

    /// <summary>
    /// <paramref name="held"/>'s target, made anew (and registered) in <paramref name="format"/> when missing or of another size.
    /// </summary>
    GpuTexture Sized(ref Held held, InternalFormat format, int width, int height, string name)
    {
        if (held.Texture is { } made && made.Desc.Width == width && made.Desc.Height == height) return made;

        Release(ref held);
        var texture = ctx.CreateTexture(new TextureDesc(format, width, height, Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: name));
        held = new Held { Texture = texture, Index = Register(texture) };

        return texture;
    }

    /// <summary>
    /// The registration of <paramref name="texture"/>, which the caller owns (the scene's depth), made again when it is another texture.
    /// </summary>
    uint Registered(ref Held held, GpuTexture texture)
    {
        if (held.Texture == texture) return held.Index;
        if (held.Texture is not null) ctx.Bindless.Free(BindlessKind.Texture2D, held.Index);

        held = new Held { Texture = texture, Index = Register(texture) };
        return held.Index;
    }

    /// <summary>
    /// <paramref name="texture"/> in the bindless 2D table, read texel by texel (nearest, clamped).
    /// </summary>
    uint Register(GpuTexture texture)
    {
        var sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0));
        return ctx.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, texture.View()));
    }

    /// <summary>
    /// Frees one of this class's own targets and its registration.
    /// </summary>
    void Release(ref Held held)
    {
        if (held.Texture is null) return;

        ctx.Bindless.Free(BindlessKind.Texture2D, held.Index);
        held.Texture.Dispose();
        held = default;
    }

    /// <summary>
    /// Frees the targets and every registration (the scene's depth stays its owner's).
    /// </summary>
    public void Dispose()
    {
        Release(ref _g);
        Release(ref _reliefA);
        Release(ref _reliefB);
        if (_depth.Texture is not null) ctx.Bindless.Free(BindlessKind.Texture2D, _depth.Index);
        _depth = default;
    }
}
#endif
