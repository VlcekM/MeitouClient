using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Post;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Upscalers;

/// <summary>
/// NVIDIA DLSS Super Resolution through <see cref="Ngx"/> directly (Linux, and Windows with frame generation; <see cref="DlssUpscaler"/> is the
/// Streamline path on Windows otherwise). One DLSS feature per (mode, render size, display size, preset, exposure given), remade when they change;
/// per frame the four images (colour, depth, motion, output), the exposure and the water hint go in as <c>NVSDK_NGX_Resource_VK</c>, with the jitter,
/// motion scale and reset, and the evaluation is recorded into the <see cref="GpuContext"/>'s frame. Conventions as for FSR
/// (<see cref="UpscaleInputs"/>): images are bottom-up and jitter/motion share their frame, so the jitter goes through as is and motion (UV, previous
/// to current) is scaled by −render size (NGX: render pixels, current towards previous).
/// </summary>
public sealed unsafe class NgxDlssUpscaler : IUpscaler
{
    /// <summary>
    /// NVSDK_NGX_DLSS_Feature_Flags: HDR colour, motion vectors at the render size, and DLSS's own auto exposure.
    /// </summary>
    const int FlagHdr = 1 << 0, FlagMotionLowRes = 1 << 1, FlagAutoExposure = 1 << 6;

    /// <summary>
    /// The NGX instance DLSS runs through.
    /// </summary>
    readonly Ngx _ngx;

    /// <summary>
    /// The device context the evaluation is recorded on.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The parameter map the feature is created and evaluated with.
    /// </summary>
    readonly void* _parameters;

    /// <summary>
    /// The DLSS feature (null: none yet, or released for new options).
    /// </summary>
    void* _feature;

    /// <summary>
    /// The options the feature was created with: the mode, the render and display sizes, the preset and whether DLSS measures the exposure.
    /// </summary>
    (int Mode, int RenderWidth, int RenderHeight, int DisplayWidth, int DisplayHeight, uint Preset, bool AutoExposure) _options;

    /// <summary>
    /// A call failed: DLSS is off for the rest of the run.
    /// </summary>
    bool _failed;

    /// <summary>
    /// Keeps the NGX instance and the parameter map; the feature comes with the first dispatch.
    /// </summary>
    NgxDlssUpscaler(Ngx ngx, GpuContext ctx, void* parameters)
    {
        this._ngx = ngx;
        this._ctx = ctx;
        this._parameters = parameters;
    }

    /// <summary>
    /// NVSDK_NGX_Resource_VK with its image-view member (nvsdk_ngx_defs_vk.h).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct VkResource
    {
        /// <summary>
        /// The image view and the image.
        /// </summary>
        public nint View, Image;

        /// <summary>
        /// The image's subresource range (a VkImageSubresourceRange): aspect, base level, levels, base layer and layers.
        /// </summary>
        public uint Aspect, BaseLevel, Levels, BaseLayer, Layers;

        /// <summary>
        /// The image's format (a VkFormat) and size.
        /// </summary>
        public uint Format, Width, Height;

        /// <summary>
        /// The resource's type: 0 an image view.
        /// </summary>
        public int Type;

        /// <summary>
        /// Whether NGX writes the image (the output only).
        /// </summary>
        public byte ReadWrite;
    }

    /// <summary>
    /// The upscaler's kind, for the post chain's choice and the statistics.
    /// </summary>
    public UpscalerKind Kind => UpscalerKind.Dlss;

    /// <summary>
    /// The upscaler's name with the render size of its feature.
    /// </summary>
    public string Name { get; private set; } = "dlss";

    /// <summary>
    /// The upscaler on <paramref name="ngx"/>, or null with the reason (NGX not loaded, DLSS not supported, or the parameter map not allocated).
    /// </summary>
    public static NgxDlssUpscaler? TryCreate(Ngx? ngx, GpuContext ctx, out string? reason)
    {
        if (ngx is null)
        {
            reason = "DLSS needs NGX, which did not load (see the upscaler line above)";
            return null;
        }

        if (!ngx.DlssSupported)
        {
            reason = "DLSS is not supported on this device (see the ngx lines above)";
            return null;
        }

        void* parameters;
        int rc = ngx.AllocateParameters(&parameters);
        if (rc != Ngx.ResultSuccess)
        {
            reason = $"NVSDK_NGX_VULKAN_AllocateParameters failed ({rc:x})";
            return null;
        }

        reason = null;
        Console.WriteLine($"upscaler  DLSS through NGX ({Ngx.LibraryName})");
        return new NgxDlssUpscaler(ngx, ctx, parameters);
    }

