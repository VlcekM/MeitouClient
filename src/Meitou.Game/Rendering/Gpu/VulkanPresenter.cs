using System.Diagnostics;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Upscalers;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// Shows <see cref="Backbuffer"/> in a window: a swapchain on the device's surface, and at the end of each frame a blit of the backbuffer
/// into the acquired image, flipped vertically (the one flip of the GL convention the renderers draw with: row 0 is the bottom of the
/// picture, the swapchain's row 0 the top). Vsync picks FIFO, otherwise MAILBOX (else IMMEDIATE). Native since phase 8 stage 3
/// (docs/renderer-native.md 8.9): the backbuffer is a <see cref="Texture"/> of its own and the frames are <see cref="GpuContext"/>'s.
/// </summary>
public sealed unsafe class VulkanPresenter : IDisposable
{
    #region State

    /// <summary>
    /// The device with the window's surface.
    /// </summary>
    readonly VulkanDevice _device;
    /// <summary>
    /// The context whose frame the present is recorded into.
    /// </summary>
    readonly GpuContext _ctx;
    /// <summary>
    /// The Vulkan API entry points.
    /// </summary>
    readonly Vk _vk;
    /// <summary>
    /// The VK_KHR_swapchain functions.
    /// </summary>
    readonly KhrSwapchain _khr;
    /// <summary>
    /// The swapchain; 0 before the first frame.
    /// </summary>
    SwapchainKHR _swapchain;
    /// <summary>
    /// The swapchain's images.
    /// </summary>
    Image[] _images = [];
    /// <summary>
    /// The swapchain images' format.
    /// </summary>
    Format _format;
    /// <summary>
    /// The swapchain images' size.
    /// </summary>
    Extent2D _extent;
    /// <summary>
    /// Signalled when the frame slot's image is acquired; one per frame slot.
    /// </summary>
    readonly VkSemaphore[] _acquired;
    /// <summary>
    /// Signalled when an image's rendering is done (the present waits on it); one per swapchain image.
    /// </summary>
    VkSemaphore[] _rendered = [];
    /// <summary>
    /// The swapchain image acquired for this frame.
    /// </summary>
    uint _imageIndex;
    /// <summary>
    /// Whether an image is acquired and not yet presented.
    /// </summary>
    bool _acquiredImage;
    /// <summary>
    /// The window size the swapchain was made for (another size recreates it).
    /// </summary>
    int _wantWidth, _wantHeight;
    /// <summary>
    /// Vsync (FIFO), and whether the swapchain must be recreated at the next frame (true until the first).
    /// </summary>
    bool _vsync, _stale = true;

    #endregion

    #region Properties

    /// <summary>
    /// Vsync (FIFO); changing it recreates the swapchain at the next frame.
    /// </summary>
    public bool VSync
    {
        get => _vsync;
        set
        {
            if (_vsync == value) return;
            _vsync = value;
            _stale = true;
        }
    }

    /// <summary>
    /// Presents through this function instead of the driver's <c>vkQueuePresentKHR</c> (Streamline's proxy, which it must see every frame).
    /// </summary>
    public delegate* unmanaged<Queue, PresentInfoKHR*, Result> PresentFunction { get; set; }

    /// <summary>
    /// AMD's frame-interpolation swapchain, when the Frame generation setting is on and it works: the swapchain is created, acquired from and
    /// presented through it (frames are generated on top of it by <see cref="Upscalers.FrameGeneration"/>). Null for the plain swapchain; set
    /// to null again, with a log line, when it fails.
    /// </summary>
    public FfxSwapchain? FrameGeneration { get; set; }

    /// <summary>
    /// Streamline's swapchain calls (DLSS frame generation, which owns the real swapchain and presents from its own queue): set before the
    /// first frame.
    /// </summary>
    public Streamline.SwapchainHooks? StreamlineHooks { get; set; }

    /// <summary>
    /// What the frame is drawn into (RGBA8, the window's framebuffer size; made again when the size changes): valid after <see cref="BeginFrame"/>.
    /// </summary>
    public GpuTexture? Backbuffer { get; private set; }

    /// <summary>
    /// Asks for an HDR10 swapchain (read at each swapchain creation; needs VK_EXT_swapchain_colorspace, not with <see cref="FrameGeneration"/>).
    /// </summary>
    public bool WantHdr { get; init; }

    /// <summary>
    /// The swapchain is HDR10: the backbuffer is RGBA16F and the host presents PQ pictures (<see cref="UiLayer.CompositeHdr"/>).
    /// </summary>
    public bool HdrActive { get; private set; }

