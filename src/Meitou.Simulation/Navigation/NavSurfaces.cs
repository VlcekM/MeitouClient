using System.Numerics;

namespace Meitou.Navigation;

/// <summary>
/// The walkable collision triangles of a zone's buildings and foliage (exterior and interiors), kept beside the navmesh for heights. The mesh's
/// polygons are only planar between their corners, so on stairs and ramps a polygon spanning several steps lies several units above or below the
/// treads; a character's height is taken from the real surface the polygon stands for (<see cref="NavmeshWalkability.GroundHeight(float, float, float)"/>).
/// </summary>
public sealed class NavSurfaces
{
    /// <summary>Grid cell of the index (units).</summary>
    const float Cell = 32;

    /// <summary>Three corners per triangle, world coordinates.</summary>
    public Vector3[] Corners { get; }
    readonly Vector2 min;
    readonly int columns, rows;
    readonly int[][] cells;
    /// <summary>Each triangle's box, so most of a cell's triangles are passed over without the exact tests.</summary>
    readonly Box[] boxes;

    readonly record struct Box(float X0, float X1, float Y0, float Y1, float Z0, float Z1);

    public NavSurfaces(Vector3[] corners)
    {
        Corners = corners;
        if (corners.Length == 0) { cells = []; boxes = []; return; }
        var lo = new Vector2(float.MaxValue);
        var hi = new Vector2(float.MinValue);
        foreach (var c in corners) { lo = Vector2.Min(lo, new(c.X, c.Z)); hi = Vector2.Max(hi, new(c.X, c.Z)); }
        min = lo;
        columns = (int)((hi.X - lo.X) / Cell) + 1;
        rows = (int)((hi.Y - lo.Y) / Cell) + 1;
        var lists = new List<int>?[columns * rows];
        boxes = new Box[corners.Length / 3];
        for (int t = 0; t < corners.Length / 3; t++)
        {
            var a = corners[t * 3]; var b = corners[t * 3 + 1]; var c = corners[t * 3 + 2];
            boxes[t] = new Box(MathF.Min(a.X, MathF.Min(b.X, c.X)), MathF.Max(a.X, MathF.Max(b.X, c.X)), MathF.Min(a.Y, MathF.Min(b.Y, c.Y)),
                MathF.Max(a.Y, MathF.Max(b.Y, c.Y)), MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), MathF.Max(a.Z, MathF.Max(b.Z, c.Z)));
            var (x0, x1, z0, z1) = Range(t);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++) (lists[z * columns + x] ??= []).Add(t);
        }
        cells = [.. lists.Select(l => l is null ? [] : l.ToArray())];
    }

    public int TriangleCount => Corners.Length / 3;

    (int X0, int X1, int Z0, int Z1) Range(int t)
    {
        var a = Corners[t * 3]; var b = Corners[t * 3 + 1]; var c = Corners[t * 3 + 2];
        return (Col(MathF.Min(a.X, MathF.Min(b.X, c.X))), Col(MathF.Max(a.X, MathF.Max(b.X, c.X))), Row(MathF.Min(a.Z, MathF.Min(b.Z, c.Z))), Row(MathF.Max(a.Z, MathF.Max(b.Z, c.Z))));
    }

    int Col(float x) => Math.Clamp((int)((x - min.X) / Cell), 0, columns - 1);
    int Row(float z) => Math.Clamp((int)((z - min.Y) / Cell), 0, rows - 1);

    /// <summary>
    /// How far (units) beside a surface's edge its edge height still counts (engine choice: two Recast cells). The mesh reaches a cell or two past the
    /// edge of a raised floor (Observed: the landing at the Hub bar's door), where nothing but the terrain far below is under it.
    /// </summary>
    public const float EdgeReach = 4;

    /// <summary>
    /// The height of the surface at (x, z) nearest to <paramref name="y"/>, when one is within <paramref name="range"/>. A surface beside the point within
    /// <see cref="EdgeReach"/> counts with its edge height, its distance added to the difference (a steep side under the point loses to the top beside it).
    /// </summary>
    public bool TryNear(float x, float z, float y, float range, out float height) => TryNear(x, z, y, range, out height, out _);

    /// <inheritdoc cref="TryNear(float, float, float, float, out float)"/>
    /// <param name="beside">The height is a surface's edge beside the point, none being over it.</param>
    public bool TryNear(float x, float z, float y, float range, out float height, out bool beside)
    {
        height = y;
        beside = false;
        if (cells.Length == 0) return false;
        float best = range, bestEdge = range, edgeHeight = y;
        bool found = false, foundEdge = false;
        int x0 = Math.Max(0, (int)MathF.Floor((x - EdgeReach - min.X) / Cell)), x1 = Math.Min(columns - 1, (int)MathF.Floor((x + EdgeReach - min.X) / Cell));
        int z0 = Math.Max(0, (int)MathF.Floor((z - EdgeReach - min.Y) / Cell)), z1 = Math.Min(rows - 1, (int)MathF.Floor((z + EdgeReach - min.Y) / Cell));
        for (int cz = z0; cz <= z1; cz++)
            for (int cx = x0; cx <= x1; cx++)
                foreach (int t in cells[cz * columns + cx])
                {
                    // Neither over the triangle nor within EdgeReach of it, or its heights all farther from y than what would still count.
                    ref readonly var box = ref boxes[t];
                    float reachY = MathF.Max(best, bestEdge);
                    if (x < box.X0 - EdgeReach || x > box.X1 + EdgeReach || z < box.Z0 - EdgeReach || z > box.Z1 + EdgeReach
                        || box.Y0 - y > reachY || y - box.Y1 > reachY) continue;
                    var a = Corners[t * 3]; var b = Corners[t * 3 + 1]; var c = Corners[t * 3 + 2];
                    if (NavGeometry.Barycentric(x, z, a, b, c, out float l1, out float l2, out float l3) && l1 >= -1e-4f && l2 >= -1e-4f && l3 >= -1e-4f)
                    {
                        float h = l1 * a.Y + l2 * b.Y + l3 * c.Y;
                        float d = MathF.Abs(h - y);
                        if (d > best) continue;
                        best = d;
                        height = h;
                        found = true;
                    }
                    else if (EdgeHeight(a, b, c, x, z, out float reach) is { } e && MathF.Abs(e - y) + reach <= bestEdge)
                    {
                        bestEdge = MathF.Abs(e - y) + reach;
                        edgeHeight = e;
                        foundEdge = true;
                    }
                }
        if (foundEdge && (!found || bestEdge < best)) (height, beside) = (edgeHeight, true);
        return found || foundEdge;
    }

    /// <summary>The height of the triangle's edge point nearest to (x, z) seen from above, when it is within <see cref="EdgeReach"/>.</summary>
    static float? EdgeHeight(Vector3 a, Vector3 b, Vector3 c, float x, float z, out float reach)
    {
        float bestD = EdgeReach * EdgeReach;
        float? h = null;
        reach = EdgeReach;
        for (int k = 0; k < 3; k++)
        {
            var (p, q) = k switch { 0 => (a, b), 1 => (b, c), _ => (c, a) };
            float dx = q.X - p.X, dz = q.Z - p.Z, len2 = dx * dx + dz * dz;
            float t = len2 < 1e-9f ? 0 : Math.Clamp(((x - p.X) * dx + (z - p.Z) * dz) / len2, 0, 1);
            float ex = p.X + t * dx - x, ez = p.Z + t * dz - z, d = ex * ex + ez * ez;
            if (d > bestD) continue;
            bestD = d;
            reach = MathF.Sqrt(d);
            h = p.Y + t * (q.Y - p.Y);
        }
        return h;
    }

    /// <summary>
    /// The walkable non-terrain triangles of gathered geometry (buildings and foliage) whose box touches the zone box grown by <paramref name="margin"/>, without their parts
    /// inside a solid (<see cref="ZoneGeometry.Solids"/>, the builder's rule): a stair box's top face goes on inside the foundation it leans on, and a walker's height
    /// followed it down under the floor (docs/game/pathfinding.md, "Solids").
    /// </summary>
    internal static Vector3[] From(ZoneGeometry g, Vector2 zoneMin, Vector2 zoneMax, float margin = 8)
    {
        var list = new List<Vector3>();
        var pieces = new List<List<Vector3>>();
        var next = new List<List<Vector3>>();
        var v = g.Vertices;
        for (int t = g.BuildingFirstTriangle; t < g.TriangleCount; t++)
        {
            if (g.Areas[t] != NavArea.Ground) continue;
            int ia = g.Indices[t * 3] * 3, ib = g.Indices[t * 3 + 1] * 3, ic = g.Indices[t * 3 + 2] * 3;
            var a = new Vector3(v[ia], v[ia + 1], v[ia + 2]); var b = new Vector3(v[ib], v[ib + 1], v[ib + 2]); var c = new Vector3(v[ic], v[ic + 1], v[ic + 2]);
            if (MathF.Max(a.X, MathF.Max(b.X, c.X)) < zoneMin.X - margin || MathF.Min(a.X, MathF.Min(b.X, c.X)) > zoneMax.X + margin
                || MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) < zoneMin.Y - margin || MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) > zoneMax.Y + margin) continue;
            pieces.Clear();
            pieces.Add([a, b, c]);
            float x0 = MathF.Min(a.X, MathF.Min(b.X, c.X)), x1 = MathF.Max(a.X, MathF.Max(b.X, c.X));
            float z0 = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), z1 = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
            foreach (var solid in g.Solids)
            {
                if (x1 < solid.Min.X || x0 > solid.Max.X || z1 < solid.Min.Y || z0 > solid.Max.Y) continue;
                next.Clear();
                foreach (var piece in pieces) Outside(piece, solid, next);
                (pieces, next) = (next, pieces);
                if (pieces.Count == 0) break;
            }
            foreach (var piece in pieces)
                for (int k = 1; k + 1 < piece.Count; k++) { list.Add(piece[0]); list.Add(piece[k]); list.Add(piece[k + 1]); }
        }
        return [.. list];
    }

    /// <summary>
    /// Adds the parts of a convex polygon outside the solid to <paramref name="output"/> (convex pieces). Inside means as in the builder: below every face, by more than
    /// <see cref="ZoneNavMeshBuilder.SolidTopInset"/> under the upward ones, so the solid's own top stays.
    /// </summary>
    static void Outside(List<Vector3> polygon, NavSolid solid, List<List<Vector3>> output)
    {
        // Wholly outside one face: kept whole (most polygons near a solid, its own top among them).
        foreach (var plane in solid.Planes)
        {
            var n = new Vector3(plane.X, plane.Y, plane.Z);
            float d = plane.W + (plane.Y > 0 ? ZoneNavMeshBuilder.SolidTopInset : 0);
            bool all = true;
            foreach (var p in polygon) if (Vector3.Dot(n, p) + d < 0) { all = false; break; }
            if (all) { output.Add(polygon); return; }
        }
        var rest = polygon;
        foreach (var plane in solid.Planes)
        {
            var n = new Vector3(plane.X, plane.Y, plane.Z);
            float d = plane.W + (plane.Y > 0 ? ZoneNavMeshBuilder.SolidTopInset : 0);
            List<Vector3> outside = [], inside = [];
            for (int i = 0; i < rest.Count; i++)
            {
                var p = rest[i];
                var q = rest[(i + 1) % rest.Count];
                float dp = Vector3.Dot(n, p) + d, dq = Vector3.Dot(n, q) + d;
                (dp >= 0 ? outside : inside).Add(p);
                if (dp >= 0 != dq >= 0)
                {
                    var cut = Vector3.Lerp(p, q, dp / (dp - dq));
                    outside.Add(cut);
                    inside.Add(cut);
                }
            }
            if (outside.Count >= 3) output.Add(outside);
            if (inside.Count < 3) return;
            rest = inside;
        }
    }
}
