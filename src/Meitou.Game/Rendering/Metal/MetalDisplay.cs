using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.GLFW;
using Silk.NET.Windowing;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// The window and the Metal swapchain: a GLFW window with a CAMetalLayer on its content view, presented by Metal. The
/// mirror of the Vulkan display (Meitou.Game.Rendering.Display.VulkanDisplay) for the Metal path.
/// </summary>
public sealed unsafe class MetalDisplay : IDisposable
{
    #region Window and layer

    /// <summary>
    /// The layer on the window's content view (backs <see cref="Layer"/>).
    /// </summary>
    CAMetalLayer _layer;

    /// <summary>
    /// The window size in points and the framebuffer size in pixels the layer was last fitted to.
    /// </summary>
    int _layerWidth, _layerHeight, _layerPixelWidth, _layerPixelHeight;
    /// <summary>
    /// The window's NSWindow (from GLFW).
    /// </summary>
    readonly IntPtr _nsWindow;
    /// <summary>
    /// The content view the layer is attached to (replaced when AppKit swaps the view).
    /// </summary>
    IntPtr _contentView;

    /// <summary>
    /// The <c>valueForKey:</c> selector.
    /// </summary>
    static readonly Selector ValueForKey = new("valueForKey:");

    /// <summary>
    /// The <c>setDrawableSize:</c> selector.
    /// </summary>
    static readonly Selector DrawableSizeSelector = new("setDrawableSize:");

    /// <summary>
    /// The <c>toggleFullScreen:</c> selector.
    /// </summary>
    static readonly Selector ToggleFullScreenSelector = new("toggleFullScreen:");

    /// <summary>
    /// The <c>setBackgroundColor:</c> selector.
    /// </summary>
    static readonly Selector BackgroundColorSelector = new("setBackgroundColor:");

    /// <summary>
    /// The <c>setCollectionBehavior:</c> selector.
    /// </summary>
    static readonly Selector CollectionBehaviorSelector = new("setCollectionBehavior:");

    /// <summary>
    /// NSWindowCollectionBehaviorManaged | NSWindowCollectionBehaviorFullScreenPrimary: the window may take a full-screen Space of its own (the
    /// green button, Ctrl+Cmd+F, <see cref="SetFullScreen"/>).
    /// </summary>
    const ulong FullScreenPrimaryBehavior = (1UL << 2) | (1UL << 7);

    /// <summary>
    /// NSWindowStyleMaskFullScreen: set in the style mask while the window is in its full-screen Space.
    /// </summary>
    const ulong FullScreenStyleMask = 1UL << 14;

    /// <summary>
    /// How long the state last asked for is reported in place of AppKit's: the change into or out of the Space is animated, and the style mask
    /// follows only during it, so a setting read every frame would otherwise ask again.
    /// </summary>
    static readonly TimeSpan FullScreenTransition = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The full-screen state last asked for through <see cref="SetFullScreen"/>.
    /// </summary>
    bool _fullScreenWanted;

    /// <summary>
    /// When <see cref="_fullScreenWanted"/> was asked for (Stopwatch ticks; 0 before any request).
    /// </summary>
    long _fullScreenAskedStamp;

    /// <summary>
    /// <see cref="FullScreenTransitions.Count"/> when <see cref="_fullScreenWanted"/> was asked for.
    /// </summary>
    int _transitionsAtRequest;

    /// <summary>
    /// The window the game draws into.
    /// </summary>
    public IWindow Window { get; }

    /// <summary>
    /// The Metal device.
    /// </summary>
    public MTLDevice Device { get; }

    /// <summary>
    /// The queue the frames are committed to.
    /// </summary>
    public MTLCommandQueue Queue { get; }

    /// <summary>
    /// The layer the drawables come from (a struct wrapper: callers read it, the display owns its mutation).
    /// </summary>
    public CAMetalLayer Layer => _layer;

    /// <summary>
    /// Creates the window, the system default device and its queue, and a CAMetalLayer (BGRA8, three drawables, display sync as
    /// <paramref name="vsync"/>) filling the window's content view.
    /// </summary>
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
        this._nsWindow = nsWindow;
        SendUInteger(nsWindow, CollectionBehaviorSelector.SelPtr, FullScreenPrimaryBehavior);
        FullScreenTransitions.Observe(nsWindow);

