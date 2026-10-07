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
        GLEnum.R8 => Format.R8Unorm,
        GLEnum.RG8 => Format.R8G8Unorm,
        GLEnum.R16 => Format.R16Unorm,
        GLEnum.R16f => Format.R16Sfloat,
        GLEnum.RG16f => Format.R16G16Sfloat,
        GLEnum.Rgba16f => Format.R16G16B16A16Sfloat,
        GLEnum.R32f => Format.R32Sfloat,
        GLEnum.RG32f => Format.R32G32Sfloat,
        GLEnum.Rgba32f => Format.R32G32B32A32Sfloat,
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
        _ => throw new NotSupportedException($"texture format {f}"),
    };

    public static bool IsDepthFormat(Format f) => f is Format.D32Sfloat or Format.D24UnormS8Uint or Format.X8D24UnormPack32 or Format.D16Unorm;

    /// <summary>Formats sampled with nearest filtering and integer border colours.</summary>
    public static bool IsIntegerFormat(Format f) => f is Format.R8G8B8A8Uint or Format.R32Uint or Format.R16Uint;

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
    public static FrontFace FrontFace(FrontFaceDirection f) => f == FrontFaceDirection.Ccw ? Silk.NET.Vulkan.FrontFace.Clockwise : Silk.NET.Vulkan.FrontFace.CounterClockwise;

    public static PrimitiveTopology Topology(PrimitiveType mode) => mode switch
    {
        PrimitiveType.TriangleStrip => PrimitiveTopology.TriangleStrip,
        PrimitiveType.Triangles => PrimitiveTopology.TriangleList,
        PrimitiveType.Lines => PrimitiveTopology.LineList,
        PrimitiveType.LineStrip => PrimitiveTopology.LineStrip,
        PrimitiveType.Points => PrimitiveTopology.PointList,
        PrimitiveType.TriangleFan => PrimitiveTopology.TriangleFan,
        _ => throw new NotSupportedException($"primitive {mode}"),
    };

    /// <summary>The Vulkan format of a GL vertex attribute (<c>glVertexAttribPointer</c> / <c>glVertexAttribIPointer</c>).</summary>
    public static Format VertexFormat(GLEnum type, int size, bool normalized, bool integer) => (type, size, normalized, integer) switch
    {
        (GLEnum.Float, 1, _, _) => Format.R32Sfloat,
        (GLEnum.Float, 2, _, _) => Format.R32G32Sfloat,
        (GLEnum.Float, 3, _, _) => Format.R32G32B32Sfloat,
        (GLEnum.Float, 4, _, _) => Format.R32G32B32A32Sfloat,
        (GLEnum.UnsignedByte, 4, true, false) => Format.R8G8B8A8Unorm,
        (GLEnum.UnsignedByte, 4, false, true) => Format.R8G8B8A8Uint,
        (GLEnum.UnsignedByte, 4, false, false) => Format.R8G8B8A8Uscaled,
        (GLEnum.UnsignedShort, 2, true, false) => Format.R16G16Unorm,
        (GLEnum.UnsignedShort, 4, true, false) => Format.R16G16B16A16Unorm,
        (GLEnum.UnsignedShort, 4, false, true) => Format.R16G16B16A16Uint,
        (GLEnum.Int, 1, _, true) => Format.R32Sint,
        (GLEnum.UnsignedInt, 1, _, true) => Format.R32Uint,
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
    public static Format DummyVertexFormat(ScalarKind kind) => kind switch
    {
        ScalarKind.Int => Format.R32G32B32A32Sint,
        ScalarKind.UInt or ScalarKind.Bool => Format.R32G32B32A32Uint,
        _ => Format.R32G32B32A32Sfloat,
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
}

public enum FrontFaceDirection : int
{
    CW = 0x900,
    Ccw = 0x901,
}

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
}

public enum InternalFormat : int
{
    Rgba8 = 0x8058,
    DepthComponent24 = 0x81A6,
    R8 = 0x8229,
    R16 = 0x822A,
    R32f = 0x822E,
    RG16f = 0x822F,
    RG32f = 0x8230,
    CompressedRgbS3TCDxt1Ext = 0x83F0,
    CompressedRgbaS3TCDxt1Ext = 0x83F1,
    CompressedRgbaS3TCDxt3Ext = 0x83F2,
    CompressedRgbaS3TCDxt5Ext = 0x83F3,
    Rgba32f = 0x8814,
    Rgba16f = 0x881A,
    R11fG11fB10f = 0x8C3A,
    DepthComponent32f = 0x8CAC,
    Rgba8ui = 0x8D7C,
}

public enum PrimitiveType : int
{
    Points = 0x0,
    Lines = 0x1,
    LineStrip = 0x3,
    Triangles = 0x4,
    TriangleStrip = 0x5,
    TriangleFan = 0x6,
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
