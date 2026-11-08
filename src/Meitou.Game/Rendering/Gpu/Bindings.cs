namespace Meitou.Rendering.Gpu;

/// <summary>What a program's slot is bound to (the kinds the engine's programs ask for).</summary>
public enum GpuBindingKind : int
{
    /// <summary>A texture with its sampler (GL's combined image sampler).</summary>
    Sampled = 0,
    /// <summary>A storage image (GL's image).</summary>
    StorageImage = 1,
    /// <summary>A uniform buffer (GL's uniform block).</summary>
    UniformBuffer = 2,
    /// <summary>A storage buffer (GL's shader storage buffer).</summary>
    StorageBuffer = 3,
}

/// <summary>
/// One slot of a program's binding list: the slot, what it holds and the resource behind it
/// (docs/resource-layer.md). The layout, the image layout and the descriptor type are the backend's
/// business; the program itself says which stage and which set the slot belongs to.
/// </summary>
public readonly record struct GpuBinding(uint Slot, GpuBindingKind Kind, SampledTexture Texture = default, GpuBufferBinding Buffer = default)
{
    /// <summary>A texture with its sampler.</summary>
    public static GpuBinding Sampled(uint slot, in SampledTexture texture) => new(slot, GpuBindingKind.Sampled, texture);

    /// <summary>A storage image (its sampler is not used).</summary>
    public static GpuBinding StorageImage(uint slot, in SampledTexture texture) => new(slot, GpuBindingKind.StorageImage, texture);

    /// <summary>A uniform buffer.</summary>
    public static GpuBinding Uniform(uint slot, in GpuBufferBinding buffer) => new(slot, GpuBindingKind.UniformBuffer, default, buffer);

    /// <summary>A storage buffer.</summary>
    public static GpuBinding Storage(uint slot, in GpuBufferBinding buffer) => new(slot, GpuBindingKind.StorageBuffer, default, buffer);
}
