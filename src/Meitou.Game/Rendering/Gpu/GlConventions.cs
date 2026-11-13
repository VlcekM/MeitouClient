using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// The GL-to-Vulkan rules that decide pixels: formats, blend factors, compare functions, wrap modes, swizzles, vertex attribute
/// formats, and the stand-ins GL reads for what is missing (docs/renderer-native.md 2.4). Moved verbatim from the former GL translation
/// layer (wave 2, step 2), which is gone since phase 8 stage 3; the renderers keep stating their resources in this GL vocabulary.
/// </summary>
public static class GlConventions
{
    #region Formats

    /// <summary>
    /// The Vulkan format of a GL internal format. Nothing is sRGB; <c>DEPTH_COMPONENT24</c> is a 32-bit float depth buffer
    /// (engine choice: the same or finer); RGB8 is stored as RGBA8.
    /// </summary>
    public static Format VkFormat(InternalFormat f) => (GlEnum)f switch
    {
        GlEnum.Rgba8 or GlEnum.Rgba => Format.R8G8B8A8Unorm,
        GlEnum.Rgb8 or GlEnum.Rgb => Format.R8G8B8A8Unorm,
        GlEnum.Srgb8Alpha8 => Format.R8G8B8A8Srgb,
        GlEnum.R8 => Format.R8Unorm,
        GlEnum.R8Ui => Format.R8Uint,
        GlEnum.Rg8 => Format.R8G8Unorm,
        GlEnum.Rg16 => Format.R16G16Unorm,
        GlEnum.R16 => Format.R16Unorm,
        GlEnum.R16Ui => Format.R16Uint,
        GlEnum.R16I => Format.R16Sint,
        GlEnum.R16F => Format.R16Sfloat,
        GlEnum.Rg16F => Format.R16G16Sfloat,
        GlEnum.Rgba16 => Format.R16G16B16A16Unorm,
        (GlEnum)InternalFormat.Rgba16Snorm => Format.R16G16B16A16SNorm,
        GlEnum.Rgba16F => Format.R16G16B16A16Sfloat,
        GlEnum.Rgba16Ui => Format.R16G16B16A16Uint,
        GlEnum.R32F => Format.R32Sfloat,
        GlEnum.R32Ui => Format.R32Uint,
        GlEnum.R32I => Format.R32Sint,
        GlEnum.Rg32F => Format.R32G32Sfloat,
        GlEnum.Rgb32F => Format.R32G32B32Sfloat,
        GlEnum.Rgba32F => Format.R32G32B32A32Sfloat,
        GlEnum.Rgba32Ui => Format.R32G32B32A32Uint,
        GlEnum.Rgba32I => Format.R32G32B32A32Sint,
        GlEnum.R11FG11FB10F => Format.B10G11R11UfloatPack32,
        GlEnum.Rgb10A2 => Format.A2B10G10R10UnormPack32,
        GlEnum.Rgba8Ui => Format.R8G8B8A8Uint,
        GlEnum.DepthComponent24 or GlEnum.DepthComponent => Format.D32Sfloat,   // engine choice: 32-bit float depth for GL's 24-bit (same or finer)
        GlEnum.DepthComponent32F => Format.D32Sfloat,
        GlEnum.Depth24Stencil8 => Format.D24UnormS8Uint,
        (GlEnum)InternalFormat.CompressedRgbS3TcDxt1Ext => Format.BC1RgbUnormBlock,
        (GlEnum)InternalFormat.CompressedRgbaS3TcDxt1Ext => Format.BC1RgbaUnormBlock,
        (GlEnum)InternalFormat.CompressedRgbaS3TcDxt3Ext => Format.BC2UnormBlock,
        (GlEnum)InternalFormat.CompressedRgbaS3TcDxt5Ext => Format.BC3UnormBlock,
        GlEnum.CompressedRedRgtc1 => Format.BC4UnormBlock,
        GlEnum.CompressedRgRgtc2 => Format.BC5UnormBlock,
        (GlEnum)InternalFormat.CompressedRgbaBptcUnorm => Format.BC7UnormBlock,
        _ => throw new NotSupportedException($"texture format {f}"),
    };

    /// <summary>
    /// The Vulkan format of a GL internal format, or <c>Undefined</c> for none (an attachment that is not there).
    /// </summary>
    public static Format VkFormatOrUndefined(InternalFormat f) => f == InternalFormat.None ? Format.Undefined : VkFormat(f);

    #endregion

    #region Draw state

    /// <summary>
    /// The Vulkan load op of the engine's.
    /// </summary>
    public static AttachmentLoadOp VkLoadOp(LoadOp op) => op switch
    {
        LoadOp.Load => AttachmentLoadOp.Load,
        LoadOp.Clear => AttachmentLoadOp.Clear,
        _ => AttachmentLoadOp.DontCare,
    };

    /// <summary>
    /// The Vulkan index type of the engine's.
    /// </summary>
    public static IndexType VkIndexKind(IndexKind kind) => kind == IndexKind.U32 ? IndexType.Uint32 : IndexType.Uint16;

    #endregion

    #region Textures

    /// <summary>
    /// The Vulkan filters of a GL minification filter (the mip interpolation folded in).
    /// </summary>
    public static (Filter Min, SamplerMipmapMode Mip) VkFilters(TextureMinFilter min) => min switch
    {
        TextureMinFilter.Nearest => (Filter.Nearest, SamplerMipmapMode.Nearest),
        TextureMinFilter.Linear => (Filter.Linear, SamplerMipmapMode.Nearest),
        TextureMinFilter.NearestMipmapNearest => (Filter.Nearest, SamplerMipmapMode.Nearest),
        TextureMinFilter.LinearMipmapNearest => (Filter.Linear, SamplerMipmapMode.Nearest),
        TextureMinFilter.NearestMipmapLinear => (Filter.Nearest, SamplerMipmapMode.Linear),
        _ => (Filter.Linear, SamplerMipmapMode.Linear),
    };

