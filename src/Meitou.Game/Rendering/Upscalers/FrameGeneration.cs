using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Post;
using Silk.NET.Vulkan;
using static Meitou.Game.Rendering.Upscalers.FfxApi;

namespace Meitou.Game.Rendering.Upscalers;

/// <summary>
/// One frame's inputs to frame generation: the post chain's depth and motion (bottom-up, as for the upscalers: see <see cref="UpscaleInputs"/>) and
/// the camera (<see cref="ViewToClip"/> and <see cref="ClipToPreviousClip"/> as in <see cref="UpscaleInputs"/>).
/// </summary>
public sealed record FrameGenInputs
{
    /// <summary>
    /// The post chain's depth and motion vectors, bottom-up.
    /// </summary>
    public required GpuTexture Depth, Motion;

    /// <summary>
    /// The render size in pixels (before upscaling).
    /// </summary>
    public required int RenderWidth, RenderHeight;

    /// <summary>
    /// The projection's jitter this frame, in pixels.
    /// </summary>
    public required Vector2 JitterPixels;

    /// <summary>
    /// The camera's near and far planes, vertical field of view (radians), the frame time in seconds and the aspect ratio.
    /// </summary>
    public required float Near, Far, FieldOfView, DeltaSeconds, Aspect;

    /// <summary>
    /// Whether the history is cut (a camera cut): the interpolation starts afresh.
    /// </summary>
    public required bool Reset;

    /// <summary>
    /// The camera's position and its right, up and forward axes.
    /// </summary>
    public required Vector3 Eye, Right, Up, Forward;

    /// <summary>
    /// The camera's projection, and the reprojection from this frame's clip space to the previous frame's.
    /// </summary>
    public required Matrix4x4 ViewToClip, ClipToPreviousClip;
}

/// <summary>
/// Frame generation as the game loop and the post chain drive it, whichever vendor's: <see cref="BeginFrame"/>, <see cref="Prepare"/> from the post
/// chain (depth and motion), <see cref="Finish"/> before the present. AMD's (<see cref="FrameGeneration"/>) shows the world alone and lays the
/// interface over every frame itself; NVIDIA's (<see cref="DlssFrameGeneration"/>) shows the finished picture and is told which part is interface.
/// </summary>
public interface IFrameGenerator : IDisposable
{
    /// <summary>
    /// Frame generation is wanted for this frame (<see cref="BeginFrame"/>).
    /// </summary>
    bool Wanted { get; }
    /// <summary>
    /// This frame goes out with frame generation on (<see cref="Finish"/>).
    /// </summary>
    bool Enabled { get; }
    /// <summary>
    /// With <see cref="Enabled"/>: the window is shown the world alone (the backbuffer), not the picture with the interface.
    /// </summary>
    bool PresentsWorldAlone { get; }
    /// <summary>
    /// Starts a frame of <paramref name="displayW"/> × <paramref name="displayH"/>; <paramref name="wanted"/> is false under the loading screen and
    /// for screenshots and recorded frames.
    /// </summary>
    void BeginFrame(bool wanted, int displayW, int displayH);
    /// <summary>
    /// Takes this frame's depth and motion; call after the post chain made them, outside a native segment.
    /// </summary>
    bool Prepare(FrameGenInputs inputs);
    /// <summary>
    /// Before the present: <paramref name="world"/> is the bottom-up backbuffer (the world alone), <paramref name="ui"/> the bottom-up premultiplied
    /// interface layer. True when frame generation runs on this frame's present.
    /// </summary>
    bool Finish(GpuTexture world, GpuTexture? ui);
}

/// <summary>
/// AMD FSR 3 frame generation (FidelityFX SDK 1.1.x <c>ffx_framegeneration.h</c>, MIT, from <c>amd_fidelityfx_vk.dll</c>; our own code) on top of
/// <see cref="FfxSwapchain"/>: the layer presents each rendered frame and, between two of them, one picture interpolated from the last two.
/// Per frame: <see cref="BeginFrame"/> (is it wanted: not under the loading screen, not for a screenshot), <see cref="Prepare"/> from the post chain
/// (depth and motion), <see cref="Finish"/> before the present (the interface picture, the configuration). The interpolation itself runs inside the
/// layer's present, through the dispatch callback registered in the configuration.
/// <para>
/// Conventions (docs/render-post.md, "Frame generation"): the picture handed to the present is the world alone, top-down (the backbuffer flipped by
/// the present blit); the interface is a separate premultiplied RGBA8 picture, top-down, laid over every presented frame by the layer. Depth and
/// motion are bottom-up in the post chain, so each is copied flipped into a target of its own (a blit, only while frame generation is on) and the
/// motion's y scale changes sign: x -renderWidth, y +renderHeight (the upscalers' scale is -renderWidth, -renderHeight in the bottom-up layout), the
/// jitter's y is negated.
/// </para>
/// </summary>
public sealed unsafe class FrameGeneration : IFrameGenerator
{
    #region Interop structures

