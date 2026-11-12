using Meitou.Game.Rendering.Gpu;
using SharpMetal.Metal;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// GL's vocabulary on Metal (the twin of GlConventions, which maps it to Vulkan): pixel formats, filters,
/// address and compare modes, blend factors and equations, primitives, cull and winding. The front face and
/// the viewport pass through unchanged: Metal's NDC points up as GL's does, so the engine's GL-style shaders
/// need no flip (the Vulkan path's clockwise mapping is its own). Formats the sky pass does not use throw.
/// </summary>
public static class MetalConventions
{
    /// <summary>
    /// The Metal pixel format of an engine one (GL's 24-bit depth is a 32-bit float, as Vulkan's).
    /// </summary>
    public static MTLPixelFormat Pixel(InternalFormat f) => f switch
    {
        InternalFormat.Rgba8 => MTLPixelFormat.RGBA8Unorm,
        InternalFormat.Srgb8Alpha8 => MTLPixelFormat.RGBA8UnormsRGB,
        InternalFormat.Rgba16F => MTLPixelFormat.RGBA16Float,
        InternalFormat.Rgba32F => MTLPixelFormat.RGBA32Float,
        InternalFormat.R8 => MTLPixelFormat.R8Unorm,
        InternalFormat.R16 => MTLPixelFormat.R16Unorm,
        InternalFormat.Rg16 => MTLPixelFormat.RG16Unorm,
        InternalFormat.R16F => MTLPixelFormat.R16Float,
        InternalFormat.Rgba16 => MTLPixelFormat.RGBA16Unorm,
        InternalFormat.R32F => MTLPixelFormat.R32Float,
        InternalFormat.Rg16F => MTLPixelFormat.RG16Float,
        InternalFormat.Rg32F => MTLPixelFormat.RG32Float,
        InternalFormat.R8Ui => MTLPixelFormat.R8Uint,
        InternalFormat.R32Ui => MTLPixelFormat.R32Uint,
        InternalFormat.R32I => MTLPixelFormat.R32Sint,
        InternalFormat.R16Ui => MTLPixelFormat.R16Uint,
        InternalFormat.R16I => MTLPixelFormat.R16Sint,
        InternalFormat.Rgba8Ui => MTLPixelFormat.RGBA8Uint,
        InternalFormat.Rgba16Ui => MTLPixelFormat.RGBA16Uint,
        InternalFormat.Rgba32Ui => MTLPixelFormat.RGBA32Uint,
        InternalFormat.Rgba32I => MTLPixelFormat.RGBA32Sint,
        InternalFormat.CompressedRedRgtc1 => MTLPixelFormat.BC4RUnorm,
        InternalFormat.CompressedRgRgtc2 => MTLPixelFormat.BC5RGUnorm,
        InternalFormat.CompressedRgbaBptcUnorm => MTLPixelFormat.BC7RGBAUnorm,
        InternalFormat.DepthComponent24 or InternalFormat.DepthComponent32F => MTLPixelFormat.Depth32Float,
        InternalFormat.Depth24Stencil8 or InternalFormat.Depth32FStencil8 => MTLPixelFormat.Depth32FloatStencil8,
        InternalFormat.CompressedRgbS3TcDxt1Ext or InternalFormat.CompressedRgbaS3TcDxt1Ext => MTLPixelFormat.BC1RGBA,
        InternalFormat.CompressedRgbaS3TcDxt3Ext => MTLPixelFormat.BC2RGBA,
        InternalFormat.CompressedRgbaS3TcDxt5Ext => MTLPixelFormat.BC3RGBA,
        _ => throw new NotSupportedException($"no Metal pixel format for {f}"),
    };

    /// <summary>
    /// Whether the format is a depth (or depth-stencil) format.
    /// </summary>
    public static bool IsDepthFormat(InternalFormat f) =>
        f is InternalFormat.DepthComponent24 or InternalFormat.DepthComponent32F or InternalFormat.Depth24Stencil8 or InternalFormat.Depth32FStencil8;

