using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using static Meitou.Rendering.Upscalers.FfxApi;

namespace Meitou.Rendering.Upscalers;

/// <summary>One frame's inputs to frame generation: the post chain's depth and motion (bottom-up, as for the upscalers: see <see cref="UpscaleInputs"/>) and the camera
/// (<see cref="ViewToClip"/> and <see cref="ClipToPreviousClip"/> as in <see cref="UpscaleInputs"/>).</summary>
public sealed record FrameGenInputs
{
    public required GpuTexture Depth, Motion;
    public required int RenderWidth, RenderHeight;
    public required Vector2 JitterPixels;
    public required float Near, Far, FieldOfView, DeltaSeconds, Aspect;
    public required bool Reset;
    public required Vector3 Eye, Right, Up, Forward;
    public required Matrix4x4 ViewToClip, ClipToPreviousClip;
}

/// <summary>
/// Frame generation as the game loop and the post chain drive it, whichever vendor's: <see cref="BeginFrame"/>, <see cref="Prepare"/> from the post chain
/// (depth and motion), <see cref="Finish"/> before the present. AMD's (<see cref="FrameGeneration"/>) shows the world alone and lays the interface over every
/// frame itself; NVIDIA's (<see cref="DlssFrameGeneration"/>) shows the finished picture and is told which part is interface.
/// </summary>
public interface IFrameGenerator : IDisposable
{
    /// <summary>Frame generation is wanted for this frame (<see cref="BeginFrame"/>).</summary>
    bool Wanted { get; }
    /// <summary>This frame goes out with frame generation on (<see cref="Finish"/>).</summary>
    bool Enabled { get; }
    /// <summary>With <see cref="Enabled"/>: the window is shown the world alone (the backbuffer), not the picture with the interface.</summary>
    bool PresentsWorldAlone { get; }
    /// <summary>Starts a frame of <paramref name="displayW"/> × <paramref name="displayH"/>; <paramref name="wanted"/> is false under the loading screen and for
    /// screenshots and recorded frames.</summary>
    void BeginFrame(bool wanted, int displayW, int displayH);
    /// <summary>Takes this frame's depth and motion; call after the post chain made them, outside a native segment.</summary>
    bool Prepare(FrameGenInputs inputs);
    /// <summary>Before the present: <paramref name="world"/> is the bottom-up backbuffer (the world alone), <paramref name="ui"/> the bottom-up premultiplied
    /// interface layer. True when frame generation runs on this frame's present.</summary>
    bool Finish(GpuTexture world, GpuTexture? ui);
}

/// <summary>
/// AMD FSR 3 frame generation (FidelityFX SDK 1.1.x <c>ffx_framegeneration.h</c>, MIT, from <c>amd_fidelityfx_vk.dll</c>; our own code) on top of
/// <see cref="FfxSwapchain"/>: the layer presents each rendered frame and, between two of them, one picture interpolated from the last two.
/// Per frame: <see cref="BeginFrame"/> (is it wanted: not under the loading screen, not for a screenshot), <see cref="Prepare"/> from the post chain
/// (depth and motion), <see cref="Finish"/> before the present (the interface picture, the configuration). The interpolation itself runs inside the
/// layer's present, through the dispatch callback registered in the configuration.
/// <para>
/// Conventions (docs/render-post.md, "Frame generation"): the picture handed to the present is the world alone, top-down (the
/// backbuffer flipped by the present blit); the interface is a separate premultiplied RGBA8 picture, top-down, laid over every presented frame by the layer.
/// Depth and motion are bottom-up in the post chain, so each is copied flipped into a target of its own (a blit, only while frame generation is on) and the
/// motion's y scale changes sign: x -renderWidth, y +renderHeight (the upscalers' scale is -renderWidth, -renderHeight in the bottom-up layout), the jitter's y is negated.
/// </para>
/// </summary>
public sealed unsafe class FrameGeneration : IFrameGenerator
{
    const ulong CreateType = 0x00020001, ConfigureType = 0x00020002, PrepareType = 0x00020004, CameraInfoType = 0x0002000a, BackendVkType = 0x00000003;
    const uint EnableDebugChecking = 1 << 6;