    /// <summary>
    /// The descriptor types (ffx_api.h, ffx_framegeneration.h): create, configure, prepare, camera info, and the Vulkan backend.
    /// </summary>
    const ulong CreateType = 0x00020001, ConfigureType = 0x00020002, PrepareType = 0x00020004, CameraInfoType = 0x0002000a, BackendVkType = 0x00000003;

    /// <summary>
    /// <c>FFX_FRAMEGENERATION_ENABLE_DEBUG_CHECKING</c>: the library checks its inputs.
    /// </summary>
    const uint EnableDebugChecking = 1 << 6;

    /// <summary>
    /// <c>ffxCreateContextDescFrameGeneration</c>: the context's flags, the display and largest render sizes, and the back buffer's format.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct CreateDesc
    {
        /// <summary>
        /// The descriptor's type (<see cref="CreateType"/>) and the backend descriptor after it.
        /// </summary>
        public Header Header;

        /// <summary>
        /// The creation flags (<see cref="EnableDebugChecking"/> when <c>MEITOU_UPSCALER_LOG=1</c>).
        /// </summary>
        public uint Flags;

        /// <summary>
        /// The display size and the largest render size.
        /// </summary>
        public Dimensions DisplaySize, MaxRenderSize;

        /// <summary>
        /// The swapchain's format (FfxApiSurfaceFormat).
        /// </summary>
        public uint BackBufferFormat;
    }

    /// <summary>
    /// <c>ffxCreateBackendVKDesc</c>: the Vulkan device the context runs on.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct BackendVkDesc
    {
        /// <summary>
        /// The descriptor's type (<see cref="BackendVkType"/>).
        /// </summary>
        public Header Header;

        /// <summary>
        /// The device, the physical device and the loader's <c>vkGetDeviceProcAddr</c>.
        /// </summary>
        public nint Device, PhysicalDevice, DeviceProcAddr;
    }

    /// <summary>
    /// <c>ffxConfigureDescFrameGeneration</c>: the swapchain, the callbacks and whether this frame is generated.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct ConfigureDesc
    {
        /// <summary>
        /// The descriptor's type (<see cref="ConfigureType"/>).
        /// </summary>
        public Header Header;

        /// <summary>
        /// The swapchain, the present callback and its context (unused: the layer composes the interface), and the generation callback (<see
        /// cref="OnGenerate"/>) and its context (the context slot).
        /// </summary>
        public nint SwapChain, PresentCallback, PresentCallbackUserContext, FrameGenerationCallback, FrameGenerationCallbackUserContext;

        /// <summary>
        /// Whether frame generation is on for the frame, and whether its work may run asynchronously.
        /// </summary>
        public byte FrameGenerationEnabled, AllowAsyncWorkloads;

        /// <summary>
        /// The colour without the interface (unused: the world is presented alone).
        /// </summary>
        public Resource HudLessColor;

        /// <summary>
        /// The configuration flags.
        /// </summary>
        public uint Flags;

        /// <summary>
        /// Whether only the generated frames are presented.
        /// </summary>
        public byte OnlyPresentGenerated;

        /// <summary>
        /// The part of the display generated (zero: the whole display).
        /// </summary>
        public Rect GenerationRect;

        /// <summary>
        /// The frame's number (<see cref="FrameId"/>).
        /// </summary>
        public ulong FrameId;
    }

