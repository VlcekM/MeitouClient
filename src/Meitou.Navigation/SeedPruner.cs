using System.Numerics;

namespace Meitou.Navigation;

/// <summary>
/// The original generator keeps only the regions within 4 units of a seed point (regionPruningSettings, minRegionArea 1e8 makes every region "too small" otherwise),
/// and the largest region when none qualifies. This is that pass over the polygon graph: roofs, closed yards and cliff tops without a seed are dropped
/// (docs/game/pathfinding.md, "Region pruning by seeds").
/// </summary>
public static class SeedPruner
{
    /// <summary>Marks the polygons not connected to any seed as not kept. Returns how many seeds found a polygon.</summary>
    public static int Prune(ZoneNavMesh mesh, ZoneGeometry g, NavBuildSettings s)
    {
        int n = mesh.PolygonCount;
        if (n == 0) return 0;
        const float cell = 64;
        int gx = (int)MathF.Ceiling((mesh.BoundsMax.X - mesh.BoundsMin.X) / cell) + 1, gz = (int)MathF.Ceiling((mesh.BoundsMax.Y - mesh.BoundsMin.Y) / cell) + 1;
        var grid = new List<int>?[gx * gz];
        var boxMin = new Vector2[n];
        var boxMax = new Vector2[n];
        for (int p = 0; p < n; p++)
        {
            var lo = new Vector2(float.MaxValue);
            var hi = new Vector2(float.MinValue);
            foreach (int i in mesh.Polygons[p]) { var v = mesh.Vertices[i]; lo = Vector2.Min(lo, new(v.X, v.Z)); hi = Vector2.Max(hi, new(v.X, v.Z)); }
            boxMin[p] = lo; boxMax[p] = hi;
            int x0 = Cell(lo.X - mesh.BoundsMin.X), x1 = Cell(hi.X - mesh.BoundsMin.X), z0 = Cell(lo.Y - mesh.BoundsMin.Y), z1 = Cell(hi.Y - mesh.BoundsMin.Y);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++) (grid[z * gx + x] ??= []).Add(p);
        }
        int Cell(float v) => Math.Clamp((int)(v / cell), 0, int.MaxValue / 2);

        var keep = new bool[n];
        var queue = new Queue<int>();
        int matched = 0;
        foreach (var seed in g.Seeds)
        {
            bool found = false;
            foreach (float slack in new[] { s.SeedHeightSlack, 3 * s.SeedHeightSlack })
            {
                float reach = s.SeedDistance;
                int cx0 = Cell(seed.X - reach - mesh.BoundsMin.X), cx1 = Cell(seed.X + reach - mesh.BoundsMin.X);
                int cz0 = Cell(seed.Z - reach - mesh.BoundsMin.Y), cz1 = Cell(seed.Z + reach - mesh.BoundsMin.Y);
                for (int z = Math.Max(cz0, 0); z <= Math.Min(cz1, gz - 1); z++)
                    for (int x = Math.Max(cx0, 0); x <= Math.Min(cx1, gx - 1); x++)
                    {
                        if (grid[z * gx + x] is not { } list) continue;
                        foreach (int p in list)
                        {
                            if (keep[p]) { found = true; continue; }
                            if (seed.X < boxMin[p].X - reach || seed.X > boxMax[p].X + reach || seed.Z < boxMin[p].Y - reach || seed.Z > boxMax[p].Y + reach) continue;
                            if (DistanceXZ(mesh, p, seed.X, seed.Z) > reach) continue;
                            float y = mesh.HeightAt(p, Math.Clamp(seed.X, boxMin[p].X, boxMax[p].X), Math.Clamp(seed.Z, boxMin[p].Y, boxMax[p].Y));
                            if (Math.Abs(y - seed.Y) > slack) continue;
                            keep[p] = true;
                            queue.Enqueue(p);
                            found = true;
                        }
                    }
                if (found) break;
            }
            if (found) matched++;
        }

        if (matched == 0)
        {
            // "All regions are below the area threshold and too far from a seed point. Keeping the largest region."
            var component = Components(mesh);
            int best = component.GroupBy(c => c).OrderByDescending(c => c.Count()).First().Key;
            for (int p = 0; p < n; p++) if (component[p] == best) { keep[p] = true; }
            for (int p = 0; p < n; p++) mesh.Kept[p] = keep[p];
            return 0;
        }

        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            foreach (int q in mesh.Neighbours[p])
                if (q >= 0 && !keep[q]) { keep[q] = true; queue.Enqueue(q); }
            var tl = mesh.LinksOf(p);
            for (int i = 1; i < tl.Length; i += 2)
                if (!keep[tl[i]]) { keep[tl[i]] = true; queue.Enqueue(tl[i]); }
        }
        for (int p = 0; p < n; p++) mesh.Kept[p] = keep[p];
        return matched;
    }

    static int[] Components(ZoneNavMesh mesh)
    {
        var id = new int[mesh.PolygonCount];
        Array.Fill(id, -1);
        int next = 0;
        var stack = new Stack<int>();
        for (int p = 0; p < id.Length; p++)
        {
            if (id[p] >= 0) continue;
            id[p] = next;
            stack.Push(p);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                foreach (int q in mesh.Neighbours[c]) if (q >= 0 && id[q] < 0) { id[q] = next; stack.Push(q); }
                var tl = mesh.LinksOf(c);
                for (int i = 1; i < tl.Length; i += 2) if (id[tl[i]] < 0) { id[tl[i]] = next; stack.Push(tl[i]); }
            }
            next++;
        }
        return id;
    }

    /// <summary>Distance on the XZ plane from the point to the polygon (0 inside).</summary>
    public static float DistanceXZ(ZoneNavMesh mesh, int polygon, float x, float z)
    {
        if (mesh.Contains(polygon, x, z)) return 0;
        var poly = mesh.Polygons[polygon];
        float best = float.MaxValue;
        for (int k = 0; k < poly.Length; k++)
        {
            var a = mesh.Vertices[poly[k]];
            var b = mesh.Vertices[poly[(k + 1) % poly.Length]];
            best = Math.Min(best, SegmentDistance(x, z, a.X, a.Z, b.X, b.Z));
        }
        return best;
    }

    static float SegmentDistance(float px, float pz, float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        float len2 = dx * dx + dz * dz;
        float t = len2 < 1e-9f ? 0 : Math.Clamp(((px - ax) * dx + (pz - az) * dz) / len2, 0, 1);
        float cx = ax + t * dx - px, cz = az + t * dz - pz;
        return MathF.Sqrt(cx * cx + cz * cz);
    }
}
