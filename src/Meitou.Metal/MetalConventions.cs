using Meitou.Rendering.Gpu;
using SharpMetal.Metal;

namespace Meitou.Metal;

/// <summary>
/// GL's vocabulary on Metal (the twin of GlConventions, which maps it to Vulkan): pixel formats, filters,
/// address and compare modes, blend factors and equations, primitives, cull and winding. The front face and
/// the viewport pass through unchanged: Metal's NDC points up as GL's does, so the engine's GL-style shaders
/// need no flip (the Vulkan path's clockwise mapping is its own). Formats the sky pass does not use throw.
/// </summary>
public static class MetalConventions
{
    /// <summary>The Metal pixel format of an engine one (GL's 24-bit depth is a 32-bit float, as Vulkan's).</summary>
    public static MTLPixelFormat Pixel(InternalFormat f) => f switch
    {
        InternalFormat.Rgba8 => MTLPixelFormat.RGBA8Unorm,
        InternalFormat.Srgb8Alpha8 => MTLPixelFormat.RGBA8UnormsRGB,
        InternalFormat.Rgba16f => MTLPixelFormat.RGBA16Float,
        InternalFormat.Rgba32f => MTLPixelFormat.RGBA32Float,
        InternalFormat.R8 => MTLPixelFormat.R8Unorm,
        InternalFormat.R16 => MTLPixelFormat.R16Unorm,
        InternalFormat.Rg16 => MTLPixelFormat.RG16Unorm,
        InternalFormat.R16f => MTLPixelFormat.R16Float,
        InternalFormat.Rgba16 => MTLPixelFormat.RGBA16Unorm,
        InternalFormat.R32f => MTLPixelFormat.R32Float,
        InternalFormat.RG16f => MTLPixelFormat.RG16Float,
        InternalFormat.RG32f => MTLPixelFormat.RG32Float,
        InternalFormat.R8ui => MTLPixelFormat.R8Uint,
        InternalFormat.R32ui => MTLPixelFormat.R32Uint,
        InternalFormat.R32i => MTLPixelFormat.R32Sint,
        InternalFormat.R16ui => MTLPixelFormat.R16Uint,
        InternalFormat.R16i => MTLPixelFormat.R16Sint,
        InternalFormat.Rgba8ui => MTLPixelFormat.RGBA8Uint,
        InternalFormat.Rgba16ui => MTLPixelFormat.RGBA16Uint,
        InternalFormat.Rgba32ui => MTLPixelFormat.RGBA32Uint,
        InternalFormat.Rgba32i => MTLPixelFormat.RGBA32Sint,
        InternalFormat.CompressedRedRgtc1 => MTLPixelFormat.BC4RUnorm,
        InternalFormat.CompressedRGRgtc2 => MTLPixelFormat.BC5RGUnorm,
        InternalFormat.CompressedRgbaBptcUnorm => MTLPixelFormat.BC7RGBAUnorm,
        InternalFormat.DepthComponent24 or InternalFormat.DepthComponent32f => MTLPixelFormat.Depth32Float,
        InternalFormat.Depth24Stencil8 or InternalFormat.Depth32fStencil8 => MTLPixelFormat.Depth32FloatStencil8,
        InternalFormat.CompressedRgbS3TCDxt1Ext or InternalFormat.CompressedRgbaS3TCDxt1Ext => MTLPixelFormat.BC1RGBA,
        InternalFormat.CompressedRgbaS3TCDxt3Ext => MTLPixelFormat.BC2RGBA,
        InternalFormat.CompressedRgbaS3TCDxt5Ext => MTLPixelFormat.BC3RGBA,
        _ => throw new NotSupportedException($"no Metal pixel format for {f}"),
    };

    /// <summary>The engine's sRGB rule: the sRGB format is its linear twin's, as GlConventions names Vulkan's.</summary>
    public static bool IsDepthFormat(InternalFormat f) =>
        f is InternalFormat.DepthComponent24 or InternalFormat.DepthComponent32f or InternalFormat.Depth24Stencil8 or InternalFormat.Depth32fStencil8;

