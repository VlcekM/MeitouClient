namespace Meitou.Rendering.Gpu;

// The OpenGL enumerations the renderers' calls take (IGl and the VkGl translation). The names and numeric values are those of the
// OpenGL specification's tokens; only the members the code uses are listed, so a renderer states what it needs and nothing more.
// Add a member here only together with its translation in VkGl.

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

public enum BlitFramebufferFilter : int
{
    Nearest = 0x2600,
    Linear = 0x2601,
}

public enum BufferTargetARB : int
{
    ArrayBuffer = 0x8892,
    ElementArrayBuffer = 0x8893,
    UniformBuffer = 0x8A11,
}

public enum BufferUsageARB : int
{
    StreamDraw = 0x88E0,
    StaticDraw = 0x88E4,
    StaticRead = 0x88E5,
    StaticCopy = 0x88E6,
    DynamicDraw = 0x88E8,
}

[Flags]
public enum ClearBufferMask : int
{
    DepthBufferBit = 0x100,
    ColorBufferBit = 0x4000,
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

public enum DrawBufferMode : int
{
    None = 0x0,
}

public enum DrawElementsType : int
{
    UnsignedShort = 0x1403,
    UnsignedInt = 0x1405,
}

public enum EnableCap : int
{
    CullFace = 0xB44,
    DepthTest = 0xB71,
    Blend = 0xBE2,
    ScissorTest = 0xC11,
    PolygonOffsetLine = 0x2A02,
    PolygonOffsetFill = 0x8037,
    Multisample = 0x809D,
    SampleAlphaToCoverage = 0x809E,
    DepthClamp = 0x864F,
    TextureCubeMapSeamless = 0x884F,
    FramebufferSrgb = 0x8DB9,
}

public enum FramebufferAttachment : int
{
    DepthStencilAttachment = 0x821A,
    ColorAttachment0 = 0x8CE0,
    DepthAttachment = 0x8D00,
}

public enum FramebufferTarget : int
{
    ReadFramebuffer = 0x8CA8,
    DrawFramebuffer = 0x8CA9,
    Framebuffer = 0x8D40,
}

public enum FrontFaceDirection : int
{
    CW = 0x900,
    Ccw = 0x901,
}

public enum GetPName : int
{
    Viewport = 0xBA2,
    MaxTextureSize = 0xD33,
    MaxCombinedTextureImageUnits = 0x8B4D,
    DrawFramebufferBinding = 0x8CA6,
    ReadFramebufferBinding = 0x8CAA,
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

public enum PixelFormat : int
{
    DepthComponent = 0x1902,
    Red = 0x1903,
    Rgb = 0x1907,
    Rgba = 0x1908,
    RG = 0x8227,
    RGInteger = 0x8228,
    RedInteger = 0x8D94,
    RgbInteger = 0x8D98,
    RgbaInteger = 0x8D99,
}

public enum PixelStoreParameter : int
{
    UnpackAlignment = 0xCF5,
    PackAlignment = 0xD05,
}

public enum PixelType : int
{
    Byte = 0x1400,
    UnsignedByte = 0x1401,
    Short = 0x1402,
    UnsignedShort = 0x1403,
    UnsignedInt = 0x1405,
    Float = 0x1406,
    HalfFloat = 0x140B,
}

public enum PolygonMode : int
{
    Line = 0x1B01,
    Fill = 0x1B02,
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

public enum ProgramPropertyARB : int
{
    LinkStatus = 0x8B82,
}

public enum QueryCounterTarget : int
{
    Timestamp = 0x8E28,
}

public enum QueryObjectParameterName : int
{
    Result = 0x8866,
    ResultAvailable = 0x8867,
}

public enum QueryTarget : int
{
    TimeElapsed = 0x88BF,
}

public enum ReadBufferMode : int
{
    None = 0x0,
    Back = 0x405,
}

public enum RenderbufferTarget : int
{
    Renderbuffer = 0x8D41,
}

public enum ShaderParameterName : int
{
    CompileStatus = 0x8B81,
}

public enum ShaderType : int
{
    FragmentShader = 0x8B30,
    VertexShader = 0x8B31,
}

public enum TextureCompareMode : int
{
    None = 0x0,
    CompareRefToTexture = 0x884E,
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

public enum TextureParameterName : int
{
    TextureBorderColor = 0x1004,
    TextureMagFilter = 0x2800,
    TextureMinFilter = 0x2801,
    TextureWrapS = 0x2802,
    TextureWrapT = 0x2803,
    TextureWrapR = 0x8072,
    TextureBaseLevel = 0x813C,
    TextureMaxLevel = 0x813D,
    TextureCompareMode = 0x884C,
    TextureCompareFunc = 0x884D,
}

public enum TextureTarget : int
{
    Texture2D = 0xDE1,
    TextureCubeMap = 0x8513,
    TextureCubeMapPositiveX = 0x8515,
    TextureCubeMapNegativeZ = 0x851A,
    Texture2DArray = 0x8C1A,
}

public enum TextureUnit : int
{
    Texture0 = 0x84C0,
}

public enum TextureWrapMode : int
{
    Repeat = 0x2901,
    ClampToBorder = 0x812D,
    ClampToEdge = 0x812F,
    MirroredRepeat = 0x8370,
}

public enum TriangleFace : int
{
    Front = 0x404,
    Back = 0x405,
    FrontAndBack = 0x408,
}

public enum VertexAttribIType : int
{
    UnsignedByte = 0x1401,
}

public enum VertexAttribPointerType : int
{
    Float = 0x1406,
}

