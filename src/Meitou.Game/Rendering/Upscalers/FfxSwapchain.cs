using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Meitou.Game.Rendering.Upscalers;

/// <summary>
/// The swapchain of AMD's FSR 3 frame-interpolation layer (<c>ffxProvider_FrameGenerationSwapChain_VK</c> of <c>amd_fidelityfx_vk.dll</c>, FidelityFX
/// SDK 1.1.x, MIT; <c>ffx_api_vk.h</c> and <c>ffx_provider_framegenerationswapchain_vk.cpp</c> give the struct layouts and the call order), standing
/// in for the plain <c>vkCreateSwapchainKHR</c> / acquire / present of <see cref="Gpu.VulkanPresenter"/>. Frames are generated on top of it by
/// <see cref="FrameGeneration"/>; without that context the layer presents every rendered frame as it is. The first <see cref="Create"/> makes the FFX
/// context (it makes the swapchain); later ones (a resize, a vsync change) destroy the old swapchain and create the next through the context's own
/// replacements, as the header says. Four queues are handed over (game, async compute, present, image acquire), the first of the device's graphics
/// queue, the rest its <see cref="VulkanDevice.ExtraQueues"/>; their <c>submitFunc</c>s lock (the game queue on the device's
/// <see cref="VulkanDevice.QueueLock"/>, the others on locks of their own, since only the layer submits on them). Windows, Proton and Linux
/// (<c>libamd_fidelityfx_vk.so</c>, tools/scripts/build-ffx-linux.sh).
/// </summary>
public sealed unsafe class FfxSwapchain : IDisposable
{
    #region Interop structures

    /// <summary>
    /// The descriptor types (ffx_api_vk.h): the swapchain context's create, the interface picture's registration, and the replacement functions'
    /// query.
    /// </summary>
    const ulong CreateSwapchainType = 0x40001, RegisterUiType = 0x40002, QueryFunctionsType = 0x40005;

    /// <summary>
    /// <c>ffxApiHeader</c>: a descriptor's type and the next descriptor in its chain.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct ApiHeader
    {
        /// <summary>
        /// The descriptor's type.
        /// </summary>
        public ulong Type;

        /// <summary>
        /// The next descriptor in the chain (0: none).
        /// </summary>
        public nint Next;
    }

    /// <summary>
    /// <c>VkQueueInfoFFXAPI</c>: one queue handed to the layer, its family and its submit function.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct QueueInfo
    {
        /// <summary>
        /// The queue (a VkQueue).
        /// </summary>
        public nint Queue;

        /// <summary>
        /// The queue's family.
        /// </summary>
        public uint FamilyIndex;

        /// <summary>
        /// The function the layer submits on the queue through (<see cref="SubmitGame"/> and the others).
        /// </summary>
        public nint SubmitFunc;
    }

    /// <summary>
    /// <c>ffxCreateContextDescFrameGenerationSwapChainVK</c>: the device, the swapchain to make and the four queues.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct CreateDesc
    {
        /// <summary>
        /// The descriptor's type (<see cref="CreateSwapchainType"/>).
        /// </summary>
        public ApiHeader Header;

        /// <summary>
        /// The physical device, the device, where the made swapchain is written (<see cref="_swapchainSlot"/>), and the allocation callbacks (0).
        /// </summary>
        public nint PhysicalDevice, Device, Swapchain, Allocator;

        /// <summary>
        /// The swapchain to make.
        /// </summary>
        public SwapchainCreateInfoKHR CreateInfo;

        /// <summary>
        /// The game, async compute, present and image acquire queues.
        /// </summary>
        public QueueInfo GameQueue, AsyncComputeQueue, PresentQueue, ImageAcquireQueue;
    }

    /// <summary>
    /// <c>ffxConfigureDescFrameGenerationSwapChainRegisterUiResourceVK</c>: the interface picture laid over every presented image.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct RegisterUiDesc
    {
        /// <summary>
        /// The descriptor's type (<see cref="RegisterUiType"/>).
        /// </summary>
        public ApiHeader Header;

        /// <summary>
        /// The interface picture (default: none).
        /// </summary>
        public FfxApi.Resource Ui;

        /// <summary>
        /// The flags: premultiplied alpha and the layer's own double buffering.
        /// </summary>
        public uint Flags;
    }

