using System.Diagnostics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using static Meitou.Rendering.Upscalers.SlTypes;

namespace Meitou.Rendering.Upscalers;

/// <summary>
/// NVIDIA DLSS frame generation (DLSS-G) through <see cref="Streamline"/>: Streamline owns the real swapchain (<see cref="Streamline.SwapchainHooks"/>) and, at
/// each present, shows one frame interpolated from the last two before the rendered one. Per frame, on <see cref="Streamline.FrameGenerationViewport"/>: the
/// camera constants, depth and motion (<see cref="Prepare"/>), the picture without the interface and the interface itself (<see cref="Finish"/>), all tagged
/// valid until the present, and the mode (on, or off under the loading screen and for screenshots).
/// <para>
/// Conventions (docs/render-post.md "Frame generation"): the swapchain's picture is top-down, so everything DLSS-G reads is copied flipped into targets of its
/// own: depth and motion at the render size (constants without the y flip, motion scaled −1, +1), the world at the display size (the backbuffer before the
/// interface) and the interface layer (premultiplied RGBA8, which is what DLSS-G's UI colour and alpha buffer asks for). The window shows the finished picture.
/// </para>
/// </summary>
public sealed unsafe class DlssFrameGeneration : IFrameGenerator
{
    const uint ModeOff = 0, ModeOn = 1, RetainResourcesWhenOff = 1 << 3;
    const byte SlTrue = 1, SlInvalid = 2;

    readonly GpuContext ctx;
    readonly Streamline sl;
    readonly ViewportHandle* viewport;
    GpuTexture? depthFlipped, motionFlipped, hudless, uiFlipped;
    bool modeOn, modeSet, failed, loggedActive, loggedUnprepared;
    int displayWidth, displayHeight;
    uint lastStatus;
    long lastStateTicks;

    public bool Wanted { get; private set; }
    public bool Prepared { get; private set; }
    public bool Enabled { get; private set; }
    /// <summary>The window shows the finished picture; DLSS-G is told which part is interface.</summary>
    public bool PresentsWorldAlone => false;
    /// <summary>Frames Streamline presented for the last rendered one (1: none generated, 2: one), at the last state read about once a second; 0 before it.</summary>
    public uint PresentedPerRendered { get; private set; }

    DlssFrameGeneration(GpuContext ctx, Streamline sl)
    {
        this.ctx = ctx;
        this.sl = sl;
        viewport = (ViewportHandle*)NativeMemory.AllocZeroed((nuint)sizeof(ViewportHandle));
        *viewport = new ViewportHandle { StructType = ViewportType, StructVersion = 1, Value = Streamline.FrameGenerationViewport };
    }

    /// <summary>The generator on <paramref name="sl"/>, or null with the reason (DLSS frame generation not supported or not loaded).</summary>
    public static DlssFrameGeneration? TryCreate(GpuContext ctx, Streamline? sl, out string? reason)
    {
        if (sl is not { FrameGenerationSupported: true }) { reason = "DLSS frame generation is not supported here (see the frame generation lines above)"; return null; }
        reason = null;
        return new DlssFrameGeneration(ctx, sl);
    }

    public void BeginFrame(bool wanted, int displayW, int displayH)
    {
        (displayWidth, displayHeight) = (displayW, displayH);
        Prepared = Enabled = false;
        Wanted = wanted && !failed;
    }

    /// <summary>Copies depth and motion flipped, sets this frame's constants (top-down) and tags them, valid until the present.</summary>
    public bool Prepare(FrameGenInputs i)
    {
        if (!Wanted) return false;
        nint token = sl.FrameToken();
        if (token == 0) return Fail("slGetNewFrameToken failed");
        var constants = CameraConstants(i.ViewToClip, i.ClipToPreviousClip, i.JitterPixels, i.Eye, i.Up, i.Right, i.Forward,
            i.Near, i.Far, i.FieldOfView, i.Aspect, i.Reset, bottomUp: false);
        int rc = sl.SetConstants(&constants, token, viewport);
        if (rc != 0) return Fail($"slSetConstants failed ({rc})");

        var depth = FrameGeneration.Target(ctx, ref depthFlipped, i.Depth.Desc.Format, i.RenderWidth, i.RenderHeight, "dlss-g depth top-down");
        var motion = FrameGeneration.Target(ctx, ref motionFlipped, i.Motion.Desc.Format, i.RenderWidth, i.RenderHeight, "dlss-g motion top-down");
        var list = (CommandList)ctx.BeginNative("dlss-g depth and motion");
        try
        {
            FrameGeneration.Flip(ctx.Device.Vk, list.Handle, ctx.Device.NativeImage(i.Depth), ctx.Device.NativeImage(depth), i.RenderWidth, i.RenderHeight);
            FrameGeneration.Flip(ctx.Device.Vk, list.Handle, ctx.Device.NativeImage(i.Motion), ctx.Device.NativeImage(motion), i.RenderWidth, i.RenderHeight);
            Barrier(list);
            rc = TagPair(token, list.Handle.Handle, depth, BufferDepth, motion, BufferMotionVectors);
        }
        finally
        {
            ctx.EndNative(list);
        }
        if (rc != 0) return Fail($"slSetTagForFrame (depth, motion) failed ({rc})");
        Prepared = true;
        return true;
    }