    /// <summary>
    /// Stopwatch ticks spent acquiring swapchain images, since the presenter was made.
    /// </summary>
    public long AcquireTicks { get; private set; }
    /// <summary>
    /// Stopwatch ticks spent presenting, since the presenter was made.
    /// </summary>
    public long PresentTicks { get; private set; }

    #endregion

    #region Construction

    /// <summary>
    /// A presenter for the context's device, which must have a surface; the swapchain is made at the first frame.
    /// </summary>
    public VulkanPresenter(GpuContext ctx, bool vsync)
    {
        _device = ctx.Device;
        if (!_device.HasSurface) throw new InvalidOperationException("the device has no surface");
        this._ctx = ctx;
        this._vsync = vsync;
        _vk = _device.Vk;
        if (!_vk.TryGetDeviceExtension(_device.Instance, _device.Device, out _khr))
            throw new InvalidOperationException("VK_KHR_swapchain missing");
        _acquired = new VkSemaphore[_device.Frames.Count];
        for (int i = 0; i < _acquired.Length; i++) _acquired[i] = NewSemaphore();
    }

    /// <summary>
    /// A new binary semaphore.
    /// </summary>
    VkSemaphore NewSemaphore()
    {
        var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        VulkanException.Check(_vk.CreateSemaphore(_device.Device, &info, null, out var s), "vkCreateSemaphore");
        return s;
    }

    #endregion

    #region Frame

    /// <summary>
    /// Starts a frame of <paramref name="width"/> × <paramref name="height"/> (the window's framebuffer): the frame draws into
    /// <see cref="Backbuffer"/>. False when the window has no area (minimised): skip the frame.
    /// </summary>
    public bool BeginFrame(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        if (_stale || width != _wantWidth || height != _wantHeight) Recreate(width, height);
        var backbufferFormat = HdrActive ? InternalFormat.Rgba16F : InternalFormat.Rgba8;
        if (Backbuffer is not { } b || b.Desc.Width != width || b.Desc.Height != height || b.Desc.Format != backbufferFormat)
        {
            Backbuffer?.Dispose();   // released after the frames in flight
            Backbuffer = _ctx.CreateTexture(new TextureDesc(backbufferFormat, width, height,
                Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.TransferDst | TextureUse.Sampled, Name: "backbuffer"));
        }

        _ctx.BeginFrame();

        // The semaphore of this frame slot: its previous use was waited on by the slot's previous submission, whose fence BeginFrame waited for.
        var semaphore = _acquired[_device.Frames.Slot];
        long acquireStart = Stopwatch.GetTimestamp();
        Result r;
        if (FrameGeneration is { } fg)
        {
            fixed (uint* pi = &_imageIndex) r = fg.Acquire(_device.Device, _swapchain, ulong.MaxValue, semaphore, default, pi);
        }
        else if (StreamlineHooks is { } sl)
        {
            fixed (uint* pi = &_imageIndex) r = sl.Acquire(_device.Device, _swapchain, ulong.MaxValue, semaphore, default, pi);
        }
        else r = _khr.AcquireNextImage(_device.Device, _swapchain, ulong.MaxValue, semaphore, default, ref _imageIndex);
        AcquireTicks += Stopwatch.GetTimestamp() - acquireStart;
        if (r is Result.ErrorOutOfDateKhr)
        {
            _stale = true;
            _acquiredImage = false;
            return true;   // draw anyway; nothing is shown
        }

        if (r is not (Result.Success or Result.SuboptimalKhr)) _device.Check(r, "vkAcquireNextImageKHR");
        if (r == Result.SuboptimalKhr) _stale = true;
        _acquiredImage = true;
        return true;
    }

    /// <summary>
    /// Copies the backbuffer into the swapchain image (flipped), or <paramref name="topDown"/> (a finished picture whose row 0 is the top) as it
    /// is, submits the frame and presents it.
    /// </summary>
    public void Present(GpuTexture? topDown = null)
    {
        StageClock.BeginSubmission();
        if (!_acquiredImage || (topDown ?? Backbuffer) is not { } src)
        {
            _ctx.EndFrame();
            return;
        }

        var dst = _images[_imageIndex];
        var list = (CommandList)_ctx.BeginNative("present");
        var cb = list.Handle;
        RecordBlit(cb, dst, src, topDown);
        _ctx.EndNative(list);
        var wait = _acquired[_device.Frames.Slot];
        var signal = _rendered[_imageIndex];
        var stage = PipelineStageFlags.TransferBit;
        _ctx.EndFrame([wait], [stage], [signal]);
        PresentImage(signal);
    }

