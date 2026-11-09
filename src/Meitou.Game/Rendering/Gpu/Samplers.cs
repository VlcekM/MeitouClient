using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using Sampler = Silk.NET.Vulkan.Sampler;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// GL's sampler state as a value (docs/renderer-native.md 2.4). <see cref="FromGl"/> is the rule VkGl used in <c>SamplerFor</c>, moved here so
/// translated and native draws share it: integer formats sample nearest, a sampler without mipmaps clamps its LOD to 0.25 (GL's "no mips" behaviour),
/// the LOD bias applies to mipmapped samplers only, anisotropy is clamped to 16, and the compare mode is used only by shadow samplers.
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

    /// <summary>
    /// <see cref="FromGl"/> for a plain sampler clamped to the edge on every axis: no compare, no border, no anisotropy, no LOD bias (the
    /// renderers' own targets and pictures are read with it).
    /// </summary>
    public static SamplerDesc Clamped(TextureMinFilter min, TextureMagFilter mag) =>
        FromGl(min, mag, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false,
            1, false, 0);
}

/// <summary>
/// One Vulkan sampler per <see cref="SamplerDesc"/>, for the device's lifetime. Applies the device rules: anisotropy only where the device has it,
/// and on NVIDIA a −0.25 LOD bias for anisotropic samplers (its OpenGL picked mips a quarter level finer than its Vulkan driver; DECISIONS 9). Render
/// thread only.
/// </summary>
public sealed unsafe class SamplerCache : IGpuSamplers, IDisposable
{
    /// <summary>
    /// The device the samplers are made on.
    /// </summary>
    readonly VulkanDevice _device;
    /// <summary>
    /// The made samplers' indices in the device's sampler table, by their (device-adjusted) description.
    /// </summary>
    readonly Dictionary<SamplerDesc, int> _samplers = [];
    /// <summary>
    /// Backs <see cref="MaxAnisotropy"/>.
    /// </summary>
    int _maxAnisotropy = 16;

    /// <summary>
    /// An empty cache on <paramref name="device"/>.
    /// </summary>
    public SamplerCache(VulkanDevice device) => this._device = device;

    /// <summary>
    /// How many samplers are made.
    /// </summary>
    public int Count => _samplers.Count;

    /// <summary>
    /// The Tab panel's anisotropic filtering: the most any sampler gets (1, 2, 4, 8 or 16; 16, the default, leaves every texture as asked). A change
    /// bumps <see cref="Generation"/>, which the caches of sampler bindings compare to make theirs again.
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
    /// The sampler for <paramref name="desc"/> with its anisotropy capped by <see cref="MaxAnisotropy"/> (1 without device anisotropy); made on first
    /// use.
    /// </summary>
    public GpuSampler Get(in SamplerDesc desc)
    {
        var key = _device.SamplerAnisotropy
            ? desc with { Anisotropy = Math.Min(desc.Anisotropy, _maxAnisotropy) }
            : desc with { Anisotropy = 1 };
        if (_samplers.TryGetValue(key, out var index)) return new GpuSampler(index, key);

        var vkFilters = GlConventions.VkFilters(key.Min);
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MinFilter = vkFilters.Min,
            MagFilter = GlConventions.VkFilter(key.Mag),
            MipmapMode = vkFilters.Mip,
            AddressModeU = GlConventions.AddressMode(key.U),
            AddressModeV = GlConventions.AddressMode(key.V),
            AddressModeW = GlConventions.AddressMode(key.W),
            AnisotropyEnable = key.Anisotropy > 1,
            MaxAnisotropy = key.Anisotropy,
            CompareEnable = key.Compare,
            CompareOp = GlConventions.CompareOp(key.Op),
            MinLod = 0,
            MaxLod = key.Mipmapped ? Vk.LodClampNone : 0.25f,

            // NVIDIA's OpenGL picks mips a quarter level finer than its Vulkan driver with anisotropic filtering on: matched here
            // (docs/engine.md "Vulkan backend"; measured 1.2 -> 0.001 mean difference on the rock view). Plus the upscaler's bias.
            MipLodBias = (key.Anisotropy > 1 && _device.Properties.VendorID == 0x10DE ? -0.25f : 0) + key.LodBias,
            BorderColor = key.IntegerFormat
                ? (key.TransparentBorder ? BorderColor.IntTransparentBlack : BorderColor.IntOpaqueBlack)
                : key.TransparentBorder ? BorderColor.FloatTransparentBlack : BorderColor.FloatOpaqueWhite,
        };
        VulkanException.Check(_device.Vk.CreateSampler(_device.Device, &info, null, out var created), "vkCreateSampler");
        _samplers[key] = _device.AddSampler(created);
        return new GpuSampler(_samplers[key], key);
    }

    /// <summary>
    /// Destroys every sampler made.
    /// </summary>
    public void Dispose()
    {
        foreach (var s in _samplers.Values) _device.Vk.DestroySampler(_device.Device, _device.SamplerAt(s), null);

        _samplers.Clear();
    }
}
