using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The loaded navmesh's faces as <see cref="SurfaceFace"/>s for the town refit's 3D snap (<see cref="IWalkability.SurfaceFaces"/>).
/// </summary>
static class NavSurfaceFaces
{
    /// <summary>
    /// The kept polygons of the loaded zones whose box meets the cube of half size <paramref name="reach"/> round <paramref name="centre"/>; null
    /// when the zone under the centre has no mesh loaded yet. A polygon's edge is open when no kept polygon lies behind it, it has no link and it
    /// is not on the zone's border (the next zone continues there). Door and water polygons are filtered (the original's hit filter skips them).
    /// </summary>
    public static IReadOnlyList<SurfaceFace>? Of(NavWorld world, Vector3 centre, float reach)
    {
        if (!world.Contains(WorldLayout.ZoneOf(centre.X, centre.Z))) return null;

        var faces = new List<SurfaceFace>();
        foreach (var zone in world.Zones)
        {
            var mesh = world.Find(zone)!;
            if (!ZoneReaches(mesh, centre, reach)) continue;

            for (int p = 0; p < mesh.PolygonCount; p++)
            {
                if (mesh.Kept[p] && Reaches(mesh, p, centre, reach)) faces.Add(FaceOf(mesh, p));
            }
        }

        return faces;
    }

    /// <summary>
    /// Whether the zone's box meets the square round the centre.
    /// </summary>
    static bool ZoneReaches(ZoneNavMesh mesh, Vector3 centre, float reach) =>
        mesh.BoundsMax.X >= centre.X - reach && mesh.BoundsMin.X <= centre.X + reach
        && mesh.BoundsMax.Y >= centre.Z - reach && mesh.BoundsMin.Y <= centre.Z + reach;

    /// <summary>
    /// Whether the polygon's box meets the cube round the centre.
    /// </summary>
    static bool Reaches(ZoneNavMesh mesh, int polygon, Vector3 centre, float reach)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (int i in mesh.Polygons[polygon])
        {
            lo = Vector3.Min(lo, mesh.Vertices[i]);
            hi = Vector3.Max(hi, mesh.Vertices[i]);
        }

        return hi.X >= centre.X - reach && lo.X <= centre.X + reach && hi.Z >= centre.Z - reach && lo.Z <= centre.Z + reach
            && hi.Y >= centre.Y - reach && lo.Y <= centre.Y + reach;
    }

    /// <summary>
    /// The polygon's corners, open edges and filter flag.
    /// </summary>
    static SurfaceFace FaceOf(ZoneNavMesh mesh, int polygon)
    {
        var poly = mesh.Polygons[polygon];
        var corners = new Vector3[poly.Length];
        var open = new bool[poly.Length];
        for (int k = 0; k < poly.Length; k++)
        {
            corners[k] = mesh.Vertices[poly[k]];
            open[k] = IsOpen(mesh, polygon, k);
        }

        byte area = mesh.Areas[polygon];
        bool filtered = area is NavArea.Door or NavArea.Water or NavArea.WaterWaist or NavArea.WaterDeep;
        return new SurfaceFace(corners, open, filtered, (x, z) => mesh.HeightAt(polygon, x, z));
    }

    /// <summary>
    /// Whether edge <paramref name="edge"/> of the polygon ends the walkable surface: no kept neighbour, no link, not on the zone's border.
    /// </summary>
    static bool IsOpen(ZoneNavMesh mesh, int polygon, int edge)
    {
        int behind = mesh.Neighbours[polygon][edge];
        if (behind >= 0 && mesh.Kept[behind]) return false;
        if (mesh.HasLink(polygon, edge)) return false;

        var poly = mesh.Polygons[polygon];
        var a = mesh.Vertices[poly[edge]];
        var b = mesh.Vertices[poly[(edge + 1) % poly.Length]];
        return !OnZoneBorder(mesh, a, b);
    }

    /// <summary>
    /// Whether both ends of the edge lie on one border line of the zone's box.
    /// </summary>
    static bool OnZoneBorder(ZoneNavMesh mesh, Vector3 a, Vector3 b) =>
        NavGeometry.OnLine(a.X, b.X, mesh.BoundsMin.X) || NavGeometry.OnLine(a.X, b.X, mesh.BoundsMax.X)
        || NavGeometry.OnLine(a.Z, b.Z, mesh.BoundsMin.Y) || NavGeometry.OnLine(a.Z, b.Z, mesh.BoundsMax.Y);
}
