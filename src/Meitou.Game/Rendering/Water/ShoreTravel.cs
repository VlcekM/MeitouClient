using System.Numerics;

namespace Meitou.Game.Rendering.Water;

/// <summary>
/// The shore waves' travel times (docs/render-water.md "Shore waves"): one swell from <see cref="Direction"/> arrives as a plane wave over the water
/// deeper than <see cref="FullDepth"/> and runs on into the shallows at the shallow-water celerity, so its crests slow, bunch up and turn towards the
/// depth contours, wrap round headlands and the ends of walls, and never follow the distance to the shore. A first-arrival (Dijkstra) march over the
/// shore field's grid, 8 neighbours; the walls and posts standing in the water (the buildings' collision cut at the water's height) block it. With
/// the times, how much of the swell is left: 1 on the open water, thinned where its way runs beside land or a wall (a narrow inlet, the lee of a
/// headland or a pier).
/// </summary>
internal static class ShoreTravel
{
    /// <summary>
    /// The way the swell runs (unit, world X/Z): one fixed direction, so a moved field or a change of weather never moves a crest.
    /// </summary>
    public static readonly Vector2 Direction = Vector2.Normalize(new Vector2(-0.55f, 0.83f));

    /// <summary>
    /// The celerity over deep water (units/s) and the depth from which the waves feel the floor (units); in the shallows
    /// c = <see cref="DeepCelerity"/>·sqrt(depth / <see cref="FullDepth"/>).
    /// </summary>
    public const float DeepCelerity = 22, FullDepth = 60;

    /// <summary>
    /// The shallowest depth the celerity takes (units): the beach above the water runs at that speed, so the run-up keeps a phase.
    /// </summary>
    public const float MinDepth = 1;

    /// <summary>
    /// How far inland the march goes (units of shore distance).
    /// </summary>
    const float Inland = 250;

    /// <summary>
    /// How much a neighbour beside the way that is land or a wall thins the swell (against 1 for a neighbour it comes from).
    /// </summary>
    const float Beside = 0.1f;

    /// <summary>
    /// The celerity over the beach (units/s): the run-up's phase climbs it about a wave period per 110 units, as the swash does.
    /// </summary>
    public const float LandCelerity = 14;

    /// <summary>
    /// The 8 neighbours' steps (texels) and their lengths.
    /// </summary>
    static readonly (int X, int Z, float Length)[] Steps =
        [(1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1), (1, 1, 1.41421356f), (1, -1, 1.41421356f), (-1, 1, 1.41421356f), (-1, -1, 1.41421356f)];

    /// <summary>
    /// The celerity at a depth (units/s).
    /// </summary>
    public static float Celerity(float depth) =>
        depth <= 0 ? LandCelerity : DeepCelerity * MathF.Sqrt(Math.Clamp(depth / FullDepth, MinDepth / FullDepth, 1));

    /// <summary>
    /// Fills <paramref name="travel"/> (seconds, the world-plane time of the swell, the same for every field) and <paramref name="swell"/> (0 to 1)
    /// for the grid of <paramref name="depth"/> (units, water positive) and <paramref name="dist"/> (shore distance), its texel (0, 0) at world
    /// (<paramref name="x0"/>, <paramref name="z0"/>).
    /// </summary>
    public static void Bake(float[] depth, float[] dist, int n, float texel, float x0, float z0, IReadOnlyList<(Vector2 A, Vector2 B)>? walls,
        float[] travel, float[] swell)
    {
        for (int k = 0; k < n * n; k++)
            if (!float.IsFinite(depth[k])) depth[k] = -1;   // a height that is not a number is land: it cannot spread through the march

        var blocked = Walls(walls, n, texel, x0, z0);
        var order = March(depth, dist, n, texel, x0, z0, blocked, travel, swell);
        foreach (int k in order) swell[k] = Left(k, n, depth, blocked, travel, swell);

        Fill(blocked, travel, n);
        for (int k = 0; k < n * n; k++)
            if (float.IsInfinity(travel[k])) travel[k] = Plane(k, n, texel, x0, z0);

        Soften(travel, blocked, n, 16);
        Soften(swell, blocked, n, 2);
        ReplaceNonFinite(travel, swell, n, texel, x0, z0);
    }

