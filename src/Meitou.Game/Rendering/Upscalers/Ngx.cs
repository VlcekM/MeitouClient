using System.Runtime.InteropServices;
using System.Text;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.Game.Rendering.Upscalers;

/// <summary>
/// NVIDIA NGX, the runtime behind DLSS that Streamline wraps on Windows. On Linux it is the only DLSS path; on Windows (and Proton) it is used
/// instead of Streamline when frame generation is on, because Streamline's interposer must see every present and the FFX swapchain owns it. NGX's
/// documented Vulkan API comes from NVIDIA's NGX client library (the DLSS SDK's static <c>libnvsdk_ngx.a</c>, built into <c>libmeitou_ngx.so</c> by
/// tools/scripts/build-ngx-shim.sh), which talks to the NVIDIA driver's <c>libnvidia-ngx.so.1</c> (on Windows <c>meitou_ngx.dll</c>,
/// tools/scripts/build-ngx-shim-win.sh, finds the driver's NGX itself); the DLSS model (<c>libnvidia-ngx-dlss.so.*</c>, <c>nvngx_dlss.dll</c> on
/// Windows) and the client library are looked for in
/// <see cref="VendorUpscalers.DlssPath"/> and the executable's folder, neither is in the repository.
/// <see cref="TryInit"/> loads the client library before the Vulkan instance exists and reports the extensions DLSS needs (<see cref="Apply"/>
/// adds them to the device), the device is handed over once it exists, and <see cref="DlssSupported"/> says whether the model loaded.
/// Shut down (<see cref="Dispose"/>) before the device is destroyed. docs/render-post.md "Frame contract".
/// </summary>
public sealed unsafe class Ngx : IDisposable
{
    #region Library

    /// <summary>
    /// MEITOU_NGX_LOG=1 prints everything NGX says (its verbose level) (hundreds of lines at start-up); otherwise only its errors, minus the optional
    /// config files and the system model folder it looks for.
    /// </summary>
    static readonly bool Verbose = Environment.GetEnvironmentVariable("MEITOU_NGX_LOG") is "1";

    /// <summary>
    /// The NVIDIA driver's NGX library on Linux, which the client library talks to.
    /// </summary>
    const string DriverLibraryName = "libnvidia-ngx.so.1";

    /// <summary>
    /// The NGX API version the init is called with: <c>NVSDK_NGX_VERSION_API_MACRO</c> (nvsdk_ngx_defs.h).
    /// </summary>
    const uint ApiVersion = 0x15;

    /// <summary>
    /// <c>NVSDK_NGX_Result_Success</c>: what NGX's calls return when they succeed.
    /// </summary>
    internal const int ResultSuccess = 1;

    /// <summary>
    /// <c>NVSDK_NGX_Feature_SuperSampling</c>: the DLSS feature.
    /// </summary>
    internal const int FeatureSuperSampling = 1;

    /// <summary>
    /// The client library's file name: <c>meitou_ngx.dll</c> on Windows, <c>libmeitou_ngx.so</c> elsewhere.
    /// </summary>
    public static string LibraryName => OperatingSystem.IsWindows() ? "meitou_ngx.dll" : "libmeitou_ngx.so";

    /// <summary>
    /// The DLSS model's file name pattern: <c>nvngx_dlss.dll</c> on Windows, <c>libnvidia-ngx-dlss.so.*</c> elsewhere.
    /// </summary>
    static string ModelPattern => OperatingSystem.IsWindows() ? "nvngx_dlss.dll" : "libnvidia-ngx-dlss.so.*";

    /// <summary>
    /// <c>NVSDK_NGX_PathListInfo</c>: the folders NGX looks for feature models in.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct PathListInfo
    {
        /// <summary>
        /// The folders, wide strings.
        /// </summary>
        public nint Paths;

        /// <summary>
        /// How many folders.
        /// </summary>
        public uint Length;
    }

    /// <summary>
    /// <c>NVSDK_NGX_LoggingInfo</c>: where NGX's log goes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct LoggingInfo
    {
        /// <summary>
        /// The log callback (<see cref="OnLog"/>).
        /// </summary>
        public nint Callback;

        /// <summary>
        /// The least level logged (1 on, 2 verbose).
        /// </summary>
        public int MinimumLevel;

        /// <summary>
        /// Whether NGX's other log sinks are off (1: only the callback).
        /// </summary>
        public byte DisableOtherSinks;
    }