    /// <summary>
    /// NVSDK_NGX_PerfQuality_Value for a render scale (DLAA 1, quality 1/1.5, balanced 1/1.72, performance 1/2, ultra performance 1/3).
    /// </summary>
    static int Mode(float scale) => scale switch
    {
        >= 0.99f => 5,   // DLAA
        >= 0.64f => 2,   // MaxQuality
        >= 0.55f => 1,   // Balanced
        >= 0.45f => 0,   // MaxPerf
        _ => 3,          // UltraPerformance
    };

    /// <inheritdoc/>
    public bool Dispatch(UpscaleInputs i)
    {
        if (_failed) return false;

        var wanted = (Mode(i.RenderWidth / (float)i.DisplayWidth), i.RenderWidth, i.RenderHeight, i.DisplayWidth, i.DisplayHeight,
            VendorUpscalers.DlssPreset(i.Preset), i.Exposure is null);
        if (_feature != null && _options != wanted) Release();

        var list = (CommandList)_ctx.BeginNative("dlss upscale");
        var cb = list.Handle.Handle;
        try
        {
            if (_feature == null && !Create(cb, wanted)) return false;

            var p = _parameters;

            // Every native image stays in GENERAL: NGX reads the inputs as sampled images and writes the output as a storage image.
            var colour = Resource(i.Colour);
            var depth = Resource(i.Depth);
            var motion = Resource(i.Motion);
            var output = Resource(i.Output, output: true);
            var hint = i.Reactive is null ? default : Resource(i.Reactive);
            var exposure = i.Exposure is null ? default : Resource(i.Exposure);
            NgxParameters.SetPointer(p, "Color\0"u8, &colour);
            NgxParameters.SetPointer(p, "Depth\0"u8, &depth);
            NgxParameters.SetPointer(p, "MotionVectors\0"u8, &motion);
            NgxParameters.SetPointer(p, "Output\0"u8, &output);
            NgxParameters.SetPointer(p, "TransparencyMask\0"u8, null);

            // Our exposure when given: the feature was made without auto exposure then (Create).
            NgxParameters.SetPointer(p, "ExposureTexture\0"u8, i.Exposure is null ? null : &exposure);

            // The water mask as DLSS's bias towards the current frame (as FSR's reactive mask); DLSS 310's transformer presets ignore it.
            NgxParameters.SetPointer(p, "DLSS.Input.Bias.Current.Color.Mask\0"u8, i.Reactive is null ? null : &hint);
            NgxParameters.SetFloat(p, "Jitter.Offset.X\0"u8, i.JitterPixels.X);
            NgxParameters.SetFloat(p, "Jitter.Offset.Y\0"u8, i.JitterPixels.Y);
            NgxParameters.SetFloat(p, "MV.Scale.X\0"u8, -i.RenderWidth);
            NgxParameters.SetFloat(p, "MV.Scale.Y\0"u8, -i.RenderHeight);
            NgxParameters.SetFloat(p, "Sharpness\0"u8, 0);
            NgxParameters.SetInt(p, "Reset\0"u8, i.Reset ? 1 : 0);
            NgxParameters.SetUInt(p, "DLSS.Render.Subrect.Dimensions.Width\0"u8, (uint)i.RenderWidth);
            NgxParameters.SetUInt(p, "DLSS.Render.Subrect.Dimensions.Height\0"u8, (uint)i.RenderHeight);
            NgxParameters.SetFloat(p, "DLSS.Pre.Exposure\0"u8, 1);
            NgxParameters.SetFloat(p, "DLSS.Exposure.Scale\0"u8, 1);
            NgxParameters.SetFloat(p, "FrameTimeDeltaInMsec\0"u8, i.DeltaSeconds * 1000);
            int rc = _ngx.EvaluateFeature(cb, _feature, p, 0);

            // The map keeps what it was given: the images' descriptions are this call's locals, so they do not outlive it (a later CreateFeature1
            // reads the map).
            NgxParameters.SetPointer(p, "Color\0"u8, null);
            NgxParameters.SetPointer(p, "Depth\0"u8, null);
            NgxParameters.SetPointer(p, "MotionVectors\0"u8, null);
            NgxParameters.SetPointer(p, "Output\0"u8, null);
            NgxParameters.SetPointer(p, "DLSS.Input.Bias.Current.Color.Mask\0"u8, null);
            NgxParameters.SetPointer(p, "ExposureTexture\0"u8, null);
            if (rc != Ngx.ResultSuccess) return Fail($"NVSDK_NGX_VULKAN_EvaluateFeature failed ({rc:x})");
        }
        finally
        {
            _ctx.EndNative(list);
        }

        return true;
    }

