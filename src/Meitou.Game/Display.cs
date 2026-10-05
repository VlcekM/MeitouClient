using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;
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

    public Display(string renderer, WindowOptions options, bool visible, bool vsync)
    {
        if (renderer == "vulkan")
        {
            if (visible)
            {
                Window = Silk.NET.Windowing.Window.Create(options with { API = GraphicsAPI.DefaultVulkan, VSync = false });
                Window.Initialize();
                var surface = Window.VkSurface ?? throw new InvalidOperationException("the window has no Vulkan surface support");
                var names = surface.GetRequiredExtensions(out uint count);
                var extensions = new string[count];
                for (int i = 0; i < count; i++) extensions[i] = SilkMarshal.PtrToString((nint)names[i])!;
                vulkan = VulkanDevice.Create(new VulkanDeviceOptions
                {
                    Validation = Environment.GetEnvironmentVariable("MEITOU_VK_VALIDATION") == "1",
                    InstanceExtensions = extensions,
                    CreateSurface = instance => surface.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface(),
                });
            }
            else vulkan = VulkanDevice.Create(new VulkanDeviceOptions { Validation = Environment.GetEnvironmentVariable("MEITOU_VK_VALIDATION") == "1" });
            Console.WriteLine($"vulkan    {vulkan.DeviceName}");
            vkGl = new VkGl(vulkan);
            Gl = vkGl;
            if (visible) presenter = new VulkanPresenter(vulkan, vkGl, vsync);
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
        if (vulkan is not null)
        {
            if (vulkan.ValidationErrors > 0) Console.WriteLine($"vulkan validation: {vulkan.ValidationErrors} errors\n{string.Join("\n", vulkan.ValidationLog.Take(20))}");
            vulkan.Dispose();
        }
        rawGl?.Dispose();
        Window?.Dispose();
    }
}