    /// <summary>
    /// <c>ffxQueryDescSwapchainReplacementFunctionsVK</c>: the context's replacements for the swapchain calls.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct FunctionsQuery
    {
        /// <summary>
        /// The descriptor's type (<see cref="QueryFunctionsType"/>).
        /// </summary>
        public ApiHeader Header;

        /// <summary>
        /// The replacements of <c>vkCreateSwapchainKHR</c>, <c>vkDestroySwapchainKHR</c>, <c>vkGetSwapchainImagesKHR</c>,
        /// <c>vkAcquireNextImageKHR</c> and <c>vkQueuePresentKHR</c>, and the HDR metadata and present count functions.
        /// </summary>
        public nint Create, Destroy, GetImages, Acquire, Present, SetHdrMetadata, GetLastPresentCount;
    }

    #endregion

    #region Queues

    /// <summary>
    /// Graphics-family queues beyond the first that this wants: present, image acquire and (when the family has it) async compute.
    /// </summary>
    public const int WantedExtraQueues = 3;

    /// <summary>
    /// The fewest extra queues it works with: present and image acquire; the async compute role then shares the present queue.
    /// </summary>
    public const int MinimumExtraQueues = 2;

    /// <summary>
    /// The device of the layer that exists: <c>submitFunc</c> has no queue argument, so each role has a function of its own; one layer at a time per
    /// process.
    /// </summary>
    static VulkanDevice? s_submitDevice;

    /// <summary>
    /// The queues of the four roles: the device's graphics queue for the game, then its extra queues for present, image acquire and async compute
    /// (the present queue when there are only two).
    /// </summary>
    static Queue s_gameQueue, s_computeQueue, s_presentQueue, s_acquireQueue;

    /// <summary>
    /// The locks of the queues only the layer submits on.
    /// </summary>
    static readonly object ComputeLock = new(), PresentLock = new(), AcquireLock = new();

    /// <summary>
    /// Guards <see cref="s_submitDevice"/>: one layer at a time.
    /// </summary>
    static readonly object LayerLock = new();

    /// <summary>
    /// The swapchain's submit functions the layer calls, one per role (the game, async compute, present and image acquire queues).
    /// </summary>
    [UnmanagedCallersOnly]
    static Result SubmitGame(uint count, SubmitInfo* submits, Fence fence) =>
        Submit(s_gameQueue, s_submitDevice!.QueueLock, count, submits, fence);

    /// <summary>
    /// Submits on the async compute queue, under its own lock.
    /// </summary>
    [UnmanagedCallersOnly]
    static Result SubmitCompute(uint count, SubmitInfo* submits, Fence fence) =>
        Submit(s_computeQueue, ComputeLock, count, submits, fence);

    /// <summary>
    /// Submits on the present queue, under its own lock.
    /// </summary>
    [UnmanagedCallersOnly]
    static Result SubmitPresent(uint count, SubmitInfo* submits, Fence fence) =>
        Submit(s_presentQueue, PresentLock, count, submits, fence);

    /// <summary>
    /// Submits on the image acquire queue, under its own lock.
    /// </summary>
    [UnmanagedCallersOnly]
    static Result SubmitAcquire(uint count, SubmitInfo* submits, Fence fence) =>
        Submit(s_acquireQueue, AcquireLock, count, submits, fence);

    /// <summary>
    /// Submits on <paramref name="queue"/> under <paramref name="gate"/>; an exception becomes <see cref="Result.ErrorDeviceLost"/>, since nothing
    /// may escape into the library.
    /// </summary>
    static Result Submit(Queue queue, object gate, uint count, SubmitInfo* submits, Fence fence)
    {
        try
        {
            lock (gate)
            {
                return s_submitDevice!.Vk.QueueSubmit(queue, count, submits, fence);
            }
        }
        catch (Exception)
        {
            // nothing may escape into the library
            return Result.ErrorDeviceLost;
        }
    }

    /// <summary>
    /// A queue as the layer is handed it, with its submit function.
    /// </summary>
    static QueueInfo Info(Queue queue, nint submit) => new() { Queue = queue.Handle, FamilyIndex = SubmitFamily, SubmitFunc = submit };

    /// <summary>
    /// The family of every queue handed over: the device's graphics family.
    /// </summary>
    static uint SubmitFamily => s_submitDevice!.GraphicsFamily;

