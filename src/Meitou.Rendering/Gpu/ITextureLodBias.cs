namespace Meitou.Rendering.Gpu;

/// <summary>
/// A backend that can shift the mip level of every mipmapped texture fetch at once: temporal upscalers render at a smaller size, and
/// sample textures at the detail of the display size (a negative bias, log2 of the render scale and a little more), so the reconstructed
/// picture keeps the texture detail. VkGl implements it (the sampler's mipLodBias); OpenGL 3.3 has no global bias, so GL leaves it out.
/// </summary>
public interface ITextureLodBias
{
    float TextureLodBias { get; set; }
}