    /// <summary>
    /// The Vulkan filter of a GL magnification filter.
    /// </summary>
    public static Filter VkFilter(TextureMagFilter mag) => mag == TextureMagFilter.Nearest ? Filter.Nearest : Filter.Linear;

    /// <summary>
    /// The Vulkan filter of a GL minification filter (the mip interpolation dropped).
    /// </summary>
    public static Filter VkFilter(TextureMinFilter min) => min is TextureMinFilter.Nearest or TextureMinFilter.NearestMipmapNearest
        or TextureMinFilter.NearestMipmapLinear ? Filter.Nearest : Filter.Linear;

    /// <summary>
    /// Whether the format is a depth buffer (with or without a stencil).
    /// </summary>
    public static bool IsDepthFormat(InternalFormat f) => f is InternalFormat.DepthComponent24 or InternalFormat.DepthComponent32F
        or InternalFormat.Depth24Stencil8 or InternalFormat.Depth16;

    /// <summary>
    /// The Vulkan aspect of a texture (the depth bit for a depth format, the colour bit otherwise).
    /// </summary>
    public static ImageAspectFlags VkAspect(TextureDesc desc) => desc.IsDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;

    /// <summary>
    /// Formats sampled with nearest filtering and integer border colours.
    /// </summary>
    public static bool IsIntegerFormat(InternalFormat f) =>
        f is InternalFormat.Rgba8Ui or InternalFormat.R32Ui or InternalFormat.R16Ui or InternalFormat.R8Ui;

    #endregion

    #region Blending and depth

    /// <summary>
    /// The Vulkan blend factor of a GL one; throws for a factor the renderers never use.
    /// </summary>
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

    /// <summary>
    /// The Vulkan compare op of a GL depth function; anything not listed (<c>ALWAYS</c>) maps to Always.
    /// </summary>
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

    #endregion

    #region Samplers

    /// <summary>
    /// The Vulkan address mode of a GL wrap mode; anything not listed (<c>REPEAT</c>) maps to Repeat.
    /// </summary>
    public static SamplerAddressMode AddressMode(TextureWrapMode m) => m switch
    {
        TextureWrapMode.ClampToEdge => SamplerAddressMode.ClampToEdge,
        TextureWrapMode.ClampToBorder => SamplerAddressMode.ClampToBorder,
        TextureWrapMode.MirroredRepeat => SamplerAddressMode.MirroredRepeat,
        _ => SamplerAddressMode.Repeat,
    };

    /// <summary>
    /// A <c>TEXTURE_SWIZZLE_*</c> value.
    /// </summary>
    public static ComponentSwizzle Swizzle(int gl) => (GlEnum)gl switch
    {
        GlEnum.Red => ComponentSwizzle.R,
        GlEnum.Green => ComponentSwizzle.G,
        GlEnum.Blue => ComponentSwizzle.B,
        GlEnum.Alpha => ComponentSwizzle.A,
        GlEnum.Zero => ComponentSwizzle.Zero,
        GlEnum.One => ComponentSwizzle.One,
        _ => ComponentSwizzle.Identity,
    };

    #endregion

    #region Pipeline state

    /// <summary>
    /// GL's counter-clockwise front faces are clockwise in Vulkan's framebuffer space (same pixel rows, opposite sign of the area).
    /// </summary>
    public static FrontFace VkFrontFace(FrontFaceDirection f) => f == FrontFaceDirection.Ccw ? FrontFace.Clockwise : FrontFace.CounterClockwise;

    /// <summary>
    /// The Vulkan topology of a GL primitive mode; throws for a mode the renderers never draw.
    /// </summary>
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

    /// <summary>
    /// The Vulkan cull mode of a GL cull face.
    /// </summary>
    public static CullModeFlags VkCull(CullMode mode) => mode switch
    {
        CullMode.None => CullModeFlags.None,
        CullMode.Front => CullModeFlags.FrontBit,
        CullMode.Back => CullModeFlags.BackBit,
        _ => CullModeFlags.FrontAndBack,
    };

    /// <summary>
    /// The Vulkan polygon mode of a GL polygon mode.
    /// </summary>
    public static PolygonMode VkPolygon(PolygonKind kind) => kind switch
    {
        PolygonKind.Point => PolygonMode.Point,
        PolygonKind.Line => PolygonMode.Line,
        _ => PolygonMode.Fill,
    };

    /// <summary>
    /// The Vulkan blend equation of a GL one.
    /// </summary>
    public static BlendOp VkBlendOp(BlendOperation op) => op switch
    {
        BlendOperation.Add => BlendOp.Add,
        BlendOperation.Subtract => BlendOp.Subtract,
        BlendOperation.ReverseSubtract => BlendOp.ReverseSubtract,
        BlendOperation.Min => BlendOp.Min,
        _ => BlendOp.Max,
    };

    /// <summary>
    /// The Vulkan colour mask of a GL one (the channels a draw writes).
    /// </summary>
    public static ColorComponentFlags VkColorMask(ColorMask mask)
    {
        var flags = default(ColorComponentFlags);
        if (mask.HasFlag(ColorMask.R)) flags |= ColorComponentFlags.RBit;
        if (mask.HasFlag(ColorMask.G)) flags |= ColorComponentFlags.GBit;
        if (mask.HasFlag(ColorMask.B)) flags |= ColorComponentFlags.BBit;
        if (mask.HasFlag(ColorMask.A)) flags |= ColorComponentFlags.ABit;
        return flags;
    }

