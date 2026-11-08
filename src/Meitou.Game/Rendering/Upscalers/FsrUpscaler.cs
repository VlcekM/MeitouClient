using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using Meitou.Rendering.Gpu;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Upscalers;

/// <summary>
/// AMD FSR 3.1 upscaling through the FidelityFX API (<c>amd_fidelityfx_vk.dll</c>, MIT, from the FidelityFX SDK 1.1.x; never in the
/// repository): loaded at run time from <c>MEITOU_FFX_PATH</c> (the DLL or its folder) or next to the executable. The context is
/// created for the render and display size of the first dispatch and again when they change; the dispatch is recorded into
/// the <see cref="GpuContext"/>'s frame. Inputs follow <see cref="UpscaleInputs"/>: images stay in GENERAL (declared as COMMON, which the
/// backend reads as GENERAL and returns them to), motion is turned into FSR's convention (render pixels towards the previous
/// position) by the motion-vector scale. docs/engine.md "Upscaling".
/// </summary>
public sealed unsafe class FsrUpscaler : IUpscaler
{
    public static readonly string LibraryName = OperatingSystem.IsMacOS() ? "libamd_fidelityfx_vk.dylib" : OperatingSystem.IsLinux() ? "libamd_fidelityfx_vk.so" : "amd_fidelityfx_vk.dll";

    /// <summary>The Vulkan loader the process is bound to (FFX calls <c>vkGetDeviceProcAddr</c> through it).</summary>
    internal static string VulkanLoaderName => OperatingSystem.IsMacOS() ? "libvulkan.1.dylib" : OperatingSystem.IsLinux() ? "libvulkan.so.1" : "vulkan-1.dll";

    /// <summary>A null-terminated <c>wchar_t</c> string of the library (UTF-16 on Windows, UTF-32 on Linux and macOS).</summary>
    internal static string WideString(char* s)
    {
        if (OperatingSystem.IsWindows()) return new string(s);
        var sb = new System.Text.StringBuilder();
        for (int* p = (int*)s; *p != 0; p++) sb.Append(char.ConvertFromUtf32(*p is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? *p : 0xFFFD));
        return sb.ToString();
    }

    // ffx_api: type constants, flags, states (FidelityFX SDK headers, MIT).
    const ulong CreateUpscaleType = 0x00010000, DispatchUpscaleType = 0x00010001, BackendVkType = 0x00000003;
    const uint EnableHdr = 1 << 0, EnableAutoExposure = 1 << 5, EnableDebugChecking = 1 << 7;
    const uint StateCommon = 1, StateUnorderedAccess = 2, UsageUav = 2, TypeTexture2D = 2;

