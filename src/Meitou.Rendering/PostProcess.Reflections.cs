using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using Meitou.Rendering.Gi;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;

namespace Meitou.Rendering;

/// <summary>The indirect specular's reflections (the Meitou <c>ssr</c> switch, docs/render-gi.md "Reflections"): after the GI resolve, before the shafts and the fog volumes.</summary>
public sealed unsafe partial class PostProcess
{
    sealed class SsrMarchPass : GiPass
    {
        public readonly SamplerSlot Scene;
        public readonly UniformHandle Ssr;
        public SsrMarchPass(GpuContext gpu) : base(gpu, SsrShaders.March, "post ssr march") => (Scene, Ssr) = (P.Sampler("uScene"), P.Uniform("uSsr"));
    }

    sealed class SsrShadePass : GiPass
    {
        public readonly SamplerSlot Hit, GiResolved;
        public readonly UniformHandle HasGi;
        public SsrShadePass(GpuContext gpu) : base(gpu, SsrShaders.Shade, "post ssr shade") =>
            (Hit, GiResolved, HasGi) = (P.Sampler("uHit"), P.Sampler("uGiLow"), P.Uniform("uHasGi"));
    }

    sealed class SsrApplyPass : GiPass
    {
        public readonly SamplerSlot Low;
        public readonly UniformHandle LowSize;
        public SsrApplyPass(GpuContext gpu) : base(gpu, SsrShaders.Apply, "post ssr apply") => (Low, LowSize) = (P.Sampler("uLow"), P.Uniform("uLowSize"));
    }

    SsrMarchPass? ssrMarch;
    SsrShadePass? ssrShade;
    SsrApplyPass? ssrApply;
    /// <summary>What the march and the rays found, and what the shade made of it (the GI resolve's size).</summary>
    Target2D? ssrHit, ssrLow;

    /// <summary>
    /// The lit surfaces store their gloss for <see cref="RunReflections"/>: the scene targets carry the surface target even without <see cref="GiResolve"/>.
    /// Set before <see cref="Begin"/>.
    /// </summary>
    public bool SsrSurfaces { get; set; }

    /// <summary>
    /// Replaces the sky's reflection on the surfaces that stored a gloss, and occludes it on the rest while the GI resolve runs: the march, the rays where
    /// <paramref name="rays"/> and <paramref name="scene"/> can, the shade at the resolve's size, the upsample added to the scene colour. After <see cref="RunGiResolve"/>.
    /// </summary>
    internal void RunReflections(GiProbes? rays, GiScene? scene)
    {
        if (!SsrSurfaces || giAlbedo is null || sceneColour is null || !haveScene) return;
        MarkScene();
        EnsureSsr();
        RunSsrMarch();
        if (rays is not null && scene is not null && (ssrHit!.Texture.Desc.Use & TextureUse.Storage) != 0)
            rays.TraceReflections((CommandList)Segment(), scene, ReflectionFrame());
        RunSsrShade();
        var a = ssrApply ??= new SsrApplyPass(Gpu);
        SetGiView(a);
        Bind(a.P, a.Low, ssrLow);
        a.P.Set(a.LowSize, (float)ssrLow!.Width, (float)ssrLow.Height);
        Draw(a.P, sceneColour.Attachment, sceneColour.Format, width, height, width, height, GiAddState);
        Stamp("ssr");
        CloseSegment();
    }

    void EnsureSsr()
    {
        int w = (width + giDivisor - 1) / giDivisor, h = (height + giDivisor - 1) / giDivisor;
        if (ssrHit is not null && ssrHit.Width == w && ssrHit.Height == h) return;
        ssrHit?.Texture.Dispose();
        ssrLow?.Texture.Dispose();
        using var batch = Gpu.Uploads.Begin();
        ssrHit = Make(batch, w, h, InternalFormat.Rgba16f, TextureMinFilter.Nearest, "post ssr hit");
        ssrLow = Make(batch, w, h, InternalFormat.Rgba16f, TextureMinFilter.Nearest, "post ssr low");
    }

    /// <summary>The lobe's spread with an upscaler to average the frames, the mirror direction without one; the frame, wrapped for the shaders' float.</summary>
    (float Spread, float Frame) SsrSampling => (Temporal ? 1 : 0, frameIndex & 1023);

    void RunSsrMarch()
    {
        var m = ssrMarch ??= new SsrMarchPass(Gpu);
        SetGiView(m);
        Bind(m.P, m.Scene, sceneColour);
        m.P.Set(m.Ssr, SsrSampling.Spread, SsrSampling.Frame);
        Draw(m.P, ssrHit!);
    }

    void RunSsrShade()
    {
        var s = ssrShade ??= new SsrShadePass(Gpu);
        SetGiView(s);
        Bind(s.P, s.Hit, ssrHit);
        Bind(s.P, s.GiResolved, GiResolve > 0 ? giLow : null);
        s.P.Set(s.HasGi, GiResolve > 0 ? 1f : 0f);
        s.P.ApplyGlobals();   // the atmosphere's uniforms, through the frame globals
        Draw(s.P, ssrLow!);
    }

    GiProbes.ReflectionFrame ReflectionFrame()
    {
        float tanY = MathF.Tan(fovNow * 0.5f);
        var rot = viewRotation;
        return new GiProbes.ReflectionFrame(ssrHit!.Texture, giAlbedo!.Texture, sceneDepth!.Texture, eyeNow,
            new(rot.M11, rot.M21, rot.M31), new(rot.M12, rot.M22, rot.M32), ViewWindow.BackFor(rot, fovNow, aspectNow),
            ViewWindow.Tan(tanY * aspectNow, tanY), planes, width, height, giDivisor, SsrSampling.Spread, SsrSampling.Frame);
    }

    void DisposeReflections()
    {
        ssrMarch?.P.Dispose();
        ssrShade?.P.Dispose();
        ssrApply?.P.Dispose();
    }
}