    /// <summary>The bytes a texel (or a texture block) takes, as the uploads' row strides need.</summary>
    public static int TexelBytes(InternalFormat f) => f switch
    {
        InternalFormat.CompressedRgbS3TCDxt1Ext or InternalFormat.CompressedRgbaS3TCDxt1Ext => 8,  // a 4x4 block
        InternalFormat.CompressedRgbaS3TCDxt3Ext or InternalFormat.CompressedRgbaS3TCDxt5Ext or InternalFormat.CompressedRgbaBptcUnorm => 16,  // a 4x4 block
        InternalFormat.Rgba16f => 8,
        InternalFormat.Rgba32f => 16,
        InternalFormat.R8 => 1,
        InternalFormat.R16 or InternalFormat.R16f => 2,
        InternalFormat.Rg16 or InternalFormat.Rgba16 => 4,
        InternalFormat.R32f => 4,
        InternalFormat.RG16f => 4,
        InternalFormat.RG32f => 8,
        InternalFormat.R8ui => 1,
        InternalFormat.R32ui or InternalFormat.R32i => 4,
        InternalFormat.R16ui or InternalFormat.R16i => 2,
        InternalFormat.Rgba8ui or InternalFormat.Rgba16ui => 4,
        InternalFormat.Rgba32ui or InternalFormat.Rgba32i => 16,
        InternalFormat.CompressedRedRgtc1 => 8,
        InternalFormat.CompressedRGRgtc2 => 16,   // BC5: two channels
        InternalFormat.Rgba8 or InternalFormat.Srgb8Alpha8 => 4,
        _ => throw new NotSupportedException($"no texel size for {f}"),
    };

    /// <summary>4 for the block formats (BC1's 4x4), else 1.</summary>
    public static int BlockWidth(InternalFormat f) =>
        f is InternalFormat.CompressedRgbS3TCDxt1Ext or InternalFormat.CompressedRgbaS3TCDxt1Ext or InternalFormat.CompressedRgbaS3TCDxt3Ext
            or InternalFormat.CompressedRgbaS3TCDxt5Ext or InternalFormat.CompressedRedRgtc1 or InternalFormat.CompressedRGRgtc2 or InternalFormat.CompressedRgbaBptcUnorm ? 4 : 1;

    public static MTLSamplerMinMagFilter Filter(TextureMinFilter f) =>
        f is TextureMinFilter.Nearest or TextureMinFilter.NearestMipmapNearest or TextureMinFilter.NearestMipmapLinear
            ? MTLSamplerMinMagFilter.Nearest : MTLSamplerMinMagFilter.Linear;

    public static MTLSamplerMinMagFilter Filter(TextureMagFilter f) =>
        f == TextureMagFilter.Nearest ? MTLSamplerMinMagFilter.Nearest : MTLSamplerMinMagFilter.Linear;

    public static MTLSamplerMipFilter MipFilter(TextureMinFilter f) => f switch
    {
        TextureMinFilter.NearestMipmapNearest or TextureMinFilter.LinearMipmapNearest => MTLSamplerMipFilter.Nearest,
        TextureMinFilter.NearestMipmapLinear or TextureMinFilter.LinearMipmapLinear => MTLSamplerMipFilter.Linear,
        _ => MTLSamplerMipFilter.NotMipmapped,
    };

    public static MTLSamplerAddressMode AddressMode(TextureWrapMode w) => w switch
    {
        TextureWrapMode.Repeat => MTLSamplerAddressMode.Repeat,
        TextureWrapMode.MirroredRepeat => MTLSamplerAddressMode.MirrorRepeat,
        TextureWrapMode.ClampToEdge => MTLSamplerAddressMode.ClampToEdge,
        TextureWrapMode.ClampToBorder => MTLSamplerAddressMode.ClampToBorderColor,
        _ => throw new NotSupportedException($"no Metal address mode for {w}"),
    };

    public static MTLCompareFunction Compare(DepthFunction op) => op switch
    {
        DepthFunction.Never => MTLCompareFunction.Never,
        DepthFunction.Less => MTLCompareFunction.Less,
        DepthFunction.Equal => MTLCompareFunction.Equal,
        DepthFunction.Lequal => MTLCompareFunction.LessEqual,
        DepthFunction.Greater => MTLCompareFunction.Greater,
        DepthFunction.Notequal => MTLCompareFunction.NotEqual,
        DepthFunction.Gequal => MTLCompareFunction.GreaterEqual,
        DepthFunction.Always => MTLCompareFunction.Always,
        _ => throw new NotSupportedException($"no Metal compare for {op}"),
    };

