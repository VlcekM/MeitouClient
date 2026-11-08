using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Meitou.Game.Rendering.Upscalers;

/// <summary>
/// The swapchain of AMD's FSR 3 frame-interpolation layer (<c>ffxProvider_FrameGenerationSwapChain_VK</c> of <c>amd_fidelityfx_vk.dll</c>, FidelityFX SDK
/// 1.1.x, MIT; <c>ffx_api_vk.h</c> and <c>ffx_provider_framegenerationswapchain_vk.cpp</c> give the struct layouts and the call order), standing in for the plain
/// <c>vkCreateSwapchainKHR</c> / acquire / present of <see cref="Gpu.VulkanPresenter"/>. Frames are generated on top of it by <see cref="FrameGeneration"/>; without
/// that context the layer presents every rendered frame as it is. The first <see cref="Create"/> makes the FFX context (it makes the swapchain); later ones
/// (a resize, a vsync change) destroy the old swapchain and create the next through the context's own replacements, as the header says.
/// Four queues are handed over (game, async compute, present, image acquire), the first of the device's graphics queue, the rest its
/// <see cref="VulkanDevice.ExtraQueues"/>; their <c>submitFunc</c>s lock (the game queue on the device's <see cref="VulkanDevice.QueueLock"/>, the others
/// on locks of their own, since only the layer submits on them). Windows, Proton and Linux (<c>libamd_fidelityfx_vk.so</c>, tools/scripts/build-ffx-linux.sh).
/// </summary>
public sealed unsafe class FfxSwapchain : IDisposable
{
    const ulong CreateSwapchainType = 0x40001, RegisterUiType = 0x40002, QueryFunctionsType = 0x40005;
    /// <summary>Graphics-family queues beyond the first that this wants: present, image acquire and (when the family has it) async compute.</summary>
    public const int WantedExtraQueues = 3;
    /// <summary>The fewest extra queues it works with: present and image acquire; the async compute role then shares the present queue.</summary>
    public const int MinimumExtraQueues = 2;