    /// <summary>
    /// An image for NGX; only the output is written (read-write).
    /// </summary>
    VkResource Resource(GpuTexture texture, bool output = false)
    {
        var d = texture.Desc;
        return new VkResource
        {
            View = (nint)_ctx.Device.NativeView(texture).Handle,
            Image = (nint)_ctx.Device.NativeImage(texture).Handle,
            Aspect = (uint)GlConventions.VkAspect(d),
            Levels = 1,
            Layers = 1,
            Format = (uint)GlConventions.VkFormat(d.Format),
            Width = (uint)d.Width,
            Height = (uint)d.Height,
            ReadWrite = (byte)(output ? 1 : 0),
        };
    }

    /// <summary>
    /// Creates the DLSS feature for the options <paramref name="o"/> (render and display size, mode, flags, the preset for every mode), recorded into
    /// <paramref name="cb"/>; false (logged, DLSS off) on failure.
    /// </summary>
    bool Create(nint cb, (int Mode, int RenderWidth, int RenderHeight, int DisplayWidth, int DisplayHeight, uint Preset, bool AutoExposure) o)
    {
        var p = _parameters;
        NgxParameters.SetUInt(p, "CreationNodeMask\0"u8, 1);
        NgxParameters.SetUInt(p, "VisibilityNodeMask\0"u8, 1);
        NgxParameters.SetUInt(p, "Width\0"u8, (uint)o.RenderWidth);
        NgxParameters.SetUInt(p, "Height\0"u8, (uint)o.RenderHeight);
        NgxParameters.SetUInt(p, "OutWidth\0"u8, (uint)o.DisplayWidth);
        NgxParameters.SetUInt(p, "OutHeight\0"u8, (uint)o.DisplayHeight);
        NgxParameters.SetInt(p, "PerfQualityValue\0"u8, o.Mode);
        NgxParameters.SetInt(p, "DLSS.Feature.Create.Flags\0"u8, FlagHdr | FlagMotionLowRes | (o.AutoExposure ? FlagAutoExposure : 0));
        NgxParameters.SetInt(p, "DLSS.Enable.Output.Subrects\0"u8, 0);
        NgxParameters.SetUInt(p, "DLSS.Hint.Render.Preset.DLAA\0"u8, o.Preset);
        NgxParameters.SetUInt(p, "DLSS.Hint.Render.Preset.Quality\0"u8, o.Preset);
        NgxParameters.SetUInt(p, "DLSS.Hint.Render.Preset.Balanced\0"u8, o.Preset);
        NgxParameters.SetUInt(p, "DLSS.Hint.Render.Preset.Performance\0"u8, o.Preset);
        NgxParameters.SetUInt(p, "DLSS.Hint.Render.Preset.UltraPerformance\0"u8, o.Preset);
        NgxParameters.SetUInt(p, "DLSS.Hint.Render.Preset.UltraQuality\0"u8, o.Preset);
        void* handle;
        int rc = _ngx.CreateFeature(_ctx.Device.Device.Handle, cb, Ngx.FeatureSuperSampling, p, &handle);
        if (rc != Ngx.ResultSuccess) return Fail($"NVSDK_NGX_VULKAN_CreateFeature1 failed ({rc:x})");

        _feature = handle;
        _options = o;
        Name = $"dlss {o.RenderWidth}x{o.RenderHeight}";
        return true;
    }

    /// <summary>
    /// Releases the feature once every frame that may use it has finished (a frame being recorded may have created or evaluated it).
    /// </summary>
    void Release()
    {
        if (_feature == null) return;

        var f = _feature;
        _ctx.Device.Frames.DeferDelete(() => _ngx.ReleaseFeature(f));
        _feature = null;
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

    /// <summary>
    /// Releases the feature and, after the frames in flight, the parameter map.
    /// </summary>
    public void Dispose()
    {
        Release();
        var p = _parameters;
        _ctx.Device.Frames.DeferDelete(() => _ngx.DestroyParameters(p));
    }
}
