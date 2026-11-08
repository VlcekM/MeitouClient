using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using static Meitou.Game.Rendering.Upscalers.SlTypes;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Meitou.Game.Rendering.Upscalers;

/// <summary>
/// NVIDIA Streamline (MIT; the signed <c>sl.interposer.dll</c>, <c>sl.common.dll</c>, <c>sl.dlss.dll</c> and NVIDIA's <c>nvngx_dlss.dll</c>, with frame
/// generation also <c>sl.dlss_g.dll</c>, <c>nvngx_dlssg.dll</c>, <c>sl.reflex.dll</c> and <c>sl.pcl.dll</c>; none of them in the repository), loaded at run
/// time from <see cref="VendorUpscalers.DlssPath"/> or next to the executable, in Streamline's manual-hooking mode: <see cref="TryInit"/> runs
/// <c>slInit</c> before the Vulkan instance exists and reports the extensions, features and queues the loaded features need (<see cref="Apply"/> adds
/// them to the device), the device is handed over with <c>slSetVulkanInfo</c>, and presents go through the interposer's <c>vkQueuePresentKHR</c>
/// (<see cref="PresentProxy"/>), which Streamline needs every frame. With DLSS frame generation the swapchain's whole life goes through the
/// interposer too (<see cref="SwapchainHooks"/>). Shut down (<see cref="Dispose"/>) before the device is destroyed. docs/render-post.md.
/// </summary>
public sealed unsafe class Streamline : IDisposable
{
    public const string InterposerName = "sl.interposer.dll";
    /// <summary>The DLLs DLSS frame generation adds to the upscaler's.</summary>
    public static readonly string[] FrameGenerationFiles = ["sl.dlss_g.dll", "nvngx_dlssg.dll", "sl.reflex.dll", "sl.pcl.dll"];
    const ulong SdkVersion = (2UL << 48) | (14UL << 32) | (16UL << 16) | 0xfedc;  // Streamline 2.14.16 (sl_version.h)
    /// <summary>The viewports: DLSS upscales on 0 (constants for the post chain's bottom-up images), frame generation runs on 1 (top-down, as the swapchain).</summary>
    public const uint UpscaleViewport = 0, FrameGenerationViewport = 1;
    static readonly Guid PreferencesType = new("1ca10965-bf8e-432b-8da1-6716d879fb14"), RequirementsType = new("66714097-ac6d-4bc6-8915-1e0f55a6b61f"),
        AdapterInfoType = new("0677315f-a746-4492-9f42-cb6142c9c3d4"), VulkanInfoType = new("0eed6fd5-82cd-43a9-bdb5-47a5ba2f45d6");

    [StructLayout(LayoutKind.Sequential)]
    struct Preferences
    {
        public nint Next; public Guid StructType; public nuint StructVersion;
        public byte ShowConsole; public uint LogLevel; public nint PathsToPlugins; public uint NumPathsToPlugins; public nint PathToLogsAndData;
        public nint AllocateCallback, ReleaseCallback, LogMessageCallback;
        public ulong Flags; public nint FeaturesToLoad; public uint NumFeaturesToLoad; public uint ApplicationId; public uint Engine;
        public nint EngineVersion, ProjectId; public uint RenderApi;
    }
    [StructLayout(LayoutKind.Sequential)] struct SlVersion { public uint Major, Minor, Build; }
    [StructLayout(LayoutKind.Sequential)]
    struct FeatureRequirements
    {
        public nint Next; public Guid StructType; public nuint StructVersion;
        public uint Flags, MaxNumCpuThreads, MaxNumViewports, NumRequiredTags; public nint RequiredTags;
        public SlVersion OsDetected, OsRequired, DriverDetected, DriverRequired;
        public uint VkNumComputeQueuesRequired, VkNumGraphicsQueuesRequired;
        public uint VkNumDeviceExtensions; public nint VkDeviceExtensions;
        public uint VkNumInstanceExtensions; public nint VkInstanceExtensions;
        public uint VkNumFeatures12; public nint VkFeatures12;
        public uint VkNumFeatures13; public nint VkFeatures13;
        public uint VkNumOpticalFlowQueuesRequired;
    }
    [StructLayout(LayoutKind.Sequential)] struct AdapterInfo { public nint Next; public Guid StructType; public nuint StructVersion; public nint Luid; public uint LuidSize; public nint VkPhysicalDevice; }
    [StructLayout(LayoutKind.Sequential)]
    struct VulkanInfo
    {
        public nint Next; public Guid StructType; public nuint StructVersion;
        public nint Device, Instance, PhysicalDevice;
        public uint ComputeQueueIndex, ComputeQueueFamily, GraphicsQueueIndex, GraphicsQueueFamily, OpticalFlowQueueIndex, OpticalFlowQueueFamily;
        public byte UseNativeOpticalFlowMode; public uint ComputeQueueCreateFlags, GraphicsQueueCreateFlags, OpticalFlowQueueCreateFlags;
    }