    [StructLayout(LayoutKind.Sequential)]
    struct CreateDesc { public Header Header; public uint Flags; public Dimensions DisplaySize, MaxRenderSize; public uint BackBufferFormat; }
    [StructLayout(LayoutKind.Sequential)] struct BackendVkDesc { public Header Header; public nint Device, PhysicalDevice, DeviceProcAddr; }
    [StructLayout(LayoutKind.Sequential)]
    struct ConfigureDesc
    {
        public Header Header;
        public nint SwapChain, PresentCallback, PresentCallbackUserContext, FrameGenerationCallback, FrameGenerationCallbackUserContext;
        public byte FrameGenerationEnabled, AllowAsyncWorkloads;
        public Resource HudLessColor;
        public uint Flags;
        public byte OnlyPresentGenerated;
        public Rect GenerationRect;
        public ulong FrameId;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PrepareDesc
    {
        public Header Header;
        public ulong FrameId;
        public uint Flags;
        public nint CommandList;
        public Dimensions RenderSize;
        public Coords JitterOffset, MotionVectorScale;
        public float FrameTimeDelta;
        public byte UnusedReset;
        public float CameraNear, CameraFar, CameraFovAngleVertical, ViewSpaceToMetersFactor;
        public Resource Depth, MotionVectors;
    }
    [StructLayout(LayoutKind.Sequential)] struct Float3 { public float X, Y, Z; }
    [StructLayout(LayoutKind.Sequential)] struct CameraInfoDesc { public Header Header; public Float3 Position, Up, Right, Forward; }

    readonly GpuContext ctx;
    readonly VulkanDevice device;
    readonly FfxSwapchain layer;
    readonly delegate* unmanaged<nint*, void*, void*, uint> createContext;
    readonly delegate* unmanaged<nint*, void*, uint> destroyContext;
    readonly delegate* unmanaged<nint*, void*, uint> configure;
    static delegate* unmanaged<nint*, void*, uint> dispatchFunction;   // the callback has no way to carry it
    readonly bool debug;
    // Kept as long as the context (ffx_api.h); the slot also is the callback's user context.
    readonly CreateDesc* createDesc;
    readonly BackendVkDesc* backendDesc;
    readonly nint* contextSlot;
    (int, int, uint) contextSize;
    GpuTexture? depthFlipped, motionFlipped, uiFlipped;
    ulong frameId;
    bool resetNext = true, idAssigned, failed, layerGone, uiRegistered, loggedActive, loggedUnprepared;
    int displayWidth, displayHeight;

    /// <summary>Frame generation is wanted for this frame (<see cref="BeginFrame"/>).</summary>
    public bool Wanted { get; private set; }
    /// <summary>The world is shown alone; the layer lays the registered interface over it.</summary>
    public bool PresentsWorldAlone => true;
    /// <summary>The post chain gave depth and motion this frame (<see cref="Prepare"/>), so the frame can be interpolated.</summary>
    public bool Prepared { get; private set; }
    /// <summary>This frame goes out with frame generation on (<see cref="Finish"/>): wanted and prepared.</summary>
    public bool Enabled { get; private set; }
    /// <summary>The number of the last frame handed over (it steps by 1 per rendered frame, by 2 where the logic must restart).</summary>
    public ulong FrameId => frameId;

    FrameGeneration(GpuContext ctx, FfxSwapchain layer, nint library)
    {
        this.ctx = ctx;
        device = ctx.Device;
        this.layer = layer;
        createContext = (delegate* unmanaged<nint*, void*, void*, uint>)NativeLibrary.GetExport(library, "ffxCreateContext");
        destroyContext = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxDestroyContext");
        configure = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxConfigure");
        dispatchFunction = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxDispatch");
        debug = Environment.GetEnvironmentVariable("MEITOU_UPSCALER_LOG") == "1";
        createDesc = (CreateDesc*)NativeMemory.AllocZeroed((nuint)sizeof(CreateDesc));
        backendDesc = (BackendVkDesc*)NativeMemory.AllocZeroed((nuint)sizeof(BackendVkDesc));
        contextSlot = (nint*)NativeMemory.AllocZeroed((nuint)sizeof(nint));
        layer.Disposing += OnLayerDisposing;
    }

