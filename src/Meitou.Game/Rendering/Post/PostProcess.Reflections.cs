using Meitou.Game.Rendering.Frame;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.Game.Rendering.Post;

/// <summary>The indirect specular's reflections (the Meitou <c>ssr</c> switch, docs/render-gi.md "Reflections"): after the GI resolve, before the shafts and the fog volumes.</summary>
public sealed unsafe partial class PostProcess
{
    sealed class SsrMarchPass : GiPass
    {
        public readonly SamplerSlot Scene;
        public SsrMarchPass(GpuContext gpu) : base(gpu, SsrShaders.March, "post ssr march") => Scene = P.Sampler("uScene");
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

    SsrMarchPass? _ssrMarch;
    SsrShadePass? _ssrShade;
    SsrApplyPass? _ssrApply;
    /// <summary>What the march and the rays found, and what the shade made of it (the GI resolve's size).</summary>
    Target2D? _ssrHit, _ssrLow;

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
        if (!SsrSurfaces || _giAlbedo is null || _sceneColour is null || !_haveScene) return;
        MarkScene();
        EnsureSsr();
        RunSsrMarch();
        if (rays is not null && scene is not null && (_ssrHit!.Texture.Desc.Use & TextureUse.Storage) != 0)
            rays.TraceReflections((CommandList)Segment(), scene, ReflectionFrame());
        RunSsrShade();
        var a = _ssrApply ??= new SsrApplyPass(Gpu);
        SetGiView(a);
        Bind(a.P, a.Low, _ssrLow);
        a.P.Set(a.LowSize, (float)_ssrLow!.Width, (float)_ssrLow.Height);
        Draw(a.P, _sceneColour.Attachment, _sceneColour.Format, _width, _height, _width, _height, GiAddState);
        Stamp("ssr");
        CloseSegment();
    }

    void EnsureSsr()
    {
        int w = (_width + _giDivisor - 1) / _giDivisor, h = (_height + _giDivisor - 1) / _giDivisor;
        if (_ssrHit is not null && _ssrHit.Width == w && _ssrHit.Height == h) return;
        _ssrHit?.Texture.Dispose();
        _ssrLow?.Texture.Dispose();
        using var batch = Gpu.Uploads.Begin();
        _ssrHit = Make(batch, w, h, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post ssr hit");
        _ssrLow = Make(batch, w, h, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post ssr low");
    }

    void RunSsrMarch()
    {
        var m = _ssrMarch ??= new SsrMarchPass(Gpu);
        SetGiView(m);
        Bind(m.P, m.Scene, _sceneColour);
        Draw(m.P, _ssrHit!);
    }

    void RunSsrShade()
    {
        var s = _ssrShade ??= new SsrShadePass(Gpu);
        SetGiView(s);
        Bind(s.P, s.Hit, _ssrHit);
        Bind(s.P, s.GiResolved, GiResolve > 0 ? _giLow : null);
        s.P.Set(s.HasGi, GiResolve > 0 ? 1f : 0f);
        s.P.ApplyGlobals();   // the atmosphere's uniforms, through the frame globals
        Draw(s.P, _ssrLow!);
    }

    GiProbes.ReflectionFrame ReflectionFrame()
    {
        float tanY = MathF.Tan(_fovNow * 0.5f);
        var rot = _viewRotation;
        return new GiProbes.ReflectionFrame(_ssrHit!.Texture, _giAlbedo!.Texture, _sceneDepth!.Texture, _eyeNow,
            new(rot.M11, rot.M21, rot.M31), new(rot.M12, rot.M22, rot.M32), ViewWindow.BackFor(rot, _fovNow, _aspectNow),
            ViewWindow.Tan(tanY * _aspectNow, tanY), _planes, _width, _height, _giDivisor);
    }

    void DisposeReflections()
    {
        _ssrMarch?.P.Dispose();
        _ssrShade?.P.Dispose();
        _ssrApply?.P.Dispose();
    }
}
