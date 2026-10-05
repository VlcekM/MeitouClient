using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;
using Sampler = Silk.NET.Vulkan.Sampler;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// GL's sampler state as a value (docs/renderer-native.md 2.4). <see cref="FromGl"/> is the rule VkGl used in <c>SamplerFor</c>, moved here
/// so translated and native draws share it: integer formats sample nearest, a sampler without mipmaps clamps its LOD to 0.25 (GL's
/// "no mips" behaviour), the LOD bias applies to mipmapped samplers only, anisotropy is clamped to 16, and the compare mode is used only by
/// shadow samplers.
/// </summary>
public readonly record struct SamplerDesc(Filter Min, Filter Mag, SamplerMipmapMode Mip, bool Mipmapped,
    SamplerAddressMode U, SamplerAddressMode V, SamplerAddressMode W, bool Compare, CompareOp Op, bool TransparentBorder,
    bool IntegerFormat, float Anisotropy, float LodBias)
{
    /// <param name="compare">The sampler is a shadow sampler and the texture's compare mode is on.</param>
    /// <param name="lodBias">The upscaler's bias (<see cref="ITextureLodBias"/>); applied only when the filter is mipmapped.</param>
    public static SamplerDesc FromGl(TextureMinFilter min, TextureMagFilter mag, TextureWrapMode s, TextureWrapMode t, TextureWrapMode r,
        bool compare, DepthFunction func, bool transparentBorder, float anisotropy, bool integerFormat, float lodBias)
    {
        var (vkMin, mip, mipmapped) = min switch
        {
            TextureMinFilter.Nearest => (Filter.Nearest, SamplerMipmapMode.Nearest, false),
            TextureMinFilter.Linear => (Filter.Linear, SamplerMipmapMode.Nearest, false),
            TextureMinFilter.NearestMipmapNearest => (Filter.Nearest, SamplerMipmapMode.Nearest, true),
            TextureMinFilter.LinearMipmapNearest => (Filter.Linear, SamplerMipmapMode.Nearest, true),
            TextureMinFilter.NearestMipmapLinear => (Filter.Nearest, SamplerMipmapMode.Linear, true),
            _ => (Filter.Linear, SamplerMipmapMode.Linear, true),
        };
        var vkMag = mag == TextureMagFilter.Nearest ? Filter.Nearest : Filter.Linear;
        if (integerFormat) (vkMin, vkMag, mip) = (Filter.Nearest, Filter.Nearest, SamplerMipmapMode.Nearest);
        return new SamplerDesc(vkMin, vkMag, mip, mipmapped, GlConventions.AddressMode(s), GlConventions.AddressMode(t), GlConventions.AddressMode(r),
            compare, GlConventions.CompareOp(func), transparentBorder, integerFormat, Math.Clamp(anisotropy, 1, 16), mipmapped ? lodBias : 0);
    }
}

/// <summary>
/// One Vulkan sampler per <see cref="SamplerDesc"/>, for the device's lifetime. Applies the device rules: anisotropy only where the device
/// has it, and on NVIDIA a −0.25 LOD bias for anisotropic samplers (its OpenGL picked mips a quarter level finer than its Vulkan driver;
/// DECISIONS 9). Render thread only.
/// </summary>
public sealed unsafe class SamplerCache : IDisposable
{
    readonly VulkanDevice device;
    readonly Dictionary<SamplerDesc, Sampler> samplers = [];

    public SamplerCache(VulkanDevice device) => this.device = device;

    public int Count => samplers.Count;

    public Sampler Get(in SamplerDesc desc)
    {
        var key = device.SamplerAnisotropy ? desc : desc with { Anisotropy = 1 };
        if (samplers.TryGetValue(key, out var sampler)) return sampler;
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MinFilter = key.Min, MagFilter = key.Mag, MipmapMode = key.Mip,
            AddressModeU = key.U, AddressModeV = key.V, AddressModeW = key.W,
            AnisotropyEnable = key.Anisotropy > 1, MaxAnisotropy = key.Anisotropy,
            CompareEnable = key.Compare, CompareOp = key.Op,
            MinLod = 0, MaxLod = key.Mipmapped ? Vk.LodClampNone : 0.25f,
            // NVIDIA's OpenGL picks mips a quarter level finer than its Vulkan driver with anisotropic filtering on: matched here
            // (docs/engine.md "Vulkan backend"; measured 1.2 -> 0.001 mean difference on the rock view). Plus the upscaler's bias.
            MipLodBias = (key.Anisotropy > 1 && device.Properties.VendorID == 0x10DE ? -0.25f : 0) + key.LodBias,
            BorderColor = key.IntegerFormat ? (key.TransparentBorder ? BorderColor.IntTransparentBlack : BorderColor.IntOpaqueBlack)
                : key.TransparentBorder ? BorderColor.FloatTransparentBlack : BorderColor.FloatOpaqueWhite,
        };
        VulkanException.Check(device.Vk.CreateSampler(device.Device, &info, null, out sampler), "vkCreateSampler");
        samplers[key] = sampler;
        return sampler;
    }

    public void Dispose()
    {
        foreach (var s in samplers.Values) device.Vk.DestroySampler(device.Device, s, null);
        samplers.Clear();
    }
}
