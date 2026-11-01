using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The low-resolution particles over the vendor upscaler's output (docs/render-post.md "Colour, night and particles"), both sizes in one pass with their own
/// temporal filter (<see cref="PostProcessShaders.ParticleLate"/>). Each display pixel takes the particles at the surfaces of the four render pixels round it
/// (the jitter undone), weighted bilinearly, then last frame's result, reprojected and kept within those four values, blended in. One surface per pixel
/// (the nearest of the four, as before) gave the outline of a ridge in front of the dust a hard edge again over the upscaler's smoothed one: a staircase.
/// </summary>
public sealed partial class PostProcess
{
    sealed class LateParticlesPass : FullscreenProgram
    {
        public readonly SamplerSlot AccumH, LowDepthH, AccumFarH, LowDepthFarH, AccumQ, LowDepthQ, AccumFarQ, LowDepthFarQ, Depth, History, Motion;
        public readonly UniformHandle LowSizeH, LowSizeQ, Has, NearPlanes, ClipDepthZeroToOne, Late, Target;
        public LateParticlesPass(GpuContext gpu) : base(gpu, PostProcessShaders.ParticleLate, "post late particles")
        {
            (AccumH, LowDepthH, AccumFarH, LowDepthFarH) = (P.Sampler("uAccumH"), P.Sampler("uLowDepthH"), P.Sampler("uAccumFarH"), P.Sampler("uLowDepthFarH"));
            (AccumQ, LowDepthQ, AccumFarQ, LowDepthFarQ) = (P.Sampler("uAccumQ"), P.Sampler("uLowDepthQ"), P.Sampler("uAccumFarQ"), P.Sampler("uLowDepthFarQ"));
            (Depth, History, Motion) = (P.Sampler("uDepth"), P.Sampler("uHistory"), P.Sampler("uMotion"));
            (LowSizeH, LowSizeQ, Has, NearPlanes) = (P.Uniform("uLowSizeH"), P.Uniform("uLowSizeQ"), P.Uniform("uHas"), P.Uniform("uNearPlanes"));
            (ClipDepthZeroToOne, Late, Target) = (P.Uniform("uClipDepthZeroToOne"), P.Uniform("uLate"), P.Uniform("uTarget"));
        }
    }

    sealed class LateParticlesApplyPass : FullscreenProgram
    {
        public readonly SamplerSlot Layer;
        public LateParticlesApplyPass(GpuContext gpu) : base(gpu, PostProcessShaders.ParticleLateApply, "post late particles apply") => Layer = P.Sampler("uLayer");
    }

    LateParticlesPass? lateParticles;
    LateParticlesApplyPass? lateParticlesApply;
    Target2D? lateParticlesNow, lateParticlesLast;
    bool lateParticlesValid;
    /// <summary>The share of this frame's particles in the filter (about ten frames, so the jitter's phases at an outline average out).</summary>
    const float LateParticlesBlend = 0.1f;

    /// <summary>The low-resolution particles kept for after the vendor upscaler, both sizes through the filter, over its output <paramref name="target"/>
    /// (<paramref name="reset"/>: no history).</summary>
    void CompositeLateParticlesFiltered(Target2D target, int targetWidth, int targetHeight, bool reset)
    {
        var (half, quarter) = (lowTargets[0], lowTargets[1]);
        bool h = half.Late, q = quarter.Late;
        half.Late = quarter.Late = false;
        if (!h && !q) { lateParticlesValid = false; return; }
        if (lateParticlesNow is null || lateParticlesNow.Width != targetWidth || lateParticlesNow.Height != targetHeight)
        {
            lateParticlesNow?.Texture.Dispose();   // released after the frames in flight
            lateParticlesLast?.Texture.Dispose();
            using var batch = Gpu.Uploads.Begin();
            lateParticlesNow = Make(batch, targetWidth, targetHeight, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post late particles a");
            lateParticlesLast = Make(batch, targetWidth, targetHeight, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post late particles b");
            lateParticlesValid = false;
        }
        (lateParticlesNow, lateParticlesLast) = (lateParticlesLast!, lateParticlesNow);
        bool history = lateParticlesValid && !reset;
        var p = lateParticles ??= new LateParticlesPass(Gpu);
        Bind(p.P, p.AccumH, h ? half.Accum : null);
        Bind(p.P, p.LowDepthH, h ? half.Depth : null);
        Bind(p.P, p.AccumFarH, h ? half.AccumFar : null);
        Bind(p.P, p.LowDepthFarH, h ? half.DepthFar : null);
        Bind(p.P, p.AccumQ, q ? quarter.Accum : null);
        Bind(p.P, p.LowDepthQ, q ? quarter.Depth : null);
        Bind(p.P, p.AccumFarQ, q ? quarter.AccumFar : null);
        Bind(p.P, p.LowDepthFarQ, q ? quarter.DepthFar : null);
        Bind(p.P, p.Depth, sceneDepth);
        Bind(p.P, p.History, history ? lateParticlesLast : null);
        Bind(p.P, p.Motion, motion);
        p.P.Set(p.LowSizeH, (float)(half.Accum?.Width ?? 1), (float)(half.Accum?.Height ?? 1));
        p.P.Set(p.LowSizeQ, (float)(quarter.Accum?.Width ?? 1), (float)(quarter.Accum?.Height ?? 1));
        p.P.Set(p.Has, h ? half.Divisor : 0, q ? quarter.Divisor : 0, 0, 0);
        p.P.Set(p.NearPlanes, nearPlanes.X, nearPlanes.Y);
        p.P.Set(p.ClipDepthZeroToOne, Gpu.ClipDepthZeroToOne ? 1 : 0);
        p.P.Set(p.Late, (float)width / targetWidth, (float)height / targetHeight, JitterPixels.X, JitterPixels.Y);
        p.P.Set(p.Target, targetWidth, targetHeight, history ? 1 : 0, LateParticlesBlend);
        Draw(p.P, lateParticlesNow.Attachment, lateParticlesNow.Format, targetWidth, targetHeight, targetWidth, targetHeight, PassState);
        var a = lateParticlesApply ??= new LateParticlesApplyPass(Gpu);
        Bind(a.P, a.Layer, lateParticlesNow);
        Draw(a.P, target.Attachment, target.Format, targetWidth, targetHeight, targetWidth, targetHeight, ParticleLowState);
        CloseSegment();
        lateParticlesValid = true;
    }

    void DisposeLateParticles()
    {
        lateParticles?.P.Dispose();
        lateParticlesApply?.P.Dispose();
        lateParticlesNow?.Texture.Dispose();
        lateParticlesLast?.Texture.Dispose();
    }
}
