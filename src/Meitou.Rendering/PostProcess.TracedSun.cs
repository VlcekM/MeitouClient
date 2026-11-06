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

    Target2D? tracedTerm, tracedMask, tracedHistoryA, tracedHistoryB;
    TracedAddPass? tracedAddPass;
    /// <summary>The filter's last frame: its eye-relative position to clip, its eye, its frame index (the history is used only by the next frame).</summary>
    Matrix4x4 tracedPrevious;
    Vector3 tracedEye;
    long tracedFrame = long.MinValue;

    GpuTexture? TracedTermTarget => TracedSun && GiResolve > 0 ? tracedTerm?.Texture : null;

    /// <summary>The term and the mask, at the render size, made on the first frame that traces (<see cref="Free"/> drops them with the other targets).</summary>
    void EnsureTracedSun()
    {
        if (!TracedSun || GiResolve <= 0 || tracedTerm is not null) return;
        using var batch = Gpu.Uploads.Begin();
        tracedTerm = new Target2D(batch.Create(new TextureDesc(InternalFormat.Rgba16f, width, height, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "post traced sun term")),
            TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        tracedMask = new Target2D(batch.Create(new TextureDesc(InternalFormat.Rgba8, width, height, Use: TextureUse.Sampled | TextureUse.Storage, Name: "post traced sun mask")),
            TextureMinFilter.Nearest, TextureMagFilter.Nearest);
    }

    /// <summary>
    /// The filtered masks, made on the first frame that traces with a temporal upscaler (and storage for RGBA16F): the one the filter writes this frame and last frame's. Null:
    /// the raw mask is used (without an upscaler the rays go to the disc's centre and hold still).
    /// </summary>
    (Target2D Previous, Target2D Filtered)? TracedHistory()
    {
        if (!Temporal || !SupportsStorage(InternalFormat.Rgba16f)) return null;
        if (tracedHistoryA is null)
        {
            using var batch = Gpu.Uploads.Begin();
            tracedHistoryA = Make(batch, width, height, InternalFormat.Rgba16f, TextureMinFilter.Nearest, "post traced sun history");
            tracedHistoryB = Make(batch, width, height, InternalFormat.Rgba16f, TextureMinFilter.Nearest, "post traced sun history");
            tracedFrame = long.MinValue;
        }
        (tracedHistoryA, tracedHistoryB) = (tracedHistoryB!, tracedHistoryA);
        return (tracedHistoryA, tracedHistoryB);
    }

    /// <summary>The sun term times the mask and the haze the fragment's colour went through, added to the scene.</summary>
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
        if (tracedTerm is null || tracedMask is null || giAlbedo is null || sceneColour is null || sceneDepth is null) return;
        float pixelSlope = 2 * MathF.Tan(fovNow * 0.5f) / height;
        var history = TracedHistory();
        // Last frame's filtered mask holds while the camera has not cut (the same rule as the upscalers' reset, tighter).
        bool valid = history is not null && tracedFrame == frameIndex - 1 && Vector3.Distance(eyeNow, tracedEye) < 500;
        rays.Run(scene, new(sceneDepth.Texture, giAlbedo.Texture, tracedTerm.Texture, tracedMask.Texture, width, height, history?.Previous.Texture, history?.Filtered.Texture),
            inverse, eyeNow, toSun, pixelSlope, Temporal ? (uint)frameIndex : null, new Gi.TracedSunHistory(tracedPrevious, eyeNow - tracedEye, valid));
        Matrix4x4.Invert(inverse, out tracedPrevious);
        (tracedEye, tracedFrame) = (eyeNow, frameIndex);
        var add = tracedAddPass ??= new TracedAddPass(Gpu);
        SetGiView(add);
        Bind(add.P, add.Term, tracedTerm);
        Bind(add.P, add.Mask, history?.Filtered ?? tracedMask);
        add.P.ApplyGlobals();   // the haze's uniforms, through the frame globals
        Draw(add.P, sceneColour.Attachment, sceneColour.Format, width, height, width, height, GiAddState);
        Stamp("traced sun");
        CloseSegment();
    }
}
