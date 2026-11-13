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
    /// The apply, made when first used.
    /// </summary>
    LateFogShadowApplyPass? _lateFogShadowApply;

    /// <summary>
    /// The half-size shadow; the filter's this and last frame and the composite, at the output's size.
    /// </summary>
    Target2D? _fogShadowHalf, _lateFogNow, _lateFogLast, _lateFogOut;

    /// <summary>
    /// Whether this frame prepared a half-size shadow, and whether the filter's last frame holds one.
    /// </summary>
    bool _lateFogPending, _lateFogValid;

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

        int hw = Math.Max((_width + 1) / 2, 1);
        int hh = Math.Max((_height + 1) / 2, 1);
        if (_fogShadowHalf is null || _fogShadowHalf.Width != hw || _fogShadowHalf.Height != hh)
        {
            _fogShadowHalf?.Texture.Dispose();   // released after the frames in flight
            using var batch = Gpu.Uploads.Begin();
            _fogShadowHalf = Make(batch, hw, hh, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post fog shadow half");
        }

        var p = _fogShadowHalfPass ??= new LayerPass(Gpu, PostProcessShaders.FogShadowHalf, "post fog shadow half");
        SetLayerCommon(p, grid);
        p.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms
        Draw(p.P, _fogShadowHalf);
        (_lateFogPending, _lateFogGrid) = (true, grid);
    }

    /// <summary>
    /// The volumes' shadow over <paramref name="output"/> (the upscaler's), into a target of its own which it returns; <paramref name="output"/>
    /// itself when there is none this frame.
    /// </summary>
    Target2D CompositeLateFogShadow(Target2D output, int targetWidth, int targetHeight, bool reset)
    {
        if (!_lateFogPending)
        {
            _lateFogValid = false;
            return output;
        }

        _lateFogPending = false;
        EnsureLateFogTargets(output, targetWidth, targetHeight);
        (_lateFogNow, _lateFogLast) = (_lateFogLast!, _lateFogNow);
        FilterLateFogShadow(targetWidth, targetHeight, reset);
        ApplyLateFogShadow(output, targetWidth, targetHeight);
        Stamp("late fog shadow");
        CloseSegment();
        _lateFogValid = true;

        return _lateFogOut!;
    }

    /// <summary>
    /// Releases the pass programs and the targets.
    /// </summary>
    void DisposeLateFogShadow()
    {
        _fogShadowHalfPass?.P.Dispose();
        _lateFogShadow?.P.Dispose();
        _lateFogShadowApply?.P.Dispose();
        foreach (var t in new[] { _fogShadowHalf, _lateFogNow, _lateFogLast, _lateFogOut }) t?.Texture.Dispose();
    }

    /// <summary>
    /// The filter's two targets and the composite's at the output's size; a new size drops the history.
    /// </summary>
    void EnsureLateFogTargets(Target2D output, int targetWidth, int targetHeight)
    {
        if (_lateFogNow is not null && _lateFogNow.Width == targetWidth && _lateFogNow.Height == targetHeight) return;

        foreach (var t in new[] { _lateFogNow, _lateFogLast, _lateFogOut }) t?.Texture.Dispose();   // released after the frames in flight

        using var batch = Gpu.Uploads.Begin();
        _lateFogNow = Make(batch, targetWidth, targetHeight, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post late fog shadow a");
        _lateFogLast = Make(batch, targetWidth, targetHeight, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post late fog shadow b");
        _lateFogOut = Make(batch, targetWidth, targetHeight, output.Format, TextureMinFilter.Linear, "post late fog shadow out");
        _lateFogValid = false;
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
    /// The filtered shadow over <paramref name="output"/> into <see cref="_lateFogOut"/>.
    /// </summary>
    void ApplyLateFogShadow(Target2D output, int targetWidth, int targetHeight)
    {
        var a = _lateFogShadowApply ??= new LateFogShadowApplyPass(Gpu);
        Bind(a.P, a.Scene, output);
        Bind(a.P, a.Shadow, _lateFogNow);
        Draw(a.P, _lateFogOut!.Attachment, _lateFogOut.Format, targetWidth, targetHeight, targetWidth, targetHeight, PassState);
    }

    #endregion
}
