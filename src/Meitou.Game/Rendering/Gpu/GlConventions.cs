using Meitou.Rendering.Gpu.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// The GL-to-Vulkan rules that decide pixels: formats, blend factors, compare functions, wrap modes, swizzles, vertex attribute
/// formats, and the stand-ins GL reads for what is missing (docs/renderer-native.md 2.4). Moved verbatim from the former GL translation
/// layer (wave 2, step 2), which is gone since phase 8 stage 3; the renderers keep stating their resources in this GL vocabulary.
/// </summary>
public static class GlConventions
{
    /// <summary>The Vulkan format of a GL internal format. Nothing is sRGB; <c>DEPTH_COMPONENT24</c> is a 32-bit float depth buffer
    /// (engine choice: the same or finer); RGB8 is stored as RGBA8.</summary>
    public static Format VkFormat(InternalFormat f) => (GLEnum)f switch
    {
        GLEnum.Rgba8 or GLEnum.Rgba => Format.R8G8B8A8Unorm,
        GLEnum.Rgb8 or GLEnum.Rgb => Format.R8G8B8A8Unorm,
        GLEnum.Srgb8Alpha8 => Format.R8G8B8A8Srgb,
        GLEnum.R8 => Format.R8Unorm,
        GLEnum.R8ui => Format.R8Uint,
        GLEnum.RG8 => Format.R8G8Unorm,
        GLEnum.RG16 => Format.R16G16Unorm,
        GLEnum.R16 => Format.R16Unorm,
        GLEnum.R16ui => Format.R16Uint,
        GLEnum.R16i => Format.R16Sint,
        GLEnum.R16f => Format.R16Sfloat,
        GLEnum.RG16f => Format.R16G16Sfloat,
        GLEnum.Rgba16 => Format.R16G16B16A16Unorm,
        (GLEnum)InternalFormat.Rgba16Snorm => Format.R16G16B16A16SNorm,
        GLEnum.Rgba16f => Format.R16G16B16A16Sfloat,
        GLEnum.Rgba16ui => Format.R16G16B16A16Uint,
        GLEnum.R32f => Format.R32Sfloat,
        GLEnum.R32ui => Format.R32Uint,
        GLEnum.R32i => Format.R32Sint,
        GLEnum.RG32f => Format.R32G32Sfloat,
        GLEnum.Rgb32f => Format.R32G32B32Sfloat,
        GLEnum.Rgba32f => Format.R32G32B32A32Sfloat,
        GLEnum.Rgba32ui => Format.R32G32B32A32Uint,
        GLEnum.Rgba32i => Format.R32G32B32A32Sint,
        GLEnum.R11fG11fB10f => Format.B10G11R11UfloatPack32,
        GLEnum.Rgba8ui => Format.R8G8B8A8Uint,
        GLEnum.DepthComponent24 or GLEnum.DepthComponent => Format.D32Sfloat,   // engine choice: 32-bit float depth for GL's 24-bit (same or finer)
        GLEnum.DepthComponent32f => Format.D32Sfloat,
        GLEnum.Depth24Stencil8 => Format.D24UnormS8Uint,
        (GLEnum)InternalFormat.CompressedRgbS3TCDxt1Ext => Format.BC1RgbUnormBlock,
        (GLEnum)InternalFormat.CompressedRgbaS3TCDxt1Ext => Format.BC1RgbaUnormBlock,
        (GLEnum)InternalFormat.CompressedRgbaS3TCDxt3Ext => Format.BC2UnormBlock,
        (GLEnum)InternalFormat.CompressedRgbaS3TCDxt5Ext => Format.BC3UnormBlock,
        GLEnum.CompressedRedRgtc1 => Format.BC4UnormBlock,
        GLEnum.CompressedRGRgtc2 => Format.BC5UnormBlock,
        (GLEnum)InternalFormat.CompressedRgbaBptcUnorm => Format.BC7UnormBlock,
        _ => throw new NotSupportedException($"texture format {f}"),
    };

    /// <summary>The Vulkan format of a GL internal format, or <c>Undefined</c> for none (an attachment that is not there).</summary>
    public static Format VkFormatOrUndefined(InternalFormat f) => f == InternalFormat.None ? Format.Undefined : VkFormat(f);

    /// <summary>The Vulkan load op of the engine's.</summary>
    public static AttachmentLoadOp VkLoadOp(LoadOp op) => op switch
    {
        LoadOp.Load => AttachmentLoadOp.Load,
        LoadOp.Clear => AttachmentLoadOp.Clear,
        _ => AttachmentLoadOp.DontCare,
    };

