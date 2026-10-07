using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Meitou.Rendering.Upscalers;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace Meitou.Rendering.Display;

/// <summary>
/// The window and the GPU behind the renderers: a Vulkan device, the native <see cref="GpuContext"/> they draw with and,
/// with a window, <see cref="VulkanPresenter"/> showing its backbuffer. Without a window (offscreen pictures, benchmarks) Vulkan runs
/// headless. <c>MEITOU_VK_VALIDATION=1</c> turns on the validation layer; its error count is reported on dispose.
/// </summary>
public sealed unsafe class VulkanDisplay : IDisposable
{
    /// <summary>The window; null when headless.</summary>
    public IWindow? Window { get; }
    public VulkanDevice Device { get; }
    /// <summary>The native GPU API the renderers draw with; its frames are what <see cref="BeginFrame"/> / <see cref="Present"/> begin and submit.</summary>
    public GpuContext Context { get; }
    readonly VulkanPresenter? presenter;

    /// <summary>With a window: what the frame is drawn into (the window's framebuffer size), valid after <see cref="BeginFrame"/>; null when headless.</summary>
    public Texture? Backbuffer => presenter?.Backbuffer;
    /// <summary>Stopwatch ticks spent acquiring swapchain images and presenting (0 when headless).</summary>
    public long AcquireTicks => presenter?.AcquireTicks ?? 0;
    public long PresentTicks => presenter?.PresentTicks ?? 0;
    /// <summary>The window's vsync (<see cref="VulkanPresenter.VSync"/>; the swapchain is remade at the next frame). Always off without a window.</summary>
    public bool VSync { get => presenter?.VSync ?? false; set { if (presenter is not null) presenter.VSync = value; } }

    /// <summary>Streamline (DLSS), when <c>streamline</c> asked for it and it loaded; shut down before the device.</summary>
    public Streamline? Streamline { get; }

    /// <summary>A window created from <paramref name="options"/> (its Vulkan surface; shown), or headless when <paramref name="options"/> is null.</summary>
    public VulkanDisplay(WindowOptions? options, bool vsync, bool streamline = false)
    {
        var (validation, sync, gpu) = VulkanDeviceOptions.FromEnvironment();
        validationRequested = validation == true;
        var deviceOptions = new VulkanDeviceOptions
        {
            Validation = validation == true, SyncValidation = sync, GpuValidation = gpu,
            PipelineCachePath = PipelineCacheFile(),
        };
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
        Context = new GpuContext(Device);
        if (options is not null) presenter = new VulkanPresenter(Context, vsync) { PresentFunction = Streamline is { DlssSupported: true } s ? s.PresentProxy : null };
    }

    /// <summary>Starts a frame of the given size; false when nothing can be shown (a minimised window): skip drawing.</summary>
    public bool BeginFrame(int width, int height) => presenter?.BeginFrame(width, height) ?? width > 0 && height > 0;

    /// <summary>Shows the frame (headless: just submits it). The backbuffer stays intact until the next frame, so a screenshot of it is read after this.</summary>
    public void Present()
    {
        if (presenter is not null) presenter.Present();
        else Context.EndFrame();
    }

    /// <summary>Headless: ends the frame (submits it).</summary>
    public void EndFrame() => Context.EndFrame();

    public void Dispose()
    {
        presenter?.Dispose();
        Context.Dispose();
        Device.Frames.WaitAll();   // what was released after the frames in flight
        Streamline?.Dispose();
        if (Device.ValidationErrors > 0) Console.WriteLine($"vulkan validation: {Device.ValidationErrors} errors\n{string.Join("\n", Device.ValidationLog.Take(20))}");
        else if (validationRequested) Console.WriteLine("vulkan validation: 0 errors");
        Device.Dispose();
        Window?.Dispose();
    }

    readonly bool validationRequested;

    /// <summary>
    /// <c>%LOCALAPPDATA%\Meitou\pipeline-cache.bin</c> (the driver checks the header and ignores a cache from another device or driver);
    /// <c>MEITOU_PIPELINE_CACHE</c> overrides the path, <c>MEITOU_PIPELINE_CACHE=0</c> turns it off. Null when there is no place for it.
    /// </summary>
    static string? PipelineCacheFile()
    {
        var env = Environment.GetEnvironmentVariable("MEITOU_PIPELINE_CACHE");
        if (env == "0") return null;
        if (!string.IsNullOrEmpty(env)) return env;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) return null;
        try
        {
            var dir = Path.Combine(root, "Meitou");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "pipeline-cache.bin");
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
