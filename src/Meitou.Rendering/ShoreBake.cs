using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Rendering;

/// <summary>
/// The shore distance field's CPU bake (no GPU; docs/viewer.md "Shore distance field"). A square grid of <see cref="Size"/>² texels of
/// <see cref="Texel"/> world units, centred on a point snapped to whole texels, holds
/// <list type="bullet">
/// <item><b>Distance</b>: the signed distance to the waterline in world units, positive over water (terrain under
/// <see cref="WorldWater.Height"/>), negative over land, clamped to ±<see cref="MaxDistance"/>.</item>
/// <item><b>Exposure</b>: 0 (a pond, a narrow bay, a channel) to 1 (open sea), see <see cref="ExposureRule"/>.</item>
/// </list>
/// Texel (i, j) is centred on world (<see cref="X0"/> + (i + 0.5)·Texel, <see cref="Z0"/> + (j + 0.5)·Texel), so a texture of the grid sampled at
/// uv = (p − (X0, Z0)) / (Size·Texel) with linear filtering reads the same values as <see cref="Sample"/>.
/// </summary>
internal sealed class ShoreGrid
{
    public const float DefaultMaxDistance = 4000f;

    /// <summary>
    /// Exposure rule (the "reach" of a water texel is the largest distance-to-shore of any water within <see cref="ReachRadius"/> units of it,
    /// measured on 8-texel blocks): exposure = smoothstep(<see cref="ReachLow"/>, <see cref="ReachHigh"/>, reach). A pond or bay whose water
    /// never lies more than ~600 units from a shore within 1500 units of the point is 0 (a town pond in the swamps reaches about 400); anywhere with
    /// 1200+ units of open water within 1500 is 1 (surf needs a long stretch of open water to build over).
    /// Beyond the grid's edge the water counts as open sea (reach = max) when the nearest edge block is water, else as land.
    /// </summary>
    public const float ReachRadius = 1500f, ReachLow = 600f, ReachHigh = 1200f;
    public const string ExposureRule = "smoothstep(600, 1200, max distance-to-shore of water within 1500 units)";

    public ShoreGrid(int size, float texel, float x0, float z0, float maxDistance, float[] distance, float[] exposure)
    {
        (Size, Texel, X0, Z0, MaxDistance, Distance, Exposure) = (size, texel, x0, z0, maxDistance, distance, exposure);
    }

    public int Size { get; }
    public float Texel { get; }
    /// <summary>World X and Z of the grid's low corner (the outer edge of texel (0, 0)).</summary>
    public float X0 { get; }
    public float Z0 { get; }
    public float MaxDistance { get; }
    /// <summary>Signed distance, row-major (row = Z).</summary>
    public float[] Distance { get; }
    public float[] Exposure { get; }
    public float X1 => X0 + Size * Texel;
    public float Z1 => Z0 + Size * Texel;

    /// <summary>The grid as the texture's texels: (distance, exposure) pairs, row-major.</summary>
    public float[] Interleaved()
    {
        var rg = new float[Size * Size * 2];
        for (int k = 0; k < Size * Size; k++) (rg[2 * k], rg[2 * k + 1]) = (Distance[k], Exposure[k]);
        return rg;
    }

    /// <summary>Signed distance and exposure at a world point (bilinear, clamped at the grid's edge), as the GPU texture reads them.</summary>
    public Vector2 Sample(float x, float z)
    {
        float fx = Math.Clamp((x - X0) / Texel - 0.5f, 0, Size - 1), fz = Math.Clamp((z - Z0) / Texel - 0.5f, 0, Size - 1);
        int i = Math.Min((int)fx, Size - 2), j = Math.Min((int)fz, Size - 2);
        float tx = fx - i, tz = fz - j;
        Vector2 At(int a, int b) => new(Distance[b * Size + a], Exposure[b * Size + a]);
        return Vector2.Lerp(Vector2.Lerp(At(i, j), At(i + 1, j), tx), Vector2.Lerp(At(i, j + 1), At(i + 1, j + 1), tx), tz);
    }
}

internal static class ShoreBake
{
    const float None = 1e20f;
    /// <summary>Texels per exposure block edge.</summary>
    const int Block = 8;

    /// <summary>The centre a bake for <paramref name="eye"/> would use: snapped to whole texels.</summary>
    public static (float X, float Z) SnapCentre(float x, float z, float texel) => (MathF.Round(x / texel) * texel, MathF.Round(z / texel) * texel);

