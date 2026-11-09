namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// What a texture is used for (the backend's image usage).
/// </summary>
[Flags]
public enum TextureUse
{
    /// <summary>
    /// Sampled by shaders.
    /// </summary>
    Sampled = 1,
    /// <summary>
    /// Read and written as a storage image.
    /// </summary>
    Storage = 2,
    /// <summary>
    /// Rendered into as a colour attachment.
    /// </summary>
    ColourTarget = 4,
    /// <summary>
    /// Rendered into as a depth attachment.
    /// </summary>
    DepthTarget = 8,
    /// <summary>
    /// Copied from.
    /// </summary>
    TransferSrc = 16,
    /// <summary>
    /// Copied into (uploads).
    /// </summary>
    TransferDst = 32,
    /// <summary>
    /// Read as a fragment shading rate attachment.
    /// </summary>
    ShadingRate = 64
}

/// <summary>
/// The shape of a texture.
/// </summary>
public enum TextureKind
{
    /// <summary>
    /// One or more 2D layers (a view of several is a 2D array).
    /// </summary>
    Texture2D,
    /// <summary>
    /// A 2D array.
    /// </summary>
    Texture2DArray,
    /// <summary>
    /// A cube map: six layers per cube.
    /// </summary>
    Cube
}

/// <summary>
/// What a <see cref="GpuTexture"/> is made from (docs/renderer-native.md 2.4). <c>Streamed</c>: it comes and goes as the view moves (a world
/// texture), so its memory is pooled apart from the long-lived images (<c>GpuAllocator</c>'s <c>PoolKey</c>).
/// </summary>
public sealed record TextureDesc(InternalFormat Format, int Width, int Height, int Levels = 1, int Layers = 1, int Samples = 1,
    TextureKind Kind = TextureKind.Texture2D, TextureUse Use = TextureUse.Sampled | TextureUse.TransferDst, string Name = "",
    bool Private = false, bool Streamed = false)
{
    /// <summary>
    /// Whether the format is a depth (or depth-stencil) format.
    /// </summary>
    public bool IsDepth => GlConventions.IsDepthFormat(Format);
}

/// <summary>
/// A texture as a draw samples it: the sampler and the view (combined image sampler, GENERAL layout).
/// </summary>
public readonly record struct SampledTexture(GpuSampler Sampler, GpuView View)
{
    /// <summary>
    /// No view: nothing is bound.
    /// </summary>
    public bool IsNull => View.IsNull;

    /// <summary>
    /// Equality by handle: the view's index and the sampler's are the identity (a view belongs to one texture).
    /// </summary>
    public bool Equals(SampledTexture other) => Sampler == other.Sampler && View == other.View;

    public override int GetHashCode() => HashCode.Combine(Sampler, View);
}

/// <summary>
/// A texture the renderers hold: the backend's identity (the index into the backend's textures) and its description (docs/resource-layer.md). The
/// image is the backend's, and so are the views: the backend cuts them where it binds them.
/// </summary>
public sealed class GpuTexture : IDisposable
{
    /// <summary>
    /// The backend the texture's views and release go through.
    /// </summary>
    readonly IGpuContext _ctx;

    /// <summary>
    /// A handle the backend made for its texture at <paramref name="index"/>.
    /// </summary>
    internal GpuTexture(IGpuContext ctx, TextureDesc desc, int index)
    {
        this._ctx = ctx;
        Desc = desc;
        Index = index;
    }

    /// <summary>
    /// What the texture was made from.
    /// </summary>
    public TextureDesc Desc { get; }

    /// <summary>
    /// The index into the backend's textures.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// A view of a level and layer range with a swizzle (-1 = the rest).
    /// </summary>
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
        return _ctx.View(this, baseLevel, levelCount, baseLayer, layerCount, type, swizzle);
    }

    /// <summary>
    /// A one-level, one-layer 2D view to render into.
    /// </summary>
    public GpuView Attachment(int level = 0, int layer = 0) => _ctx.View(this, level, 1, layer, 1, GpuViewType.Texture2D, default);

    /// <summary>
    /// Releases the texture through the backend.
    /// </summary>
    public void Dispose() => _ctx.ReleaseTexture(this);
}
