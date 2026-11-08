using System.Runtime.InteropServices;
using System.Text;
using Meitou.Rendering.Gpu.Core;

namespace Meitou.Rendering.Upscalers;

/// <summary>
/// NVIDIA NGX, the runtime behind DLSS that Streamline wraps on Windows. On Linux it is the only DLSS path; on Windows (and Proton) it is used instead
/// of Streamline when frame generation is on, because Streamline's interposer must see every present and the FFX swapchain owns it. NGX's documented Vulkan API comes from NVIDIA's NGX client
/// library (the DLSS SDK's static <c>libnvsdk_ngx.a</c>, built into <c>libmeitou_ngx.so</c> by tools/scripts/build-ngx-shim.sh), which talks
/// to the NVIDIA driver's <c>libnvidia-ngx.so.1</c> (on Windows <c>meitou_ngx.dll</c>, tools/scripts/build-ngx-shim-win.sh, finds the driver's NGX itself);
/// the DLSS model (<c>libnvidia-ngx-dlss.so.*</c>, <c>nvngx_dlss.dll</c> on Windows) and the client library are looked for in
/// <see cref="VendorUpscalers.DlssPath"/> and the executable's folder, neither is in the repository.
/// <see cref="TryInit"/> loads the client library before the Vulkan instance exists and reports the extensions DLSS needs (<see cref="Apply"/>
/// adds them to the device), the device is handed over once it exists, and <see cref="DlssSupported"/> says whether the model loaded.
/// Shut down (<see cref="Dispose"/>) before the device is destroyed. docs/render-post.md "Frame contract".
/// </summary>
public sealed unsafe class Ngx : IDisposable
{
    public static string LibraryName => OperatingSystem.IsWindows() ? "meitou_ngx.dll" : "libmeitou_ngx.so";
    static string ModelPattern => OperatingSystem.IsWindows() ? "nvngx_dlss.dll" : "libnvidia-ngx-dlss.so.*";
    const string DriverLibraryName = "libnvidia-ngx.so.1";
    const uint ApiVersion = 0x15;   // NVSDK_NGX_VERSION_API_MACRO (nvsdk_ngx_defs.h)
    internal const int ResultSuccess = 1;
    internal const int FeatureSuperSampling = 1;

    [StructLayout(LayoutKind.Sequential)] struct PathListInfo { public nint Paths; public uint Length; }
    [StructLayout(LayoutKind.Sequential)] struct LoggingInfo { public nint Callback; public int MinimumLevel; public byte DisableOtherSinks; }
    [StructLayout(LayoutKind.Sequential)] struct FeatureCommonInfo { public PathListInfo PathList; public nint Internal; public LoggingInfo Logging; }

    // NVSDK_NGX_VULKAN_Init_with_ProjectID, called through libffi (InitFromNativeCaller).
    readonly nint init;
    readonly delegate* unmanaged<nint, int> shutdown;
    /// <summary>The strings handed to NGX at init (paths, project id, version), kept until it is shut down.</summary>
    readonly List<nint> initStrings = [];
    readonly delegate* unmanaged<void**, int> getCapabilityParameters;
    internal readonly delegate* unmanaged<void**, int> AllocateParameters;
    internal readonly delegate* unmanaged<void*, int> DestroyParameters;
    internal readonly delegate* unmanaged<nint, nint, int, void*, void**, int> CreateFeature;
    internal readonly delegate* unmanaged<void*, int> ReleaseFeature;
    internal readonly delegate* unmanaged<nint, void*, void*, nint, int> EvaluateFeature;
    nint device;
    bool disposed;

    public string[] InstanceExtensions { get; }
    public string[] DeviceExtensions { get; }
    /// <summary>After the device exists: DLSS runs on it (an NVIDIA GPU on NVIDIA's driver, and the DLSS model in place).</summary>
    public bool DlssSupported { get; private set; }
    /// <summary>Where the DLSS model is looked for besides the executable's folder (<see cref="VendorUpscalers.DlssPath"/>), or null.</summary>
    public string? ModelPath { get; }

