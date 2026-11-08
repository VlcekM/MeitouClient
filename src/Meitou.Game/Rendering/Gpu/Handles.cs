using Meitou.Rendering.Gpu.Shaders;
namespace Meitou.Rendering.Gpu;

/// <summary>What a view covers (the engine's words; the backend maps it to its own).</summary>
public enum GpuViewType { Texture2D, Texture2DArray, Cube }

/// <summary>Which component a texel reads (the swizzle of a sampled view). The default is the identity.</summary>
public enum GpuSwizzleComponent { Identity, R, G, B, A, One, Zero }

/// <summary>The swizzle of a sampled view (the engine's words; the backend maps it). The default is the identity.</summary>
public readonly record struct GpuSwizzle(GpuSwizzleComponent R = GpuSwizzleComponent.Identity, GpuSwizzleComponent G = GpuSwizzleComponent.Identity,
    GpuSwizzleComponent B = GpuSwizzleComponent.Identity, GpuSwizzleComponent A = GpuSwizzleComponent.Identity)
{
    /// <summary>The identity swizzle.</summary>
    public static GpuSwizzle Identity => default;

    /// <summary>The red channel in all three, alpha one (a single-channel format).</summary>
    public static GpuSwizzle RrrOne => new(GpuSwizzleComponent.R, GpuSwizzleComponent.R, GpuSwizzleComponent.R, GpuSwizzleComponent.One);
}

/// <summary>
/// A sampler the renderers hold: the backend's identity and the description it was made from
/// (docs/resource-layer.md). The Vulkan sampler itself is the backend's; index 0 is the null one.
/// </summary>
public readonly record struct GpuSampler(int Index, SamplerDesc Desc)
{
    /// <summary>The null sampler (a backend's empty sampler).</summary>
    public static GpuSampler Null => new(0, default);

    public bool IsNull => Index == 0;
}

/// <summary>
/// A view of a texture the renderers hold: the backend's identity (the index into the backend's
/// views) (docs/resource-layer.md). The level and layer range it covers is the backend's.
/// </summary>
public readonly record struct GpuView(int Index)
{
    /// <summary>The null view (a backend's empty view).</summary>
    public static GpuView Null => new(0);

    public bool IsNull => Index == 0;
}

/// <summary>
/// What a program is, besides its backend's compiled objects (docs/resource-layer.md): the engine's own
/// compile result. The GLSL it came from, the SPIR-V the engine's compiler made (the backend's input:
/// Vulkan's modules and layouts come from it, Metal's MSL is translated from it), the SPIR-V's reflection
/// and the program's sets in the engine's words. Created once at compile; never mutated afterwards.
/// </summary>
public sealed class ProgramInfo
{
    public int Index { get; init; }
    public required string Name { get; init; }
    public ShaderModel Model { get; init; }
    public byte[]? VertexCode { get; init; }
    public byte[]? FragmentCode { get; init; }
    public byte[]? TessControlCode { get; init; }
    public byte[]? TessEvaluationCode { get; init; }
    public byte[]? ComputeCode { get; init; }
    /// <summary>The legacy compile result (reflection of both stages, default blocks), null for other models.</summary>
    public CompiledProgram? Compiled { get; init; }
    public Shaders.ShaderReflection? VertexReflection { get; init; }
    public Shaders.ShaderReflection? FragmentReflection { get; init; }
    public Shaders.ShaderReflection? ComputeReflection { get; init; }
    /// <summary>The program's sets in the engine's words (the layout the backend made).</summary>
    public GpuSetLayout[] NeutralSets { get; init; } = [];
    public uint PushConstantBytes { get; init; }
    /// <summary>The GLSL of the fragment stage as compiled, kept for the native model only (the bench's instrumented variant is made from it).</summary>
    public string? FragmentSource { get; internal set; }
}