    /// <summary>
    /// The Vulkan scissor rectangle of a GL one.
    /// </summary>
    public static Rect2D VkRect(PixelRect r) => new(new Offset2D(r.X, r.Y), new Extent2D(r.Width, r.Height));

    /// <summary>
    /// The Vulkan viewport of a GL one.
    /// </summary>
    public static Viewport VkViewport(PixelViewport v) => new(v.X, v.Y, v.Width, v.Height, v.MinDepth, v.MaxDepth);

    #endregion

    #region Vertex input

    /// <summary>
    /// The Vulkan format of a GL vertex attribute (<c>glVertexAttribPointer</c> / <c>glVertexAttribIPointer</c>).
    /// </summary>
    public static InternalFormat VertexFormat(GlEnum type, int size, bool normalized, bool integer) => (type, size, normalized, integer) switch
    {
        (GlEnum.Float, 1, _, _) => InternalFormat.R32F,
        (GlEnum.Float, 2, _, _) => InternalFormat.Rg32F,
        (GlEnum.Float, 3, _, _) => InternalFormat.Rgb32F,
        (GlEnum.Float, 4, _, _) => InternalFormat.Rgba32F,
        (GlEnum.UnsignedByte, 4, true, false) => InternalFormat.Rgba8,
        (GlEnum.UnsignedByte, 4, false, true) => InternalFormat.Rgba8Ui,
        (GlEnum.UnsignedShort, 2, true, false) => InternalFormat.Rg16,
        (GlEnum.UnsignedShort, 4, true, false) => InternalFormat.Rgba16,
        (GlEnum.UnsignedShort, 4, false, true) => InternalFormat.Rgba16Ui,
        (GlEnum.Int, 1, _, true) => InternalFormat.R32I,
        (GlEnum.UnsignedInt, 1, _, true) => InternalFormat.R32Ui,
        _ => throw new NotSupportedException($"vertex attribute {type} x{size} normalized {normalized} integer {integer}"),
    };

    /// <summary>
    /// Bytes of one tightly packed vertex attribute (GL's stride 0).
    /// </summary>
    public static uint VertexBytes(GlEnum type, int size) => (uint)size * type switch
    {
        GlEnum.UnsignedByte or GlEnum.Byte => 1u,
        GlEnum.UnsignedShort or GlEnum.Short or GlEnum.HalfFloat => 2u,
        _ => 4u,
    };

    /// <summary>
    /// The format a disabled vertex attribute is read with (GL's current generic value (0, 0, 0, 1), from
    /// <see cref="GpuDefaults.DummyVertex"/>: float at offset 0, integer at <see cref="GpuDefaults.DummyIntOffset"/>).
    /// </summary>
    public static InternalFormat DummyVertexFormat(ScalarKind kind) => kind switch
    {
        ScalarKind.Int => InternalFormat.Rgba32I,
        ScalarKind.UInt or ScalarKind.Bool => InternalFormat.Rgba32Ui,
        _ => InternalFormat.Rgba32F,
    };

    /// <summary>
    /// The dummy vertex buffer offset a disabled attribute of this kind reads.
    /// </summary>
    public static ulong DummyVertexOffset(ScalarKind kind) => kind == ScalarKind.Float ? 0ul : GpuDefaults.DummyIntOffset;

    #endregion

    #region Texture slots

    /// <summary>
    /// The kind of texture a sampler reads: 0 2D, 1 2D array, 2 cube (VkGl's per-unit target slots).
    /// </summary>
    public static int SamplerSlot(SamplerInfo s) => s.Dimension == SamplerDimension.Cube ? 2 : s.Arrayed ? 1 : 0;

    /// <summary>
    /// The target slot of a GL texture target (0 2D, 1 2D array, 2 cube).
    /// </summary>
    public static int TargetSlot(TextureTarget t) => t switch
    {
        TextureTarget.Texture2DArray => 1,
        TextureTarget.TextureCubeMap => 2,
        _ => 0,
    };

    #endregion
}

/// <summary>
/// GL's uniform semantics on a CPU copy of a default uniform block (VkGl and the native <c>LegacyProgram</c>).
/// </summary>
public static unsafe class GlUniforms
{
    /// <summary>
    /// Copies tightly packed GL values (<paramref name="count"/> elements of <paramref name="columns"/> × <paramref name="rows"/>
    /// 4-byte components) into a default block at the reflected offset and strides. A float written to an int or bool member and an int
    /// written to a float member convert as GL does; bools are stored as 0/1.
    /// </summary>
    public static void Scatter(byte[] block, UniformLookup at, byte* src, int rows, int columns, int count, bool isInt)
    {
        var m = at.Member;
        int elements = m.ArrayLength > 0 ? Math.Min(count, m.ArrayLength - at.Element) : Math.Min(count, 1);
        int colStride = m.IsMatrix ? m.MatrixStride : 16;
        int elemStride = m.ArrayLength > 0 ? m.ArrayStride : 0;
        rows = Math.Min(rows, m.Rows);
        columns = Math.Min(columns, m.Columns);
        fixed (byte* dst = block)
        {
            for (int e = 0; e < elements; e++)
            {
                for (int c = 0; c < columns; c++)
                {
                    for (int r = 0; r < rows; r++)
                    {
                        int srcIndex = (e * columns + c) * rows + r;
                        uint bits = ((uint*)src)[srcIndex];

                        // A float written to an int/bool member (glUniform1f on a bool) and an int to a float member convert as GL does.
                        if (m.Kind == ScalarKind.Float && isInt)
                        {
                            float fv = (int)bits;
                            bits = *(uint*)&fv;
                        }
                        else if (m.Kind != ScalarKind.Float && !isInt) bits = (uint)(int)*(float*)&bits;
                        if (m.Kind == ScalarKind.Bool || (m.Kind == ScalarKind.UInt && !isInt))
                            bits = bits != 0 ? 1u : 0u;
                        *(uint*)(dst + at.Offset + e * elemStride + c * colStride + r * 4) = bits;
                    }
                }
            }
        }
    }
}

