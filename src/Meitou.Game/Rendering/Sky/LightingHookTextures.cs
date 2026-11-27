using Meitou.Data.Textures;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// The images the installed lighting hooks read (docs/modding.md "Lighting hooks": <c>textures</c> in <c>meitou.json</c>), loaded once for a
/// device and published as the frame globals <c>uModTexture0</c> to <c>uModTexture3</c>: a legacy program binds its sampler by that name, a
/// native one reads it through the bindless table. An image is RGBA 8 bits a channel as its file holds it (PNG, TGA, JPG, or the top level of
/// a DDS), with mip levels made from it, tiled or clamped as the manifest says. One that cannot be loaded is logged and reads as the stand-in
/// (black, alpha 1).
/// </summary>
sealed class LightingHookTextures : IDisposable
{
    /// <summary>
    /// The loaded images, for releasing them.
    /// </summary>
    readonly List<SampledImage> _images = [];

    /// <summary>
    /// Loads every installed hook's textures on <paramref name="gpu"/> and publishes each under its sampler's name.
    /// </summary>
    public LightingHookTextures(IGpuContext gpu)
    {
        foreach (var hook in LightingHooks.Installed)
        {
            foreach (var texture in hook.Textures)
            {
                var image = Load(gpu, hook.ModId, texture);
                if (image is not null) _images.Add(image);
                gpu.Globals.Publish(LightingHooks.TextureUniform(texture.Slot), () => image?.Sampled() ?? default);
            }
        }
    }

    /// <summary>
    /// One texture's image on the device; null, with a log line, when its file cannot be read or is no image.
    /// </summary>
    static SampledImage? Load(IGpuContext gpu, string modId, LightingHooks.HookTexture texture)
    {
        try
        {
            var level = TextureLoader.LoadFile(texture.Path, allMips: false).Levels[0];
            return SampledImage.Rgba8(gpu, level, texture.Repeat, mipmaps: true, $"mod {modId} {texture.Name}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod       {modId}: lighting texture '{texture.Name}' not loaded: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Releases the images.
    /// </summary>
    public void Dispose()
    {
        foreach (var image in _images) image.Dispose();
        _images.Clear();
    }
}