    /// <summary>
    /// The first-arrival march: the open texels at least <see cref="SeedDepth"/> deep are seeded with the plane wave's time and the whole swell, then
    /// the times spread from them. Returns the texels the march reached, in the order they settled.
    /// </summary>
    static List<int> March(float[] depth, float[] dist, int n, float texel, float x0, float z0, bool[] blocked, float[] travel, float[] swell)
    {
        float seedDepth = SeedDepth(depth, blocked);
        var settled = new bool[n * n];
        var order = new List<int>(n * 16);
        var queue = new PriorityQueue<int, float>();
        for (int k = 0; k < n * n; k++)
        {
            travel[k] = float.PositiveInfinity;
            swell[k] = 0;
            if (blocked[k] || depth[k] < seedDepth) continue;

            travel[k] = Plane(k, n, texel, x0, z0);
            swell[k] = 1;
            settled[k] = true;
        }

        for (int k = 0; k < n * n; k++)
            if (settled[k]) Relax(k, n, texel, depth, dist, blocked, settled, travel, queue);

        while (queue.TryDequeue(out int k, out float t))
        {
            if (settled[k] || t > travel[k]) continue;

            settled[k] = true;
            order.Add(k);
            Relax(k, n, texel, depth, dist, blocked, settled, travel, queue);
        }

        return order;
    }

    /// <summary>
    /// A time or swell the softening left not finite is the plane wave's time, or no swell.
    /// </summary>
    static void ReplaceNonFinite(float[] travel, float[] swell, int n, float texel, float x0, float z0)
    {
        for (int k = 0; k < n * n; k++)
        {
            if (!float.IsFinite(travel[k])) travel[k] = Plane(k, n, texel, x0, z0);
            if (!float.IsFinite(swell[k])) swell[k] = 0;
        }
    }

    /// <summary>
    /// Passes of a 3×3 binomial blur over the open texels: where two arrivals meet (behind an island, off a wall's end) the first-arrival time has
    /// a crease, and a crest following it would have a corner; softened, the crests round it.
    /// </summary>
    static void Soften(float[] a, bool[] blocked, int n, int passes)
    {
        var b = new float[n * n];
        for (int pass = 0; pass < passes; pass++)
        {
            Parallel.For(0, n, j => SoftenRow(a, b, blocked, n, j));
            Array.Copy(b, a, n * n);
        }
    }

