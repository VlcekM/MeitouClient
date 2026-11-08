using Meitou.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Meitou.Metal;

/// <summary>
/// One Metal sampler per description, for the device's lifetime (the twin of <c>SamplerCache</c>): the same
/// rules it applies — anisotropy clamped to the device's dial, a sampler without mipmaps clamps its LOD to
/// 0.25 (GL's "no mips" behaviour), the compare mode is used only by shadow samplers. Metal's sampler has no
/// LOD bias; the description's is carried and applied where the sampler is bound (the encoder's LOD clamps).
/// Render thread only.
/// </summary>
public sealed class MetalSamplers : IGpuSamplers
{
    readonly MTLDevice device;
    readonly Dictionary<SamplerDesc, (MTLSamplerState State, int Index)> samplers = [];

    public MetalSamplers(MTLDevice device) => this.device = device;

    /// <summary>The Tab panel's anisotropic filtering: the most any sampler gets (a change bumps <see cref="Generation"/>, which the caches of sampler bindings compare to make theirs again).</summary>
    public int MaxAnisotropy
    {
        get => maxAnisotropy;
        set { value = Math.Clamp(value, 1, 16); if (value != maxAnisotropy) (maxAnisotropy, Generation) = (value, Generation + 1); }
    }
    int maxAnisotropy = 16;
    public int Generation { get; private set; }

    /// <summary>The backend's object of a sampler handle.</summary>
    internal MTLSamplerState Backend(in GpuSampler sampler) => samplers[sampler.Desc].State;

    /// <summary>The description's LOD bias, applied where the sampler is bound (Metal's sampler has none).</summary>
    internal static float LodBias(in GpuSampler sampler) => sampler.Desc.LodBias;

    public GpuSampler Get(in SamplerDesc desc)
    {
        var key = desc with { Anisotropy = Math.Min(desc.Anisotropy, maxAnisotropy) };
        if (samplers.TryGetValue(key, out var made)) return new GpuSampler(made.Index, key);
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
        var entry = (device.NewSamplerState(d), samplers.Count);
        samplers[key] = entry;
        return new GpuSampler(entry.Item2, key);
    }
}
