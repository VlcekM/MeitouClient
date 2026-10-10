using System.Numerics;

namespace Meitou.Rendering;

/// <summary>A saved camera for <see cref="CameraPath"/>: where the eye is, where it looks, the orbit distance, the lens and the time of day.</summary>
public readonly record struct CameraShot(Vector3 Eye, float Yaw, float Pitch, float Distance, float FieldOfView, float Hour)
{
    public static CameraShot Of(WorldCamera c, float hour) => new(c.Eye, c.Yaw, c.Pitch, c.Distance, c.FieldOfView, hour);

    /// <summary>Puts the camera here: the same eye and view direction, the target <see cref="Distance"/> in front.</summary>
    public void Apply(WorldCamera c)
    {
        (c.Yaw, c.Pitch, c.Distance, c.FieldOfView) = (Yaw, Pitch, Distance, FieldOfView);
        c.Target = Eye + c.Forward * Distance;
    }
}

/// <summary>
/// A camera flight through saved shots (the viewer's numpad, docs/viewer.md "Cinematic camera"): a centripetal Catmull-Rom spline through
/// the shots in order, so the eye passes every shot exactly, never overshoots into loops between close ones, and keeps a smooth velocity.
/// The yaw and the hour take the short way round. Every shot gets the same time, and a flight that does not loop eases in from rest and out to rest.
/// </summary>
public static class CameraPath
{
    const int Dims = 8;   // eye x, y, z, yaw, pitch, log distance, field of view, hour

    static float[] Values(in CameraShot s) => [s.Eye.X, s.Eye.Y, s.Eye.Z, s.Yaw, s.Pitch, MathF.Log(MathF.Max(s.Distance, 1)), s.FieldOfView, s.Hour];

    static CameraShot Shot(float[] v)
    {
        float hour = v[7] % 24;
        return new(new Vector3(v[0], v[1], v[2]), v[3], v[4], MathF.Exp(v[5]), v[6], hour < 0 ? hour + 24 : hour);
    }

    /// <summary>Moves <paramref name="value"/> by whole periods to within half a period of <paramref name="near"/>.</summary>
    static float Unwrap(float value, float near, float period) => value - period * MathF.Round((value - near) / period);

    /// <summary>
    /// The camera <paramref name="s"/> shots along: 0 is the first shot, 1 the second, up to <c>Count − 1</c> the last (or <c>Count</c>, the
    /// first again, when <paramref name="loop"/>).
    /// </summary>
    public static CameraShot Sample(IReadOnlyList<CameraShot> shots, float s, bool loop)
    {
        int n = shots.Count;
        if (n == 0) throw new ArgumentException("no shots", nameof(shots));
        if (n == 1) return shots[0];
        int segments = loop ? n : n - 1;
        s = loop ? ((s % n) + n) % n : Math.Clamp(s, 0, segments);
        int i = Math.Min((int)s, segments - 1);
        float t = s - i;

        // The four points round the segment i -> i+1: neighbours wrap when looping, else mirror at the ends. Angles and hours continue
        // from their neighbour towards the segment, so no point is more than half a turn (half a day) from the next.
        var p = new float[4][];
        p[1] = Values(shots[i]);
        p[2] = Continue(Values(shots[(i + 1) % n]), p[1]);
        p[0] = Continue(loop || i > 0 ? Values(shots[(i - 1 + n) % n]) : Mirror(p[1], p[2]), p[1]);
        p[3] = Continue(loop || i + 2 < n ? Values(shots[(i + 2) % n]) : Mirror(p[2], p[1]), p[2]);

        // Centripetal knots (alpha 0.5) from the eye's spacing; a floor keeps two shots at one spot from dividing by zero.
        static float Knot(float[] a, float[] b)
        {
            float d = MathF.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
            return MathF.Max(MathF.Sqrt(d), 1);
        }
        float t0 = 0, t1 = t0 + Knot(p[0], p[1]), t2 = t1 + Knot(p[1], p[2]), t3 = t2 + Knot(p[2], p[3]);
        float u = t1 + (t2 - t1) * t;

        var r = new float[Dims];
        for (int d = 0; d < Dims; d++)
        {
            // Barry and Goldman's pyramid.
            float a1 = Lerp(p[0][d], p[1][d], t0, t1, u), a2 = Lerp(p[1][d], p[2][d], t1, t2, u), a3 = Lerp(p[2][d], p[3][d], t2, t3, u);
            float b1 = Lerp(a1, a2, t0, t2, u), b2 = Lerp(a2, a3, t1, t3, u);
            r[d] = Lerp(b1, b2, t1, t2, u);
        }
        return Shot(r);
    }

    static float Lerp(float a, float b, float ta, float tb, float u) => (tb - u) / (tb - ta) * a + (u - ta) / (tb - ta) * b;

    /// <summary>The point mirrored through <paramref name="at"/> (a virtual shot past the end of an open flight).</summary>
    static float[] Mirror(float[] at, float[] other)
    {
        var m = new float[Dims];
        for (int d = 0; d < Dims; d++) m[d] = 2 * at[d] - other[d];
        return m;
    }

    /// <summary><paramref name="v"/> with its yaw and hour moved by whole turns (days) to within half of <paramref name="near"/>'s.</summary>
    static float[] Continue(float[] v, float[] near)
    {
        v[3] = Unwrap(v[3], near[3], MathF.Tau);
        v[7] = Unwrap(v[7], near[7], 24);
        return v;
    }

    /// <summary>
    /// Where along the shots a flight is after <paramref name="seconds"/> (for <see cref="Sample"/>), with <paramref name="perShot"/> seconds
    /// from one shot to the next. Without a loop the speed ramps up over the first segment's half and down over the last's (a trapezoid,
    /// so the middle shots go by at an even pace); a loop runs at an even pace throughout.
    /// </summary>
    public static float Progress(int shots, float perShot, float seconds, bool loop)
    {
        if (shots < 2) return 0;
        int segments = loop ? shots : shots - 1;
        float total = segments * perShot;
        if (loop) return seconds % total / perShot;
        float x = Math.Clamp(seconds / total, 0, 1), ramp = MathF.Min(0.5f, 0.5f / segments), top = 1 / (1 - ramp);
        float f = x < ramp ? top * x * x / (2 * ramp)
            : x > 1 - ramp ? 1 - top * (1 - x) * (1 - x) / (2 * ramp)
            : top * (ramp / 2 + x - ramp);
        return f * segments;
    }

    /// <summary>How long a flight lasts (a loop: one lap).</summary>
    public static float Duration(int shots, float perShot, bool loop) => shots < 2 ? 0 : (loop ? shots : shots - 1) * perShot;
}
