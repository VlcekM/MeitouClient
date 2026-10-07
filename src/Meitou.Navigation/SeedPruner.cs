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
        // The mesh's own point index (every polygon is still kept here, and the index does not depend on that anyway).
        var ix = mesh.Index;

        var keep = new bool[n];
        var queue = new Queue<int>();
        int matched = 0;
        foreach (var seed in g.Seeds)
        {
            bool found = false;
            foreach (float slack in new[] { s.SeedHeightSlack, 3 * s.SeedHeightSlack })
            {
                float reach = s.SeedDistance;
                int cx0 = ix.Col(seed.X - reach - mesh.BoundsMin.X), cx1 = ix.Col(seed.X + reach - mesh.BoundsMin.X);
                int cz0 = ix.Row(seed.Z - reach - mesh.BoundsMin.Y), cz1 = ix.Row(seed.Z + reach - mesh.BoundsMin.Y);
                for (int z = cz0; z <= cz1; z++)
                    for (int x = cx0; x <= cx1; x++)
                        foreach (int p in ix.Cells[z * ix.Columns + x])
                        {
                            if (keep[p]) { found = true; continue; }
                            if (seed.X < ix.Min[p].X - reach || seed.X > ix.Max[p].X + reach || seed.Z < ix.Min[p].Y - reach || seed.Z > ix.Max[p].Y + reach) continue;
                            if (mesh.DistanceXZ(p, seed.X, seed.Z) > reach) continue;
                            float y = mesh.HeightAt(p, Math.Clamp(seed.X, ix.Min[p].X, ix.Max[p].X), Math.Clamp(seed.Z, ix.Min[p].Y, ix.Max[p].Y));
                            if (Math.Abs(y - seed.Y) > slack) continue;
                            keep[p] = true;
                            queue.Enqueue(p);
                            found = true;
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
            foreach (int q in mesh.NeighboursOf(p))
                if (!keep[q]) { keep[q] = true; queue.Enqueue(q); }
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
                foreach (int q in mesh.NeighboursOf(c)) if (id[q] < 0) { id[q] = next; stack.Push(q); }
            }
            next++;
        }
        return id;
    }
}
