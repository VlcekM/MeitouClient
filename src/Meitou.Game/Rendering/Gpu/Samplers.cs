using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using Sampler = Silk.NET.Vulkan.Sampler;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// GL's sampler state as a value (docs/renderer-native.md 2.4). <see cref="FromGl"/> is the rule VkGl used in <c>SamplerFor</c>, moved here
/// so translated and native draws share it: integer formats sample nearest, a sampler without mipmaps clamps its LOD to 0.25 (GL's
/// "no mips" behaviour), the LOD bias applies to mipmapped samplers only, anisotropy is clamped to 16, and the compare mode is used only by
/// shadow samplers.
/// </summary>
public readonly record struct SamplerDesc(TextureMinFilter Min, TextureMagFilter Mag, bool Mipmapped,
    TextureWrapMode U, TextureWrapMode V, TextureWrapMode W, bool Compare, DepthFunction Op, bool TransparentBorder,
    bool IntegerFormat, float Anisotropy, float LodBias)
{
    /// <param name="compare">The sampler is a shadow sampler and the texture's compare mode is on.</param>
    /// <param name="lodBias">The upscaler's bias (<see cref="GpuContext.LodBias"/>); applied only when the filter is mipmapped.</param>
    public static SamplerDesc FromGl(TextureMinFilter min, TextureMagFilter mag, TextureWrapMode s, TextureWrapMode t, TextureWrapMode r,
        bool compare, DepthFunction func, bool transparentBorder, float anisotropy, bool integerFormat, float lodBias)
    {
        var mipmapped = min is TextureMinFilter.NearestMipmapNearest or TextureMinFilter.LinearMipmapNearest
            or TextureMinFilter.NearestMipmapLinear or TextureMinFilter.LinearMipmapLinear;
        var filter = integerFormat ? TextureMinFilter.Nearest : min;
        var magnification = integerFormat ? TextureMagFilter.Nearest : mag;
        return new SamplerDesc(filter, magnification, mipmapped, s, t, r, compare, func, transparentBorder, integerFormat,
            Math.Clamp(anisotropy, 1, 16), mipmapped ? lodBias : 0);
    }
}

/// <summary>
/// One Vulkan sampler per <see cref="SamplerDesc"/>, for the device's lifetime. Applies the device rules: anisotropy only where the device
/// has it, and on NVIDIA a −0.25 LOD bias for anisotropic samplers (its OpenGL picked mips a quarter level finer than its Vulkan driver;
/// DECISIONS 9). Render thread only.
/// </summary>
public sealed unsafe class SamplerCache : IGpuSamplers, IDisposable
{
    readonly VulkanDevice device;
    readonly Dictionary<SamplerDesc, int> samplers = [];

    public SamplerCache(VulkanDevice device) => this.device = device;

    public int Count => samplers.Count;

    /// <summary>The Tab panel's anisotropic filtering: the most any sampler gets (1, 2, 4, 8 or 16; 16, the default, leaves every texture as asked). A change bumps <see cref="Generation"/>, which the caches of sampler bindings compare to make theirs again.</summary>
    public int MaxAnisotropy
    {
        get => maxAnisotropy;
        set { value = Math.Clamp(value, 1, 16); if (value != maxAnisotropy) (maxAnisotropy, Generation) = (value, Generation + 1); }
    }
    int maxAnisotropy = 16;
    public int Generation { get; private set; }

    public GpuSampler Get(in SamplerDesc desc)
    {
        var key = device.SamplerAnisotropy ? desc with { Anisotropy = Math.Min(desc.Anisotropy, maxAnisotropy) } : desc with { Anisotropy = 1 };
        if (samplers.TryGetValue(key, out var index)) return new GpuSampler(index, key);
        var vkFilters = GlConventions.VkFilters(key.Min);
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MinFilter = vkFilters.Min, MagFilter = GlConventions.VkFilter(key.Mag), MipmapMode = vkFilters.Mip,
            AddressModeU = GlConventions.AddressMode(key.U), AddressModeV = GlConventions.AddressMode(key.V), AddressModeW = GlConventions.AddressMode(key.W),
            AnisotropyEnable = key.Anisotropy > 1, MaxAnisotropy = key.Anisotropy,
            CompareEnable = key.Compare, CompareOp = GlConventions.CompareOp(key.Op),
            MinLod = 0, MaxLod = key.Mipmapped ? Vk.LodClampNone : 0.25f,
            // NVIDIA's OpenGL picks mips a quarter level finer than its Vulkan driver with anisotropic filtering on: matched here
            // (docs/engine.md "Vulkan backend"; measured 1.2 -> 0.001 mean difference on the rock view). Plus the upscaler's bias.
            MipLodBias = (key.Anisotropy > 1 && device.Properties.VendorID == 0x10DE ? -0.25f : 0) + key.LodBias,
            BorderColor = key.IntegerFormat ? (key.TransparentBorder ? BorderColor.IntTransparentBlack : BorderColor.IntOpaqueBlack)
                : key.TransparentBorder ? BorderColor.FloatTransparentBlack : BorderColor.FloatOpaqueWhite,
        };
        VulkanException.Check(device.Vk.CreateSampler(device.Device, &info, null, out var created), "vkCreateSampler");
        samplers[key] = device.AddSampler(created);
        return new GpuSampler(samplers[key], key);
    }

    public void Dispose()
    {
        foreach (var s in samplers.Values) device.Vk.DestroySampler(device.Device, device.SamplerAt(s), null);
        samplers.Clear();
    }
}