    /// <summary>Copies the world and the interface flipped and tags them, then sets the mode for this frame's present.</summary>
    public bool Finish(GpuTexture world, GpuTexture? ui)
    {
        Enabled = Wanted && Prepared && ui is not null;
        if (Wanted && !Prepared && !loggedUnprepared)
        {
            loggedUnprepared = true;
            Console.WriteLine("frame generation  the post chain gave no depth and motion this frame (anti-aliasing / upscaling not temporal?): no frame is generated while that holds");
        }
        if (Enabled && !TagPicture(world, ui!)) Enabled = false;
        SetMode(Enabled);
        if (Enabled && !loggedActive)
        {
            loggedActive = true;
            Console.WriteLine("frame generation  DLSS active");
        }
        ReadState();
        return Enabled;
    }

    bool TagPicture(GpuTexture world, GpuTexture ui)
    {
        nint token = sl.FrameToken();
        if (token == 0) return Fail("slGetNewFrameToken failed");
        var picture = FrameGeneration.Target(ctx, ref hudless, world.Desc.Format, displayWidth, displayHeight, "dlss-g hudless top-down");
        var overlay = FrameGeneration.Target(ctx, ref uiFlipped, ui.Desc.Format, displayWidth, displayHeight, "dlss-g ui top-down");
        var list = (CommandList)ctx.BeginNative("dlss-g world and interface");
        int rc;
        try
        {
            FrameGeneration.Flip(ctx.Device.Vk, list.Handle, ctx.Device.NativeImage(world), ctx.Device.NativeImage(picture), displayWidth, displayHeight);
            FrameGeneration.Flip(ctx.Device.Vk, list.Handle, ctx.Device.NativeImage(ui), ctx.Device.NativeImage(overlay), displayWidth, displayHeight);
            Barrier(list);
            rc = TagPair(token, list.Handle.Handle, picture, BufferHudlessColor, overlay, BufferUiColorAndAlpha);
        }
        finally
        {
            ctx.EndNative(list);
        }
        return rc == 0 || Fail($"slSetTagForFrame (world, interface) failed ({rc})");
    }

    /// <summary>The copies done before anything reads them (Streamline's work is recorded later in the frame or at the present).</summary>
    void Barrier(CommandList list)
    {
        var b = new Silk.NET.Vulkan.MemoryBarrier2
        {
            SType = Silk.NET.Vulkan.StructureType.MemoryBarrier2,
            SrcStageMask = Silk.NET.Vulkan.PipelineStageFlags2.BlitBit, SrcAccessMask = Silk.NET.Vulkan.AccessFlags2.TransferWriteBit,
            DstStageMask = Silk.NET.Vulkan.PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = Silk.NET.Vulkan.AccessFlags2.ShaderReadBit | Silk.NET.Vulkan.AccessFlags2.TransferReadBit,
        };
        var dep = new Silk.NET.Vulkan.DependencyInfo { SType = Silk.NET.Vulkan.StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &b };
        ctx.Device.Vk.CmdPipelineBarrier2(list.Handle, &dep);
    }

    int TagPair(nint token, nint cb, GpuTexture a, uint aType, GpuTexture b, uint bType)
    {
        var ra = Resource(ctx, a);
        var rb = Resource(ctx, b);
        var tags = stackalloc ResourceTag[2];
        tags[0] = Tag(&ra, aType, ValidUntilPresent);
        tags[1] = Tag(&rb, bType, ValidUntilPresent);
        return sl.SetTagForFrame(token, viewport, tags, 2, cb);
    }

    /// <summary>Turns interpolation on or off for the next present (resources kept while off, so turning it back on does not stutter).</summary>
    void SetMode(bool on)
    {
        if (modeSet && modeOn == on) return;
        var options = new DlssgOptions
        {
            StructType = DlssgOptionsType, StructVersion = 5,
            Mode = on ? ModeOn : ModeOff, NumFramesToGenerate = 1, Flags = RetainResourcesWhenOff,
            Reserved15 = SlInvalid, EnableUserInterfaceRecomposition = SlTrue,
        };
        int rc = sl.DlssgSetOptions(viewport, &options);
        if (rc != 0) { Fail($"slDLSSGSetOptions failed ({rc})"); return; }
        (modeOn, modeSet) = (on, true);
    }

    /// <summary>About once a second: DLSS-G's state, its failures logged when they change (sl_dlss_g.h DLSSGStatus).</summary>
    void ReadState()
    {
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - lastStateTicks;
        if (elapsed < Stopwatch.Frequency) return;
        lastStateTicks = now;
        var state = new DlssgState { StructType = DlssgStateType, StructVersion = 4 };
        if (sl.DlssgGetState(viewport, &state, null) != 0) return;
        PresentedPerRendered = state.NumFramesActuallyPresented;
        if (state.Status == lastStatus) return;
        lastStatus = state.Status;
        Console.WriteLine($"frame generation  DLSS state: {StatusText(state.Status)}; up to {state.NumFramesToGenerateMax} generated per rendered frame, "
            + $"{state.EstimatedVramUsageInBytes / (1 << 20)} MB, smallest side {state.MinWidthOrHeight}");
    }

    static string StatusText(uint status)
    {
        if (status == 0) return "ok";
        string[] names = ["resolution too low", "Reflex not detected", "HDR format not supported", "common constants invalid", "GetCurrentBackBufferIndex not called"];
        return string.Join(", ", names.Where((_, bit) => (status & (1u << bit)) != 0));
    }

    bool Fail(string message)
    {
        Console.WriteLine($"frame generation  {message}; DLSS off for the rest of the run");
        failed = true;
        Wanted = Enabled = false;
        return false;
    }

    public void Dispose()
    {
        if (modeOn) SetMode(false);
        ctx.Device.WaitIdle();
        sl.FreeResources(FeatureDlssG, viewport);
        NativeMemory.Free(viewport);
        depthFlipped?.Dispose();
        motionFlipped?.Dispose();
        hudless?.Dispose();
        uiFlipped?.Dispose();
    }
}