// The OpenGL vocabulary the conventions above (and the renderers' texture and vertex descriptions) are stated in: the names and numeric
// values of the OpenGL specification's tokens, only the members the code uses. Kept from the GL layer removed in phase 8 stage 3.
/// <summary>
/// GL's blend factor (<c>glBlendFunc</c>), with the OpenGL specification's token values.
/// </summary>
public enum BlendingFactor : int
{
    /// <summary>
    /// <c>GL_ZERO</c>: factor 0.
    /// </summary>
    Zero = 0x0,
    /// <summary>
    /// <c>GL_ONE</c>: factor 1.
    /// </summary>
    One = 0x1,
    /// <summary>
    /// <c>GL_SRC_COLOR</c>: the source (fragment) colour.
    /// </summary>
    SrcColor = 0x300,
    /// <summary>
    /// <c>GL_ONE_MINUS_SRC_COLOR</c>: one minus the source colour.
    /// </summary>
    OneMinusSrcColor = 0x301,
    /// <summary>
    /// <c>GL_SRC_ALPHA</c>: the source alpha.
    /// </summary>
    SrcAlpha = 0x302,
    /// <summary>
    /// <c>GL_ONE_MINUS_SRC_ALPHA</c>: one minus the source alpha.
    /// </summary>
    OneMinusSrcAlpha = 0x303,
    /// <summary>
    /// <c>GL_DST_ALPHA</c>: the destination (render target) alpha.
    /// </summary>
    DstAlpha = 0x304,
    /// <summary>
    /// <c>GL_ONE_MINUS_DST_ALPHA</c>: one minus the destination alpha.
    /// </summary>
    OneMinusDstAlpha = 0x305,
    /// <summary>
    /// <c>GL_DST_COLOR</c>: the destination colour.
    /// </summary>
    DstColor = 0x306,
    /// <summary>
    /// <c>GL_ONE_MINUS_DST_COLOR</c>: one minus the destination colour.
    /// </summary>
    OneMinusDstColor = 0x307,
}

/// <summary>
/// GL's depth compare function (<c>glDepthFunc</c>), with the OpenGL specification's token values.
/// </summary>
public enum DepthFunction : int
{
    /// <summary>
    /// <c>GL_NEVER</c>: the test never passes.
    /// </summary>
    Never = 0x200,
    /// <summary>
    /// <c>GL_LESS</c>: passes when the incoming depth is less than the stored one.
    /// </summary>
    Less = 0x201,
    /// <summary>
    /// <c>GL_EQUAL</c>: passes when the depths are equal.
    /// </summary>
    Equal = 0x202,
    /// <summary>
    /// <c>GL_LEQUAL</c>: passes when the incoming depth is less than or equal to the stored one.
    /// </summary>
    Lequal = 0x203,
    /// <summary>
    /// <c>GL_GREATER</c>: passes when the incoming depth is greater than the stored one.
    /// </summary>
    Greater = 0x204,
    /// <summary>
    /// <c>GL_NOTEQUAL</c>: passes when the depths differ.
    /// </summary>
    Notequal = 0x205,
    /// <summary>
    /// <c>GL_GEQUAL</c>: passes when the incoming depth is greater than or equal to the stored one.
    /// </summary>
    Gequal = 0x206,
    /// <summary>
    /// <c>GL_ALWAYS</c>: the test always passes.
    /// </summary>
    Always = 0x207,
}

/// <summary>
/// GL's front face winding (<c>glFrontFace</c>), with the OpenGL specification's token values; see <see cref="GlConventions.VkFrontFace"/>.
/// </summary>
public enum FrontFaceDirection : int
{
    /// <summary>
    /// <c>GL_CW</c>: clockwise triangles face front.
    /// </summary>
    Cw = 0x900,
    /// <summary>
    /// <c>GL_CCW</c>: counter-clockwise triangles face front (GL's default).
    /// </summary>
    Ccw = 0x901,
}

/// <summary>
/// GL's cull face: the faces a draw discards (the cull is off with <see cref="None"/>).
/// </summary>
public enum CullMode
{
    /// <summary>
    /// No faces are culled.
    /// </summary>
    None,
    /// <summary>
    /// Front faces are culled.
    /// </summary>
    Front,
    /// <summary>
    /// Back faces are culled.
    /// </summary>
    Back,
    /// <summary>
    /// Both front and back faces are culled.
    /// </summary>
    FrontAndBack
}

/// <summary>
/// GL's polygon mode: how a polygon's interior is filled.
/// </summary>
public enum PolygonKind
{
    /// <summary>
    /// Only the vertices are rasterized, as points.
    /// </summary>
    Point,
    /// <summary>
    /// Only the edges are rasterized, as lines.
    /// </summary>
    Line,
    /// <summary>
    /// The interior is filled.
    /// </summary>
    Fill
}

/// <summary>
/// GL's blend equation, for colour and for alpha separately.
/// </summary>
public enum BlendOperation
{
    /// <summary>
    /// Source times its factor plus destination times its factor.
    /// </summary>
    Add,
    /// <summary>
    /// Source term minus destination term.
    /// </summary>
    Subtract,
    /// <summary>
    /// Destination term minus source term.
    /// </summary>
    ReverseSubtract,
    /// <summary>
    /// The smaller of source and destination (factors ignored).
    /// </summary>
    Min,
    /// <summary>
    /// The larger of source and destination (factors ignored).
    /// </summary>
    Max
}

