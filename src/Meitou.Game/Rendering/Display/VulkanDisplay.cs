using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Upscalers;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace Meitou.Game.Rendering.Display;

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
    readonly VulkanPresenter? _presenter;

    /// <summary>With a window: what the frame is drawn into (the window's framebuffer size), valid after <see cref="BeginFrame"/>; null when headless.</summary>
    public GpuTexture? Backbuffer => _presenter?.Backbuffer;
    /// <summary>Stopwatch ticks spent acquiring swapchain images and presenting (0 when headless).</summary>
    public long AcquireTicks => _presenter?.AcquireTicks ?? 0;
    public long PresentTicks => _presenter?.PresentTicks ?? 0;
    /// <summary>The window's vsync (<see cref="VulkanPresenter.VSync"/>; the swapchain is remade at the next frame). Always off without a window.</summary>
    public bool VSync { get => _presenter?.VSync ?? false; set { if (_presenter is not null) _presenter.VSync = value; } }

    /// <summary>True while the swapchain goes through AMD's frame-interpolation layer (the Frame generation setting, when it could be honoured; false after a fallback).</summary>
    public bool FrameGeneration => _presenter?.FrameGeneration is not null;
    /// <summary>The frame-interpolation layer itself (null for the plain swapchain); frame generation is configured on top of it.</summary>
    public FfxSwapchain? FrameGenerationLayer => _presenter?.FrameGeneration;

    /// <summary>True while the swapchain goes through Streamline with DLSS frame generation (the Frame generation setting's DLSS, where supported).</summary>
    public bool DlssFrameGeneration => _presenter?.StreamlineHooks is not null && Streamline is { FrameGenerationSupported: true };

    /// <summary>Streamline (DLSS), when <c>dlss</c> asked for it and it loaded; shut down before the device.</summary>
    public Streamline? Streamline { get; }
    /// <summary>NGX (DLSS on Linux, or on Windows with frame generation), when <c>dlss</c> asked for it and its client library and the DLSS model loaded; shut down before the device.</summary>
    public Ngx? Ngx { get; }

    /// <summary>Set before creating the display: turns on the device features of the bench's triangle measurements (<see cref="VulkanDeviceOptions.TriangleMeasurements"/>; <c>--bench-tris</c>).</summary>
    public static bool TriangleMeasurements { get; set; }

    /// <summary>Set before creating the display: ray queries for the global illumination (<see cref="VulkanDeviceOptions.RayTracing"/>; <c>--gi-debug</c>).</summary>
    public static bool RayTracing { get; set; }

    /// <summary>A window created from <paramref name="options"/> (its Vulkan surface; shown), or headless when <paramref name="options"/> is null.</summary>
    public VulkanDisplay(WindowOptions? options, bool vsync, bool dlss = false, FrameGenerationMethod frameGeneration = FrameGenerationMethod.Off)
    {
        var (validation, sync, gpu) = VulkanDeviceOptions.FromEnvironment();
        _validationRequested = validation == true;
        var deviceOptions = new VulkanDeviceOptions
        {
            Validation = validation == true,
            SyncValidation = sync,
            GpuValidation = gpu,
            PipelineCachePath = PipelineCacheFile(),
            TriangleMeasurements = TriangleMeasurements,
            RayTracing = RayTracing,
        };
        // DLSS frame generation goes through Streamline (Windows, a window); FSR 3's needs AMD's library, checked first, so DLSS leaves Streamline for NGX only
        // when the layer can be used.
        bool dlssFrameGeneration = frameGeneration == FrameGenerationMethod.Dlss && options is not null && OperatingSystem.IsWindows();
        if (frameGeneration == FrameGenerationMethod.Dlss && options is not null && !dlssFrameGeneration)
            Console.WriteLine("frame generation  DLSS unavailable: it needs Streamline, which is Windows only");
        dlss |= dlssFrameGeneration;
        bool fsrFrameGeneration = frameGeneration == FrameGenerationMethod.Fsr;
        string? layerWhy = OperatingSystem.IsMacOS() ? "the FFX library is not available for Metal" : null;
        bool layerLoads = fsrFrameGeneration && options is not null && layerWhy is null && FfxSwapchain.LibraryLoads(out _, out layerWhy);
        if (dlss && OperatingSystem.IsWindows() && layerLoads)
        {
            // Frame generation: the FFX swapchain owns the present, which Streamline's interposer must see, so DLSS goes through NGX directly
            // (meitou_ngx.dll) and Streamline is not loaded. Without NGX, Streamline stays and frame generation is off (below).
            Ngx = Ngx.TryInit(out var ngxWhy);
            if (Ngx is not null) Ngx.Apply(deviceOptions);
            else Console.WriteLine($"upscaler  {ngxWhy}");
        }
        if (dlss && OperatingSystem.IsWindows() && Ngx is null)
        {
            // Streamline goes in before the instance and adds what it needs to the device.
            Streamline = Streamline.TryInit(dlssFrameGeneration, out var why);
            if (Streamline is null) Console.WriteLine($"upscaler  {why}");
            else Streamline.Apply(deviceOptions);
        }
        else if (dlss && OperatingSystem.IsLinux())
        {
            // Linux: NGX through NVIDIA's client library (libmeitou_ngx.so), likewise before the instance.
            Ngx = Ngx.TryInit(out var why);
            if (Ngx is null) Console.WriteLine($"upscaler  {why}");
            else Ngx.Apply(deviceOptions);
        }
        // Frame generation (FSR 3): the swapchain goes through AMD's layer, which needs more graphics queues (FfxSwapchain). Windows, Proton and Linux.
        bool wantLayer = false;
        if (fsrFrameGeneration && options is not null)
        {
            string? why = layerWhy;
            if (!layerLoads) { }
            else if (Streamline is { DlssSupported: true }) why = "Streamline (DLSS) owns the present (NGX did not load, see the upscaler line above)";
            else wantLayer = true;
            if (!wantLayer) Console.WriteLine($"frame generation  unavailable: {why}");
            else deviceOptions.ExtraGraphicsQueues = FfxSwapchain.WantedExtraQueues;
        }
        if (options is not null)
        {
            Window = Silk.NET.Windowing.Window.Create(options.Value with { API = GraphicsAPI.DefaultVulkan, VSync = false });
            Window.Initialize();
            SetWindowIcon(Window);
            var surface = Window.VkSurface ?? throw new InvalidOperationException("the window has no Vulkan surface support");
            var names = surface.GetRequiredExtensions(out uint count);
            var extensions = new string[count];
            for (int i = 0; i < count; i++) extensions[i] = SilkMarshal.PtrToString((nint)names[i])!;
            deviceOptions.InstanceExtensions = [.. extensions, .. deviceOptions.InstanceExtensions ?? []];
            deviceOptions.CreateSurface = instance => surface.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();
            // DLSS frame generation looks the window up by its surface: once the device is handed over, the surface is made again through the interposer.
            if (Streamline is { FrameGenerationLoaded: true } sl && Window.Native?.Win32 is { } win32) sl.UseWindow(win32.Hwnd, win32.HInstance);
        }
        Device = VulkanDevice.Create(deviceOptions);
        // The validation mode in log.txt: a report then shows whether MEITOU_VK_VALIDATION=sync took effect.
        string validationMode = !Device.ValidationEnabled ? "" : Device.SyncValidationEnabled ? ", validation on (sync)" : ", validation on";
        Console.WriteLine($"vulkan    {Device.DeviceName}{validationMode}");
        if (GpuContext.DiagSubmit || GpuContext.DiagIdle)
            Console.WriteLine($"diagnostic{(GpuContext.DiagSubmit ? " MEITOU_DIAG_SUBMIT" : "")}{(GpuContext.DiagIdle ? " MEITOU_DIAG_IDLE" : "")} on (issue #108; slower frames)");
        if (GpuContext.DiagNoMaterialCache || GpuContext.DiagNoGrow)
        {
            string cache = GpuContext.DiagNoMaterialCache ? " MEITOU_DIAG_NO_MATERIAL_CACHE" : "";
            string grow = GpuContext.DiagNoGrow ? " MEITOU_DIAG_NO_GROW" : "";
            Console.WriteLine($"diagnostic{cache}{grow} on (issue #319)");
        }
        Context = new GpuContext(Device);
        if (options is not null)
        {
            _presenter = new VulkanPresenter(Context, vsync)
            {
                PresentFunction = Streamline is { DlssSupported: true } s ? s.PresentProxy : null,
                StreamlineHooks = Streamline is { FrameGenerationSupported: true } g ? g.Swapchain : null,
            };
            if (wantLayer)
            {
                var layer = FfxSwapchain.TryCreate(Device, out var why);
                if (layer is null) Console.WriteLine($"frame generation  unavailable: {why}");
                else _presenter.FrameGeneration = layer;
            }
        }
    }

    /// <summary>The two moons as the window's icon (the embedded 256 x 256 RGBA); a window system that refuses it keeps the default.</summary>
    static void SetWindowIcon(IWindow window)
    {
        try
        {
            using var stream = typeof(VulkanDisplay).Assembly.GetManifestResourceStream("meitou-256.rgba");
            if (stream is null) return;
            var pixels = new byte[256 * 256 * 4];
            stream.ReadExactly(pixels);
            var icon = new Silk.NET.Core.RawImage(256, 256, pixels);
            window.SetWindowIcon(ref icon);
        }
        catch (Exception e) { Console.WriteLine($"window icon  {e.Message}"); }
    }

    /// <summary>Opens a rendered frame for Reflex (DLSS frame generation): its frame token, Reflex's sleep, the simulation start marker. Call before the world
    /// advances; does nothing without DLSS frame generation.</summary>
    public void BeginLatencyFrame() => Streamline?.BeginFrame();

    /// <summary>Starts a frame of the given size; false when nothing can be shown (a minimised window): skip drawing.</summary>
    public bool BeginFrame(int width, int height)
    {
        Streamline?.Marker(PclMarker.SimulationEnd);
        Streamline?.Marker(PclMarker.RenderSubmitStart);
        return _presenter?.BeginFrame(width, height) ?? width > 0 && height > 0;
    }

    /// <summary>Shows the frame (headless: just submits it). The backbuffer stays intact until the next frame, so a screenshot of it is read after this.
    /// With <paramref name="topDown"/> the window shows that finished picture (row 0 the top) instead of the flipped backbuffer.</summary>
    public void Present(GpuTexture? topDown = null)
    {
        Streamline?.Marker(PclMarker.RenderSubmitEnd);
        Streamline?.Marker(PclMarker.PresentStart);
        if (_presenter is not null) _presenter.Present(topDown);
        else Context.EndFrame();
        Streamline?.Marker(PclMarker.PresentEnd);
        Streamline?.EndFrame();
    }

    /// <summary>Headless: ends the frame (submits it).</summary>
    public void EndFrame() => Context.EndFrame();

    public void Dispose()
    {
        _presenter?.Dispose();
        Context.Dispose();
        Device.Frames.WaitAll();   // what was released after the frames in flight
        Streamline?.Dispose();
        Ngx?.Dispose();
        if (Device.ValidationErrors > 0) Console.WriteLine($"vulkan validation: {Device.ValidationErrors} errors\n{string.Join("\n", Device.ValidationLog.Take(20))}");
        else if (_validationRequested) Console.WriteLine("vulkan validation: 0 errors");
        Device.Dispose();
        Window?.Dispose();
    }

    readonly bool _validationRequested;

    /// <summary>
    /// <c>%LOCALAPPDATA%\Meitou\pipeline-cache.bin</c> (the driver checks the header and ignores a cache from another device or driver);
    /// <c>--no-load-cache</c> turns it off. Null when there is no place for it.
    /// </summary>
    static string? PipelineCacheFile()
    {
        if (Data.World.LoadCaches.Disabled) return null;
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