    /// <summary>
    /// The bytes a texel (or a texture block) takes, as the uploads' row strides need.
    /// </summary>
    public static int TexelBytes(InternalFormat f) => f switch
    {
        InternalFormat.CompressedRgbS3TcDxt1Ext or InternalFormat.CompressedRgbaS3TcDxt1Ext => 8,  // a 4x4 block
        // a 4x4 block
        InternalFormat.CompressedRgbaS3TcDxt3Ext or InternalFormat.CompressedRgbaS3TcDxt5Ext or InternalFormat.CompressedRgbaBptcUnorm => 16,
        InternalFormat.Rgba16F => 8,
        InternalFormat.Rgba32F => 16,
        InternalFormat.R8 => 1,
        InternalFormat.R16 or InternalFormat.R16F => 2,
        InternalFormat.Rg16 or InternalFormat.Rgba16 => 4,
        InternalFormat.R32F => 4,
        InternalFormat.Rg16F => 4,
        InternalFormat.Rg32F => 8,
        InternalFormat.R8Ui => 1,
        InternalFormat.R32Ui or InternalFormat.R32I => 4,
        InternalFormat.R16Ui or InternalFormat.R16I => 2,
        InternalFormat.Rgba8Ui or InternalFormat.Rgba16Ui => 4,
        InternalFormat.Rgba32Ui or InternalFormat.Rgba32I => 16,
        InternalFormat.CompressedRedRgtc1 => 8,
        InternalFormat.CompressedRgRgtc2 => 16,   // BC5: two channels
        InternalFormat.Rgba8 or InternalFormat.Srgb8Alpha8 => 4,
        _ => throw new NotSupportedException($"no texel size for {f}"),
    };

    /// <summary>
    /// 4 for the block formats (BC1's 4x4), else 1.
    /// </summary>
    public static int BlockWidth(InternalFormat f) =>
        f is InternalFormat.CompressedRgbS3TcDxt1Ext or InternalFormat.CompressedRgbaS3TcDxt1Ext or InternalFormat.CompressedRgbaS3TcDxt3Ext
            or InternalFormat.CompressedRgbaS3TcDxt5Ext or InternalFormat.CompressedRedRgtc1 or InternalFormat.CompressedRgRgtc2
            or InternalFormat.CompressedRgbaBptcUnorm
            ? 4
            : 1;

    /// <summary>
    /// The Metal min/mag filter of a GL minification filter (nearest or linear, whatever its mip mode).
    /// </summary>
    public static MTLSamplerMinMagFilter Filter(TextureMinFilter f) =>
        f is TextureMinFilter.Nearest or TextureMinFilter.NearestMipmapNearest or TextureMinFilter.NearestMipmapLinear
            ? MTLSamplerMinMagFilter.Nearest : MTLSamplerMinMagFilter.Linear;

    /// <summary>
    /// The Metal min/mag filter of a GL magnification filter.
    /// </summary>
    public static MTLSamplerMinMagFilter Filter(TextureMagFilter f) =>
        f == TextureMagFilter.Nearest ? MTLSamplerMinMagFilter.Nearest : MTLSamplerMinMagFilter.Linear;

    /// <summary>
    /// The Metal mip filter of a GL minification filter (not mipmapped for the filters without mips).
    /// </summary>
    public static MTLSamplerMipFilter MipFilter(TextureMinFilter f) => f switch
    {
        TextureMinFilter.NearestMipmapNearest or TextureMinFilter.LinearMipmapNearest => MTLSamplerMipFilter.Nearest,
        TextureMinFilter.NearestMipmapLinear or TextureMinFilter.LinearMipmapLinear => MTLSamplerMipFilter.Linear,
        _ => MTLSamplerMipFilter.NotMipmapped,
    };

    /// <summary>
    /// The Metal address mode of a GL wrap mode.
    /// </summary>
    public static MTLSamplerAddressMode AddressMode(TextureWrapMode w) => w switch
    {
        TextureWrapMode.Repeat => MTLSamplerAddressMode.Repeat,
        TextureWrapMode.MirroredRepeat => MTLSamplerAddressMode.MirrorRepeat,
        TextureWrapMode.ClampToEdge => MTLSamplerAddressMode.ClampToEdge,
        TextureWrapMode.ClampToBorder => MTLSamplerAddressMode.ClampToBorderColor,
        _ => throw new NotSupportedException($"no Metal address mode for {w}"),
    };

    /// <summary>
    /// The Metal compare function of a GL depth function.
    /// </summary>
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

    /// <summary>
    /// The Metal blend factor of a GL one.
    /// </summary>
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