/// <summary>
/// GL's colour mask (glColorMask): the channels a draw writes.
/// </summary>
[Flags]
public enum ColorMask
{
    /// <summary>
    /// No channel is written.
    /// </summary>
    None = 0,
    /// <summary>
    /// The red channel.
    /// </summary>
    R = 1,
    /// <summary>
    /// The green channel.
    /// </summary>
    G = 2,
    /// <summary>
    /// The blue channel.
    /// </summary>
    B = 4,
    /// <summary>
    /// The alpha channel.
    /// </summary>
    A = 8,
    /// <summary>
    /// Red, green and blue (alpha untouched).
    /// </summary>
    Rgb = R | G | B,
    /// <summary>
    /// All four channels.
    /// </summary>
    All = R | G | B | A,
}

/// <summary>
/// GL's scissor rectangle, in pixels.
/// </summary>
public readonly record struct PixelRect(int X, int Y, uint Width, uint Height);

/// <summary>
/// GL's viewport: a pixel rectangle with the depth range.
/// </summary>
public readonly record struct PixelViewport(float X, float Y, float Width, float Height, float MinDepth = 0, float MaxDepth = 1);

/// <summary>
/// OpenGL tokens the conventions compare against (formats, component types, swizzle channels), with the OpenGL specification's values. Several names
/// share a value (<c>NoError</c> and <c>Zero</c> are both 0).
/// </summary>
public enum GlEnum : int
{
    /// <summary>
    /// <c>GL_NO_ERROR</c>.
    /// </summary>
    NoError = 0x0,
    /// <summary>
    /// <c>GL_ZERO</c>: a swizzle reading constant 0.
    /// </summary>
    Zero = 0x0,
    /// <summary>
    /// <c>GL_ONE</c>: a swizzle reading constant 1.
    /// </summary>
    One = 0x1,
    /// <summary>
    /// <c>GL_BYTE</c>: signed 8-bit components (1 byte in <see cref="GlConventions.VertexBytes"/>).
    /// </summary>
    Byte = 0x1400,
    /// <summary>
    /// <c>GL_UNSIGNED_BYTE</c>: unsigned 8-bit components.
    /// </summary>
    UnsignedByte = 0x1401,
    /// <summary>
    /// <c>GL_SHORT</c>: signed 16-bit components.
    /// </summary>
    Short = 0x1402,
    /// <summary>
    /// <c>GL_UNSIGNED_SHORT</c>: unsigned 16-bit components.
    /// </summary>
    UnsignedShort = 0x1403,
    /// <summary>
    /// <c>GL_INT</c>: signed 32-bit components.
    /// </summary>
    Int = 0x1404,
    /// <summary>
    /// <c>GL_UNSIGNED_INT</c>: unsigned 32-bit components.
    /// </summary>
    UnsignedInt = 0x1405,
    /// <summary>
    /// <c>GL_FLOAT</c>: 32-bit float components.
    /// </summary>
    Float = 0x1406,
    /// <summary>
    /// <c>GL_HALF_FLOAT</c>: 16-bit float components.
    /// </summary>
    HalfFloat = 0x140B,
    /// <summary>
    /// <c>GL_DEPTH_COMPONENT</c>: unsized depth format (stored as 32-bit float depth, see <see cref="GlConventions.VkFormat"/>).
    /// </summary>
    DepthComponent = 0x1902,
    /// <summary>
    /// <c>GL_RED</c>: the red channel as a swizzle source.
    /// </summary>
    Red = 0x1903,
    /// <summary>
    /// <c>GL_GREEN</c>: the green channel as a swizzle source.
    /// </summary>
    Green = 0x1904,
    /// <summary>
    /// <c>GL_BLUE</c>: the blue channel as a swizzle source.
    /// </summary>
    Blue = 0x1905,
    /// <summary>
    /// <c>GL_ALPHA</c>: the alpha channel as a swizzle source.
    /// </summary>
    Alpha = 0x1906,
    /// <summary>
    /// <c>GL_RGB</c>: unsized RGB format (stored as RGBA8).
    /// </summary>
    Rgb = 0x1907,
    /// <summary>
    /// <c>GL_RGBA</c>: unsized RGBA format (stored as RGBA8).
    /// </summary>
    Rgba = 0x1908,
    /// <summary>
    /// <c>GL_RGB8</c>: 8-bit RGB (stored as RGBA8).
    /// </summary>
    Rgb8 = 0x8051,
    /// <summary>
    /// <c>GL_RGBA8</c>: 8-bit normalized RGBA.
    /// </summary>
    Rgba8 = 0x8058,
    /// <summary>
    /// <c>GL_SAMPLES</c>.
    /// </summary>
    Samples = 0x80A9,
    /// <summary>
    /// <c>GL_DEPTH_COMPONENT24</c>: 24-bit depth (stored as 32-bit float depth).
    /// </summary>
    DepthComponent24 = 0x81A6,
    /// <summary>
    /// <c>GL_R8</c>: 8-bit normalized red.
    /// </summary>
    R8 = 0x8229,
    /// <summary>
    /// <c>GL_R16</c>: 16-bit normalized red.
    /// </summary>
    R16 = 0x822A,
    /// <summary>
    /// <c>GL_RG8</c>: 8-bit normalized red-green.
    /// </summary>
    Rg8 = 0x822B,
    /// <summary>
    /// <c>GL_R16F</c>: 16-bit float red.
    /// </summary>
    R16F = 0x822D,
    /// <summary>
    /// <c>GL_R32F</c>: 32-bit float red.
    /// </summary>
    R32F = 0x822E,
    /// <summary>
    /// <c>GL_RG16F</c>: 16-bit float red-green.
    /// </summary>
    Rg16F = 0x822F,
    /// <summary>
    /// <c>GL_RG32F</c>: 32-bit float red-green.
    /// </summary>
    Rg32F = 0x8230,
    /// <summary>
    /// <c>GL_RGBA32F</c>: 32-bit float RGBA.
    /// </summary>
    Rgba32F = 0x8814,
    /// <summary>
    /// <c>GL_RGBA16F</c>: 16-bit float RGBA.
    /// </summary>
    Rgba16F = 0x881A,
    /// <summary>
    /// <c>GL_COMPARE_REF_TO_TEXTURE</c>: a depth-compare (shadow) sampler mode.
    /// </summary>
    CompareRefToTexture = 0x884E,
    /// <summary>
    /// <c>GL_DEPTH24_STENCIL8</c>: 24-bit depth with 8-bit stencil.
    /// </summary>
    Depth24Stencil8 = 0x88F0,
    /// <summary>
    /// <c>GL_RGB10_A2</c>: 10-bit normalized RGB with 2-bit alpha.
    /// </summary>
    Rgb10A2 = 0x8059,
    /// <summary>
    /// <c>GL_R11F_G11F_B10F</c>: packed unsigned float RGB.
    /// </summary>
    R11FG11FB10F = 0x8C3A,
    /// <summary>
    /// <c>GL_DEPTH_COMPONENT32F</c>: 32-bit float depth.
    /// </summary>
    DepthComponent32F = 0x8CAC,
    /// <summary>
    /// <c>GL_FRAMEBUFFER_COMPLETE</c>.
    /// </summary>
    FramebufferComplete = 0x8CD5,
    /// <summary>
    /// <c>GL_MAX_SAMPLES</c>.
    /// </summary>
    MaxSamples = 0x8D57,
    /// <summary>
    /// <c>GL_RGBA8UI</c>: 8-bit unsigned integer RGBA.
    /// </summary>
    Rgba8Ui = 0x8D7C,
    /// <summary>
    /// <c>GL_COMPRESSED_RED_RGTC1</c>: BC4 one-channel compression.
    /// </summary>
    CompressedRedRgtc1 = 0x8DBB,
    /// <summary>
    /// <c>GL_COMPRESSED_RG_RGTC2</c>: BC5 two-channel compression.
    /// </summary>
    CompressedRgRgtc2 = 0x8DBD,
    /// <summary>
    /// <c>GL_SRGB8_ALPHA8</c>: 8-bit sRGB RGBA.
    /// </summary>
    Srgb8Alpha8 = 0x8C43,
    /// <summary>
    /// <c>GL_RGBA16</c>: 16-bit normalized RGBA.
    /// </summary>
    Rgba16 = 0x805A,
    /// <summary>
    /// <c>GL_R8UI</c>: 8-bit unsigned integer red.
    /// </summary>
    R8Ui = 0x8232,
    /// <summary>
    /// <c>GL_RG16</c>: 16-bit normalized red-green.
    /// </summary>
    Rg16 = 0x822C,
    /// <summary>
    /// <c>GL_R16UI</c>: 16-bit unsigned integer red.
    /// </summary>
    R16Ui = 0x8234,
    /// <summary>
    /// <c>GL_R16I</c>: 16-bit signed integer red.
    /// </summary>
    R16I = 0x8239,
    /// <summary>
    /// <c>GL_R32I</c>: 32-bit signed integer red.
    /// </summary>
    R32I = 0x8235,
    /// <summary>
    /// <c>GL_R32UI</c>: 32-bit unsigned integer red.
    /// </summary>
    R32Ui = 0x8236,
    /// <summary>
    /// <c>GL_RGBA16UI</c>: 16-bit unsigned integer RGBA.
    /// </summary>
    Rgba16Ui = 0x8D76,
    /// <summary>
    /// <c>GL_RGBA32UI</c>: 32-bit unsigned integer RGBA.
    /// </summary>
    Rgba32Ui = 0x8D70,
    /// <summary>
    /// <c>GL_RGBA32I</c>: 32-bit signed integer RGBA.
    /// </summary>
    Rgba32I = 0x8D82,
    /// <summary>
    /// <c>GL_RGB32F</c>: 32-bit float RGB.
    /// </summary>
    Rgb32F = 0x8815,
}

