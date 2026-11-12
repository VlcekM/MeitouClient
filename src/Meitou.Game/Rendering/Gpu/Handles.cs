using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Shaders;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// What a view covers (the engine's words; the backend maps it to its own).
/// </summary>
public enum GpuViewType
{
    /// <summary>
    /// One 2D layer.
    /// </summary>
    Texture2D,
    /// <summary>
    /// A range of layers as a 2D array.
    /// </summary>
    Texture2DArray,
    /// <summary>
    /// Six layers as a cube.
    /// </summary>
    Cube
}

/// <summary>
/// Which component a texel reads (the swizzle of a sampled view). The default is the identity.
/// </summary>
public enum GpuSwizzleComponent
{
    /// <summary>
    /// The view's own component.
    /// </summary>
    Identity,
    /// <summary>
    /// The red channel.
    /// </summary>
    R,
    /// <summary>
    /// The green channel.
    /// </summary>
    G,
    /// <summary>
    /// The blue channel.
    /// </summary>
    B,
    /// <summary>
    /// The alpha channel.
    /// </summary>
    A,
    /// <summary>
    /// Constant 1.
    /// </summary>
    One,
    /// <summary>
    /// Constant 0.
    /// </summary>
    Zero
}

/// <summary>
/// The swizzle of a sampled view (the engine's words; the backend maps it). The default is the identity.
/// </summary>
public readonly record struct GpuSwizzle(GpuSwizzleComponent R = GpuSwizzleComponent.Identity, GpuSwizzleComponent G = GpuSwizzleComponent.Identity,
    GpuSwizzleComponent B = GpuSwizzleComponent.Identity, GpuSwizzleComponent A = GpuSwizzleComponent.Identity)
{
    /// <summary>
    /// The identity swizzle.
    /// </summary>
    public static GpuSwizzle Identity => default;

    /// <summary>
    /// The red channel in all three, alpha one (a single-channel format).
    /// </summary>
    public static GpuSwizzle RrrOne => new(GpuSwizzleComponent.R, GpuSwizzleComponent.R, GpuSwizzleComponent.R, GpuSwizzleComponent.One);
}

/// <summary>
/// A sampler the renderers hold: the backend's identity and the description it was made from (docs/resource-layer.md). The Vulkan sampler itself is
/// the backend's; index 0 is the null one.
/// </summary>
public readonly record struct GpuSampler(int Index, SamplerDesc Desc)
{
    /// <summary>
    /// The null sampler (a backend's empty sampler).
    /// </summary>
    public static GpuSampler Null => new(0, default);

    /// <summary>
    /// Whether this is the null sampler (index 0).
    /// </summary>
    public bool IsNull => Index == 0;
}

/// <summary>
/// A view of a texture the renderers hold: the backend's identity (the index into the backend's views) (docs/resource-layer.md). The level and layer
/// range it covers is the backend's.
/// </summary>
public readonly record struct GpuView(int Index)
{
    /// <summary>
    /// The null view (a backend's empty view).
    /// </summary>
    public static GpuView Null => new(0);

    /// <summary>
    /// Whether this is the null view (index 0).
    /// </summary>
    public bool IsNull => Index == 0;
}

/// <summary>
/// What a program is, besides its backend's compiled objects (docs/resource-layer.md): the engine's own compile result. The GLSL it came from, the
/// SPIR-V the engine's compiler made (the backend's input: Vulkan's modules and layouts come from it, Metal's MSL is translated from it), the
/// SPIR-V's reflection and the program's sets in the engine's words. Created once at compile; never mutated afterwards.
/// </summary>
public sealed class ProgramInfo
{
    /// <summary>
    /// The program's index in the backend's programs.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// The program's name (diagnostics, the shader cache).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The shader model it was compiled for (<see cref="ShaderModel"/>: GL-shaped legacy or native).
    /// </summary>
    public ShaderModel Model { get; init; }

    /// <summary>
    /// The vertex stage's SPIR-V; null when the program has none.
    /// </summary>
    public byte[]? VertexCode { get; init; }