    /// <summary>The layer goes away first (a failed swapchain remake falls back to the plain swapchain): the context goes before it, and none is made again.
    /// Not configured off first: after a failed remake the swapchain it was configured with may already be destroyed, and no present comes in between.</summary>
    void OnLayerDisposing()
    {
        layerGone = true;
        DestroyContext(configureOff: false);
    }

    /// <summary>The frame-generation context for <paramref name="layer"/>'s swapchain, or null with the reason (library not loading).</summary>
    public static FrameGeneration? TryCreate(GpuContext ctx, FfxSwapchain layer, out string? reason)
    {
        if (!FfxSwapchain.LibraryLoads(out var library, out reason)) return null;
        if (!NativeLibrary.TryGetExport(library, "ffxConfigure", out _) || !NativeLibrary.TryGetExport(library, "ffxDispatch", out _))
        { reason = "the FFX library lacks ffxConfigure / ffxDispatch"; return null; }
        return new FrameGeneration(ctx, layer, library);
    }

    /// <summary>Called by the FFX layer at the present of each rendered frame: runs the interpolation, as the SDK's samples do (ffxDispatch on this context).</summary>
    [UnmanagedCallersOnly]
    static uint OnGenerate(void* desc, void* user)
    {
        try { return dispatchFunction((nint*)user, desc); }
        catch (Exception) { return 1; }   // nothing may escape into the library
    }

    [UnmanagedCallersOnly]
    static void OnMessage(uint type, char* message) => Console.WriteLine($"frame generation  {(type == 0 ? "error" : "warning")}: {FsrUpscaler.WideString(message)}");

    bool EnsureContext(int displayW, int displayH)
    {
        var size = (displayW, displayH, layer.SurfaceFormat);
        if (*contextSlot != 0 && contextSize == size) return true;
        DestroyContext();
        *backendDesc = new BackendVkDesc
        {
            Header = new Header { Type = BackendVkType },
            Device = device.Device.Handle,
            PhysicalDevice = device.PhysicalDevice.Handle,
            DeviceProcAddr = NativeLibrary.GetExport(NativeLibrary.Load(FsrUpscaler.VulkanLoaderName), "vkGetDeviceProcAddr"),
        };
        *createDesc = new CreateDesc
        {
            Header = new Header { Type = CreateType, Next = (nint)backendDesc },
            // Depth 0..1 not inverted with finite planes, motion in render pixels without the jitter, LDR colour: the defaults.
            Flags = debug ? EnableDebugChecking : 0,
            DisplaySize = new Dimensions { Width = (uint)displayW, Height = (uint)displayH },
            MaxRenderSize = new Dimensions { Width = (uint)displayW, Height = (uint)displayH },
            BackBufferFormat = layer.SurfaceFormat,
        };
        nint created = 0;
        uint rc = createContext(&created, createDesc, null);
        if (rc != 0) { Console.WriteLine($"frame generation  ffxCreateContext failed ({rc})"); return false; }
        *contextSlot = created;
        contextSize = size;
        resetNext = true;
        Console.WriteLine($"frame generation  context made for {displayW}x{displayH}");
        return true;
    }

    void DestroyContext(bool configureOff = true)
    {
        if (*contextSlot == 0) return;
        // The layer may still call back into it from a present in flight: configure it off, wait for the device, then destroy.
        if (configureOff) ConfigureFrame(false, frameId);
        device.WaitIdle();
        nint c = *contextSlot;
        destroyContext(&c, null);
        *contextSlot = 0;
    }

    /// <summary>Starts a frame of <paramref name="displayW"/> × <paramref name="displayH"/>; <paramref name="wanted"/> is false under the loading screen and for screenshots and recorded frames.</summary>
    public void BeginFrame(bool wanted, int displayW, int displayH)
    {
        (displayWidth, displayHeight) = (displayW, displayH);
        Prepared = Enabled = false;
        idAssigned = false;
        Wanted = wanted && !failed && !layerGone && EnsureContext(displayW, displayH);
        if (wanted && !Wanted) failed = true;   // the context could not be made: off for the rest of the run
    }