    /// <summary>The interposer's replacements for the swapchain calls (sl_hooks.h's Vulkan list); with DLSS frame generation every swapchain call goes through them.</summary>
    public readonly struct SwapchainHooks
    {
        public delegate* unmanaged<Device, SwapchainCreateInfoKHR*, AllocationCallbacks*, SwapchainKHR*, Result> Create { get; init; }
        public delegate* unmanaged<Device, SwapchainKHR, AllocationCallbacks*, void> Destroy { get; init; }
        public delegate* unmanaged<Device, SwapchainKHR, uint*, Image*, Result> GetImages { get; init; }
        public delegate* unmanaged<Device, SwapchainKHR, ulong, VkSemaphore, Fence, uint*, Result> Acquire { get; init; }
    }

    readonly nint library;
    readonly delegate* unmanaged<int> shutdown;
    readonly delegate* unmanaged<VulkanInfo*, int> setVulkanInfo;
    readonly delegate* unmanaged<uint, AdapterInfo*, int> isFeatureSupported;
    readonly delegate* unmanaged<uint, byte, int> setFeatureLoaded;
    readonly delegate* unmanaged<uint, byte*, void**, int> getFeatureFunction;
    readonly delegate* unmanaged<nint*, uint*, int> getNewFrameToken;
    internal readonly delegate* unmanaged<void*, nint, void*, int> SetConstants;
    internal readonly delegate* unmanaged<nint, void*, void*, uint, nint, int> SetTagForFrame;
    internal readonly delegate* unmanaged<uint, nint, void**, uint, nint, int> EvaluateFeature;
    internal readonly delegate* unmanaged<uint, void*, int> FreeResources;
    internal delegate* unmanaged<void*, void*, int> DlssSetOptions;
    internal delegate* unmanaged<void*, void*, int> DlssgSetOptions;
    internal delegate* unmanaged<void*, void*, void*, int> DlssgGetState;
    delegate* unmanaged<void*, int> reflexSetOptions;
    delegate* unmanaged<nint, int> reflexSleep;
    delegate* unmanaged<uint, nint, int> pclSetMarker;
    readonly delegate* unmanaged<nint, byte*, nint> interposerGetDeviceProcAddr;
    readonly delegate* unmanaged<nint, byte*, nint> interposerGetInstanceProcAddr;
    nint pluginPath, projectId, engineVersion, features;
    uint frameIndex;
    nint openToken;
    (nint Hwnd, nint HInstance) window;
    VulkanDevice? attached;
    bool disposed;

