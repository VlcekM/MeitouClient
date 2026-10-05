using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;
using Meitou.Rendering.Vulkan.Upscalers;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace Meitou.Game;

/// <summary>
/// The window and the GPU backend behind <see cref="IGl"/>: OpenGL (the GL calls go to the driver, <see cref="IWindow.SwapBuffers"/>
/// shows the frame) or Vulkan (<see cref="VkGl"/> translates them, <see cref="VulkanPresenter"/> shows framebuffer 0).
/// Without a window (offscreen pictures) Vulkan runs headless and OpenGL uses a hidden window.
/// </summary>
sealed unsafe class Display : IDisposable
{
    public IWindow? Window { get; }
    public IGl Gl { get; }
    public bool IsVulkan => vulkan is not null;
    readonly GL? rawGl;
    readonly VulkanDevice? vulkan;
    readonly VkGl? vkGl;
    readonly VulkanPresenter? presenter;

    /// <summary>Streamline (DLSS), when <paramref name="streamline"/> asked for it and it loaded; shut down before the device.</summary>
    public Streamline? Streamline { get; }

    public Display(string renderer, WindowOptions options, bool visible, bool vsync, bool streamline = false)
    {
        if (renderer == "vulkan")
        {
            var deviceOptions = new VulkanDeviceOptions { Validation = Environment.GetEnvironmentVariable("MEITOU_VK_VALIDATION") == "1" };
            if (streamline)
            {
                Streamline = Streamline.TryInit(out var why);
                if (Streamline is null) Console.WriteLine($"upscaler  {why}");
                else Streamline.Apply(deviceOptions);
            }
            if (visible)
            {
                Window = Silk.NET.Windowing.Window.Create(options with { API = GraphicsAPI.DefaultVulkan, VSync = false });
                Window.Initialize();
                var surface = Window.VkSurface ?? throw new InvalidOperationException("the window has no Vulkan surface support");
                var names = surface.GetRequiredExtensions(out uint count);
                var extensions = new string[count];
                for (int i = 0; i < count; i++) extensions[i] = SilkMarshal.PtrToString((nint)names[i])!;
                deviceOptions.InstanceExtensions = [.. extensions, .. deviceOptions.InstanceExtensions ?? []];
                deviceOptions.CreateSurface = instance => surface.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();
                vulkan = VulkanDevice.Create(deviceOptions);
            }
            else vulkan = VulkanDevice.Create(deviceOptions);
            Console.WriteLine($"vulkan    {vulkan.DeviceName}");
            vkGl = new VkGl(vulkan);
            Gl = vkGl;
            if (visible) presenter = new VulkanPresenter(vulkan, vkGl, vsync) { PresentFunction = Streamline is { DlssSupported: true } s ? s.PresentProxy : null };
        }
        else
        {
            Window = Silk.NET.Windowing.Window.Create(options with
            {
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
                IsVisible = visible,
                VSync = vsync,
                Samples = 0,
                PreferredDepthBufferBits = 24,
            });
            Window.Initialize();
            rawGl = Window.CreateOpenGL();
            Gl = new GlPassthrough(rawGl);
        }
    }

    /// <summary>Starts a frame of the given size; false when nothing can be shown (a minimised window): skip drawing.</summary>
    public bool BeginFrame(int width, int height) => presenter?.BeginFrame(width, height) ?? width > 0 && height > 0;

    /// <summary>Shows the frame (or, offscreen, just submits it).</summary>
    public void Present()
    {
        if (presenter is not null) presenter.Present();
        else if (vkGl is not null) vkGl.EndFrame();
        else Window!.SwapBuffers();
    }

    /// <summary>Whether a screenshot of framebuffer 0 is read after <see cref="Present"/> (Vulkan: framebuffer 0 stays intact until
    /// the next frame) rather than before the swap (OpenGL's back buffer).</summary>
    public bool ReadsAfterPresent => IsVulkan;

    /// <summary>Offscreen: ends the frame on Vulkan (submits it); nothing for OpenGL.</summary>
    public void EndFrame() => vkGl?.EndFrame();

    public void Dispose()
    {
        presenter?.Dispose();
        vkGl?.Dispose();
        Streamline?.Dispose();
        if (vulkan is not null)
        {
            if (vulkan.ValidationErrors > 0) Console.WriteLine($"vulkan validation: {vulkan.ValidationErrors} errors\n{string.Join("\n", vulkan.ValidationLog.Take(20))}");
            vulkan.Dispose();
        }
        rawGl?.Dispose();
        Window?.Dispose();
    }
}
