using System.Numerics;

namespace Meitou.Rendering;

/// <summary>
/// How stretched a mesh part's texture is: the world length one unit of texture coordinate spans, taken where it is at its largest
/// (a stretched triangle shows few texels per unit of surface, so it needs the finest mip first). Per triangle it is the larger singular
/// value of the map from texture space to the surface: a pixel's footprint in texture space has its short axis along the direction the surface is most
/// stretched in, and the sampler's level follows the short axis (anisotropic filtering), which is the one this bounds. The part's value is the one
/// 0.5% of its area (weighted by world area) exceeds, so a few collapsed or sliver triangles do not decide it, and a part with more
/// than 0.5% of its area at collapsed texture coordinates has none (infinity: its textures always keep every mip).
/// Docs: renderer-native.md 8.12.
/// </summary>
static class MeshTexelScale
{
    const int PerOctave = 4, MinOctave = -10, MaxOctave = 24;
    const int Buckets = (MaxOctave - MinOctave) * PerOctave;
    static readonly double TopShare = double.TryParse(Environment.GetEnvironmentVariable("MEITOU_MIP_TOP"), System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 0.005;

    public static float Of(ModelPart part)
    {
        if (!part.HasUv || part.Indices.Length < 3) return float.PositiveInfinity;
        var v = part.Vertices;
        var idx = part.Indices;
        Span<double> weight = stackalloc double[Buckets + 1];   // the last holds what is beyond the range (and collapsed mappings)
        double total = 0;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            ref var a = ref v[idx[i]];
            ref var b = ref v[idx[i + 1]];
            ref var c = ref v[idx[i + 2]];
            var e1 = b.Position - a.Position;
            var e2 = c.Position - a.Position;
            double area = Vector3.Cross(e1, e2).Length() * 0.5;
            if (area < 1e-9) continue;
            total += area;
            var d1 = b.Uv - a.Uv;
            var d2 = c.Uv - a.Uv;
            double det = (double)d1.X * d2.Y - (double)d1.Y * d2.X;
            int bucket;
            if (Math.Abs(det) < 1e-14) bucket = Buckets;
            else
            {
                var wu = (e1 * d2.Y - e2 * d1.Y) * (float)(1 / det);
                var wv = (e2 * d1.X - e1 * d2.X) * (float)(1 / det);
                double ga = wu.LengthSquared(), gb = Vector3.Dot(wu, wv), gc = wv.LengthSquared();
                double s2 = (ga + gc) * 0.5 + Math.Sqrt((ga - gc) * (ga - gc) * 0.25 + gb * gb);   // largest singular value squared
                double octave = 0.5 * Math.Log2(Math.Max(s2, 1e-30));
                bucket = octave >= MaxOctave ? Buckets : Math.Clamp((int)Math.Floor((octave - MinOctave) * PerOctave), 0, Buckets - 1);
            }
            weight[bucket] += area;
        }
        if (total <= 0) return float.PositiveInfinity;
        double sum = 0;
        for (int b = Buckets; b >= 0; b--)
        {
            sum += weight[b];
            if (sum <= 0 || sum < total * TopShare) continue;
            if (b == Buckets) return float.PositiveInfinity;
            return (float)Math.Pow(2, MinOctave + (b + 1) / (double)PerOctave);   // the bucket's upper edge
        }
        return float.PositiveInfinity;
    }
}
