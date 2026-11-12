using Meitou.Game.Rendering.Gpu;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using LowTarget = Meitou.Game.Rendering.Post.PostProcess.LowTarget;
using Target2D = Meitou.Game.Rendering.Post.PostProcess.Target2D;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The low-resolution particles over the vendor upscaler's output (docs/render-post.md "Colour, night and particles"), both sizes in one pass with
/// their own temporal filter (<see cref="PostProcessShaders.ParticleLate"/>). Each display pixel takes the particles at the surfaces of the four
/// render pixels round it (the jitter undone), weighted bilinearly, then last frame's result, reprojected and kept within those four values, blended
/// in. One surface per pixel (the nearest of the four, as before) gave the outline of a ridge in front of the dust a hard edge again over the
/// upscaler's smoothed one: a staircase.
/// </summary>
sealed class LateParticles(PostProcess post)
{
    #region Passes

    /// <summary>
    /// Both sizes' particles at the display size, through the filter (<see cref="PostProcessShaders.ParticleLate"/>).
    /// </summary>
    sealed class LateParticlesPass : PostProcess.FullscreenProgram
    {
        /// <summary>
        /// Both sizes' accumulations and depths (nearest and farthest), the scene's depth and colour, the upscaler's output, the history and the
        /// motion.
        /// </summary>
        public readonly SamplerSlot AccumH, LowDepthH, AccumFarH, LowDepthFarH, AccumQ, LowDepthQ, AccumFarQ, LowDepthFarQ, Depth, Scene, Output,
            History, Motion;

        /// <summary>
        /// Both sizes, which of them drew, the depth planes, the render-to-display scale and jitter, the target and the filter.
        /// </summary>
        public readonly UniformHandle LowSizeH, LowSizeQ, Has, NearPlanes, Late, Target;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public LateParticlesPass(GpuContext gpu) : base(gpu, PostProcessShaders.ParticleLate, "post late particles")
        {
            (AccumH, LowDepthH, AccumFarH, LowDepthFarH) = (P.Sampler("uAccumH"), P.Sampler("uLowDepthH"), P.Sampler("uAccumFarH"),
                P.Sampler("uLowDepthFarH"));
            (AccumQ, LowDepthQ, AccumFarQ, LowDepthFarQ) = (P.Sampler("uAccumQ"), P.Sampler("uLowDepthQ"), P.Sampler("uAccumFarQ"),
                P.Sampler("uLowDepthFarQ"));
            (Depth, Scene, Output) = (P.Sampler("uDepth"), P.Sampler("uScene"), P.Sampler("uOutput"));
            (History, Motion) = (P.Sampler("uHistory"), P.Sampler("uMotion"));
            (LowSizeH, LowSizeQ, Has, NearPlanes) = (P.Uniform("uLowSizeH"), P.Uniform("uLowSizeQ"), P.Uniform("uHas"), P.Uniform("uNearPlanes"));
            (Late, Target) = (P.Uniform("uLate"), P.Uniform("uTarget"));
        }
    }

    /// <summary>
    /// The filtered layer over the upscaler's output (<see cref="PostProcessShaders.ParticleLateApply"/>).
    /// </summary>
    sealed class LateParticlesApplyPass : PostProcess.FullscreenProgram
    {
        /// <summary>
        /// The filtered layer.
        /// </summary>
        public readonly SamplerSlot Layer;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public LateParticlesApplyPass(GpuContext gpu) : base(gpu,
            PostProcessShaders.ParticleLateApply, "post late particles apply") => Layer = P.Sampler("uLayer");
    }

    #endregion

    #region Filter

    /// <summary>
    /// The share of this frame's particles in the filter (about ten frames, so the jitter's phases at an outline average out).
    /// </summary>
    const float LateParticlesBlend = 0.1f;

    /// <summary>
    /// The filter pass (made with the first composite).
    /// </summary>
    LateParticlesPass? _lateParticles;

    /// <summary>
    /// The apply pass (made with the first composite).
    /// </summary>
    LateParticlesApplyPass? _lateParticlesApply;

    /// <summary>
    /// The filtered layer of this frame and of the last (ping-pong at the display size).
    /// </summary>
    Target2D? _lateParticlesNow, _lateParticlesLast;

    /// <summary>
    /// Whether <see cref="_lateParticlesLast"/> holds a frame's result.
    /// </summary>
    bool _lateParticlesValid;