    /// <summary>
    /// Records the blit of <paramref name="src"/> into the swapchain image <paramref name="dst"/> (flipped unless <paramref name="topDown"/> is
    /// given) and the barrier that hands the image on.
    /// </summary>
    void RecordBlit(CommandBuffer cb, Image dst, GpuTexture src, GpuTexture? topDown)
    {
        int w = src.Desc.Width, h = src.Desc.Height;

        // The source stage is the one the acquire semaphore is waited at (below): the layout change then waits for the image to be released.
        Barrier(cb, dst, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.None,
            PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit);
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        blit.SrcOffsets[0] = new Offset3D(0, 0, 0);
        blit.SrcOffsets[1] = new Offset3D(w, h, 1);

        // The backbuffer is bottom-up: flipped, its row 0 (the bottom) goes to the swapchain's last row. A top-down picture keeps its order.
        bool flip = topDown is null;
        blit.DstOffsets[0] = new Offset3D(0, flip ? (int)_extent.Height : 0, 0);
        blit.DstOffsets[1] = new Offset3D((int)_extent.Width, flip ? 0 : (int)_extent.Height, 1);
        _vk.CmdBlitImage(cb, _ctx.Device.NativeImage(src), ImageLayout.General, dst, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Nearest);

        // Through AMD's swapchain the image is a replacement back buffer that its UI composition samples (it expects shader-read layout) and
        // it moves the real swapchain image to present itself; the next frame's Undefined -> TransferDst barrier discards the contents either way.
        if (FrameGeneration is not null)
        {
            Barrier(cb, dst, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags2.BlitBit,
                AccessFlags2.TransferWriteBit, PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit);
        }
        else
        {
            Barrier(cb, dst, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr, PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.BottomOfPipeBit, AccessFlags2.None);
        }
    }

    /// <summary>
    /// Presents the acquired image once <paramref name="signal"/> (its render-finished semaphore) is signalled, through the layer, the
    /// Streamline proxy or the driver.
    /// </summary>
    void PresentImage(VkSemaphore signal)
    {
        var sc = _swapchain;
        uint index = _imageIndex;
        var info = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signal,
            SwapchainCount = 1,
            PSwapchains = &sc,
            PImageIndices = &index,
        };
        Result r;
        long presentStart = Stopwatch.GetTimestamp();

