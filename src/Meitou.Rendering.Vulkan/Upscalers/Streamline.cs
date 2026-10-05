using System.Runtime.InteropServices;
using System.Text;
using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Vulkan.Upscalers;

/// <summary>
/// NVIDIA Streamline (MIT; the signed <c>sl.interposer.dll</c>, <c>sl.common.dll</c>, <c>sl.dlss.dll</c> and NVIDIA's <c>nvngx_dlss.dll</c>,
/// none of them in the repository), loaded at run time from <c>MEITOU_STREAMLINE_PATH</c> or next to the executable, in Streamline's
/// manual-hooking mode: <see cref="TryInit"/> runs <c>slInit</c> before the Vulkan instance exists and reports the extensions and
/// features DLSS needs (<see cref="Apply"/> adds them to the device), the device is handed over with <c>slSetVulkanInfo</c>, and
/// presents go through the interposer's <c>vkQueuePresentKHR</c> (<see cref="PresentProxy"/>), which Streamline needs every frame.
/// Shut down (<see cref="Dispose"/>) before the device is destroyed. docs/engine.md "Upscaling".
/// </summary>
public sealed unsafe class Streamline : IDisposable
{
    public const string InterposerName = "sl.interposer.dll";
    const ulong SdkVersion = (2UL << 48) | (14UL << 32) | (1UL << 16) | 0xfedc;   // Streamline 2.14.1 (sl_version.h)
    const uint FeatureDlss = 0;
    // Struct identities (sl_struct.h: a GUID and a version on every structure).
    internal static readonly Guid PreferencesType = new("1ca10965-bf8e-432b-8da1-6716d879fb14"), RequirementsType = new("66714097-ac6d-4bc6-8915-1e0f55a6b61f"),
        AdapterInfoType = new("0677315f-a746-4492-9f42-cb6142c9c3d4"), VulkanInfoType = new("0eed6fd5-82cd-43a9-bdb5-47a5ba2f45d6"),
        ViewportType = new("171b6435-9b3c-4fc8-9994-fbe52569aaa4"), ResourceType = new("3a9d70cf-2418-4b72-8391-13f8721c7261"),
        ResourceTagType = new("4c6a5aad-b445-496c-87ff-1af3845be653"), ConstantsType = new("dcd35ad7-4e4a-4bad-a90c-e0c49eb23afe"),
        DlssOptionsType = new("6ac826e4-4c61-4101-a92d-638d421057b8"), DlssOptimalType = new("ef1d0957-fd58-4df7-b504-8b69d8aa6b76");

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

    readonly delegate* unmanaged<int> shutdown;
    readonly delegate* unmanaged<VulkanInfo*, int> setVulkanInfo;
    readonly delegate* unmanaged<uint, AdapterInfo*, int> isFeatureSupported;
    readonly delegate* unmanaged<uint, byte*, void**, int> getFeatureFunction;
    internal readonly delegate* unmanaged<nint*, uint*, int> GetNewFrameToken;
    internal readonly delegate* unmanaged<void*, nint, void*, int> SetConstants;
    internal readonly delegate* unmanaged<nint, void*, void*, uint, nint, int> SetTagForFrame;
    internal readonly delegate* unmanaged<uint, nint, void**, uint, nint, int> EvaluateFeature;
    internal readonly delegate* unmanaged<uint, void*, int> FreeResources;
    internal delegate* unmanaged<void*, void*, int> DlssGetOptimalSettings;
    internal delegate* unmanaged<void*, void*, int> DlssSetOptions;
    readonly delegate* unmanaged<nint, byte*, nint> interposerGetDeviceProcAddr;
    nint pluginPath, projectId, engineVersion;
    bool disposed;

    public string[] InstanceExtensions { get; } = [];
    public string[] DeviceExtensions { get; } = [];
    public string[] Features12 { get; } = [];
    /// <summary>After <see cref="Attach"/>: DLSS runs on this device (an NVIDIA GPU with a recent driver and <c>nvngx_dlss.dll</c> in place).</summary>
    public bool DlssSupported { get; private set; }
    public string Directory { get; }
    /// <summary>The interposer's <c>vkQueuePresentKHR</c> (after <see cref="Attach"/>): presents must go through it in manual-hooking mode.</summary>
    public delegate* unmanaged<Queue, PresentInfoKHR*, Result> PresentProxy { get; private set; }

