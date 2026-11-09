using System.Numerics;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The traced sun's targets and passes (docs/render-shadows.md "Traced sun"): the lit fragments write the sun term they leave out of the colour (location 2,
/// <see cref="Gi.GiResolveShaders.WithAlbedo"/>); <see cref="Gi.TracedSunPass"/> traces it into a mask at the render size; the additive pass puts it back.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>This frame's sun is traced (set before <see cref="Begin"/>; needs <see cref="GiResolve"/>): the scene targets carry <see cref="TracedTermTarget"/>.</summary>
    public bool TracedSun { get; set; }

    Target2D? _tracedTerm, _tracedMask, _tracedScratch, _tracedFiltered;
    TracedAddPass? _tracedAddPass;

    GpuTexture? TracedTermTarget => TracedSun && GiResolve > 0 ? _tracedTerm?.Texture : null;

    /// <summary>The term, the rays' mask and the filtered one, at the render size, made on the first frame that traces (<see cref="Free"/> drops them with the other targets).</summary>
    void EnsureTracedSun()
    {
        if (!TracedSun || GiResolve <= 0 || _tracedTerm is not null || !SupportsStorage(InternalFormat.Rgba16F)) return;
        using var batch = Gpu.Uploads.Begin();
        _tracedTerm = new Target2D(batch.Create(new TextureDesc(InternalFormat.Rgba16F, _width, _height, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "post traced sun term")),
            TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        _tracedMask = Make(batch, _width, _height, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post traced sun mask");
        _tracedScratch = Make(batch, _width, _height, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post traced sun penumbra");
        _tracedFiltered = Make(batch, _width, _height, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post traced sun filtered");
    }

    /// <summary>The sun term times the mask and the haze the fragment's colour went through, added to the scene.</summary>
    /// <summary><c>--debug-shadows 4</c>: the traced share and its mask drawn over the scene (<see cref="Gi.TracedSunShaders.Debug"/>).</summary>
    public bool TracedDebug { get; set; }
    TracedDebugPass? _tracedDebugPass;

    sealed class TracedDebugPass : GiPass
    {
        public readonly SamplerSlot Term, Mask;
        public TracedDebugPass(GpuContext gpu) : base(gpu, Gi.TracedSunShaders.Debug, "post traced sun debug") => (Term, Mask) = (P.Sampler("uTerm"), P.Sampler("uMask"));
    }

    sealed class TracedAddPass : GiPass
    {
        public readonly SamplerSlot Term, Mask;
        public TracedAddPass(GpuContext gpu) : base(gpu, Gi.TracedSunShaders.Add, "post traced sun") => (Term, Mask) = (P.Sampler("uTerm"), P.Sampler("uMask"));
    }

    /// <summary>
    /// Over the opaque scene, its pass closed (<see cref="WorldFrame"/>): the rays into the mask, then the additive pass. <paramref name="inverse"/>: NDC, as the
    /// frame's projection jittered it, to the position relative to the eye; <paramref name="toSun"/>: the direction the shadow maps look along.
    /// </summary>
    internal void RunTracedSun(Gi.TracedSunPass rays, Gi.GiScene scene, in Matrix4x4 inverse, Vector3 toSun)
    {
        if (_tracedTerm is null || _tracedMask is null || _tracedScratch is null || _tracedFiltered is null || _giAlbedo is null || _sceneColour is null || _sceneDepth is null) return;
        float pixelSlope = 2 * MathF.Tan(_fovNow * 0.5f) / _height;
        rays.Run(scene, new(_sceneDepth.Texture, _giAlbedo.Texture, _tracedTerm.Texture, _tracedMask.Texture, _tracedScratch.Texture, _tracedFiltered.Texture, _width, _height), inverse, _eyeNow, toSun, pixelSlope);
        var add = _tracedAddPass ??= new TracedAddPass(Gpu);
        SetGiView(add);
        Bind(add.P, add.Term, _tracedTerm);
        Bind(add.P, add.Mask, _tracedFiltered);
        add.P.ApplyGlobals();   // the haze's uniforms, through the frame globals
        Draw(add.P, _sceneColour.Attachment, _sceneColour.Format, _width, _height, _width, _height, GiAddState);
        if (TracedDebug)
        {
            var debug = _tracedDebugPass ??= new TracedDebugPass(Gpu);
            SetGiView(debug);
            Bind(debug.P, debug.Term, _tracedTerm);
            Bind(debug.P, debug.Mask, _tracedFiltered);
            Draw(debug.P, _sceneColour.Attachment, _sceneColour.Format, _width, _height, _width, _height, PassState with { ColourMask = DrawState.Rgb });
        }
        Stamp("traced sun");
        CloseSegment();
    }
}
