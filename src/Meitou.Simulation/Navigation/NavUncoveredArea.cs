using DotRecast.Recast;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// How much of a tile's contours its polygons leave uncovered. Recast's polygon step ear-clips each contour in plan and, when it finds no ear
/// left, keeps the triangles it made so far and only warns ("Bad triangulation"): the rest of the region gets no polygon (docs/game/pathfinding.md
/// "Search cost", uncovered contours; #1393).
/// </summary>
internal static class NavUncoveredArea
{
    /// <summary>
    /// Twice the area in plan, in square cells, that the contours of <paramref name="contours"/> enclose and the polygons of
    /// <paramref name="mesh"/> do not cover.
    /// </summary>
    public static long Doubled(RcContourSet contours, RcPolyMesh mesh) => ContourArea(contours) - PolygonArea(mesh);

    /// <summary>
    /// Twice the area in plan of the outlines (points x, y, z, flags each, in cells; a merged hole has no points left).
    /// </summary>
    static long ContourArea(RcContourSet contours)
    {
        long sum = 0;
        foreach (var contour in contours.conts)
        {
            for (int i = 0, j = contour.nverts - 1; i < contour.nverts; j = i++)
                sum += Cross(contour.verts, i * 4, j * 4);
        }

        return sum;
    }

    /// <summary>
    /// Twice the area in plan of the polygons (vertex indices into points x, y, z each, in cells).
    /// </summary>
    static long PolygonArea(RcPolyMesh mesh)
    {
        long sum = 0;
        int nvp = mesh.nvp;
        for (int p = 0; p < mesh.npolys; p++)
        {
            int first = p * nvp * 2;
            int count = 0;
            while (count < nvp && mesh.polys[first + count] != RcRecast.RC_MESH_NULL_IDX) count++;

            for (int i = 0, j = count - 1; i < count; j = i++)
                sum += Cross(mesh.verts, mesh.polys[first + i] * 3, mesh.polys[first + j] * 3);
        }

        return sum;
    }

    /// <summary>
    /// The shoelace term of the edge from the point at <paramref name="b"/> to the point at <paramref name="a"/> (offsets of their x).
    /// </summary>
    static long Cross(int[] points, int a, int b) => (long)points[a] * points[b + 2] - (long)points[b] * points[a + 2];
}
