namespace Meitou.Rendering.Backend;

/// <summary>A GPU resource. <see cref="IDisposable.Dispose"/> releases it at once; <see cref="IGpuDevice.Release"/> waits until no frame in flight uses it.</summary>
public interface IGpuResource : IDisposable
{
    string? Name { get; }
}

public interface IGpuBuffer : IGpuResource
{
    GpuBufferDesc Desc { get; }
}

public interface IGpuTexture : IGpuResource
{
    GpuTextureDesc Desc { get; }
}

public interface IGpuSampler : IGpuResource
{
    GpuSamplerDesc Desc { get; }
}

public interface IGpuPipeline : IGpuResource
{
    GpuPipelineDesc Desc { get; }
}

/// <summary>A GPU timer: the time between <see cref="IGpuCommandList.BeginTimer"/> and <see cref="IGpuCommandList.EndTimer"/>, read a few frames later.</summary>
public interface IGpuTimer : IGpuResource
{
    /// <summary>The last finished measurement in milliseconds, or null if none is ready; never waits unless <paramref name="wait"/>.</summary>
    double? Read(bool wait = false);
}

/// <summary>
/// Commands for one frame (or one upload batch). On OpenGL they execute as they are recorded on the thread that owns the
/// context; on Vulkan they are recorded into a command buffer (one per thread) and run when the frame is submitted.
/// </summary>
public interface IGpuCommandList
{
    void BeginRenderPass(GpuRenderPassDesc pass);
    void EndRenderPass();
    void SetViewport(int x, int y, int width, int height);
    void SetScissor(int x, int y, int width, int height);
    void SetPipeline(IGpuPipeline pipeline);
    void SetVertexBuffer(int slot, IGpuBuffer buffer, long offset = 0);
    void SetIndexBuffer(IGpuBuffer buffer, GpuIndexType type, long offset = 0);
    /// <summary>Uniform data for block <paramref name="binding"/> (copied into per-frame memory).</summary>
    void SetUniforms<T>(int binding, in T data) where T : unmanaged;
    void SetUniformBuffer(int binding, IGpuBuffer buffer, long offset, long size);
    void SetTexture(int binding, IGpuTexture texture, IGpuSampler sampler);
    void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0);
    void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int vertexOffset = 0, int firstInstance = 0);
    void UpdateBuffer<T>(IGpuBuffer buffer, long offset, ReadOnlySpan<T> data) where T : unmanaged;
    /// <summary>Writes one mip level of one layer (whole rows; BCn data as stored blocks).</summary>
    void UpdateTexture(IGpuTexture texture, int mip, int layer, int x, int y, int width, int height, ReadOnlySpan<byte> data);
    void GenerateMips(IGpuTexture texture);
    void BeginTimer(IGpuTimer timer);
    void EndTimer(IGpuTimer timer);
}

/// <summary>
/// The rendering backend: creates resources and runs frames. Frame protocol: <see cref="BeginFrame"/> (waits until the
/// frame slot's previous use has finished, then frees what was released for it), record into <see cref="Commands"/>,
/// <see cref="EndFrame"/> (submit and present). Resources released with <see cref="Release"/> are destroyed once every frame
/// that may still use them has finished (deferred deletion tied to the frame fences; OpenGL frees them immediately, its
/// driver tracks use itself).
/// </summary>
public interface IGpuDevice : IDisposable
{
    GpuCapabilities Capabilities { get; }
    /// <summary>Frames the CPU may record ahead of the GPU (2–3 on Vulkan; 1 on OpenGL, whose driver queues on its own).</summary>
    int FramesInFlight { get; }
    /// <summary>Counts frames since the start.</summary>
    long FrameNumber { get; }
    IGpuCommandList Commands { get; }

    IGpuBuffer CreateBuffer(in GpuBufferDesc desc);
    IGpuTexture CreateTexture(in GpuTextureDesc desc);
    IGpuSampler CreateSampler(in GpuSamplerDesc desc);
    IGpuPipeline CreatePipeline(GpuPipelineDesc desc);
    IGpuTimer CreateTimer();

    /// <summary>Destroys <paramref name="resource"/> after the frames in flight that might use it have finished.</summary>
    void Release(IGpuResource resource);

    void BeginFrame(int width, int height);
    void EndFrame();
    /// <summary>Waits until the GPU is idle (screenshots, benchmarks' per-frame timing, shutdown).</summary>
    void WaitIdle();
    /// <summary>Reads an RGBA8 texture (or the last presented frame when null) into top-first rows.</summary>
    byte[] ReadPixels(IGpuTexture? texture, int width, int height);
}

/// <summary>Helpers shared by the backends.</summary>
public static class GpuFormats
{
    public static bool IsCompressed(GpuFormat f) => f is GpuFormat.Bc1 or GpuFormat.Bc2 or GpuFormat.Bc3 or GpuFormat.Bc4 or GpuFormat.Bc5;

    public static bool IsDepth(GpuFormat f) => f is GpuFormat.Depth24 or GpuFormat.Depth32F or GpuFormat.Depth24Stencil8;

    /// <summary>Bytes per 4×4 block for the BCn formats, per pixel otherwise.</summary>
    public static int BytesPerUnit(GpuFormat f) => f switch
    {
        GpuFormat.Bc1 or GpuFormat.Bc4 => 8,
        GpuFormat.Bc2 or GpuFormat.Bc3 or GpuFormat.Bc5 => 16,
        GpuFormat.R8 => 1,
        GpuFormat.RG8 or GpuFormat.R16 or GpuFormat.R16F => 2,
        GpuFormat.Rgba8 or GpuFormat.Bgra8 or GpuFormat.Rg16F or GpuFormat.R32F or GpuFormat.Depth24 or GpuFormat.Depth32F or GpuFormat.Depth24Stencil8 => 4,
        GpuFormat.Rgba16F or GpuFormat.Rg32F => 8,
        GpuFormat.Rgba32F => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    /// <summary>Bytes of one mip level of one layer.</summary>
    public static long LevelBytes(GpuFormat f, int width, int height) =>
        IsCompressed(f) ? (long)((width + 3) / 4) * ((height + 3) / 4) * BytesPerUnit(f) : (long)width * height * BytesPerUnit(f);
}