    Ngx(nint lib, string[] instance, string[] device, string? modelPath)
    {
        init = NativeLibrary.GetExport(lib, "NVSDK_NGX_VULKAN_Init_with_ProjectID");
        shutdown = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(lib, "NVSDK_NGX_VULKAN_Shutdown1");
        getCapabilityParameters = (delegate* unmanaged<void**, int>)NativeLibrary.GetExport(lib, "NVSDK_NGX_VULKAN_GetCapabilityParameters");
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

    /// <summary>Loads NGX's client library and asks it what the Vulkan instance and device need; null (and why) when DLSS cannot run.</summary>
    public static Ngx? TryInit(out string? reason)
    {
        // MEITOU_DLSS=0 keeps NGX out entirely.
        if (Environment.GetEnvironmentVariable("MEITOU_DLSS") == "0") { reason = "MEITOU_DLSS=0: NGX not loaded"; return null; }
        // Only with the DLSS model in place (as Streamline only with nvngx_dlss.dll): without it DLSS cannot run, so NGX is not loaded.
        if (Find(ModelPattern) is null) { reason = $"{ModelPattern} not found (set MEITOU_DLSS_PATH or put it next to the executable); NGX not loaded"; return null; }
        if (Find(LibraryName) is not { } client) { reason = $"{LibraryName} not found (tools/scripts/build-ngx-shim{(OperatingSystem.IsWindows() ? "-win" : "")}.sh builds it; next to the executable or in MEITOU_DLSS_PATH); NGX not loaded"; return null; }
        if (!OperatingSystem.IsWindows() && !NativeLibrary.TryLoad(DriverLibraryName, out _)) { reason = $"{DriverLibraryName} does not load (NGX needs the NVIDIA driver)"; return null; }
        nint lib;
        try { lib = NativeLibrary.Load(client); }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException) { reason = $"{client} does not load: {e.Message.ReplaceLineEndings(" ")}"; return null; }
        try
        {
            var required = (delegate* unmanaged<uint*, byte***, uint*, byte***, int>)NativeLibrary.GetExport(lib, "NVSDK_NGX_VULKAN_RequiredExtensions");
            uint ni, nd; byte** ie, de;
            int rc = required(&ni, &ie, &nd, &de);
            if (rc != ResultSuccess) { reason = $"NVSDK_NGX_VULKAN_RequiredExtensions failed ({rc:x})"; return null; }
            var instance = Strings(ie, ni);
            var device = Strings(de, nd);
            var path = VendorUpscalers.DlssPath;
            reason = null;
            var ngx = new Ngx(lib, instance, device, path is null ? null : Path.GetFullPath(path));
            Console.WriteLine($"ngx       {client}: DLSS needs instance [{string.Join(", ", instance)}], device [{string.Join(", ", device)}]");
            return ngx;
        }
        catch (EntryPointNotFoundException e) { reason = $"{client}: {e.Message} (rebuild it with tools/scripts/build-ngx-shim{(OperatingSystem.IsWindows() ? "-win" : "")}.sh)"; return null; }
    }

