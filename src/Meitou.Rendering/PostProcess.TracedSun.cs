using System.Numerics;
using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The traced sun's targets and passes (docs/render-shadows.md "Traced sun"): the lit fragments write the sun term they leave out of the colour (location 2,
/// <see cref="Gi.GiResolveShaders.WithAlbedo"/>); <see cref="Gi.TracedSunPass"/> traces it into a mask at the render size; the additive pass puts it back.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>This frame's sun is traced (set before <see cref="Begin"/>; needs <see cref="GiResolve"/>): the scene targets carry <see cref="TracedTermTarget"/>.</summary>
    public bool TracedSun { get; set; }

    Target2D? tracedTerm, tracedMask, tracedFiltered;
    TracedAddPass? tracedAddPass;

    GpuTexture? TracedTermTarget => TracedSun && GiResolve > 0 ? tracedTerm?.Texture : null;

    /// <summary>The term, the rays' mask and the filtered one, at the render size, made on the first frame that traces (<see cref="Free"/> drops them with the other targets).</summary>
    void EnsureTracedSun()
    {
        if (!TracedSun || GiResolve <= 0 || tracedTerm is not null || !SupportsStorage(InternalFormat.Rgba16f)) return;
        using var batch = Gpu.Uploads.Begin();
        tracedTerm = new Target2D(batch.Create(new TextureDesc(InternalFormat.Rgba16f, width, height, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "post traced sun term")),
            TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        tracedMask = Make(batch, width, height, InternalFormat.Rgba16f, TextureMinFilter.Nearest, "post traced sun mask");
        tracedFiltered = Make(batch, width, height, InternalFormat.Rgba16f, TextureMinFilter.Nearest, "post traced sun filtered");
    }

    /// <summary>The sun term times the mask and the haze the fragment's colour went through, added to the scene.</summary>
    /// <summary><c>--debug-shadows 4</c>: the traced share and its mask drawn over the scene (<see cref="Gi.TracedSunShaders.Debug"/>).</summary>
    public bool TracedDebug { get; set; }
    TracedDebugPass? tracedDebugPass;

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
        if (tracedTerm is null || tracedMask is null || tracedFiltered is null || giAlbedo is null || sceneColour is null || sceneDepth is null) return;
        float pixelSlope = 2 * MathF.Tan(fovNow * 0.5f) / height;
        rays.Run(scene, new(sceneDepth.Texture, giAlbedo.Texture, tracedTerm.Texture, tracedMask.Texture, tracedFiltered.Texture, width, height), inverse, eyeNow, toSun, pixelSlope);
        var add = tracedAddPass ??= new TracedAddPass(Gpu);
        SetGiView(add);
        Bind(add.P, add.Term, tracedTerm);
        Bind(add.P, add.Mask, tracedFiltered);
        add.P.ApplyGlobals();   // the haze's uniforms, through the frame globals
        Draw(add.P, sceneColour.Attachment, sceneColour.Format, width, height, width, height, GiAddState);
        if (TracedDebug)
        {
            var debug = tracedDebugPass ??= new TracedDebugPass(Gpu);
            SetGiView(debug);
            Bind(debug.P, debug.Term, tracedTerm);
            Bind(debug.P, debug.Mask, tracedFiltered);
            Draw(debug.P, sceneColour.Attachment, sceneColour.Format, width, height, width, height, PassState with { ColourMask = DrawState.Rgb });
        }
        Stamp("traced sun");
        CloseSegment();
    }
}