    /// <summary>
    /// <c>NVSDK_NGX_FeatureCommonInfo</c>: the init's paths and logging.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct FeatureCommonInfo
    {
        /// <summary>
        /// The folders the models are looked for in.
        /// </summary>
        public PathListInfo PathList;

        /// <summary>
        /// NGX's internal data (0).
        /// </summary>
        public nint Internal;

        /// <summary>
        /// The logging.
        /// </summary>
        public LoggingInfo Logging;
    }

    /// <summary>
    /// <c>NVSDK_NGX_VULKAN_Init_with_ProjectID</c>: called directly on Windows, through libffi elsewhere (<see cref="InitFromNativeCaller"/>).
    /// </summary>
    readonly nint _init;

    /// <summary>
    /// <c>NVSDK_NGX_VULKAN_Shutdown1</c>, for the device.
    /// </summary>
    readonly delegate* unmanaged<nint, int> _shutdown;

    /// <summary>
    /// <c>NVSDK_NGX_VULKAN_GetCapabilityParameters</c>.
    /// </summary>
    readonly delegate* unmanaged<void**, int> _getCapabilityParameters;

    /// <summary>
    /// <c>NVSDK_NGX_VULKAN_AllocateParameters</c>, for the upscaler's parameter maps.
    /// </summary>
    internal readonly delegate* unmanaged<void**, int> AllocateParameters;

    /// <summary>
    /// <c>NVSDK_NGX_VULKAN_DestroyParameters</c>.
    /// </summary>
    internal readonly delegate* unmanaged<void*, int> DestroyParameters;

    /// <summary>
    /// <c>NVSDK_NGX_VULKAN_CreateFeature1</c>, for the upscaler's DLSS feature.
    /// </summary>
    internal readonly delegate* unmanaged<nint, nint, int, void*, void**, int> CreateFeature;

    /// <summary>
    /// <c>NVSDK_NGX_VULKAN_ReleaseFeature</c>.
    /// </summary>
    internal readonly delegate* unmanaged<void*, int> ReleaseFeature;

    /// <summary>
    /// <c>NVSDK_NGX_VULKAN_EvaluateFeature_C</c>, for the upscaler's evaluations.
    /// </summary>
    internal readonly delegate* unmanaged<nint, void*, void*, nint, int> EvaluateFeature;

    /// <summary>
    /// The instance extensions DLSS needs (<see cref="Apply"/> adds them).
    /// </summary>
    public string[] InstanceExtensions { get; }

    /// <summary>
    /// The device extensions DLSS needs.
    /// </summary>
    public string[] DeviceExtensions { get; }
    /// <summary>
    /// After the device exists: DLSS runs on it (an NVIDIA GPU on NVIDIA's driver, and the DLSS model in place).
    /// </summary>
    public bool DlssSupported { get; private set; }
    /// <summary>
    /// Where the DLSS model is looked for besides the executable's folder (<see cref="VendorUpscalers.DlssPath"/>), or null.
    /// </summary>
    public string? ModelPath { get; }

    /// <summary>
    /// Binds the client library's exports and the parameter functions.
    /// </summary>
    Ngx(nint lib, string[] instance, string[] device, string? modelPath)
    {
        _init = NativeLibrary.GetExport(lib, "NVSDK_NGX_VULKAN_Init_with_ProjectID");
        _shutdown = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(lib, "NVSDK_NGX_VULKAN_Shutdown1");
        _getCapabilityParameters = (delegate* unmanaged<void**, int>)NativeLibrary.GetExport(lib, "NVSDK_NGX_VULKAN_GetCapabilityParameters");
        AllocateParameters = (delegate* unmanaged<void**, int>)NativeLibrary.GetExport(lib, "NVSDK_NGX_VULKAN_AllocateParameters");
        DestroyParameters = (delegate* unmanaged<void*, int>)NativeLibrary.GetExport(lib, "NVSDK_NGX_VULKAN_DestroyParameters");
        CreateFeature = (delegate* unmanaged<nint, nint, int, void*, void**, int>)NativeLibrary.GetExport(lib, "NVSDK_NGX_VULKAN_CreateFeature1");
        ReleaseFeature = (delegate* unmanaged<void*, int>)NativeLibrary.GetExport(lib, "NVSDK_NGX_VULKAN_ReleaseFeature");
        EvaluateFeature = (delegate* unmanaged<nint, void*, void*, nint, int>)NativeLibrary.GetExport(lib, "NVSDK_NGX_VULKAN_EvaluateFeature_C");
        NgxParameters.Load(lib);
        InstanceExtensions = instance;
        DeviceExtensions = device;
        ModelPath = modelPath;
    }

