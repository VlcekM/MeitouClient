using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Game.Rendering.Water;

/// <summary>
/// The shore distance field's CPU bake (no GPU; docs/render-water.md "Shore distance field"). A square grid of <see cref="Size"/>² texels of
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
    /// <summary>
    /// The default clamp of the signed distance (world units).
    /// </summary>
    public const float DefaultMaxDistance = 4000f;

    /// <summary>
    /// Exposure rule (the "reach" of a water texel is the largest distance-to-shore of any water within <see cref="ReachRadius"/> units of it,
    /// measured on 8-texel blocks): exposure = smoothstep(<see cref="ReachLow"/>, <see cref="ReachHigh"/>, reach). A pond or bay whose water
    /// never lies more than ~600 units from a shore within 1500 units of the point is 0 (a town pond in the swamps reaches about 400); anywhere with
    /// 1200+ units of open water within 1500 is 1 (surf needs a long stretch of open water to build over).
    /// Beyond the grid's edge the water counts as open sea (reach = max) when the nearest edge block is water, else as land.
    /// </summary>
    public const float ReachRadius = 1500f, ReachLow = 600f, ReachHigh = 1200f;

    /// <summary>
    /// <see cref="ReachRadius"/>, <see cref="ReachLow"/> and <see cref="ReachHigh"/> as a readable rule.
    /// </summary>
    public const string ExposureRule = "smoothstep(600, 1200, max distance-to-shore of water within 1500 units)";

    /// <summary>
    /// Islets get no surf: the exposure is multiplied by smoothstep(<see cref="IsletAreaLow"/>, <see cref="IsletAreaHigh"/>, the area of the
    /// land mass of the nearest shore), discs of radius 250 to 450 units. A rock or outcrop in open water is as exposed as the coast, and
    /// without this breakers ringed it on every side and met in the middle.
    /// </summary>
    public const float IsletAreaLow = MathF.PI * 250f * 250f, IsletAreaHigh = MathF.PI * 450f * 450f;

    /// <summary>
    /// <see cref="IsletAreaLow"/> and <see cref="IsletAreaHigh"/> as a readable rule.
    /// </summary>
    public const string IsletRule = "x smoothstep(pi 250^2, pi 450^2, area of the nearest shore's land mass)";

    /// <summary>
    /// A grid over the given arrays (not copied).
    /// </summary>
    public ShoreGrid(int size, float texel, float x0, float z0, float maxDistance, float[] distance, float[] exposure)
    {
        (Size, Texel, X0, Z0, MaxDistance, Distance, Exposure) = (size, texel, x0, z0, maxDistance, distance, exposure);
    }

    /// <summary>
    /// Texels along the grid's side.
    /// </summary>
    public int Size { get; }

    /// <summary>
    /// World units per texel.
    /// </summary>
    public float Texel { get; }

    /// <summary>
    /// World X and Z of the grid's low corner (the outer edge of texel (0, 0)).
    /// </summary>
    public float X0 { get; }

    /// <summary>
    /// See <see cref="X0"/>.
    /// </summary>
    public float Z0 { get; }

    /// <summary>
    /// The clamp of <see cref="Distance"/> (world units).
    /// </summary>
    public float MaxDistance { get; }

    /// <summary>
    /// Signed distance, row-major (row = Z).
    /// </summary>
    public float[] Distance { get; }

    /// <summary>
    /// The exposure, 0 to 1, row-major (row = Z).
    /// </summary>
    public float[] Exposure { get; }

    /// <summary>
    /// World X of the grid's high edge.
    /// </summary>
    public float X1 => X0 + Size * Texel;

    /// <summary>
    /// World Z of the grid's high edge.
    /// </summary>
    public float Z1 => Z0 + Size * Texel;

    /// <summary>
    /// The grid as the texture's texels: (distance, exposure) pairs, row-major.
    /// </summary>
    public float[] Interleaved()
    {
        var rg = new float[Size * Size * 2];
        InterleaveInto(rg);
        return rg;
    }

    /// <summary>
    /// The same into an array of Size · Size · 2 floats the caller keeps from bake to bake.
    /// </summary>
    public void InterleaveInto(float[] rg)
    {
        if (rg.Length != Size * Size * 2)
        {
            throw new ArgumentException("the array has to hold Size * Size texels", nameof(rg));
        }

        for (int k = 0; k < Size * Size; k++)
        {
            (rg[2 * k], rg[2 * k + 1]) = (Distance[k], Exposure[k]);
        }
    }

    /// <summary>
    /// Whether every texel has the same (distance, exposure): the field round the eye is all land (every distance the farthest, inland) or
    /// all open water. Such a field reads the same everywhere, whatever its size, so one texel stands for it.
    /// </summary>
    public bool IsUniform(out Vector2 value)
    {
        value = new Vector2(Distance[0], Exposure[0]);
        return !Distance.AsSpan().ContainsAnyExcept(value.X) && !Exposure.AsSpan().ContainsAnyExcept(value.Y);
    }

    /// <summary>
    /// Signed distance and exposure at a world point (bilinear, clamped at the grid's edge), as the GPU texture reads them.
    /// </summary>
    public Vector2 Sample(float x, float z)
    {
        float fx = Math.Clamp((x - X0) / Texel - 0.5f, 0, Size - 1), fz = Math.Clamp((z - Z0) / Texel - 0.5f, 0, Size - 1);
        int i = Math.Min((int)fx, Size - 2), j = Math.Min((int)fz, Size - 2);
        float tx = fx - i, tz = fz - j;
        Vector2 At(int a, int b) => new(Distance[b * Size + a], Exposure[b * Size + a]);
        return Vector2.Lerp(Vector2.Lerp(At(i, j), At(i + 1, j), tx), Vector2.Lerp(At(i, j + 1), At(i + 1, j + 1), tx), tz);
    }
}

