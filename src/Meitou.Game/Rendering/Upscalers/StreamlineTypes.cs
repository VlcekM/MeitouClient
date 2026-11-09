using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Upscalers;

/// <summary>
/// Streamline's PC Latency markers (sl_pcl.h PCLMarker): where a rendered frame is, for Reflex.
/// </summary>
public enum PclMarker : uint
{
    /// <summary>
    /// The game loop's simulation of the frame starts.
    /// </summary>
    SimulationStart = 0,

    /// <summary>
    /// The frame's simulation ends.
    /// </summary>
    SimulationEnd = 1,

    /// <summary>
    /// The render thread starts recording and submitting the frame.
    /// </summary>
    RenderSubmitStart = 2,

    /// <summary>
    /// The frame's submission ends.
    /// </summary>
    RenderSubmitEnd = 3,

    /// <summary>
    /// The frame's present starts.
    /// </summary>
    PresentStart = 4,

    /// <summary>
    /// The frame's present ends.
    /// </summary>
    PresentEnd = 5,
}

/// <summary>
/// The Streamline 2.14.16 structures DLSS and DLSS frame generation are driven with, laid out as the SDK's headers (<c>sl_consts.h</c>,
/// <c>sl_core_types.h</c>, <c>sl_dlss.h</c>, <c>sl_dlss_g.h</c>, <c>sl_reflex.h</c>): every structure starts with a next pointer, its GUID and its
/// version (<c>sl_struct.h</c>).
/// </summary>
static unsafe class SlTypes
{
    /// <summary>
    /// Buffer types (sl_core_types.h, sl::BufferType): the tags the upscaler and frame generation inputs are given.
    /// </summary>
    public const uint BufferDepth = 0, BufferMotionVectors = 1, BufferHudlessColor = 2, BufferScalingInputColor = 3, BufferScalingOutputColor = 4,
        BufferExposure = 13, BufferUiColorAndAlpha = 23, BufferBiasCurrentColorHint = 29;

    /// <summary>
    /// Features (sl_core_types.h, sl::Feature): DLSS, Reflex, PC Latency and DLSS frame generation.
    /// </summary>
    public const uint FeatureDlss = 0, FeatureReflex = 3, FeaturePcl = 4, FeatureDlssG = 1000;

    /// <summary>
    /// sl::ResourceLifecycle: a tagged resource stays valid until the present, or only until the evaluate call.
    /// </summary>
    public const int ValidUntilPresent = 1, ValidUntilEvaluate = 2;

    /// <summary>
    /// The GUID of <see cref="ViewportHandle"/> (sl::ViewportHandle).
    /// </summary>
    public static readonly Guid ViewportType = new("171b6435-9b3c-4fc8-9994-fbe52569aaa4");

    /// <summary>
    /// The GUID of <see cref="SlResource"/> (sl::Resource).
    /// </summary>
    public static readonly Guid ResourceType = new("3a9d70cf-2418-4b72-8391-13f8721c7261");

    /// <summary>
    /// The GUID of <see cref="ResourceTag"/> (sl::ResourceTag).
    /// </summary>
    public static readonly Guid ResourceTagType = new("4c6a5aad-b445-496c-87ff-1af3845be653");

    /// <summary>
    /// The GUID of <see cref="Constants"/> (sl::Constants).
    /// </summary>
    public static readonly Guid ConstantsType = new("dcd35ad7-4e4a-4bad-a90c-e0c49eb23afe");

    /// <summary>
    /// The GUID of <see cref="DlssOptions"/> (sl::DLSSOptions).
    /// </summary>
    public static readonly Guid DlssOptionsType = new("6ac826e4-4c61-4101-a92d-638d421057b8");

    /// <summary>
    /// The GUID of <see cref="DlssgOptions"/> (sl::DLSSGOptions).
    /// </summary>
    public static readonly Guid DlssgOptionsType = new("fac5f1cb-2dfd-4f36-a1e6-3a9e865256c5");