    /// <summary>
    /// The fragment stage's SPIR-V; null when the program has none.
    /// </summary>
    public byte[]? FragmentCode { get; init; }

    /// <summary>
    /// The tessellation control stage's SPIR-V; null when the program has none.
    /// </summary>
    public byte[]? TessControlCode { get; init; }

    /// <summary>
    /// The tessellation evaluation stage's SPIR-V; null when the program has none.
    /// </summary>
    public byte[]? TessEvaluationCode { get; init; }

    /// <summary>
    /// The compute stage's SPIR-V; null for a graphics program.
    /// </summary>
    public byte[]? ComputeCode { get; init; }

    /// <summary>
    /// The legacy compile result (reflection of both stages, default blocks), null for other models.
    /// </summary>
    public CompiledProgram? Compiled { get; init; }

    /// <summary>
    /// The reflection of the vertex SPIR-V; null without that stage.
    /// </summary>
    public ShaderReflection? VertexReflection { get; init; }

    /// <summary>
    /// The reflection of the fragment SPIR-V; null without that stage.
    /// </summary>
    public ShaderReflection? FragmentReflection { get; init; }

    /// <summary>
    /// The reflection of the compute SPIR-V; null for a graphics program.
    /// </summary>
    public ShaderReflection? ComputeReflection { get; init; }

    /// <summary>
    /// The program's sets in the engine's words (the layout the backend made).
    /// </summary>
    public GpuSetLayout[] NeutralSets { get; init; } = [];

    /// <summary>
    /// The size of the program's push constant block in bytes (0 for none).
    /// </summary>
    public uint PushConstantBytes { get; init; }

    /// <summary>
    /// The GLSL of the fragment stage as compiled, kept for the native model only (the bench's instrumented variant is made from it).
    /// </summary>
    public string? FragmentSource { get; internal set; }
}

/// <summary>
/// A program the renderers hold: the backend's identity (the index into the backend's programs) and the engine's own compile result
/// (<see cref="ProgramInfo"/>) (docs/resource-layer.md). What the backend made of it (Vulkan: the shader modules and the pipeline layout; Metal: the
/// MSL libraries) is reached through the shader library.
/// </summary>
public readonly record struct GpuProgram(int Index, ProgramInfo Info)
{
    /// <summary>
    /// The null program (no info).
    /// </summary>
    public static GpuProgram Null => default;

    /// <summary>
    /// Whether this is the null program.
    /// </summary>
    public bool IsNull => Info is null;

    // The engine's own surface of the program (no backend object behind these).
    /// <summary>
    /// <see cref="ProgramInfo.Name"/>.
    /// </summary>
    public string Name => Info.Name;

    /// <summary>
    /// <see cref="ProgramInfo.Model"/>.
    /// </summary>
    public ShaderModel Model => Info.Model;

    /// <summary>
    /// <see cref="ProgramInfo.VertexCode"/>.
    /// </summary>
    public byte[]? VertexCode => Info.VertexCode;

    /// <summary>
    /// <see cref="ProgramInfo.FragmentCode"/>.
    /// </summary>
    public byte[]? FragmentCode => Info.FragmentCode;

    /// <summary>
    /// <see cref="ProgramInfo.TessControlCode"/>.
    /// </summary>
    public byte[]? TessControlCode => Info.TessControlCode;

    /// <summary>
    /// <see cref="ProgramInfo.TessEvaluationCode"/>.
    /// </summary>
    public byte[]? TessEvaluationCode => Info.TessEvaluationCode;

    /// <summary>
    /// <see cref="ProgramInfo.ComputeCode"/>.
    /// </summary>
    public byte[]? ComputeCode => Info.ComputeCode;

    /// <summary>
    /// The program has a tessellation evaluation stage (its pipelines draw patch lists).
    /// </summary>
    public bool Tessellated => Info.TessEvaluationCode is not null;

    /// <summary>
    /// <see cref="ProgramInfo.Compiled"/>.
    /// </summary>
    public CompiledProgram? Compiled => Info.Compiled;

    /// <summary>
    /// <see cref="ProgramInfo.VertexReflection"/>.
    /// </summary>
    public ShaderReflection? VertexReflection => Info.VertexReflection;

    /// <summary>
    /// <see cref="ProgramInfo.FragmentReflection"/>.
    /// </summary>
    public ShaderReflection? FragmentReflection => Info.FragmentReflection;

    /// <summary>
    /// <see cref="ProgramInfo.ComputeReflection"/>.
    /// </summary>
    public ShaderReflection? ComputeReflection => Info.ComputeReflection;

    /// <summary>
    /// <see cref="ProgramInfo.NeutralSets"/>.
    /// </summary>
    public GpuSetLayout[] NeutralSets => Info.NeutralSets;

    /// <summary>
    /// <see cref="ProgramInfo.PushConstantBytes"/>.
    /// </summary>
    public uint PushConstantBytes => Info.PushConstantBytes;

    /// <summary>
    /// <see cref="ProgramInfo.FragmentSource"/>.
    /// </summary>
    public string? FragmentSource => Info.FragmentSource;
}

