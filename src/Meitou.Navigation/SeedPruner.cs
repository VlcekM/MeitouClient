using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>
/// The original generator keeps only the regions within 4 units of a seed point (regionPruningSettings, minRegionArea 1e8 makes every region "too small" otherwise),
/// and the largest region when none qualifies. This is that pass over the polygon graph: roofs, closed yards and cliff tops without a seed are dropped
/// (docs/game/pathfinding.md, Generator "Seeds" and `regionPruningSettings`).
/// </summary>
internal static class SeedPruner
{
    /// <summary>
    /// How far above a destroyed interior's lowest polygon a region may start and still be its ground floor (engine choice: well under a storey, 60+ in the
    /// Hub's houses; docs/game/pathfinding.md "Destroyed interiors").
    /// </summary>
    public const float DestroyedFloorBand = 40;

    /// <summary>Marks the polygons not connected to any seed as not kept. Returns how many seeds found a polygon.</summary>
    public static int Prune(ZoneNavMesh mesh, ZoneGeometry g, NavBuildSettings s)
    {
        int n = mesh.PolygonCount;
        if (n == 0) return 0;
        var keep = new bool[n];
        var queue = new Queue<int>();
        int matched = Match(mesh, g.Seeds, s, keep, queue);
        if (matched == 0 && g.NodeSeeds.Count > 0) matched = Match(mesh, g.NodeSeeds, s, keep, queue);
        // Regions reaching the zone's border at ground level are kept when the zone is published, toward the sides whose neighbour is not loaded
        // (ExtendBorder), and from the neighbours' kept polygons when it is (ZoneKeptClosure): the cached pruning is the zone's own.

        if (g.InteriorDestroyed)
        {
            // A destroyed building's interior: the regions its seed reaches (the original's ray onto its floor, ZoneGeometryGatherer.OriginRaySeed), and its
            // ground floor, every region starting within DestroyedFloorBand of the lowest polygon (engine choice: our mesh can split a ruin's floor that the
            // original keeps whole, the Hub L-House 4645-). Its largest region can be an upper floor or wall tops under a roof whose intact collision stays.
            if (matched > 0) Spread(mesh, keep, queue);
            var component = Components(mesh);
            var bottom = new Dictionary<int, float>();
            for (int p = 0; p < n; p++)
            {
                float y = mesh.Polygons[p].Min(v => mesh.Vertices[v].Y);
                bottom[component[p]] = bottom.TryGetValue(component[p], out float b) ? MathF.Min(b, y) : y;
            }
            float floor = bottom.Values.Min();
            for (int p = 0; p < n; p++) mesh.Kept[p] = keep[p] || bottom[component[p]] <= floor + DestroyedFloorBand;
            return matched;
        }
        if (matched == 0 && g.ClippedAtBorder)
        {
            // Part of an interior whose doors are across a zone border: the neighbour's kept border edges reach it (ZoneKeptClosure).
            for (int p = 0; p < n; p++) mesh.Kept[p] = false;
            return 0;
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

        Spread(mesh, keep, queue);
        for (int p = 0; p < n; p++) mesh.Kept[p] = keep[p];
        return matched;
    }

    /// <summary>Also marks in <paramref name="keep"/> (a kept set over <paramref name="mesh"/>'s polygons) the regions that <paramref name="seeds"/> reach (no fallback); true when a polygon was added.</summary>
    public static bool Extend(ZoneNavMesh mesh, bool[] keep, IReadOnlyList<Vector3> seeds, NavBuildSettings s)
    {
        if (mesh.PolygonCount == 0 || seeds.Count == 0) return false;
        var queue = new Queue<int>();
        Match(mesh, seeds, s, keep, queue);
        if (queue.Count == 0) return false;
        Spread(mesh, keep, queue);
        return true;
    }

    /// <summary>
    /// Marks in <paramref name="keep"/> the regions with an open edge on the zone's border line toward (<paramref name="dx"/>, <paramref name="dz"/>) at ground level
    /// (the terrain, or the water's height over it, within <see cref="BorderGroundSlack"/>): the stand-in for the neighbour's seeds while that zone is not loaded.
    /// The original seeds each zone from its built neighbours' open border edges and ships every tile built, so a region that goes on into the next zone is
    /// kept wherever its seed is; pruned by its own seeds alone, a zone with few of them (Fog Islands 14.26: 1) kept 3 % of the original's area. Roofs and
    /// walkways crossing the border stay pruned, and so, once the neighbour is loaded and reaches nothing there, does ground enclosed by a building that
    /// straddles the line (World's End's Ancient Citadel, issue #144; docs/game/pathfinding.md, "Zone borders"). True when a polygon was added.
    /// </summary>
    public static bool ExtendBorder(ZoneNavMesh mesh, bool[] keep, int dx, int dz)
    {
        if (mesh.Terrain is not { } terrain) return false;
        var queue = new Queue<int>();
        foreach (var (p, k) in mesh.BorderEdges(dx, dz, skipLinked: false, all: true))
        {
            if (keep[p]) continue;
            var poly = mesh.Polygons[p];
            var mid = (mesh.Vertices[poly[k]] + mesh.Vertices[poly[(k + 1) % poly.Length]]) / 2;
            if (!terrain.TryHeightAt(mid.X, mid.Z, out float ground)) continue;
            if (MathF.Abs(mid.Y - MathF.Max(ground, WorldWater.Height)) > BorderGroundSlack) continue;
            keep[p] = true;
            queue.Enqueue(p);
        }
        if (queue.Count == 0) return false;
        Spread(mesh, keep, queue);
        return true;
    }

    /// <summary>Marks the polygons each seed claims (queued when newly kept). Returns how many seeds found a polygon, kept already or not.</summary>
    static int Match(ZoneNavMesh mesh, IReadOnlyList<Vector3> seeds, NavBuildSettings s, bool[] keep, Queue<int> queue)
    {
        // The mesh's own point index (independent of which polygons are kept).
        var ix = mesh.Index;
        int matched = 0;
        foreach (var seed in seeds)
        {
            bool found = false;
            // Engine choice: a seed that finds nothing within the slack is retried with three times the slack (a seed's Y is where it was authored).
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
        return matched;
    }

    /// <summary>How far (units) a border edge's middle may be from the ground (the terrain, or the water's height over it) to count as ground level (engine choice).</summary>
    public const float BorderGroundSlack = 30;

    static void Spread(ZoneNavMesh mesh, bool[] keep, Queue<int> queue)
    {
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            foreach (int q in mesh.NeighboursOf(p))
                if (!keep[q]) { keep[q] = true; queue.Enqueue(q); }
        }
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