    /// <summary>
    /// The GUID of <see cref="DlssgState"/> (sl::DLSSGState).
    /// </summary>
    public static readonly Guid DlssgStateType = new("cc8ac8e1-a179-44f5-97fa-e74112f9bc61");

    /// <summary>
    /// The GUID of <see cref="ReflexOptions"/> (sl::ReflexOptions).
    /// </summary>
    public static readonly Guid ReflexOptionsType = new("f03af81a-6d0b-4902-a651-c4965e215434");

    /// <summary>
    /// sl::ViewportHandle: the viewport a feature's calls are for.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ViewportHandle
    {
        /// <summary>
        /// The next structure in the chain (0: none).
        /// </summary>
        public nint Next;

        /// <summary>
        /// The structure's GUID, <see cref="ViewportType"/>.
        /// </summary>
        public Guid StructType;

        /// <summary>
        /// The structure's version.
        /// </summary>
        public nuint StructVersion;

        /// <summary>
        /// The viewport's number.
        /// </summary>
        public uint Value;
    }

    /// <summary>
    /// sl::Extent: a rectangle of an image, in pixels.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Extent
    {
        /// <summary>
        /// The rectangle's top and left corner and its size.
        /// </summary>
        public uint Top, Left, Width, Height;
    }

    /// <summary>
    /// sl::Resource: one image as Streamline sees it, its native Vulkan handles and description (<see cref="Resource"/> fills it).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SlResource
    {
        /// <summary>
        /// The next structure in the chain (0: none).
        /// </summary>
        public nint Next;

        /// <summary>
        /// The structure's GUID, <see cref="ResourceType"/>.
        /// </summary>
        public Guid StructType;

        /// <summary>
        /// The structure's version.
        /// </summary>
        public nuint StructVersion;

        /// <summary>
        /// The resource's kind (0: a 2D texture).
        /// </summary>
        public byte Type;

        /// <summary>
        /// The native image, its memory and its view (VkImage, VkDeviceMemory, VkImageView).
        /// </summary>
        public nint Native, Memory, View;

        /// <summary>
        /// The image's layout (a VkImageLayout).
        /// </summary>
        public uint State;

        /// <summary>
        /// The image's size, format (a VkFormat), mip levels and array layers.
        /// </summary>
        public uint Width, Height, NativeFormat, MipLevels, ArrayLayers;

        /// <summary>
        /// The buffer's GPU address (unused for images).
        /// </summary>
        public ulong GpuVirtualAddress;

        /// <summary>
        /// The image's create flags and usage (VkImageUsageFlags).
        /// </summary>
        public uint Flags, Usage;

        /// <summary>
        /// Streamline's own flags, and padding.
        /// </summary>
        public ushort InternalFlags, Reserved;
    }

    /// <summary>
    /// sl::ResourceTag: what a tagged resource is (its buffer type), how long it stays valid and the part of it in use.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ResourceTag
    {
        /// <summary>
        /// The next structure in the chain (0: none).
        /// </summary>
        public nint Next;

        /// <summary>
        /// The structure's GUID, <see cref="ResourceTagType"/>.
        /// </summary>
        public Guid StructType;

        /// <summary>
        /// The structure's version.
        /// </summary>
        public nuint StructVersion;

        /// <summary>
        /// The resource tagged.
        /// </summary>
        public SlResource* Resource;

        /// <summary>
        /// Its buffer type (<see cref="BufferDepth"/> and the others).
        /// </summary>
        public uint Type;

        /// <summary>
        /// How long the resource stays valid (<see cref="ValidUntilPresent"/>, <see cref="ValidUntilEvaluate"/>).
        /// </summary>
        public int Lifecycle;

        /// <summary>
        /// The part of the resource in use.
        /// </summary>
        public Extent Extent;
    }

