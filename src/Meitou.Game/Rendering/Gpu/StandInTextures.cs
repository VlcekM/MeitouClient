using Meitou.Game.Rendering.Gpu.Shaders;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// The stand-in textures of a <see cref="GpuContext"/>: what a sampler reads when nothing is bound, one per sampler slot kind, sampled type and
/// depth flag, over the default images (<see cref="GpuDefaults.Texture"/>), and the bindless 2D float entry of the plain one.
/// </summary>
sealed class StandInTextures(GpuContext ctx) : IDisposable
{
    /// <summary>
    /// The bindless index of the 2D float stand-in, once registered.
    /// </summary>
    uint? _standIn2D;

    /// <summary>
    /// The stand-ins made so far, by sampler slot, sampled type and depth flag.
    /// </summary>
    readonly Dictionary<(int, ScalarKind, bool), SampledTexture> _dummies = [];

    /// <summary>
    /// The textures borrowed over the default images, whose views are ours to destroy.
    /// </summary>
    readonly List<GpuTexture> _dummyTextures = [];

    /// <summary>
    /// The stand-in's index in the bindless 2D float array (what a 2D sampler with nothing bound reads, <see cref="For"/>), registered once.
    /// </summary>
    public uint StandIn2D => _standIn2D ??= ctx.Bindless.Register(BindlessKind.Texture2D,
        For(new SamplerInfo("", 0, 0, SamplerDimension.Dim2D, false, false, false, ScalarKind.Float, 0)));

    /// <summary>
    /// The stand-in a sampler reads when nothing is bound: (0, 0, 0, 1), or depth 1 for a shadow sampler.
    /// </summary>
    public SampledTexture For(SamplerInfo sampler)
    {
        int slot = GlConventions.SamplerSlot(sampler);
        var key = (slot, sampler.SampledKind, sampler.Depth);
        if (_dummies.TryGetValue(key, out var s)) return s;

        var d = ctx.Defaults.Texture(slot, sampler.SampledKind, sampler.Depth);
        var kind = slot == 2 ? TextureKind.Cube : slot == 1 ? TextureKind.Texture2DArray : TextureKind.Texture2D;
        var desc = new TextureDesc(d.Format, 1, 1, 1, slot == 2 ? 1 : d.Layers, Kind: kind);
        var t = ctx.BorrowTexture(desc, d.Image.Image);
        bool integer = GlConventions.IsIntegerFormat(d.Format) || d.Format == InternalFormat.R32I;
        var filter = SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.Repeat, TextureWrapMode.Repeat,
            TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, integer, 0);
        var sampled = new SampledTexture(ctx.Samplers.Get(filter), t.View());
        _dummies[key] = sampled;
        _dummyTextures.Add(t);   // borrowed image, but the view cut on it is ours to destroy
        return sampled;
    }

    /// <summary>
    /// Destroys the stand-ins' views, before their images (the context's <see cref="GpuDefaults"/>) go.
    /// </summary>
    public void Dispose()
    {
        foreach (var t in _dummyTextures) t.Dispose();   // the stand-ins' views, before their images (Defaults) go

        _dummyTextures.Clear();
    }
}