    /// <summary>The first file matching <paramref name="pattern"/> in <see cref="VendorUpscalers.DlssPath"/> or the executable's folder, or null.</summary>
    static string? Find(string pattern)
    {
        foreach (var folder in (string?[])[VendorUpscalers.DlssPath, AppContext.BaseDirectory])
        {
            try { if (folder is not null && Directory.Exists(folder) && Directory.EnumerateFiles(folder, pattern).FirstOrDefault() is { } file) return Path.GetFullPath(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    static string[] Strings(byte** array, uint count)
    {
        var result = new string[count];
        for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUTF8((nint)array[i]) ?? "";
        return result;
    }

    /// <summary>Adds what DLSS needs to the device options, and hands the device to NGX once it exists.</summary>
    public void Apply(VulkanDeviceOptions options)
    {
        options.InstanceExtensions = [.. options.InstanceExtensions ?? [], .. InstanceExtensions];
        // The device enables buffer device address as the Vulkan 1.2 feature; NGX's list names it as VK_EXT_buffer_device_address, which may not
        // be enabled together with that feature.
        options.DeviceExtensions = [.. options.DeviceExtensions ?? [], .. DeviceExtensions.Where(e => e != "VK_EXT_buffer_device_address")];
        options.Features12 = [.. options.Features12 ?? [], "bufferDeviceAddress"];
        var previous = options.DeviceCreated;
        options.DeviceCreated = d => { previous?.Invoke(d); Attach(d); };
    }

    /// <summary>
    /// NGX is loaded whenever the DLSS model is there, DLSS chosen or not, so nothing here may stop the game: a failure leaves DLSS off, and without DLSS
    /// NGX is shut down at once (as Streamline is).
    /// </summary>
    void Attach(VulkanDevice d)
    {
        try { DlssSupported = TryAttach(d); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.WriteLine($"ngx       {e.Message}");
        }
        if (!DlssSupported) Dispose();
    }

    bool TryAttach(VulkanDevice d)
    {
        // Only NVIDIA's own driver: another vendor's device (a hybrid laptop's other GPU) or NVK, Mesa's driver for NVIDIA GPUs, which reports
        // NVIDIA's vendor id, is never handed to NGX.
        if (Driver(d) != Silk.NET.Vulkan.DriverId.NvidiaProprietary) { Console.WriteLine($"ngx       {d.DeviceName} is not on NVIDIA's driver; NGX shut down"); return false; }
        // NGX's data folder, per user (XDG_DATA_HOME or ~/.local/share); its log comes through OnLog, its own log file is off.
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "meitou", "ngx");
        Directory.CreateDirectory(logs);
        string[] paths = ModelPath is null ? [AppContext.BaseDirectory] : [ModelPath, AppContext.BaseDirectory];
        var pathPointers = (byte**)Keep((byte*)NativeMemory.Alloc((nuint)(paths.Length * sizeof(nint))));
        for (int i = 0; i < paths.Length; i++) pathPointers[i] = Keep(WideString(paths[i]));
        var dataPath = Keep(WideString(logs));
        var project = Keep(AsciiString(VendorUpscalers.DlssProjectId));   // engine type custom (0)
        var version = Keep(AsciiString(VendorUpscalers.DlssEngineVersion));
        var info = new FeatureCommonInfo
        {
            PathList = new PathListInfo { Paths = (nint)pathPointers, Length = (uint)paths.Length },
            Logging = new LoggingInfo { Callback = (nint)(delegate* unmanaged<byte*, int, int, void>)&OnLog, MinimumLevel = Verbose ? 2 : 1, DisableOtherSinks = 1 },
        };
        int? initialised = OperatingSystem.IsWindows()
            ? ((delegate* unmanaged<byte*, int, byte*, byte*, nint, nint, nint, nint, nint, FeatureCommonInfo*, uint, int>)init)(project, 0, version, dataPath, d.Instance.Handle, d.PhysicalDevice.Handle, d.Device.Handle, 0, 0, &info, ApiVersion)
            : InitFromNativeCaller(init, project, 0, version, dataPath, d.Instance.Handle, d.PhysicalDevice.Handle, d.Device.Handle, &info, ApiVersion);
        if (initialised is null) return false;
        if (initialised != ResultSuccess) { Console.WriteLine($"ngx       init failed ({initialised:x}) on {d.DeviceName}"); return false; }
        device = d.Device.Handle;
        void* caps;
        int rc = getCapabilityParameters(&caps);
        if (rc != ResultSuccess) { Console.WriteLine($"ngx       NVSDK_NGX_VULKAN_GetCapabilityParameters failed ({rc:x})"); return false; }
        int available = 0, outdated = 0, initResult = 0;
        NgxParameters.GetInt(caps, "SuperSampling.Available\0"u8, &available);
        NgxParameters.GetInt(caps, "SuperSampling.NeedsUpdatedDriver\0"u8, &outdated);
        NgxParameters.GetInt(caps, "SuperSampling.FeatureInitResult\0"u8, &initResult);
        DestroyParameters(caps);
        if (available == 0)
        {
            Console.WriteLine(outdated != 0 ? $"ngx       DLSS needs a newer driver on {d.DeviceName}; NGX shut down" : $"ngx       DLSS is not available on {d.DeviceName} (it needs an RTX GPU; NGX's result {initResult:x}); NGX shut down");
            return false;
        }
        return true;
    }

    /// <summary>
    /// NVSDK_NGX_VULKAN_Init_with_ProjectID through libffi, its arguments as nvsdk_ngx_vk.h declares them (the Vulkan proc-address getters null:
    /// NGX resolves them itself). Observed: called straight from managed code the init aborts (<c>std::logic_error</c>, "basic_string::_M_construct
    /// null not valid"), and the same call through libffi succeeds; why is Unknown (libffi passes the arguments as a direct call does).
    /// Null when libffi is missing (the call is not made).
    /// </summary>
    static int? InitFromNativeCaller(nint function, byte* project, int engine, byte* version, byte* dataPath, nint instance, nint physicalDevice, nint device, FeatureCommonInfo* info, uint apiVersion)
    {
        // libffi 3.4 is .so.8, 3.3 is .so.7, with the same calls.
        if (!NativeLibrary.TryLoad("libffi.so.8", out var ffi) && !NativeLibrary.TryLoad("libffi.so.7", out ffi))
        {
            Console.WriteLine("ngx       libffi (libffi.so.8 or .so.7) does not load: it is needed to call NGX's init");
            return null;
        }
        var prepare = (delegate* unmanaged<void*, int, uint, void*, void**, int>)NativeLibrary.GetExport(ffi, "ffi_prep_cif");
        var call = (delegate* unmanaged<void*, nint, void*, void**, void>)NativeLibrary.GetExport(ffi, "ffi_call");
        void* pointer = (void*)NativeLibrary.GetExport(ffi, "ffi_type_pointer"), int32 = (void*)NativeLibrary.GetExport(ffi, "ffi_type_sint32"), uint32 = (void*)NativeLibrary.GetExport(ffi, "ffi_type_uint32");
        nint gipa = 0, gdpa = 0;
        var types = stackalloc void*[11] { pointer, int32, pointer, pointer, pointer, pointer, pointer, pointer, pointer, pointer, uint32 };
        var values = stackalloc void*[11] { &project, &engine, &version, &dataPath, &instance, &physicalDevice, &device, &gipa, &gdpa, &info, &apiVersion };
        var cif = stackalloc byte[64];   // ffi_cif: abi, nargs, arg_types, rtype, bytes, flags (32 bytes on x86-64)
        const int FfiUnix64 = 2;
        int status = prepare(cif, FfiUnix64, 11, int32, types);
        if (status != 0) { Console.WriteLine($"ngx       ffi_prep_cif failed ({status})"); return null; }
        ulong result = 0;   // integer returns need sizeof(ffi_arg)
        call(cif, function, &result, values);
        return (int)result;
    }

    static Silk.NET.Vulkan.DriverId Driver(VulkanDevice d)
    {
        var driver = new Silk.NET.Vulkan.PhysicalDeviceDriverProperties { SType = Silk.NET.Vulkan.StructureType.PhysicalDeviceDriverProperties };
        var properties = new Silk.NET.Vulkan.PhysicalDeviceProperties2 { SType = Silk.NET.Vulkan.StructureType.PhysicalDeviceProperties2, PNext = &driver };
        d.Vk.GetPhysicalDeviceProperties2(d.PhysicalDevice, &properties);
        return driver.DriverID;
    }

    byte* Keep(byte* p)
    {
        initStrings.Add((nint)p);
        return p;
    }

    /// <summary>A null-terminated ASCII string, freed with <see cref="NativeMemory.Free"/>.</summary>
    static byte* AsciiString(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s + "\0");
        var p = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.AsSpan().CopyTo(new Span<byte>(p, bytes.Length));
        return p;
    }

    /// <summary>A null-terminated <c>wchar_t</c> string (UTF-16 on Windows, UTF-32 on Linux), freed with <see cref="NativeMemory.Free"/>.</summary>
    static byte* WideString(string s)
    {
        var bytes = (OperatingSystem.IsWindows() ? Encoding.Unicode : Encoding.UTF32).GetBytes(s + "\0");
        var p = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.AsSpan().CopyTo(new Span<byte>(p, bytes.Length));
        return p;
    }

    /// <summary>MEITOU_NGX_LOG=1 prints everything NGX says (its verbose level) (hundreds of lines at start-up); otherwise only its errors, minus the optional config files and the system model folder it looks for.</summary>
    static readonly bool Verbose = Environment.GetEnvironmentVariable("MEITOU_NGX_LOG") is "1";

    [UnmanagedCallersOnly]
    static void OnLog(byte* message, int level, int feature)
    {
        var text = Marshal.PtrToStringUTF8((nint)message)?.TrimEnd();
        if (string.IsNullOrEmpty(text)) return;
        if (Verbose || (text.Contains("error", StringComparison.OrdinalIgnoreCase) && !text.Contains("configuration file") && !text.Contains("model folder"))) Console.WriteLine($"ngx       {text}");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (device != 0) shutdown(device);
        foreach (var p in initStrings) NativeMemory.Free((void*)p);
        initStrings.Clear();
    }
}

/// <summary>
/// NGX's parameter map (<c>NVSDK_NGX_Parameter</c>) through the client library's C functions of nvsdk_ngx_params.h. Names are the
/// <c>NVSDK_NGX_Parameter_*</c> strings of nvsdk_ngx_defs.h, null-terminated.
/// </summary>
static unsafe class NgxParameters
{
    static delegate* unmanaged<void*, byte*, float, void> setF;
    static delegate* unmanaged<void*, byte*, uint, void> setUI;
    static delegate* unmanaged<void*, byte*, int, void> setI;
    static delegate* unmanaged<void*, byte*, void*, void> setVoidPointer;
    static delegate* unmanaged<void*, byte*, int*, int> getI;

    public static void Load(nint lib)
    {
        setF = (delegate* unmanaged<void*, byte*, float, void>)NativeLibrary.GetExport(lib, "NVSDK_NGX_Parameter_SetF");
        setUI = (delegate* unmanaged<void*, byte*, uint, void>)NativeLibrary.GetExport(lib, "NVSDK_NGX_Parameter_SetUI");
        setI = (delegate* unmanaged<void*, byte*, int, void>)NativeLibrary.GetExport(lib, "NVSDK_NGX_Parameter_SetI");
        setVoidPointer = (delegate* unmanaged<void*, byte*, void*, void>)NativeLibrary.GetExport(lib, "NVSDK_NGX_Parameter_SetVoidPointer");
        getI = (delegate* unmanaged<void*, byte*, int*, int>)NativeLibrary.GetExport(lib, "NVSDK_NGX_Parameter_GetI");
    }

    public static void SetFloat(void* p, ReadOnlySpan<byte> name, float value)
    {
        fixed (byte* n = name) setF(p, n, value);
    }
    public static void SetUInt(void* p, ReadOnlySpan<byte> name, uint value)
    {
        fixed (byte* n = name) setUI(p, n, value);
    }
    public static void SetInt(void* p, ReadOnlySpan<byte> name, int value)
    {
        fixed (byte* n = name) setI(p, n, value);
    }
    public static void SetPointer(void* p, ReadOnlySpan<byte> name, void* value)
    {
        fixed (byte* n = name) setVoidPointer(p, n, value);
    }
    public static int GetInt(void* p, ReadOnlySpan<byte> name, int* value)
    {
        fixed (byte* n = name) return getI(p, n, value);
    }
}
