using System.Numerics;
using Meitou.Data.Audio;

namespace Meitou.Audio;

/// <summary>The listener: where the camera's ears are and which way they face (<see cref="WwisePlayer.SetListener"/>).</summary>
public readonly record struct Listener(Vector3 Position, Vector3 Front, Vector3 Up)
{
    /// <summary>At the origin looking along -z (Ogre's forward) with y up.</summary>
    public static Listener Default { get; } = new(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);

    /// <summary>The listener's right-hand direction (front × up in the right-handed, y-up world); zero when front and up are parallel.</summary>
    public Vector3 Right
    {
        get
        {
            var r = Vector3.Cross(Front, Up);
            return r.LengthSquared() < 1e-12f ? Vector3.Zero : Vector3.Normalize(r);
        }
    }
}

/// <summary>
/// The 3D maths of the player (docs/audio.md#3d-positioning): where an emitter sits relative to the listener and what an attenuation does with
/// that. Meitou's approximations: Wwise's own panner and cone are not known beyond the bank's data.
/// </summary>
static class Spatial
{
    /// <summary>
    /// Distance under which an emitter is pulled to the centre (units; the world is in Ogre units, a person about 180 tall): a source on top of the
    /// listener has no direction.
    /// </summary>
    const float CentreRadius = 50;

    /// <summary>
    /// The emitter's distance to the listener and its pan (-1 hard left, 0 centre, +1 hard right): the sine of the angle off the listener's
    /// front-up plane, so a source behind pans like one in front (the sound field is folded onto the stereo pair). A listener without a usable
    /// right (front parallel to up) hears everything centred.
    /// </summary>
    public static (float Distance, float Pan) Locate(Vector3 emitter, in Listener listener)
    {
        var rel = emitter - listener.Position;
        float distance = rel.Length();
        var right = listener.Right;
        if (right == Vector3.Zero || distance < 1e-3f) return (distance, 0);
        float side = Vector3.Dot(rel, right);
        float ahead = Vector3.Dot(rel, Vector3.Normalize(listener.Front));
        float horizontal = MathF.Sqrt(side * side + ahead * ahead);
        if (horizontal < 1e-3f) return (distance, 0);
        float pan = side / horizontal;
        return (distance, pan * (horizontal / (horizontal + CentreRadius)));
    }

    /// <summary>Left and right gains for a pan: constant gain per side that is full on the near side and falls to 0 on the far one (equal power across).</summary>
    public static (float Left, float Right) PanGains(float pan)
    {
        float angle = (Math.Clamp(pan, -1, 1) + 1) * MathF.PI / 4;
        return (Math.Min(1, MathF.Sqrt(2) * MathF.Cos(angle)), Math.Min(1, MathF.Sqrt(2) * MathF.Sin(angle)));
    }

    /// <summary>
    /// A volume curve at <paramref name="distance"/> as a linear gain: scaling 2 curves store gain - 1 (Observed, as the RTPC volume curves), others the
    /// gain itself. Held flat beyond the last point.
    /// </summary>
    public static float VolumeGain(WwiseAttenuationCurve? curve, float distance)
    {
        if (curve is null || curve.Points.Length == 0) return 1;
        float y = WwiseCurves.Evaluate(curve.Points, distance);
        return Math.Max(0, curve.Scaling == 2 ? 1 + y : y);
    }

    /// <summary>The cone's gain and low-pass amount for an emitter whose front is <paramref name="front"/> (zero: no direction, no cone effect).</summary>
    public static (float Gain, float LowPass) Cone(WwiseCone? cone, Vector3 front, Vector3 emitter, Vector3 listener)
    {
        if (cone is null || front.LengthSquared() < 1e-12f) return (1, 0);
        var toListener = listener - emitter;
        if (toListener.LengthSquared() < 1e-12f) return (1, 0);
        float cos = Math.Clamp(Vector3.Dot(Vector3.Normalize(front), Vector3.Normalize(toListener)), -1, 1);
        // The cone angles are full angles (the whole opening), so the half angle is compared.
        float angle = MathF.Acos(cos) * 180 / MathF.PI;
        float inside = cone.InsideDegrees / 2, outside = Math.Max(inside + 1e-3f, cone.OutsideDegrees / 2);
        float t = Math.Clamp((angle - inside) / (outside - inside), 0, 1);
        return (WwiseCurves.DbToGain(cone.OutsideVolume * t), cone.LowPass * t);
    }
}