    public string[] InstanceExtensions { get; private set; } = [];
    public string[] DeviceExtensions { get; private set; } = [];
    public string[] Features12 { get; private set; } = [];
    public string[] Features13 { get; private set; } = [];
    /// <summary>Queues of the graphics family Streamline's features use (graphics and compute; DLSS frame generation presents on one), created after ours.</summary>
    public int GraphicsQueues { get; private set; }
    int computeQueues;
    /// <summary>After <see cref="Attach"/>: DLSS runs on this device (an NVIDIA GPU with a recent driver and <c>nvngx_dlss.dll</c> in place).</summary>
    public bool DlssSupported { get; private set; }
    /// <summary>DLSS frame generation was asked for and its DLLs loaded (<see cref="TryInit"/>).</summary>
    public bool FrameGenerationLoaded { get; }
    /// <summary>After <see cref="Attach"/>: DLSS frame generation and Reflex run on this device (an RTX 40 or later with hardware GPU scheduling on).</summary>
    public bool FrameGenerationSupported { get; private set; }
    public string Directory { get; }
    /// <summary>The interposer's <c>vkQueuePresentKHR</c> (after <see cref="Attach"/>): presents must go through it in manual-hooking mode.</summary>
    public delegate* unmanaged<Queue, PresentInfoKHR*, Result> PresentProxy { get; private set; }
    /// <summary>The swapchain calls through the interposer, when <see cref="FrameGenerationSupported"/>.</summary>
    public SwapchainHooks? Swapchain { get; private set; }

    Streamline(nint interposer, string directory, bool frameGeneration)
    {
        library = interposer;
        Directory = directory;
        FrameGenerationLoaded = frameGeneration;
        shutdown = (delegate* unmanaged<int>)NativeLibrary.GetExport(interposer, "slShutdown");
        setVulkanInfo = (delegate* unmanaged<VulkanInfo*, int>)NativeLibrary.GetExport(interposer, "slSetVulkanInfo");
        isFeatureSupported = (delegate* unmanaged<uint, AdapterInfo*, int>)NativeLibrary.GetExport(interposer, "slIsFeatureSupported");
        setFeatureLoaded = (delegate* unmanaged<uint, byte, int>)NativeLibrary.GetExport(interposer, "slSetFeatureLoaded");
        getFeatureFunction = (delegate* unmanaged<uint, byte*, void**, int>)NativeLibrary.GetExport(interposer, "slGetFeatureFunction");
        getNewFrameToken = (delegate* unmanaged<nint*, uint*, int>)NativeLibrary.GetExport(interposer, "slGetNewFrameToken");
        SetConstants = (delegate* unmanaged<void*, nint, void*, int>)NativeLibrary.GetExport(interposer, "slSetConstants");
        SetTagForFrame = (delegate* unmanaged<nint, void*, void*, uint, nint, int>)NativeLibrary.GetExport(interposer, "slSetTagForFrame");
        EvaluateFeature = (delegate* unmanaged<uint, nint, void**, uint, nint, int>)NativeLibrary.GetExport(interposer, "slEvaluateFeature");
        FreeResources = (delegate* unmanaged<uint, void*, int>)NativeLibrary.GetExport(interposer, "slFreeResources");
        interposerGetDeviceProcAddr = (delegate* unmanaged<nint, byte*, nint>)NativeLibrary.GetExport(interposer, "vkGetDeviceProcAddr");
        interposerGetInstanceProcAddr = (delegate* unmanaged<nint, byte*, nint>)NativeLibrary.GetExport(interposer, "vkGetInstanceProcAddr");
    }

    static string[] Strings(nint array, uint count)
    {
        var result = new string[count];
        for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringAnsi(((nint*)array)[i]) ?? "";
        return result;
    }

    [UnmanagedCallersOnly]
    static void OnLog(int type, byte* message)
    {
        // 0 info, 1 warning, 2 error; info only with MEITOU_UPSCALER_LOG=1.
        if (type == 0 && Environment.GetEnvironmentVariable("MEITOU_UPSCALER_LOG") != "1") return;
        Console.WriteLine($"streamline {(type == 2 ? "error" : type == 1 ? "warning" : "info")}: {Marshal.PtrToStringAnsi((nint)message)?.TrimEnd()}");
    }