    /// <summary>
    /// Loads NGX's client library and asks it what the Vulkan instance and device need; null (and why) when DLSS cannot run.
    /// </summary>
    public static Ngx? TryInit(out string? reason)
    {
        // MEITOU_DLSS=0 keeps NGX out entirely.
        if (Environment.GetEnvironmentVariable("MEITOU_DLSS") == "0")
        {
            reason = "MEITOU_DLSS=0: NGX not loaded";
            return null;
        }

        // Only with the DLSS model in place (as Streamline only with nvngx_dlss.dll): without it DLSS cannot run, so NGX is not loaded.
        if (Find(ModelPattern) is null)
        {
            reason = $"{ModelPattern} not found (set MEITOU_DLSS_PATH or put it next to the executable); NGX not loaded";
            return null;
        }

        if (Find(LibraryName) is not { } client)
        {
            reason = $"{LibraryName} not found (tools/scripts/build-ngx-shim{ShimSuffix()}.sh builds it; next to the executable or in "
                + "MEITOU_DLSS_PATH); NGX not loaded";
            return null;
        }

        if (!OperatingSystem.IsWindows() && !NativeLibrary.TryLoad(DriverLibraryName, out _))
        {
            reason = $"{DriverLibraryName} does not load (NGX needs the NVIDIA driver)";
            return null;
        }

        return Load(client, out reason);
    }

    /// <summary>
    /// The build script's suffix for the platform (<c>-win</c> on Windows).
    /// </summary>
    static string ShimSuffix() => OperatingSystem.IsWindows() ? "-win" : "";

    /// <summary>
    /// Loads the client library at <paramref name="client"/>; null (and why) when it does not load or lacks an export.
    /// </summary>
    static Ngx? Load(string client, out string? reason)
    {
        nint lib;
        try
        {
            lib = NativeLibrary.Load(client);
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            reason = $"{client} does not load: {e.Message.ReplaceLineEndings(" ")}";
            return null;
        }

        try
        {
            return Create(client, lib, out reason);
        }
        catch (EntryPointNotFoundException e)
        {
            reason = $"{client}: {e.Message} (rebuild it with tools/scripts/build-ngx-shim{ShimSuffix()}.sh)";
            return null;
        }
    }

    /// <summary>
    /// Asks the client library for the extensions DLSS needs and makes the instance; null (and why) when the query fails.
    /// </summary>
    static Ngx? Create(string client, nint lib, out string? reason)
    {
        var required = (delegate* unmanaged<uint*, byte***, uint*, byte***, int>)NativeLibrary.GetExport(lib, "NVSDK_NGX_VULKAN_RequiredExtensions");
        uint ni, nd;
        byte** ie, de;
        int rc = required(&ni, &ie, &nd, &de);
        if (rc != ResultSuccess)
        {
            reason = $"NVSDK_NGX_VULKAN_RequiredExtensions failed ({rc:x})";
            return null;
        }

        var instance = Strings(ie, ni);
        var device = Strings(de, nd);
        var path = VendorUpscalers.DlssPath;
        reason = null;
        var ngx = new Ngx(lib, instance, device, path is null ? null : Path.GetFullPath(path));
        Console.WriteLine($"ngx       {client}: DLSS needs instance [{string.Join(", ", instance)}], device [{string.Join(", ", device)}]");
        return ngx;
    }

