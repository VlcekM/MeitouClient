using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Meitou.Rendering.Vulkan;

/// <summary>
/// Shows <see cref="VkGl"/>'s framebuffer 0 in a window: a swapchain on the device's surface, and at the end of each frame a
/// blit of the backbuffer into the acquired image, flipped vertically (the one flip of the GL-on-Vulkan convention: GL's row 0 is
/// the bottom of the window, Vulkan's swapchain row 0 the top). Vsync picks FIFO, otherwise MAILBOX (else IMMEDIATE).
/// </summary>
public sealed unsafe class VulkanPresenter : IDisposable
{
    readonly VulkanDevice device;
    readonly VkGl gl;
    readonly Vk vk;
    readonly KhrSwapchain khr;
    SwapchainKHR swapchain;
    Image[] images = [];
    Format format;
    Extent2D extent;
    readonly VkSemaphore[] acquired;   // per frame slot
    VkSemaphore[] rendered = [];       // per swapchain image
    uint imageIndex;
    bool acquiredImage;
    int wantWidth, wantHeight;

    /// <summary>Vsync (FIFO); changing it recreates the swapchain at the next frame.</summary>
    public bool VSync { get => vsync; set { if (vsync != value) { vsync = value; stale = true; } } }
    bool vsync, stale = true;

    public VulkanPresenter(VulkanDevice device, VkGl gl, bool vsync)
    {
        if (!device.HasSurface) throw new InvalidOperationException("the device has no surface");
        this.device = device;
        this.gl = gl;
        this.vsync = vsync;
        vk = device.Vk;
        if (!vk.TryGetDeviceExtension(device.Instance, device.Device, out khr)) throw new InvalidOperationException("VK_KHR_swapchain missing");
        acquired = new VkSemaphore[device.Frames.Count];
        for (int i = 0; i < acquired.Length; i++) acquired[i] = NewSemaphore();
    }

    VkSemaphore NewSemaphore()
    {
        var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        VkGl.Check(vk.CreateSemaphore(device.Device, &info, null, out var s));
        return s;
    }

    /// <summary>Starts a frame of <paramref name="width"/> × <paramref name="height"/> (the window's framebuffer): GL calls after this
    /// draw into it. False when the window has no area (minimised): skip the frame.</summary>
    public bool BeginFrame(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        if (stale || width != wantWidth || height != wantHeight) Recreate(width, height);
        gl.BeginFrame(width, height);
        // The semaphore of this frame slot: its previous use was waited on by the slot's previous submission, whose fence BeginFrame waited for.
        var semaphore = acquired[device.Frames.Slot];
        var r = khr.AcquireNextImage(device.Device, swapchain, ulong.MaxValue, semaphore, default, ref imageIndex);
        if (r is Result.ErrorOutOfDateKhr) { stale = true; acquiredImage = false; return true; }   // draw anyway; nothing is shown
        if (r is not (Result.Success or Result.SuboptimalKhr)) VkGl.Check(r);
        if (r == Result.SuboptimalKhr) stale = true;
        acquiredImage = true;
        return true;
    }