        // Black, not AppKit's grey, wherever the layer has no picture yet: the moment between the window showing and its first frame, and the
        // edge a resize or the full-screen animation uncovers before the next frame.
        var black = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("NSColor"), "blackColor");
        MsgSendPtr(nsWindow, BackgroundColorSelector.SelPtr, black);

        // The layer has to fill the view (NSViewWidthSizable | NSViewHeightSizable) or the drawable covers only part of
        // the window; its drawable size then follows the view's bounds times the scale factor, retina included.
        var contentView = ObjectiveC.IntPtr_objc_msgSend(nsWindow, "contentView");
        this._contentView = contentView;
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
        this._layer = layer;
        Window.Resize += size => Console.WriteLine($"metal     window resize event {size.X}x{size.Y} points");
        Window.FramebufferResize += size => Console.WriteLine($"metal     framebuffer resize event {size.X}x{size.Y} pixels");
        SyncLayer();
    }

    /// <summary>
    /// What AppKit sees right now; a zoom/fullscreen can change the content view or detach the layer without GLFW reporting a framebuffer size.
    /// </summary>
    public string NativeWindowState()
    {
        var view = ObjectiveC.IntPtr_objc_msgSend(_nsWindow, "contentView");
        var layer = view == IntPtr.Zero ? IntPtr.Zero : ObjectiveC.IntPtr_objc_msgSend(view, "layer");
        var key = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("NSString"), "stringWithUTF8String:", "frame");
        var value = view == IntPtr.Zero ? IntPtr.Zero : MsgSendPtr(view, ValueForKey.SelPtr, key);
        var description = value == IntPtr.Zero ? IntPtr.Zero : ObjectiveC.IntPtr_objc_msgSend(value, "description");
        string frame = description == IntPtr.Zero ? "nil" : Marshal.PtrToStringUTF8(ObjectiveC.IntPtr_objc_msgSend(description, "UTF8String")) ?? "nil";
        bool zoomed = ObjectiveC.bool_objc_msgSend(_nsWindow, "isZoomed");
        bool visible = ObjectiveC.bool_objc_msgSend(_nsWindow, "isVisible");
        ulong style = ObjectiveC.ulong_objc_msgSend(_nsWindow, "styleMask");
        return $"view {(view == _contentView ? "same" : "CHANGED")}, layer {(layer == Layer.NativePtr ? "same" : "CHANGED")}, " +
            $"frame {frame}, zoomed {zoomed}, visible {visible}, style 0x{style:X}";
    }

    /// <summary>
    /// Fits the layer to the window: its frame in points and its contents scale from the framebuffer's pixels. The window can
    /// change size during play, and a layer left at its first size either keeps the old drawable (a resized window shows black)
    /// or leaves the new area untouched (the desktop shows through at the edge).
    /// </summary>
    public void SyncLayer()
    {
        // A title-bar zoom (and native fullscreen) can replace the content view's layer without changing the window's
        // size; reattach ours before looking at the size, or the game keeps presenting into a detached layer.
        var view = ObjectiveC.IntPtr_objc_msgSend(_nsWindow, "contentView");
        bool reattached = false;
        if (view != IntPtr.Zero && (view != _contentView || ObjectiveC.IntPtr_objc_msgSend(view, "layer") != Layer.NativePtr))
        {
            _contentView = view;
            ObjectiveC.objc_msgSend(view, "setWantsLayer:", true);
            ObjectiveC.objc_msgSend(Layer.NativePtr, "setAutoresizingMask:", (byte)18);
            ObjectiveC.objc_msgSend(view, "setLayer:", Layer.NativePtr);
            reattached = true;
            Console.WriteLine("metal     reattached the metal layer to the window's content view");
        }
        var size = Window.Size;
        var pixels = Window.FramebufferSize;
        if (!reattached && size.X == _layerWidth && size.Y == _layerHeight && pixels.X == _layerPixelWidth && pixels.Y == _layerPixelHeight) return;
        (_layerWidth, _layerHeight, _layerPixelWidth, _layerPixelHeight) = (size.X, size.Y, pixels.X, pixels.Y);
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

    /// <summary>
    /// Brings the app to the front, so a screenshot of the screen shows the window.
    /// </summary>
    public void Activate()
    {
        var app = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("NSApplication"), "sharedApplication");
        ObjectiveC.objc_msgSend(app, "activateIgnoringOtherApps:", true);
    }

    /// <summary>
    /// Whether the window is in native macOS full screen, its own Space: the state last asked for while the animation into or out of it runs,
    /// then AppKit's, which the green button and Ctrl+Cmd+F also change.
    /// </summary>
    public bool FullScreen => _fullScreenAskedStamp != 0 && Stopwatch.GetElapsedTime(_fullScreenAskedStamp) < FullScreenTransition
        ? _fullScreenWanted
        : (ObjectiveC.ulong_objc_msgSend(_nsWindow, "styleMask") & FullScreenStyleMask) != 0;

    /// <summary>
    /// The animation into or out of the full-screen Space last asked for is still running: AppKit has not said it ended, and it was asked for less
    /// than <see cref="FullScreenTransition"/> ago (a refused or failed request ends nothing).
    /// </summary>
    public bool FullScreenTransitioning => _fullScreenAskedStamp != 0 && FullScreenTransitions.Count == _transitionsAtRequest
        && Stopwatch.GetElapsedTime(_fullScreenAskedStamp) < FullScreenTransition;

    /// <summary>
    /// Enters or leaves native macOS full screen. A Space of its own, not a window covering the monitor: Cmd+Tab and Ctrl+Left/Right switch
    /// away from it, and AppKit hides the menu bar and the Dock itself. A window that is not shown yet ignores the request.
    /// </summary>
    public void SetFullScreen(bool on)
    {
        if (on == FullScreen) return;
        _fullScreenWanted = on;
        _fullScreenAskedStamp = Stopwatch.GetTimestamp();
        _transitionsAtRequest = FullScreenTransitions.Count;
        MsgSendPtr(_nsWindow, ToggleFullScreenSelector.SelPtr, IntPtr.Zero);
        Console.WriteLine($"window    {(on ? "entering" : "leaving")} native full screen");
    }

    /// <summary>
    /// The layer's actual display-sync state (diagnostic and the game's settings).
    /// </summary>
    public bool VSync
    {
        get => _layer.DisplaySyncEnabled;
        set => _layer.DisplaySyncEnabled = value;
    }

    /// <summary>
    /// Disposes the window.
    /// </summary>
    public void Dispose() => Window.Dispose();

    /// <summary>
    /// <c>objc_msgSend</c> with one pointer argument, returning a pointer.
    /// </summary>
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern IntPtr MsgSendPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    /// <summary>
    /// CAMetalLayer's <c>drawableSize</c> is a CGSize (two doubles): SharpMetal models it as an IntPtr, so the
    /// setter is called directly with the two doubles the ABI passes for the struct.
    /// </summary>
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern void SetDrawableSize(IntPtr layer, IntPtr selector, double width, double height);

    /// <summary>
    /// <c>objc_msgSend</c> with one NSUInteger argument (for <c>setCollectionBehavior:</c>).
    /// </summary>
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern void SendUInteger(IntPtr receiver, IntPtr selector, ulong value);

    #endregion

    #region Frames

    /// <summary>
    /// When "no drawable" was last logged (Stopwatch ticks): it is logged at most once a second.
    /// </summary>
    long _noDrawableStamp;

    /// <summary>
    /// How long the last <see cref="TryNextDrawable"/> waited in <c>CAMetalLayer.nextDrawable</c> (ms). The trace
    /// separates this present/swapchain stall from the CPU time spent recording the frame.
    /// </summary>
    public double LastDrawableWaitMilliseconds { get; private set; }

    /// <summary>
    /// Begins a frame: the command buffer to record it in. The drawable is not taken here but as late as possible (<see cref="TryNextDrawable"/>),
    /// so the wait for the display to release one does not hold the frame's recording or its GPU work. False while the window has no pixels
    /// (minimised, mid-resize): the frame is skipped.
    /// </summary>
    public bool TryBeginFrame(out MTLCommandBuffer command)
    {
        SyncLayer();
        command = default;
        var pixels = Window.FramebufferSize;
        if (pixels.X <= 0 || pixels.Y <= 0) return false;

        command = Queue.CommandBuffer();
        return true;
    }

    /// <summary>
    /// The layer's next drawable, taken when the frame's scene work is already committed; false (and logged once a second) when none is ready.
    /// </summary>
    public bool TryNextDrawable(out CAMetalDrawable drawable)
    {
        long started = Stopwatch.GetTimestamp();
        drawable = Layer.NextDrawable;
        LastDrawableWaitMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (drawable.DrawableID != 0) return true;

        long now = Stopwatch.GetTimestamp();
        if (now - _noDrawableStamp > Stopwatch.Frequency)
        {
            _noDrawableStamp = now;
            Console.WriteLine($"metal     no drawable: window {Window.Size.X}x{Window.Size.Y} points, " +
                $"framebuffer {Window.FramebufferSize.X}x{Window.FramebufferSize.Y} pixels");
        }
        drawable = default;
        return false;
    }

    /// <summary>
    /// Opens the render pass for a frame: the drawable cleared to <paramref name="clear"/>.
    /// </summary>
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

    /// <summary>
    /// Presents the frame and commits the command buffer.
    /// </summary>
    public void Present(MTLCommandBuffer command, CAMetalDrawable drawable)
    {
        command.PresentDrawable(drawable);
        command.Commit();
    }

    #endregion
}