    #endregion

    #region Swapchain

    /// <summary>
    /// The device the swapchain is made on.
    /// </summary>
    readonly VulkanDevice _device;

    /// <summary>
    /// <c>ffxCreateContext</c>.
    /// </summary>
    readonly delegate* unmanaged<nint*, void*, void*, uint> _createContext;

    /// <summary>
    /// <c>ffxDestroyContext</c>.
    /// </summary>
    readonly delegate* unmanaged<nint*, void*, uint> _destroyContext;

    /// <summary>
    /// <c>ffxQuery</c>.
    /// </summary>
    readonly delegate* unmanaged<nint*, void*, uint> _query;

    /// <summary>
    /// <c>ffxConfigure</c>.
    /// </summary>
    readonly delegate* unmanaged<nint*, void*, uint> _configure;

    /// <summary>
    /// The create descriptor: it and the swapchain handle it points to stay valid as long as the context (ffx_api.h).
    /// </summary>
    readonly CreateDesc* _desc;

    /// <summary>
    /// Where the context writes the swapchain it made.
    /// </summary>
    readonly SwapchainKHR* _swapchainSlot;

    /// <summary>
    /// The swapchain context (0: none yet).
    /// </summary>
    nint _context;

    /// <summary>
    /// Whether the layer was disposed.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// The context's <c>vkCreateSwapchainKHR</c>.
    /// </summary>
    delegate* unmanaged<Device, SwapchainCreateInfoKHR*, AllocationCallbacks*, SwapchainKHR*, nint, Result> _replaceCreate;

    /// <summary>
    /// The context's <c>vkDestroySwapchainKHR</c>.
    /// </summary>
    delegate* unmanaged<Device, SwapchainKHR, AllocationCallbacks*, nint, void> _replaceDestroy;

    /// <summary>
    /// The context's <c>vkGetSwapchainImagesKHR</c>.
    /// </summary>
    delegate* unmanaged<Device, SwapchainKHR, uint*, Image*, Result> _getImages;

    /// <summary>
    /// The context's <c>vkAcquireNextImageKHR</c>.
    /// </summary>
    delegate* unmanaged<Device, SwapchainKHR, ulong, Semaphore, Fence, uint*, Result> _acquire;

    /// <summary>
    /// The context's <c>vkQueuePresentKHR</c>.
    /// </summary>
    delegate* unmanaged<Queue, PresentInfoKHR*, Result> _present;

    /// <summary>
    /// Raised at the start of <see cref="Dispose"/>, while the swapchain still exists: a frame-generation context on it is destroyed then, first, as
    /// the FFX API requires.
    /// </summary>
    internal event Action? Disposing;

    /// <summary>
    /// The swapchain the layer made last (changes on a resize or a vsync change); what the frame-generation context is configured with.
    /// </summary>
    public SwapchainKHR Swapchain { get; private set; }

    /// <summary>
    /// The swapchain's colour format as FfxApiSurfaceFormat (B8G8R8A8 or R8G8B8A8 UNORM).
    /// </summary>
    public uint SurfaceFormat { get; private set; }

    /// <summary>
    /// One line on the queues handed over, for the log.
    /// </summary>
    public string QueueLayout
    {
        get
        {
            string async = s_computeQueue.Handle == s_presentQueue.Handle ? "the present queue" : "queue 3";
            return $"game = graphics queue 0, present = queue 1, image acquire = queue 2, async compute = {async} (family {_device.GraphicsFamily})";
        }
    }

    /// <summary>
    /// True when the library exists and loads (<c>MEITOU_FFX_PATH</c> or next to the executable, as for FSR upscaling), else false with the reason.
    /// </summary>
    public static bool LibraryLoads(out nint library, out string? reason)
    {
        library = 0;
        foreach (var path in FsrUpscaler.Candidates())
        {
            if (!File.Exists(path)) continue;

            if (!NativeLibrary.TryLoad(path, out library))
            {
                reason = $"{path} does not load";
                return false;
            }

            if (!NativeLibrary.TryGetExport(library, "ffxCreateContext", out _) || !NativeLibrary.TryGetExport(library, "ffxQuery", out _))
            {
                reason = $"{path} lacks ffxCreateContext / ffxQuery";
                return false;
            }

            reason = null;
            return true;
        }

        reason = $"{FsrUpscaler.LibraryName} not found (set MEITOU_FFX_PATH or put it next to the executable)";
        return false;
    }

