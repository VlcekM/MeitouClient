namespace Meitou.Rendering.Gpu;

[Flags]
public enum TextureUse { Sampled = 1, Storage = 2, ColourTarget = 4, DepthTarget = 8, TransferSrc = 16, TransferDst = 32, ShadingRate = 64 }

public enum TextureKind { Texture2D, Texture2DArray, Cube }

/// <summary>What a <see cref="GpuTexture"/> is made from (docs/renderer-native.md 2.4). <c>Streamed</c>: it comes and goes as the view moves (a world texture),
/// so its memory is pooled apart from the long-lived images (<c>GpuAllocator</c>'s <c>PoolKey</c>).</summary>
public sealed record TextureDesc(InternalFormat Format, int Width, int Height, int Levels = 1, int Layers = 1, int Samples = 1,
    TextureKind Kind = TextureKind.Texture2D, TextureUse Use = TextureUse.Sampled | TextureUse.TransferDst, string Name = "",
    bool Private = false, bool Streamed = false)
{
    public bool IsDepth => GlConventions.IsDepthFormat(Format);
}

/// <summary>A texture as a draw samples it: the sampler and the view (combined image sampler, GENERAL layout).</summary>
public readonly record struct SampledTexture(GpuSampler Sampler, GpuView View)
{
    public bool IsNull => View.IsNull;

    // By handle: the view's index and the sampler's are the identity (a view belongs to one texture).
    public bool Equals(SampledTexture other) => Sampler == other.Sampler && View == other.View;
    public override int GetHashCode() => HashCode.Combine(Sampler, View);
}

/// <summary>
/// A texture the renderers hold: the backend's identity (the index into the backend's textures) and
/// its description (docs/resource-layer.md). The image is the backend's, and so are the views: the
/// backend cuts them where it binds them.
/// </summary>
public sealed class GpuTexture : IDisposable
{
    readonly IGpuContext ctx;

    internal GpuTexture(IGpuContext ctx, TextureDesc desc, int index)
    {
        this.ctx = ctx;
        Desc = desc;
        Index = index;
    }

    public TextureDesc Desc { get; }

    /// <summary>The index into the backend's textures.</summary>
    public int Index { get; }

    /// <summary>A view of a level and layer range with a swizzle (-1 = the rest).</summary>
    public GpuView View(int baseLevel = 0, int levels = -1, int baseLayer = 0, int layers = -1, GpuSwizzle swizzle = default)
    {
        int levelCount = levels < 0 ? Desc.Levels - baseLevel : levels;
        int totalLayers = Desc.Kind == TextureKind.Cube ? 6 * Math.Max(Desc.Layers, 1) : Desc.Layers;
        int layerCount = layers < 0 ? totalLayers - baseLayer : layers;
        var type = Desc.Kind switch
        {
            TextureKind.Cube => GpuViewType.Cube,
            TextureKind.Texture2DArray => GpuViewType.Texture2DArray,
            _ => layerCount > 1 ? GpuViewType.Texture2DArray : GpuViewType.Texture2D,
        };
        return ctx.View(this, baseLevel, levelCount, baseLayer, layerCount, type, swizzle);
    }

    /// <summary>A one-level, one-layer 2D view to render into.</summary>
    public GpuView Attachment(int level = 0, int layer = 0) => ctx.View(this, level, 1, layer, 1, GpuViewType.Texture2D, default);

    public void Dispose() => ctx.ReleaseTexture(this);
}
