using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The fog volumes' sun shadow after the upscaler (docs/render-shafts.md "Fog shadow after the upscaler"). Under a temporal upscaler the volumes'
/// beams went through its history with the motion of the surface behind them and trailed the camera for about twenty frames (TAA and DLSS alike; none
/// without one). With <see cref="FogShadowAfterUpscale"/> the merged pass blends the volumes unshadowed (smooth: nothing to trail),
/// <see cref="PostProcessShaders.FogShadowHalf"/> keeps the shadow's change apart at half the render size, and <see cref="CompositeLateFogShadow"/>
/// adds it over the upscaler's output through a temporal filter of its own.
/// </summary>
public sealed partial class PostProcess
{
    #region Late fog shadow pass programs

    /// <summary>
    /// The temporal filter of the half-size shadow at the output's size (<see cref="PostProcessShaders.LateFogShadow"/>).
    /// </summary>
    sealed class LateFogShadowPass : LayerPass
    {
        /// <summary>
        /// The half-size shadow, the filter's last frame and the motion.
        /// </summary>
        public readonly SamplerSlot ShadowHalf, History, Motion;

        /// <summary>
        /// The render-to-output scale with the jitter, and the output's size with the history's use and the blend.
        /// </summary>
        public readonly UniformHandle Late, LateTarget;

        /// <summary>
        /// Compiles the filter and looks up its slots.
        /// </summary>
        public LateFogShadowPass(GpuContext gpu) : base(gpu, PostProcessShaders.LateFogShadow, "post late fog shadow")
        {
            (ShadowHalf, History, Motion) = (P.Sampler("uLayerHalf"), P.Sampler("uLateHistory"), P.Sampler("uLateMotion"));
            (Late, LateTarget) = (P.Uniform("uLate"), P.Uniform("uLateTarget"));
        }
    }

    /// <summary>
    /// The filtered shadow over the upscaler's output (<see cref="PostProcessShaders.LateFogShadowApply"/>).
    /// </summary>
    sealed class LateFogShadowApplyPass : FullscreenProgram
    {
        /// <summary>
        /// The upscaler's output and the filtered shadow.
        /// </summary>
        public readonly SamplerSlot Scene, Shadow;

        /// <summary>
        /// Compiles the apply and looks up its slots.
        /// </summary>
        public LateFogShadowApplyPass(GpuContext gpu) : base(gpu, PostProcessShaders.LateFogShadowApply, "post late fog shadow apply") =>
            (Scene, Shadow) = (P.Sampler("uScene"), P.Sampler("uShadow"));
    }

    /// <summary>
    /// The filtered shadow blended in-place onto an external upscaler's output (<see cref="PostProcessShaders.LateFogShadowAdd"/>).
    /// </summary>
    sealed class LateFogShadowAddPass : FullscreenProgram
    {
        /// <summary>
        /// The filtered shadow.
        /// </summary>
        public readonly SamplerSlot Shadow;

        /// <summary>
        /// Compiles the additive apply and looks up its slot.
        /// </summary>
        public LateFogShadowAddPass(GpuContext gpu) : base(gpu, PostProcessShaders.LateFogShadowAdd, "post late fog shadow add") =>
            Shadow = P.Sampler("uShadow");
    }

    #endregion

    #region Late fog shadow

    /// <summary>
    /// The share of this frame's shadow in the filter (about ten frames, so the jitter's phases at a depth edge average out).
    /// </summary>
    const float LateFogShadowBlend = 0.1f;

    /// <summary>
    /// The half-size shadow pass, made when first used.
    /// </summary>
    LayerPass? _fogShadowHalfPass;

    /// <summary>
    /// The filter, made when first used.
    /// </summary>
    LateFogShadowPass? _lateFogShadow;

    /// <summary>
    /// The apply into a separate target (for TAA, whose output is its next history), made when first used.
    /// </summary>
    LateFogShadowApplyPass? _lateFogShadowApply;

    /// <summary>
    /// The in-place additive apply (for external upscalers), made when first used.
    /// </summary>
    LateFogShadowAddPass? _lateFogShadowAdd;

    /// <summary>
    /// The half-size shadow; the filter's this and last frame, and the TAA-only composite target, at the output's size.
    /// </summary>
    Target2D? _fogShadowHalf, _lateFogNow, _lateFogLast, _lateFogOut;

    /// <summary>
    /// Whether this frame prepared a half-size shadow, and whether the filter's last frame holds one.
    /// </summary>
    bool _lateFogPending, _lateFogValid;

