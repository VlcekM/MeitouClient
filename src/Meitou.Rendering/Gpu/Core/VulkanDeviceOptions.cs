using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu.Core;

/// <summary>How to create a <see cref="VulkanDevice"/>.</summary>
public sealed class VulkanDeviceOptions
{
    /// <summary>Validation layer + debug messenger: null = on when the layer is installed.</summary>
    public bool? Validation { get; set; }

    /// <summary>With validation: synchronisation validation too (<c>validate_sync</c> through VK_EXT_layer_settings).</summary>
    public bool SyncValidation { get; set; }

    /// <summary>With validation: GPU-assisted validation (<c>gpuav_enable</c>: bindless indices, buffer bounds in shaders).</summary>
    public bool GpuValidation { get; set; }

    /// <summary>
    /// <c>MEITOU_VK_VALIDATION</c>: "1" validation, "sync" plus synchronisation validation, "gpu" plus GPU-assisted validation, "0" off; null
    /// when unset (the caller's default applies).
    /// </summary>
    public static (bool? Validation, bool Sync, bool Gpu) FromEnvironment()
    {
        var v = Environment.GetEnvironmentVariable("MEITOU_VK_VALIDATION")?.Trim().ToLowerInvariant();
        return v switch
        {
            null or "" => (null, false, false),
            "0" or "off" or "false" => (false, false, false),
            "sync" => (true, true, false),
            "gpu" => (true, false, true),
            _ => (true, false, false),
        };
    }

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

    /// <summary>Extra device extensions, enabled when the device has them (e.g. what Streamline asks for DLSS).</summary>
    public string[]? DeviceExtensions { get; set; }

    /// <summary>Vulkan 1.2 features to enable when supported, by their C name (Streamline's list: <c>descriptorIndexing</c>, <c>bufferDeviceAddress</c>, <c>timelineSemaphore</c>).</summary>
    public string[]? Features12 { get; set; }

    /// <summary>
    /// Bench-only measurements (<c>--bench-tris</c>, docs/bench.md "Triangles per pass"): turns on the pipeline statistics queries and
    /// the fragment shader barycentrics (VK_KHR_fragment_shader_barycentric) when the device has them, and buffer device address.
    /// Off by default: a normal run creates the device exactly as before.
    /// </summary>
    public bool TriangleMeasurements { get; set; }

    /// <summary>
    /// Ray queries for the ray-traced global illumination (<c>--gi</c>, docs/render-gi.md): enables VK_KHR_acceleration_structure, VK_KHR_ray_query
    /// and buffer device address when the device has them (<see cref="VulkanDevice.HasRayQuery"/>). Off by default: a normal run creates the device
    /// exactly as before.
    /// </summary>
    public bool RayTracing { get; set; }

    /// <summary>Called right after the device is created (Streamline's <c>slSetVulkanInfo</c>).</summary>
    public Action<VulkanDevice>? DeviceCreated { get; set; }
}
