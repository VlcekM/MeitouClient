using Meitou.Game.Rendering.Frame;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The indirect specular's reflections (the Meitou <c>ssr</c> switch, docs/render-gi.md "Reflections"): after the GI resolve, before the shafts and
/// the fog volumes.
/// </summary>
public sealed unsafe partial class PostProcess
{
    #region Reflection pass programs

    /// <summary>
    /// The screen-space march (<see cref="SsrShaders.March"/>).
    /// </summary>
    sealed class SsrMarchPass : GiPass
    {
        /// <summary>
        /// The scene colour the march reads.
        /// </summary>
        public readonly SamplerSlot Scene;

        /// <summary>
        /// Compiles the march and looks up its slot.
        /// </summary>
        public SsrMarchPass(GpuContext gpu) : base(gpu, SsrShaders.March, "post ssr march") => Scene = P.Sampler("uScene");
    }

    /// <summary>
    /// The shade of what the march and the rays found (<see cref="SsrShaders.Shade"/>).
    /// </summary>
    sealed class SsrShadePass : GiPass
    {
        /// <summary>
        /// The hits and the GI resolve's two low-size results.
        /// </summary>
        public readonly SamplerSlot Hit, GiResolved, GiResolvedSky;

        /// <summary>
        /// 1 while the GI resolve runs, else 0; and which part is drawn: 0 the light found, 1 the sky's kept share.
        /// </summary>
        public readonly UniformHandle HasGi, Part;

        /// <summary>
        /// Compiles the shade and looks up its slots.
        /// </summary>
        public SsrShadePass(GpuContext gpu) : base(gpu, SsrShaders.Shade, "post ssr shade") =>
            (Hit, GiResolved, GiResolvedSky, HasGi, Part) =
                (P.Sampler("uHit"), P.Sampler("uGiLow"), P.Sampler("uGiLowSky"), P.Uniform("uHasGi"), P.Uniform("uPart"));
    }

    /// <summary>
    /// The shade's upsample added to the scene (<see cref="SsrShaders.Apply"/>).
    /// </summary>
    sealed class SsrApplyPass : GiPass
    {
        /// <summary>
        /// The shade's two results: the light found, the sky's kept share.
        /// </summary>
        public readonly SamplerSlot Low, LowSky;

        /// <summary>
        /// The shade's result's size.
        /// </summary>
        public readonly UniformHandle LowSize;

        /// <summary>
        /// Compiles the apply and looks up its slots.
        /// </summary>
        public SsrApplyPass(GpuContext gpu) : base(gpu, SsrShaders.Apply, "post ssr apply") =>
            (Low, LowSky, LowSize) = (P.Sampler("uLow"), P.Sampler("uLowSky"), P.Uniform("uLowSize"));
    }

    #endregion

    #region Reflections

    /// <summary>
    /// The march, made when first used.
    /// </summary>
    SsrMarchPass? _ssrMarch;

    /// <summary>
    /// The shade, made when first used.
    /// </summary>
    SsrShadePass? _ssrShade;

    /// <summary>
    /// The apply, made when first used.
    /// </summary>
    SsrApplyPass? _ssrApply;

    /// <summary>
    /// What the march and the rays found, and what the shade made of it: the light found, and the sky's kept share (the GI resolve's size).
    /// </summary>
    Target2D? _ssrHit, _ssrLow, _ssrLowSky;

    /// <summary>
    /// The lit surfaces store their gloss for <see cref="RunReflections"/>: the scene targets carry the surface target even without
    /// <see cref="GiResolve"/>. Set before <see cref="Begin"/>.
    /// </summary>
    public bool SsrSurfaces { get; set; }

    /// <summary>
    /// Replaces the sky's reflection on the surfaces that stored a gloss, and occludes it on the rest while the GI resolve runs: the march, the rays
    /// where <paramref name="rays"/> and <paramref name="scene"/> can, the shade at the resolve's size, the upsample added to the scene colour. After
    /// <see cref="RunGiResolve"/>.
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
        Bind(a.P, a.LowSky, _ssrLowSky);
        a.P.Set(a.LowSize, (float)_ssrLow!.Width, (float)_ssrLow.Height);
        a.P.ApplyGlobals();   // the atmosphere's uniforms, through the frame globals: each pixel's own reflection of the sky
        Draw(a.P, _sceneColour.Attachment, _sceneColour.Format, _width, _height, _width, _height, GiAddState);
        Stamp("ssr");
        CloseSegment();
    }

    /// <summary>
    /// Releases the pass programs.
    /// </summary>
    void DisposeReflections()
    {
        _ssrMarch?.P.Dispose();
        _ssrShade?.P.Dispose();
        _ssrApply?.P.Dispose();
    }

    /// <summary>
    /// The hit and shade targets at the GI resolve's size.
    /// </summary>
    void EnsureSsr()
    {
        int w = (_width + _giDivisor - 1) / _giDivisor;
        int h = (_height + _giDivisor - 1) / _giDivisor;
        if (_ssrHit is not null && _ssrHit.Width == w && _ssrHit.Height == h) return;

        _ssrHit?.Texture.Dispose();
        _ssrLow?.Texture.Dispose();
        _ssrLowSky?.Texture.Dispose();

        using var batch = Gpu.Uploads.Begin();
        _ssrHit = Make(batch, w, h, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post ssr hit");
        _ssrLow = Make(batch, w, h, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post ssr low");
        _ssrLowSky = Make(batch, w, h, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post ssr low sky");
    }

    /// <summary>
    /// The screen-space march into the hit target.
    /// </summary>
    void RunSsrMarch()
    {
        var m = _ssrMarch ??= new SsrMarchPass(Gpu);
        SetGiView(m);
        Bind(m.P, m.Scene, _sceneColour);
        Draw(m.P, _ssrHit!);
    }

    /// <summary>
    /// The hits shaded into the low-size target.
    /// </summary>
    void RunSsrShade()
    {
        var s = _ssrShade ??= new SsrShadePass(Gpu);
        SetGiView(s);
        Bind(s.P, s.Hit, _ssrHit);
        Bind(s.P, s.GiResolved, GiResolve > 0 ? _giLow : null);
        Bind(s.P, s.GiResolvedSky, GiResolve > 0 ? _giLowSky : null);
        s.P.Set(s.HasGi, GiResolve > 0 ? 1f : 0f);
        s.P.ApplyGlobals();   // the atmosphere's uniforms, through the frame globals
        s.P.Set(s.Part, 0f);
        Draw(s.P, _ssrLow!);
        s.P.Set(s.Part, 1f);
        Draw(s.P, _ssrLowSky!);
    }

    /// <summary>
    /// The camera and the targets as the reflection rays read them.
    /// </summary>
    GiProbes.ReflectionFrame ReflectionFrame()
    {
        float tanY = MathF.Tan(_fovNow * 0.5f);
        var rot = _viewRotation;

        return new GiProbes.ReflectionFrame(_ssrHit!.Texture, _giAlbedo!.Texture, _sceneDepth!.Texture, _eyeNow,
            new(rot.M11, rot.M21, rot.M31), new(rot.M12, rot.M22, rot.M32), ViewWindow.BackFor(rot, _fovNow, _aspectNow),
            ViewWindow.Tan(tanY * _aspectNow, tanY), _planes, _width, _height, _giDivisor);
    }

    #endregion
}
