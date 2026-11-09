using Meitou.Data.World;

namespace Meitou.Game.Rendering.Water;

/// <summary>
/// The Meitou water's river map, baked at load from the whole-world heights (docs/formats/terrain.md "Flow map", docs/render-water.md "Meitou
/// water"). The game's <c>flowmap.png</c> does not follow the river channels, and the water stands flat at Y = 100 over a bed with no usable slope,
/// so the direction is derived: water whose half-width is a few texels at most is a river; along each stretch of it (the principal axis of the river
/// texels within <see cref="Reach"/> texels, which must be clearly elongated: ponds and marsh are not rivers) the water runs towards the lower bank,
/// the valley floor falling downstream (a regression of the lowest land beside the river against the position along the axis; a stretch without a
/// clear fall is still water). The real rivers end in lakes or just stop inland, so no route to the sea is used. One texel per flow-map texel (144
/// units); RGBA8: R, G the unit flow direction (x, z) as 0.5 + 0.5 v, B the half-width in texels over 8, A the river weight (0 outside, 1 in a clear
/// river, fading with the width and out over <see cref="Dilate"/> texels of land). The shader refines the direction with the shore field's channel
/// axis (the sign comes from this map).
/// </summary>
public static class RiverFlowBake
{
    /// <summary>
    /// The widest a river is, as the largest distance to land (in texels) found within <see cref="Radius"/>; water beyond is open.
    /// </summary>
    public const float OpenHalfWidth = 6f;

    /// <summary>
    /// Radius in texels over which the half-width is measured.
    /// </summary>
    public const int Radius = 5;

    /// <summary>
    /// Radius in texels over which the river's axis and fall are fitted.
    /// </summary>
    public const int Reach = 10;

    /// <summary>
    /// Radius in texels in which the bank's height is the lowest land.
    /// </summary>
    public const int BankRadius = 3;

    /// <summary>
    /// Texels of land round the water that get the river's direction too, so bilinear reads keep it up to the bank.
    /// </summary>
    public const int Dilate = 3;

    /// <summary>
    /// A connected river shorter than this many texels (144 units each) is not a river: puddles and marsh channels.
    /// </summary>
    public const int MinLength = 24;

    /// <summary>
    /// The most one texel counts for in the vote on a river's sense: a bank slope of this many height units a texel.
    /// </summary>
    public const float VoteCap = 2f;

    /// <summary>
    /// Passes of 3 × 3 weighted averaging over the fitted directions.
    /// </summary>
    public const int SmoothPasses = 4;

    /// <summary>
    /// Below this agreement of the directions within 2 texels (the length of their weighted mean, 0..1) a river fades out to still water.
    /// </summary>
    public const float MinCoherence = 0.55f;

    /// <summary>
    /// The eight neighbours of a texel, as (x, y) steps.
    /// </summary>
    static readonly (int X, int Y)[] Around = [(-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1)];

    /// <param name="heights">Heights at the texel corners ((size + 1)², row-major), raw 16-bit, as the whole-world grid at every eighth sample.</param>
    /// <param name="size">Texels per side of the map to bake (the corner grid is size + 1 per side).</param>
    /// <param name="waterRaw">The water level in the same raw units.</param>
    public static byte[] Bake(ushort[] heights, int size, ushort waterRaw)
    {
        int g = size + 1;
        if (heights.Length != g * g)
        {
            throw new ArgumentException("heights must be (size + 1)² corner samples", nameof(heights));
        }

        int n = size;
        var h = SampleHeights(heights, n, g, waterRaw, out var wet);
        var d = DistanceToLand(wet, n);
        var half = HalfWidths(d, wet, n);
        var bank = BankHeights(h, wet, half, n);
        var (fx, fz, weight, slope) = FitAxes(bank, wet, half, n);
        AssignSenses(fx, fz, slope, weight, n);
        SmoothDirections(fx, fz, weight, n);
        weight = AgreeingWeights(fx, fz, weight, n);
        (fx, fz, weight) = DilateOntoBanks(fx, fz, weight, n);
        return Pack(fx, fz, weight, half, n);
    }