    /// <summary>
    /// Timestamp (<see cref="Environment.TickCount64"/>) of the last frame the late fog shadow ran.
    /// </summary>
    long _lateFogLastUsed;

    /// <summary>
    /// The layers' grid the half-size shadow was prepared with, for the filter.
    /// </summary>
    (System.Numerics.Vector4 Grid, System.Numerics.Vector4 Range, System.Numerics.Vector2 Tan, float Darkening) _lateFogGrid;

    /// <summary>
    /// Meitou: with a temporal upscaler and the merged layers, the fog volumes' shadow is added after the upscaler (default on;
    /// <c>MEITOU_FOG_SHADOW_LATE=0</c> turns it off, for comparing).
    /// </summary>
    public bool FogShadowAfterUpscale { get; set; } = Environment.GetEnvironmentVariable("MEITOU_FOG_SHADOW_LATE") != "0";

    /// <summary>
    /// The late shadow runs this frame: switched on, under a temporal upscaler, with the merged layers.
    /// </summary>
    bool LateFogShadowOn => FogShadowAfterUpscale && Temporal && MergeLayers;

    /// <summary>
    /// The volumes' shadow change at half size, for <see cref="CompositeLateFogShadow"/> (nothing without a shadow this frame).
    /// </summary>
    void PrepareLateFogShadow((System.Numerics.Vector4 Grid, System.Numerics.Vector4 Range, System.Numerics.Vector2 Tan, float Darkening) grid)
    {
        if (!LateFogShadowOn || grid.Darkening <= 0) return;

        _lateFogLastUsed = Environment.TickCount64;
        int hw = Math.Max((_width + 1) / 2, 1);
        int hh = Math.Max((_height + 1) / 2, 1);
        if (_fogShadowHalf is null || _fogShadowHalf.Width != hw || _fogShadowHalf.Height != hh)
        {
            _fogShadowHalf?.Texture.Dispose();   // released after the frames in flight
            using var batch = Gpu.Uploads.Begin();
            _fogShadowHalf = Make(batch, hw, hh, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post fog shadow half",
                transient: new TransientLifetime(TransientPhase.FogAndAo, TransientPhase.PostUpscale));
        }

        Gpu.BeginTransientPhase(TransientPhase.FogAndAo);

        var p = _fogShadowHalfPass ??= new LayerPass(Gpu, PostProcessShaders.FogShadowHalf, "post fog shadow half");
        SetLayerCommon(p, grid);
        p.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms
        Draw(p.P, _fogShadowHalf);
        (_lateFogPending, _lateFogGrid) = (true, grid);
    }

    /// <summary>
    /// The volumes' shadow over <paramref name="output"/> (the upscaler's): blended in-place onto <paramref name="output"/> when
    /// <paramref name="inPlace"/> (external upscaler, whose output is not reused as history), or into <see cref="_lateFogOut"/> for TAA; returns
    /// <paramref name="output"/> unchanged when there is no shadow this frame.
    /// </summary>
    Target2D CompositeLateFogShadow(Target2D output, int targetWidth, int targetHeight, bool reset, bool inPlace)
    {
        if (!_lateFogPending)
        {
            _lateFogValid = false;
            return output;
        }

        _lateFogPending = false;
        _lateFogLastUsed = Environment.TickCount64;
        EnsureLateFogTargets(output, targetWidth, targetHeight, inPlace);
        (_lateFogNow, _lateFogLast) = (_lateFogLast!, _lateFogNow);
        FilterLateFogShadow(targetWidth, targetHeight, reset);
        if (inPlace) ApplyLateFogShadowInPlace(output, targetWidth, targetHeight);
        else ApplyLateFogShadow(output, targetWidth, targetHeight);
        Stamp("late fog shadow");
        CloseSegment();
        _lateFogValid = true;

        return inPlace ? output : _lateFogOut!;
    }

    /// <summary>
    /// Releases the late fog shadow targets (after the frames in flight) and forgets the history.
    /// </summary>
    void FreeLateFogShadow()
    {
        foreach (var t in new[] { _fogShadowHalf, _lateFogNow, _lateFogLast, _lateFogOut }) t?.Texture.Dispose();
        _fogShadowHalf = _lateFogNow = _lateFogLast = _lateFogOut = null;
        _lateFogValid = _lateFogPending = false;
    }

    /// <summary>
    /// Releases the late fog shadow targets when unused for more than <see cref="IdleReleaseMs"/> milliseconds.
    /// </summary>
    void ReleaseIdleLateFogShadow(long now)
    {
        if (!_lateFogPending && (_fogShadowHalf is not null || _lateFogNow is not null || _lateFogLast is not null || _lateFogOut is not null)
            && now - _lateFogLastUsed > IdleReleaseMs)
        {
            FreeLateFogShadow();
        }
    }

