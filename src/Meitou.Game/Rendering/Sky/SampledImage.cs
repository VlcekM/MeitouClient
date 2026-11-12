using System.Numerics;
using Meitou.Data.Textures;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// (Phase 8 stage 2.) A native texture with the sampler state of the GL texture it replaces: <see cref="SamplerDesc.FromGl"/> with the GL parameters
/// and, on a mipmapped filter, the upscaler's LOD bias (<see cref="IGpuContext.LodBias"/>), as VkGl's <c>SamplerFor</c> made it; the view covers the
/// levels VkGl's covered. For <see cref="LegacyProgram.Bind"/> and the frame globals (no bindless entry of its own). Render thread only.
/// </summary>
internal sealed class SampledImage : IDisposable
{
    /// <summary>
    /// The context whose sampler cache the sampler comes from.
    /// </summary>
    readonly IGpuContext _ctx;

    /// <summary>
    /// The GL minification filter (mipmapped or not).
    /// </summary>
    readonly TextureMinFilter _min;

    /// <summary>
    /// The GL magnification filter.
    /// </summary>
    readonly TextureMagFilter _mag;

    /// <summary>
    /// The GL wrap of S and T, and of R.
    /// </summary>
    readonly TextureWrapMode _wrap, _wrapR;

    /// <summary>
    /// The LOD bias <see cref="_cached"/> was made with (NaN: none yet).
    /// </summary>
    float _cachedBias = float.NaN;

    /// <summary>
    /// The texture with its sampler at <see cref="_cachedBias"/>.
    /// </summary>
    SampledTexture _cached;

    /// <summary>
    /// A texture with the GL sampler state it stands for.
    /// </summary>
    SampledImage(IGpuContext ctx, GpuTexture texture, TextureMinFilter min, TextureMagFilter mag, TextureWrapMode wrap, TextureWrapMode wrapR)
    {
        this._ctx = ctx;
        Texture = texture;
        (this._min, this._mag, this._wrap, this._wrapR) = (min, mag, wrap, wrapR);
    }

    /// <summary>
    /// The native texture.
    /// </summary>
    public GpuTexture Texture { get; }

    /// <summary>
    /// The sampler and view a draw samples it with now (what VkGl's <c>Sampled</c> gave for the GL texture).
    /// </summary>
    public SampledTexture Sampled()
    {
        float bias = _ctx.LodBias;
        if (!(bias == _cachedBias))
        {
            var sampler = _ctx.Samplers.Get(SamplerDesc.FromGl(_min, _mag, _wrap, _wrap, _wrapR, false, DepthFunction.Lequal, false, 1, false, bias));
            (_cached, _cachedBias) = (new SampledTexture(sampler, Texture.View()), bias);
        }
        return _cached;
    }

    /// <summary>
    /// The whole of a texture's first level as an upload rectangle.
    /// </summary>
    static PixelRect Rect(int width, int height) => new(0, 0, (uint)width, (uint)height);

    /// <summary>
    /// What <c>WorldGl.Texture2D</c> made: RGBA8 from top-first rows, with <paramref name="mipmaps"/> the whole chain made on the GPU as GL's
    /// GenerateMipmap made it in VkGl (<see cref="IGpuCommandList.GenerateMips"/>) and trilinear filtering, else one level, linear; repeating or
    /// clamped to the edge (S and T; R stays GL's default, repeat).
    /// </summary>
    public static SampledImage Rgba8(IGpuContext ctx, RgbaImage image, bool repeat, bool mipmaps, string name) =>
        Rgba8(ctx, image.Width, image.Height, image.Pixels, repeat, mipmaps, name);

    /// <inheritdoc cref="Rgba8(GpuContext, RgbaImage, bool, bool, string)"/>
    public static SampledImage Rgba8(IGpuContext ctx, int width, int height, byte[] rgba, bool repeat, bool mipmaps, string name)
    {
        int levels = mipmaps ? 1 + (int)Math.Floor(Math.Log2(Math.Max(Math.Max(width, height), 1))) : 1;
        using var batch = ctx.Uploads.Begin();
        var t = batch.Create(new TextureDesc(InternalFormat.Rgba8, width, height, levels,
            Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.TransferSrc, Name: name));
        batch.Write(t, 0, 0, Rect(width, height), rgba.AsSpan(0, width * height * 4));
        if (mipmaps) batch.Commands.GenerateMips(t);
        return new SampledImage(ctx, t, mipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear, TextureMagFilter.Linear,
            repeat ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat);
    }