    /// <summary>
    /// The low-resolution particles kept for after the vendor upscaler (<paramref name="half"/> and <paramref name="quarter"/>), both sizes through
    /// the filter, over its output <paramref name="target"/> (<paramref name="reset"/>: no history).
    /// </summary>
    public void CompositeFiltered(LowTarget half, LowTarget quarter, Target2D target, int targetWidth, int targetHeight, bool reset)
    {
        bool h = half.Late, q = quarter.Late;
        half.Late = quarter.Late = false;
        if (!h && !q)
        {
            _lateParticlesValid = false;
            return;
        }
        EnsureLayers(targetWidth, targetHeight);
        (_lateParticlesNow, _lateParticlesLast) = (_lateParticlesLast!, _lateParticlesNow);
        bool history = _lateParticlesValid && !reset;
        Filter(half, h, quarter, q, target, targetWidth, targetHeight, history);
        var a = _lateParticlesApply ??= new LateParticlesApplyPass(post.Gpu);
        post.Bind(a.P, a.Layer, _lateParticlesNow);
        post.Draw(a.P, target.Attachment, target.Format, targetWidth, targetHeight, targetWidth, targetHeight, PostProcess.ParticleLowState);
        post.CloseSegment();
        _lateParticlesValid = true;
    }

    /// <summary>
    /// The two layers at the display size (made again, without history, when it changed).
    /// </summary>
    void EnsureLayers(int targetWidth, int targetHeight)
    {
        if (_lateParticlesNow is null || _lateParticlesNow.Width != targetWidth || _lateParticlesNow.Height != targetHeight)
        {
            _lateParticlesNow?.Texture.Dispose();   // released after the frames in flight
            _lateParticlesLast?.Texture.Dispose();
            using var batch = post.Gpu.Uploads.Begin();
            _lateParticlesNow = post.Make(batch, targetWidth, targetHeight, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post late particles a");
            _lateParticlesLast = post.Make(batch, targetWidth, targetHeight, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post late particles b");
            _lateParticlesValid = false;
        }
    }

    /// <summary>
    /// Both sizes (<paramref name="h"/>, <paramref name="q"/>: drawn this frame) and the history through the filter into this frame's layer.
    /// </summary>
    void Filter(LowTarget half, bool h, LowTarget quarter, bool q, Target2D target, int targetWidth, int targetHeight, bool history)
    {
        var p = _lateParticles ??= new LateParticlesPass(post.Gpu);
        var planes = post.Planes;
        var jitter = post.JitterPixels;
        post.Bind(p.P, p.AccumH, h ? half.Accum : null);
        post.Bind(p.P, p.LowDepthH, h ? half.Depth : null);
        post.Bind(p.P, p.AccumFarH, h ? half.AccumFar : null);
        post.Bind(p.P, p.LowDepthFarH, h ? half.DepthFar : null);
        post.Bind(p.P, p.AccumQ, q ? quarter.Accum : null);
        post.Bind(p.P, p.LowDepthQ, q ? quarter.Depth : null);
        post.Bind(p.P, p.AccumFarQ, q ? quarter.AccumFar : null);
        post.Bind(p.P, p.LowDepthFarQ, q ? quarter.DepthFar : null);
        post.Bind(p.P, p.Depth, post.SceneDepthTarget);
        post.Bind(p.P, p.Scene, post.SceneColourTarget);
        post.Bind(p.P, p.Output, target);
        post.Bind(p.P, p.History, history ? _lateParticlesLast : null);
        post.Bind(p.P, p.Motion, post.MotionTarget);
        p.P.Set(p.LowSizeH, (float)(half.Accum?.Width ?? 1), (float)(half.Accum?.Height ?? 1));
        p.P.Set(p.LowSizeQ, (float)(quarter.Accum?.Width ?? 1), (float)(quarter.Accum?.Height ?? 1));
        p.P.Set(p.Has, h ? half.Divisor : 0, q ? quarter.Divisor : 0, 0, 0);
        p.P.Set(p.NearPlanes, planes.X, planes.Y);
        p.P.Set(p.Late, (float)post.RenderWidth / targetWidth, (float)post.RenderHeight / targetHeight, jitter.X, jitter.Y);
        p.P.Set(p.Target, targetWidth, targetHeight, history ? 1 : 0, LateParticlesBlend);
        var layer = _lateParticlesNow!;
        post.Draw(p.P, layer.Attachment, layer.Format, targetWidth, targetHeight, targetWidth, targetHeight, PostProcess.PassState);
    }

    #endregion

    #region Lifetime

    /// <summary>
    /// Disposes the passes and the layers.
    /// </summary>
    public void Dispose()
    {
        _lateParticles?.P.Dispose();
        _lateParticlesApply?.P.Dispose();
        _lateParticlesNow?.Texture.Dispose();
        _lateParticlesLast?.Texture.Dispose();
    }

    #endregion
}
