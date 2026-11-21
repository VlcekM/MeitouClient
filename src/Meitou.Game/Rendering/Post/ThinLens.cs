using System.Numerics;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The depth of field's thin lens in the camera's own terms (docs/render-post.md "Depth of field"): a full-frame sensor and the camera's field of view
/// give the focal length, and the f-number and the focus give the circle of confusion the passes of <see cref="DepthOfField"/> take.
/// </summary>
static class ThinLens
{
    /// <summary>
    /// The sensor's height: a full frame's, in millimetres.
    /// </summary>
    public const float SensorHeight = 24;

    /// <summary>
    /// Millimetres in a world unit (10 units are a metre: docs/render-post.md "Fog").
    /// </summary>
    public const float UnitLength = 100;

    /// <summary>
    /// The focal length that gives a vertical field of view on the sensor, in millimetres.
    /// </summary>
    public static float FocalLength(float fieldOfView) => SensorHeight / 2 / MathF.Tan(fieldOfView / 2);

    /// <summary>
    /// The radius, in half-size pixels of a picture <paramref name="height"/> high, of the disc a point at infinity blurs to with the lens
    /// focused at <paramref name="focus"/> world units: the aperture's diameter f / N scaled by f / (s - f), over the sensor. Nearer points take
    /// (z - s) / z of it, in the shader.
    /// </summary>
    public static float InfinityCoc(float fieldOfView, float aperture, float focus, int height)
    {
        float f = FocalLength(fieldOfView);
        float s = MathF.Max(focus * UnitLength, f + 1);
        float diameter = f * f / (MathF.Max(aperture, 0.01f) * (s - f));

        return diameter / SensorHeight * height * 0.25f;
    }

    /// <summary>
    /// The largest CoC, in half-size pixels, for a picture <paramref name="height"/> high (<see cref="PostOptions.PictureScale"/> times larger than
    /// the window in a photo-mode picture).
    /// </summary>
    public static float MaxCoc(PostOptions o, int height) =>
        MathF.Min(MathF.Max(height * PostOptions.DofMaxCocShare, 2), PostOptions.DofMaxCocPixels * MathF.Max(o.PictureScale, 1));

    /// <summary>
    /// What the CoC passes' two lens uniforms hold (<c>uAuto</c>, <c>uLens</c> of <see cref="CameraEffectShaders"/>): the autofocus's lens (the CoC at
    /// infinity times the focus in millimetres less the focal length, the focal length, 1 when the autofocus is on, the sharp zone's half depth) and
    /// the set lens (the CoC at infinity, the focus distance, <paramref name="maxCoc"/>).
    /// </summary>
    public static (Vector4 Auto, Vector3 Lens) Uniforms(PostOptions o, float fieldOfView, int height, float maxCoc)
    {
        float focal = FocalLength(fieldOfView);
        float autoCoc = focal * focal / MathF.Max(o.LensAperture, 0.01f) / SensorHeight * height * 0.25f;
        var auto = new Vector4(autoCoc, focal, o.LensAutoFocus ? 1f : 0f, MathF.Max(o.LensSharpZone, 0));
        float infinity = InfinityCoc(fieldOfView, o.LensAperture, o.LensFocus, height);

        return (auto, new Vector3(infinity, o.LensFocus, maxCoc));
    }
}