    [StructLayout(LayoutKind.Sequential)] struct ApiHeader { public ulong Type; public nint Next; }
    [StructLayout(LayoutKind.Sequential)] struct QueueInfo { public nint Queue; public uint FamilyIndex; public nint SubmitFunc; }
    [StructLayout(LayoutKind.Sequential)]
    struct CreateDesc
    {
        public ApiHeader Header;
        public nint PhysicalDevice, Device, Swapchain, Allocator;
        public SwapchainCreateInfoKHR CreateInfo;
        public QueueInfo GameQueue, AsyncComputeQueue, PresentQueue, ImageAcquireQueue;
    }
    [StructLayout(LayoutKind.Sequential)] struct RegisterUiDesc { public ApiHeader Header; public FfxApi.Resource Ui; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    struct FunctionsQuery
    {
        public ApiHeader Header;
        public nint Create, Destroy, GetImages, Acquire, Present, SetHdrMetadata, GetLastPresentCount;
    }

    readonly VulkanDevice _device;
    readonly delegate* unmanaged<nint*, void*, void*, uint> _createContext;
    readonly delegate* unmanaged<nint*, void*, uint> _destroyContext;
    readonly delegate* unmanaged<nint*, void*, uint> _query;
    readonly delegate* unmanaged<nint*, void*, uint> _configure;
    // The create desc and the swapchain handle it points to stay valid as long as the context (ffx_api.h).
    readonly CreateDesc* _desc;
    readonly SwapchainKHR* _swapchainSlot;
    nint _context;
    bool _disposed;
    delegate* unmanaged<Device, SwapchainCreateInfoKHR*, AllocationCallbacks*, SwapchainKHR*, nint, Result> _replaceCreate;
    delegate* unmanaged<Device, SwapchainKHR, AllocationCallbacks*, nint, void> _replaceDestroy;
    delegate* unmanaged<Device, SwapchainKHR, uint*, Image*, Result> _getImages;
    delegate* unmanaged<Device, SwapchainKHR, ulong, Semaphore, Fence, uint*, Result> _acquire;
    delegate* unmanaged<Queue, PresentInfoKHR*, Result> _present;

    // submitFunc has no queue argument, so each role has a function of its own; one layer at a time per process.
    static VulkanDevice? s_submitDevice;
    static Queue s_gameQueue, s_computeQueue, s_presentQueue, s_acquireQueue;
    static readonly object ComputeLock = new(), PresentLock = new(), AcquireLock = new();
    // Guards submitDevice: one layer at a time.
    static readonly object LayerLock = new();

    /// <summary>True when the library exists and loads (<c>MEITOU_FFX_PATH</c> or next to the executable, as for FSR upscaling), else false with the reason.</summary>
    public static bool LibraryLoads(out nint library, out string? reason)
    {
        library = 0;
        foreach (var path in FsrUpscaler.Candidates())
        {
            if (!File.Exists(path)) continue;
            if (!NativeLibrary.TryLoad(path, out library)) { reason = $"{path} does not load"; return false; }
            if (!NativeLibrary.TryGetExport(library, "ffxCreateContext", out _) || !NativeLibrary.TryGetExport(library, "ffxQuery", out _))
            { reason = $"{path} lacks ffxCreateContext / ffxQuery"; return false; }
            reason = null;
            return true;
        }
        reason = $"{FsrUpscaler.LibraryName} not found (set MEITOU_FFX_PATH or put it next to the executable)";
        return false;
    }

    FfxSwapchain(VulkanDevice device, nint library)
    {
        this._device = device;
        _createContext = (delegate* unmanaged<nint*, void*, void*, uint>)NativeLibrary.GetExport(library, "ffxCreateContext");
        _destroyContext = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxDestroyContext");
        _query = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxQuery");
        _configure = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxConfigure");
        _desc = (CreateDesc*)NativeMemory.AllocZeroed((nuint)sizeof(CreateDesc));
        _swapchainSlot = (SwapchainKHR*)NativeMemory.AllocZeroed((nuint)sizeof(SwapchainKHR));
    }

    /// <summary>The layer for <paramref name="device"/>, or null with the reason (library missing, too few queues).</summary>
    public static FfxSwapchain? TryCreate(VulkanDevice device, out string? reason)
    {
        if (!LibraryLoads(out var library, out reason)) return null;
        if (device.ExtraQueues.Length < MinimumExtraQueues)
        {
            reason = $"the graphics queue family offers {device.ExtraQueues.Length + 1} queues, the layer needs {MinimumExtraQueues + 1}";
            return null;
        }
        lock (LayerLock)
        {
            if (s_submitDevice is not null) { reason = "a swapchain layer already exists"; return null; }
            s_submitDevice = device;
        }
        var extra = device.ExtraQueues;
        s_gameQueue = device.GraphicsQueue;
        s_presentQueue = extra[0];
        s_acquireQueue = extra[1];
        s_computeQueue = extra.Length > 2 ? extra[2] : extra[0];
        return new FfxSwapchain(device, library);
    }

    static Result Submit(Queue queue, object gate, uint count, SubmitInfo* submits, Fence fence)
    {
        try { lock (gate) return s_submitDevice!.Vk.QueueSubmit(queue, count, submits, fence); }
        catch (Exception) { return Result.ErrorDeviceLost; }   // nothing may escape into the library
    }

    [UnmanagedCallersOnly] static Result SubmitGame(uint count, SubmitInfo* submits, Fence fence) => Submit(s_gameQueue, s_submitDevice!.QueueLock, count, submits, fence);
    [UnmanagedCallersOnly] static Result SubmitCompute(uint count, SubmitInfo* submits, Fence fence) => Submit(s_computeQueue, ComputeLock, count, submits, fence);
    [UnmanagedCallersOnly] static Result SubmitPresent(uint count, SubmitInfo* submits, Fence fence) => Submit(s_presentQueue, PresentLock, count, submits, fence);
    [UnmanagedCallersOnly] static Result SubmitAcquire(uint count, SubmitInfo* submits, Fence fence) => Submit(s_acquireQueue, AcquireLock, count, submits, fence);

    /// <summary>The swapchain the layer made last (changes on a resize or a vsync change); what the frame-generation context is configured with.</summary>
    public SwapchainKHR Swapchain { get; private set; }
    /// <summary>The swapchain's colour format as FfxApiSurfaceFormat (B8G8R8A8 or R8G8B8A8 UNORM).</summary>
    public uint SurfaceFormat { get; private set; }

    /// <summary>
    /// Registers (or, with a null <paramref name="ui"/>, removes) the interface picture the layer lays over every presented image, rendered and generated:
    /// premultiplied alpha, copied by the layer at the present into a buffer of its own (so the next frame may overwrite it). The layer forgets it at each
    /// present, so this is called every frame. The image must be top-down like the presented one and have TransferSrc usage.
    /// </summary>
    internal bool RegisterUi(FfxApi.Resource? ui)
    {
        if (_context == 0) return false;
        const uint premulAlpha = 1 << 0, internalDoubleBuffering = 1 << 1;
        var d = new RegisterUiDesc { Header = new ApiHeader { Type = RegisterUiType }, Ui = ui ?? default, Flags = ui is null ? 0 : premulAlpha | internalDoubleBuffering };
        nint c = _context;
        return _configure(&c, &d) == 0;
    }

    /// <summary>One line on the queues handed over, for the log.</summary>
    public string QueueLayout => $"game = graphics queue 0, present = queue 1, image acquire = queue 2, async compute = {(s_computeQueue.Handle == s_presentQueue.Handle ? "the present queue" : "queue 3")} (family {_device.GraphicsFamily})";

    /// <summary>
    /// Makes the swapchain described by <paramref name="info"/> (its <c>OldSwapchain</c> is ignored: the old one is <paramref name="old"/>, destroyed here). The first call
    /// makes the FFX context; a later one destroys <paramref name="old"/> through the replacement and creates the next. False with <paramref name="why"/> on any failure;
    /// the layer is then unusable (call <see cref="Dispose"/>).
    /// </summary>
    public bool Create(SwapchainCreateInfoKHR info, SwapchainKHR old, out SwapchainKHR swapchain, out string? why)
    {
        swapchain = default;
        info.OldSwapchain = default;
        SurfaceFormat = info.ImageFormat == Format.R8G8B8A8Unorm ? FfxApi.FormatR8G8B8A8Unorm : FfxApi.FormatB8G8R8A8Unorm;
        if (_context == 0)
        {
            *_swapchainSlot = old;
            *_desc = new CreateDesc
            {
                Header = new ApiHeader { Type = CreateSwapchainType },
                PhysicalDevice = _device.PhysicalDevice.Handle,
                Device = _device.Device.Handle,
                Swapchain = (nint)_swapchainSlot,
                CreateInfo = info,
                GameQueue = Info(s_gameQueue, (nint)(delegate* unmanaged<uint, SubmitInfo*, Fence, Result>)&SubmitGame),
                AsyncComputeQueue = Info(s_computeQueue, (nint)(delegate* unmanaged<uint, SubmitInfo*, Fence, Result>)&SubmitCompute),
                PresentQueue = Info(s_presentQueue, (nint)(delegate* unmanaged<uint, SubmitInfo*, Fence, Result>)&SubmitPresent),
                ImageAcquireQueue = Info(s_acquireQueue, (nint)(delegate* unmanaged<uint, SubmitInfo*, Fence, Result>)&SubmitAcquire),
            };
            nint created = 0;
            uint rc = _createContext(&created, _desc, null);
            if (rc != 0) { why = $"ffxCreateContext (swapchain) returned {rc}"; return false; }
            _context = created;
            var q = new FunctionsQuery { Header = new ApiHeader { Type = QueryFunctionsType } };
            nint c = _context;
            rc = _query(&c, &q);
            if (rc != 0 || q.Create == 0 || q.Destroy == 0 || q.GetImages == 0 || q.Acquire == 0 || q.Present == 0)
            { why = $"ffxQuery (replacement functions) returned {rc}"; return false; }
            _replaceCreate = (delegate* unmanaged<Device, SwapchainCreateInfoKHR*, AllocationCallbacks*, SwapchainKHR*, nint, Result>)q.Create;
            _replaceDestroy = (delegate* unmanaged<Device, SwapchainKHR, AllocationCallbacks*, nint, void>)q.Destroy;
            _getImages = (delegate* unmanaged<Device, SwapchainKHR, uint*, Image*, Result>)q.GetImages;
            _acquire = (delegate* unmanaged<Device, SwapchainKHR, ulong, Semaphore, Fence, uint*, Result>)q.Acquire;
            _present = (delegate* unmanaged<Queue, PresentInfoKHR*, Result>)q.Present;
            swapchain = Swapchain = *_swapchainSlot;
            why = null;
            return swapchain.Handle != 0 || Fail(out why, "the context made no swapchain");
        }
        if (old.Handle != 0) _replaceDestroy(_device.Device, old, null, _context);
        var created2 = default(SwapchainKHR);
        var r = _replaceCreate(_device.Device, &info, null, &created2, _context);
        if (r != Result.Success) { why = $"the replacement vkCreateSwapchainKHR returned {r}"; return false; }
        swapchain = Swapchain = created2;
        why = null;
        return true;

        static bool Fail(out string? why, string text) { why = text; return false; }
    }

    static QueueInfo Info(Queue queue, nint submit) => new() { Queue = queue.Handle, FamilyIndex = SubmitFamily, SubmitFunc = submit };
    static uint SubmitFamily => s_submitDevice!.GraphicsFamily;

    public Result GetImages(Device d, SwapchainKHR swapchain, uint* count, Image* images) => _getImages(d, swapchain, count, images);
    public Result Acquire(Device d, SwapchainKHR swapchain, ulong timeout, Semaphore semaphore, Fence fence, uint* index) => _acquire(d, swapchain, timeout, semaphore, fence, index);
    public Result Present(Queue queue, PresentInfoKHR* info) => _present(queue, info);

    /// <summary>Raised at the start of <see cref="Dispose"/>, while the swapchain still exists: a frame-generation context on it is destroyed then, first, as the FFX API requires.</summary>
    internal event Action? Disposing;

    /// <summary>Destroys the context, which destroys the swapchain it made. Not before the device is idle.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disposing?.Invoke();
        Disposing = null;
        if (_context != 0)
        {
            nint c = _context;
            _destroyContext(&c, null);
            _context = 0;
        }
        lock (LayerLock) { if (s_submitDevice == _device) s_submitDevice = null; }
        NativeMemory.Free(_desc);
        NativeMemory.Free(_swapchainSlot);
    }
}