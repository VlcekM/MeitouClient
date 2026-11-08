using Meitou.Data.Audio;

namespace Meitou.Audio;

/// <summary>Evaluates Wwise curves (RTPC and layer crossfade curves; docs/formats/wwise-bnk.md#shared-pieces).</summary>
static class WwiseCurves
{
    /// <summary>
    /// The curve at <paramref name="x"/>: held flat outside the points; between two points shaped by the first point's interpolation
    /// (the Wwise curve shapes: 0 log3, 1 sine, 2 log1, 3 inverted S, 4 linear, 5 S, 6 exp1, 7 reciprocal sine, 8 exp3, 9 constant).
    /// </summary>
    public static float Evaluate(ReadOnlySpan<WwiseCurvePoint> points, float x)
    {
        if (points.Length == 0) return 0;
        if (x <= points[0].X) return points[0].Y;
        for (int i = 0; i < points.Length - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (x > b.X) continue;
            float t = b.X > a.X ? (x - a.X) / (b.X - a.X) : 1;
            return a.Y + (b.Y - a.Y) * Shape(a.Interpolation, t);
        }
        return points[^1].Y;
    }

    public static float Shape(uint interpolation, float t) => interpolation switch
    {
        0 => 1 - MathF.Pow(1 - t, 3),
        1 => MathF.Sin(t * MathF.PI / 2),
        2 => 1 - (1 - t) * (1 - t),
        3 => t < 0.5f ? 0.5f * (1 - MathF.Pow(1 - 2 * t, 2)) : 0.5f + 0.5f * MathF.Pow(2 * t - 1, 2),
        5 => t * t * (3 - 2 * t),
        6 => t * t,
        7 => 1 - MathF.Cos(t * MathF.PI / 2),
        8 => t * t * t,
        9 => 0,
        _ => t,
    };

    public static float DbToGain(float db) => db <= -96 ? 0 : MathF.Pow(10, db / 20);
}
