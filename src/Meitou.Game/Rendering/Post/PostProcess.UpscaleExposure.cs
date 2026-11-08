using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using System.Runtime.InteropServices;

using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// DLSS's exposure (docs/render-post.md "Frame contract"): the scale the composite gives the colour before its tone map, written into a 1 x 1 R32F ahead of the
/// upscaler. The adaptation runs after the upscaler, so it is last frame's adapted luminance (it moves at 0.5 / s); DLSS's own measure is the fallback.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>The composite's key over the adapted luminance (hdr.material's EXPOSURE_KEY, PostProcessShaders.Composite).</summary>
    const float ExposureKey = 0.55f;

    Target2D? upscaleExposure;
    GpuProgram? upscaleExposureProgram;
    GpuComputePipeline? upscaleExposurePipeline;

    [StructLayout(LayoutKind.Sequential)]
    struct UpscaleExposurePush
    {
        public float Exposure, Key, Auto;
    }

    /// <summary>The 1 x 1 exposure for the vendor upscaler, written in this frame's post segment; null where R32F cannot be a storage image (DLSS then measures its own).</summary>
    Target2D? WriteUpscaleExposure()
    {
        if (!SupportsStorage(InternalFormat.R32f) || adaptB is null) return null;
        if (upscaleExposure is null)
        {
            using var batch = Gpu.Uploads.Begin();
            upscaleExposure = Make(batch, 1, 1, InternalFormat.R32f, TextureMinFilter.Nearest, "post upscale exposure");
        }
        if (upscaleExposureProgram is null)
        {
            upscaleExposureProgram = Gpu.Shaders.Compute(UpscaleExposureShader, "post upscale exposure");
            upscaleExposurePipeline = Gpu.Pipelines.Get(new ComputePipelineDesc(upscaleExposureProgram!.Value, "post upscale exposure"));
        }

        var cmd = Segment();
        var adapted = Sampled(adaptB);
        var bindings = stackalloc GpuBinding[2];
        bindings[0] = GpuBinding.Sampled(0, new SampledTexture(Point(), adapted.View));
        bindings[1] = GpuBinding.StorageImage(1, new SampledTexture(default, upscaleExposure.Texture.View()));
        bool auto = AutoExposure is not null && adaptedValid;
        var push = new UpscaleExposurePush { Exposure = Options.Exposure, Key = ExposureKey, Auto = auto ? 1 : 0 };
        cmd.Order();
        cmd.BindPipeline(upscaleExposurePipeline!.Value);
        cmd.BindProgramResources(upscaleExposureProgram!.Value, 0, new ReadOnlySpan<GpuBinding>(bindings, 2));
        cmd.PushConstants(upscaleExposureProgram!.Value, GpuStage.Compute, in push);
        cmd.Dispatch(1, 1);
        cmd.Order();
        return upscaleExposure;
    }

    void DisposeUpscaleExposure()
    {
        upscaleExposure?.Texture.Dispose();
        upscaleExposure = null;
        if (upscaleExposureProgram is { } p) Gpu.Shaders.Dispose(p);
    }

    const string UpscaleExposureShader = """
        #version 450
        layout(local_size_x = 1) in;
        layout(set = 0, binding = 0) uniform sampler2D uAdapted;   // RG32F: the clamped adapted luminance, the smoothed mean
        layout(set = 0, binding = 1, r32f) writeonly uniform image2D uExposure;
        layout(push_constant) uniform Push { float exposure; float key; float autoOn; } pc;

        void main()
        {
            float e = pc.exposure;
            if (pc.autoOn != 0.0) e *= max(pc.key / texelFetch(uAdapted, ivec2(0), 0).r, 0.001);
            imageStore(uExposure, ivec2(0), vec4(e));
        }
        """;
}