    /// <summary>
    /// <c>ffxDispatchDescFrameGenerationPrepare</c>: one rendered frame's depth, motion and camera.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct PrepareDesc
    {
        /// <summary>
        /// The descriptor's type (<see cref="PrepareType"/>) and the camera info after it.
        /// </summary>
        public Header Header;

        /// <summary>
        /// The frame's number.
        /// </summary>
        public ulong FrameId;

        /// <summary>
        /// The dispatch flags.
        /// </summary>
        public uint Flags;

        /// <summary>
        /// The command buffer the dispatch is recorded into.
        /// </summary>
        public nint CommandList;

        /// <summary>
        /// The render size.
        /// </summary>
        public Dimensions RenderSize;

        /// <summary>
        /// The jitter in pixels (y negated for the flipped rows), and the scale that turns our motion vectors into FFX's.
        /// </summary>
        public Coords JitterOffset, MotionVectorScale;

        /// <summary>
        /// The frame time in milliseconds.
        /// </summary>
        public float FrameTimeDelta;

        /// <summary>
        /// Unused (the reset is the frame number's step).
        /// </summary>
        public byte UnusedReset;

        /// <summary>
        /// The camera's near and far planes, its vertical field of view, and the view space to metres factor (0.1: Kenshi units taken as decimetres).
        /// </summary>
        public float CameraNear, CameraFar, CameraFovAngleVertical, ViewSpaceToMetersFactor;

        /// <summary>
        /// The depth and the motion vectors, flipped top-down.
        /// </summary>
        public Resource Depth, MotionVectors;
    }

    /// <summary>
    /// Three floats: a position or direction in <see cref="CameraInfoDesc"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Float3
    {
        /// <summary>
        /// The components.
        /// </summary>
        public float X, Y, Z;
    }

    /// <summary>
    /// The camera info chained to the prepare dispatch (<see cref="CameraInfoType"/>): the camera's position and axes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct CameraInfoDesc
    {
        /// <summary>
        /// The descriptor's type (<see cref="CameraInfoType"/>).
        /// </summary>
        public Header Header;

        /// <summary>
        /// The camera's position and its up, right and forward axes.
        /// </summary>
        public Float3 Position, Up, Right, Forward;
    }

    #endregion

    #region Context

    /// <summary>
    /// The device context the copies are recorded on.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The Vulkan device.
    /// </summary>
    readonly VulkanDevice _device;

    /// <summary>
    /// The FFX swapchain layer the frames are presented through.
    /// </summary>
    readonly FfxSwapchain _layer;

    /// <summary>
    /// <c>ffxCreateContext</c>.
    /// </summary>
    readonly delegate* unmanaged<nint*, void*, void*, uint> _createContext;

    /// <summary>
    /// <c>ffxDestroyContext</c>.
    /// </summary>
    readonly delegate* unmanaged<nint*, void*, uint> _destroyContext;

    /// <summary>
    /// <c>ffxConfigure</c>.
    /// </summary>
    readonly delegate* unmanaged<nint*, void*, uint> _configure;

    /// <summary>
    /// <c>ffxDispatch</c>: static, since the generation callback has no way to carry it.
    /// </summary>
    static delegate* unmanaged<nint*, void*, uint> s_dispatchFunction;

    /// <summary>
    /// Whether the library's debug checking and messages are on (<c>MEITOU_UPSCALER_LOG=1</c>).
    /// </summary>
    readonly bool _debug;

    /// <summary>
    /// The create descriptor, kept as long as the context (ffx_api.h).
    /// </summary>
    readonly CreateDesc* _createDesc;

    /// <summary>
    /// The backend descriptor, kept as long as the context (ffx_api.h).
    /// </summary>
    readonly BackendVkDesc* _backendDesc;

    /// <summary>
    /// The context's slot, kept as long as the context: also the generation callback's user context (0: no context).
    /// </summary>
    readonly nint* _contextSlot;

    /// <summary>
    /// The display size and back buffer format the context was made for.
    /// </summary>
    (int, int, uint) _contextSize;

    /// <summary>
    /// The depth, motion and interface pictures flipped top-down for the layer.
    /// </summary>
    GpuTexture? _depthFlipped, _motionFlipped, _uiFlipped;

    /// <summary>
    /// The number of the last frame handed over (<see cref="FrameId"/>).
    /// </summary>
    ulong _frameId;

    /// <summary>
    /// The frame numbering's next step restarts the logic; this frame's number is assigned; the context could not be made or a call failed (off for
    /// the run); the layer went away; an interface picture is registered; the one-time log lines were written.
    /// </summary>
    bool _resetNext = true, _idAssigned, _failed, _layerGone, _uiRegistered, _loggedActive, _loggedUnprepared;

    /// <summary>
    /// The display size of the frame being made.
    /// </summary>
    int _displayWidth, _displayHeight;

