using System.Numerics;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Gives the zone mesh's door polygons their buildings.
/// </summary>
internal static class NavDoorAssignment
{
    /// <summary>
    /// Gives every door polygon the building whose door painter covers it, so a door can be opened and closed at run time (<see cref="NavDoors"/>).
    /// </summary>
    internal static void Assign(ZoneNavMesh mesh, ZoneGeometry g, NavBuildSettings s)
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
            foreach (int i in mesh.Polygons[p])
                centre += mesh.Vertices[i];

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