/// <summary>
/// GL's sized internal formats the renderers state their textures, attachments and vertex attributes in, with the OpenGL specification's token
/// values; <see cref="GlConventions.VkFormat"/> maps them to Vulkan. A new integer format also goes in <see cref="BindlessTable.ScalarOf"/>, which
/// otherwise samples it as float.
/// </summary>
public enum InternalFormat : int
{
    /// <summary>
    /// No format: an attachment that is not there (<see cref="GlConventions.VkFormatOrUndefined"/>).
    /// </summary>
    None = 0,
    /// <summary>
    /// <c>GL_RGBA8</c>: 8-bit normalized RGBA.
    /// </summary>
    Rgba8 = 0x8058,
    /// <summary>
    /// <c>GL_RGBA16</c>: 16-bit normalized RGBA.
    /// </summary>
    Rgba16 = 0x805A,
    /// <summary>
    /// <c>GL_RGBA16_SNORM</c>: 16-bit signed normalized RGBA.
    /// </summary>
    Rgba16Snorm = 0x8F9B,
    /// <summary>
    /// <c>GL_DEPTH_COMPONENT24</c>: 24-bit depth (stored as 32-bit float depth).
    /// </summary>
    DepthComponent24 = 0x81A6,
    /// <summary>
    /// <c>GL_R8</c>: 8-bit normalized red.
    /// </summary>
    R8 = 0x8229,
    /// <summary>
    /// <c>GL_R16</c>: 16-bit normalized red.
    /// </summary>
    R16 = 0x822A,
    /// <summary>
    /// <c>GL_R16F</c>: 16-bit float red.
    /// </summary>
    R16F = 0x822D,
    /// <summary>
    /// <c>GL_R32F</c>: 32-bit float red.
    /// </summary>
    R32F = 0x822E,
    /// <summary>
    /// <c>GL_RG16F</c>: 16-bit float red-green.
    /// </summary>
    Rg16F = 0x822F,
    /// <summary>
    /// <c>GL_RG32F</c>: 32-bit float red-green.
    /// </summary>
    Rg32F = 0x8230,
    /// <summary>
    /// <c>GL_R8UI</c>: 8-bit unsigned integer red.
    /// </summary>
    R8Ui = 0x8232,
    /// <summary>
    /// <c>GL_RG16</c>: 16-bit normalized red-green.
    /// </summary>
    Rg16 = 0x822C,
    /// <summary>
    /// <c>GL_RGB10_A2</c>: 10-bit normalized RGB with 2-bit alpha.
    /// </summary>
    Rgb10A2 = 0x8059,
    /// <summary>
    /// <c>GL_R16UI</c>: 16-bit unsigned integer red.
    /// </summary>
    R16Ui = 0x8234,
    /// <summary>
    /// <c>GL_R16I</c>: 16-bit signed integer red.
    /// </summary>
    R16I = 0x8239,
    /// <summary>
    /// <c>GL_R32I</c>: 32-bit signed integer red.
    /// </summary>
    R32I = 0x8235,
    /// <summary>
    /// <c>GL_R32UI</c>: 32-bit unsigned integer red.
    /// </summary>
    R32Ui = 0x8236,
    /// <summary>
    /// <c>GL_COMPRESSED_RGB_S3TC_DXT1_EXT</c>: BC1 without alpha.
    /// </summary>
    CompressedRgbS3TcDxt1Ext = 0x83F0,
    /// <summary>
    /// <c>GL_COMPRESSED_RGBA_S3TC_DXT1_EXT</c>: BC1 with 1-bit alpha.
    /// </summary>
    CompressedRgbaS3TcDxt1Ext = 0x83F1,
    /// <summary>
    /// <c>GL_COMPRESSED_RGBA_S3TC_DXT3_EXT</c>: BC2.
    /// </summary>
    CompressedRgbaS3TcDxt3Ext = 0x83F2,
    /// <summary>
    /// <c>GL_COMPRESSED_RGBA_S3TC_DXT5_EXT</c>: BC3.
    /// </summary>
    CompressedRgbaS3TcDxt5Ext = 0x83F3,
    /// <summary>
    /// <c>GL_RGBA32F</c>: 32-bit float RGBA.
    /// </summary>
    Rgba32F = 0x8814,
    /// <summary>
    /// <c>GL_RGB32F</c>: 32-bit float RGB.
    /// </summary>
    Rgb32F = 0x8815,
    /// <summary>
    /// <c>GL_RGBA16F</c>: 16-bit float RGBA.
    /// </summary>
    Rgba16F = 0x881A,
    /// <summary>
    /// <c>GL_R11F_G11F_B10F</c>: packed unsigned float RGB.
    /// </summary>
    R11FG11FB10F = 0x8C3A,
    /// <summary>
    /// <c>GL_SRGB8_ALPHA8</c>: 8-bit sRGB RGBA.
    /// </summary>
    Srgb8Alpha8 = 0x8C43,
    /// <summary>
    /// <c>GL_DEPTH_COMPONENT32F</c>: 32-bit float depth.
    /// </summary>
    DepthComponent32F = 0x8CAC,
    /// <summary>
    /// <c>GL_DEPTH24_STENCIL8</c>: 24-bit depth with 8-bit stencil.
    /// </summary>
    Depth24Stencil8 = 0x88F0,
    /// <summary>
    /// <c>GL_RGBA8UI</c>: 8-bit unsigned integer RGBA.
    /// </summary>
    Rgba8Ui = 0x8D7C,
    /// <summary>
    /// <c>GL_RGBA16UI</c>: 16-bit unsigned integer RGBA.
    /// </summary>
    Rgba16Ui = 0x8D76,
    /// <summary>
    /// <c>GL_RGBA32UI</c>: 32-bit unsigned integer RGBA.
    /// </summary>
    Rgba32Ui = 0x8D70,
    /// <summary>
    /// <c>GL_RGBA32I</c>: 32-bit signed integer RGBA.
    /// </summary>
    Rgba32I = 0x8D82,
    /// <summary>
    /// <c>GL_COMPRESSED_RED_RGTC1</c>: BC4 one-channel compression.
    /// </summary>
    CompressedRedRgtc1 = 0x8DBB,
    /// <summary>
    /// <c>GL_COMPRESSED_RG_RGTC2</c>: BC5 two-channel compression.
    /// </summary>
    CompressedRgRgtc2 = 0x8DBD,
    /// <summary>
    /// <c>GL_COMPRESSED_RGBA_BPTC_UNORM</c>: BC7.
    /// </summary>
    CompressedRgbaBptcUnorm = 0x8E8C,
    /// <summary>
    /// <c>GL_DEPTH_COMPONENT16</c>: 16-bit depth.
    /// </summary>
    Depth16 = 0x81A5,
    /// <summary>
    /// <c>GL_DEPTH32F_STENCIL8</c>: 32-bit float depth with 8-bit stencil.
    /// </summary>
    Depth32FStencil8 = 0x8CAD,
}