    /// <summary>
    /// A 2D DDS file with every level it has: BC1 blocks as they are (half a byte a texel), any other format decoded to RGBA8; trilinear, repeating
    /// or clamped to the edge. The sky's starfield and planet maps (4096² and 4096 × 2048 BC1: 11 and 6 MB instead of 85 and 43 MB as RGBA8).
    /// <paramref name="bc3Blocks"/>: BC2 and BC3 as blocks too, which the GPU decodes to the same channels (the debris' limb and item maps: decoding
    /// them on the render thread held the first frame of a town's loading screen 0.3 s).
    /// </summary>
    public static SampledImage FromDds(IGpuContext ctx, DdsFile dds, bool repeat, string name, bool bc3Blocks = false)
    {
        var format = dds.Format switch
        {
            DdsFormat.Bc1 => InternalFormat.CompressedRgbaS3TcDxt1Ext,
            DdsFormat.Bc2 when bc3Blocks => InternalFormat.CompressedRgbaS3TcDxt3Ext,
            DdsFormat.Bc3 when bc3Blocks => InternalFormat.CompressedRgbaS3TcDxt5Ext,
            _ => InternalFormat.Rgba8,
        };
        bool blocks = format != InternalFormat.Rgba8;
        using var batch = ctx.Uploads.Begin();
        var t = batch.Create(new TextureDesc(format, dds.Width, dds.Height, dds.MipCount, Name: name));
        for (int level = 0; level < dds.MipCount; level++)
        {
            if (blocks)
            {
                var s = dds.Surface(0, level);
                batch.Write(t, level, 0, Rect(s.Width, s.Height), dds.Data.AsSpan(s.Offset, s.Length));
            }
            else
            {
                var img = DdsDecoder.Decode(dds, 0, level);
                batch.Write(t, level, 0, Rect(img.Width, img.Height), img.Pixels);
            }
        }
        var filter = dds.MipCount > 1 ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear;
        return new SampledImage(ctx, t, filter, TextureMagFilter.Linear, repeat ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge,
            TextureWrapMode.Repeat);
    }

    /// <summary>
    /// RGBA32F, one level, linear, clamped to the edge: the GL texture <c>WaterRenderer</c> made for its biome parameter maps.
    /// </summary>
    public static SampledImage Rgba32F(IGpuContext ctx, Vector4[] data, int width, int height, string name)
    {
        using var batch = ctx.Uploads.Begin();
        var t = batch.Create(new TextureDesc(InternalFormat.Rgba32F, width, height, Name: name));
        batch.Write(t, 0, 0, Rect(width, height), System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan(0, width * height)));
        return new SampledImage(ctx, t, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat);
    }

    /// <summary>
    /// Two RGBA32F layers (the water's biome parameter maps A and B), linear, clamped to the edge.
    /// </summary>
    public static SampledImage Rgba32FArray(IGpuContext ctx, Vector4[] a, Vector4[] b, int width, int height, string name)
    {
        using var batch = ctx.Uploads.Begin();
        var t = batch.Create(new TextureDesc(InternalFormat.Rgba32F, width, height, Layers: 2,
            Kind: TextureKind.Texture2DArray, Name: name));
        batch.Write(t, 0, 0, Rect(width, height), System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan(0, width * height)));
        batch.Write(t, 0, 1, Rect(width, height), System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.AsSpan(0, width * height)));
        return new SampledImage(ctx, t, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat);
    }

    /// <summary>
    /// A cube map from a DDS file with all its mips (RGBA8), faces in the DDS order (+X, −X, +Y, −Y, +Z, −Z) as layers 0..5 (Vulkan's and
    /// GL's face order); trilinear, clamped to the edge on S, T and R (Vulkan's cube sampling is always seamless, as GL's was with
    /// <c>TEXTURE_CUBE_MAP_SEAMLESS</c>). The GL version's <c>MAX_LEVEL</c> was the file's last level, so its view had the file's levels too.
    /// </summary>
    public static SampledImage Cube(IGpuContext ctx, DdsFile dds, string name)
    {
        if (!dds.IsCubemap || dds.ImageCount < 6) throw new InvalidOperationException($"not a cube map: {dds}");
        var first = DdsDecoder.Decode(dds, 0, 0);
        using var batch = ctx.Uploads.Begin();
        var t = batch.Create(new TextureDesc(InternalFormat.Rgba8, first.Width, first.Height, dds.MipCount, Kind: TextureKind.Cube, Name: name));
        for (int face = 0; face < 6; face++)
            for (int level = 0; level < dds.MipCount; level++)
            {
                var img = face == 0 && level == 0 ? first : DdsDecoder.Decode(dds, face, level);
                batch.Write(t, level, face, Rect(img.Width, img.Height), img.Pixels);
            }
        return new SampledImage(ctx, t, TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge);
    }

    /// <summary>
    /// Frees the texture.
    /// </summary>
    public void Dispose() => Texture.Dispose();
}
