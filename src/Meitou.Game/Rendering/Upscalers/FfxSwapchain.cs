using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Meitou.Rendering.Upscalers;

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

    readonly VulkanDevice device;
    readonly delegate* unmanaged<nint*, void*, void*, uint> createContext;
    readonly delegate* unmanaged<nint*, void*, uint> destroyContext;
    readonly delegate* unmanaged<nint*, void*, uint> query;
    readonly delegate* unmanaged<nint*, void*, uint> configure;
    // The create desc and the swapchain handle it points to stay valid as long as the context (ffx_api.h).
    readonly CreateDesc* desc;
    readonly SwapchainKHR* swapchainSlot;
    nint context;
    bool disposed;
    delegate* unmanaged<Device, SwapchainCreateInfoKHR*, AllocationCallbacks*, SwapchainKHR*, nint, Result> replaceCreate;
    delegate* unmanaged<Device, SwapchainKHR, AllocationCallbacks*, nint, void> replaceDestroy;
    delegate* unmanaged<Device, SwapchainKHR, uint*, Image*, Result> getImages;
    delegate* unmanaged<Device, SwapchainKHR, ulong, Semaphore, Fence, uint*, Result> acquire;
    delegate* unmanaged<Queue, PresentInfoKHR*, Result> present;

    // submitFunc has no queue argument, so each role has a function of its own; one layer at a time per process.
    static VulkanDevice? submitDevice;
    static Queue gameQueue, computeQueue, presentQueue, acquireQueue;
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
        this.device = device;
        createContext = (delegate* unmanaged<nint*, void*, void*, uint>)NativeLibrary.GetExport(library, "ffxCreateContext");
        destroyContext = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxDestroyContext");
        query = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxQuery");
        configure = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxConfigure");
        desc = (CreateDesc*)NativeMemory.AllocZeroed((nuint)sizeof(CreateDesc));
        swapchainSlot = (SwapchainKHR*)NativeMemory.AllocZeroed((nuint)sizeof(SwapchainKHR));
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
            if (submitDevice is not null) { reason = "a swapchain layer already exists"; return null; }
            submitDevice = device;
        }
        var extra = device.ExtraQueues;
        gameQueue = device.GraphicsQueue;
        presentQueue = extra[0];
        acquireQueue = extra[1];
        computeQueue = extra.Length > 2 ? extra[2] : extra[0];
        return new FfxSwapchain(device, library);
    }

    static Result Submit(Queue queue, object gate, uint count, SubmitInfo* submits, Fence fence)
    {
        try { lock (gate) return submitDevice!.Vk.QueueSubmit(queue, count, submits, fence); }
        catch (Exception) { return Result.ErrorDeviceLost; }   // nothing may escape into the library
    }

    [UnmanagedCallersOnly] static Result SubmitGame(uint count, SubmitInfo* submits, Fence fence) => Submit(gameQueue, submitDevice!.QueueLock, count, submits, fence);
    [UnmanagedCallersOnly] static Result SubmitCompute(uint count, SubmitInfo* submits, Fence fence) => Submit(computeQueue, ComputeLock, count, submits, fence);
    [UnmanagedCallersOnly] static Result SubmitPresent(uint count, SubmitInfo* submits, Fence fence) => Submit(presentQueue, PresentLock, count, submits, fence);
    [UnmanagedCallersOnly] static Result SubmitAcquire(uint count, SubmitInfo* submits, Fence fence) => Submit(acquireQueue, AcquireLock, count, submits, fence);

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
        if (context == 0) return false;
        const uint PremulAlpha = 1 << 0, InternalDoubleBuffering = 1 << 1;
        var d = new RegisterUiDesc { Header = new ApiHeader { Type = RegisterUiType }, Ui = ui ?? default, Flags = ui is null ? 0 : PremulAlpha | InternalDoubleBuffering };
        nint c = context;
        return configure(&c, &d) == 0;
    }

    /// <summary>One line on the queues handed over, for the log.</summary>
    public string QueueLayout => $"game = graphics queue 0, present = queue 1, image acquire = queue 2, async compute = {(computeQueue.Handle == presentQueue.Handle ? "the present queue" : "queue 3")} (family {device.GraphicsFamily})";

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
        if (context == 0)
        {
            *swapchainSlot = old;
            *desc = new CreateDesc
            {
                Header = new ApiHeader { Type = CreateSwapchainType },
                PhysicalDevice = device.PhysicalDevice.Handle,
                Device = device.Device.Handle,
                Swapchain = (nint)swapchainSlot,
                CreateInfo = info,
                GameQueue = Info(gameQueue, (nint)(delegate* unmanaged<uint, SubmitInfo*, Fence, Result>)&SubmitGame),
                AsyncComputeQueue = Info(computeQueue, (nint)(delegate* unmanaged<uint, SubmitInfo*, Fence, Result>)&SubmitCompute),
                PresentQueue = Info(presentQueue, (nint)(delegate* unmanaged<uint, SubmitInfo*, Fence, Result>)&SubmitPresent),
                ImageAcquireQueue = Info(acquireQueue, (nint)(delegate* unmanaged<uint, SubmitInfo*, Fence, Result>)&SubmitAcquire),
            };
            nint created = 0;
            uint rc = createContext(&created, desc, null);
            if (rc != 0) { why = $"ffxCreateContext (swapchain) returned {rc}"; return false; }
            context = created;
            var q = new FunctionsQuery { Header = new ApiHeader { Type = QueryFunctionsType } };
            nint c = context;
            rc = query(&c, &q);
            if (rc != 0 || q.Create == 0 || q.Destroy == 0 || q.GetImages == 0 || q.Acquire == 0 || q.Present == 0)
            { why = $"ffxQuery (replacement functions) returned {rc}"; return false; }
            replaceCreate = (delegate* unmanaged<Device, SwapchainCreateInfoKHR*, AllocationCallbacks*, SwapchainKHR*, nint, Result>)q.Create;
            replaceDestroy = (delegate* unmanaged<Device, SwapchainKHR, AllocationCallbacks*, nint, void>)q.Destroy;
            getImages = (delegate* unmanaged<Device, SwapchainKHR, uint*, Image*, Result>)q.GetImages;
            acquire = (delegate* unmanaged<Device, SwapchainKHR, ulong, Semaphore, Fence, uint*, Result>)q.Acquire;
            present = (delegate* unmanaged<Queue, PresentInfoKHR*, Result>)q.Present;
            swapchain = Swapchain = *swapchainSlot;
            why = null;
            return swapchain.Handle != 0 || Fail(out why, "the context made no swapchain");
        }
        if (old.Handle != 0) replaceDestroy(device.Device, old, null, context);
        var created2 = default(SwapchainKHR);
        var r = replaceCreate(device.Device, &info, null, &created2, context);
        if (r != Result.Success) { why = $"the replacement vkCreateSwapchainKHR returned {r}"; return false; }
        swapchain = Swapchain = created2;
        why = null;
        return true;

        static bool Fail(out string? why, string text) { why = text; return false; }
    }

    static QueueInfo Info(Queue queue, nint submit) => new() { Queue = queue.Handle, FamilyIndex = submitFamily, SubmitFunc = submit };
    static uint submitFamily => submitDevice!.GraphicsFamily;

    public Result GetImages(Device d, SwapchainKHR swapchain, uint* count, Image* images) => getImages(d, swapchain, count, images);
    public Result Acquire(Device d, SwapchainKHR swapchain, ulong timeout, Semaphore semaphore, Fence fence, uint* index) => acquire(d, swapchain, timeout, semaphore, fence, index);
    public Result Present(Queue queue, PresentInfoKHR* info) => present(queue, info);

    /// <summary>Raised at the start of <see cref="Dispose"/>, while the swapchain still exists: a frame-generation context on it is destroyed then, first, as the FFX API requires.</summary>
    internal event Action? Disposing;

    /// <summary>Destroys the context, which destroys the swapchain it made. Not before the device is idle.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Disposing?.Invoke();
        Disposing = null;
        if (context != 0)
        {
            nint c = context;
            destroyContext(&c, null);
            context = 0;
        }
        lock (LayerLock) { if (submitDevice == device) submitDevice = null; }
        NativeMemory.Free(desc);
        NativeMemory.Free(swapchainSlot);
    }
}