    /// <summary>
    /// Frame generation is wanted for this frame (<see cref="BeginFrame"/>).
    /// </summary>
    public bool Wanted { get; private set; }
    /// <summary>
    /// The world is shown alone; the layer lays the registered interface over it.
    /// </summary>
    public bool PresentsWorldAlone => true;
    /// <summary>
    /// The post chain gave depth and motion this frame (<see cref="Prepare"/>), so the frame can be interpolated.
    /// </summary>
    public bool Prepared { get; private set; }
    /// <summary>
    /// This frame goes out with frame generation on (<see cref="Finish"/>): wanted and prepared.
    /// </summary>
    public bool Enabled { get; private set; }
    /// <summary>
    /// The number of the last frame handed over (it steps by 1 per rendered frame, by 2 where the logic must restart).
    /// </summary>
    public ulong FrameId => _frameId;

    /// <summary>
    /// Binds the library's functions, allocates the descriptors that live with the context, and follows the layer's disposal.
    /// </summary>
    FrameGeneration(GpuContext ctx, FfxSwapchain layer, nint library)
    {
        this._ctx = ctx;
        _device = ctx.Device;
        this._layer = layer;
        _createContext = (delegate* unmanaged<nint*, void*, void*, uint>)NativeLibrary.GetExport(library, "ffxCreateContext");
        _destroyContext = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxDestroyContext");
        _configure = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxConfigure");
        s_dispatchFunction = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(library, "ffxDispatch");
        _debug = Environment.GetEnvironmentVariable("MEITOU_UPSCALER_LOG") == "1";
        _createDesc = (CreateDesc*)NativeMemory.AllocZeroed((nuint)sizeof(CreateDesc));
        _backendDesc = (BackendVkDesc*)NativeMemory.AllocZeroed((nuint)sizeof(BackendVkDesc));
        _contextSlot = (nint*)NativeMemory.AllocZeroed((nuint)sizeof(nint));
        layer.Disposing += OnLayerDisposing;
    }

    /// <summary>
    /// The layer goes away first (a failed swapchain remake falls back to the plain swapchain): the context goes before it, and none is made again.
    /// Not configured off first: after a failed remake the swapchain it was configured with may already be destroyed, and no present comes in
    /// between.
    /// </summary>
    void OnLayerDisposing()
    {
        _layerGone = true;
        DestroyContext(configureOff: false);
    }

    /// <summary>
    /// The frame-generation context for <paramref name="layer"/>'s swapchain, or null with the reason (library not loading).
    /// </summary>
    public static FrameGeneration? TryCreate(GpuContext ctx, FfxSwapchain layer, out string? reason)
    {
        if (!FfxSwapchain.LibraryLoads(out var library, out reason)) return null;
        if (!NativeLibrary.TryGetExport(library, "ffxConfigure", out _) || !NativeLibrary.TryGetExport(library, "ffxDispatch", out _))
        {
            reason = "the FFX library lacks ffxConfigure / ffxDispatch";
            return null;
        }

        return new FrameGeneration(ctx, layer, library);
    }

    /// <summary>
    /// Called by the FFX layer at the present of each rendered frame: runs the interpolation, as the SDK's samples do (ffxDispatch on this context).
    /// </summary>
    [UnmanagedCallersOnly]
    static uint OnGenerate(void* desc, void* user)
    {
        try
        {
            return s_dispatchFunction((nint*)user, desc);
        }
        catch (Exception)
        {
            return 1;   // nothing may escape into the library
        }
    }

    /// <summary>
    /// A message callback for the library, errors and warnings to the console (registered nowhere at present).
    /// </summary>
    [UnmanagedCallersOnly]
    static void OnMessage(uint type, char* message)
    {
        string level = type == 0 ? "error" : "warning";
        Console.WriteLine($"frame generation  {level}: {FsrUpscaler.WideString(message)}");
    }

    /// <summary>
    /// Makes the context for the display size and the layer's format (again when they changed); false when it cannot be made.
    /// </summary>
    bool EnsureContext(int displayW, int displayH)
    {
        var size = (displayW, displayH, _layer.SurfaceFormat);
        if (*_contextSlot != 0 && _contextSize == size) return true;
        DestroyContext();
        *_backendDesc = new BackendVkDesc
        {
            Header = new Header { Type = BackendVkType },
            Device = _device.Device.Handle,
            PhysicalDevice = _device.PhysicalDevice.Handle,
            DeviceProcAddr = NativeLibrary.GetExport(NativeLibrary.Load(FsrUpscaler.VulkanLoaderName), "vkGetDeviceProcAddr"),
        };
        *_createDesc = new CreateDesc
        {
            Header = new Header { Type = CreateType, Next = (nint)_backendDesc },
            // Depth 0..1 not inverted with finite planes, motion in render pixels without the jitter, LDR colour: the defaults.
            Flags = _debug ? EnableDebugChecking : 0,
            DisplaySize = new Dimensions { Width = (uint)displayW, Height = (uint)displayH },
            MaxRenderSize = new Dimensions { Width = (uint)displayW, Height = (uint)displayH },
            BackBufferFormat = _layer.SurfaceFormat,
        };
        nint created = 0;
        uint rc = _createContext(&created, _createDesc, null);
        if (rc != 0)
        {
            Console.WriteLine($"frame generation  ffxCreateContext failed ({rc})");
            return false;
        }

        *_contextSlot = created;
        _contextSize = size;
        _resetNext = true;
        Console.WriteLine($"frame generation  context made for {displayW}x{displayH}");
        return true;
    }