    /// <summary>
    /// Bakes the field round (<paramref name="cx"/>, <paramref name="cz"/>) (snapped to whole texels). The waterline is where
    /// 100 − height changes sign between neighbouring texels, placed by linear interpolation between them (sub-texel); distances are to those
    /// crossing points (8SSEDT: two sweeps carrying the nearest crossing point, O(N²), near-exact), then smoothed with one 3×3 binomial
    /// Gaussian so the gradient has no kinks. The height sampling, sign and smoothing are parallel over rows; the sweeps run on one thread.
    /// </summary>
    public static ShoreGrid Bake(Func<float, float, float> height, float cx, float cz, int size = 1024, float texel = 10f, float maxDistance = ShoreGrid.DefaultMaxDistance)
    {
        (cx, cz) = SnapCentre(cx, cz, texel);
        float x0 = cx - size * 0.5f * texel, z0 = cz - size * 0.5f * texel;
        int n = size;

        // s > 0: water.
        var s = new float[n * n];
        Parallel.For(0, n, j =>
        {
            float z = z0 + (j + 0.5f) * texel;
            for (int i = 0; i < n; i++) s[j * n + i] = WorldWater.Height - height(x0 + (i + 0.5f) * texel, z);
        });

        // Seeds: each texel takes the nearest waterline crossing on its four edges, as a point in texel units.
        var seeds = new Seed[n * n];
        Parallel.For(0, n, j =>
        {
            for (int i = 0; i < n; i++)
            {
                float a = s[j * n + i], best = None, bx = 0, bz = 0;
                bool water = a > 0;
                void Edge(int ii, int jj)
                {
                    if ((uint)ii >= (uint)n || (uint)jj >= (uint)n) return;
                    float b = s[jj * n + ii];
                    if ((b > 0) == water) return;
                    float t = a / (a - b);
                    if (t * t >= best) return;
                    best = t * t;
                    bx = i + t * (ii - i);
                    bz = j + t * (jj - j);
                }
                Edge(i - 1, j); Edge(i + 1, j); Edge(i, j - 1); Edge(i, j + 1);
                seeds[j * n + i] = new Seed { D = best, X = bx, Z = bz };
            }
        });

        Sweeps(seeds, n);

        var dist = new float[n * n];
        Parallel.For(0, n, j =>
        {
            for (int i = 0; i < n; i++)
            {
                float sq = seeds[j * n + i].D;
                float d = sq >= None * 0.5f ? maxDistance : Math.Min(MathF.Sqrt(sq) * texel, maxDistance);
                dist[j * n + i] = s[j * n + i] > 0 ? d : -d;
            }
        });

        var tmp = new float[n * n];
        Smooth(dist, tmp, n);
        var exposure = Exposure(dist, n, texel, maxDistance);
        return new ShoreGrid(n, texel, x0, z0, maxDistance, dist, exposure);
    }

    struct Seed { public float D, X, Z; }

