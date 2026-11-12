using System.Diagnostics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using static Meitou.Game.Rendering.Upscalers.SlTypes;

namespace Meitou.Game.Rendering.Upscalers;

/// <summary>
/// NVIDIA DLSS frame generation (DLSS-G) through <see cref="Streamline"/>: Streamline owns the real swapchain (<see
/// cref="Streamline.SwapchainHooks"/>) and, at each present, shows one frame interpolated from the last two before the rendered one. Per frame, on
/// <see cref="Streamline.FrameGenerationViewport"/>: the camera constants, depth and motion (<see cref="Prepare"/>), the picture without the
/// interface and the interface itself (<see cref="Finish"/>), all tagged valid until the present, and the mode (on, or off under the loading screen
/// and for screenshots).
/// <para>
/// Conventions (docs/render-post.md "Frame generation"): the swapchain's picture is top-down, so everything DLSS-G reads is copied flipped into
/// targets of its own: depth and motion at the render size (constants without the y flip, motion scaled −1, +1), the world at the display size (the
/// backbuffer before the interface) and the interface layer (premultiplied RGBA8, which is what DLSS-G's UI colour and alpha buffer asks for). The
/// window shows the finished picture.
/// </para>
/// </summary>
public sealed unsafe class DlssFrameGeneration : IFrameGenerator
{
    /// <summary>
    /// sl::DLSSGMode off and on, and <c>DLSSGFlags::eRetainResourcesWhenOff</c> (sl_dlss_g.h).
    /// </summary>
    const uint ModeOff = 0, ModeOn = 1, RetainResourcesWhenOff = 1 << 3;

    /// <summary>
    /// sl::Boolean's true and invalid.
    /// </summary>
    const byte SlTrue = 1, SlInvalid = 2;

    /// <summary>
    /// The device context the copies and tags are recorded on.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The Streamline instance frame generation runs through.
    /// </summary>
    readonly Streamline _sl;

    /// <summary>
    /// The frame generation viewport (<see cref="Streamline.FrameGenerationViewport"/>), in native memory for Streamline's calls.
    /// </summary>
    readonly ViewportHandle* _viewport;

    /// <summary>
    /// The depth and motion flipped top-down, and the world without the interface and the interface layer flipped top-down.
    /// </summary>
    GpuTexture? _depthFlipped, _motionFlipped, _hudless, _uiFlipped;

    /// <summary>
    /// Whether the mode is on, whether it was ever set, whether a call failed (off for the run), and whether the one-time log lines were written.
    /// </summary>
    bool _modeOn, _modeSet, _failed, _loggedActive, _loggedUnprepared;

    /// <summary>
    /// The display size of the frame being made.
    /// </summary>
    int _displayWidth, _displayHeight;

    /// <summary>
    /// The DLSS-G status last read (0: ok).
    /// </summary>
    uint _lastStatus;

    /// <summary>
    /// When the state was last read, in <see cref="Stopwatch"/> ticks.
    /// </summary>
    long _lastStateTicks;

    /// <summary>
    /// Frame generation is wanted for this frame (<see cref="BeginFrame"/>), and nothing failed so far.
    /// </summary>
    public bool Wanted { get; private set; }

    /// <summary>
    /// The post chain gave depth and motion this frame (<see cref="Prepare"/>).
    /// </summary>
    public bool Prepared { get; private set; }

    /// <summary>
    /// This frame goes out with frame generation on (<see cref="Finish"/>).
    /// </summary>
    public bool Enabled { get; private set; }
    /// <summary>
    /// The window shows the finished picture; DLSS-G is told which part is interface.
    /// </summary>
    public bool PresentsWorldAlone => false;
    /// <summary>
    /// Frames Streamline presented for the last rendered one (1: none generated, 2: one), at the last state read about once a second; 0 before it.
    /// </summary>
    public uint PresentedPerRendered { get; private set; }

    /// <summary>
    /// Allocates the frame generation viewport.
    /// </summary>
    DlssFrameGeneration(GpuContext ctx, Streamline sl)
    {
        this._ctx = ctx;
        this._sl = sl;
        _viewport = (ViewportHandle*)NativeMemory.AllocZeroed((nuint)sizeof(ViewportHandle));
        *_viewport = new ViewportHandle { StructType = ViewportType, StructVersion = 1, Value = Streamline.FrameGenerationViewport };
    }

    /// <summary>
    /// The generator on <paramref name="sl"/>, or null with the reason (DLSS frame generation not supported or not loaded).
    /// </summary>
    public static DlssFrameGeneration? TryCreate(GpuContext ctx, Streamline? sl, out string? reason)
    {
        if (sl is not { FrameGenerationSupported: true })
        {
            reason = "DLSS frame generation is not supported here (see the frame generation lines above)";
            return null;
        }

        reason = null;
        return new DlssFrameGeneration(ctx, sl);
    }