/// <summary>
/// The shore field's CPU bake into a <see cref="ShoreGrid"/>.
/// </summary>
internal static class ShoreBake
{
    /// <summary>
    /// No crossing found: the squared distance of a texel with none.
    /// </summary>
    const float None = 1e20f;

    /// <summary>
    /// Texels per exposure block edge.
    /// </summary>
    const int Block = 8;

    /// <summary>
    /// The centre a bake for (<paramref name="x"/>, <paramref name="z"/>) would use: snapped to whole texels.
    /// </summary>
    public static (float X, float Z) SnapCentre(float x, float z, float texel) => (MathF.Round(x / texel) * texel, MathF.Round(z / texel) * texel);

    /// <summary>
    /// Bakes the field round (<paramref name="cx"/>, <paramref name="cz"/>) (snapped to whole texels). The waterline is where
    /// 100 − height changes sign between neighbouring texels, placed by linear interpolation between them (sub-texel); distances are to those
    /// crossing points (8SSEDT: two sweeps carrying the nearest crossing point, O(N²), near-exact), then smoothed with one 3×3 binomial
    /// Gaussian so the gradient has no kinks. The height sampling, sign and smoothing are parallel over rows; the sweeps run on one thread.
    /// </summary>
    public static ShoreGrid Bake(Func<float, float, float> height, float cx, float cz, int size = 1024, float texel = 10f,
        float maxDistance = ShoreGrid.DefaultMaxDistance, Scratch? scratch = null)
    {
        (cx, cz) = SnapCentre(cx, cz, texel);
        float x0 = cx - size * 0.5f * texel, z0 = cz - size * 0.5f * texel;
        int n = size;
        scratch ??= new Scratch();

        // s > 0: water.
        var s = Scratch.Get(ref scratch.S, n * n);
        Parallel.For(0, n, j =>
        {
            float z = z0 + (j + 0.5f) * texel;
            for (int i = 0; i < n; i++)
            {
                s[j * n + i] = WorldWater.Height - height(x0 + (i + 0.5f) * texel, z);
            }
        });

        var seeds = Scratch.Get(ref scratch.Seeds, n * n);
        SeedCrossings(s, seeds, n);
        Sweeps(seeds, n);
        var dist = SignedDistance(s, seeds, n, texel, maxDistance, Scratch.Get(ref scratch.Dist, n * n));
        var tmp = Scratch.Get(ref scratch.Tmp, n * n);
        Smooth(dist, tmp, n);
        var exposure = Exposure(dist, n, texel, maxDistance, IsletBlocks(s, seeds, n, texel, scratch), Scratch.Get(ref scratch.Exposure, n * n));
        return new ShoreGrid(n, texel, x0, z0, maxDistance, dist, exposure);
    }

