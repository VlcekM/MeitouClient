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
    SimulationStart = 0,
    SimulationEnd = 1,
    RenderSubmitStart = 2,
    RenderSubmitEnd = 3,
    PresentStart = 4,
    PresentEnd = 5,
}

/// <summary>
/// The Streamline 2.14.16 structures DLSS and DLSS frame generation are driven with, laid out as the SDK's headers (<c>sl_consts.h</c>,
/// <c>sl_core_types.h</c>, <c>sl_dlss.h</c>, <c>sl_dlss_g.h</c>, <c>sl_reflex.h</c>): every structure starts with a next pointer, its GUID and its version
/// (<c>sl_struct.h</c>).
/// </summary>
static unsafe class SlTypes
{
    // Buffer types (sl_core_types.h).
    public const uint BufferDepth = 0, BufferMotionVectors = 1, BufferHudlessColor = 2, BufferScalingInputColor = 3, BufferScalingOutputColor = 4,
        BufferExposure = 13, BufferUiColorAndAlpha = 23, BufferBiasCurrentColorHint = 29;

    // Features (sl_core_types.h).
    public const uint FeatureDlss = 0, FeatureReflex = 3, FeaturePcl = 4, FeatureDlssG = 1000;

    // sl::ResourceLifecycle.
    public const int ValidUntilPresent = 1, ValidUntilEvaluate = 2;

    public static readonly Guid ViewportType = new("171b6435-9b3c-4fc8-9994-fbe52569aaa4");
    public static readonly Guid ResourceType = new("3a9d70cf-2418-4b72-8391-13f8721c7261");
    public static readonly Guid ResourceTagType = new("4c6a5aad-b445-496c-87ff-1af3845be653");
    public static readonly Guid ConstantsType = new("dcd35ad7-4e4a-4bad-a90c-e0c49eb23afe");
    public static readonly Guid DlssOptionsType = new("6ac826e4-4c61-4101-a92d-638d421057b8");
    public static readonly Guid DlssgOptionsType = new("fac5f1cb-2dfd-4f36-a1e6-3a9e865256c5");
    public static readonly Guid DlssgStateType = new("cc8ac8e1-a179-44f5-97fa-e74112f9bc61");
    public static readonly Guid ReflexOptionsType = new("f03af81a-6d0b-4902-a651-c4965e215434");

    [StructLayout(LayoutKind.Sequential)]
    public struct ViewportHandle
    {
        public nint Next;
        public Guid StructType;
        public nuint StructVersion;
        public uint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Extent
    {
        public uint Top, Left, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SlResource
    {
        public nint Next;
        public Guid StructType;
        public nuint StructVersion;
        public byte Type;
        public nint Native, Memory, View;
        public uint State;
        public uint Width, Height, NativeFormat, MipLevels, ArrayLayers;
        public ulong GpuVirtualAddress;
        public uint Flags, Usage;
        public ushort InternalFlags, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ResourceTag
    {
        public nint Next;
        public Guid StructType;
        public nuint StructVersion;
        public SlResource* Resource;
        public uint Type;
        public int Lifecycle;
        public Extent Extent;
    }

    /// <summary>
    /// sl::Constants (version 2): the camera of one frame and viewport. Matrices are row-major, without the jitter.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public nint Next;
        public Guid StructType;
        public nuint StructVersion;
        public Matrix4x4 CameraViewToClip, ClipToCameraView, ClipToLensClip, ClipToPrevClip, PrevClipToClip;
        public Vector2 JitterOffset, MvecScale, CameraPinholeOffset;
        public Vector3 CameraPos, CameraUp, CameraRight, CameraFwd;
        public float CameraNear, CameraFar, CameraFov, CameraAspectRatio, MotionVectorsInvalidValue;
        public byte DepthInverted, CameraMotionIncluded, MotionVectors3D, Reset, OrthographicProjection, MotionVectorsDilated, MotionVectorsJittered;
        public float MinRelativeLinearDepthObjectSeparation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DlssOptions
    {
        public nint Next;
        public Guid StructType;
        public nuint StructVersion;
        public uint Mode, OutputWidth, OutputHeight;
        public float Sharpness, PreExposure, ExposureScale;
        public byte ColorBuffersHdr, IndicatorInvertAxisX, IndicatorInvertAxisY;
        public uint DlaaPreset, QualityPreset, BalancedPreset, PerformancePreset, UltraPerformancePreset, UltraQualityPreset;
        public byte UseAutoExposure, AlphaUpscalingEnabled;
    }

    /// <summary>
    /// sl::DLSSGOptions (version 5). Mode 0 off, 1 on; Boolean members are sl::Boolean (0 false, 1 true, 2 invalid).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DlssgOptions
    {
        public nint Next;
        public Guid StructType;
        public nuint StructVersion;
        public uint Mode, NumFramesToGenerate, Flags, DynamicResWidth, DynamicResHeight, NumBackBuffers, MvecDepthWidth, MvecDepthHeight, ColorWidth,
            ColorHeight;
        public uint ColorBufferFormat, MvecBufferFormat, DepthBufferFormat, HudLessBufferFormat, UiBufferFormat;
        public nint OnErrorCallback;
        public byte Reserved15;
        public uint QueueParallelismMode;
        public byte EnableUserInterfaceRecomposition;
        public float DynamicTargetFrameRate;
    }

    /// <summary>
    /// sl::DLSSGState (version 4).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DlssgState
    {
        public nint Next;
        public Guid StructType;
        public nuint StructVersion;
        public ulong EstimatedVramUsageInBytes;
        public uint Status, MinWidthOrHeight, NumFramesActuallyPresented, NumFramesToGenerateMax;
        public byte Reserved4, IsVsyncSupportAvailable;
        public nint InputsProcessingCompletionFence;
        public ulong LastPresentInputsProcessingCompletionFenceValue;
        public byte IsDynamicMfgSupported;
    }

    /// <summary>
    /// sl::ReflexOptions (version 1). Mode 0 off, 1 low latency, 2 low latency with boost.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ReflexOptions
    {
        public nint Next;
        public Guid StructType;
        public nuint StructVersion;
        public int Mode;
        public uint FrameLimitUs;
        public byte UseMarkersToOptimize;
        public ushort VirtualKey;
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
    public static Constants CameraConstants(Matrix4x4 viewToClip, Matrix4x4 clipToPrevious, Vector2 jitterPixels, Vector3 eye, Vector3 up, Vector3 right,
        Vector3 forward, float near, float far, float fov, float aspect, bool reset, bool bottomUp)
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
