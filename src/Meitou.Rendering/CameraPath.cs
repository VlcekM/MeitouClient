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

/// <summary>A shot on the timeline: reached at <see cref="Time"/> seconds; with <see cref="Ease"/> the camera slows to a stop there.</summary>
public readonly record struct CameraKey(CameraShot Shot, float Time, bool Ease = false);

/// <summary>
/// A camera flight through timed shots (the viewer's numpad and F2 editor, docs/viewer.md "Cinematic camera"): a cubic Hermite spline in time
/// through every key, each value's slope at a key the difference of its neighbours over their time apart (a Catmull-Rom spline in time), so the
/// speed changes smoothly however unevenly the keys are spaced. The slope is zero at an easing key and at both ends of an open flight (it
/// starts and ends at rest). The yaw and the hour take the short way round.
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

    /// <summary><paramref name="v"/> with its yaw and hour moved by whole turns (days) to within half of <paramref name="near"/>'s.</summary>
    static float[] Continue(float[] v, float[] near)
    {
        v[3] = Unwrap(v[3], near[3], MathF.Tau);
        v[7] = Unwrap(v[7], near[7], 24);
        return v;
    }

    /// <summary>How long a flight lasts: to the last key, or with <paramref name="loopBack"/> seconds more back to the first (one lap).</summary>
    public static float Duration(IReadOnlyList<CameraKey> keys, float? loopBack) =>
        keys.Count == 0 ? 0 : keys[^1].Time - keys[0].Time + (keys.Count > 1 && loopBack is { } back ? back : 0);

    /// <summary>
    /// The camera at <paramref name="time"/> seconds along keys sorted by time. <paramref name="loopBack"/>: loop, taking that many seconds from the
    /// last key back to the first; null: an open flight, held at the first key before it and the last after it.
    /// </summary>
    public static CameraShot Sample(IReadOnlyList<CameraKey> keys, float time, float? loopBack = null)
    {
        int n = keys.Count;
        if (n == 0) throw new ArgumentException("no keys", nameof(keys));
        if (n == 1) return keys[0].Shot;
        bool loop = loopBack is not null;
        float start = keys[0].Time, back = MathF.Max(loopBack ?? 0, 0.01f);
        // The key times on one lap, with the first key again at the lap's end.
        float T(int i) => i < n ? keys[i].Time : keys[0].Time + Duration(keys, back);
        if (loop) time = start + ((time - start) % Duration(keys, back) + Duration(keys, back)) % Duration(keys, back);
        else time = Math.Clamp(time, start, keys[^1].Time);

        int segments = loop ? n : n - 1, i = 0;
        while (i < segments - 1 && time >= T(i + 1)) i++;
        float t0 = T(i), t1 = T(i + 1);

        // The segment's ends and their neighbours (wrapping when looping), angles and hours continued towards the segment.
        float[] p1 = Values(keys[i].Shot), p2 = Continue(Values(keys[(i + 1) % n].Shot), p1);
        float[]? p0 = loop || i > 0 ? Continue(Values(keys[(i - 1 + n) % n].Shot), p1) : null;
        float[]? p3 = loop || i + 2 < n ? Continue(Values(keys[(i + 2) % n].Shot), p2) : null;
        float tPrev = i > 0 ? T(i - 1) : T(n - 1) - Duration(keys, back), tNext = loop && i + 2 > n ? T(1) + Duration(keys, back) : i + 2 <= n ? T(i + 2) : 0;
        bool ease1 = keys[i].Ease || p0 is null, ease2 = keys[(i + 1) % n].Ease || p3 is null;

        float h = MathF.Max(t1 - t0, 1e-4f), s = Math.Clamp((time - t0) / h, 0, 1);
        float s2 = s * s, s3 = s2 * s;
        float h00 = 2 * s3 - 3 * s2 + 1, h10 = s3 - 2 * s2 + s, h01 = -2 * s3 + 3 * s2, h11 = s3 - s2;
        var r = new float[Dims];
        for (int d = 0; d < Dims; d++)
        {
            // Slopes per second at the two ends; the Hermite basis wants them per segment, so times h.
            float m1 = ease1 ? 0 : (p2[d] - p0![d]) / MathF.Max(t1 - tPrev, 1e-4f);
            float m2 = ease2 ? 0 : (p3![d] - p1[d]) / MathF.Max(tNext - t0, 1e-4f);
            r[d] = h00 * p1[d] + h10 * h * m1 + h01 * p2[d] + h11 * h * m2;
        }
        return Shot(r);
    }
}