    /// <summary>Copies framebuffer 0 into the swapchain image (flipped), submits the frame and presents it.</summary>
    public void Present()
    {
        if (!acquiredImage) { gl.EndFrame(); return; }
        var (src, w, h) = gl.Backbuffer;
        var dst = images[imageIndex];
        gl.RecordInFrame(cb =>
        {
            Barrier(cb, dst, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.None,
                PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            };
            blit.SrcOffsets[0] = new Offset3D(0, 0, 0);
            blit.SrcOffsets[1] = new Offset3D(w, h, 1);
            // Flipped: GL row 0 (the bottom) goes to the swapchain's last row.
            blit.DstOffsets[0] = new Offset3D(0, (int)extent.Height, 0);
            blit.DstOffsets[1] = new Offset3D((int)extent.Width, 0, 1);
            vk.CmdBlitImage(cb, src, ImageLayout.General, dst, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Nearest);
            Barrier(cb, dst, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr, PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.BottomOfPipeBit, AccessFlags2.None);
        });
        var wait = acquired[device.Frames.Slot];
        var signal = rendered[imageIndex];
        var stage = PipelineStageFlags.TransferBit;
        gl.EndFrame([wait], [stage], [signal]);
        var sc = swapchain;
        uint index = imageIndex;
        var info = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1, PWaitSemaphores = &signal,
            SwapchainCount = 1, PSwapchains = &sc, PImageIndices = &index,
        };
        Result r;
        lock (device.QueueLock) r = khr.QueuePresent(device.GraphicsQueue, &info);
        if (r is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr) stale = true;
        else VkGl.Check(r);
        acquiredImage = false;
    }

    void Barrier(CommandBuffer cb, Image image, ImageLayout from, ImageLayout to, PipelineStageFlags2 srcStage, AccessFlags2 srcAccess, PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
    {
        var b = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage, SrcAccessMask = srcAccess, DstStageMask = dstStage, DstAccessMask = dstAccess,
            OldLayout = from, NewLayout = to, Image = image,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        var dep = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &b };
        vk.CmdPipelineBarrier2(cb, &dep);
    }

    void Recreate(int width, int height)
    {
        device.WaitIdle();
        var surfaceExt = device.SurfaceExtension!;
        var surface = device.Surface;
        var pd = device.PhysicalDevice;
        VkGl.Check(surfaceExt.GetPhysicalDeviceSurfaceCapabilities(pd, surface, out var caps));
        uint n = 0;
        surfaceExt.GetPhysicalDeviceSurfaceFormats(pd, surface, ref n, null);
        var formats = new SurfaceFormatKHR[n];
        fixed (SurfaceFormatKHR* pf = formats) surfaceExt.GetPhysicalDeviceSurfaceFormats(pd, surface, ref n, pf);
        // A UNORM format: the backbuffer holds display-ready values already (GL's default framebuffer is not sRGB-encoding either).
        var chosen = formats.FirstOrDefault(f => f.Format is Format.B8G8R8A8Unorm or Format.R8G8B8A8Unorm, formats[0]);
        surfaceExt.GetPhysicalDeviceSurfacePresentModes(pd, surface, ref n, null);
        var modes = new PresentModeKHR[n];
        fixed (PresentModeKHR* pm = modes) surfaceExt.GetPhysicalDeviceSurfacePresentModes(pd, surface, ref n, pm);
        var mode = vsync ? PresentModeKHR.FifoKhr
            : modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr
            : modes.Contains(PresentModeKHR.ImmediateKhr) ? PresentModeKHR.ImmediateKhr : PresentModeKHR.FifoKhr;
        extent = caps.CurrentExtent.Width != uint.MaxValue ? caps.CurrentExtent
            : new Extent2D(Math.Clamp((uint)width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width), Math.Clamp((uint)height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
        uint count = Math.Max(caps.MinImageCount + 1, 3);
        if (caps.MaxImageCount > 0) count = Math.Min(count, caps.MaxImageCount);
        var old = swapchain;
        var info = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = surface, MinImageCount = count, ImageFormat = chosen.Format, ImageColorSpace = chosen.ColorSpace,
            ImageExtent = extent, ImageArrayLayers = 1, ImageUsage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive, PreTransform = caps.CurrentTransform, CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = mode, Clipped = true, OldSwapchain = old,
        };
        VkGl.Check(khr.CreateSwapchain(device.Device, &info, null, out swapchain));
        if (old.Handle != 0) khr.DestroySwapchain(device.Device, old, null);
        uint imageCount = 0;
        khr.GetSwapchainImages(device.Device, swapchain, ref imageCount, null);
        images = new Image[imageCount];
        fixed (Image* pi = images) khr.GetSwapchainImages(device.Device, swapchain, ref imageCount, pi);
        foreach (var s in rendered) vk.DestroySemaphore(device.Device, s, null);
        rendered = new VkSemaphore[imageCount];
        for (int i = 0; i < rendered.Length; i++) rendered[i] = NewSemaphore();
        format = chosen.Format;
        (wantWidth, wantHeight) = (width, height);
        stale = false;
        Console.WriteLine($"swapchain {extent.Width}x{extent.Height} {format}, {imageCount} images, {mode}");
    }

    public void Dispose()
    {
        device.WaitIdle();
        foreach (var s in rendered) vk.DestroySemaphore(device.Device, s, null);
        foreach (var s in acquired) vk.DestroySemaphore(device.Device, s, null);
        if (swapchain.Handle != 0) khr.DestroySwapchain(device.Device, swapchain, null);
    }
}