    /// <summary>
    /// The first file matching <paramref name="pattern"/> in <see cref="VendorUpscalers.DlssPath"/> or the executable's folder, or null.
    /// </summary>
    static string? Find(string pattern)
    {
        foreach (var folder in (string?[])[VendorUpscalers.DlssPath, AppContext.BaseDirectory])
        {
            try
            {
                if (folder is not null && Directory.Exists(folder) && Directory.EnumerateFiles(folder, pattern).FirstOrDefault() is { } file)
                {
                    return Path.GetFullPath(file);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// The <paramref name="count"/> UTF-8 strings of a native string array.
    /// </summary>
    static string[] Strings(byte** array, uint count)
    {
        var result = new string[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = Marshal.PtrToStringUTF8((nint)array[i]) ?? "";
        }

        return result;
    }

    /// <summary>
    /// Adds what DLSS needs to the device options, and hands the device to NGX once it exists.
    /// </summary>
    public void Apply(VulkanDeviceOptions options)
    {
        options.InstanceExtensions = [.. options.InstanceExtensions ?? [], .. InstanceExtensions];

        // The device enables buffer device address as the Vulkan 1.2 feature; NGX's list names it as VK_EXT_buffer_device_address, which may not
        // be enabled together with that feature.
        options.DeviceExtensions = [.. options.DeviceExtensions ?? [], .. DeviceExtensions.Where(e => e != "VK_EXT_buffer_device_address")];
        options.Features12 = [.. options.Features12 ?? [], "bufferDeviceAddress"];
        var previous = options.DeviceCreated;
        options.DeviceCreated = d =>
        {
            previous?.Invoke(d);
            Attach(d);
        };
    }

    #endregion

    #region Device

    /// <summary>
    /// The device NGX was initialised on (0: none), which <see cref="Dispose"/> shuts it down for.
    /// </summary>
    nint _device;

    /// <summary>
    /// NGX is loaded whenever the DLSS model is there, DLSS chosen or not, so nothing here may stop the game: a failure leaves DLSS off, and without
    /// DLSS NGX is shut down at once (as Streamline is).
    /// </summary>
    void Attach(VulkanDevice d)
    {
        try
        {
            DlssSupported = TryAttach(d);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.WriteLine($"ngx       {e.Message}");
        }

        if (!DlssSupported) Dispose();
    }

    /// <summary>
    /// Initialises NGX on the device and checks DLSS is available; false (logged) on NVK or another vendor's driver, a failed init or no DLSS.
    /// </summary>
    bool TryAttach(VulkanDevice d)
    {
        // Only NVIDIA's own driver: another vendor's device (a hybrid laptop's other GPU) or NVK, Mesa's driver for NVIDIA GPUs, which reports
        // NVIDIA's vendor id, is never handed to NGX.
        if (Driver(d) != Silk.NET.Vulkan.DriverId.NvidiaProprietary)
        {
            Console.WriteLine($"ngx       {d.DeviceName} is not on NVIDIA's driver; NGX shut down");
            return false;
        }

        var info = CreateInitInfo(out var project, out var version, out var dataPath);
        int? initialised = InitNgx(d, project, version, dataPath, &info);
        if (initialised is null) return false;
        if (initialised != ResultSuccess)
        {
            Console.WriteLine($"ngx       init failed ({initialised:x}) on {d.DeviceName}");
            return false;
        }

        _device = d.Device.Handle;
        return CheckCapabilities(d);
    }

    /// <summary>
    /// The init arguments: the model and engine folders, the logs folder, and the project and engine version strings (kept until shut down).
    /// </summary>
    FeatureCommonInfo CreateInitInfo(out byte* project, out byte* version, out byte* dataPath)
    {
        // NGX's data folder, per user (XDG_DATA_HOME or ~/.local/share); its log comes through OnLog, its own log file is off.
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "meitou", "ngx");
        Directory.CreateDirectory(logs);
        string[] paths = ModelPath is null ? [AppContext.BaseDirectory] : [ModelPath, AppContext.BaseDirectory];
        var pathPointers = (byte**)Keep((byte*)NativeMemory.Alloc((nuint)(paths.Length * sizeof(nint))));
        for (int i = 0; i < paths.Length; i++)
        {
            pathPointers[i] = Keep(WideString(paths[i]));
        }

        dataPath = Keep(WideString(logs));
        project = Keep(AsciiString(VendorUpscalers.DlssProjectId));   // engine type custom (0)
        version = Keep(AsciiString(VendorUpscalers.DlssEngineVersion));
        return new FeatureCommonInfo
        {
            PathList = new PathListInfo { Paths = (nint)pathPointers, Length = (uint)paths.Length },
            Logging = new LoggingInfo
            {
                Callback = (nint)(delegate* unmanaged<byte*, int, int, void>)&OnLog,
                MinimumLevel = Verbose ? 2 : 1,
                DisableOtherSinks = 1,
            },
        };
    }

    /// <summary>
    /// Calls the init with the device's handles; null when it could not be called (libffi missing).
    /// </summary>
    int? InitNgx(VulkanDevice d, byte* project, byte* version, byte* dataPath, FeatureCommonInfo* info)
    {
        if (OperatingSystem.IsWindows())
        {
            var init = (delegate* unmanaged<byte*, int, byte*, byte*, nint, nint, nint, nint, nint, FeatureCommonInfo*, uint, int>)_init;
            return init(project, 0, version, dataPath, d.Instance.Handle, d.PhysicalDevice.Handle, d.Device.Handle, 0, 0, info, ApiVersion);
        }

        return InitFromNativeCaller(_init, project, 0, version, dataPath, d.Instance.Handle, d.PhysicalDevice.Handle, d.Device.Handle, info,
            ApiVersion);
    }

    /// <summary>
    /// Whether NGX reports the super sampling feature available on this device (a newer driver or an RTX GPU otherwise).
    /// </summary>
    bool CheckCapabilities(VulkanDevice d)
    {
        void* caps;
        int rc = _getCapabilityParameters(&caps);
        if (rc != ResultSuccess)
        {
            Console.WriteLine($"ngx       NVSDK_NGX_VULKAN_GetCapabilityParameters failed ({rc:x})");
            return false;
        }

        int available = 0, outdated = 0, initResult = 0;
        NgxParameters.GetInt(caps, "SuperSampling.Available\0"u8, &available);
        NgxParameters.GetInt(caps, "SuperSampling.NeedsUpdatedDriver\0"u8, &outdated);
        NgxParameters.GetInt(caps, "SuperSampling.FeatureInitResult\0"u8, &initResult);
        DestroyParameters(caps);
        if (available != 0) return true;

        Console.WriteLine(outdated != 0
            ? $"ngx       DLSS needs a newer driver on {d.DeviceName}; NGX shut down"
            : $"ngx       DLSS is not available on {d.DeviceName} (it needs an RTX GPU; NGX's result {initResult:x}); NGX shut down");
        return false;
    }

    /// <summary>
    /// NVSDK_NGX_VULKAN_Init_with_ProjectID through libffi, its arguments as nvsdk_ngx_vk.h declares them (the Vulkan proc-address getters null:
    /// NGX resolves them itself). Observed: called straight from managed code the init aborts (<c>std::logic_error</c>, "basic_string::_M_construct
    /// null not valid"), and the same call through libffi succeeds; why is Unknown (libffi passes the arguments as a direct call does).
    /// Null when libffi is missing (the call is not made).
    /// </summary>
    static int? InitFromNativeCaller(nint function, byte* project, int engine, byte* version, byte* dataPath, nint instance, nint physicalDevice,
        nint device, FeatureCommonInfo* info, uint apiVersion)
    {
        // libffi 3.4 is .so.8, 3.3 is .so.7, with the same calls.
        if (!NativeLibrary.TryLoad("libffi.so.8", out var ffi) && !NativeLibrary.TryLoad("libffi.so.7", out ffi))
        {
            Console.WriteLine("ngx       libffi (libffi.so.8 or .so.7) does not load: it is needed to call NGX's init");
            return null;
        }

        var prepare = (delegate* unmanaged<void*, int, uint, void*, void**, int>)NativeLibrary.GetExport(ffi, "ffi_prep_cif");
        var call = (delegate* unmanaged<void*, nint, void*, void**, void>)NativeLibrary.GetExport(ffi, "ffi_call");
        void* pointer = (void*)NativeLibrary.GetExport(ffi, "ffi_type_pointer");
        void* int32 = (void*)NativeLibrary.GetExport(ffi, "ffi_type_sint32");
        void* uint32 = (void*)NativeLibrary.GetExport(ffi, "ffi_type_uint32");
        nint gipa = 0, gdpa = 0;
        var types = stackalloc void*[11] { pointer, int32, pointer, pointer, pointer, pointer, pointer, pointer, pointer, pointer, uint32 };
        var values = stackalloc void*[11]
        {
            &project, &engine, &version, &dataPath, &instance, &physicalDevice, &device, &gipa, &gdpa, &info, &apiVersion,
        };
        var cif = stackalloc byte[64];   // ffi_cif: abi, nargs, arg_types, rtype, bytes, flags (32 bytes on x86-64)
        const int ffiUnix64 = 2;
        int status = prepare(cif, ffiUnix64, 11, int32, types);
        if (status != 0)
        {
            Console.WriteLine($"ngx       ffi_prep_cif failed ({status})");
            return null;
        }

        ulong result = 0;   // integer returns need sizeof(ffi_arg)
        call(cif, function, &result, values);
        return (int)result;
    }

    /// <summary>
    /// The device's Vulkan driver id.
    /// </summary>
    static Silk.NET.Vulkan.DriverId Driver(VulkanDevice d)
    {
        var driver = new Silk.NET.Vulkan.PhysicalDeviceDriverProperties { SType = Silk.NET.Vulkan.StructureType.PhysicalDeviceDriverProperties };
        var properties = new Silk.NET.Vulkan.PhysicalDeviceProperties2
        {
            SType = Silk.NET.Vulkan.StructureType.PhysicalDeviceProperties2,
            PNext = &driver,
        };
        d.Vk.GetPhysicalDeviceProperties2(d.PhysicalDevice, &properties);
        return driver.DriverID;
    }

    #endregion

    #region Native strings and log

    /// <summary>
    /// The strings handed to NGX at init (paths, project id, version), kept until it is shut down.
    /// </summary>
    readonly List<nint> _initStrings = [];

    /// <summary>
    /// Keeps <paramref name="p"/> to be freed at shut down, and returns it.
    /// </summary>
    byte* Keep(byte* p)
    {
        _initStrings.Add((nint)p);
        return p;
    }

    /// <summary>
    /// A null-terminated ASCII string, freed with <see cref="NativeMemory.Free"/>.
    /// </summary>
    static byte* AsciiString(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s + "\0");
        var p = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.AsSpan().CopyTo(new Span<byte>(p, bytes.Length));
        return p;
    }

    /// <summary>
    /// A null-terminated <c>wchar_t</c> string (UTF-16 on Windows, UTF-32 on Linux), freed with <see cref="NativeMemory.Free"/>.
    /// </summary>
    static byte* WideString(string s)
    {
        var bytes = (OperatingSystem.IsWindows() ? Encoding.Unicode : Encoding.UTF32).GetBytes(s + "\0");
        var p = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.AsSpan().CopyTo(new Span<byte>(p, bytes.Length));
        return p;
    }

    /// <summary>
    /// NGX's log callback: everything with <c>MEITOU_NGX_LOG=1</c>, else only the wanted errors.
    /// </summary>
    [UnmanagedCallersOnly]
    static void OnLog(byte* message, int level, int feature)
    {
        var text = Marshal.PtrToStringUTF8((nint)message)?.TrimEnd();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (Verbose || IsWantedError(text))
        {
            Console.WriteLine($"ngx       {text}");
        }
    }

    /// <summary>
    /// Whether a log line is an error worth showing (not the missing optional config files or the system model folder).
    /// </summary>
    static bool IsWantedError(string text) =>
        text.Contains("error", StringComparison.OrdinalIgnoreCase) && !text.Contains("configuration file") && !text.Contains("model folder");

    #endregion

    #region Disposal

    /// <summary>
    /// Whether NGX was shut down.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// Shuts NGX down for the device (once) and frees the init's strings.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device != 0) _shutdown(_device);
        foreach (var p in _initStrings)
        {
            NativeMemory.Free((void*)p);
        }

        _initStrings.Clear();
    }

    #endregion
}

/// <summary>
/// NGX's parameter map (<c>NVSDK_NGX_Parameter</c>) through the client library's C functions of nvsdk_ngx_params.h. Names are the
/// <c>NVSDK_NGX_Parameter_*</c> strings of nvsdk_ngx_defs.h, null-terminated.
/// </summary>
static unsafe class NgxParameters
{
    /// <summary>
    /// <c>NVSDK_NGX_Parameter_SetF</c>.
    /// </summary>
    static delegate* unmanaged<void*, byte*, float, void> s_setF;

