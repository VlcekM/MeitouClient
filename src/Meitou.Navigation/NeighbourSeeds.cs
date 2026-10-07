using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>
/// The midpoints of the open border edges of the already built neighbouring zones, as seeds for a zone about to be built (docs/game/pathfinding.md,
/// "Seeds": FUN_1403c9820, the four neighbours), so that regions reaching across a zone border stay even where this zone has no seed of its own in them.
/// As in the original the result depends on which neighbours were loaded first, and a cached mesh keeps what it was built with.
/// </summary>
public static class NeighbourSeeds
{
    public static List<Vector3> Collect(NavWorld world, ZoneCoordinate zone, ZoneGeometry g)
    {
        var seeds = new List<Vector3>();
        foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            if (world.Find(new ZoneCoordinate(zone.X + dx, zone.Y + dz)) is not { } mesh) continue;
            // The shared border, on the neighbour's side: its bounds edge facing this zone.
            foreach (var (p, k) in mesh.BorderEdges(-dx, -dz, skipLinked: true))
            {
                var poly = mesh.Polygons[p];
                var mid = (mesh.Vertices[poly[k]] + mesh.Vertices[poly[(k + 1) % poly.Length]]) * 0.5f;
                if (mid.X < g.ZoneMin.X - 1 || mid.X > g.ZoneMax.X + 1 || mid.Z < g.ZoneMin.Y - 1 || mid.Z > g.ZoneMax.Y + 1) continue;
                seeds.Add(mid);
            }
        }
        return seeds;
    }
}