    /// <summary>The Vulkan index type of the engine's.</summary>
    public static IndexType VkIndexKind(IndexKind kind) => kind == IndexKind.U32 ? IndexType.Uint32 : IndexType.Uint16;

    /// <summary>The Vulkan filters of a GL minification filter (the mip interpolation folded in).</summary>
    public static (Filter Min, SamplerMipmapMode Mip) VkFilters(TextureMinFilter min) => min switch
    {
        TextureMinFilter.Nearest => (Filter.Nearest, SamplerMipmapMode.Nearest),
        TextureMinFilter.Linear => (Filter.Linear, SamplerMipmapMode.Nearest),
        TextureMinFilter.NearestMipmapNearest => (Filter.Nearest, SamplerMipmapMode.Nearest),
        TextureMinFilter.LinearMipmapNearest => (Filter.Linear, SamplerMipmapMode.Nearest),
        TextureMinFilter.NearestMipmapLinear => (Filter.Nearest, SamplerMipmapMode.Linear),
        _ => (Filter.Linear, SamplerMipmapMode.Linear),
    };

    /// <summary>The Vulkan filter of a GL magnification filter.</summary>
    public static Filter VkFilter(TextureMagFilter mag) => mag == TextureMagFilter.Nearest ? Filter.Nearest : Filter.Linear;

    /// <summary>The Vulkan filter of a GL minification filter (the mip interpolation dropped).</summary>
    public static Filter VkFilter(TextureMinFilter min) => min is TextureMinFilter.Nearest or TextureMinFilter.NearestMipmapNearest
        or TextureMinFilter.NearestMipmapLinear ? Filter.Nearest : Filter.Linear;

    /// <summary>Whether the format is a depth buffer (with or without a stencil).</summary>
    public static bool IsDepthFormat(InternalFormat f) => f is InternalFormat.DepthComponent24 or InternalFormat.DepthComponent32f
        or InternalFormat.Depth24Stencil8 or InternalFormat.Depth16;

    /// <summary>The Vulkan aspect of a texture (the depth bit for a depth format, the colour bit otherwise).</summary>
    public static ImageAspectFlags VkAspect(TextureDesc desc) => desc.IsDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;

    /// <summary>Formats sampled with nearest filtering and integer border colours.</summary>
    public static bool IsIntegerFormat(InternalFormat f) => f is InternalFormat.Rgba8ui or InternalFormat.R32ui or InternalFormat.R16ui or InternalFormat.R8ui;

    public static BlendFactor BlendFactor(BlendingFactor f) => f switch
    {
        BlendingFactor.Zero => Silk.NET.Vulkan.BlendFactor.Zero,
        BlendingFactor.One => Silk.NET.Vulkan.BlendFactor.One,
        BlendingFactor.SrcColor => Silk.NET.Vulkan.BlendFactor.SrcColor,
        BlendingFactor.OneMinusSrcColor => Silk.NET.Vulkan.BlendFactor.OneMinusSrcColor,
        BlendingFactor.DstColor => Silk.NET.Vulkan.BlendFactor.DstColor,
        BlendingFactor.OneMinusDstColor => Silk.NET.Vulkan.BlendFactor.OneMinusDstColor,
        BlendingFactor.SrcAlpha => Silk.NET.Vulkan.BlendFactor.SrcAlpha,
        BlendingFactor.OneMinusSrcAlpha => Silk.NET.Vulkan.BlendFactor.OneMinusSrcAlpha,
        BlendingFactor.DstAlpha => Silk.NET.Vulkan.BlendFactor.DstAlpha,
        BlendingFactor.OneMinusDstAlpha => Silk.NET.Vulkan.BlendFactor.OneMinusDstAlpha,
        _ => throw new NotSupportedException($"blend factor {f}"),
    };

    public static CompareOp CompareOp(DepthFunction f) => f switch
    {
        DepthFunction.Never => Silk.NET.Vulkan.CompareOp.Never,
        DepthFunction.Less => Silk.NET.Vulkan.CompareOp.Less,
        DepthFunction.Equal => Silk.NET.Vulkan.CompareOp.Equal,
        DepthFunction.Lequal => Silk.NET.Vulkan.CompareOp.LessOrEqual,
        DepthFunction.Greater => Silk.NET.Vulkan.CompareOp.Greater,
        DepthFunction.Notequal => Silk.NET.Vulkan.CompareOp.NotEqual,
        DepthFunction.Gequal => Silk.NET.Vulkan.CompareOp.GreaterOrEqual,
        _ => Silk.NET.Vulkan.CompareOp.Always,
    };