/// <summary>
/// A graphics pipeline the renderers hold: the description it was made from (docs/resource-layer.md) — it is the pipeline's identity (the backend
/// caches by it). The pipeline state is the backend's (Vulkan: a VkPipeline; Metal: an MTLRenderPipelineState). <c>Native</c> is the backend's own
/// handle when it has one to carry (Vulkan's VkPipeline: a bind needs no lookup, and the bench's counting variant of the same description is told
/// apart by it); 0 makes the backend look it up by the description.
/// </summary>
public readonly record struct GpuPipeline(GraphicsPipelineDesc Desc, ulong Native = 0)
{
    /// <summary>
    /// The null pipeline (no program).
    /// </summary>
    public static GpuPipeline Null => default;

    /// <summary>
    /// Whether this is the null pipeline (its program is null).
    /// </summary>
    public bool IsNull => Desc.Program.IsNull;
}

/// <summary>
/// A compute pipeline the renderers hold: the description it was made from — the pipeline's identity (<c>Native</c> as <see cref="GpuPipeline"/>'s).
/// </summary>
public readonly record struct GpuComputePipeline(ComputePipelineDesc Desc, ulong Native = 0)
{
    /// <summary>
    /// The null pipeline (no program).
    /// </summary>
    public static GpuComputePipeline Null => default;

    /// <summary>
    /// Whether this is the null pipeline (its program is null).
    /// </summary>
    public bool IsNull => Desc.Program.IsNull;
}

/// <summary>
/// The part of a buffer a copy covers (the source's offset, the destination's, and the size).
/// </summary>
public readonly record struct GpuBufferCopy(ulong SourceOffset, ulong DestinationOffset, ulong Size);

/// <summary>
/// Which pipeline a command belongs to (the stages it runs in).
/// </summary>
public enum GpuBindPoint
{
    /// <summary>
    /// Draws (vertex, tessellation and fragment stages).
    /// </summary>
    Graphics,
    /// <summary>
    /// Dispatches (the compute stage).
    /// </summary>
    Compute
}

/// <summary>
/// A set of resources the renderers bind to a program: the backend's identity (the index into the backend's sets) (docs/resource-layer.md). The set
/// itself is the backend's (Vulkan: a descriptor set; Metal: the program's argument table).
/// </summary>
public readonly record struct GpuSet(int Index)
{
    /// <summary>
    /// The null set.
    /// </summary>
    public static GpuSet Null => new(0);

    /// <summary>
    /// Whether this is the null set (index 0).
    /// </summary>
    public bool IsNull => Index == 0;
}

/// <summary>
/// A buffer the renderers hold: the backend's identity (the index into the backend's buffers) and its size (docs/resource-layer.md). The buffer
/// itself is the backend's.
/// </summary>
public readonly record struct GpuBuffer(int Index, ulong Size)
{
    /// <summary>
    /// The null buffer (a backend's empty buffer).
    /// </summary>
    public static GpuBuffer Null => new(0, 0);

    /// <summary>
    /// Whether this is the null buffer (index 0).
    /// </summary>
    public bool IsNull => Index == 0;
}