    /// <summary>
    /// Destroys the context: configured off first (<paramref name="configureOff"/>), after the device has gone idle.
    /// </summary>
    void DestroyContext(bool configureOff = true)
    {
        if (*_contextSlot == 0) return;

        // The layer may still call back into it from a present in flight: configure it off, wait for the device, then destroy.
        if (configureOff) ConfigureFrame(false, _frameId);
        _device.WaitIdle();
        nint c = *_contextSlot;
        _destroyContext(&c, null);
        *_contextSlot = 0;
    }

    #endregion

    #region Frames

    /// <summary>
    /// Starts a frame of <paramref name="displayW"/> × <paramref name="displayH"/>; <paramref name="wanted"/> is false under the loading screen and
    /// for screenshots and recorded frames.
    /// </summary>
    public void BeginFrame(bool wanted, int displayW, int displayH)
    {
        (_displayWidth, _displayHeight) = (displayW, displayH);
        Prepared = Enabled = false;
        _idAssigned = false;
        Wanted = wanted && !_failed && !_layerGone && EnsureContext(displayW, displayH);
        if (wanted && !Wanted) _failed = true;   // the context could not be made: off for the rest of the run
    }

    /// <summary>
    /// This frame's number: assigned once a frame, stepping by 2 (a restart) on a cut or after frames without frame generation, else by 1.
    /// </summary>
    ulong NextId(bool reset)
    {
        if (!_idAssigned)
        {
            // Any step other than 1 restarts the logic (the header's rule): skip a number on a cut, after frames without it, on a new context.
            _frameId += reset || _resetNext ? 2UL : 1UL;
            _resetNext = false;
            _idAssigned = true;
        }
        return _frameId;
    }

    /// <summary>
    /// Copies <paramref name="src"/> into <paramref name="dst"/> upside down (both in GENERAL): bottom-up to top-down.
    /// </summary>
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

    /// <summary>
    /// <see cref="Target(GpuContext, ref GpuTexture?, InternalFormat, int, int, string)"/> on this generator's context.
    /// </summary>
    GpuTexture Target(ref GpuTexture? slot, InternalFormat format, int w, int h, string name) => Target(_ctx, ref slot, format, w, h, name);

    /// <summary>
    /// A copy target of the size and format asked, made again when they change.
    /// </summary>
    internal static GpuTexture Target(GpuContext ctx, ref GpuTexture? slot, InternalFormat format, int w, int h, string name)
    {
        if (slot is null || slot.Desc.Width != w || slot.Desc.Height != h || slot.Desc.Format != format)
        {
            slot?.Dispose();   // released after the frames in flight
            slot = ctx.CreateTexture(new TextureDesc(format, w, h, Use: TextureUse.Sampled | TextureUse.TransferSrc | TextureUse.TransferDst,
                Name: name));
        }
        return slot;
    }