    public static SamplerAddressMode AddressMode(TextureWrapMode m) => m switch
    {
        TextureWrapMode.ClampToEdge => SamplerAddressMode.ClampToEdge,
        TextureWrapMode.ClampToBorder => SamplerAddressMode.ClampToBorder,
        TextureWrapMode.MirroredRepeat => SamplerAddressMode.MirroredRepeat,
        _ => SamplerAddressMode.Repeat,
    };

    /// <summary>A <c>TEXTURE_SWIZZLE_*</c> value.</summary>
    public static ComponentSwizzle Swizzle(int gl) => (GLEnum)gl switch
    {
        GLEnum.Red => ComponentSwizzle.R,
        GLEnum.Green => ComponentSwizzle.G,
        GLEnum.Blue => ComponentSwizzle.B,
        GLEnum.Alpha => ComponentSwizzle.A,
        GLEnum.Zero => ComponentSwizzle.Zero,
        GLEnum.One => ComponentSwizzle.One,
        _ => ComponentSwizzle.Identity,
    };

    /// <summary>GL's counter-clockwise front faces are clockwise in Vulkan's framebuffer space (same pixel rows, opposite sign of the area).</summary>
    public static FrontFace VkFrontFace(FrontFaceDirection f) => f == FrontFaceDirection.Ccw ? Silk.NET.Vulkan.FrontFace.Clockwise : Silk.NET.Vulkan.FrontFace.CounterClockwise;

    public static PrimitiveTopology Topology(PrimitiveType mode) => mode switch
    {
        PrimitiveType.TriangleStrip => PrimitiveTopology.TriangleStrip,
        PrimitiveType.Triangles => PrimitiveTopology.TriangleList,
        PrimitiveType.Lines => PrimitiveTopology.LineList,
        PrimitiveType.LineStrip => PrimitiveTopology.LineStrip,
        PrimitiveType.Points => PrimitiveTopology.PointList,
        PrimitiveType.TriangleFan => PrimitiveTopology.TriangleFan,
        PrimitiveType.PatchList => PrimitiveTopology.PatchList,
        _ => throw new NotSupportedException($"primitive {mode}"),
    };

    /// <summary>The Vulkan cull mode of a GL cull face.</summary>
    public static CullModeFlags VkCull(CullMode mode) => mode switch
    {
        CullMode.None => CullModeFlags.None,
        CullMode.Front => CullModeFlags.FrontBit,
        CullMode.Back => CullModeFlags.BackBit,
        _ => CullModeFlags.FrontAndBack,
    };

    /// <summary>The Vulkan polygon mode of a GL polygon mode.</summary>
    public static Silk.NET.Vulkan.PolygonMode VkPolygon(PolygonKind kind) => kind switch
    {
        PolygonKind.Point => Silk.NET.Vulkan.PolygonMode.Point,
        PolygonKind.Line => Silk.NET.Vulkan.PolygonMode.Line,
        _ => Silk.NET.Vulkan.PolygonMode.Fill,
    };

    /// <summary>The Vulkan blend equation of a GL one.</summary>
    public static Silk.NET.Vulkan.BlendOp VkBlendOp(BlendOperation op) => op switch
    {
        BlendOperation.Add => Silk.NET.Vulkan.BlendOp.Add,
        BlendOperation.Subtract => Silk.NET.Vulkan.BlendOp.Subtract,
        BlendOperation.ReverseSubtract => Silk.NET.Vulkan.BlendOp.ReverseSubtract,
        BlendOperation.Min => Silk.NET.Vulkan.BlendOp.Min,
        _ => Silk.NET.Vulkan.BlendOp.Max,
    };

    /// <summary>The Vulkan colour mask of a GL one (the channels a draw writes).</summary>
    public static ColorComponentFlags VkColorMask(ColorMask mask)
    {
        var flags = default(ColorComponentFlags);
        if (mask.HasFlag(ColorMask.R)) flags |= ColorComponentFlags.RBit;
        if (mask.HasFlag(ColorMask.G)) flags |= ColorComponentFlags.GBit;
        if (mask.HasFlag(ColorMask.B)) flags |= ColorComponentFlags.BBit;
        if (mask.HasFlag(ColorMask.A)) flags |= ColorComponentFlags.ABit;
        return flags;
    }

