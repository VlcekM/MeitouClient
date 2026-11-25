using DotRecast.Recast;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// How much of a tile's contours its polygons leave uncovered. Recast's polygon step ear-clips each contour in plan and, when it finds no ear
/// left, keeps the triangles it made so far and only warns ("Bad triangulation"): the rest of the region gets no polygon (#1393). An outline that
/// touches itself can give an inverted triangle, which the merge folds into a polygon that is not convex; the mesh's point test takes polygons as
/// convex, so such a polygon leaves most of its area off the mesh (#1430). Both count (docs/game/pathfinding.md "Search cost", uncovered contours).
/// </summary>
internal static class NavUncoveredArea
{
    /// <summary>
    /// Twice the area in plan, in square cells, that the contours of <paramref name="contours"/> enclose and the convex polygons of
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
    /// Twice the area in plan of the convex polygons (vertex indices into points x, y, z each, in cells).
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

            if (!Convex(mesh, first, count)) continue;

            for (int i = 0, j = count - 1; i < count; j = i++)
                sum += Cross(mesh.verts, mesh.polys[first + i] * 3, mesh.polys[first + j] * 3);
        }

        return sum;
    }

    /// <summary>
    /// Whether the polygon whose <paramref name="count"/> vertex indices start at <paramref name="first"/> turns the contours' way (the way that
    /// makes <see cref="Cross"/>'s sum positive) or runs straight at every corner.
    /// </summary>
    static bool Convex(RcPolyMesh mesh, int first, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int a = mesh.polys[first + i] * 3;
            int b = mesh.polys[first + (i + 1) % count] * 3;
            int c = mesh.polys[first + (i + 2) % count] * 3;
            long turn = (long)(mesh.verts[b] - mesh.verts[a]) * (mesh.verts[c + 2] - mesh.verts[b + 2])
                - (long)(mesh.verts[b + 2] - mesh.verts[a + 2]) * (mesh.verts[c] - mesh.verts[b]);
            if (turn > 0) return false;
        }

        return true;
    }

    /// <summary>
    /// The shoelace term of the edge from the point at <paramref name="b"/> to the point at <paramref name="a"/> (offsets of their x).
    /// </summary>
    static long Cross(int[] points, int a, int b) => (long)points[a] * points[b + 2] - (long)points[b] * points[a + 2];
}