    /// <summary>
    /// The Metal blend operation of a GL one.
    /// </summary>
    public static MTLBlendOperation BlendOp(BlendOperation op) => op switch
    {
        BlendOperation.Add => MTLBlendOperation.Add,
        BlendOperation.Subtract => MTLBlendOperation.Subtract,
        BlendOperation.ReverseSubtract => MTLBlendOperation.ReverseSubtract,
        BlendOperation.Min => MTLBlendOperation.Min,
        BlendOperation.Max => MTLBlendOperation.Max,
        _ => throw new NotSupportedException($"no Metal blend operation for {op}"),
    };

    /// <summary>
    /// The Metal primitive of a GL one (points, lines, line strip, triangles, triangle strip).
    /// </summary>
    public static MTLPrimitiveType Primitive(PrimitiveType t) => t switch
    {
        PrimitiveType.Points => MTLPrimitiveType.Point,
        PrimitiveType.Lines => MTLPrimitiveType.Line,
        PrimitiveType.LineStrip => MTLPrimitiveType.LineStrip,
        PrimitiveType.Triangles => MTLPrimitiveType.Triangle,
        PrimitiveType.TriangleStrip => MTLPrimitiveType.TriangleStrip,
        _ => throw new NotSupportedException($"no Metal primitive for {t}"),
    };

    /// <summary>
    /// The Metal index type of an index kind.
    /// </summary>
    public static MTLIndexType IndexType(IndexKind k) => k switch
    {
        IndexKind.U16 => MTLIndexType.UInt16,
        IndexKind.U32 => MTLIndexType.UInt32,
        _ => throw new NotSupportedException($"no Metal index type for {k}"),
    };

    /// <summary>
    /// The bytes of one index of <paramref name="type"/>.
    /// </summary>
    public static ulong IndexBytes(MTLIndexType type) => type == MTLIndexType.UInt32 ? 4ul : 2ul;

    /// <summary>
    /// The Metal cull mode of a GL one; throws for front-and-back.
    /// </summary>
    public static MTLCullMode Cull(CullMode c) => c switch
    {
        CullMode.None => MTLCullMode.None,
        CullMode.Front => MTLCullMode.Front,
        CullMode.Back => MTLCullMode.Back,
        _ => throw new NotSupportedException($"Metal has no front-and-back cull"),
    };

    /// <summary>
    /// The winding passes through: Metal's NDC points up as GL's does (no flip, unlike Vulkan's).
    /// </summary>
    public static MTLWinding Winding(FrontFaceDirection f) =>
        f == FrontFaceDirection.Cw ? MTLWinding.Clockwise : MTLWinding.CounterClockwise;

    /// <summary>
    /// The vertex format of an attribute input (Metal's own vertex format table).
    /// </summary>
    public static MTLVertexFormat VertexFormat(InternalFormat f) => f switch
    {
        InternalFormat.R8 => MTLVertexFormat.UCharNormalized,
        InternalFormat.Rgba8 => MTLVertexFormat.UChar4Normalized,
        InternalFormat.R8Ui => MTLVertexFormat.UChar,
        InternalFormat.R16F => MTLVertexFormat.Half,
        InternalFormat.Rg16F => MTLVertexFormat.Half2,
        InternalFormat.R32F => MTLVertexFormat.Float,
        InternalFormat.Rg32F => MTLVertexFormat.Float2,
        InternalFormat.Rgb32F => MTLVertexFormat.Float3,
        InternalFormat.Rgba32F => MTLVertexFormat.Float4,
        InternalFormat.Rgba16F => MTLVertexFormat.Half4,
        InternalFormat.Rgba16 => MTLVertexFormat.UShort4Normalized,
        InternalFormat.Rgba16Snorm => MTLVertexFormat.Short4Normalized,
        InternalFormat.Rgba8Ui => MTLVertexFormat.UChar4,
        InternalFormat.Rgba32Ui => MTLVertexFormat.UInt4,
        InternalFormat.Rgba32I => MTLVertexFormat.Int4,
        InternalFormat.R32Ui => MTLVertexFormat.UInt,
        InternalFormat.R32I => MTLVertexFormat.Int,
        _ => throw new NotSupportedException($"no Metal vertex format for {f}"),
    };
}
