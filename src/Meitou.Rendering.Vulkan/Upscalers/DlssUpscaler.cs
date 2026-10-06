using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Vulkan.Upscalers;

/// <summary>
/// NVIDIA DLSS Super Resolution through <see cref="Streamline"/> (which must have been initialised before the device and attached to it).
/// Per frame: a frame token, the camera constants, the four tagged images (depth, motion, colour in, colour out) and the evaluation,
/// recorded into <see cref="VkGl"/>'s frame. The DLSS mode follows the render scale (DLAA at 1); the render size stays ours.
/// Conventions as for FSR (<see cref="UpscaleInputs"/>): our images are bottom-up, so the matrices given to DLSS have their y flipped
/// to describe the picture as stored (row 0 at clip y = +1, as DLSS assumes), and motion is scaled by −1 (DLSS: towards the previous position).
/// </summary>
public sealed unsafe class DlssUpscaler : IUpscaler
{
    [StructLayout(LayoutKind.Sequential)] struct ViewportHandle { public nint Next; public Guid StructType; public nuint StructVersion; public uint Value; }
    [StructLayout(LayoutKind.Sequential)] struct Extent { public uint Top, Left, Width, Height; }
    [StructLayout(LayoutKind.Sequential)]
    struct SlResource
    {
        public nint Next; public Guid StructType; public nuint StructVersion;
        public byte Type; public nint Native, Memory, View; public uint State;
        public uint Width, Height, NativeFormat, MipLevels, ArrayLayers; public ulong GpuVirtualAddress; public uint Flags, Usage; public ushort InternalFlags, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)] struct ResourceTag { public nint Next; public Guid StructType; public nuint StructVersion; public SlResource* Resource; public uint Type; public int Lifecycle; public Extent Extent; }
    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public nint Next; public Guid StructType; public nuint StructVersion;
        public Matrix4x4 CameraViewToClip, ClipToCameraView, ClipToLensClip, ClipToPrevClip, PrevClipToClip;
        public Vector2 JitterOffset, MvecScale, CameraPinholeOffset;
        public Vector3 CameraPos, CameraUp, CameraRight, CameraFwd;
        public float CameraNear, CameraFar, CameraFov, CameraAspectRatio, MotionVectorsInvalidValue;
        public byte DepthInverted, CameraMotionIncluded, MotionVectors3D, Reset, OrthographicProjection, MotionVectorsDilated, MotionVectorsJittered;
        public float MinRelativeLinearDepthObjectSeparation;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct DlssOptions
    {
        public nint Next; public Guid StructType; public nuint StructVersion;
        public uint Mode, OutputWidth, OutputHeight; public float Sharpness, PreExposure, ExposureScale;
        public byte ColorBuffersHdr, IndicatorInvertAxisX, IndicatorInvertAxisY;
        public uint DlaaPreset, QualityPreset, BalancedPreset, PerformancePreset, UltraPerformancePreset, UltraQualityPreset;
        public byte UseAutoExposure, AlphaUpscalingEnabled;
    }

    const uint FeatureDlss = 0, BufferDepth = 0, BufferMotionVectors = 1, BufferScalingInputColor = 3, BufferScalingOutputColor = 4, BufferBiasCurrentColorHint = 29;
    const int ValidUntilEvaluate = 2;

    readonly Streamline sl;
    readonly VkGl gl;
    readonly ViewportHandle* viewport;
    uint frameIndex;
    (uint Mode, int Width, int Height) options = (uint.MaxValue, 0, 0);
    bool failed;

    public UpscalerKind Kind => UpscalerKind.Dlss;
    public string Name { get; private set; } = "dlss";

    DlssUpscaler(Streamline sl, VkGl gl)
    {
        this.sl = sl;
        this.gl = gl;
        viewport = (ViewportHandle*)NativeMemory.AllocZeroed((nuint)sizeof(ViewportHandle));
        *viewport = new ViewportHandle { StructType = Streamline.ViewportType, StructVersion = 1, Value = 0 };
    }

    public static DlssUpscaler? TryCreate(Streamline? sl, VkGl gl, out string? reason)
    {
        if (sl is null) { reason = "DLSS needs Streamline loaded before the device (start with --upscaler dlss and the Streamline DLLs)"; return null; }
        if (!sl.DlssSupported) { reason = "DLSS is not supported on this device (see the streamline lines above)"; return null; }
        reason = null;
        Console.WriteLine($"upscaler  DLSS through Streamline from {sl.Directory}");
        return new DlssUpscaler(sl, gl);
    }

    /// <summary>DLSS's mode for a render scale (its presets: DLAA 1, quality 1/1.5, balanced 1/1.72, performance 1/2, ultra performance 1/3).</summary>
    static readonly uint Preset = uint.TryParse(Environment.GetEnvironmentVariable("MEITOU_DLSS_PRESET"), out var p) ? p : 0;

    static uint Mode(float scale) => scale switch
    {
        >= 0.99f => 6,   // eDLAA
        >= 0.64f => 3,   // eMaxQuality
        >= 0.55f => 2,   // eBalanced
        >= 0.45f => 1,   // eMaxPerformance
        _ => 4,          // eUltraPerformance
    };

    /// <summary>Flips y of a clip space: our clip y = +1 is image row H (bottom-up), DLSS's is row 0.</summary>
    static readonly Matrix4x4 FlipY = Matrix4x4.CreateScale(1, -1, 1);

    static SlResource Resource(Meitou.Rendering.Gpu.Texture texture, uint layout)
    {
        var d = texture.Desc;
        return new SlResource
        {
            StructType = Streamline.ResourceType, StructVersion = 1,
            Type = 0,   // eTex2d
            Native = (nint)texture.Image.Handle, View = (nint)texture.Attachment().Handle, State = layout,
            Width = (uint)d.Width, Height = (uint)d.Height, NativeFormat = (uint)d.Format,
            MipLevels = (uint)d.Levels, ArrayLayers = 1, Usage = (uint)(texture.Underlying?.Usage ?? 0),
        };
    }

    public bool Dispatch(UpscaleInputs i)
    {
        if (failed) return false;
        uint mode = Mode(i.RenderWidth / (float)i.DisplayWidth);
        if (options != (mode, i.DisplayWidth, i.DisplayHeight))
        {
            var o = new DlssOptions
            {
                StructType = Streamline.DlssOptionsType, StructVersion = 3,
                Mode = mode, OutputWidth = (uint)i.DisplayWidth, OutputHeight = (uint)i.DisplayHeight,
                PreExposure = 1, ExposureScale = 1, ColorBuffersHdr = 1, UseAutoExposure = 1,
                // MEITOU_DLSS_PRESET: Streamline's preset number for every mode (0 default, 1..15 = A..O), e.g. 5 (E, CNN) for the water hint.
                DlaaPreset = Preset, QualityPreset = Preset, BalancedPreset = Preset, PerformancePreset = Preset, UltraPerformancePreset = Preset,
            };
            int rc = sl.DlssSetOptions(viewport, &o);
            if (rc != 0) return Fail($"slDLSSSetOptions failed ({rc})");
            options = (mode, i.DisplayWidth, i.DisplayHeight);
            Name = $"dlss {i.RenderWidth}x{i.RenderHeight}";
        }

        nint token;
        uint index = frameIndex++;
        int r = sl.GetNewFrameToken(&token, &index);
        if (r != 0) return Fail($"slGetNewFrameToken failed ({r})");

        var viewToClip = i.ViewToClip * FlipY;
        Matrix4x4.Invert(viewToClip, out var clipToView);
        var toPrevious = FlipY * i.ClipToPreviousClip * FlipY;
        Matrix4x4.Invert(toPrevious, out var fromPrevious);
        var constants = new Constants
        {
            StructType = Streamline.ConstantsType, StructVersion = 2,
            CameraViewToClip = viewToClip, ClipToCameraView = clipToView, ClipToLensClip = Matrix4x4.Identity,
            ClipToPrevClip = toPrevious, PrevClipToClip = fromPrevious,
            JitterOffset = i.JitterPixels,
            MvecScale = new Vector2(-1, -1),   // ours: UV from previous to current; DLSS: normalised, towards the previous position
            CameraPos = i.Eye, CameraUp = i.Up, CameraRight = i.Right, CameraFwd = i.Forward,
            CameraNear = i.Near, CameraFar = i.Far, CameraFov = i.FieldOfView, CameraAspectRatio = i.Aspect,
            MotionVectorsInvalidValue = float.MaxValue,
            CameraMotionIncluded = 1, Reset = (byte)(i.Reset ? 1 : 0),
            MinRelativeLinearDepthObjectSeparation = 40,
        };
        r = sl.SetConstants(&constants, token, viewport);
        if (r != 0) return Fail($"slSetConstants failed ({r})");

        var list = gl.Context.BeginNative("dlss upscale");
        var cb = list.Handle;
        try
        {
            // VkGl keeps every image in GENERAL.
            var depth = Resource(i.Depth, (uint)ImageLayout.General);
            var motion = Resource(i.Motion, (uint)ImageLayout.General);
            var colour = Resource(i.Colour, (uint)ImageLayout.General);
            var output = Resource(i.Output, (uint)ImageLayout.General);
            bool hint = i.Reactive is not null;
            var reactive = hint ? Resource(i.Reactive!, (uint)ImageLayout.General) : default;
            var tags = stackalloc ResourceTag[5];
            tags[0] = Tag(&depth, BufferDepth);
            tags[1] = Tag(&motion, BufferMotionVectors);
            tags[2] = Tag(&colour, BufferScalingInputColor);
            tags[3] = Tag(&output, BufferScalingOutputColor);
            // The water mask as DLSS's bias towards the current frame (lerp(history, current, bias); NGX's "Bias.Current.Color.Mask"), as FSR's
            // reactive mask. Observed: the CNN presets (e.g. E) use it, DLSS 310's default transformer preset ignores it.
            if (hint) tags[4] = Tag(&reactive, BufferBiasCurrentColorHint);
            r = sl.SetTagForFrame(token, viewport, tags, hint ? 5u : 4u, cb.Handle);
            if (r != 0) return Fail($"slSetTagForFrame failed ({r})");
            void* input = viewport;
            r = sl.EvaluateFeature(FeatureDlss, token, &input, 1, cb.Handle);
            if (r != 0) return Fail($"slEvaluateFeature failed ({r})");
        }
        finally
        {
            gl.Context.EndNative(list);
        }
        return true;
    }

    static ResourceTag Tag(SlResource* resource, uint type) => new()
    {
        StructType = Streamline.ResourceTagType, StructVersion = 1, Resource = resource, Type = type, Lifecycle = ValidUntilEvaluate,
    };

    bool Fail(string message)
    {
        Console.WriteLine($"upscaler  {message}");
        failed = true;
        return false;
    }

    public void Dispose()
    {
        gl.Device.WaitIdle();
        sl.FreeResources(FeatureDlss, viewport);
        NativeMemory.Free(viewport);
    }
}