    /// <summary>
    /// Seeds: each texel takes the nearest waterline crossing on its four edges, as a point in texel units.
    /// </summary>
    static void SeedCrossings(float[] s, Seed[] seeds, int n)
    {
        Parallel.For(0, n, j =>
        {
            for (int i = 0; i < n; i++)
            {
                seeds[j * n + i] = NearestCrossing(s, n, i, j);
            }
        });
    }

    /// <summary>
    /// The nearest waterline crossing on texel (<paramref name="i"/>, <paramref name="j"/>)'s four edges (<see cref="None"/> when there is none).
    /// </summary>
    static Seed NearestCrossing(float[] s, int n, int i, int j)
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

        Edge(i - 1, j);
        Edge(i + 1, j);
        Edge(i, j - 1);
        Edge(i, j + 1);
        return new Seed { D = best, X = bx, Z = bz };
    }

    /// <summary>
    /// The signed distance of each texel to the waterline (positive over water), in world units, clamped to <paramref name="maxDistance"/>.
    /// </summary>
    static float[] SignedDistance(float[] s, Seed[] seeds, int n, float texel, float maxDistance, float[] dist)
    {
        Parallel.For(0, n, j =>
        {
            for (int i = 0; i < n; i++)
            {
                float sq = seeds[j * n + i].D;
                float d = sq >= None * 0.5f ? maxDistance : Math.Min(MathF.Sqrt(sq) * texel, maxDistance);
                dist[j * n + i] = s[j * n + i] > 0 ? d : -d;
            }
        });
        return dist;
    }

    /// <summary>
    /// The <see cref="ShoreGrid.IsletRule"/> per exposure block: the area of the land mass each texel's nearest shore belongs to (4-connected
    /// land texels, by a flood fill; one touching the grid's edge counts as large, it may go on beyond), ramped, averaged over the block and
    /// then over its 3×3 neighbours, so the surf fades over a few hundred units where the nearest shore switches from an islet to the coast
    /// behind it instead of cutting off.
    /// </summary>
    static float[] IsletBlocks(float[] s, Seed[] seeds, int n, float texel, Scratch scratch)
    {
        var label = Scratch.Get(ref scratch.Label, n * n);
        Array.Clear(label);
        var area = LabelLandMasses(s, n, texel, label, Scratch.Get(ref scratch.Stack, n * n));
        var ramp = area.Select(a => SmoothStep(ShoreGrid.IsletAreaLow, ShoreGrid.IsletAreaHigh, a)).ToArray();
        if (ramp.All(r => r >= 1)) return [];   // no islets: nothing to scale

        int nb = (n + Block - 1) / Block;
        return BlurBlocks(IsletBlockAverages(s, seeds, n, label, ramp), nb);
    }

    /// <summary>
    /// Labels every land mass (4-connected land texels) with its id, and returns its area by id (index 0 unused); a mass touching the grid's edge
    /// counts as <see cref="float.MaxValue"/>.
    /// </summary>
    static List<float> LabelLandMasses(float[] s, int n, float texel, int[] label, int[] stack)
    {
        var area = new List<float> { 0 };
        for (int k0 = 0; k0 < n * n; k0++)
        {
            if (s[k0] > 0 || label[k0] != 0) continue;
            int id = area.Count, top = 0, count = 0;
            bool edge = false;
            label[k0] = id;
            stack[top++] = k0;
            while (top > 0)
            {
                int k = stack[--top], i = k % n;
                count++;
                if (i == 0 || i == n - 1 || k < n || k >= n * n - n)
                {
                    edge = true;
                }

                if (i > 0 && label[k - 1] == 0 && s[k - 1] <= 0)
                {
                    label[k - 1] = id;
                    stack[top++] = k - 1;
                }

                if (i < n - 1 && label[k + 1] == 0 && s[k + 1] <= 0)
                {
                    label[k + 1] = id;
                    stack[top++] = k + 1;
                }

                if (k >= n && label[k - n] == 0 && s[k - n] <= 0)
                {
                    label[k - n] = id;
                    stack[top++] = k - n;
                }

                if (k < n * n - n && label[k + n] == 0 && s[k + n] <= 0)
                {
                    label[k + n] = id;
                    stack[top++] = k + n;
                }
            }

            area.Add(edge ? float.MaxValue : count * texel * texel);
        }

        return area;
    }

    /// <summary>
    /// The islet ramp averaged over each exposure block: a texel takes the ramp of the land mass its nearest shore belongs to, and 1 at sea.
    /// </summary>
    static float[] IsletBlockAverages(float[] s, Seed[] seeds, int n, int[] label, float[] ramp)
    {
        int nb = (n + Block - 1) / Block;
        var blocks = new float[nb * nb];
        Parallel.For(0, nb, bj =>
        {
            for (int bi = 0; bi < nb; bi++)
            {
                float sum = 0;
                int count = 0;
                for (int j = bj * Block; j < Math.Min(n, (bj + 1) * Block); j++)
                {
                    for (int i = bi * Block; i < Math.Min(n, (bi + 1) * Block); i++, count++)
                    {
                        int k = j * n + i, land = k;
                        if (s[k] > 0)
                        {
                            // The nearest crossing lies on the edge between two texel centres: the land one of them.
                            var seed = seeds[k];
                            if (seed.D >= None * 0.5f)
                            {
                                sum += 1;
                                continue;
                            }

                            int ax = Math.Clamp((int)MathF.Floor(seed.X), 0, n - 1), az = Math.Clamp((int)MathF.Floor(seed.Z), 0, n - 1);
                            land = s[az * n + ax] <= 0 ? az * n + ax
                                : Math.Clamp((int)MathF.Ceiling(seed.Z), 0, n - 1) * n + Math.Clamp((int)MathF.Ceiling(seed.X), 0, n - 1);
                            if (s[land] > 0)
                            {
                                sum += 1;
                                continue;
                            }
                        }

                        sum += ramp[label[land]];
                    }
                }

                blocks[bj * nb + bi] = sum / count;
            }
        });
        return blocks;
    }

    /// <summary>
    /// The block values averaged over their 3 × 3 neighbourhoods, so the surf fades over a few hundred units.
    /// </summary>
    static float[] BlurBlocks(float[] blocks, int nb)
    {
        var blurred = new float[nb * nb];
        Parallel.For(0, nb, bj =>
        {
            for (int bi = 0; bi < nb; bi++)
            {
                float sum = 0;
                for (int dj = -1; dj <= 1; dj++)
                {
                    for (int di = -1; di <= 1; di++)
                    {
                        sum += blocks[Math.Clamp(bj + dj, 0, nb - 1) * nb + Math.Clamp(bi + di, 0, nb - 1)];
                    }
                }

                blurred[bj * nb + bi] = sum / 9;
            }
        });
        return blurred;
    }

    /// <summary>
    /// A texel's nearest waterline crossing point.
    /// </summary>
    internal struct Seed
    {
        /// <summary>
        /// D: the squared distance (texel units², <see cref="None"/> for none); X, Z: the crossing point in texel units.
        /// </summary>
        public float D, X, Z;
    }

    /// <summary>
    /// The arrays of a bake (a 1024² bake otherwise allocates some 40 MB on the large object heap, and the viewer bakes up to four times a second
    /// in a fast flight: a third of all the allocation of a flight, and the gen2 collections that go with it). One bake at a time per scratch; the
    /// grid a bake returns is made of its arrays, so it is good until the next bake with the same scratch.
    /// </summary>
    internal sealed class Scratch
    {
        /// <summary>
        /// The sign field (positive over water), the distance, the smoothing's scratch and the exposure.
        /// </summary>
        internal float[]? S, Dist, Tmp, Exposure;

        /// <summary>
        /// The nearest crossing per texel.
        /// </summary>
        internal Seed[]? Seeds;

        /// <summary>
        /// The land mass labels per texel and the flood fill's stack.
        /// </summary>
        internal int[]? Label, Stack;

        /// <summary>
        /// <paramref name="array"/> when it has <paramref name="length"/> elements, else a new array stored in it.
        /// </summary>
        internal static T[] Get<T>(ref T[]? array, int length) => array is { } a && a.Length == length ? a : array = new T[length];
    }

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
                for (int i = 0; i < n; i++)
                {
                    Try(a, n, i, j, i - 1, j);
                    Try(a, n, i, j, i, j - 1);
                    Try(a, n, i, j, i - 1, j - 1);
                    Try(a, n, i, j, i + 1, j - 1);
                }

                for (int i = n - 1; i >= 0; i--) Try(a, n, i, j, i + 1, j);
            }

            for (int j = n - 1; j >= 0; j--)
            {
                for (int i = n - 1; i >= 0; i--)
                {
                    Try(a, n, i, j, i + 1, j);
                    Try(a, n, i, j, i, j + 1);
                    Try(a, n, i, j, i + 1, j + 1);
                    Try(a, n, i, j, i - 1, j + 1);
                }

                for (int i = 0; i < n; i++) Try(a, n, i, j, i - 1, j);
            }
        }
    }

    /// <summary>
    /// One 3×3 binomial (1-2-1) Gaussian, edge texels repeated.
    /// </summary>
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

    /// <summary>
    /// Hermite smoothstep of <paramref name="x"/> between <paramref name="lo"/> and <paramref name="hi"/> (0 to 1).
    /// </summary>
    static float SmoothStep(float lo, float hi, float x)
    {
        float t = Math.Clamp((x - lo) / (hi - lo), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// The <see cref="ShoreGrid.ExposureRule"/>: block maxima of the water's distance, a disc dilation over them, the ramp, a bilinear upsample.
    /// </summary>
    static float[] Exposure(float[] dist, int n, float texel, float maxDistance, float[] islets, float[] e)
    {
        int nb = (n + Block - 1) / Block;
        var reachBlock = BlockMaxima(dist, n, nb);
        float blockSize = Block * texel;
        int r = (int)MathF.Ceiling(ShoreGrid.ReachRadius / blockSize);
        float reachBlocks = ShoreGrid.ReachRadius / blockSize + 0.5f;
        var coarse = DiscMaxima(reachBlock, nb, r, reachBlocks, maxDistance, islets);
        UpsampleExposure(coarse, n, nb, e);
        return e;
    }

    /// <summary>
    /// The largest distance of the water in each exposure block.
    /// </summary>
    static float[] BlockMaxima(float[] dist, int n, int nb)
    {
        var reachBlock = new float[nb * nb];
        Parallel.For(0, nb, bj =>
        {
            for (int bi = 0; bi < nb; bi++)
            {
                float m = 0;
                for (int j = bj * Block; j < Math.Min(n, (bj + 1) * Block); j++)
                {
                    for (int i = bi * Block; i < Math.Min(n, (bi + 1) * Block); i++)
                    {
                        m = Math.Max(m, dist[j * n + i]);
                    }
                }

                reachBlock[bj * nb + bi] = m;
            }
        });
        return reachBlock;
    }

    /// <summary>
    /// The reach of each block: the largest block maximum within a disc of <paramref name="reachBlocks"/> blocks, times the ramp, times the islet
    /// factor. The blocks are padded by <paramref name="r"/> on every side, outside the grid open sea where the edge block there is water.
    /// </summary>
    static float[] DiscMaxima(float[] reachBlock, int nb, int r, float reachBlocks, float maxDistance, float[] islets)
    {
        int np = nb + 2 * r;
        var padded = new float[np * np];
        for (int pj = 0; pj < np; pj++)
        {
            for (int pi = 0; pi < np; pi++)
            {
                int ci = pi - r, cj = pj - r;
                bool inside = (uint)ci < (uint)nb && (uint)cj < (uint)nb;
                float edge = reachBlock[Math.Clamp(cj, 0, nb - 1) * nb + Math.Clamp(ci, 0, nb - 1)] > 0 ? maxDistance : 0;
                padded[pj * np + pi] = inside ? reachBlock[cj * nb + ci] : edge;
            }
        }

        // The disc as one span of columns per row.
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

                float ramp = SmoothStep(ShoreGrid.ReachLow, ShoreGrid.ReachHigh, m);
                coarse[bj * nb + bi] = ramp * (islets.Length > 0 ? islets[bj * nb + bi] : 1);
            }
        });
        return coarse;
    }

    /// <summary>
    /// Bilinear upsample of the block values to every texel of the grid.
    /// </summary>
    static void UpsampleExposure(float[] coarse, int n, int nb, float[] e)
    {
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
    }
}