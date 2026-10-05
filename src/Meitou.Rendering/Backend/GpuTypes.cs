namespace Meitou.Rendering.Backend;

/// <summary>Which graphics API a device drives.</summary>
public enum GpuBackendKind { OpenGL, Vulkan }

/// <summary>Pixel formats the renderers use (the stored BCn formats are uploaded block for block).</summary>
public enum GpuFormat
{
    R8, RG8, Rgba8, Bgra8, R16, R16F, Rg16F, Rgba16F, R32F, Rg32F, Rgba32F,
    Depth24, Depth32F, Depth24Stencil8,
    Bc1, Bc2, Bc3, Bc4, Bc5,
}

[Flags]
public enum GpuBufferUsage { Vertex = 1, Index = 2, Uniform = 4, Storage = 8, Indirect = 16, TransferSource = 32, TransferDestination = 64 }

[Flags]
public enum GpuTextureUsage { Sampled = 1, RenderTarget = 2, DepthTarget = 4, Storage = 8, TransferSource = 16, TransferDestination = 32 }

public enum GpuTextureKind { Texture2D, Texture2DArray, TextureCube }

public enum GpuFilter { Nearest, Linear }

public enum GpuAddress { Repeat, ClampToEdge, ClampToBorder, MirroredRepeat }

public enum GpuCompare { Never, Less, LessOrEqual, Equal, GreaterOrEqual, Greater, Always }

public enum GpuCull { None, Back, Front }

public enum GpuBlend { Opaque, Alpha, PremultipliedAlpha, Additive }

public enum GpuTopology { Triangles, TriangleStrip, Lines }

public enum GpuIndexType { UInt16, UInt32 }

public enum GpuLoad { Load, Clear, DontCare }

public enum GpuStore { Store, DontCare }

public enum GpuShaderStage { Vertex, Fragment, Compute }

/// <summary>A buffer: size in bytes, usage, and whether the CPU writes it every frame (mapped, per frame in flight).</summary>
public readonly record struct GpuBufferDesc(long Size, GpuBufferUsage Usage, bool Dynamic = false, string? Name = null);

/// <summary>A texture or texture array. <see cref="Layers"/> is the array size (6 for a cube); <see cref="Samples"/> &gt; 1 is multisampled.</summary>
public readonly record struct GpuTextureDesc(
    GpuTextureKind Kind, GpuFormat Format, int Width, int Height, int Layers = 1, int MipLevels = 1, int Samples = 1,
    GpuTextureUsage Usage = GpuTextureUsage.Sampled | GpuTextureUsage.TransferDestination, string? Name = null);

public readonly record struct GpuSamplerDesc(
    GpuFilter Min = GpuFilter.Linear, GpuFilter Mag = GpuFilter.Linear, GpuFilter Mip = GpuFilter.Linear,
    GpuAddress U = GpuAddress.Repeat, GpuAddress V = GpuAddress.Repeat, GpuAddress W = GpuAddress.Repeat,
    float MaxAnisotropy = 1, GpuCompare? Compare = null, float MinLod = 0, float MaxLod = 1000);

/// <summary>
/// One shader stage. OpenGL compiles <see cref="Glsl"/> (GLSL 3.30); Vulkan takes <see cref="SpirV"/> (compiled from GLSL 4.50
/// at build time or startup). A stage may carry both.
/// </summary>
public sealed record GpuShaderSource(GpuShaderStage Stage, string? Glsl = null, byte[]? SpirV = null, string EntryPoint = "main");

/// <summary>A vertex attribute: shader location, component count of floats (or normalized bytes), byte offset, buffer slot.</summary>
public readonly record struct GpuVertexAttribute(int Location, int Components, int Offset, int Slot = 0, bool NormalizedBytes = false);

/// <summary>A vertex buffer slot: stride in bytes and whether it advances per instance.</summary>
public readonly record struct GpuVertexSlot(int Stride, bool PerInstance = false);

/// <summary>Fixed-function state baked into a pipeline (Vulkan's model; OpenGL applies it when the pipeline is bound).</summary>
public readonly record struct GpuRasterState(
    GpuCull Cull = GpuCull.Back, bool Wireframe = false, bool DepthTest = true, bool DepthWrite = true,
    GpuCompare DepthCompare = GpuCompare.LessOrEqual, GpuBlend Blend = GpuBlend.Opaque, bool AlphaToCoverage = false,
    bool DepthClamp = false, float DepthBiasConstant = 0, float DepthBiasSlope = 0, bool ColourWrite = true);

/// <summary>A graphics pipeline: shaders, vertex layout, fixed state and the formats of the targets it draws into.</summary>
public sealed record GpuPipelineDesc(
    GpuShaderSource Vertex, GpuShaderSource Fragment,
    GpuVertexSlot[] Slots, GpuVertexAttribute[] Attributes,
    GpuRasterState State, GpuTopology Topology = GpuTopology.Triangles,
    GpuFormat[]? ColourFormats = null, GpuFormat? DepthFormat = null, int Samples = 1, string? Name = null);

/// <summary>A colour or depth attachment of a render pass: the texture (layer, mip), how it is loaded and stored, the clear value.</summary>
public readonly record struct GpuAttachment(IGpuTexture Texture, GpuLoad Load = GpuLoad.Clear, GpuStore Store = GpuStore.Store,
    System.Numerics.Vector4 Clear = default, float ClearDepth = 1, int Layer = 0, int Mip = 0, IGpuTexture? Resolve = null);

/// <summary>What a render pass draws into. No colour attachments and no depth means the presentation target (window / swapchain).</summary>
public sealed record GpuRenderPassDesc(GpuAttachment[] Colour, GpuAttachment? Depth = null, string? Name = null);

/// <summary>What the device can do (used to choose code paths and fallbacks).</summary>
public sealed record GpuCapabilities(
    GpuBackendKind Kind, string DeviceName, string ApiVersion, int MaxTextureSize, int MaxArrayLayers, int MaxSamples,
    bool Bindless, bool TimestampQueries, bool DedicatedTransferQueue, bool ValidationEnabled);