    /// <summary>The Vulkan scissor rectangle of a GL one.</summary>
    public static Rect2D VkRect(PixelRect r) => new(new Offset2D(r.X, r.Y), new Extent2D(r.Width, r.Height));

    /// <summary>The Vulkan viewport of a GL one.</summary>
    public static Viewport VkViewport(PixelViewport v) => new(v.X, v.Y, v.Width, v.Height, v.MinDepth, v.MaxDepth);

    /// <summary>The Vulkan format of a GL vertex attribute (<c>glVertexAttribPointer</c> / <c>glVertexAttribIPointer</c>).</summary>
    public static InternalFormat VertexFormat(GLEnum type, int size, bool normalized, bool integer) => (type, size, normalized, integer) switch
    {
        (GLEnum.Float, 1, _, _) => InternalFormat.R32f,
        (GLEnum.Float, 2, _, _) => InternalFormat.RG32f,
        (GLEnum.Float, 3, _, _) => InternalFormat.Rgb32f,
        (GLEnum.Float, 4, _, _) => InternalFormat.Rgba32f,
        (GLEnum.UnsignedByte, 4, true, false) => InternalFormat.Rgba8,
        (GLEnum.UnsignedByte, 4, false, true) => InternalFormat.Rgba8ui,
        (GLEnum.UnsignedShort, 2, true, false) => InternalFormat.Rg16,
        (GLEnum.UnsignedShort, 4, true, false) => InternalFormat.Rgba16,
        (GLEnum.UnsignedShort, 4, false, true) => InternalFormat.Rgba16ui,
        (GLEnum.Int, 1, _, true) => InternalFormat.R32i,
        (GLEnum.UnsignedInt, 1, _, true) => InternalFormat.R32ui,
        _ => throw new NotSupportedException($"vertex attribute {type} x{size} normalized {normalized} integer {integer}"),
    };

    /// <summary>Bytes of one tightly packed vertex attribute (GL's stride 0).</summary>
    public static uint VertexBytes(GLEnum type, int size) => (uint)size * type switch
    {
        GLEnum.UnsignedByte or GLEnum.Byte => 1u,
        GLEnum.UnsignedShort or GLEnum.Short or GLEnum.HalfFloat => 2u,
        _ => 4u,
    };

    /// <summary>The format a disabled vertex attribute is read with (GL's current generic value (0, 0, 0, 1), from <see cref="GpuDefaults.DummyVertex"/>:
    /// float at offset 0, integer at <see cref="GpuDefaults.DummyIntOffset"/>).</summary>
    public static InternalFormat DummyVertexFormat(ScalarKind kind) => kind switch
    {
        ScalarKind.Int => InternalFormat.Rgba32i,
        ScalarKind.UInt or ScalarKind.Bool => InternalFormat.Rgba32ui,
        _ => InternalFormat.Rgba32f,
    };

    /// <summary>The dummy vertex buffer offset a disabled attribute of this kind reads.</summary>
    public static ulong DummyVertexOffset(ScalarKind kind) => kind == ScalarKind.Float ? 0ul : GpuDefaults.DummyIntOffset;

    /// <summary>The kind of texture a sampler reads: 0 2D, 1 2D array, 2 cube (VkGl's per-unit target slots).</summary>
    public static int SamplerSlot(SamplerInfo s) => s.Dimension == SamplerDimension.Cube ? 2 : s.Arrayed ? 1 : 0;

    /// <summary>The target slot of a GL texture target (0 2D, 1 2D array, 2 cube).</summary>
    public static int TargetSlot(TextureTarget t) => t switch
    {
        TextureTarget.Texture2DArray => 1,
        TextureTarget.TextureCubeMap => 2,
        _ => 0,
    };
}

