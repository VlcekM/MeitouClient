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
            bool alongX = dx != 0;
            float border = dx < 0 ? mesh.BoundsMax.X : dx > 0 ? mesh.BoundsMin.X : dz < 0 ? mesh.BoundsMax.Y : mesh.BoundsMin.Y;
            for (int p = 0; p < mesh.PolygonCount; p++)
            {
                if (!mesh.Kept[p]) continue;
                var poly = mesh.Polygons[p];
                for (int k = 0; k < poly.Length; k++)
                {
                    if (mesh.Neighbours[p][k] >= 0 || HasLink(mesh, p, k)) continue;
                    var a = mesh.Vertices[poly[k]];
                    var b = mesh.Vertices[poly[(k + 1) % poly.Length]];
                    float da = alongX ? a.X : a.Z, db = alongX ? b.X : b.Z;
                    if (MathF.Abs(da - border) > 0.05f || MathF.Abs(db - border) > 0.05f) continue;
                    var mid = (a + b) * 0.5f;
                    if (mid.X < g.ZoneMin.X - 1 || mid.X > g.ZoneMax.X + 1 || mid.Z < g.ZoneMin.Y - 1 || mid.Z > g.ZoneMax.Y + 1) continue;
                    seeds.Add(mid);
                }
            }
        }
        return seeds;
    }

    static bool HasLink(ZoneNavMesh m, int polygon, int edge)
    {
        var l = m.LinksOf(polygon);
        for (int i = 0; i < l.Length; i += 2) if (l[i] == edge) return true;
        return false;
    }
}