    /// <summary>
    /// Starts a frame of <paramref name="displayW"/> x <paramref name="displayH"/>; <paramref name="wanted"/> is false under the loading screen and
    /// for screenshots and recorded frames.
    /// </summary>
    public void BeginFrame(bool wanted, int displayW, int displayH)
    {
        (_displayWidth, _displayHeight) = (displayW, displayH);
        Prepared = Enabled = false;
        Wanted = wanted && !_failed;
    }

    /// <summary>
    /// Copies depth and motion flipped, sets this frame's constants (top-down) and tags them, valid until the present.
    /// </summary>
    public bool Prepare(FrameGenInputs i)
    {
        if (!Wanted) return false;
        nint token = _sl.FrameToken();
        if (token == 0) return Fail("slGetNewFrameToken failed");
        var constants = CameraConstants(i.ViewToClip, i.ClipToPreviousClip, i.JitterPixels, i.Eye, i.Up, i.Right, i.Forward,
            i.Near, i.Far, i.FieldOfView, i.Aspect, i.Reset, bottomUp: false);
        int rc = _sl.SetConstants(&constants, token, _viewport);
        if (rc != 0) return Fail($"slSetConstants failed ({rc})");

        var depth = FrameGeneration.Target(_ctx, ref _depthFlipped, i.Depth.Desc.Format, i.RenderWidth, i.RenderHeight, "dlss-g depth top-down");
        var motion = FrameGeneration.Target(_ctx, ref _motionFlipped, i.Motion.Desc.Format, i.RenderWidth, i.RenderHeight, "dlss-g motion top-down");
        var list = (CommandList)_ctx.BeginNative("dlss-g depth and motion");
        try
        {
            var vk = _ctx.Device.Vk;
            FrameGeneration.Flip(vk, list.Handle, _ctx.Device.NativeImage(i.Depth), _ctx.Device.NativeImage(depth), i.RenderWidth, i.RenderHeight);
            FrameGeneration.Flip(vk, list.Handle, _ctx.Device.NativeImage(i.Motion), _ctx.Device.NativeImage(motion), i.RenderWidth, i.RenderHeight);
            Barrier(list);
            rc = TagPair(token, list.Handle.Handle, depth, BufferDepth, motion, BufferMotionVectors);
        }
        finally
        {
            _ctx.EndNative(list);
        }
        if (rc != 0) return Fail($"slSetTagForFrame (depth, motion) failed ({rc})");
        Prepared = true;
        return true;
    }

    /// <summary>
    /// Copies the world and the interface flipped and tags them, then sets the mode for this frame's present.
    /// </summary>
    public bool Finish(GpuTexture world, GpuTexture? ui)
    {
        Enabled = Wanted && Prepared && ui is not null;
        if (Wanted && !Prepared && !_loggedUnprepared)
        {
            _loggedUnprepared = true;
            Console.WriteLine("frame generation  the post chain gave no depth and motion this frame (anti-aliasing / upscaling not temporal?): "
                + "no frame is generated while that holds");
        }
        if (Enabled && !TagPicture(world, ui!)) Enabled = false;
        SetMode(Enabled);
        if (Enabled && !_loggedActive)
        {
            _loggedActive = true;
            Console.WriteLine("frame generation  DLSS active");
        }
        ReadState();
        return Enabled;
    }

    /// <summary>
    /// Copies the world and the interface flipped, then tags them as the HUD-less colour and the UI colour and alpha, valid until the present.
    /// </summary>
    bool TagPicture(GpuTexture world, GpuTexture ui)
    {
        nint token = _sl.FrameToken();
        if (token == 0) return Fail("slGetNewFrameToken failed");
        var picture = FrameGeneration.Target(_ctx, ref _hudless, world.Desc.Format, _displayWidth, _displayHeight, "dlss-g hudless top-down");
        var overlay = FrameGeneration.Target(_ctx, ref _uiFlipped, ui.Desc.Format, _displayWidth, _displayHeight, "dlss-g ui top-down");
        var list = (CommandList)_ctx.BeginNative("dlss-g world and interface");
        int rc;
        try
        {
            var vk = _ctx.Device.Vk;
            FrameGeneration.Flip(vk, list.Handle, _ctx.Device.NativeImage(world), _ctx.Device.NativeImage(picture), _displayWidth, _displayHeight);
            FrameGeneration.Flip(vk, list.Handle, _ctx.Device.NativeImage(ui), _ctx.Device.NativeImage(overlay), _displayWidth, _displayHeight);
            Barrier(list);
            rc = TagPair(token, list.Handle.Handle, picture, BufferHudlessColor, overlay, BufferUiColorAndAlpha);
        }
        finally
        {
            _ctx.EndNative(list);
        }
        return rc == 0 || Fail($"slSetTagForFrame (world, interface) failed ({rc})");
    }