/// <summary>GL's uniform semantics on a CPU copy of a default uniform block (VkGl and the native <c>LegacyProgram</c>).</summary>
public static unsafe class GlUniforms
{
    /// <summary>Copies tightly packed GL values (<paramref name="count"/> elements of <paramref name="columns"/> × <paramref name="rows"/>
    /// 4-byte components) into a default block at the reflected offset and strides. A float written to an int or bool member and an int
    /// written to a float member convert as GL does; bools are stored as 0/1.</summary>
    public static void Scatter(byte[] block, UniformLookup at, byte* src, int rows, int columns, int count, bool isInt)
    {
        var m = at.Member;
        int elements = m.ArrayLength > 0 ? Math.Min(count, m.ArrayLength - at.Element) : Math.Min(count, 1);
        int colStride = m.IsMatrix ? m.MatrixStride : 16;
        int elemStride = m.ArrayLength > 0 ? m.ArrayStride : 0;
        rows = Math.Min(rows, m.Rows);
        columns = Math.Min(columns, m.Columns);
        fixed (byte* dst = block)
            for (int e = 0; e < elements; e++)
                for (int c = 0; c < columns; c++)
                    for (int r = 0; r < rows; r++)
                    {
                        int srcIndex = (e * columns + c) * rows + r;
                        uint bits = ((uint*)src)[srcIndex];
                        // A float written to an int/bool member (glUniform1f on a bool) and an int to a float member convert as GL does.
                        if (m.Kind == ScalarKind.Float && isInt) { float fv = (int)bits; bits = *(uint*)&fv; }
                        else if (m.Kind != ScalarKind.Float && !isInt) bits = (uint)(int)*(float*)&bits;
                        if (m.Kind == ScalarKind.Bool || (m.Kind == ScalarKind.UInt && !isInt)) bits = bits != 0 ? 1u : 0u;
                        *(uint*)(dst + at.Offset + e * elemStride + c * colStride + r * 4) = bits;
                    }
    }
}

// The OpenGL vocabulary the conventions above (and the renderers' texture and vertex descriptions) are stated in: the names and numeric
// values of the OpenGL specification's tokens, only the members the code uses. Kept from the GL layer removed in phase 8 stage 3.
public enum BlendingFactor : int
{
    Zero = 0x0,
    One = 0x1,
    SrcColor = 0x300,
    OneMinusSrcColor = 0x301,
    SrcAlpha = 0x302,
    OneMinusSrcAlpha = 0x303,
    DstAlpha = 0x304,
    OneMinusDstAlpha = 0x305,
    DstColor = 0x306,
    OneMinusDstColor = 0x307,
}

public enum DepthFunction : int
{
    Never = 0x200,
    Less = 0x201,
    Equal = 0x202,
    Lequal = 0x203,
    Greater = 0x204,
    Notequal = 0x205,
    Gequal = 0x206,
    Always = 0x207,
}

public enum FrontFaceDirection : int
{
    CW = 0x900,
    Ccw = 0x901,
}

/// <summary>GL's cull face: the faces a draw discards (the cull is off with <see cref="None"/>).</summary>
public enum CullMode { None, Front, Back, FrontAndBack }

/// <summary>GL's polygon mode: how a polygon's interior is filled.</summary>
public enum PolygonKind { Point, Line, Fill }

/// <summary>GL's blend equation, for colour and for alpha separately.</summary>
public enum BlendOperation { Add, Subtract, ReverseSubtract, Min, Max }

/// <summary>GL's colour mask (glColorMask): the channels a draw writes.</summary>
[Flags]
public enum ColorMask
{
    None = 0,
    R = 1,
    G = 2,
    B = 4,
    A = 8,
    Rgb = R | G | B,
    All = R | G | B | A,
}

/// <summary>GL's scissor rectangle, in pixels.</summary>
public readonly record struct PixelRect(int X, int Y, uint Width, uint Height);

/// <summary>GL's viewport: a pixel rectangle with the depth range.</summary>
public readonly record struct PixelViewport(float X, float Y, float Width, float Height, float MinDepth = 0, float MaxDepth = 1);

public enum GLEnum : int
{
    NoError = 0x0,
    Zero = 0x0,
    One = 0x1,
    Byte = 0x1400,
    UnsignedByte = 0x1401,
    Short = 0x1402,
    UnsignedShort = 0x1403,
    Int = 0x1404,
    UnsignedInt = 0x1405,
    Float = 0x1406,
    HalfFloat = 0x140B,
    DepthComponent = 0x1902,
    Red = 0x1903,
    Green = 0x1904,
    Blue = 0x1905,
    Alpha = 0x1906,
    Rgb = 0x1907,
    Rgba = 0x1908,
    Rgb8 = 0x8051,
    Rgba8 = 0x8058,
    Samples = 0x80A9,
    DepthComponent24 = 0x81A6,
    R8 = 0x8229,
    R16 = 0x822A,
    RG8 = 0x822B,
    RG16 = 0x822C,
    R16f = 0x822D,
    R32f = 0x822E,
    RG16f = 0x822F,
    RG32f = 0x8230,
    Rgba32f = 0x8814,
    Rgba16f = 0x881A,
    CompareRefToTexture = 0x884E,
    Depth24Stencil8 = 0x88F0,
    R11fG11fB10f = 0x8C3A,
    DepthComponent32f = 0x8CAC,
    FramebufferComplete = 0x8CD5,
    MaxSamples = 0x8D57,
    Rgba8ui = 0x8D7C,
    CompressedRedRgtc1 = 0x8DBB,
    CompressedRGRgtc2 = 0x8DBD,
    Srgb8Alpha8 = 0x8C43,
    Rgba16 = 0x805A,
    R8ui = 0x8232,
    Rg16 = 0x822C,
    R16ui = 0x8234,
    R16i = 0x8239,
    R32i = 0x8235,
    R32ui = 0x8236,
    Rgba16ui = 0x8D76,
    Rgba32ui = 0x8D70,
    Rgba32i = 0x8D82,
    Rgb32f = 0x8815,

}