/// <summary>
/// GL's primitive mode (<c>glDrawArrays</c> mode), with the OpenGL specification's token values; <see cref="GlConventions.Topology"/> maps it to
/// Vulkan.
/// </summary>
public enum PrimitiveType : int
{
    /// <summary>
    /// <c>GL_POINTS</c>: one point per vertex.
    /// </summary>
    Points = 0x0,
    /// <summary>
    /// <c>GL_LINES</c>: a line per vertex pair.
    /// </summary>
    Lines = 0x1,
    /// <summary>
    /// <c>GL_LINE_STRIP</c>: connected lines.
    /// </summary>
    LineStrip = 0x3,
    /// <summary>
    /// <c>GL_TRIANGLES</c>: a triangle per three vertices.
    /// </summary>
    Triangles = 0x4,
    /// <summary>
    /// <c>GL_TRIANGLE_STRIP</c>: each vertex after the second makes a triangle with the previous two.
    /// </summary>
    TriangleStrip = 0x5,
    /// <summary>
    /// <c>GL_TRIANGLE_FAN</c>: triangles sharing the first vertex.
    /// </summary>
    TriangleFan = 0x6,
    /// <summary>
    /// <c>GL_PATCHES</c>: tessellation patches.
    /// </summary>
    PatchList = 0xE,
}

