using System.Numerics;

namespace Meitou.Navigation;

public static partial class ZoneNavMeshBuilder
{
    /// <summary>A convex counter-clockwise polygon with every edge moved outward by <paramref name="d"/> (corners mitred). A thin door leaf must paint a closed band of cells, or a path slips diagonally between them.</summary>
    static Vector2[] Inflate(Vector2[] p, float d)
    {
        int n = p.Length;
        var result = new Vector2[n];
        float sign = NavGeometry.SignedArea(p) >= 0 ? 1 : -1;
        for (int i = 0; i < n; i++)
        {
            var a = p[(i + n - 1) % n]; var b = p[i]; var c = p[(i + 1) % n];
            var e0 = Vector2.Normalize(b - a); var e1 = Vector2.Normalize(c - b);
            var n0 = new Vector2(e0.Y, -e0.X) * sign; var n1 = new Vector2(e1.Y, -e1.X) * sign;
            var m = n0 + n1;
            float k = 1 + Vector2.Dot(n0, n1);
            result[i] = k < 1e-3f ? b + n0 * d : b + m * (d / k);
        }
        return result;
    }

    /// <summary>Gives every door polygon the building whose door painter covers it, so a door can be opened and closed at run time (<see cref="NavDoors"/>).</summary>
    static void AssignDoors(ZoneNavMesh mesh, ZoneGeometry g, NavBuildSettings s)
    {
        var owners = g.Painters.Where(p => p.Owner is not null).ToList();
        if (owners.Count == 0) return;
        var ids = owners.Select(p => p.Owner!).Distinct().ToList();
        var doorOf = new int[mesh.PolygonCount];
        Array.Fill(doorOf, -1);
        float tolerance = s.CellSize * (s.DoorInflateCells + 1);
        bool any = false;
        for (int p = 0; p < mesh.PolygonCount; p++)
        {
            if (mesh.Areas[p] != NavArea.Door) continue;
            var centre = Vector3.Zero;
            foreach (int i in mesh.Polygons[p]) centre += mesh.Vertices[i];
            centre /= mesh.Polygons[p].Length;
            float best = float.MaxValue;
            foreach (var painter in owners)
            {
                if (centre.Y < painter.YMin - 5 || centre.Y > painter.YMax + 5) continue;
                float d = NavGeometry.DistanceOutsideConvex(painter.Polygon, new Vector2(centre.X, centre.Z));
                if (d > tolerance || d >= best) continue;
                best = d;
                doorOf[p] = ids.IndexOf(painter.Owner!);
                any = true;
            }
        }
        if (!any) return;
        mesh.DoorIds = [.. ids];
        mesh.DoorOf = doorOf;
    }
}