    public static MTLBlendFactor BlendFactor(BlendingFactor f) => f switch
    {
        BlendingFactor.Zero => MTLBlendFactor.Zero,
        BlendingFactor.One => MTLBlendFactor.One,
        BlendingFactor.SrcColor => MTLBlendFactor.SourceColor,
        BlendingFactor.OneMinusSrcColor => MTLBlendFactor.OneMinusSourceColor,
        BlendingFactor.SrcAlpha => MTLBlendFactor.SourceAlpha,
        BlendingFactor.OneMinusSrcAlpha => MTLBlendFactor.OneMinusSourceAlpha,
        BlendingFactor.DstAlpha => MTLBlendFactor.DestinationAlpha,
        BlendingFactor.OneMinusDstAlpha => MTLBlendFactor.OneMinusDestinationAlpha,
        BlendingFactor.DstColor => MTLBlendFactor.DestinationColor,
        BlendingFactor.OneMinusDstColor => MTLBlendFactor.OneMinusDestinationColor,
        _ => throw new NotSupportedException($"no Metal blend factor for {f}"),
    };

    public static MTLBlendOperation BlendOp(BlendOperation op) => op switch
    {
        BlendOperation.Add => MTLBlendOperation.Add,
        BlendOperation.Subtract => MTLBlendOperation.Subtract,
        BlendOperation.ReverseSubtract => MTLBlendOperation.ReverseSubtract,
        BlendOperation.Min => MTLBlendOperation.Min,
        BlendOperation.Max => MTLBlendOperation.Max,
        _ => throw new NotSupportedException($"no Metal blend operation for {op}"),
    };

    public static MTLPrimitiveType Primitive(PrimitiveType t) => t switch
    {
        PrimitiveType.Points => MTLPrimitiveType.Point,
        PrimitiveType.Lines => MTLPrimitiveType.Line,
        PrimitiveType.LineStrip => MTLPrimitiveType.LineStrip,
        PrimitiveType.Triangles => MTLPrimitiveType.Triangle,
        PrimitiveType.TriangleStrip => MTLPrimitiveType.TriangleStrip,
        _ => throw new NotSupportedException($"no Metal primitive for {t}"),
    };

    public static MTLIndexType IndexType(IndexKind k) => k switch
    {
        IndexKind.U16 => MTLIndexType.UInt16,
        IndexKind.U32 => MTLIndexType.UInt32,
        _ => throw new NotSupportedException($"no Metal index type for {k}"),
    };

    /// <summary>The bytes of one index of <paramref name="type"/>.</summary>
    public static ulong IndexBytes(MTLIndexType type) => type == MTLIndexType.UInt32 ? 4ul : 2ul;

    public static MTLCullMode Cull(CullMode c) => c switch
    {
        CullMode.None => MTLCullMode.None,
        CullMode.Front => MTLCullMode.Front,
        CullMode.Back => MTLCullMode.Back,
        _ => throw new NotSupportedException($"Metal has no front-and-back cull"),
    };

    /// <summary>The winding passes through: Metal's NDC points up as GL's does (no flip, unlike Vulkan's).</summary>
    public static MTLWinding Winding(FrontFaceDirection f) =>
        f == FrontFaceDirection.CW ? MTLWinding.Clockwise : MTLWinding.CounterClockwise;

    /// <summary>The vertex format of an attribute input (Metal's own vertex format table).</summary>
    public static MTLVertexFormat VertexFormat(InternalFormat f) => f switch
    {
        InternalFormat.R8 => MTLVertexFormat.UCharNormalized,
        InternalFormat.Rgba8 => MTLVertexFormat.UChar4Normalized,
        InternalFormat.R8ui => MTLVertexFormat.UChar,
        InternalFormat.R16f => MTLVertexFormat.Half,
        InternalFormat.RG16f => MTLVertexFormat.Half2,
        InternalFormat.R32f => MTLVertexFormat.Float,
        InternalFormat.RG32f => MTLVertexFormat.Float2,
        InternalFormat.Rgb32f => MTLVertexFormat.Float3,
        InternalFormat.Rgba32f => MTLVertexFormat.Float4,
        InternalFormat.Rgba16f => MTLVertexFormat.Half4,
        InternalFormat.Rgba16 => MTLVertexFormat.UShort4Normalized,
        InternalFormat.Rgba16Snorm => MTLVertexFormat.Short4Normalized,
        InternalFormat.Rgba8ui => MTLVertexFormat.UChar4,
        InternalFormat.Rgba32ui => MTLVertexFormat.UInt4,
        InternalFormat.Rgba32i => MTLVertexFormat.Int4,
        InternalFormat.R32ui => MTLVertexFormat.UInt,
        InternalFormat.R32i => MTLVertexFormat.Int,
        _ => throw new NotSupportedException($"no Metal vertex format for {f}"),
    };
}
