using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// SMAA's precomputed lookup textures (Jimenez et al., MIT; THIRD_PARTY_NOTICES.md), embedded from assets/smaa: AreaTex, the coverage areas of
/// every orthogonal and diagonal edge pattern by distance, and SearchTex, how far the last bilinear edge fetch of a search overshoots the edge's end
/// (docs/render-post.md "SMAA"). Made on first use and kept for the chain's life.
/// </summary>
sealed class SmaaLookup(IGpuContext ctx) : IDisposable
{
    /// <summary>
    /// AreaTex's size: 160 × 560 (orthogonal patterns in the left half, diagonal in the right; 7 rows of subsample offsets, SMAA 1x uses the
    /// first), stored R8G8.
    /// </summary>
    const int AreaWidth = 160, AreaHeight = 560;

    /// <summary>
    /// SearchTex's size: 64 × 16 (the useful part of a 66 × 33 table), stored R8.
    /// </summary>
    const int SearchWidth = 64, SearchHeight = 16;

    /// <summary>
    /// The context the textures are made on.
    /// </summary>
    readonly IGpuContext _ctx = ctx;

    /// <summary>
    /// The two textures, null until <see cref="Ensure"/> made them.
    /// </summary>
    GpuTexture? _area, _search;

    /// <summary>
    /// AreaTex sampled bilinearly (its lookups land between texels), clamped.
    /// </summary>
    public SampledTexture Area { get; private set; }

    /// <summary>
    /// SearchTex sampled at its texel centres (nearest), clamped.
    /// </summary>
    public SampledTexture Search { get; private set; }

    /// <summary>
    /// Makes the textures the first time it is called.
    /// </summary>
    public void Ensure()
    {
        if (_area is not null) return;

        using var batch = _ctx.Uploads.Begin();
        _area = Upload(batch, InternalFormat.Rgba8, AreaWidth, AreaHeight, ToRgba(Read("smaa-area.rg8")), "post smaa area");
        _search = Upload(batch, InternalFormat.R8, SearchWidth, SearchHeight, Read("smaa-search.r8"), "post smaa search");
        Area = new SampledTexture(Sampler(TextureMinFilter.Linear, TextureMagFilter.Linear), _area.View());
        Search = new SampledTexture(Sampler(TextureMinFilter.Nearest, TextureMagFilter.Nearest), _search.View());
    }

    /// <summary>
    /// Frees the textures (after the frames in flight).
    /// </summary>
    public void Dispose()
    {
        _area?.Dispose();
        _search?.Dispose();
        _area = _search = null;
    }

    /// <summary>
    /// A new texture holding <paramref name="pixels"/>.
    /// </summary>
    static GpuTexture Upload(IGpuUploadBatch batch, InternalFormat format, int w, int h, byte[] pixels, string name)
    {
        var texture = batch.Create(new TextureDesc(format, w, h, Use: TextureUse.Sampled | TextureUse.TransferDst, Name: name));
        batch.Write(texture, 0, 0, new PixelRect(0, 0, (uint)w, (uint)h), pixels);
        return texture;
    }

    /// <summary>
    /// A clamped sampler with these filters and no mips.
    /// </summary>
    GpuSampler Sampler(TextureMinFilter min, TextureMagFilter mag) => _ctx.Samplers.Get(SamplerDesc.FromGl(min, mag, TextureWrapMode.ClampToEdge,
        TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0));

    /// <summary>
    /// An embedded table's bytes.
    /// </summary>
    static byte[] Read(string name)
    {
        using var stream = typeof(SmaaLookup).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded resource {name} is missing");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// R8G8 texels widened to RGBA8 (B and A zero): the engine has no two-channel 8-bit format.
    /// </summary>
    static byte[] ToRgba(byte[] rg)
    {
        var rgba = new byte[rg.Length * 2];
        for (int i = 0; i < rg.Length / 2; i++)
        {
            rgba[4 * i] = rg[2 * i];
            rgba[4 * i + 1] = rg[2 * i + 1];
        }

        return rgba;
    }
}
