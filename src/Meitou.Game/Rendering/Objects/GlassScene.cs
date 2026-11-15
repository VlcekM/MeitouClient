using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// The scene's colour copied before the objects' blended pass for the glass bottles to refract (docs/render-objects.md "Glass bottles"): the lit
/// opaque scene after the GI's resolve and the reflections, at the render size. Vulkan only: elsewhere the bottles stay ordinary see-through items.
/// </summary>
sealed class GlassScene(IGpuContext gpu) : IDisposable
{
    /// <summary>
    /// Linear and edge-clamped, no mipmaps: a bent lookup near the screen's edge takes the edge.
    /// </summary>
    static readonly SamplerDesc ClampLinear = new(TextureMinFilter.Linear, TextureMagFilter.Linear, false,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false,
        DepthFunction.Always, false, false, 1, 0);

    /// <summary>
    /// The copy's bindless entry.
    /// </summary>
    readonly BindlessTexture _entry = new(gpu, BindlessKind.Texture2D);

    /// <summary>
    /// The copy (made again when the scene's size or format changed).
    /// </summary>
    GpuTexture? _copy;

    /// <summary>
    /// Copies <paramref name="scene"/> (no pass open) and returns the copy's bindless index; null where there is no copy (not Vulkan).
    /// </summary>
    public uint? Capture(GpuTexture scene)
    {
        if (gpu is not GpuContext vulkan) return null;

        var cmd = gpu.BeginNative("glass scene copy");
        Ensure(vulkan, cmd, scene.Desc);
        cmd.Blit(scene, _copy!, TextureMinFilter.Linear);
        gpu.EndNative(cmd);

        return _entry.Index(new SampledTexture(gpu.Samplers.Get(ClampLinear), _copy!.View()));
    }

    /// <summary>
    /// Makes the copy's texture when there is none of the scene's size and format.
    /// </summary>
    void Ensure(GpuContext vulkan, IGpuCommandList cmd, TextureDesc scene)
    {
        if (_copy is { } c && c.Desc.Width == scene.Width && c.Desc.Height == scene.Height && c.Desc.Format == scene.Format) return;

        _copy?.Dispose();
        _copy = vulkan.CreateTexture(new TextureDesc(scene.Format, scene.Width, scene.Height, Name: "glass scene"), ((CommandList)cmd).Handle);
    }

    /// <summary>
    /// Frees the copy and its entry.
    /// </summary>
    public void Dispose()
    {
        _entry.Dispose();
        _copy?.Dispose();
    }
}