    /// <summary>
    /// <c>NVSDK_NGX_Parameter_SetUI</c>.
    /// </summary>
    static delegate* unmanaged<void*, byte*, uint, void> s_setUi;

    /// <summary>
    /// <c>NVSDK_NGX_Parameter_SetI</c>.
    /// </summary>
    static delegate* unmanaged<void*, byte*, int, void> s_setI;

    /// <summary>
    /// <c>NVSDK_NGX_Parameter_SetVoidPointer</c>.
    /// </summary>
    static delegate* unmanaged<void*, byte*, void*, void> s_setVoidPointer;

    /// <summary>
    /// <c>NVSDK_NGX_Parameter_GetI</c>.
    /// </summary>
    static delegate* unmanaged<void*, byte*, int*, int> s_getI;

    /// <summary>
    /// Binds the parameter functions of the client library <paramref name="lib"/>.
    /// </summary>
    public static void Load(nint lib)
    {
        s_setF = (delegate* unmanaged<void*, byte*, float, void>)NativeLibrary.GetExport(lib, "NVSDK_NGX_Parameter_SetF");
        s_setUi = (delegate* unmanaged<void*, byte*, uint, void>)NativeLibrary.GetExport(lib, "NVSDK_NGX_Parameter_SetUI");
        s_setI = (delegate* unmanaged<void*, byte*, int, void>)NativeLibrary.GetExport(lib, "NVSDK_NGX_Parameter_SetI");
        s_setVoidPointer = (delegate* unmanaged<void*, byte*, void*, void>)NativeLibrary.GetExport(lib, "NVSDK_NGX_Parameter_SetVoidPointer");
        s_getI = (delegate* unmanaged<void*, byte*, int*, int>)NativeLibrary.GetExport(lib, "NVSDK_NGX_Parameter_GetI");
    }

