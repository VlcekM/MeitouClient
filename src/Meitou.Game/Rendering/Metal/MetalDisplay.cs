using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.GLFW;
using Silk.NET.Windowing;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;

namespace Meitou.Metal;

/// <summary>
/// The window and the Metal swapchain: a GLFW window with a CAMetalLayer on its content view, presented by Metal. The
/// mirror of the Vulkan display (Meitou.Rendering.Display.VulkanDisplay) for the Metal path.
/// </summary>
public sealed unsafe class MetalDisplay : IDisposable
{
    /// <summary>The window the game draws into.</summary>
    public IWindow Window { get; }

    /// <summary>The Metal device.</summary>
    public MTLDevice Device { get; }

    /// <summary>The queue the frames are committed to.</summary>
    public MTLCommandQueue Queue { get; }

    CAMetalLayer layer;

    /// <summary>The layer the drawables come from (a struct wrapper: callers read it, the display owns its mutation).</summary>
    public CAMetalLayer Layer => layer;

    public MetalDisplay(WindowOptions options, bool vsync = true)
    {
        ObjectiveC.LinkMetal();
        Window = Silk.NET.Windowing.Window.Create(options);
        Window.Initialize();
        Device = MTLDevice.CreateSystemDefaultDevice();
        Queue = Device.NewCommandQueue();

        // NSWindow* from GLFW: Silk.NET.GLFW has no binding for it, so the export is taken from the same brew dylib the
        // engine's runtime already loads (meitou-run.sh points DYLD at it).
        var glfw = NativeLibrary.Load("libglfw.3.dylib");
        var getCocoaWindow = (delegate* unmanaged<WindowHandle*, IntPtr>)NativeLibrary.GetExport(glfw, "glfwGetCocoaWindow");
        var nsWindow = getCocoaWindow((WindowHandle*)Window.Native!.Glfw!.Value);
        this.nsWindow = nsWindow;

        // The layer has to fill the view (NSViewWidthSizable | NSViewHeightSizable) or the drawable covers only part of
        // the window; its drawable size then follows the view's bounds times the scale factor, retina included.
        var contentView = ObjectiveC.IntPtr_objc_msgSend(nsWindow, "contentView");
        this.contentView = contentView;
        ObjectiveC.objc_msgSend(nsWindow, "setPreservesContentDuringLiveResize:", true);
        ObjectiveC.objc_msgSend(contentView, "setWantsLayer:", true);
        var layerPtr = new ObjectiveCClass(ObjectiveC.objc_getClass("CAMetalLayer")).AllocInit();
        ObjectiveC.objc_msgSend(layerPtr, "setAutoresizingMask:", (byte)18);
        ObjectiveC.objc_msgSend(contentView, "setLayer:", layerPtr);
        var layer = new CAMetalLayer(layerPtr);
        layer.Device = Device;
        layer.PixelFormat = MTLPixelFormat.BGRA8Unorm;
        layer.FramebufferOnly = true;
        // Three drawables, matching MetalContext.FrameRingDepth: a fourth would let the CPU run further ahead than the
        // constants ring can safely retain, and a smaller ring (the layer's old default is also three) serialises present.
        layer.MaximumDrawableCount = MetalContext.FrameRingDepth;
        // The Metal path had no vsync switch of its own; it was always on. Follow the game's setting like VulkanDisplay.
        layer.DisplaySyncEnabled = vsync;
        layer.AllowsNextDrawableTimeout = true;
        this.layer = layer;
        Window.Resize += size => Console.WriteLine($"metal     window resize event {size.X}x{size.Y} points");
        Window.FramebufferResize += size => Console.WriteLine($"metal     framebuffer resize event {size.X}x{size.Y} pixels");
        SyncLayer();
    }

    int layerWidth, layerHeight, layerPixelWidth, layerPixelHeight;
    readonly IntPtr nsWindow;
    IntPtr contentView;

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern IntPtr MsgSendPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    static readonly Selector ValueForKey = new("valueForKey:");

