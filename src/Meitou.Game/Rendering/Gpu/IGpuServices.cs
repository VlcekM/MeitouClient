namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// The programs the engine's shaders compile to (the seam's surface, docs/resource-layer.md): the GLSL goes in, the engine's compile result comes out
/// (<see cref="GpuProgram"/>). The backend's own artifacts (Vulkan: the modules and layouts; Metal: the MSL libraries) are the backend's.
/// </summary>
public interface IGpuShaders
{
    /// <summary>
    /// Whether a fragment shader that discards needs an unchanged depth output to keep rejected fragments from occluding later layers.
    /// </summary>
    bool DiscardNeedsLateDepth => false;

    /// <summary>
    /// Compiles (or takes from the caches) the GLSL pair as a legacy program (docs/renderer-native.md 3.2).
    /// </summary>
    GpuProgram Legacy(string vertexGlsl, string fragmentGlsl, string name);

    /// <summary>
    /// Compiles (or takes from the caches) the GLSL pair as a native-model program (docs/renderer-native.md 3.2).
    /// </summary>
    GpuProgram Native(string vertexGlsl, string fragmentGlsl, string name, GpuSetLayout[] setLayouts, uint pushConstantBytes);

    /// <summary>
    /// A native program with tessellation stages (patch lists of three control points), where the backend supports them.
    /// </summary>
    GpuProgram NativeTessellated(string vertexGlsl, string controlGlsl, string evaluationGlsl, string fragmentGlsl, string name,
        GpuSetLayout[] setLayouts, uint pushConstantBytes);

    /// <summary>
    /// A compute program: set 0 from the shader's reflection, the other sets from <paramref name="extraSets"/>.
    /// </summary>
    GpuProgram Compute(string glsl, string name, params GpuSetLayout[] extraSets);

    /// <summary>
    /// Releases a program (its backend artifacts go once the frames in flight are done); the index is never reused.
    /// </summary>
    void Dispose(GpuProgram program);
}

/// <summary>
/// The pipelines by description (the engine's own): the description is the identity, the backend caches by it.
/// </summary>
public interface IGpuPipelines
{
    /// <summary>
    /// The graphics pipeline for a description (made on first use).
    /// </summary>
    GpuPipeline Get(in GraphicsPipelineDesc desc);

    /// <summary>
    /// The compute pipeline for a description (made on first use).
    /// </summary>
    GpuComputePipeline Get(in ComputePipelineDesc desc);

    /// <summary>
    /// Drops a program's cached pipelines (the program is going away).
    /// </summary>
    void Forget(GpuProgram program);
}

/// <summary>
/// The samplers by description (the backend's objects; the renderers hold the handles).
/// </summary>
public interface IGpuSamplers
{
    /// <summary>
    /// The sampler for a description (made on first use).
    /// </summary>
    GpuSampler Get(in SamplerDesc desc);

    /// <summary>
    /// The largest anisotropy the samplers use (the renderers' quality dial).
    /// </summary>
    int MaxAnisotropy { get; set; }

    /// <summary>
    /// Bumped when the samplers' state changed (the anisotropy dial): the caches of sampler bindings compare it.
    /// </summary>
    int Generation { get; }
}

/// <summary>
/// The uploads (the staging copies into the textures and the buffers).
/// </summary>
public interface IGpuUploads
{
    /// <summary>
    /// A batch of copies recorded into its own transfer commands, flushed when it is disposed.
    /// </summary>
    IGpuUploadBatch Begin();
}

/// <summary>
/// A host-readable buffer the GPU writes into (the bakes' and the culls' readbacks): read once the frame that wrote it has completed.
/// </summary>
public interface IGpuReadback : IDisposable
{
    /// <summary>
    /// The buffer the GPU writes.
    /// </summary>
    GpuBuffer Handle { get; }

    /// <summary>
    /// The buffer's size in bytes.
    /// </summary>
    ulong Size { get; }

    /// <summary>
    /// The bytes [<paramref name="offset"/>, +<paramref name="size"/>), once the writing frame has completed.
    /// </summary>
    ReadOnlySpan<byte> Read(ulong offset, ulong size);
}

/// <summary>
/// A batch of uploads (<see cref="IGpuUploads.Begin"/>): the copies it records go to the device when it is disposed.
/// </summary>
public interface IGpuUploadBatch : IDisposable
{
    /// <summary>
    /// A new texture the batch's commands can write into.
    /// </summary>
    GpuTexture Create(TextureDesc desc);

    /// <summary>
    /// Queues bytes for a region of the texture's level and layer.
    /// </summary>
    void Write(GpuTexture target, int level, int layer, in PixelRect region, ReadOnlySpan<byte> data);

    /// <summary>
    /// Queues bytes for a buffer's range.
    /// </summary>
    void Write(DeviceBuffer target, ulong offset, ReadOnlySpan<byte> data);

    /// <summary>
    /// The commands the batch's copies go to (a mip chain is recorded there too).
    /// </summary>
    IGpuCommandList Commands { get; }
}
