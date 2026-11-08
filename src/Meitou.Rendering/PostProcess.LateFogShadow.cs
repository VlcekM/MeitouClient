using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The fog volumes' sun shadow after the upscaler (docs/render-shafts.md "Fog shadow after the upscaler"). Under a temporal upscaler the volumes' beams went
/// through its history with the motion of the surface behind them and trailed the camera for about twenty frames (TAA and DLSS alike; none without one).
/// With <see cref="FogShadowAfterUpscale"/> the merged pass blends the volumes unshadowed (smooth: nothing to trail), <see cref="PostProcessShaders.FogShadowHalf"/>
/// keeps the shadow's change apart at half the render size, and <see cref="CompositeLateFogShadow"/> adds it over the upscaler's output through a temporal
/// filter of its own.
/// </summary>
public sealed partial class PostProcess
{
    /// <summary>Meitou: with a temporal upscaler and the merged layers, the fog volumes' shadow is added after the upscaler (default on; <c>MEITOU_FOG_SHADOW_LATE=0</c> turns it off, for comparing).</summary>
    public bool FogShadowAfterUpscale { get; set; } = Environment.GetEnvironmentVariable("MEITOU_FOG_SHADOW_LATE") != "0";
    bool LateFogShadowOn => FogShadowAfterUpscale && Temporal && MergeLayers;

    sealed class LateFogShadowPass : LayerPass
    {
        public readonly SamplerSlot ShadowHalf, History, Motion;
        public readonly UniformHandle Late, LateTarget;
        public LateFogShadowPass(GpuContext gpu) : base(gpu, PostProcessShaders.LateFogShadow, "post late fog shadow")
        {
            (ShadowHalf, History, Motion) = (P.Sampler("uLayerHalf"), P.Sampler("uLateHistory"), P.Sampler("uLateMotion"));
            (Late, LateTarget) = (P.Uniform("uLate"), P.Uniform("uLateTarget"));
        }
    }

    sealed class LateFogShadowApplyPass : FullscreenProgram
    {
        public readonly SamplerSlot Scene, Shadow;
        public LateFogShadowApplyPass(GpuContext gpu) : base(gpu, PostProcessShaders.LateFogShadowApply, "post late fog shadow apply") =>
            (Scene, Shadow) = (P.Sampler("uScene"), P.Sampler("uShadow"));
    }

    LayerPass? fogShadowHalfPass;
    LateFogShadowPass? lateFogShadow;
    LateFogShadowApplyPass? lateFogShadowApply;
    Target2D? fogShadowHalf, lateFogNow, lateFogLast, lateFogOut;
    bool lateFogPending, lateFogValid;
    (System.Numerics.Vector4 Grid, System.Numerics.Vector4 Range, System.Numerics.Vector2 Tan, float Darkening) lateFogGrid;
    /// <summary>The share of this frame's shadow in the filter (about ten frames, so the jitter's phases at a depth edge average out).</summary>
    const float LateFogShadowBlend = 0.1f;

    /// <summary>The volumes' shadow change at half size, for <see cref="CompositeLateFogShadow"/> (nothing without a shadow this frame).</summary>
    void PrepareLateFogShadow((System.Numerics.Vector4 Grid, System.Numerics.Vector4 Range, System.Numerics.Vector2 Tan, float Darkening) grid)
    {
        if (!LateFogShadowOn || grid.Darkening <= 0) return;
        int hw = Math.Max((width + 1) / 2, 1), hh = Math.Max((height + 1) / 2, 1);
        if (fogShadowHalf is null || fogShadowHalf.Width != hw || fogShadowHalf.Height != hh)
        {
            fogShadowHalf?.Texture.Dispose();   // released after the frames in flight
            using var batch = Gpu.Uploads.Begin();
            fogShadowHalf = Make(batch, hw, hh, InternalFormat.Rgba16f, TextureMinFilter.Nearest, "post fog shadow half");
        }
        var p = fogShadowHalfPass ??= new LayerPass(Gpu, PostProcessShaders.FogShadowHalf, "post fog shadow half");
        SetLayerCommon(p, grid);
        p.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms
        Draw(p.P, fogShadowHalf);
        (lateFogPending, lateFogGrid) = (true, grid);
    }

    /// <summary>The volumes' shadow over <paramref name="output"/> (the upscaler's), into a target of its own which it returns; <paramref name="output"/> itself
    /// when there is none this frame.</summary>
    Target2D CompositeLateFogShadow(Target2D output, int targetWidth, int targetHeight, bool reset)
    {
        if (!lateFogPending) { lateFogValid = false; return output; }
        lateFogPending = false;
        if (lateFogNow is null || lateFogNow.Width != targetWidth || lateFogNow.Height != targetHeight)
        {
            foreach (var t in new[] { lateFogNow, lateFogLast, lateFogOut }) t?.Texture.Dispose();   // released after the frames in flight
            using var batch = Gpu.Uploads.Begin();
            lateFogNow = Make(batch, targetWidth, targetHeight, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post late fog shadow a");
            lateFogLast = Make(batch, targetWidth, targetHeight, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post late fog shadow b");
            lateFogOut = Make(batch, targetWidth, targetHeight, output.Format, TextureMinFilter.Linear, "post late fog shadow out");
            lateFogValid = false;
        }
        (lateFogNow, lateFogLast) = (lateFogLast!, lateFogNow);
        bool history = lateFogValid && !reset;
        var p = lateFogShadow ??= new LateFogShadowPass(Gpu);
        SetLayerCommon(p, lateFogGrid);
        Bind(p.P, p.ShadowHalf, fogShadowHalf);
        Bind(p.P, p.History, history ? lateFogLast : null);
        Bind(p.P, p.Motion, motion);
        p.P.Set(p.Late, (float)width / targetWidth, (float)height / targetHeight, JitterPixels.X, JitterPixels.Y);
        p.P.Set(p.LateTarget, targetWidth, targetHeight, history ? 1 : 0, LateFogShadowBlend);
        p.P.ApplyGlobals();
        Draw(p.P, lateFogNow.Attachment, lateFogNow.Format, targetWidth, targetHeight, targetWidth, targetHeight, PassState);
        var a = lateFogShadowApply ??= new LateFogShadowApplyPass(Gpu);
        Bind(a.P, a.Scene, output);
        Bind(a.P, a.Shadow, lateFogNow);
        Draw(a.P, lateFogOut!.Attachment, lateFogOut.Format, targetWidth, targetHeight, targetWidth, targetHeight, PassState);
        Stamp("late fog shadow");
        CloseSegment();
        lateFogValid = true;
        return lateFogOut;
    }

    void DisposeLateFogShadow()
    {
        fogShadowHalfPass?.P.Dispose();
        lateFogShadow?.P.Dispose();
        lateFogShadowApply?.P.Dispose();
        foreach (var t in new[] { fogShadowHalf, lateFogNow, lateFogLast, lateFogOut }) t?.Texture.Dispose();
    }
}