public enum InternalFormat : int
{
    None = 0,
    Rgba8 = 0x8058,
    Rgba16 = 0x805A,
    Rgba16Snorm = 0x8F9B,
    DepthComponent24 = 0x81A6,
    R8 = 0x8229,
    R16 = 0x822A,
    R16f = 0x822D,
    R32f = 0x822E,
    RG16f = 0x822F,
    RG32f = 0x8230,
    R8ui = 0x8232,
    Rg16 = 0x822C,
    R16ui = 0x8234,
    R16i = 0x8239,
    R32i = 0x8235,
    R32ui = 0x8236,
    CompressedRgbS3TCDxt1Ext = 0x83F0,
    CompressedRgbaS3TCDxt1Ext = 0x83F1,
    CompressedRgbaS3TCDxt3Ext = 0x83F2,
    CompressedRgbaS3TCDxt5Ext = 0x83F3,
    Rgba32f = 0x8814,
    Rgb32f = 0x8815,
    Rgba16f = 0x881A,
    R11fG11fB10f = 0x8C3A,
    Srgb8Alpha8 = 0x8C43,
    DepthComponent32f = 0x8CAC,
    Depth24Stencil8 = 0x88F0,
    Rgba8ui = 0x8D7C,
    Rgba16ui = 0x8D76,
    Rgba32ui = 0x8D70,
    Rgba32i = 0x8D82,
    CompressedRedRgtc1 = 0x8DBB,
    CompressedRGRgtc2 = 0x8DBD,
    CompressedRgbaBptcUnorm = 0x8E8C,
    Depth16 = 0x81A5,
    Depth32fStencil8 = 0x8CAD,
}

public enum PrimitiveType : int
{
    Points = 0x0,
    Lines = 0x1,
    LineStrip = 0x3,
    Triangles = 0x4,
    TriangleStrip = 0x5,
    TriangleFan = 0x6,
    PatchList = 0xE,
}

/// <summary>What a render target does with its contents when the pass begins (GL's load op).</summary>
public enum LoadOp : int
{
    Load = 0,
    Clear = 1,
    DontCare = 2,
}

/// <summary>The colour a render target is cleared to.</summary>
public readonly record struct ClearColour(float R, float G, float B, float A)
{
    /// <summary>The clear colour as the backend's own (Vulkan's).</summary>
    public Silk.NET.Vulkan.ClearColorValue Vk => new(R, G, B, A);
}

/// <summary>The index format of a vertex index buffer (GL's index type).</summary>
public enum IndexKind : int
{
    U16 = 0,
    U32 = 1,
}

public enum TextureMagFilter : int
{
    Nearest = 0x2600,
    Linear = 0x2601,
}

public enum TextureMinFilter : int
{
    Nearest = 0x2600,
    Linear = 0x2601,
    NearestMipmapNearest = 0x2700,
    LinearMipmapNearest = 0x2701,
    NearestMipmapLinear = 0x2702,
    LinearMipmapLinear = 0x2703,
}

public enum TextureTarget : int
{
    Texture2D = 0xDE1,
    TextureCubeMap = 0x8513,
    TextureCubeMapPositiveX = 0x8515,
    TextureCubeMapNegativeZ = 0x851A,
    Texture2DArray = 0x8C1A,
}

public enum TextureWrapMode : int
{
    Repeat = 0x2901,
    ClampToBorder = 0x812D,
    ClampToEdge = 0x812F,
    MirroredRepeat = 0x8370,
}