    [StructLayout(LayoutKind.Sequential)] struct ApiHeader { public ulong Type; public nint Next; }
    [StructLayout(LayoutKind.Sequential)] struct Dimensions { public uint Width, Height; }
    [StructLayout(LayoutKind.Sequential)] struct Coords { public float X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct ResourceDescription { public uint Type, Format, Width, Height, Depth, MipCount, Flags, Usage; }
    [StructLayout(LayoutKind.Sequential)] struct ApiResource { public nint Handle; public ResourceDescription Description; public uint State; }
    [StructLayout(LayoutKind.Sequential)] struct CreateUpscaleDesc { public ApiHeader Header; public uint Flags; public Dimensions MaxRenderSize, MaxUpscaleSize; public nint Message; }
    [StructLayout(LayoutKind.Sequential)] struct BackendVkDesc { public ApiHeader Header; public nint Device, PhysicalDevice, DeviceProcAddr; }
    [StructLayout(LayoutKind.Sequential)]
    struct DispatchUpscaleDesc
    {
        public ApiHeader Header;
        public nint CommandList;
        public ApiResource Color, Depth, MotionVectors, Exposure, Reactive, TransparencyAndComposition, Output;
        public Coords JitterOffset, MotionVectorScale;
        public Dimensions RenderSize, UpscaleSize;
        public byte EnableSharpening;
        public float Sharpness, FrameTimeDelta, PreExposure;
        public byte Reset;
        public float CameraNear, CameraFar, CameraFovAngleVertical, ViewSpaceToMetersFactor;
        public uint Flags;
    }

    readonly VulkanDevice device;
    readonly GpuContext ctx;
    readonly delegate* unmanaged<nint*, void*, void*, uint> createContext;
    readonly delegate* unmanaged<nint*, void*, uint> destroyContext;
    readonly delegate* unmanaged<nint*, void*, uint> dispatch;
    readonly bool debug;
    // The create descs must stay alive as long as the context (ffx_api.h), so they live in native memory.
    readonly CreateUpscaleDesc* createDesc;
    readonly BackendVkDesc* backendDesc;
    nint context;
    (int, int, int, int) contextSize;
    bool failed;

    public UpscalerKind Kind => UpscalerKind.Fsr;
    public string Name { get; private set; } = "fsr 3.1";

    FsrUpscaler(VulkanDevice device, GpuContext ctx, nint library)
    {
        this.device = device;
        this.ctx = ctx;
        createContext = (delegate* unmanaged<nint*, void*, void*, uint>)NativeLibrary.GetExport(library, "ffxCreateContext");
        destroyContext = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxDestroyContext");
        dispatch = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxDispatch");
        debug = Environment.GetEnvironmentVariable("MEITOU_UPSCALER_LOG") == "1";
        createDesc = (CreateUpscaleDesc*)NativeMemory.AllocZeroed((nuint)sizeof(CreateUpscaleDesc));
        backendDesc = (BackendVkDesc*)NativeMemory.AllocZeroed((nuint)sizeof(BackendVkDesc));
    }

    /// <summary>The upscaler, or null with the reason when the library is not found or does not load.</summary>
    public static FsrUpscaler? TryCreate(GpuContext ctx, out string? reason)
    {
        foreach (var path in Candidates())
        {
            if (!File.Exists(path)) continue;
            if (!NativeLibrary.TryLoad(path, out var library)) { reason = $"{path} does not load"; return null; }
            reason = null;
            Console.WriteLine($"upscaler  FSR from {path}");
            return new FsrUpscaler(ctx.Device, ctx, library);
        }
        reason = $"{LibraryName} not found (set MEITOU_FFX_PATH or put it next to the executable)";
        return null;
    }

    internal static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable("MEITOU_FFX_PATH") is { Length: > 0 } env)
            yield return Directory.Exists(env) ? Path.Combine(env, LibraryName) : env;
        yield return Path.Combine(AppContext.BaseDirectory, LibraryName);
    }

    [UnmanagedCallersOnly]
    static void OnMessage(uint type, char* message) => Console.WriteLine($"fsr       {(type == 0 ? "error" : "warning")}: {WideString(message)}");

    bool EnsureContext(UpscaleInputs i)
    {
        var size = (i.RenderWidth, i.RenderHeight, i.DisplayWidth, i.DisplayHeight);
        if (context != 0 && contextSize == size) return true;
        DestroyContext();
        *backendDesc = new BackendVkDesc
        {
            Header = new ApiHeader { Type = BackendVkType },
            Device = device.Device.Handle,
            PhysicalDevice = device.PhysicalDevice.Handle,
            // FFX calls vkGetDeviceProcAddr through the loader the process is already bound to.
            DeviceProcAddr = NativeLibrary.GetExport(NativeLibrary.Load(VulkanLoaderName), "vkGetDeviceProcAddr"),
        };
        *createDesc = new CreateUpscaleDesc
        {
            Header = new ApiHeader { Type = CreateUpscaleType, Next = (nint)backendDesc },
            // The scene colour is linear HDR before the exposure (which the chain applies after the upscaler): FSR measures its own.
            Flags = EnableHdr | EnableAutoExposure | (debug ? EnableDebugChecking : 0),
            MaxRenderSize = new Dimensions { Width = (uint)i.RenderWidth, Height = (uint)i.RenderHeight },
            MaxUpscaleSize = new Dimensions { Width = (uint)i.DisplayWidth, Height = (uint)i.DisplayHeight },
            Message = debug ? (nint)(delegate* unmanaged<uint, char*, void>)&OnMessage : 0,
        };
        nint created = 0;
        uint rc = createContext(&created, createDesc, null);
        if (rc != 0)
        {
            Console.WriteLine($"upscaler  ffxCreateContext failed ({rc})");
            return false;
        }
        context = created;
        contextSize = size;
        Name = $"fsr 3.1 {i.RenderWidth}x{i.RenderHeight}";
        return true;
    }

    void DestroyContext()
    {
        if (context == 0) return;
        device.WaitIdle();   // frames in flight may still use it
        nint c = context;
        destroyContext(&c, null);
        context = 0;
    }

    static uint SurfaceFormat(InternalFormat f) => f switch
    {
        InternalFormat.Rgba16f => 4,
        InternalFormat.Rgba32f => 3,
        InternalFormat.RG16f => 18,
        InternalFormat.R32f => 28,
        InternalFormat.Rgba8 => 10,
        InternalFormat.R8 => 25,
        InternalFormat.R11fG11fB10f => 16,
        _ => 0,
    };

    ApiResource Resource(Meitou.Rendering.Gpu.GpuTexture texture, bool output)
    {
        var d = texture.Desc;
        return new ApiResource
        {
            Handle = (nint)ctx.Device.NativeImage(texture).Handle,
            Description = new ResourceDescription
            {
                Type = TypeTexture2D, Format = SurfaceFormat(d.Format), Width = (uint)d.Width, Height = (uint)d.Height,
                Depth = 1, MipCount = 1, Usage = output ? UsageUav : 0,
            },
            // Every native image stays in GENERAL: COMMON (inputs) and UNORDERED_ACCESS (output) are both GENERAL to the backend.
            State = output ? StateUnorderedAccess : StateCommon,
        };
    }

    public bool Dispatch(UpscaleInputs i)
    {
        if (failed || !EnsureContext(i)) { failed = true; return false; }
        var desc = new DispatchUpscaleDesc
        {
            Header = new ApiHeader { Type = DispatchUpscaleType },
            Color = Resource(i.Colour, false),
            Depth = Resource(i.Depth, false),
            MotionVectors = Resource(i.Motion, false),
            Reactive = i.Reactive is { } r ? Resource(r, false) : default,
            Output = Resource(i.Output, true),
            // Both move the picture by +jitter along +column and +row of the image (ours is bottom-up GL, FSR's top-down D3D with
            // the y offset negated in the projection: the same direction in image rows).
            JitterOffset = new Coords { X = i.JitterPixels.X, Y = i.JitterPixels.Y },
            // Ours: UV from previous to current; FSR: render pixels from current towards previous.
            MotionVectorScale = new Coords { X = -i.RenderWidth, Y = -i.RenderHeight },
            RenderSize = new Dimensions { Width = (uint)i.RenderWidth, Height = (uint)i.RenderHeight },
            UpscaleSize = new Dimensions { Width = (uint)i.DisplayWidth, Height = (uint)i.DisplayHeight },
            EnableSharpening = (byte)(i.Sharpness > 0 ? 1 : 0),
            Sharpness = i.Sharpness,
            FrameTimeDelta = i.DeltaSeconds * 1000,
            PreExposure = 1,
            Reset = (byte)(i.Reset ? 1 : 0),
            CameraNear = i.Near,
            CameraFar = i.Far,
            CameraFovAngleVertical = i.FieldOfView,
            Flags = 0,
            ViewSpaceToMetersFactor = 0.1f,   // Kenshi units taken as decimetres (docs/formats/terrain.md "Unit size"; DECISIONS 16)
        };
        var list = (CommandList)ctx.BeginNative("fsr upscale");
        var cb = list.Handle;
        desc.CommandList = cb.Handle;
        nint c = context;
        uint rc = dispatch(&c, &desc);
        ctx.EndNative(list);
        if (rc != 0)
        {
            Console.WriteLine($"upscaler  ffxDispatch failed ({rc})");
            failed = true;
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        DestroyContext();
        NativeMemory.Free(createDesc);
        NativeMemory.Free(backendDesc);
        // The library stays loaded for the process: unloading it under a live device is not worth the risk.
    }
}