    /// <summary>
    /// The texel heights (the mean of each texel's four corners) and which texels the water covers at any corner.
    /// </summary>
    static float[] SampleHeights(ushort[] heights, int n, int g, ushort waterRaw, out bool[] wet)
    {
        var h = new float[n * n];
        wet = new bool[n * n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int avg = (heights[j * g + i] + heights[j * g + i + 1] + heights[(j + 1) * g + i] + heights[(j + 1) * g + i + 1]) / 4;
                // Wet when the water covers any corner: the narrow rivers are a sample or two wide and an average would break them into pieces.
                wet[j * n + i] = Math.Min(Math.Min(heights[j * g + i], heights[j * g + i + 1]),
                    Math.Min(heights[(j + 1) * g + i], heights[(j + 1) * g + i + 1])) < waterRaw;
                h[j * n + i] = WorldLayout.RawToHeight((ushort)avg);
            }
        }

        return h;
    }

    /// <summary>
    /// The distance of each texel to land (two-pass chamfer; the map's edge counts as water: the sea goes on).
    /// </summary>
    static float[] DistanceToLand(bool[] wet, int n)
    {
        var d = new float[n * n];
        for (int k = 0; k < d.Length; k++) d[k] = wet[k] ? 1e6f : 0f;
        Sweep(d, n, 0, n, 1, 0, n, 1);
        Sweep(d, n, n - 1, -1, -1, n - 1, -1, -1);
        return d;
    }

    /// <summary>
    /// The half-width of each water texel: the largest distance to land within Radius. A texel further than OpenHalfWidth from land is open itself;
    /// only the rest scan.
    /// </summary>
    static float[] HalfWidths(float[] d, bool[] wet, int n)
    {
        var half = new float[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int k = y * n + x;
                if (!wet[k]) continue;
                if (d[k] > OpenHalfWidth)
                {
                    half[k] = d[k];
                    continue;
                }

                half[k] = LargestDistanceAround(d, n, x, y);
            }
        }

        return half;
    }

    /// <summary>
    /// The largest distance to land within <see cref="Radius"/> texels of (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    static float LargestDistanceAround(float[] d, int n, int x, int y)
    {
        float m = d[y * n + x];
        int y0 = Math.Max(y - Radius, 0), y1 = Math.Min(y + Radius, n - 1);
        int x0 = Math.Max(x - Radius, 0), x1 = Math.Min(x + Radius, n - 1);
        for (int yy = y0; yy <= y1; yy++)
        {
            for (int xx = x0; xx <= x1; xx++)
            {
                int dx = xx - x, dy = yy - y;
                if (dx * dx + dy * dy <= Radius * Radius && d[yy * n + xx] > m) m = d[yy * n + xx];
            }
        }

        return m;
    }

    /// <summary>
    /// Texel <paramref name="k"/> is water no wider than <see cref="OpenHalfWidth"/>.
    /// </summary>
    static bool IsRiver(bool[] wet, float[] half, int k) => wet[k] && half[k] <= OpenHalfWidth;

    /// <summary>
    /// The bank of each river texel: the lowest land within BankRadius of it.
    /// </summary>
    static float[] BankHeights(float[] h, bool[] wet, float[] half, int n)
    {
        var bank = new float[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int k = y * n + x;
                if (!IsRiver(wet, half, k)) continue;
                float lowest = float.MaxValue;
                for (int dy = -BankRadius; dy <= BankRadius; dy++)
                {
                    for (int dx = -BankRadius; dx <= BankRadius; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        bool outside = xx < 0 || yy < 0 || xx >= n || yy >= n;
                        if (outside || dx * dx + dy * dy > BankRadius * BankRadius || wet[yy * n + xx]) continue;
                        lowest = Math.Min(lowest, h[yy * n + xx]);
                    }
                }

                bank[k] = lowest == float.MaxValue ? h[k] : lowest;
            }
        }

        return bank;
    }

    /// <summary>
    /// Per river texel: the axis of the river texels within Reach (principal component of their positions; how elongated it is), and the bank's slope
    /// along it. The water runs down it. Returns the axis (x, z), the weight (0 where there is no clear axis) and the slope.
    /// </summary>
    static (float[] Fx, float[] Fz, float[] Weight, float[] Slope) FitAxes(float[] bank, bool[] wet, float[] half, int n)
    {
        var fx = new float[n * n];
        var fz = new float[n * n];
        var weight = new float[n * n];
        var slope = new float[n * n];
        var near = new List<(int X, int Y, float P)>(320);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int k = y * n + x;
                if (!IsRiver(wet, half, k)) continue;
                if (!FitAxisAt(bank, wet, half, n, x, y, near, out var tx, out var ty, out var axisSlope, out var aniso)) continue;
                fx[k] = tx;   // the axis for now, with no sense
                fz[k] = ty;
                slope[k] = axisSlope;   // units of height per texel along (tx, ty)
                weight[k] = (1f - Smooth(3f, OpenHalfWidth, half[k])) * Smooth(0.5f, 0.75f, aniso);
            }
        }

        return (fx, fz, weight, slope);
    }

    /// <summary>
    /// Fits the river's axis and bank slope around one river texel; false when the texel has too few river neighbours or no clear axis.
    /// </summary>
    static bool FitAxisAt(float[] bank, bool[] wet, float[] half, int n, int x, int y, List<(int X, int Y, float P)> near,
        out float tx, out float ty, out float axisSlope, out float aniso)
    {
        tx = ty = axisSlope = aniso = 0;
        near.Clear();
        double mx = 0, my = 0, mp = 0;
        for (int dy = -Reach; dy <= Reach; dy++)
        {
            for (int dx = -Reach; dx <= Reach; dx++)
            {
                int xx = x + dx, yy = y + dy;
                bool outside = xx < 0 || yy < 0 || xx >= n || yy >= n;
                if (outside || dx * dx + dy * dy > Reach * Reach || !IsRiver(wet, half, yy * n + xx)) continue;
                near.Add((dx, dy, bank[yy * n + xx]));
                mx += dx;
                my += dy;
                mp += bank[yy * n + xx];
            }
        }

        if (near.Count < 8) return false;
        mx /= near.Count;
        my /= near.Count;
        mp /= near.Count;
        double cxx = 0, cxy = 0, cyy = 0;
        foreach (var (ox, oy, _) in near)
        {
            double ax = ox - mx, ay = oy - my;
            cxx += ax * ax;
            cxy += ax * ay;
            cyy += ay * ay;
        }

        double mean = (cxx + cyy) / 2, diff = Math.Sqrt(((cxx - cyy) / 2) * ((cxx - cyy) / 2) + cxy * cxy);
        double l1 = mean + diff, l2 = mean - diff;
        if (l1 < 1e-6) return false;
        aniso = (float)((l1 - l2) / (l1 + l2));
        double theta = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
        double axisX = Math.Cos(theta), axisZ = Math.Sin(theta);
        double sPp = 0, sPt = 0;
        foreach (var (ox, oy, p) in near)
        {
            double u = (ox - mx) * axisX + (oy - my) * axisZ;
            sPt += u * u;
            sPp += u * (p - mp);
        }

        if (sPt < 1e-6) return false;
        if (aniso < 0.5f) return false;
        tx = (float)axisX;
        ty = (float)axisZ;
        axisSlope = (float)(sPp / sPt);
        return true;
    }

    /// <summary>
    /// The sense of the axes. One stretch's sense has to be the same all along it, but the bank's height is noisy from texel to texel, so the senses
    /// are made consistent first (spreading from a texel to its neighbours, each turned to agree with the one before it) and the fall decides once
    /// for the whole connected river: the sum of the bank's slope along its flow. A river whose sum is not clearly downhill, or which is a few texels
    /// long, is no river.
    /// </summary>
    static void AssignSenses(float[] fx, float[] fz, float[] slope, float[] weight, int n)
    {
        var seen = new bool[n * n];
        var stack = new Stack<int>();
        var members = new List<int>();
        for (int start = 0; start < n * n; start++)
        {
            if (weight[start] <= 0 || seen[start]) continue;
            members.Clear();
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                members.Add(k);
                int x = k % n, y = k / n;
                foreach (var (ox, oy) in Around)
                {
                    int xx = x + ox, yy = y + oy;
                    if (xx < 0 || yy < 0 || xx >= n || yy >= n) continue;
                    int q = yy * n + xx;
                    if (weight[q] <= 0 || seen[q]) continue;
                    seen[q] = true;
                    if (fx[k] * fx[q] + fz[k] * fz[q] < 0)
                    {
                        fx[q] = -fx[q];
                        fz[q] = -fz[q];
                        slope[q] = -slope[q];
                    }

                    stack.Push(q);
                }
            }

            VoteSense(members, fx, fz, slope, weight);
        }
    }

    /// <summary>
    /// Decides one connected river's sense by the sum of its bank slopes, and keeps the river's weight only when that sum is clearly downhill.
    /// </summary>
    static void VoteSense(List<int> members, float[] fx, float[] fz, float[] slope, float[] weight)
    {
        double net = 0, total = 0;
        foreach (int k in members)
        {
            float v = Math.Clamp(slope[k], -VoteCap, VoteCap);   // clamped: a cliff beside the river is one vote
            net += v;
            total += Math.Abs(v);
        }

        float confidence = total > 0 ? (float)(Math.Abs(net) / total) : 0f;
        float keep = members.Count >= MinLength ? Smooth(0.1f, 0.4f, confidence) : 0f;
        bool flip = net > 0;   // the bank rises along the axis: the water runs the other way
        foreach (int k in members)
        {
            if (flip)
            {
                fx[k] = -fx[k];
                fz[k] = -fz[k];
            }

            weight[k] *= keep;
        }
    }

    /// <summary>
    /// Smooths the directions: each texel's axis was fitted on its own, so neighbours (and neighbouring rivers, each with its own vote) can differ by
    /// a lot, which showed as hard seams along the texel grid. A few passes of weighted averaging over 3 × 3 river texels.
    /// </summary>
    static void SmoothDirections(float[] fx, float[] fz, float[] weight, int n)
    {
        for (int pass = 0; pass < SmoothPasses; pass++)
        {
            var sx = new float[n * n];
            var sz = new float[n * n];
            for (int y = 1; y < n - 1; y++)
            {
                for (int x = 1; x < n - 1; x++)
                {
                    int k = y * n + x;
                    if (weight[k] <= 0) continue;
                    float ax = fx[k] * weight[k], az = fz[k] * weight[k];
                    foreach (var (ox, oy) in Around)
                    {
                        int q = (y + oy) * n + x + ox;
                        ax += fx[q] * weight[q];
                        az += fz[q] * weight[q];
                    }

                    float len = MathF.Sqrt(ax * ax + az * az);
                    (sx[k], sz[k]) = len > 1e-4f ? (ax / len, az / len) : (fx[k], fz[k]);
                }
            }

            for (int k = 0; k < n * n; k++)
            {
                if (weight[k] > 0) (fx[k], fz[k]) = (sx[k], sz[k]);
            }
        }
    }

    /// <summary>
    /// The weight fades where the directions within 2 texels still disagree (a pond or junction where the fitted axes fan out radially, two rivers
    /// voting head-on into each other): there the water is still rather than spinning.
    /// </summary>
    static float[] AgreeingWeights(float[] fx, float[] fz, float[] weight, int n)
    {
        var agreed = (float[])weight.Clone();
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int k = y * n + x;
                if (weight[k] <= 0) continue;
                float ax = 0, az = 0, sw = 0;
                for (int oy = -2; oy <= 2; oy++)
                {
                    for (int ox = -2; ox <= 2; ox++)
                    {
                        int xx = x + ox, yy = y + oy;
                        if (xx < 0 || yy < 0 || xx >= n || yy >= n) continue;
                        int q = yy * n + xx;
                        ax += fx[q] * weight[q];
                        az += fz[q] * weight[q];
                        sw += weight[q];
                    }
                }

                agreed[k] = weight[k] * Smooth(MinCoherence, MinCoherence + 0.3f, sw > 0 ? MathF.Sqrt(ax * ax + az * az) / sw : 0);
            }
        }

        return agreed;
    }

    /// <summary>
    /// Dilates the water onto the neighbouring texels (the banks), each step weakening the weight.
    /// </summary>
    static (float[] Fx, float[] Fz, float[] Weight) DilateOntoBanks(float[] fx, float[] fz, float[] weight, int n)
    {
        for (int step = 0; step < Dilate; step++)
        {
            var nx = (float[])fx.Clone();
            var nz = (float[])fz.Clone();
            var nw = (float[])weight.Clone();
            for (int y = 1; y < n - 1; y++)
            {
                for (int x = 1; x < n - 1; x++)
                {
                    int k = y * n + x;
                    if (weight[k] > 0) continue;
                    float sx = 0, sz = 0, sw = 0;
                    foreach (var (ox, oy) in Around)
                    {
                        int q = (y + oy) * n + x + ox;
                        sx += fx[q] * weight[q];
                        sz += fz[q] * weight[q];
                        sw += weight[q];
                    }

                    if (sw <= 0) continue;
                    float len = MathF.Sqrt(sx * sx + sz * sz);
                    if (len < 1e-4f) continue;
                    nx[k] = sx / len;
                    nz[k] = sz / len;
                    nw[k] = sw / 8f * 0.9f;
                }
            }

            fx = nx;
            fz = nz;
            weight = nw;
        }

        return (fx, fz, weight);
    }

    /// <summary>
    /// The RGBA8 map: the unit direction (x, z) as 0.5 + 0.5 v (still water points to the middle), the half-width over 8, and the weight.
    /// </summary>
    static byte[] Pack(float[] fx, float[] fz, float[] weight, float[] half, int n)
    {
        var result = new byte[n * n * 4];
        for (int k = 0; k < n * n; k++)
        {
            bool any = weight[k] > 0;
            result[k * 4] = (byte)Math.Round(any ? 255 * (0.5f + 0.5f * fx[k]) : 128);
            result[k * 4 + 1] = (byte)Math.Round(any ? 255 * (0.5f + 0.5f * fz[k]) : 128);
            result[k * 4 + 2] = (byte)Math.Round(255 * Math.Clamp(half[k] / 8f, 0f, 1f));
            result[k * 4 + 3] = (byte)Math.Round(255 * Math.Clamp(weight[k], 0f, 1f));
        }

        return result;
    }

    /// <summary>
    /// Hermite smoothstep of <paramref name="x"/> between <paramref name="a"/> and <paramref name="b"/> (0 to 1).
    /// </summary>
    static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// One chamfer pass of the distance to land (texels; 0 on land): each texel takes the least of its value and its already visited neighbours' plus
    /// 1 (straight) or √2 (diagonal), going from (<paramref name="x0"/>, <paramref name="y0"/>) by (<paramref name="dx"/>, <paramref name="dy"/>).
    /// </summary>
    static void Sweep(float[] d, int n, int y0, int y1, int dy, int x0, int x1, int dx)
    {
        for (int y = y0; y != y1; y += dy)
            for (int x = x0; x != x1; x += dx)
            {
                int k = y * n + x;
                if (d[k] == 0) continue;
                float v = d[k];
                if (x - dx >= 0 && x - dx < n) v = Math.Min(v, d[k - dx] + 1f);
                if (y - dy >= 0 && y - dy < n) v = Math.Min(v, d[k - dy * n] + 1f);
                if (x - dx >= 0 && x - dx < n && y - dy >= 0 && y - dy < n) v = Math.Min(v, d[k - dy * n - dx] + 1.4142f);
                if (x + dx >= 0 && x + dx < n && y - dy >= 0 && y - dy < n) v = Math.Min(v, d[k - dy * n + dx] + 1.4142f);
                d[k] = v;
            }
    }
}