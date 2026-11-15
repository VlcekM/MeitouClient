namespace Meitou.Simulation.Navigation;

/// <summary>
/// The codes of the building interiors (docs/game/pathfinding.md "Gate codes"): a building's own code is the code outside its door
/// (<c>FUN_140555fd0</c>: the code at its first door's outer marker), and what stands inside it takes the building's. Meitou reads an object's code
/// from its position, so an interior polygon the pass left unlabelled gets the code of the exterior ground behind its building's doors.
/// </summary>
internal static class GateInteriorCodes
{
    /// <summary>
    /// The unlabelled interior polygons' codes by polygon key: per connected interior (across zone borders too), the highest code of the exterior
    /// polygons its joins reach (engine choice where doors open onto different regions; through an exterior door polygon to the ground beside it);
    /// -1 when none.
    /// </summary>
    public static Dictionary<long, int> Of(NavWorld world, Dictionary<long, int> codes)
    {
        var inside = new Dictionary<long, int>();
        var seen = new HashSet<long>();
        var part = new List<NavRef>();
        var queue = new Queue<NavRef>();
        for (int s = 0; s < world.ZoneCount; s++)
        {
            var m = world.Mesh(s);
            for (int p = Math.Min(m.InteriorStart, m.PolygonCount); p < m.PolygonCount; p++)
            {
                var start = new NavRef(s, p);
                if (!m.Kept[p] || !seen.Add(start.Key)) continue;

                Collect(world, start, seen, part, queue);
                int code = Outside(world, codes, part);
                foreach (var r in part)
                    if (!codes.ContainsKey(r.Key)) inside[r.Key] = code;
            }
        }

        return inside;
    }

    /// <summary>
    /// The connected interior polygons from <paramref name="start"/> into <paramref name="part"/>.
    /// </summary>
    static void Collect(NavWorld world, NavRef start, HashSet<long> seen, List<NavRef> part, Queue<NavRef> queue)
    {
        part.Clear();
        queue.Enqueue(start);
        while (queue.TryDequeue(out var r))
        {
            part.Add(r);
            foreach (var q in Joins(world, r))
            {
                if (!IsInterior(world, q) || !seen.Add(q.Key)) continue;

                queue.Enqueue(q);
            }
        }
    }

    /// <summary>
    /// The highest code of the exterior ground the interior's joins lead to, -1 when they lead nowhere.
    /// </summary>
    static int Outside(NavWorld world, Dictionary<long, int> codes, List<NavRef> part)
    {
        int best = -1;
        foreach (var r in part)
        {
            foreach (var q in Joins(world, r))
            {
                if (IsInterior(world, q) || !world.Mesh(q.Zone).Kept[q.Polygon]) continue;

                best = Math.Max(best, GroundCode(world, codes, q));
            }
        }

        return best;
    }

    /// <summary>
    /// A polygon's neighbours: across its edges and in-mesh links, then the world links across zone borders.
    /// </summary>
    static IEnumerable<NavRef> Joins(NavWorld world, NavRef r)
    {
        foreach (int q in world.Mesh(r.Zone).NeighboursOf(r.Polygon)) yield return new NavRef(r.Zone, q);
        foreach (var link in world.LinksOf(r).ToArray()) yield return link.To;
    }

    /// <summary>
    /// Whether the polygon is a kept interior polygon.
    /// </summary>
    static bool IsInterior(NavWorld world, NavRef r)
    {
        var m = world.Mesh(r.Zone);
        return r.Polygon >= m.InteriorStart && m.Kept[r.Polygon];
    }

    /// <summary>
    /// The code of an exterior polygon, or for an exterior door polygon the highest of the ground its run of door polygons borders.
    /// </summary>
    static int GroundCode(NavWorld world, Dictionary<long, int> codes, NavRef r)
    {
        if (!GateFaces.IsDoor(world.Mesh(r.Zone), r.Polygon)) return GateFaces.CodeOf(world, codes, r);

        int best = -1;
        var seen = new HashSet<long> { r.Key };
        var queue = new Queue<NavRef>([r]);
        while (queue.TryDequeue(out var d))
        {
            foreach (var q in Joins(world, d))
            {
                var m = world.Mesh(q.Zone);
                if (q.Polygon >= m.InteriorStart || !m.Kept[q.Polygon] || !seen.Add(q.Key)) continue;

                if (GateFaces.IsDoor(m, q.Polygon)) queue.Enqueue(q);
                else best = Math.Max(best, GateFaces.CodeOf(world, codes, q));
            }
        }

        return best;
    }
}