    /// <summary>
    /// The copies done before anything reads them (Streamline's work is recorded later in the frame or at the present).
    /// </summary>
    void Barrier(CommandList list)
    {
        var b = new Silk.NET.Vulkan.MemoryBarrier2
        {
            SType = Silk.NET.Vulkan.StructureType.MemoryBarrier2,
            SrcStageMask = Silk.NET.Vulkan.PipelineStageFlags2.BlitBit,
            SrcAccessMask = Silk.NET.Vulkan.AccessFlags2.TransferWriteBit,
            DstStageMask = Silk.NET.Vulkan.PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = Silk.NET.Vulkan.AccessFlags2.ShaderReadBit | Silk.NET.Vulkan.AccessFlags2.TransferReadBit,
        };
        var dep = new Silk.NET.Vulkan.DependencyInfo
        {
            SType = Silk.NET.Vulkan.StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &b,
        };
        _ctx.Device.Vk.CmdPipelineBarrier2(list.Handle, &dep);
    }

    /// <summary>
    /// Tags <paramref name="a"/> and <paramref name="b"/> as buffer types <paramref name="aType"/> and <paramref name="bType"/>, valid until the
    /// present.
    /// </summary>
    int TagPair(nint token, nint cb, GpuTexture a, uint aType, GpuTexture b, uint bType)
    {
        var ra = Resource(_ctx, a);
        var rb = Resource(_ctx, b);
        var tags = stackalloc ResourceTag[2];
        tags[0] = Tag(&ra, aType, ValidUntilPresent);
        tags[1] = Tag(&rb, bType, ValidUntilPresent);
        return _sl.SetTagForFrame(token, _viewport, tags, 2, cb);
    }

    /// <summary>
    /// Turns interpolation on or off for the next present (resources kept while off, so turning it back on does not stutter).
    /// </summary>
    void SetMode(bool on)
    {
        if (_modeSet && _modeOn == on) return;
        var options = new DlssgOptions
        {
            StructType = DlssgOptionsType,
            StructVersion = 5,
            Mode = on ? ModeOn : ModeOff,
            NumFramesToGenerate = 1,
            Flags = RetainResourcesWhenOff,
            Reserved15 = SlInvalid,
            EnableUserInterfaceRecomposition = SlTrue,
        };
        int rc = _sl.DlssgSetOptions(_viewport, &options);
        if (rc != 0)
        {
            Fail($"slDLSSGSetOptions failed ({rc})");
            return;
        }

        (_modeOn, _modeSet) = (on, true);
    }

    /// <summary>
    /// About once a second: DLSS-G's state, its failures logged when they change (sl_dlss_g.h DLSSGStatus).
    /// </summary>
    void ReadState()
    {
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - _lastStateTicks;
        if (elapsed < Stopwatch.Frequency) return;
        _lastStateTicks = now;
        var state = new DlssgState { StructType = DlssgStateType, StructVersion = 4 };
        if (_sl.DlssgGetState(_viewport, &state, null) != 0) return;
        PresentedPerRendered = state.NumFramesActuallyPresented;
        if (state.Status == _lastStatus) return;
        _lastStatus = state.Status;
        Console.WriteLine($"frame generation  DLSS state: {StatusText(state.Status)}; up to {state.NumFramesToGenerateMax} generated per "
            + $"rendered frame, {state.EstimatedVramUsageInBytes / (1 << 20)} MB, smallest side {state.MinWidthOrHeight}");
    }

    /// <summary>
    /// The DLSS-G status bits as words ("ok" when none is set).
    /// </summary>
    static string StatusText(uint status)
    {
        if (status == 0) return "ok";
        string[] names =
        [
            "resolution too low", "Reflex not detected", "HDR format not supported", "common constants invalid",
            "GetCurrentBackBufferIndex not called",
        ];
        return string.Join(", ", names.Where((_, bit) => (status & (1u << bit)) != 0));
    }

    /// <summary>
    /// Logs <paramref name="message"/> and turns DLSS frame generation off for the rest of the run; false.
    /// </summary>
    bool Fail(string message)
    {
        Console.WriteLine($"frame generation  {message}; DLSS off for the rest of the run");
        _failed = true;
        Wanted = Enabled = false;
        return false;
    }

    /// <summary>
    /// Turns the mode off, frees DLSS-G's resources for the viewport (after the device has gone idle), the viewport and the flipped copies.
    /// </summary>
    public void Dispose()
    {
        if (_modeOn) SetMode(false);
        _ctx.Device.WaitIdle();
        _sl.FreeResources(FeatureDlssG, _viewport);
        NativeMemory.Free(_viewport);
        _depthFlipped?.Dispose();
        _motionFlipped?.Dispose();
        _hudless?.Dispose();
        _uiFlipped?.Dispose();
    }
}