    static IEnumerable<string> Candidates()
    {
        if (VendorUpscalers.DlssPath is { } env) yield return env;
        yield return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Loads and initialises Streamline for DLSS, and with <paramref name="frameGeneration"/> for DLSS frame generation and Reflex too (when their DLLs are
    /// there; else without, with a log line); must run before the Vulkan instance is created. Null (with the reason) when it is not there or fails.
    /// </summary>
    public static Streamline? TryInit(bool frameGeneration, out string? reason)
    {
        foreach (var dir in Candidates())
        {
            var path = Path.Combine(dir, InterposerName);
            if (!File.Exists(path)) continue;
            if (!File.Exists(Path.Combine(dir, "sl.dlss.dll")) || !File.Exists(Path.Combine(dir, "nvngx_dlss.dll")))
            {
                reason = $"{dir} has {InterposerName} but not sl.dlss.dll and nvngx_dlss.dll beside it";
                return null;
            }
            if (frameGeneration && FrameGenerationFiles.Where(f => !File.Exists(Path.Combine(dir, f))).ToArray() is { Length: > 0 } missing)
            {
                Console.WriteLine($"frame generation  DLSS unavailable: {dir} lacks {string.Join(", ", missing)}");
                frameGeneration = false;
            }
            if (!NativeLibrary.TryLoad(path, out var lib)) { reason = $"{path} does not load"; return null; }
            return Init(lib, Path.GetFullPath(dir), frameGeneration, out reason);
        }
        reason = $"{InterposerName} not found (set MEITOU_DLSS_PATH or put the Streamline DLLs next to the executable)";
        return null;
    }

    static Streamline? Init(nint lib, string dir, bool frameGeneration, out string? reason)
    {
        var init = (delegate* unmanaged<Preferences*, ulong, int>)NativeLibrary.GetExport(lib, "slInit");
        var sl = new Streamline(lib, dir, frameGeneration)
        {
            pluginPath = Marshal.StringToHGlobalUni(dir),
            projectId = Marshal.StringToHGlobalAnsi(VendorUpscalers.DlssProjectId),
            engineVersion = Marshal.StringToHGlobalAnsi(VendorUpscalers.DlssEngineVersion),
        };
        uint[] wanted = frameGeneration ? [FeatureDlss, FeatureDlssG, FeatureReflex, FeaturePcl] : [FeatureDlss];
        sl.features = Marshal.AllocHGlobal(wanted.Length * sizeof(uint));
        for (int i = 0; i < wanted.Length; i++) ((uint*)sl.features)[i] = wanted[i];
        nint paths = sl.pluginPath;
        var prefs = new Preferences
        {
            StructType = PreferencesType, StructVersion = 1,
            LogLevel = 1,
            PathsToPlugins = (nint)(&paths), NumPathsToPlugins = 1,
            LogMessageCallback = (nint)(delegate* unmanaged<int, byte*, void>)&OnLog,
            // Client state tracking off, manual hooking, frame-based tagging (sl_core_types.h PreferenceFlags); no over-the-air updates.
            Flags = 1UL << 0 | 1UL << 2 | 1UL << 7,
            FeaturesToLoad = sl.features, NumFeaturesToLoad = (uint)wanted.Length,
            Engine = 0, EngineVersion = sl.engineVersion, ProjectId = sl.projectId,
            RenderApi = 2,
        };
        int rc = init(&prefs, SdkVersion);
        if (rc != 0) { sl.FreeStrings(); reason = $"slInit failed ({rc})"; return null; }
        foreach (uint feature in wanted)
            if (!sl.AddRequirements(feature, out reason)) { sl.Dispose(); return null; }
        reason = null;
        Console.WriteLine($"streamline {dir}: features [{string.Join(", ", wanted)}] need instance [{string.Join(", ", sl.InstanceExtensions)}], "
            + $"device [{string.Join(", ", sl.DeviceExtensions)}], 1.2 [{string.Join(", ", sl.Features12)}], 1.3 [{string.Join(", ", sl.Features13)}], "
            + $"queues: {sl.GraphicsQueues - sl.computeQueues} graphics, {sl.computeQueues} compute");
        return sl;
    }

    /// <summary>Adds what <paramref name="feature"/> needs (slGetFeatureRequirements) to the union over the loaded features.</summary>
    bool AddRequirements(uint feature, out string? reason)
    {
        var requirements = (delegate* unmanaged<uint, FeatureRequirements*, int>)NativeLibrary.GetExport(library, "slGetFeatureRequirements");
        var req = new FeatureRequirements { StructType = RequirementsType, StructVersion = 2 };
        int rc = requirements(feature, &req);
        if (rc != 0) { reason = $"slGetFeatureRequirements({feature}) failed ({rc})"; return false; }
        InstanceExtensions = [.. InstanceExtensions.Union(Strings(req.VkInstanceExtensions, req.VkNumInstanceExtensions))];
        DeviceExtensions = [.. DeviceExtensions.Union(Strings(req.VkDeviceExtensions, req.VkNumDeviceExtensions))];
        Features12 = [.. Features12.Union(Strings(req.VkFeatures12, req.VkNumFeatures12))];
        Features13 = [.. Features13.Union(Strings(req.VkFeatures13, req.VkNumFeatures13))];
        computeQueues += (int)req.VkNumComputeQueuesRequired;
        GraphicsQueues += (int)(req.VkNumGraphicsQueuesRequired + req.VkNumComputeQueuesRequired);
        reason = null;
        return true;
    }

    /// <summary>Adds what the loaded features need to the device options, and hands the device to Streamline once it exists.</summary>
    public void Apply(VulkanDeviceOptions options)
    {
        options.InstanceExtensions = [.. options.InstanceExtensions ?? [], .. InstanceExtensions];
        options.DeviceExtensions = [.. options.DeviceExtensions ?? [], .. DeviceExtensions];
        options.Features12 = [.. options.Features12 ?? [], .. Features12];
        options.ExtraGraphicsQueues += GraphicsQueues;
        var previous = options.DeviceCreated;
        options.DeviceCreated = d => { previous?.Invoke(d); Attach(d); };
    }

    /// <summary>The window DLSS frame generation presents to (Win32 handles), set before the device is made.</summary>
    public void UseWindow(nint hwnd, nint hinstance) => window = (hwnd, hinstance);

    /// <summary>
    /// The window's surface made again through the interposer's <c>vkCreateWin32SurfaceKHR</c>, which records the window behind the surface for DLSS frame
    /// generation (sl_hooks.h lists it). Only after <c>slSetVulkanInfo</c>: in manual hooking the hook calls through an instance table that call fills
    /// (Streamline 2.14.16 <c>sl.interposer/vulkan/wrapper.cpp</c>), so it would crash before. The device's surface is replaced; false when it cannot be made.
    /// </summary>
    bool ReplaceSurface(VulkanDevice device)
    {
        if (window.Hwnd == 0 || !device.HasSurface) return false;
        var surface = CreateWin32Surface(device.Instance, window.Hwnd, window.HInstance);
        if (surface.Handle == 0) return false;
        device.ReplaceSurface(surface);
        return true;
    }

    SurfaceKHR CreateWin32Surface(Instance instance, nint hwnd, nint hinstance)
    {
        nint fn;
        fixed (byte* name = "vkCreateWin32SurfaceKHR\0"u8) fn = interposerGetInstanceProcAddr(instance.Handle, name);
        if (fn == 0) return default;
        // VkWin32SurfaceCreateInfoKHR: sType 1000009000, next, flags, hinstance, hwnd.
        var info = stackalloc nint[5];
        info[0] = 1000009000;
        info[1] = 0;
        info[2] = 0;
        info[3] = hinstance;
        info[4] = hwnd;
        SurfaceKHR surface;
        var r = ((delegate* unmanaged<Instance, void*, AllocationCallbacks*, SurfaceKHR*, Result>)fn)(instance, info, null, &surface);
        return r == Result.Success ? surface : default;
    }

    void Attach(VulkanDevice device)
    {
        // Streamline's queues follow ours in the graphics family: graphics first, then compute (Apply asked for both there).
        uint graphics = (uint)(GraphicsQueues - computeQueues);
        var info = new VulkanInfo
        {
            StructType = VulkanInfoType, StructVersion = 3,
            Device = device.Device.Handle, Instance = device.Instance.Handle, PhysicalDevice = device.PhysicalDevice.Handle,
            GraphicsQueueFamily = device.GraphicsFamily, GraphicsQueueIndex = 1,
            ComputeQueueFamily = device.GraphicsFamily, ComputeQueueIndex = 1 + graphics,
        };
        if (GraphicsQueues > device.ExtraQueues.Length)
            Console.WriteLine($"streamline the graphics family has {device.ExtraQueues.Length} queues beyond ours, Streamline asked for {GraphicsQueues}");
        // Without DLSS Streamline is shut down at once, so no NVIDIA layer stays attached to another vendor's device (#55).
        int rc = setVulkanInfo(&info);
        if (rc != 0) { Console.WriteLine($"streamline slSetVulkanInfo failed ({rc})"); Dispose(); return; }
        rc = Supported(device, FeatureDlss);
        if (rc != 0) { Console.WriteLine($"streamline DLSS is not supported on {device.DeviceName} ({rc}); Streamline shut down"); Dispose(); return; }
        DlssSetOptions = (delegate* unmanaged<void*, void*, int>)Function(FeatureDlss, "slDLSSSetOptions");
        if (DlssSetOptions == null) { Console.WriteLine("streamline slDLSSSetOptions missing"); Dispose(); return; }
        fixed (byte* name = "vkQueuePresentKHR\0"u8) PresentProxy = (delegate* unmanaged<Queue, PresentInfoKHR*, Result>)interposerGetDeviceProcAddr(device.Device.Handle, name);
        DlssSupported = true;
        if (FrameGenerationLoaded) AttachFrameGeneration(device);
    }

    int Supported(VulkanDevice device, uint feature)
    {
        var adapter = new AdapterInfo { StructType = AdapterInfoType, StructVersion = 1, VkPhysicalDevice = device.PhysicalDevice.Handle };
        return isFeatureSupported(feature, &adapter);
    }

    void* Function(uint feature, string name)
    {
        void* fn = null;
        var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) return getFeatureFunction(feature, p, &fn) == 0 ? fn : null;
    }

    nint DeviceProc(VulkanDevice device, string name)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) return interposerGetDeviceProcAddr(device.Device.Handle, p);
    }

    /// <summary>DLSS frame generation and Reflex: supported here, their functions and the swapchain hooks; else unloaded (a loaded DLSS-G makes the swapchain render
    /// off-screen even while off, NVIDIA's guide section 18).</summary>
    void AttachFrameGeneration(VulkanDevice device)
    {
        int g = Supported(device, FeatureDlssG), reflex = Supported(device, FeatureReflex), pcl = Supported(device, FeaturePcl);
        DlssgSetOptions = (delegate* unmanaged<void*, void*, int>)Function(FeatureDlssG, "slDLSSGSetOptions");
        DlssgGetState = (delegate* unmanaged<void*, void*, void*, int>)Function(FeatureDlssG, "slDLSSGGetState");
        reflexSetOptions = (delegate* unmanaged<void*, int>)Function(FeatureReflex, "slReflexSetOptions");
        reflexSleep = (delegate* unmanaged<nint, int>)Function(FeatureReflex, "slReflexSleep");
        pclSetMarker = (delegate* unmanaged<uint, nint, int>)Function(FeaturePcl, "slPCLSetMarker");
        bool functions = DlssgSetOptions != null && DlssgGetState != null && reflexSetOptions != null && reflexSleep != null && pclSetMarker != null;
        if (g != 0 || reflex != 0 || pcl != 0 || !functions)
        {
            Console.WriteLine($"frame generation  DLSS unavailable on {device.DeviceName}: slIsFeatureSupported DLSS-G {g}, Reflex {reflex}, PCL {pcl}"
                + (functions ? "" : ", functions missing") + " (DLSS-G needs an RTX 40 or later and Windows' hardware-accelerated GPU scheduling)");
            setFeatureLoaded(FeatureDlssG, 0);
            return;
        }
        var hooks = new SwapchainHooks
        {
            Create = (delegate* unmanaged<Device, SwapchainCreateInfoKHR*, AllocationCallbacks*, SwapchainKHR*, Result>)DeviceProc(device, "vkCreateSwapchainKHR"),
            Destroy = (delegate* unmanaged<Device, SwapchainKHR, AllocationCallbacks*, void>)DeviceProc(device, "vkDestroySwapchainKHR"),
            GetImages = (delegate* unmanaged<Device, SwapchainKHR, uint*, Image*, Result>)DeviceProc(device, "vkGetSwapchainImagesKHR"),
            Acquire = (delegate* unmanaged<Device, SwapchainKHR, ulong, VkSemaphore, Fence, uint*, Result>)DeviceProc(device, "vkAcquireNextImageKHR"),
        };
        var waitIdle = (delegate* unmanaged<Device, Result>)DeviceProc(device, "vkDeviceWaitIdle");
        if (hooks.Create == null || hooks.Destroy == null || hooks.GetImages == null || hooks.Acquire == null || waitIdle == null)
        {
            Console.WriteLine("frame generation  DLSS unavailable: the interposer lacks a swapchain hook");
            setFeatureLoaded(FeatureDlssG, 0);
            return;
        }
        if (!ReplaceSurface(device)) Console.WriteLine("frame generation  the window's surface could not be made through Streamline; DLSS-G may not find the window");
        // Reflex must be on while DLSS-G runs (DLSSGStatus eFailReflexNotDetectedAtRuntime otherwise).
        var reflexOptions = new ReflexOptions { StructType = ReflexOptionsType, StructVersion = 1, Mode = 1 };
        int rc = reflexSetOptions(&reflexOptions);
        if (rc != 0) Console.WriteLine($"frame generation  slReflexSetOptions failed ({rc})");
        Swapchain = hooks;
        device.WaitIdleFunction = waitIdle;
        attached = device;
        FrameGenerationSupported = true;
        Console.WriteLine($"frame generation  DLSS supported on {device.DeviceName} (Reflex low latency on)");
    }

    /// <summary>
    /// Starts a rendered frame for DLSS frame generation (the game's loop, before the world advances): a new frame token, Reflex's sleep and the simulation
    /// marker. The token is the one every Streamline call of the frame uses until <see cref="EndFrame"/>. Does nothing without frame generation.
    /// </summary>
    public void BeginFrame()
    {
        if (!FrameGenerationSupported || disposed) return;
        openToken = NewToken();
        if (openToken == 0) return;
        reflexSleep(openToken);
        Marker(PclMarker.SimulationStart);
    }

    /// <summary>A PC Latency marker for the open frame (none open: nothing).</summary>
    public void Marker(PclMarker marker)
    {
        if (openToken != 0 && pclSetMarker != null && !disposed) pclSetMarker((uint)marker, openToken);
    }

    /// <summary>Ends the frame <see cref="BeginFrame"/> opened (after its present).</summary>
    public void EndFrame() => openToken = 0;

    /// <summary>The token of the open frame, else a new one (DLSS upscaling without frame generation: one per evaluation); 0 on failure.</summary>
    public nint FrameToken() => openToken != 0 ? openToken : NewToken();

    nint NewToken()
    {
        nint token;
        uint index = ++frameIndex;
        return getNewFrameToken(&token, &index) == 0 ? token : 0;
    }

    void FreeStrings()
    {
        Marshal.FreeHGlobal(pluginPath);
        Marshal.FreeHGlobal(projectId);
        Marshal.FreeHGlobal(engineVersion);
        Marshal.FreeHGlobal(features);
        pluginPath = projectId = engineVersion = features = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (attached is not null) attached.WaitIdleFunction = null;   // the proxy goes with Streamline
        shutdown();
        FreeStrings();
    }
}