    ulong NextId(bool reset)
    {
        if (!idAssigned)
        {
            // Any step other than 1 restarts the logic (the header's rule): skip a number on a cut, after frames without it, on a new context.
            frameId += reset || resetNext ? 2UL : 1UL;
            resetNext = false;
            idAssigned = true;
        }
        return frameId;
    }

    /// <summary>Copies <paramref name="src"/> into <paramref name="dst"/> upside down (both in GENERAL): bottom-up to top-down.</summary>
    internal static void Flip(Vk vk, CommandBuffer cb, Image src, Image dst, int w, int h)
    {
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        blit.SrcOffsets[0] = new Offset3D(0, 0, 0);
        blit.SrcOffsets[1] = new Offset3D(w, h, 1);
        // Row 0 of the destination is the last row of the source.
        blit.DstOffsets[0] = new Offset3D(0, h, 0);
        blit.DstOffsets[1] = new Offset3D(w, 0, 1);
        vk.CmdBlitImage(cb, src, ImageLayout.General, dst, ImageLayout.General, 1, &blit, Filter.Nearest);
    }

    GpuTexture Target(ref GpuTexture? slot, InternalFormat format, int w, int h, string name) => Target(ctx, ref slot, format, w, h, name);

    /// <summary>A copy target of the size and format asked, made again when they change.</summary>
    internal static GpuTexture Target(GpuContext ctx, ref GpuTexture? slot, InternalFormat format, int w, int h, string name)
    {
        if (slot is null || slot.Desc.Width != w || slot.Desc.Height != h || slot.Desc.Format != format)
        {
            slot?.Dispose();   // released after the frames in flight
            slot = ctx.CreateTexture(new TextureDesc(format, w, h, Use: TextureUse.Sampled | TextureUse.TransferSrc | TextureUse.TransferDst, Name: name));
        }
        return slot;
    }

    /// <summary>
    /// Copies depth and motion flipped into targets of their own and records the Prepare dispatch (depth, motion, jitter, camera, frame time) in the frame.
    /// Call after the post chain made them, outside a native segment. False (frame generation off for this frame) when the dispatch fails.
    /// </summary>
    public bool Prepare(FrameGenInputs i)
    {
        if (!Wanted || failed) return false;
        var depth = Target(ref depthFlipped, i.Depth.Desc.Format, i.RenderWidth, i.RenderHeight, "fg depth top-down");
        var motion = Target(ref motionFlipped, i.Motion.Desc.Format, i.RenderWidth, i.RenderHeight, "fg motion top-down");
        var list = (CommandList)ctx.BeginNative("fg flip depth and motion");
        Flip(device.Vk, list.Handle, device.NativeImage(i.Depth), device.NativeImage(depth), i.RenderWidth, i.RenderHeight);
        Flip(device.Vk, list.Handle, device.NativeImage(i.Motion), device.NativeImage(motion), i.RenderWidth, i.RenderHeight);
        ctx.EndNative(list);

        ulong id = NextId(i.Reset);
        var camera = new CameraInfoDesc
        {
            Header = new Header { Type = CameraInfoType },
            Position = new Float3 { X = i.Eye.X, Y = i.Eye.Y, Z = i.Eye.Z },
            Up = new Float3 { X = i.Up.X, Y = i.Up.Y, Z = i.Up.Z },
            Right = new Float3 { X = i.Right.X, Y = i.Right.Y, Z = i.Right.Z },
            Forward = new Float3 { X = i.Forward.X, Y = i.Forward.Y, Z = i.Forward.Z },
        };
        var desc = new PrepareDesc
        {
            Header = new Header { Type = PrepareType, Next = (nint)(&camera) },
            FrameId = id,
            RenderSize = new Dimensions { Width = (uint)i.RenderWidth, Height = (uint)i.RenderHeight },
            // The jitter is not in the motion (no jitter cancellation flag), so it only matters as a hint; its y follows the flipped rows.
            JitterOffset = new Coords { X = i.JitterPixels.X, Y = -i.JitterPixels.Y },
            // Ours: UV from previous to current, y up. FFX: render pixels from current towards previous, y down (the rows were flipped): x as for the upscalers, y the other sign.
            MotionVectorScale = new Coords { X = -i.RenderWidth, Y = i.RenderHeight },
            FrameTimeDelta = i.DeltaSeconds * 1000,
            CameraNear = i.Near,
            CameraFar = i.Far,
            CameraFovAngleVertical = i.FieldOfView,
            ViewSpaceToMetersFactor = 0.1f,   // as for the upscalers: Kenshi units taken as decimetres
            Depth = Input(ctx, depth),
            MotionVectors = Input(ctx, motion),
        };
        var cmd = (CommandList)ctx.BeginNative("fg prepare");
        desc.CommandList = cmd.Handle.Handle;
        uint rc = dispatchFunction(contextSlot, &desc);
        ctx.EndNative(cmd);
        if (rc != 0) { Console.WriteLine($"frame generation  prepare dispatch failed ({rc}); off for the rest of the run"); failed = true; return false; }
        Prepared = true;
        return true;
    }

