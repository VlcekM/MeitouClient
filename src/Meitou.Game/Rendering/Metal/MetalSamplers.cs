using Meitou.Game.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// One Metal sampler per description, for the device's lifetime (the twin of <c>SamplerCache</c>): the same
/// rules it applies — anisotropy clamped to the device's dial, a sampler without mipmaps clamps its LOD to
/// 0.25 (GL's "no mips" behaviour), the compare mode is used only by shadow samplers. Metal's sampler has no
/// LOD bias; the description's is carried and applied where the sampler is bound (the encoder's LOD clamps).
/// Render thread only.
/// </summary>
public sealed class MetalSamplers : IGpuSamplers
{
    /// <summary>
    /// The device the sampler states are made on.
    /// </summary>
    readonly MTLDevice _device;
    /// <summary>
    /// The made sampler states and their indices (the order they were made in), by their capped description.
    /// </summary>
    readonly Dictionary<SamplerDesc, (MTLSamplerState State, int Index)> _samplers = [];
    /// <summary>
    /// Backs <see cref="MaxAnisotropy"/>.
    /// </summary>
    int _maxAnisotropy = 16;

    /// <summary>
    /// An empty cache on <paramref name="device"/>.
    /// </summary>
    public MetalSamplers(MTLDevice device) => this._device = device;

    /// <summary>
    /// The Tab panel's anisotropic filtering: the most any sampler gets (a change bumps <see cref="Generation"/>, which the caches of sampler
    /// bindings compare to make theirs again).
    /// </summary>
    public int MaxAnisotropy
    {
        get => _maxAnisotropy;
        set
        {
            value = Math.Clamp(value, 1, 16);
            if (value != _maxAnisotropy) (_maxAnisotropy, Generation) = (value, Generation + 1);
        }
    }

    /// <summary>
    /// Bumped by every change of <see cref="MaxAnisotropy"/>.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>
    /// The backend's object of a sampler handle.
    /// </summary>
    internal MTLSamplerState Backend(in GpuSampler sampler) => _samplers[sampler.Desc].State;

    /// <summary>
    /// The description's LOD bias, applied where the sampler is bound (Metal's sampler has none).
    /// </summary>
    internal static float LodBias(in GpuSampler sampler) => sampler.Desc.LodBias;

    /// <summary>
    /// The sampler for <paramref name="desc"/> with its anisotropy capped by <see cref="MaxAnisotropy"/>; its state made on first use (usable in
    /// argument buffers).
    /// </summary>
    public GpuSampler Get(in SamplerDesc desc)
    {
        var key = desc with { Anisotropy = Math.Min(desc.Anisotropy, _maxAnisotropy) };
        if (_samplers.TryGetValue(key, out var made)) return new GpuSampler(made.Index, key);
        var d = new MTLSamplerDescriptor();
        d.MinFilter = MetalConventions.Filter(key.Min);
        d.MagFilter = MetalConventions.Filter(key.Mag);
        d.MipFilter = MetalConventions.MipFilter(key.Min);
        d.SAddressMode = MetalConventions.AddressMode(key.U);
        d.TAddressMode = MetalConventions.AddressMode(key.V);
        d.RAddressMode = MetalConventions.AddressMode(key.W);
        d.CompareFunction = key.Compare ? MetalConventions.Compare(key.Op) : MTLCompareFunction.Never;

        // A sampler in an argument buffer needs the descriptor's permission (the table's members are such samplers).
        d.SupportArgumentBuffers = true;
        d.LodMinClamp = 0;
        d.LodMaxClamp = key.Mipmapped ? 1000f : 0.25f;   // Vulkan's LodClampNone, and GL's "no mips" clamp
        if (key.Anisotropy > 1) d.MaxAnisotropy = (ulong)key.Anisotropy;
        var entry = (_device.NewSamplerState(d), _samplers.Count);
        _samplers[key] = entry;
        return new GpuSampler(entry.Item2, key);
    }
}