    /// <summary>
    /// Releases the pass programs and the targets.
    /// </summary>
    void DisposeLateFogShadow()
    {
        _fogShadowHalfPass?.P.Dispose();
        _lateFogShadow?.P.Dispose();
        _lateFogShadowApply?.P.Dispose();
        _lateFogShadowAdd?.P.Dispose();
        FreeLateFogShadow();
    }

    /// <summary>
    /// The filter's two targets and, when not <paramref name="inPlace"/>, the composite's at the output's size; a new size drops the history.
    /// </summary>
    void EnsureLateFogTargets(Target2D output, int targetWidth, int targetHeight, bool inPlace)
    {
        if (inPlace && _lateFogOut is not null)
        {
            _lateFogOut.Texture.Dispose();
            _lateFogOut = null;
        }

        bool needFilter = _lateFogNow is null || _lateFogNow.Width != targetWidth || _lateFogNow.Height != targetHeight;
        bool needOut = !inPlace && (_lateFogOut is null || _lateFogOut.Width != targetWidth || _lateFogOut.Height != targetHeight
            || _lateFogOut.Format != output.Format);
        if (!needFilter && !needOut) return;

        using var batch = Gpu.Uploads.Begin();
        if (needFilter)
        {
            _lateFogNow?.Texture.Dispose();   // released after the frames in flight
            _lateFogLast?.Texture.Dispose();
            _lateFogNow = Make(batch, targetWidth, targetHeight, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post late fog shadow a");
            _lateFogLast = Make(batch, targetWidth, targetHeight, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post late fog shadow b");
            _lateFogValid = false;
        }
        if (needOut)
        {
            _lateFogOut?.Texture.Dispose();
            _lateFogOut = Make(batch, targetWidth, targetHeight, output.Format, TextureMinFilter.Linear, "post late fog shadow out",
                transient: TransientLifetime.Single(TransientPhase.PostUpscale));
        }
    }

    /// <summary>
    /// The half-size shadow through the temporal filter into <see cref="_lateFogNow"/>, from the last frame's unless <paramref name="reset"/>.
    /// </summary>
    void FilterLateFogShadow(int targetWidth, int targetHeight, bool reset)
    {
        bool history = _lateFogValid && !reset;
        var p = _lateFogShadow ??= new LateFogShadowPass(Gpu);
        SetLayerCommon(p, _lateFogGrid);
        Bind(p.P, p.ShadowHalf, _fogShadowHalf);
        Bind(p.P, p.History, history ? _lateFogLast : null);
        Bind(p.P, p.Motion, _motion);
        p.P.Set(p.Late, (float)_width / targetWidth, (float)_height / targetHeight, JitterPixels.X, JitterPixels.Y);
        p.P.Set(p.LateTarget, targetWidth, targetHeight, history ? 1 : 0, LateFogShadowBlend);
        p.P.ApplyGlobals();
        Draw(p.P, _lateFogNow!.Attachment, _lateFogNow.Format, targetWidth, targetHeight, targetWidth, targetHeight, PassState);
    }

    /// <summary>
    /// The filtered shadow over <paramref name="output"/> into <see cref="_lateFogOut"/> (for TAA).
    /// </summary>
    void ApplyLateFogShadow(Target2D output, int targetWidth, int targetHeight)
    {
        var a = _lateFogShadowApply ??= new LateFogShadowApplyPass(Gpu);
        Bind(a.P, a.Scene, output);
        Bind(a.P, a.Shadow, _lateFogNow);
        Draw(a.P, _lateFogOut!.Attachment, _lateFogOut.Format, targetWidth, targetHeight, targetWidth, targetHeight, PassState);
    }

    /// <summary>
    /// The filtered shadow added in-place onto <paramref name="output"/> via hardware blending, then clamped to zero.
    /// </summary>
    void ApplyLateFogShadowInPlace(Target2D output, int targetWidth, int targetHeight)
    {
        var a = _lateFogShadowAdd ??= new LateFogShadowAddPass(Gpu);
        Bind(a.P, a.Shadow, _lateFogNow);
        Draw(a.P, output.Attachment, output.Format, targetWidth, targetHeight, targetWidth, targetHeight, GiAddState);

        var floor = _lampShadowFloor ??= new LampShadowFloorPass(Gpu);
        Draw(floor.P, output.Attachment, output.Format, targetWidth, targetHeight, targetWidth, targetHeight, LampShadowFloorState);
    }

    #endregion
}
