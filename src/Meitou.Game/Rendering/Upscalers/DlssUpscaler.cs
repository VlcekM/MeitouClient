using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Post;
using static Meitou.Game.Rendering.Upscalers.SlTypes;

namespace Meitou.Game.Rendering.Upscalers;

/// <summary>
/// NVIDIA DLSS Super Resolution through <see cref="Streamline"/> (which must have been initialised before the device and attached to it).
/// Per frame: the frame's token (<see cref="Streamline.FrameToken"/>), the camera constants, the tagged images (depth, motion, colour in, colour out,
/// the exposure and water hint when given) and the evaluation, recorded into the <see cref="GpuContext"/>'s frame. The DLSS mode follows the render
/// scale (DLAA at 1); the render size stays ours.
/// Conventions as for FSR (<see cref="UpscaleInputs"/>): our images are bottom-up, so the matrices given to DLSS have their y flipped to describe the
/// picture as stored (row 0 at clip y = +1, as DLSS assumes), and motion is scaled by −1 (DLSS: towards the previous position).
/// </summary>
public sealed unsafe class DlssUpscaler : IUpscaler
{
    /// <summary>
    /// The Streamline instance DLSS runs through.
    /// </summary>
    readonly Streamline _sl;

    /// <summary>
    /// The device context the evaluation is recorded on.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The upscale viewport (<see cref="Streamline.UpscaleViewport"/>), in native memory for Streamline's calls.
    /// </summary>
    readonly ViewportHandle* _viewport;

    /// <summary>
    /// The options last set: the DLSS mode, the display size, the preset and whether DLSS measures the exposure (mode <see cref="uint.MaxValue"/>:
    /// none yet).
    /// </summary>
    (uint Mode, int Width, int Height, uint Preset, bool AutoExposure) _options = (uint.MaxValue, 0, 0, 0, false);

    /// <summary>
    /// The render size <see cref="Name"/> was last made for.
    /// </summary>
    (int Width, int Height) _named;

    /// <summary>
    /// A call failed: DLSS is off for the rest of the run.
    /// </summary>
    bool _failed;

    /// <summary>
    /// Allocates the upscale viewport.
    /// </summary>
    DlssUpscaler(Streamline sl, GpuContext ctx)
    {
        this._sl = sl;
        this._ctx = ctx;
        _viewport = (ViewportHandle*)NativeMemory.AllocZeroed((nuint)sizeof(ViewportHandle));
        *_viewport = new ViewportHandle { StructType = ViewportType, StructVersion = 1, Value = Streamline.UpscaleViewport };
    }

    /// <summary>
    /// The upscaler's kind, for the post chain's choice and the statistics.
    /// </summary>
    public UpscalerKind Kind => UpscalerKind.Dlss;

    /// <summary>
    /// The upscaler's name with the render size of the last frame.
    /// </summary>
    public string Name { get; private set; } = "dlss";

    /// <summary>
    /// The upscaler on <paramref name="sl"/>, or null with the reason (Streamline not loaded, or DLSS not supported on the device).
    /// </summary>
    public static DlssUpscaler? TryCreate(Streamline? sl, GpuContext ctx, out string? reason)
    {
        if (sl is null)
        {
            reason = "DLSS needs Streamline loaded before the device (start with --upscaler dlss and the Streamline DLLs)";
            return null;
        }

        if (!sl.DlssSupported)
        {
            reason = "DLSS is not supported on this device (see the streamline lines above)";
            return null;
        }

        reason = null;
        Console.WriteLine($"upscaler  DLSS through Streamline from {sl.Directory}");
        return new DlssUpscaler(sl, ctx);
    }

    /// <summary>
    /// DLSS's mode for a render scale (its presets: DLAA 1, quality 1/1.5, balanced 1/1.72, performance 1/2, ultra performance 1/3).
    /// </summary>
    static uint Mode(float scale) => scale switch
    {
        >= 0.99f => 6,   // eDLAA
        >= 0.64f => 3,   // eMaxQuality
        >= 0.55f => 2,   // eBalanced
        >= 0.45f => 1,   // eMaxPerformance
        _ => 4,          // eUltraPerformance
    };

    /// <inheritdoc/>
    public bool Dispatch(UpscaleInputs i)
    {
        if (_failed || !SetOptions(i)) return false;

        // The render size moves within a mode (0.89 and 0.83 are both Quality), so the name follows it, not the options.
        if (_named != (i.RenderWidth, i.RenderHeight))
        {
            _named = (i.RenderWidth, i.RenderHeight);
            Name = $"dlss {i.RenderWidth}x{i.RenderHeight}";
        }

        return Evaluate(i);
    }

    /// <summary>
    /// Frees DLSS's resources for the viewport (after the device has gone idle) and the viewport.
    /// </summary>
    public void Dispose()
    {
        _ctx.Device.WaitIdle();
        _sl.FreeResources(FeatureDlss, _viewport);
        NativeMemory.Free(_viewport);
    }

