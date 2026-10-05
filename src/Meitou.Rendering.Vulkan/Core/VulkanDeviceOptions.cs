using Silk.NET.Vulkan;

namespace Meitou.Rendering.Vulkan.Core;

/// <summary>How to create a <see cref="VulkanDevice"/>.</summary>
public sealed class VulkanDeviceOptions
{
    /// <summary>Validation layer + debug messenger: null = on when the layer is installed.</summary>
    public bool? Validation { get; set; }

    /// <summary>Prefer a discrete GPU over an integrated one.</summary>
    public bool PreferDiscrete { get; set; } = true;

    /// <summary>Frames the CPU may run ahead of the GPU (2..3).</summary>
    public int FramesInFlight { get; set; } = 2;

    /// <summary>Pipeline cache file: loaded when it exists (a bad file is ignored), saved on dispose.</summary>
    public string? PipelineCachePath { get; set; }

    /// <summary>
    /// Creates the window surface from the instance (null = headless, no swapchain extension). The device owns the
    /// surface afterwards (<see cref="VulkanDevice.Surface"/>) and destroys it on dispose. The windowing library's
    /// required instance extensions go in <see cref="InstanceExtensions"/>.
    /// </summary>
    public Func<Instance, SurfaceKHR>? CreateSurface { get; set; }

    /// <summary>Extra instance extensions to enable (e.g. the ones the windowing library needs for a surface).</summary>
    public string[]? InstanceExtensions { get; set; }
}