    Streamline(nint interposer, string directory, FeatureRequirements req)
    {
        Directory = directory;
        shutdown = (delegate* unmanaged<int>)NativeLibrary.GetExport(interposer, "slShutdown");
        setVulkanInfo = (delegate* unmanaged<VulkanInfo*, int>)NativeLibrary.GetExport(interposer, "slSetVulkanInfo");
        isFeatureSupported = (delegate* unmanaged<uint, AdapterInfo*, int>)NativeLibrary.GetExport(interposer, "slIsFeatureSupported");
        getFeatureFunction = (delegate* unmanaged<uint, byte*, void**, int>)NativeLibrary.GetExport(interposer, "slGetFeatureFunction");
        GetNewFrameToken = (delegate* unmanaged<nint*, uint*, int>)NativeLibrary.GetExport(interposer, "slGetNewFrameToken");
        SetConstants = (delegate* unmanaged<void*, nint, void*, int>)NativeLibrary.GetExport(interposer, "slSetConstants");
        SetTagForFrame = (delegate* unmanaged<nint, void*, void*, uint, nint, int>)NativeLibrary.GetExport(interposer, "slSetTagForFrame");
        EvaluateFeature = (delegate* unmanaged<uint, nint, void**, uint, nint, int>)NativeLibrary.GetExport(interposer, "slEvaluateFeature");
        FreeResources = (delegate* unmanaged<uint, void*, int>)NativeLibrary.GetExport(interposer, "slFreeResources");
        interposerGetDeviceProcAddr = (delegate* unmanaged<nint, byte*, nint>)NativeLibrary.GetExport(interposer, "vkGetDeviceProcAddr");
        InstanceExtensions = Strings(req.VkInstanceExtensions, req.VkNumInstanceExtensions);
        DeviceExtensions = Strings(req.VkDeviceExtensions, req.VkNumDeviceExtensions);
        Features12 = Strings(req.VkFeatures12, req.VkNumFeatures12);
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
        // 0 info, 1 warning, 2 error; info only with MEITOU_STREAMLINE_LOG=1.
        if (type == 0 && Environment.GetEnvironmentVariable("MEITOU_STREAMLINE_LOG") != "1") return;
        Console.WriteLine($"streamline {(type == 2 ? "error" : type == 1 ? "warning" : "info")}: {Marshal.PtrToStringAnsi((nint)message)?.TrimEnd()}");
    }