        // The frame-interpolation layer takes the device's queue lock itself where it submits on the game queue (its submit function); holding it
        // here while the layer waits for its presenter thread could deadlock against that.
        if (FrameGeneration is { } fg) r = fg.Present(_device.GraphicsQueue, &info);
        else
        {
            lock (_device.QueueLock)
                r = PresentFunction != null ? PresentFunction(_device.GraphicsQueue, &info) : _khr.QueuePresent(_device.GraphicsQueue, &info);
        }
        PresentTicks += Stopwatch.GetTimestamp() - presentStart;
        StageClock.Lap(StageClock.DriverWait, gpuTimestamp: false);
        if (r is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr) _stale = true;
        else _device.Check(r, "vkQueuePresentKHR");
        _acquiredImage = false;
    }

    /// <summary>
    /// Records one image barrier with a layout transition.
    /// </summary>
    void Barrier(CommandBuffer cb, Image image, ImageLayout from, ImageLayout to, PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
        PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
    {
        var b = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = from,
            NewLayout = to,
            Image = image,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        var dep = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &b };
        _vk.CmdPipelineBarrier2(cb, &dep);
    }

    #endregion

    #region Swapchain

    /// <summary>
    /// Makes the swapchain for the window's size: the surface's format, present mode and extent, through the frame-generation layer or
    /// Streamline when one is on, else the driver's own.
    /// </summary>
    void Recreate(int width, int height)
    {
        _device.WaitIdle();
        var caps = QuerySurfaceCapabilities();
        var chosen = ChooseSurfaceFormat();
        var mode = ChoosePresentMode();
        _extent = ChooseExtent(caps, width, height);
        uint count = Math.Max(caps.MinImageCount + 1, 3);
        if (caps.MaxImageCount > 0) count = Math.Min(count, caps.MaxImageCount);
        var old = _swapchain;
        bool layer = FrameGeneration is not null || StreamlineHooks is not null;
        var info = SwapchainInfo(caps, chosen, mode, count, layer, old);
        if (FrameGeneration is { } fg)
        {
            bool ok;
            string? why;
            try
            {
                ok = fg.Create(info, old, out _swapchain, out why);
            }
            catch (Exception e)
            {
                (ok, why, _swapchain) = (false, e.Message, default);
            }

            if (ok)
            {
                Console.WriteLine($"frame generation  swapchain made through the FFX layer ({fg.QueueLayout})");
            }
            else
            {
                // Back to the plain swapchain: the layer (and the swapchain it made) goes first. Nothing is in flight (WaitIdle above).
                Console.WriteLine($"frame generation  unavailable, plain swapchain instead: {why}");
                try
                {
                    fg.Dispose();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"frame generation  {e.Message}");
                }

                FrameGeneration = null;
                old = default;
                info.OldSwapchain = default;
                info.ImageUsage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.ColorAttachmentBit;
            }
        }

        if (FrameGeneration is null && StreamlineHooks is { } slc)
        {
            SwapchainKHR made;
            VulkanException.Check(slc.Create(_device.Device, &info, null, &made), "vkCreateSwapchainKHR (Streamline)");
            _swapchain = made;
            if (old.Handle != 0) slc.Destroy(_device.Device, old, null);
        }
        else if (FrameGeneration is null)
        {
            VulkanException.Check(_khr.CreateSwapchain(_device.Device, &info, null, out _swapchain), "vkCreateSwapchainKHR");
            if (old.Handle != 0) _khr.DestroySwapchain(_device.Device, old, null);
        }

        uint imageCount = LoadSwapchainImages();
        RecreateRenderedSemaphores(imageCount);
        _format = chosen.Format;
        (_wantWidth, _wantHeight) = (width, height);
        _stale = false;
        Console.WriteLine($"swapchain {_extent.Width}x{_extent.Height} {_format}{(HdrActive ? " HDR10" : "")}, {imageCount} images, {mode}");
    }

    /// <summary>
    /// The surface's capabilities: the extent and image count limits and the current transform.
    /// </summary>
    SurfaceCapabilitiesKHR QuerySurfaceCapabilities()
    {
        var surfaceExt = _device.SurfaceExtension!;
        var surface = _device.Surface;
        var pd = _device.PhysicalDevice;
        var result = surfaceExt.GetPhysicalDeviceSurfaceCapabilities(pd, surface, out var caps);
        VulkanException.Check(result, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        return caps;

    }

    /// <summary>
    /// The surface's first UNORM format, or its first format when none is UNORM.
    /// </summary>
    SurfaceFormatKHR ChooseSurfaceFormat()
    {
        var surfaceExt = _device.SurfaceExtension!;
        var surface = _device.Surface;
        var pd = _device.PhysicalDevice;
        uint n = 0;
        surfaceExt.GetPhysicalDeviceSurfaceFormats(pd, surface, ref n, null);
        var formats = new SurfaceFormatKHR[n];
        fixed (SurfaceFormatKHR* pf = formats) surfaceExt.GetPhysicalDeviceSurfaceFormats(pd, surface, ref n, pf);

        // A UNORM format: the backbuffer holds display-ready values already (no sRGB encoding, as GL's default framebuffer did not).
        var chosen = formats.FirstOrDefault(f => f.Format is Format.B8G8R8A8Unorm or Format.R8G8B8A8Unorm, formats[0]);

        // HDR10 where the display offers it (Windows: HDR turned on for that monitor); the SDR format otherwise.
        var hdr = formats.FirstOrDefault(IsHdr10Format);
        HdrActive = WantHdr && FrameGeneration is null && hdr.ColorSpace == ColorSpaceKHR.SpaceHdr10ST2084Ext;
        return HdrActive ? hdr : chosen;
    }

    /// <summary>
    /// Whether a surface format is HDR10: the ST 2084 colour space in a 10-bit UNORM format.
    /// </summary>
    static bool IsHdr10Format(SurfaceFormatKHR f) =>
        f.ColorSpace == ColorSpaceKHR.SpaceHdr10ST2084Ext && f.Format is Format.A2B10G10R10UnormPack32 or Format.A2R10G10B10UnormPack32;

    /// <summary>
    /// FIFO with vsync, else MAILBOX, else IMMEDIATE when the surface has them.
    /// </summary>
    PresentModeKHR ChoosePresentMode()
    {
        var surfaceExt = _device.SurfaceExtension!;
        var surface = _device.Surface;
        var pd = _device.PhysicalDevice;
        uint n = 0;
        surfaceExt.GetPhysicalDeviceSurfacePresentModes(pd, surface, ref n, null);
        var modes = new PresentModeKHR[n];
        fixed (PresentModeKHR* pm = modes) surfaceExt.GetPhysicalDeviceSurfacePresentModes(pd, surface, ref n, pm);
        return _vsync ? PresentModeKHR.FifoKhr
            : modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr
            : modes.Contains(PresentModeKHR.ImmediateKhr) ? PresentModeKHR.ImmediateKhr : PresentModeKHR.FifoKhr;
    }

    /// <summary>
    /// The surface's own extent when it names one, else the window's size clamped to the surface's limits.
    /// </summary>
    static Extent2D ChooseExtent(SurfaceCapabilitiesKHR caps, int width, int height) =>
        caps.CurrentExtent.Width != uint.MaxValue ? caps.CurrentExtent
            : new Extent2D(Math.Clamp((uint)width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Math.Clamp((uint)height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));

    /// <summary>
    /// The swapchain description; the transfer source usage is added when a layer (frame generation or Streamline) reads the images.
    /// </summary>
    SwapchainCreateInfoKHR SwapchainInfo(SurfaceCapabilitiesKHR caps, SurfaceFormatKHR chosen, PresentModeKHR mode, uint count, bool layer,
        SwapchainKHR old)
    {
        return new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _device.Surface,
            MinImageCount = count,
            ImageFormat = chosen.Format,
            ImageColorSpace = chosen.ColorSpace,
            ImageExtent = _extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.ColorAttachmentBit
                | (layer ? ImageUsageFlags.TransferSrcBit : 0),
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = mode,
            Clipped = true,
            OldSwapchain = old,
        };
    }

    /// <summary>
    /// Fetches the images of the current swapchain (through the layer or the driver) and returns how many there are.
    /// </summary>
    uint LoadSwapchainImages()
    {
        uint imageCount = 0;
        if (FrameGeneration is null && StreamlineHooks is { } sli)
        {
            VulkanException.Check(sli.GetImages(_device.Device, _swapchain, &imageCount, null), "vkGetSwapchainImagesKHR (Streamline)");
            _images = new Image[imageCount];
            fixed (Image* pi = _images)
                VulkanException.Check(sli.GetImages(_device.Device, _swapchain, &imageCount, pi), "vkGetSwapchainImagesKHR (Streamline)");
        }
        else if (FrameGeneration is { } fgi)
        {
            VulkanException.Check(fgi.GetImages(_device.Device, _swapchain, &imageCount, null), "vkGetSwapchainImagesKHR (FFX)");
            _images = new Image[imageCount];
            fixed (Image* pi = _images)
                VulkanException.Check(fgi.GetImages(_device.Device, _swapchain, &imageCount, pi), "vkGetSwapchainImagesKHR (FFX)");
        }
        else
        {
            _khr.GetSwapchainImages(_device.Device, _swapchain, ref imageCount, null);
            _images = new Image[imageCount];
            fixed (Image* pi = _images) _khr.GetSwapchainImages(_device.Device, _swapchain, ref imageCount, pi);
        }

        return imageCount;
    }

    /// <summary>
    /// Replaces the render-finished semaphores: one per swapchain image.
    /// </summary>
    void RecreateRenderedSemaphores(uint imageCount)
    {
        foreach (var s in _rendered) _vk.DestroySemaphore(_device.Device, s, null);
        _rendered = new VkSemaphore[imageCount];
        for (int i = 0; i < _rendered.Length; i++) _rendered[i] = NewSemaphore();
    }

    #endregion

    #region Lifetime

    /// <summary>
    /// Waits for the device to go idle, then releases the backbuffer, the semaphores and the swapchain (through frame generation or Streamline when
    /// they made it).
    /// </summary>
    public void Dispose()
    {
        _device.WaitIdle();
        Backbuffer?.Dispose();
        Backbuffer = null;
        foreach (var s in _rendered) _vk.DestroySemaphore(_device.Device, s, null);
        foreach (var s in _acquired) _vk.DestroySemaphore(_device.Device, s, null);
        if (FrameGeneration is { } fg)
        {
            fg.Dispose();   // destroys the swapchain it made
            FrameGeneration = null;
        }
        else if (_swapchain.Handle != 0 && StreamlineHooks is { } sl) sl.Destroy(_device.Device, _swapchain, null);
        else if (_swapchain.Handle != 0) _khr.DestroySwapchain(_device.Device, _swapchain, null);
    }

    #endregion
}