    /// <summary>
    /// The layer for <paramref name="device"/>, or null with the reason (library missing, too few queues).
    /// </summary>
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
            if (s_submitDevice is not null)
            {
                reason = "a swapchain layer already exists";
                return null;
            }

            s_submitDevice = device;
        }

        var extra = device.ExtraQueues;
        s_gameQueue = device.GraphicsQueue;
        s_presentQueue = extra[0];
        s_acquireQueue = extra[1];
        s_computeQueue = extra.Length > 2 ? extra[2] : extra[0];
        return new FfxSwapchain(device, library);
    }

    /// <summary>
    /// Registers (or, with a null <paramref name="ui"/>, removes) the interface picture the layer lays over every presented image, rendered and
    /// generated: premultiplied alpha, copied by the layer at the present into a buffer of its own (so the next frame may overwrite it). The layer
    /// forgets it at each present, so this is called every frame. The image must be top-down like the presented one and have TransferSrc usage.
    /// </summary>
    internal bool RegisterUi(FfxApi.Resource? ui)
    {
        if (_context == 0) return false;

        const uint premulAlpha = 1 << 0, internalDoubleBuffering = 1 << 1;
        var d = new RegisterUiDesc
        {
            Header = new ApiHeader { Type = RegisterUiType },
            Ui = ui ?? default,
            Flags = ui is null ? 0 : premulAlpha | internalDoubleBuffering,
        };
        nint c = _context;
        return _configure(&c, &d) == 0;
    }

    /// <summary>
    /// Makes the swapchain described by <paramref name="info"/> (its <c>OldSwapchain</c> is ignored: the old one is <paramref name="old"/>, destroyed
    /// here). The first call makes the FFX context; a later one destroys <paramref name="old"/> through the replacement and creates the next. False
    /// with <paramref name="why"/> on any failure; the layer is then unusable (call <see cref="Dispose"/>).
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
            if (rc != 0)
            {
                why = $"ffxCreateContext (swapchain) returned {rc}";
                return false;
            }

            _context = created;
            var q = new FunctionsQuery { Header = new ApiHeader { Type = QueryFunctionsType } };
            nint c = _context;
            rc = _query(&c, &q);
            if (rc != 0 || q.Create == 0 || q.Destroy == 0 || q.GetImages == 0 || q.Acquire == 0 || q.Present == 0)
            {
                why = $"ffxQuery (replacement functions) returned {rc}";
                return false;
            }

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
        if (r != Result.Success)
        {
            why = $"the replacement vkCreateSwapchainKHR returned {r}";
            return false;
        }

        swapchain = Swapchain = created2;
        why = null;
        return true;

        static bool Fail(out string? why, string text)
        {
            why = text;
            return false;
        }
    }

    /// <summary>
    /// The swapchain's images, through the context's <c>vkGetSwapchainImagesKHR</c>.
    /// </summary>
    public Result GetImages(Device d, SwapchainKHR swapchain, uint* count, Image* images) => _getImages(d, swapchain, count, images);

    /// <summary>
    /// Acquires the next image, through the context's <c>vkAcquireNextImageKHR</c>.
    /// </summary>
    public Result Acquire(Device d, SwapchainKHR swapchain, ulong timeout, Semaphore semaphore, Fence fence, uint* index) =>
        _acquire(d, swapchain, timeout, semaphore, fence, index);

    /// <summary>
    /// Presents, through the context's <c>vkQueuePresentKHR</c> (where frames are generated).
    /// </summary>
    public Result Present(Queue queue, PresentInfoKHR* info) => _present(queue, info);

    /// <summary>
    /// Destroys the context, which destroys the swapchain it made. Not before the device is idle.
    /// </summary>
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

        lock (LayerLock)
        {
            if (s_submitDevice == _device) s_submitDevice = null;
        }

        NativeMemory.Free(_desc);
        NativeMemory.Free(_swapchainSlot);
    }

    /// <summary>
    /// Binds the library's functions and allocates the descriptor and the swapchain slot that live with the context.
    /// </summary>
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

    #endregion
}