    /// <summary>
    /// Sets the float parameter <paramref name="name"/> (null-terminated) of map <paramref name="p"/>.
    /// </summary>
    public static void SetFloat(void* p, ReadOnlySpan<byte> name, float value)
    {
        fixed (byte* n = name) s_setF(p, n, value);
    }

    /// <summary>
    /// Sets the unsigned integer parameter <paramref name="name"/> (null-terminated) of map <paramref name="p"/>.
    /// </summary>
    public static void SetUInt(void* p, ReadOnlySpan<byte> name, uint value)
    {
        fixed (byte* n = name) s_setUi(p, n, value);
    }

    /// <summary>
    /// Sets the integer parameter <paramref name="name"/> (null-terminated) of map <paramref name="p"/>.
    /// </summary>
    public static void SetInt(void* p, ReadOnlySpan<byte> name, int value)
    {
        fixed (byte* n = name) s_setI(p, n, value);
    }

    /// <summary>
    /// Sets the pointer parameter <paramref name="name"/> (null-terminated) of map <paramref name="p"/>.
    /// </summary>
    public static void SetPointer(void* p, ReadOnlySpan<byte> name, void* value)
    {
        fixed (byte* n = name) s_setVoidPointer(p, n, value);
    }

    /// <summary>
    /// Reads the integer parameter <paramref name="name"/> (null-terminated) of map <paramref name="p"/> into <paramref name="value"/>; NGX's result.
    /// </summary>
    public static int GetInt(void* p, ReadOnlySpan<byte> name, int* value)
    {
        fixed (byte* n = name) return s_getI(p, n, value);
    }
}