/// <summary>
/// A program the renderers hold: the backend's identity (the index into the backend's programs) and the
/// engine's own compile result (<see cref="ProgramInfo"/>) (docs/resource-layer.md). What the backend made
/// of it (Vulkan: the shader modules and the pipeline layout; Metal: the MSL libraries) is reached through
/// the shader library.
/// </summary>
public readonly record struct GpuProgram(int Index, ProgramInfo Info)
{
    /// <summary>The null program (no info).</summary>
    public static GpuProgram Null => default;

    public bool IsNull => Info is null;

    // The engine's own surface of the program (no backend object behind these).
    public string Name => Info.Name;
    public ShaderModel Model => Info.Model;
    public byte[]? VertexCode => Info.VertexCode;
    public byte[]? FragmentCode => Info.FragmentCode;
    public byte[]? TessControlCode => Info.TessControlCode;
    public byte[]? TessEvaluationCode => Info.TessEvaluationCode;
    public byte[]? ComputeCode => Info.ComputeCode;
    /// <summary>The program has a tessellation evaluation stage (its pipelines draw patch lists).</summary>
    public bool Tessellated => Info.TessEvaluationCode is not null;
    public CompiledProgram? Compiled => Info.Compiled;
    public Shaders.ShaderReflection? VertexReflection => Info.VertexReflection;
    public Shaders.ShaderReflection? FragmentReflection => Info.FragmentReflection;
    public Shaders.ShaderReflection? ComputeReflection => Info.ComputeReflection;
    public GpuSetLayout[] NeutralSets => Info.NeutralSets;
    public uint PushConstantBytes => Info.PushConstantBytes;
    public string? FragmentSource => Info.FragmentSource;
}

/// <summary>
/// A graphics pipeline the renderers hold: the description it was made from (docs/resource-layer.md)
/// — it is the pipeline's identity (the backend caches by it). The pipeline state is the backend's
/// (Vulkan: a VkPipeline; Metal: an MTLRenderPipelineState). <c>Native</c> is the backend's own handle when it has one
/// to carry (Vulkan's VkPipeline: a bind needs no lookup, and the bench's counting variant of the same description is
/// told apart by it); 0 makes the backend look it up by the description.
/// </summary>
public readonly record struct GpuPipeline(GraphicsPipelineDesc Desc, ulong Native = 0)
{
    /// <summary>The null pipeline (no program).</summary>
    public static GpuPipeline Null => default;

    public bool IsNull => Desc.Program.IsNull;
}

/// <summary>
/// A compute pipeline the renderers hold: the description it was made from — the pipeline's identity (<c>Native</c> as
/// <see cref="GpuPipeline"/>'s).
/// </summary>
public readonly record struct GpuComputePipeline(ComputePipelineDesc Desc, ulong Native = 0)
{
    /// <summary>The null pipeline (no program).</summary>
    public static GpuComputePipeline Null => default;

    public bool IsNull => Desc.Program.IsNull;
}

/// <summary>The part of a buffer a copy covers (the source's offset, the destination's, and the size).</summary>
public readonly record struct GpuBufferCopy(ulong SourceOffset, ulong DestinationOffset, ulong Size);

/// <summary>Which pipeline a command belongs to (the stages it runs in).</summary>
public enum GpuBindPoint { Graphics, Compute }

/// <summary>
/// A set of resources the renderers bind to a program: the backend's identity (the index into the
/// backend's sets) (docs/resource-layer.md). The set itself is the backend's (Vulkan: a descriptor
/// set; Metal: the program's argument table).
/// </summary>
public readonly record struct GpuSet(int Index)
{
    /// <summary>The null set.</summary>
    public static GpuSet Null => new(0);

    public bool IsNull => Index == 0;
}

/// <summary>
/// A buffer the renderers hold: the backend's identity (the index into the backend's buffers) and
/// its size (docs/resource-layer.md). The buffer itself is the backend's.
/// </summary>
public readonly record struct GpuBuffer(int Index, ulong Size)
{
    /// <summary>The null buffer (a backend's empty buffer).</summary>
    public static GpuBuffer Null => new(0, 0);

    public bool IsNull => Index == 0;
}