    /// <summary>
    /// sl::Constants (version 2): the camera of one frame and viewport. Matrices are row-major, without the jitter.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        /// <summary>
        /// The next structure in the chain (0: none).
        /// </summary>
        public nint Next;

        /// <summary>
        /// The structure's GUID, <see cref="ConstantsType"/>.
        /// </summary>
        public Guid StructType;

        /// <summary>
        /// The structure's version.
        /// </summary>
        public nuint StructVersion;

        /// <summary>
        /// The camera's projection, its inverse, the lens distortion (identity), and the reprojections to the previous frame's clip space and back.
        /// </summary>
        public Matrix4x4 CameraViewToClip, ClipToCameraView, ClipToLensClip, ClipToPrevClip, PrevClipToClip;

        /// <summary>
        /// The jitter in pixels, the scale that turns our motion vectors into Streamline's, and the pinhole offset.
        /// </summary>
        public Vector2 JitterOffset, MvecScale, CameraPinholeOffset;

        /// <summary>
        /// The camera's position and its up, right and forward axes.
        /// </summary>
        public Vector3 CameraPos, CameraUp, CameraRight, CameraFwd;

        /// <summary>
        /// The camera's near and far planes, vertical field of view and aspect ratio, and the motion vector value that marks none.
        /// </summary>
        public float CameraNear, CameraFar, CameraFov, CameraAspectRatio, MotionVectorsInvalidValue;

        /// <summary>
        /// Booleans (0 / 1): reversed depth, camera motion included in the motion vectors, 3D motion vectors, a history reset, an orthographic
        /// projection, dilated and jittered motion vectors.
        /// </summary>
        public byte DepthInverted, CameraMotionIncluded, MotionVectors3D, Reset, OrthographicProjection, MotionVectorsDilated, MotionVectorsJittered;