    /// <summary>
    /// 8SSEDT with real-valued seed points: two sweeps (down then up, each with a left and a right pass), carrying the nearest seed point along,
    /// so the distances are to the interpolated waterline and not to texel centres. D becomes the squared distance in texel units.
    /// </summary>
    static unsafe void Sweeps(Seed[] seeds, int n)
    {
        fixed (Seed* a = seeds)
        {
            static void Try(Seed* a, int n, int i, int j, int ii, int jj)
            {
                if ((uint)ii >= (uint)n || (uint)jj >= (uint)n) return;
                Seed* k = a + jj * n + ii;
                if (k->D >= None) return;
                float dx = i - k->X, dz = j - k->Z, dd = dx * dx + dz * dz;
                Seed* at = a + j * n + i;
                if (dd < at->D) *at = new Seed { D = dd, X = k->X, Z = k->Z };
            }
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++) { Try(a, n, i, j, i - 1, j); Try(a, n, i, j, i, j - 1); Try(a, n, i, j, i - 1, j - 1); Try(a, n, i, j, i + 1, j - 1); }
                for (int i = n - 1; i >= 0; i--) Try(a, n, i, j, i + 1, j);
            }
            for (int j = n - 1; j >= 0; j--)
            {
                for (int i = n - 1; i >= 0; i--) { Try(a, n, i, j, i + 1, j); Try(a, n, i, j, i, j + 1); Try(a, n, i, j, i + 1, j + 1); Try(a, n, i, j, i - 1, j + 1); }
                for (int i = 0; i < n; i++) Try(a, n, i, j, i - 1, j);
            }
        }
    }
    /// <summary>One 3×3 binomial (1-2-1) Gaussian, edge texels repeated.</summary>
    static void Smooth(float[] a, float[] tmp, int n)
    {
        Parallel.For(0, n, j =>
        {
            for (int i = 0; i < n; i++)
                tmp[j * n + i] = 0.25f * (a[j * n + Math.Max(i - 1, 0)] + 2 * a[j * n + i] + a[j * n + Math.Min(i + 1, n - 1)]);
        });
        Parallel.For(0, n, j =>
        {
            int jm = Math.Max(j - 1, 0), jp = Math.Min(j + 1, n - 1);
            for (int i = 0; i < n; i++)
                a[j * n + i] = 0.25f * (tmp[jm * n + i] + 2 * tmp[j * n + i] + tmp[jp * n + i]);
        });
    }

    static float SmoothStep(float lo, float hi, float x)
    {
        float t = Math.Clamp((x - lo) / (hi - lo), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>The <see cref="ShoreGrid.ExposureRule"/>: block maxima of the water's distance, a disc dilation over them, the ramp, a bilinear upsample.</summary>
    static float[] Exposure(float[] dist, int n, float texel, float maxDistance)
    {
        int nb = (n + Block - 1) / Block;
        var reachBlock = new float[nb * nb];
        Parallel.For(0, nb, bj =>
        {
            for (int bi = 0; bi < nb; bi++)
            {
                float m = 0;
                for (int j = bj * Block; j < Math.Min(n, (bj + 1) * Block); j++)
                    for (int i = bi * Block; i < Math.Min(n, (bi + 1) * Block); i++)
                        m = Math.Max(m, dist[j * n + i]);
                reachBlock[bj * nb + bi] = m;
            }
        });

        float blockSize = Block * texel;
        int r = (int)MathF.Ceiling(ShoreGrid.ReachRadius / blockSize);
        float reachBlocks = ShoreGrid.ReachRadius / blockSize + 0.5f;
        // The blocks padded by r on every side, outside the grid open sea where the edge block there is water; then the disc as one span of
        // columns per row.
        int np = nb + 2 * r;
        var padded = new float[np * np];
        for (int pj = 0; pj < np; pj++)
            for (int pi = 0; pi < np; pi++)
            {
                int ci = pi - r, cj = pj - r;
                padded[pj * np + pi] = (uint)ci < (uint)nb && (uint)cj < (uint)nb ? reachBlock[cj * nb + ci]
                    : reachBlock[Math.Clamp(cj, 0, nb - 1) * nb + Math.Clamp(ci, 0, nb - 1)] > 0 ? maxDistance : 0;
            }
        var span = new int[2 * r + 1];
        for (int dj = -r; dj <= r; dj++) span[dj + r] = (int)MathF.Floor(MathF.Sqrt(MathF.Max(reachBlocks * reachBlocks - dj * dj, 0)));
        var coarse = new float[nb * nb];
        Parallel.For(0, nb, bj =>
        {
            for (int bi = 0; bi < nb; bi++)
            {
                float m = 0;
                for (int dj = -r; dj <= r; dj++)
                {
                    int w = Math.Min(span[dj + r], r), row = (bj + dj + r) * np + bi + r;
                    for (int di = -w; di <= w; di++) m = MathF.Max(m, padded[row + di]);
                }
                coarse[bj * nb + bi] = SmoothStep(ShoreGrid.ReachLow, ShoreGrid.ReachHigh, m);
            }
        });

        var e = new float[n * n];
        Parallel.For(0, n, j =>
        {
            float fz = Math.Clamp((j + 0.5f) / Block - 0.5f, 0, nb - 1);
            int bj = Math.Min((int)fz, Math.Max(nb - 2, 0)), bj1 = Math.Min(bj + 1, nb - 1);
            float tz = fz - bj;
            for (int i = 0; i < n; i++)
            {
                float fx = Math.Clamp((i + 0.5f) / Block - 0.5f, 0, nb - 1);
                int bi = Math.Min((int)fx, Math.Max(nb - 2, 0)), bi1 = Math.Min(bi + 1, nb - 1);
                float tx = fx - bi;
                float a = coarse[bj * nb + bi] * (1 - tx) + coarse[bj * nb + bi1] * tx;
                float b = coarse[bj1 * nb + bi] * (1 - tx) + coarse[bj1 * nb + bi1] * tx;
                e[j * n + i] = a * (1 - tz) + b * tz;
            }
        });
        return e;
    }
}
