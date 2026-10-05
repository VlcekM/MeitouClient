using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;
using Meitou.Rendering.Vulkan.Upscalers;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace Meitou.Rendering.Display;

/// <summary>
/// The window and the GPU backend behind <see cref="IGl"/>: a Vulkan device, <see cref="VkGl"/> translating the renderers' calls and,
/// with a window, <see cref="VulkanPresenter"/> showing framebuffer 0. Without a window (offscreen pictures, benchmarks) Vulkan runs
/// headless. <c>MEITOU_VK_VALIDATION=1</c> turns on the validation layer; its error count is reported on dispose.
/// </summary>
public sealed unsafe class VulkanDisplay : IDisposable
{
    /// <summary>The window; null when headless.</summary>
    public IWindow? Window { get; }
    public IGl Gl { get; }
    public VkGl VkGl { get; }
    public VulkanDevice Device { get; }
    readonly VulkanPresenter? presenter;

    /// <summary>Streamline (DLSS), when <c>streamline</c> asked for it and it loaded; shut down before the device.</summary>
    public Streamline? Streamline { get; }

    /// <summary>A window created from <paramref name="options"/> (its Vulkan surface; shown), or headless when <paramref name="options"/> is null.</summary>
    public VulkanDisplay(WindowOptions? options, bool vsync, bool streamline = false)
    {
        var deviceOptions = new VulkanDeviceOptions { Validation = Environment.GetEnvironmentVariable("MEITOU_VK_VALIDATION") == "1" };
        if (streamline)
        {
            // Streamline goes in before the instance and adds what it needs to the device.
            Streamline = Streamline.TryInit(out var why);
            if (Streamline is null) Console.WriteLine($"upscaler  {why}");
            else Streamline.Apply(deviceOptions);
        }
        if (options is not null)
        {
            Window = Silk.NET.Windowing.Window.Create(options.Value with { API = GraphicsAPI.DefaultVulkan, VSync = false });
            Window.Initialize();
            var surface = Window.VkSurface ?? throw new InvalidOperationException("the window has no Vulkan surface support");
            var names = surface.GetRequiredExtensions(out uint count);
            var extensions = new string[count];
            for (int i = 0; i < count; i++) extensions[i] = SilkMarshal.PtrToString((nint)names[i])!;
            deviceOptions.InstanceExtensions = [.. extensions, .. deviceOptions.InstanceExtensions ?? []];
            deviceOptions.CreateSurface = instance => surface.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();
        }
        Device = VulkanDevice.Create(deviceOptions);
        Console.WriteLine($"vulkan    {Device.DeviceName}");
        VkGl = new VkGl(Device);
        Gl = VkGl;
        if (options is not null) presenter = new VulkanPresenter(Device, VkGl, vsync) { PresentFunction = Streamline is { DlssSupported: true } s ? s.PresentProxy : null };
    }

    /// <summary>Starts a frame of the given size; false when nothing can be shown (a minimised window): skip drawing.</summary>
    public bool BeginFrame(int width, int height) => presenter?.BeginFrame(width, height) ?? width > 0 && height > 0;

    /// <summary>Shows the frame (headless: just submits it). Framebuffer 0 stays intact until the next frame, so a screenshot of it is read after this.</summary>
    public void Present()
    {
        if (presenter is not null) presenter.Present();
        else VkGl.EndFrame();
    }

    /// <summary>Headless: ends the frame (submits it).</summary>
    public void EndFrame() => VkGl.EndFrame();

    public void Dispose()
    {
        presenter?.Dispose();
        VkGl.Dispose();
        Streamline?.Dispose();
        if (Device.ValidationErrors > 0) Console.WriteLine($"vulkan validation: {Device.ValidationErrors} errors\n{string.Join("\n", Device.ValidationLog.Take(20))}");
        else if (Environment.GetEnvironmentVariable("MEITOU_VK_VALIDATION") == "1") Console.WriteLine("vulkan validation: 0 errors");
        Device.Dispose();
        Window?.Dispose();
    }
}
