using System.Numerics;

namespace Meitou.Data.Particles;

public enum PuDynamicKind { Fixed, Random, CurvedLinear, CurvedSpline, Oscillate }

/// <summary>
/// A ParticleUniverse "dynamic attribute" (docs/formats/particle-universe.md): a number, or <c>dyn_random</c> (uniform between
/// <c>min</c> and <c>max</c>, drawn when read), <c>dyn_curved_linear</c> / <c>dyn_curved_spline</c> (control points (x, y), read at
/// an x such as the particle's life fraction) or <c>dyn_oscillate</c> (<c>base + amplitude · wave(frequency · x + phase)</c>).
/// Immutable; <see cref="Evaluate"/> takes the random source so a seeded simulation stays deterministic.
/// </summary>
public sealed class PuDynamic
{
    public PuDynamicKind Kind { get; }
    /// <summary>Fixed: the value. Random: the minimum. Oscillate: the base.</summary>
    public float A { get; }
    /// <summary>Random: the maximum. Oscillate: the amplitude.</summary>
    public float B { get; }
    /// <summary>The control points, sorted by x.</summary>
    public IReadOnlyList<Vector2> Points { get; }
    public float Frequency { get; }
    public float Phase { get; }
    /// <summary>Oscillate: a square wave instead of a sine.</summary>
    public bool Square { get; }

    PuDynamic(PuDynamicKind kind, float a, float b, Vector2[]? points = null, float frequency = 0, float phase = 0, bool square = false)
    {
        (Kind, A, B, Points, Frequency, Phase, Square) = (kind, a, b, points ?? [], frequency, phase, square);
    }

    public static PuDynamic Fixed(float value) => new(PuDynamicKind.Fixed, value, value);
    public static PuDynamic Random(float min, float max) => new(PuDynamicKind.Random, min, max);
    public static PuDynamic Curve(IEnumerable<Vector2> points, bool spline) =>
        new(spline ? PuDynamicKind.CurvedSpline : PuDynamicKind.CurvedLinear, 0, 0, [.. points.OrderBy(p => p.X)]);
    public static PuDynamic Oscillate(float baseValue, float amplitude, float frequency, float phase, bool square) =>
        new(PuDynamicKind.Oscillate, baseValue, amplitude, null, frequency, phase, square);

    public bool IsFixed => Kind == PuDynamicKind.Fixed;

    /// <summary>The smallest and largest value the attribute can take (random: its range; curves: their control points; oscillate: base ± amplitude).</summary>
    public (float Min, float Max) Range => Kind switch
    {
        PuDynamicKind.Fixed => (A, A),
        PuDynamicKind.Random => (Math.Min(A, B), Math.Max(A, B)),
        PuDynamicKind.Oscillate => (A - Math.Abs(B), A + Math.Abs(B)),
        _ => Points.Count == 0 ? (0, 0) : (Points.Min(p => p.Y), Points.Max(p => p.Y)),
    };

    /// <summary>The value at <paramref name="x"/> (ignored by fixed and random values); <paramref name="random"/> is read by <c>dyn_random</c> only (null: the middle).</summary>
    public float Evaluate(float x, Random? random = null)
    {
        switch (Kind)
        {
            case PuDynamicKind.Fixed: return A;
            case PuDynamicKind.Random: return random is null ? (A + B) * 0.5f : A + (B - A) * random.NextSingle();
            case PuDynamicKind.Oscillate:
            {
                float turns = Frequency * x + Phase;
                float wave = Square ? (turns - MathF.Floor(turns) < 0.5f ? 1 : -1) : MathF.Sin(MathF.Tau * turns);
                return A + B * wave;
            }
            default:
            {
                var p = Points;
                if (p.Count == 0) return 0;
                if (p.Count == 1 || x <= p[0].X) return p[0].Y;
                if (x >= p[^1].X) return p[^1].Y;
                int i = 0;
                while (p[i + 1].X < x) i++;
                float span = p[i + 1].X - p[i].X;
                float t = span <= 1e-9f ? 0 : (x - p[i].X) / span;
                if (Kind == PuDynamicKind.CurvedLinear) return p[i].Y + (p[i + 1].Y - p[i].Y) * t;
                // Catmull-Rom through the control points (Observed: the format says "spline"; the exact basis is not known).
                float y0 = p[Math.Max(i - 1, 0)].Y, y1 = p[i].Y, y2 = p[i + 1].Y, y3 = p[Math.Min(i + 2, p.Count - 1)].Y;
                float t2 = t * t, t3 = t2 * t;
                return 0.5f * (2 * y1 + (y2 - y0) * t + (2 * y0 - 5 * y1 + 4 * y2 - y3) * t2 + (3 * y1 - y0 - 3 * y2 + y3) * t3);
            }
        }
    }
}