    /// <summary>
    /// Copies depth and motion flipped into targets of their own and records the Prepare dispatch (depth, motion, jitter, camera, frame time) in the
    /// frame. Call after the post chain made them, outside a native segment. False (frame generation off for this frame) when the dispatch fails.
    /// </summary>
    public bool Prepare(FrameGenInputs i)
    {
        if (!Wanted || _failed) return false;
        var depth = Target(ref _depthFlipped, i.Depth.Desc.Format, i.RenderWidth, i.RenderHeight, "fg depth top-down");
        var motion = Target(ref _motionFlipped, i.Motion.Desc.Format, i.RenderWidth, i.RenderHeight, "fg motion top-down");
        var list = (CommandList)_ctx.BeginNative("fg flip depth and motion");
        Flip(_device.Vk, list.Handle, _device.NativeImage(i.Depth), _device.NativeImage(depth), i.RenderWidth, i.RenderHeight);
        Flip(_device.Vk, list.Handle, _device.NativeImage(i.Motion), _device.NativeImage(motion), i.RenderWidth, i.RenderHeight);
        _ctx.EndNative(list);

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
            // Ours: UV from previous to current, y up. FFX: render pixels from current towards previous, y down (the rows were flipped):
            // x as for the upscalers, y the other sign.
            MotionVectorScale = new Coords { X = -i.RenderWidth, Y = i.RenderHeight },
            FrameTimeDelta = i.DeltaSeconds * 1000,
            CameraNear = i.Near,
            CameraFar = i.Far,
            CameraFovAngleVertical = i.FieldOfView,
            ViewSpaceToMetersFactor = 0.1f,   // as for the upscalers: Kenshi units taken as decimetres
            Depth = Input(_ctx, depth),
            MotionVectors = Input(_ctx, motion),
        };
        var cmd = (CommandList)_ctx.BeginNative("fg prepare");
        desc.CommandList = cmd.Handle.Handle;
        uint rc = s_dispatchFunction(_contextSlot, &desc);
        _ctx.EndNative(cmd);
        if (rc != 0)
        {
            Console.WriteLine($"frame generation  prepare dispatch failed ({rc}); off for the rest of the run");
            _failed = true;
            return false;
        }

        Prepared = true;
        return true;
    }

    /// <summary>
    /// Configures the context for frame <paramref name="id"/> with frame generation on or off; true when there is no context yet or the call
    /// succeeds.
    /// </summary>
    bool ConfigureFrame(bool enabled, ulong id)
    {
        if (*_contextSlot == 0) return true;   // no context yet (never wanted so far): nothing to configure
        var d = new ConfigureDesc
        {
            Header = new Header { Type = ConfigureType },
            SwapChain = (nint)_layer.Swapchain.Handle,
            // No present callback: the layer composes the registered interface picture itself.
            FrameGenerationCallback = (nint)(delegate* unmanaged<void*, void*, uint>)&OnGenerate,
            FrameGenerationCallbackUserContext = (nint)_contextSlot,
            FrameGenerationEnabled = (byte)(enabled ? 1 : 0),
            // GenerationRect left zero: the whole display (the SDK's provider fills it in when width and height are 0).
            FrameId = id,
        };
        return _configure(_contextSlot, &d) == 0;
    }

    /// <summary>
    /// Before the present: hands the interface picture (<paramref name="ui"/>, the bottom-up premultiplied RGBA8 layer; flipped here) to the layer
    /// and configures the context for this frame. Frame generation runs on this frame's present only when it was wanted and prepared. True when it
    /// does.
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

        if (Enabled && !_loggedActive)
        {
            _loggedActive = true;
            Console.WriteLine($"frame generation  active (frame {_frameId + 1})");
        }

        ulong id = NextId(false);
        Resource? registered = null;
        if (Enabled)
        {
            var flipped = Target(ref _uiFlipped, InternalFormat.Rgba8, _displayWidth, _displayHeight, "fg ui top-down");
            var list = (CommandList)_ctx.BeginNative("fg flip ui");
            Flip(_device.Vk, list.Handle, _device.NativeImage(ui!), _device.NativeImage(flipped), _displayWidth, _displayHeight);
            _ctx.EndNative(list);
            registered = Input(_ctx, flipped);
        }
        else _resetNext = true;   // the next frame after frames without it starts afresh
        if (!ConfigureFrame(Enabled, id))
        {
            Console.WriteLine("frame generation  configure failed; off for the rest of the run");
            _failed = true;
            Enabled = false;
        }

        if ((registered is not null || _uiRegistered) && !_layerGone)
        {
            if (!_layer.RegisterUi(Enabled ? registered : null))
            {
                Console.WriteLine("frame generation  registering the interface picture failed; off for the rest of the run");
                _failed = true;
                Enabled = false;
            }

            _uiRegistered = Enabled;
        }
        return Enabled;
    }

    #endregion

    #region Disposal

    /// <summary>
    /// Configures the context off (unless the layer went first) and destroys it, then frees the flipped pictures and the descriptors.
    /// </summary>
    public void Dispose()
    {
        _layer.Disposing -= OnLayerDisposing;
        DestroyContext(configureOff: !_layerGone);
        _depthFlipped?.Dispose();
        _motionFlipped?.Dispose();
        _uiFlipped?.Dispose();
        NativeMemory.Free(_createDesc);
        NativeMemory.Free(_backendDesc);
        NativeMemory.Free(_contextSlot);
    }

    #endregion
}