    /// <summary>
    /// One row <paramref name="j"/> of a <see cref="Soften"/> pass, from <paramref name="a"/> into <paramref name="b"/>; a blocked texel is copied.
    /// </summary>
    static void SoftenRow(float[] a, float[] b, bool[] blocked, int n, int j)
    {
        for (int i = 0; i < n; i++)
        {
            int k = j * n + i;
            if (blocked[k])
            {
                b[k] = a[k];
                continue;
            }

            float sum = 0, weight = 0;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int ii = i + dx, jj = j + dz;
                    if ((uint)ii >= (uint)n || (uint)jj >= (uint)n || blocked[jj * n + ii]) continue;

                    float w = (2 - MathF.Abs(dx)) * (2 - MathF.Abs(dz));
                    sum += a[jj * n + ii] * w;
                    weight += w;
                }

            b[k] = sum / weight;
        }
    }

    /// <summary>
    /// The seeds' depth: <see cref="FullDepth"/>, or where no water is that deep, 0.7 of the deepest (a shallow lake still has a swell, from its
    /// middle).
    /// </summary>
    static float SeedDepth(float[] depth, bool[] blocked)
    {
        float deepest = 0;
        for (int k = 0; k < depth.Length; k++)
            if (!blocked[k]) deepest = MathF.Max(deepest, depth[k]);

        return deepest >= FullDepth ? FullDepth : MathF.Max(deepest * 0.7f, 0.5f);
    }

    /// <summary>
    /// The plane wave's time at texel <paramref name="k"/>: its world position along <see cref="Direction"/> over the deep celerity.
    /// </summary>
    static float Plane(int k, int n, float texel, float x0, float z0)
    {
        var world = new Vector2(x0 + (k % n + 0.5f) * texel, z0 + (k / n + 0.5f) * texel);

        return Vector2.Dot(world, Direction) / DeepCelerity;
    }

    /// <summary>
    /// The march's step from the settled texel <paramref name="k"/>: each open neighbour not yet settled (and not too far inland) takes the time
    /// through <paramref name="k"/> at the two texels' mean celerity when that is sooner, and is queued with it.
    /// </summary>
    static void Relax(int k, int n, float texel, float[] depth, float[] dist, bool[] blocked, bool[] settled, float[] travel,
        PriorityQueue<int, float> queue)
    {
        int i = k % n, j = k / n;
        float ck = Celerity(depth[k]);
        foreach (var (sx, sz, length) in Steps)
        {
            int ii = i + sx, jj = j + sz;
            if ((uint)ii >= (uint)n || (uint)jj >= (uint)n) continue;

            int m = jj * n + ii;
            if (settled[m] || blocked[m] || dist[m] < -Inland) continue;

            // A diagonal step may not squeeze between two blocked texels that touch at a corner.
            if (sx != 0 && sz != 0 && (blocked[j * n + ii] || blocked[jj * n + i])) continue;

            float t = travel[k] + length * texel * 2 / (ck + Celerity(depth[m]));
            if (t >= travel[m]) continue;

            travel[m] = t;
            queue.Enqueue(m, t);
        }
    }

    /// <summary>
    /// The swell left at k: the mean of the neighbours it came from (those reached before it), thinned by the land and walls beside its way (not
    /// those ahead: the beach the waves run up). On land itself nothing thins it, so the run-up keeps what reached the waterline.
    /// </summary>
    static float Left(int k, int n, float[] depth, bool[] blocked, float[] travel, float[] swell)
    {
        int i = k % n, j = k / n;
        float sum = 0, weight = 0;
        var way = Vector2.Zero;
        foreach (var (sx, sz, length) in Steps)
        {
            int ii = i + sx, jj = j + sz;
            if ((uint)ii >= (uint)n || (uint)jj >= (uint)n) continue;

            int m = jj * n + ii;
            if (blocked[m] || !(travel[m] < travel[k])) continue;

            float w = 1 / length;
            sum += swell[m] * w;
            weight += w;
            way -= new Vector2(sx, sz) * w;
        }

        if (weight <= 0) return 0;

        if (depth[k] > 0 && way.LengthSquared() > 1e-6f) ThinBeside(i, j, n, depth, blocked, Vector2.Normalize(way), ref weight);

        return sum / weight;
    }

    /// <summary>
    /// Adds to <paramref name="weight"/> the land and walls among texel (<paramref name="i"/>, <paramref name="j"/>)'s neighbours, each by how far
    /// it lies across the unit <paramref name="way"/> the swell came.
    /// </summary>
    static void ThinBeside(int i, int j, int n, float[] depth, bool[] blocked, Vector2 way, ref float weight)
    {
        foreach (var (sx, sz, length) in Steps)
        {
            int ii = i + sx, jj = j + sz;
            if ((uint)ii >= (uint)n || (uint)jj >= (uint)n) continue;

            int m = jj * n + ii;
            if (!blocked[m] && depth[m] > 0) continue;

            float across = 1 - MathF.Abs(Vector2.Dot(new Vector2(sx, sz) / length, way));
            weight += Beside * across / length;
        }
    }

    /// <summary>
    /// The texels the walls' segments cross (sampled every quarter texel, so a line is closed on the grid).
    /// </summary>
    static bool[] Walls(IReadOnlyList<(Vector2 A, Vector2 B)>? walls, int n, float texel, float x0, float z0)
    {
        var blocked = new bool[n * n];
        if (walls is null) return blocked;

        foreach (var (a, b) in walls)
        {
            var ta = new Vector2((a.X - x0) / texel, (a.Y - z0) / texel);
            var tb = new Vector2((b.X - x0) / texel, (b.Y - z0) / texel);
            int steps = (int)MathF.Ceiling(Vector2.Distance(ta, tb) * 4) + 1;
            for (int s = 0; s <= steps; s++)
            {
                var p = Vector2.Lerp(ta, tb, s / (float)steps);
                int i = (int)MathF.Floor(p.X), j = (int)MathF.Floor(p.Y);
                if ((uint)i < (uint)n && (uint)j < (uint)n) blocked[j * n + i] = true;
            }
        }

        return blocked;
    }

    /// <summary>
    /// A blocked texel takes the mean time of its open neighbours, so the bilinear filter does not smear a wall's zero across the water beside it.
    /// </summary>
    static void Fill(bool[] blocked, float[] travel, int n)
    {
        for (int k = 0; k < n * n; k++)
        {
            if (!blocked[k]) continue;

            int i = k % n, j = k / n;
            float sum = 0;
            int count = 0;
            foreach (var (sx, sz, _) in Steps)
            {
                int ii = i + sx, jj = j + sz;
                if ((uint)ii >= (uint)n || (uint)jj >= (uint)n || blocked[jj * n + ii]) continue;

                sum += travel[jj * n + ii];
                count++;
            }

            if (count > 0) travel[k] = sum / count;
        }
    }
}