using System.Numerics;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// What the composite's look uniforms hold after the grade (docs/render-post.md "Vignette and film grain"): photo mode's filter
/// (<c>uFilterR</c>, <c>uFilterG</c>, <c>uFilterB</c>, <c>uFilterContrast</c>), the vignette (<c>uVignette</c>) and the film grain (<c>uGrain</c>).
/// One place for both backends' composites (<see cref="PostProcess"/> and <see cref="PostComposite"/>).
/// </summary>
readonly record struct CompositeLook(Vector4 FilterR, Vector4 FilterG, Vector4 FilterB, float FilterContrast, Vector4 Vignette, Vector3 Grain)
{
    /// <summary>
    /// The grain's size in pixels at a picture the window's size.
    /// </summary>
    const float GrainPixels = 1.5f;

    /// <summary>
    /// The seeds the grain's noise cycles through, one a frame.
    /// </summary>
    const int GrainSeeds = 997;

    /// <summary>
    /// The look for a <paramref name="width"/> x <paramref name="height"/> picture. The vignette and the grain need <paramref name="whole"/>, a whole
    /// view of a scene (not a tiled shot's tile, not the title), in any camera; the grain's size grows with a picture drawn larger than the window,
    /// so it looks the same in it. <paramref name="frame"/> is the frame number, which seeds the grain.
    /// </summary>
    public static CompositeLook Of(PostOptions o, bool whole, int width, int height, long frame)
    {
        var filter = o.PhotoFilter;
        var (centre, corner) = VignetteWindow(width, height, o.VignetteAspect);
        var vignette = new Vector4(centre, corner, whole && o.Vignette ? Math.Clamp(o.VignetteStrength, 0, 1) : 0);
        float grainStrength = whole && o.FilmGrain ? Math.Clamp(o.GrainStrength, 0, 1) : 0f;
        var grain = new Vector3(grainStrength, GrainPixels * MathF.Max(o.PictureScale, 1), frame % GrainSeeds);

        return new CompositeLook(filter?.Red ?? Vector4.Zero, filter?.Green ?? Vector4.Zero, filter?.Blue ?? Vector4.Zero, filter?.Contrast ?? 0f,
            vignette, grain);
    }

    /// <summary>
    /// The centre of the part of a <paramref name="width"/> x <paramref name="height"/> picture the vignette darkens towards, and the distance from it
    /// to its corner (pixels): the whole picture, or the centred window of <paramref name="aspect"/> photo mode's frame leaves (as
    /// <c>PhotoGuides.Window</c>).
    /// </summary>
    static (Vector2 Centre, float Corner) VignetteWindow(int width, int height, float aspect)
    {
        float w = width, h = height;
        if (aspect > 0)
        {
            if (aspect > w / MathF.Max(h, 1)) h = w / aspect;
            else w = h * aspect;
        }

        return (new Vector2(width, height) * 0.5f, new Vector2(w, h).Length() * 0.5f);
    }
}