    static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable("MEITOU_STREAMLINE_PATH") is { Length: > 0 } env) yield return env;
        yield return AppContext.BaseDirectory;
    }

    /// <summary>Loads and initialises Streamline for DLSS; must run before the Vulkan instance is created. Null (with the reason) when it is not there or fails.</summary>
    public static Streamline? TryInit(out string? reason)
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
            if (!NativeLibrary.TryLoad(path, out var lib)) { reason = $"{path} does not load"; return null; }
            var init = (delegate* unmanaged<Preferences*, ulong, int>)NativeLibrary.GetExport(lib, "slInit");
            var requirements = (delegate* unmanaged<uint, FeatureRequirements*, int>)NativeLibrary.GetExport(lib, "slGetFeatureRequirements");
            var full = Path.GetFullPath(dir);
            nint plugin = Marshal.StringToHGlobalUni(full);
            nint project = Marshal.StringToHGlobalAnsi("6e8a8f8c-6f1b-4b8e-9a51-6b2d5c9b4e21");   // Meitou's own project id (any GUID)
            nint version = Marshal.StringToHGlobalAnsi("0.3");
            uint feature = FeatureDlss;
            nint paths = plugin;
            var prefs = new Preferences
            {
                StructType = PreferencesType, StructVersion = 1,
                LogLevel = 1,
                PathsToPlugins = (nint)(&paths), NumPathsToPlugins = 1,
                LogMessageCallback = (nint)(delegate* unmanaged<int, byte*, void>)&OnLog,
                // Client state tracking off, manual hooking, frame-based tagging; no over-the-air updates.
                Flags = 1UL << 0 | 1UL << 2 | 1UL << 7,
                FeaturesToLoad = (nint)(&feature), NumFeaturesToLoad = 1,
                Engine = 0, EngineVersion = version, ProjectId = project,
                RenderApi = 2,
            };
            int rc = init(&prefs, SdkVersion);
            if (rc != 0) { reason = $"slInit failed ({rc})"; return null; }
            var req = new FeatureRequirements { StructType = RequirementsType, StructVersion = 2 };
            rc = requirements(FeatureDlss, &req);
            if (rc != 0) { reason = $"slGetFeatureRequirements failed ({rc})"; return null; }
            reason = null;
            var sl = new Streamline(lib, full, req) { pluginPath = plugin, projectId = project, engineVersion = version };
            Console.WriteLine($"streamline {full}: DLSS needs instance [{string.Join(", ", sl.InstanceExtensions)}], device [{string.Join(", ", sl.DeviceExtensions)}], 1.2 [{string.Join(", ", sl.Features12)}]");
            return sl;
        }
        reason = $"{InterposerName} not found (set MEITOU_STREAMLINE_PATH or put the Streamline DLLs next to the executable)";
        return null;
    }

    /// <summary>Adds what DLSS needs to the device options, and hands the device to Streamline once it exists.</summary>
    public void Apply(VulkanDeviceOptions options)
    {
        options.InstanceExtensions = [.. options.InstanceExtensions ?? [], .. InstanceExtensions];
        options.DeviceExtensions = [.. options.DeviceExtensions ?? [], .. DeviceExtensions];
        options.Features12 = [.. options.Features12 ?? [], .. Features12];
        var previous = options.DeviceCreated;
        options.DeviceCreated = d => { previous?.Invoke(d); Attach(d); };
    }

    void Attach(VulkanDevice device)
    {
        var info = new VulkanInfo
        {
            StructType = VulkanInfoType, StructVersion = 3,
            Device = device.Device.Handle, Instance = device.Instance.Handle, PhysicalDevice = device.PhysicalDevice.Handle,
            ComputeQueueFamily = device.GraphicsFamily, GraphicsQueueFamily = device.GraphicsFamily,
        };
        int rc = setVulkanInfo(&info);
        if (rc != 0) { Console.WriteLine($"streamline slSetVulkanInfo failed ({rc})"); return; }
        var adapter = new AdapterInfo { StructType = AdapterInfoType, StructVersion = 1, VkPhysicalDevice = device.PhysicalDevice.Handle };
        rc = isFeatureSupported(FeatureDlss, &adapter);
        if (rc != 0) { Console.WriteLine($"streamline DLSS is not supported on {device.DeviceName} ({rc})"); return; }
        void* fn = null;
        fixed (byte* name = "slDLSSGetOptimalSettings\0"u8) rc = getFeatureFunction(FeatureDlss, name, &fn);
        if (rc != 0 || fn == null) { Console.WriteLine($"streamline slDLSSGetOptimalSettings missing ({rc})"); return; }
        DlssGetOptimalSettings = (delegate* unmanaged<void*, void*, int>)fn;
        fixed (byte* name = "slDLSSSetOptions\0"u8) rc = getFeatureFunction(FeatureDlss, name, &fn);
        if (rc != 0 || fn == null) { Console.WriteLine($"streamline slDLSSSetOptions missing ({rc})"); return; }
        DlssSetOptions = (delegate* unmanaged<void*, void*, int>)fn;
        fixed (byte* name = "vkQueuePresentKHR\0"u8) PresentProxy = (delegate* unmanaged<Queue, PresentInfoKHR*, Result>)interposerGetDeviceProcAddr(device.Device.Handle, name);
        DlssSupported = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        shutdown();
        Marshal.FreeHGlobal(pluginPath);
        Marshal.FreeHGlobal(projectId);
        Marshal.FreeHGlobal(engineVersion);
    }
}