    /// <summary>
    /// The DLSS options for this frame's mode, display size, preset and exposure, set again when one of them changes (a preset picked in the settings
    /// applies at once).
    /// </summary>
    bool SetOptions(UpscaleInputs i)
    {
        // Streamline's preset number for every mode (0 default, 1..15 = A..O). Our exposure when given (DLSS's own measure is its fallback).
        uint mode = Mode(i.RenderWidth / (float)i.DisplayWidth);
        uint preset = VendorUpscalers.DlssPreset(i.Preset);
        bool autoExposure = i.Exposure is null;
        if (_options == (mode, i.DisplayWidth, i.DisplayHeight, preset, autoExposure)) return true;

        var o = new DlssOptions
        {
            StructType = DlssOptionsType,
            StructVersion = 3,
            Mode = mode,
            OutputWidth = (uint)i.DisplayWidth,
            OutputHeight = (uint)i.DisplayHeight,
            PreExposure = 1,
            ExposureScale = 1,
            ColorBuffersHdr = 1,
            UseAutoExposure = (byte)(autoExposure ? 1 : 0),
            DlaaPreset = preset,
            QualityPreset = preset,
            BalancedPreset = preset,
            PerformancePreset = preset,
            UltraPerformancePreset = preset,
            UltraQualityPreset = preset,
        };
        int rc = _sl.DlssSetOptions(_viewport, &o);
        if (rc != 0) return Fail($"slDLSSSetOptions failed ({rc})");

        _options = (mode, i.DisplayWidth, i.DisplayHeight, preset, autoExposure);
        return true;
    }

    /// <summary>
    /// Sets this frame's constants, tags its images and records the evaluation; false (logged, DLSS off) when a call fails.
    /// </summary>
    bool Evaluate(UpscaleInputs i)
    {
        // Our images are bottom-up: the constants describe them as stored.
        nint token = _sl.FrameToken();
        if (token == 0) return Fail("slGetNewFrameToken failed");

        var constants = CameraConstants(i.ViewToClip, i.ClipToPreviousClip, i.JitterPixels, i.Eye, i.Up, i.Right, i.Forward,
            i.Near, i.Far, i.FieldOfView, i.Aspect, i.Reset, bottomUp: true);
        int r = _sl.SetConstants(&constants, token, _viewport);
        if (r != 0) return Fail($"slSetConstants failed ({r})");

        var list = (CommandList)_ctx.BeginNative("dlss upscale");
        try
        {
            r = Tag(i, token, list.Handle.Handle);
            if (r != 0) return Fail($"slSetTagForFrame failed ({r})");

            void* input = _viewport;
            r = _sl.EvaluateFeature(FeatureDlss, token, &input, 1, list.Handle.Handle);
            if (r != 0) return Fail($"slEvaluateFeature failed ({r})");
        }
        finally
        {
            _ctx.EndNative(list);
        }

        return true;
    }

    /// <summary>
    /// Tags this frame's images, valid until the evaluation; every native image stays in GENERAL.
    /// </summary>
    int Tag(UpscaleInputs i, nint token, nint cb)
    {
        var depth = Resource(_ctx, i.Depth);
        var motion = Resource(_ctx, i.Motion);
        var colour = Resource(_ctx, i.Colour);
        var output = Resource(_ctx, i.Output);
        var reactive = i.Reactive is null ? default : Resource(_ctx, i.Reactive);
        var exposure = i.Exposure is null ? default : Resource(_ctx, i.Exposure);
        var tags = stackalloc ResourceTag[6];
        uint count = 0;
        tags[count++] = SlTypes.Tag(&depth, BufferDepth, ValidUntilEvaluate);
        tags[count++] = SlTypes.Tag(&motion, BufferMotionVectors, ValidUntilEvaluate);
        tags[count++] = SlTypes.Tag(&colour, BufferScalingInputColor, ValidUntilEvaluate);
        tags[count++] = SlTypes.Tag(&output, BufferScalingOutputColor, ValidUntilEvaluate);

        // The water mask as DLSS's bias towards the current frame (lerp(history, current, bias); NGX's "Bias.Current.Color.Mask"), as FSR's
        // reactive mask. Observed: the CNN presets (e.g. E) use it, DLSS 310's default transformer preset ignores it.
        if (i.Reactive is not null) tags[count++] = SlTypes.Tag(&reactive, BufferBiasCurrentColorHint, ValidUntilEvaluate);
        if (i.Exposure is not null) tags[count++] = SlTypes.Tag(&exposure, BufferExposure, ValidUntilEvaluate);
        return _sl.SetTagForFrame(token, _viewport, tags, count, cb);
    }

    /// <summary>
    /// Logs <paramref name="message"/> and turns DLSS off for the rest of the run; false.
    /// </summary>
    bool Fail(string message)
    {
        Console.WriteLine($"upscaler  {message}");
        _failed = true;
        return false;
    }
}