        /// <summary>
        /// The smallest relative linear depth difference that separates objects.
        /// </summary>
        public float MinRelativeLinearDepthObjectSeparation;
    }

    /// <summary>
    /// sl::DLSSOptions: the DLSS mode, output size, sharpness, exposure and the presets per quality mode.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DlssOptions
    {
        /// <summary>
        /// The next structure in the chain (0: none).
        /// </summary>
        public nint Next;

        /// <summary>
        /// The structure's GUID, <see cref="DlssOptionsType"/>.
        /// </summary>
        public Guid StructType;

        /// <summary>
        /// The structure's version.
        /// </summary>
        public nuint StructVersion;

        /// <summary>
        /// The DLSS quality mode (sl::DLSSMode) and the output's size.
        /// </summary>
        public uint Mode, OutputWidth, OutputHeight;

        /// <summary>
        /// The sharpening, the pre-exposure the colour was multiplied by, and the exposure scale.
        /// </summary>
        public float Sharpness, PreExposure, ExposureScale;

        /// <summary>
        /// Booleans (0 / 1): HDR colour buffers, and the on-screen indicator's mirrored axes.
        /// </summary>
        public byte ColorBuffersHdr, IndicatorInvertAxisX, IndicatorInvertAxisY;

        /// <summary>
        /// The render preset of each quality mode (sl::DLSSPreset; 0 the default).
        /// </summary>
        public uint DlaaPreset, QualityPreset, BalancedPreset, PerformancePreset, UltraPerformancePreset, UltraQualityPreset;

        /// <summary>
        /// Booleans (0 / 1): DLSS's own auto exposure, and alpha upscaling.
        /// </summary>
        public byte UseAutoExposure, AlphaUpscalingEnabled;
    }

    /// <summary>
    /// sl::DLSSGOptions (version 5). Mode 0 off, 1 on; Boolean members are sl::Boolean (0 false, 1 true, 2 invalid).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DlssgOptions
    {
        /// <summary>
        /// The next structure in the chain (0: none).
        /// </summary>
        public nint Next;

        /// <summary>
        /// The structure's GUID, <see cref="DlssgOptionsType"/>.
        /// </summary>
        public Guid StructType;

        /// <summary>
        /// The structure's version.
        /// </summary>
        public nuint StructVersion;

        /// <summary>
        /// The mode, the generated frames per rendered one, the flags, the dynamic resolution's size, the swapchain's back buffers, and the sizes of
        /// the motion vectors and depth and of the colour.
        /// </summary>
        public uint Mode, NumFramesToGenerate, Flags, DynamicResWidth, DynamicResHeight, NumBackBuffers, MvecDepthWidth, MvecDepthHeight, ColorWidth,
            ColorHeight;

        /// <summary>
        /// The native formats of the colour, motion vector, depth, HUD-less and UI buffers.
        /// </summary>
        public uint ColorBufferFormat, MvecBufferFormat, DepthBufferFormat, HudLessBufferFormat, UiBufferFormat;

        /// <summary>
        /// A callback for errors (0: none).
        /// </summary>
        public nint OnErrorCallback;

        /// <summary>
        /// Reserved.
        /// </summary>
        public byte Reserved15;

        /// <summary>
        /// How the frame generation's queue work runs alongside the game's (sl::DLSSGQueueParallelismMode).
        /// </summary>
        public uint QueueParallelismMode;

        /// <summary>
        /// Whether the UI is recomposed over the generated frames (an sl::Boolean).
        /// </summary>
        public byte EnableUserInterfaceRecomposition;

        /// <summary>
        /// The frame rate dynamic multi-frame generation aims at.
        /// </summary>
        public float DynamicTargetFrameRate;
    }

    /// <summary>
    /// sl::DLSSGState (version 4).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DlssgState
    {
        /// <summary>
        /// The next structure in the chain (0: none).
        /// </summary>
        public nint Next;

        /// <summary>
        /// The structure's GUID, <see cref="DlssgStateType"/>.
        /// </summary>
        public Guid StructType;

        /// <summary>
        /// The structure's version.
        /// </summary>
        public nuint StructVersion;

        /// <summary>
        /// The video memory frame generation estimates it uses, in bytes.
        /// </summary>
        public ulong EstimatedVramUsageInBytes;

        /// <summary>
        /// The status flags, the smallest width or height it supports, the frames actually presented last, and the most frames it can generate.
        /// </summary>
        public uint Status, MinWidthOrHeight, NumFramesActuallyPresented, NumFramesToGenerateMax;

        /// <summary>
        /// Reserved, and whether vsync support is available (an sl::Boolean).
        /// </summary>
        public byte Reserved4, IsVsyncSupportAvailable;

        /// <summary>
        /// The fence that signals when the inputs are processed.
        /// </summary>
        public nint InputsProcessingCompletionFence;

        /// <summary>
        /// The fence value of the last present's inputs.
        /// </summary>
        public ulong LastPresentInputsProcessingCompletionFenceValue;

        /// <summary>
        /// Whether dynamic multi-frame generation is supported (an sl::Boolean).
        /// </summary>
        public byte IsDynamicMfgSupported;
    }

    /// <summary>
    /// sl::ReflexOptions (version 1). Mode 0 off, 1 low latency, 2 low latency with boost.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ReflexOptions
    {
        /// <summary>
        /// The next structure in the chain (0: none).
        /// </summary>
        public nint Next;

        /// <summary>
        /// The structure's GUID, <see cref="ReflexOptionsType"/>.
        /// </summary>
        public Guid StructType;

        /// <summary>
        /// The structure's version.
        /// </summary>
        public nuint StructVersion;

        /// <summary>
        /// The Reflex mode (0 off, 1 low latency, 2 low latency with boost).
        /// </summary>
        public int Mode;

        /// <summary>
        /// The frame limit in microseconds (0: none).
        /// </summary>
        public uint FrameLimitUs;

        /// <summary>
        /// Whether Reflex uses the latency markers to optimise.
        /// </summary>
        public byte UseMarkersToOptimize;

        /// <summary>
        /// The virtual key the latency flash indicator answers to.
        /// </summary>
        public ushort VirtualKey;

        /// <summary>
        /// The thread the markers come from.
        /// </summary>
        public uint IdThread;
    }

    /// <summary>
    /// A Streamline description of <paramref name="texture"/>, kept in GENERAL (as every image the upscalers see).
    /// </summary>
    public static SlResource Resource(GpuContext ctx, GpuTexture texture)
    {
        var d = texture.Desc;
        return new SlResource
        {
            StructType = ResourceType,
            StructVersion = 1,
            Type = 0,   // eTex2d
            Native = (nint)ctx.Device.NativeImage(texture).Handle,
            View = (nint)ctx.Device.NativeView(texture).Handle,
            State = (uint)ImageLayout.General,
            Width = (uint)d.Width,
            Height = (uint)d.Height,
            NativeFormat = (uint)GlConventions.VkFormat(d.Format),
            MipLevels = (uint)d.Levels,
            ArrayLayers = 1,
            Usage = (uint)ctx.Device.NativeUsage(texture),
        };
    }

    /// <summary>
    /// A tag of <paramref name="resource"/> as buffer type <paramref name="type"/>, valid for <paramref name="lifecycle"/>, the whole of it in use.
    /// </summary>
    public static ResourceTag Tag(SlResource* resource, uint type, int lifecycle) => new()
    {
        StructType = ResourceTagType,
        StructVersion = 1,
        Resource = resource,
        Type = type,
        Lifecycle = lifecycle,
    };

    /// <summary>
    /// Flips y of a clip space: our clip y = +1 is image row H of a bottom-up picture, Streamline's is row 0.
    /// </summary>
    public static readonly Matrix4x4 FlipY = Matrix4x4.CreateScale(1, -1, 1);

    /// <summary>
    /// The constants of one frame for images stored bottom-up (<paramref name="bottomUp"/>: the post chain's, as DLSS upscales them, so the matrices
    /// get their y flipped and motion is scaled by −1, −1) or top-down (copied flipped, as frame generation takes them: no flip, motion −1, +1,
    /// jitter y negated). Ours: motion in UV from previous to current; Streamline's: normalised, towards the previous position.
    /// </summary>
    public static Constants CameraConstants(Matrix4x4 viewToClip, Matrix4x4 clipToPrevious, Vector2 jitterPixels, Vector3 eye, Vector3 up,
        Vector3 right, Vector3 forward, float near, float far, float fov, float aspect, bool reset, bool bottomUp)
    {
        var toClip = bottomUp ? viewToClip * FlipY : viewToClip;
        Matrix4x4.Invert(toClip, out var clipToView);
        var toPrevious = bottomUp ? FlipY * clipToPrevious * FlipY : clipToPrevious;
        Matrix4x4.Invert(toPrevious, out var fromPrevious);
        return new Constants
        {
            StructType = ConstantsType,
            StructVersion = 2,
            CameraViewToClip = toClip,
            ClipToCameraView = clipToView,
            ClipToLensClip = Matrix4x4.Identity,
            ClipToPrevClip = toPrevious,
            PrevClipToClip = fromPrevious,
            JitterOffset = bottomUp ? jitterPixels : new Vector2(jitterPixels.X, -jitterPixels.Y),
            MvecScale = new Vector2(-1, bottomUp ? -1 : 1),
            CameraPos = eye,
            CameraUp = up,
            CameraRight = right,
            CameraFwd = forward,
            CameraNear = near,
            CameraFar = far,
            CameraFov = fov,
            CameraAspectRatio = aspect,
            MotionVectorsInvalidValue = float.MaxValue,
            CameraMotionIncluded = 1,
            Reset = (byte)(reset ? 1 : 0),
            MinRelativeLinearDepthObjectSeparation = 40,
        };
    }
}
