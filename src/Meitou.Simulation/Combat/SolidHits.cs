using System.Numerics;
using Meitou.Simulation.Ragdolls;

namespace Meitou.Simulation.Combat;

/// <summary>
/// Where a bolt or a line of fire meets the world's solid parts: the terrain (the walkability's <see cref="IWalkability.TerrainHeight"/>, not its
/// walkable surfaces, which put a roof over whoever stands under one) and the buildings' collision (the ragdolls'
/// <see cref="IRagdollEnvironment.Buildings"/>). Safe to call from the partitioned phases. A building whose collision is still being read is not
/// there yet (it is gathered on a worker the first time it is asked for), so the first shots near it may pass it.
/// </summary>
static class SolidHits
{
    /// <summary>
    /// Length of the pieces a segment is cut into for the buildings' 32-unit collision cells (units).
    /// </summary>
    const float Chunk = 32;

    [ThreadStatic]
    static List<Vector3>? s_triangles;

    /// <summary>
    /// The fraction of <paramref name="seg"/> from <paramref name="from"/> where it first meets the terrain or a building, -1 when it meets neither.
    /// </summary>
    public static float Hit(IWalkability walk, IRagdollEnvironment? buildings, Vector3 from, Vector3 seg, float terrainStep, out bool building, bool roofs = false)
    {
        float ground = Terrain(walk, from, seg, terrainStep);
        float wall = buildings is null ? -1 : Buildings(buildings, from, seg, ground < 0 ? 1 : ground, roofs);
        building = wall >= 0;

        return building ? wall : ground;
    }

    /// <summary>
    /// The fraction of the segment where it goes under the terrain, -1 when it stays above (sampled every <paramref name="step"/> units).
    /// </summary>
    static float Terrain(IWalkability walk, Vector3 from, Vector3 seg, float step)
    {
        float length = seg.Length();
        int n = Math.Max(1, (int)MathF.Ceiling(length / step));
        float before = from.Y - walk.TerrainHeight(from.X, from.Z);
        if (before < 0) return 0;

        for (int k = 1; k <= n; k++)
        {
            var p = from + seg * (k / (float)n);
            float above = p.Y - walk.TerrainHeight(p.X, p.Z);
            if (above < 0) return (k - 1 + before / (before - above)) / n;

            before = above;
        }

        return -1;
    }

    /// <summary>
    /// The fraction of the segment where it first crosses a building's collision triangle before <paramref name="limit"/>, -1 for none.
    /// </summary>
    static float Buildings(IRagdollEnvironment env, Vector3 from, Vector3 seg, float limit, bool roofs)
    {
        var list = s_triangles ??= [];
        int pieces = Math.Max(1, (int)MathF.Ceiling(seg.Length() * limit / Chunk));
        for (int k = 0; k < pieces; k++)
        {
            float t0 = limit * k / pieces;
            float t1 = limit * (k + 1) / pieces;
            Vector3 a = from + seg * t0, b = from + seg * t1;
            list.Clear();
            if (roofs)
                env.Roofs(Vector3.Min(a, b) - Vector3.One, Vector3.Max(a, b) + Vector3.One, list);
            else
                env.Buildings(Vector3.Min(a, b) - Vector3.One, Vector3.Max(a, b) + Vector3.One, list);

            float best = float.PositiveInfinity;
            for (int i = 0; i + 2 < list.Count; i += 3)
                if (Crosses(from, seg, list[i], list[i + 1], list[i + 2], out float t) && t >= t0 && t <= t1 && t < best) best = t;

            if (best <= t1) return best;
        }

        return -1;
    }

    /// <summary>
    /// Whether the segment crosses the triangle (either side), at fraction <paramref name="t"/> of it (Möller–Trumbore).
    /// </summary>
    static bool Crosses(Vector3 from, Vector3 seg, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(seg, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return false;

        float inv = 1 / det;
        var s = from - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return false;

        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(seg, q) * inv;
        if (v < 0 || u + v > 1) return false;

        t = Vector3.Dot(e2, q) * inv;
        return t is >= 0 and <= 1;
    }
}