/// <summary>
/// What a render target does with its contents when the pass begins (GL's load op).
/// </summary>
public enum LoadOp : int
{
    /// <summary>
    /// Keep the previous contents.
    /// </summary>
    Load = 0,
    /// <summary>
    /// Clear to the pass's clear value.
    /// </summary>
    Clear = 1,
    /// <summary>
    /// The previous contents are not needed (undefined).
    /// </summary>
    DontCare = 2,
}

/// <summary>
/// The colour a render target is cleared to.
/// </summary>
public readonly record struct ClearColour(float R, float G, float B, float A)
{
    /// <summary>
    /// The clear colour as the backend's own (Vulkan's).
    /// </summary>
    public ClearColorValue Vk => new(R, G, B, A);
}

/// <summary>
/// The index format of a vertex index buffer (GL's index type).
/// </summary>
public enum IndexKind : int
{
    /// <summary>
    /// 16-bit unsigned indices.
    /// </summary>
    U16 = 0,
    /// <summary>
    /// 32-bit unsigned indices.
    /// </summary>
    U32 = 1,
}

/// <summary>
/// GL's magnification filter, with the OpenGL specification's token values.
/// </summary>
public enum TextureMagFilter : int
{
    /// <summary>
    /// <c>GL_NEAREST</c>: the nearest texel.
    /// </summary>
    Nearest = 0x2600,
    /// <summary>
    /// <c>GL_LINEAR</c>: bilinear between the four nearest texels.
    /// </summary>
    Linear = 0x2601,
}

/// <summary>
/// GL's minification filter (texel filter and mip selection), with the OpenGL specification's token values.
/// </summary>
public enum TextureMinFilter : int
{
    /// <summary>
    /// <c>GL_NEAREST</c>: the nearest texel of the base level, no mips.
    /// </summary>
    Nearest = 0x2600,
    /// <summary>
    /// <c>GL_LINEAR</c>: bilinear on the base level, no mips.
    /// </summary>
    Linear = 0x2601,
    /// <summary>
    /// <c>GL_NEAREST_MIPMAP_NEAREST</c>: nearest texel of the nearest mip.
    /// </summary>
    NearestMipmapNearest = 0x2700,
    /// <summary>
    /// <c>GL_LINEAR_MIPMAP_NEAREST</c>: bilinear in the nearest mip.
    /// </summary>
    LinearMipmapNearest = 0x2701,
    /// <summary>
    /// <c>GL_NEAREST_MIPMAP_LINEAR</c>: nearest texel, blended between two mips.
    /// </summary>
    NearestMipmapLinear = 0x2702,
    /// <summary>
    /// <c>GL_LINEAR_MIPMAP_LINEAR</c>: bilinear, blended between two mips (trilinear).
    /// </summary>
    LinearMipmapLinear = 0x2703,
}

/// <summary>
/// GL's texture target, with the OpenGL specification's token values; <see cref="GlConventions.TargetSlot"/> maps it to a slot.
/// </summary>
public enum TextureTarget : int
{
    /// <summary>
    /// <c>GL_TEXTURE_2D</c>.
    /// </summary>
    Texture2D = 0xDE1,
    /// <summary>
    /// <c>GL_TEXTURE_CUBE_MAP</c>.
    /// </summary>
    TextureCubeMap = 0x8513,
    /// <summary>
    /// <c>GL_TEXTURE_CUBE_MAP_POSITIVE_X</c>: the first cube face (faces follow in +X, -X, +Y, -Y, +Z, -Z order).
    /// </summary>
    TextureCubeMapPositiveX = 0x8515,
    /// <summary>
    /// <c>GL_TEXTURE_CUBE_MAP_NEGATIVE_Z</c>: the last cube face.
    /// </summary>
    TextureCubeMapNegativeZ = 0x851A,
    /// <summary>
    /// <c>GL_TEXTURE_2D_ARRAY</c>.
    /// </summary>
    Texture2DArray = 0x8C1A,
}

/// <summary>
/// GL's texture wrap mode, with the OpenGL specification's token values; <see cref="GlConventions.AddressMode"/> maps it to Vulkan.
/// </summary>
public enum TextureWrapMode : int
{
    /// <summary>
    /// <c>GL_REPEAT</c>: the texture tiles.
    /// </summary>
    Repeat = 0x2901,
    /// <summary>
    /// <c>GL_CLAMP_TO_BORDER</c>: outside reads the border colour.
    /// </summary>
    ClampToBorder = 0x812D,
    /// <summary>
    /// <c>GL_CLAMP_TO_EDGE</c>: outside reads the edge texel.
    /// </summary>
    ClampToEdge = 0x812F,
    /// <summary>
    /// <c>GL_MIRRORED_REPEAT</c>: the texture tiles, every other tile mirrored.
    /// </summary>
    MirroredRepeat = 0x8370,
}