    /// <summary>What AppKit sees right now; a zoom/fullscreen can change the content view or detach the layer without GLFW
    /// reporting a framebuffer size.</summary>
    public string NativeWindowState()
    {
        var view = ObjectiveC.IntPtr_objc_msgSend(nsWindow, "contentView");
        var layer = view == IntPtr.Zero ? IntPtr.Zero : ObjectiveC.IntPtr_objc_msgSend(view, "layer");
        var key = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("NSString"), "stringWithUTF8String:", "frame");
        var value = view == IntPtr.Zero ? IntPtr.Zero : MsgSendPtr(view, ValueForKey.SelPtr, key);
        var description = value == IntPtr.Zero ? IntPtr.Zero : ObjectiveC.IntPtr_objc_msgSend(value, "description");
        string frame = description == IntPtr.Zero ? "nil" : Marshal.PtrToStringUTF8(ObjectiveC.IntPtr_objc_msgSend(description, "UTF8String")) ?? "nil";
        bool zoomed = ObjectiveC.bool_objc_msgSend(nsWindow, "isZoomed");
        bool visible = ObjectiveC.bool_objc_msgSend(nsWindow, "isVisible");
        ulong style = ObjectiveC.ulong_objc_msgSend(nsWindow, "styleMask");
        return $"view {(view == contentView ? "same" : "CHANGED")}, layer {(layer == Layer.NativePtr ? "same" : "CHANGED")}, " +
            $"frame {frame}, zoomed {zoomed}, visible {visible}, style 0x{style:X}";
    }

    /// <summary>CAMetalLayer's <c>drawableSize</c> is a CGSize (two doubles): SharpMetal models it as an IntPtr, so the
    /// setter is called directly with the two doubles the ABI passes for the struct.</summary>
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern void SetDrawableSize(IntPtr layer, IntPtr selector, double width, double height);

    static readonly Selector DrawableSizeSelector = new("setDrawableSize:");

    /// <summary>
    /// Fits the layer to the window: its frame in points and its contents scale from the framebuffer's pixels. The window can
    /// change size during play, and a layer left at its first size either keeps the old drawable (a resized window shows black)
    /// or leaves the new area untouched (the desktop shows through at the edge).
    /// </summary>
    public void SyncLayer()
    {
        // A title-bar zoom (and native fullscreen) can replace the content view's layer without changing the window's
        // size; reattach ours before looking at the size, or the game keeps presenting into a detached layer.
        var view = ObjectiveC.IntPtr_objc_msgSend(nsWindow, "contentView");
        bool reattached = false;
        if (view != IntPtr.Zero && (view != contentView || ObjectiveC.IntPtr_objc_msgSend(view, "layer") != Layer.NativePtr))
        {
            contentView = view;
            ObjectiveC.objc_msgSend(view, "setWantsLayer:", true);
            ObjectiveC.objc_msgSend(Layer.NativePtr, "setAutoresizingMask:", (byte)18);
            ObjectiveC.objc_msgSend(view, "setLayer:", Layer.NativePtr);
            reattached = true;
            Console.WriteLine("metal     reattached the metal layer to the window's content view");
        }
        var size = Window.Size;
        var pixels = Window.FramebufferSize;
        if (!reattached && size.X == layerWidth && size.Y == layerHeight && pixels.X == layerPixelWidth && pixels.Y == layerPixelHeight) return;
        (layerWidth, layerHeight, layerPixelWidth, layerPixelHeight) = (size.X, size.Y, pixels.X, pixels.Y);
        if (size.X <= 0 || size.Y <= 0) return;
        // CAMetalLayer's drawable is bounds * contentsScale, not frame * contentsScale: setting only the frame during a
        // resize leaves the drawable at its old pixel size (a black or stale window after the drag).
        ObjectiveC.objc_msgSend(Layer.NativePtr, "setFrame:", new NSRect(0, 0, size.X, size.Y));
        ObjectiveC.objc_msgSend(Layer.NativePtr, "setBounds:", new NSRect(0, 0, size.X, size.Y));
        ObjectiveC.objc_msgSend(Layer.NativePtr, "setContentsScale:", pixels.X / (double)size.X);
        // CAMetalLayer keeps the drawableSize it first computed; setting bounds/contentsScale does not necessarily
        // recompute it, and a stale drawable is a black (or old-size) window after a drag.
        SetDrawableSize(Layer.NativePtr, DrawableSizeSelector.SelPtr, pixels.X, pixels.Y);
        Console.WriteLine($"metal     window {size.X}x{size.Y} points, {pixels.X}x{pixels.Y} pixels (scale {pixels.X / (double)size.X:0.##})");
    }

    /// <summary>Brings the app to the front, so a screenshot of the screen shows the window.</summary>
    public void Activate()
    {
        var app = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("NSApplication"), "sharedApplication");
        ObjectiveC.objc_msgSend(app, "activateIgnoringOtherApps:", true);
    }

    /// <summary>
    /// Borderless full screen still leaves the macOS menu bar and the Dock above the window. Hide both while the game is
    /// full screen (the window itself already covers the monitor's video mode); restore them for the windowed mode and on exit.
    /// </summary>
    public void SetFullScreenPresentation(bool fullScreen)
    {
        var app = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("NSApplication"), "sharedApplication");
        // NSApplicationPresentationHideDock | NSApplicationPresentationHideMenuBar.
        SetPresentationOptions(app, PresentationOptionsSelector.SelPtr, fullScreen ? 10UL : 0UL);
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern void SetPresentationOptions(IntPtr receiver, IntPtr selector, ulong options);

    static readonly Selector PresentationOptionsSelector = new("setPresentationOptions:");

    long noDrawableStamp;

    /// <summary>How long the last <see cref="TryBeginFrame"/> waited in <c>CAMetalLayer.nextDrawable</c> (ms). The trace
    /// separates this present/swapchain stall from the CPU time spent recording the frame.</summary>
    public double LastDrawableWaitMilliseconds { get; private set; }

    /// <summary>Begins a frame: the drawable to draw into and the command buffer to record it in.</summary>
    public bool TryBeginFrame(out MTLCommandBuffer command, out CAMetalDrawable drawable)
    {
        SyncLayer();
        command = default;
        drawable = default;
        long started = Stopwatch.GetTimestamp();
        var next = Layer.NextDrawable;
        LastDrawableWaitMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (next.DrawableID == 0)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - noDrawableStamp > Stopwatch.Frequency)
            {
                noDrawableStamp = now;
                Console.WriteLine($"metal     no drawable: window {Window.Size.X}x{Window.Size.Y} points, framebuffer {Window.FramebufferSize.X}x{Window.FramebufferSize.Y} pixels");
            }
            return false;
        }
        drawable = next;
        command = Queue.CommandBuffer();
        return true;
    }

    /// <summary>The layer's actual display-sync state (diagnostic and the game's settings).</summary>
    public bool VSync
    {
        get => layer.DisplaySyncEnabled;
        set => layer.DisplaySyncEnabled = value;
    }

    /// <summary>Opens the render pass for a frame: the drawable cleared to <paramref name="clear"/>.</summary>
    public MTLRenderCommandEncoder BeginPass(MTLCommandBuffer command, CAMetalDrawable drawable, MTLClearColor clear)
    {
        var descriptor = new MTLRenderPassDescriptor(
            ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor"));
        // The attachments come back as a struct copy, so a value-type property (the clear colour) has to be written back
        // into the array, or the pass clears to whatever the descriptor's memory held.
        var attachments = descriptor.ColorAttachments;
        var attachment = attachments.Object(0);
        attachment.Texture = drawable.Texture;
        attachment.LoadAction = MTLLoadAction.Clear;
        attachment.StoreAction = MTLStoreAction.Store;
        attachment.ClearColor = clear;
        attachments.SetObject(attachment, 0);
        return command.RenderCommandEncoder(descriptor);
    }

    /// <summary>Presents the frame and commits the command buffer.</summary>
    public void Present(MTLCommandBuffer command, CAMetalDrawable drawable)
    {
        command.PresentDrawable(drawable);
        command.Commit();
    }

    public void Dispose() => Window.Dispose();
}
