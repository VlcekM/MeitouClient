using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Where ground of the loaded navmesh goes on into a zone that is not loaded: an unlinked edge of one of its polygons on a zone's border line whose
/// neighbour across it is not loaded. A goal the loaded zones do not reach is proven unreachable only when the walker's ground or the goal's is
/// closed inside them; ground open to such a zone may meet the other through it (<see cref="NavmeshWalkability.FindPath(System.Numerics.Vector3,
/// System.Numerics.Vector3, NavAgent)"/>).
/// </summary>
public static class NavFrontier
{
    /// <summary>
    /// The four sides of a zone, as (dx, dz) steps to the neighbour across each.
    /// </summary>
    static readonly (int Dx, int Dz)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>
    /// The zones not loaded that the polygons of <paramref name="ground"/> (<see cref="NavRef.Key"/>s, ascending) border.
    /// </summary>
    public static HashSet<ZoneCoordinate> OfGround(NavWorld w, long[] ground)
    {
        var open = new HashSet<ZoneCoordinate>();
        foreach (var zone in w.Zones)
        {
            if (!w.TryGetSlot(zone, out int slot)) continue;

            long slotKey = (long)slot << 32;
            AddOpenSides(w, zone, w.Mesh(slot), p => Array.BinarySearch(ground, slotKey | (uint)p) >= 0, open);
        }

        return open;
    }

    /// <summary>
    /// The zones not loaded that the goal polygon's ground within its own zone (the kept polygons its edges and links lead to) borders.
    /// </summary>
    public static HashSet<ZoneCoordinate> OfGoal(NavWorld w, NavRef goal)
    {
        var open = new HashSet<ZoneCoordinate>();
        foreach (var zone in w.Zones)
        {
            if (!w.TryGetSlot(zone, out int slot) || slot != goal.Zone) continue;

            var mesh = w.Mesh(slot);
            var reached = Flood(mesh, goal.Polygon);
            AddOpenSides(w, zone, mesh, reached.Contains, open);
        }

        return open;
    }

    /// <summary>
    /// The kept polygons of <paramref name="mesh"/> that <paramref name="start"/> reaches through its edges and links.
    /// </summary>
    static HashSet<int> Flood(ZoneNavMesh mesh, int start)
    {
        var reached = new HashSet<int> { start };
        var queue = new Queue<int>([start]);
        while (queue.TryDequeue(out int p))
            foreach (int q in mesh.NeighboursOf(p))
                if (mesh.Kept[q] && reached.Add(q)) queue.Enqueue(q);
        return reached;
    }

    /// <summary>
    /// Adds to <paramref name="open"/> each neighbour of <paramref name="zone"/> that is not loaded and that a polygon <paramref name="inGround"/>
    /// holds has an unlinked edge on the border line to.
    /// </summary>
    static void AddOpenSides(NavWorld w, ZoneCoordinate zone, ZoneNavMesh mesh, Func<int, bool> inGround, HashSet<ZoneCoordinate> open)
    {
        foreach (var (dx, dz) in Sides)
        {
            var next = new ZoneCoordinate(zone.X + dx, zone.Y + dz);
            if (w.Contains(next) || open.Contains(next)) continue;

            foreach (var (polygon, _) in mesh.BorderEdges(dx, dz, skipLinked: true))
            {
                if (!inGround(polygon)) continue;

                open.Add(next);
                break;
            }
        }
    }
}