    bool ConfigureFrame(bool enabled, ulong id)
    {
        if (*contextSlot == 0) return true;   // no context yet (never wanted so far): nothing to configure
        var d = new ConfigureDesc
        {
            Header = new Header { Type = ConfigureType },
            SwapChain = (nint)layer.Swapchain.Handle,
            // No present callback: the layer composes the registered interface picture itself.
            FrameGenerationCallback = (nint)(delegate* unmanaged<void*, void*, uint>)&OnGenerate,
            FrameGenerationCallbackUserContext = (nint)contextSlot,
            FrameGenerationEnabled = (byte)(enabled ? 1 : 0),
            // GenerationRect left zero: the whole display (the SDK's provider fills it in when width and height are 0).
            FrameId = id,
        };
        return configure(contextSlot, &d) == 0;
    }

    /// <summary>
    /// Before the present: hands the interface picture (<paramref name="ui"/>, the bottom-up premultiplied RGBA8 layer; flipped here) to the layer and
    /// configures the context for this frame. Frame generation runs on this frame's present only when it was wanted and prepared. True when it does.
    /// </summary>
    public bool Finish(GpuTexture world, GpuTexture? ui)
    {
        Enabled = Wanted && Prepared && ui is not null;
        if (Wanted && !Prepared && !loggedUnprepared) { loggedUnprepared = true; Console.WriteLine("frame generation  the post chain gave no depth and motion this frame (anti-aliasing / upscaling not temporal?): no frame is generated while that holds"); }
        if (Enabled && !loggedActive) { loggedActive = true; Console.WriteLine($"frame generation  active (frame {frameId + 1})"); }
        ulong id = NextId(false);
        FfxApi.Resource? registered = null;
        if (Enabled)
        {
            var flipped = Target(ref uiFlipped, InternalFormat.Rgba8, displayWidth, displayHeight, "fg ui top-down");
            var list = (CommandList)ctx.BeginNative("fg flip ui");
            Flip(device.Vk, list.Handle, device.NativeImage(ui!), device.NativeImage(flipped), displayWidth, displayHeight);
            ctx.EndNative(list);
            registered = Input(ctx, flipped);
        }
        else resetNext = true;   // the next frame after frames without it starts afresh
        if (!ConfigureFrame(Enabled, id)) { Console.WriteLine("frame generation  configure failed; off for the rest of the run"); failed = true; Enabled = false; }
        if ((registered is not null || uiRegistered) && !layerGone)
        {
            if (!layer.RegisterUi(Enabled ? registered : null)) { Console.WriteLine("frame generation  registering the interface picture failed; off for the rest of the run"); failed = true; Enabled = false; }
            uiRegistered = Enabled;
        }
        return Enabled;
    }

    public void Dispose()
    {
        layer.Disposing -= OnLayerDisposing;
        DestroyContext(configureOff: !layerGone);
        depthFlipped?.Dispose();
        motionFlipped?.Dispose();
        uiFlipped?.Dispose();
        NativeMemory.Free(createDesc);
        NativeMemory.Free(backendDesc);
        NativeMemory.Free(contextSlot);
    }
}
