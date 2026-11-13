using System.Numerics;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The traced sun's targets and passes (docs/render-shadows.md "Traced sun"): the lit fragments write the sun term they leave out of the colour
/// (location 2, <see cref="Gi.GiResolveShaders.WithAlbedo"/>); <see cref="Gi.TracedSunPass"/> traces it into a mask at the render size; the additive
/// pass puts it back.
/// </summary>
public sealed unsafe partial class PostProcess
{
    #region Traced sun pass programs

    /// <summary>
    /// <c>--debug-shadows 4</c>: the traced share and its mask over the scene (<see cref="Gi.TracedSunShaders.Debug"/>).
    /// </summary>
    sealed class TracedDebugPass : GiPass
    {
        /// <summary>
        /// The sun term and the filtered mask.
        /// </summary>
        public readonly SamplerSlot Term, Mask;

        /// <summary>
        /// Compiles the debug view and looks up its slots.
        /// </summary>
        public TracedDebugPass(GpuContext gpu) : base(gpu, Gi.TracedSunShaders.Debug, "post traced sun debug") =>
            (Term, Mask) = (P.Sampler("uTerm"), P.Sampler("uMask"));
    }

    /// <summary>
    /// The sun term times the mask and the haze the fragment's colour went through, added to the scene.
    /// </summary>
    sealed class TracedAddPass : GiPass
    {
        /// <summary>
        /// The sun term and the filtered mask.
        /// </summary>
        public readonly SamplerSlot Term, Mask;

        /// <summary>
        /// Compiles the additive pass and looks up its slots.
        /// </summary>
        public TracedAddPass(GpuContext gpu) : base(gpu, Gi.TracedSunShaders.Add, "post traced sun") =>
            (Term, Mask) = (P.Sampler("uTerm"), P.Sampler("uMask"));
    }

    #endregion

    #region Traced sun

    /// <summary>
    /// The sun term, the rays' mask, the penumbra filter's scratch and the filtered mask, at the render size.
    /// </summary>
    Target2D? _tracedTerm, _tracedMask, _tracedScratch, _tracedFiltered;

    /// <summary>
    /// The additive pass, made when first used.
    /// </summary>
    TracedAddPass? _tracedAddPass;

    /// <summary>
    /// The debug view, made when first used.
    /// </summary>
    TracedDebugPass? _tracedDebugPass;

    /// <summary>
    /// This frame's sun is traced (set before <see cref="Begin"/>; needs <see cref="GiResolve"/>): the scene targets carry
    /// <see cref="TracedTermTarget"/>.
    /// </summary>
    public bool TracedSun { get; set; }

    /// <summary>
    /// <c>--debug-shadows 4</c>: the traced share and its mask drawn over the scene (<see cref="Gi.TracedSunShaders.Debug"/>).
    /// </summary>
    public bool TracedDebug { get; set; }

    /// <summary>
    /// The target the lit fragments write the sun term into while the sun is traced or the contact shadows want it and the GI resolve runs.
    /// </summary>
    GpuTexture? TracedTermTarget => (TracedSun || ContactSun) && GiResolve > 0 ? _tracedTerm?.Texture : null;

    /// <summary>
    /// The term (also the contact shadows', <see cref="ContactSun"/>), the rays' mask and the filtered one, at the render size, made on the first
    /// frame that wants them (<see cref="Free"/> drops them with the other targets).
    /// </summary>
    void EnsureTracedSun()
    {
        if ((!TracedSun && !ContactSun) || GiResolve <= 0) return;
        if (_tracedTerm is not null && (!TracedSun || _tracedMask is not null)) return;
        if (!SupportsStorage(InternalFormat.Rgba16F)) return;

        using var batch = Gpu.Uploads.Begin();
        if (_tracedTerm is null)
        {
            var desc = new TextureDesc(InternalFormat.Rgba16F, _width, _height, Use: TextureUse.Sampled | TextureUse.ColourTarget,
                Name: "post traced sun term");
            _tracedTerm = new Target2D(batch.Create(desc), TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        }
        if (!TracedSun) return;

        _tracedMask = Make(batch, _width, _height, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post traced sun mask");
        _tracedScratch = Make(batch, _width, _height, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post traced sun penumbra");
        _tracedFiltered = Make(batch, _width, _height, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post traced sun filtered");
    }

    /// <summary>
    /// Over the opaque scene, its pass closed (<see cref="WorldFrame"/>): the rays into the mask, then the additive pass. <paramref name="inverse"/>:
    /// NDC, as the frame's projection jittered it, to the position relative to the eye; <paramref name="toSun"/>: the direction the shadow maps look
    /// along.
    /// </summary>
    internal void RunTracedSun(Gi.TracedSunPass rays, Gi.GiScene scene, in Matrix4x4 inverse, Vector3 toSun)
    {
        if (_tracedTerm is null || _tracedMask is null || _tracedScratch is null || _tracedFiltered is null) return;
        if (_giAlbedo is null || _sceneColour is null || _sceneDepth is null) return;

        float pixelSlope = 2 * MathF.Tan(_fovNow * 0.5f) / _height;
        var targets = new Gi.TracedSunTargets(_sceneDepth.Texture, _giAlbedo.Texture, _tracedTerm.Texture, _tracedMask.Texture,
            _tracedScratch.Texture, _tracedFiltered.Texture, _width, _height);
        rays.Run(scene, targets, inverse, _eyeNow, toSun, pixelSlope);

        var add = _tracedAddPass ??= new TracedAddPass(Gpu);
        SetGiView(add);
        Bind(add.P, add.Term, _tracedTerm);
        Bind(add.P, add.Mask, _tracedFiltered);
        add.P.ApplyGlobals();   // the haze's uniforms, through the frame globals
        Draw(add.P, _sceneColour.Attachment, _sceneColour.Format, _width, _height, _width, _height, GiAddState);

        if (TracedDebug) DrawTracedDebug(_sceneColour);

        Stamp("traced sun");
        CloseSegment();
    }

    /// <summary>
    /// <c>--debug-shadows 4</c>: the traced share and its mask drawn over <paramref name="colour"/>.
    /// </summary>
    void DrawTracedDebug(Target2D colour)
    {
        var debug = _tracedDebugPass ??= new TracedDebugPass(Gpu);
        SetGiView(debug);
        Bind(debug.P, debug.Term, _tracedTerm);
        Bind(debug.P, debug.Mask, _tracedFiltered);
        Draw(debug.P, colour.Attachment, colour.Format, _width, _height, _width, _height, PassState with { ColourMask = DrawState.Rgb });
    }

    #endregion
}
