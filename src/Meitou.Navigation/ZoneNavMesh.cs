using System.Numerics;

namespace Meitou.Navigation;

/// <summary>
/// A built navigation mesh of one zone: convex polygons (up to six vertices, counter-clockwise seen from above) with their areas and neighbours.
/// Kept in world coordinates. <see cref="Neighbours"/> is parallel to the polygon's edges: edge <c>k</c> runs from vertex <c>k</c> to <c>k + 1</c>
/// (wrapping) and leads to polygon <c>Neighbours[k]</c>, or −1 where the mesh ends.
/// </summary>
public sealed class ZoneNavMesh
{
    public const int MaxVerticesPerPolygon = 6;

    public required int ZoneX { get; init; }
    public required int ZoneZ { get; init; }
    public required Vector3[] Vertices { get; init; }
    /// <summary>Per polygon: vertex indices.</summary>
    public required int[][] Polygons { get; init; }
    /// <summary>Per polygon: the neighbour behind each edge, −1 for none.</summary>
    public required int[][] Neighbours { get; init; }
    public required byte[] Areas { get; init; }
    /// <summary>
    /// Per polygon: links across edges that face several polygons (the borders between tiles, where the two sides' vertices do not match), as pairs
    /// edge, polygon; null for most polygons. Such edges have <c>Neighbours</c> −1.
    /// </summary>
    public int[]?[]? Links { get; init; }
    /// <summary>Per polygon: false when the seed pass removed it.</summary>
    public required bool[] Kept { get; init; }
    public required Vector2 BoundsMin { get; init; }
    public required Vector2 BoundsMax { get; init; }
    /// <summary>Instance ids of the buildings whose doors are in this mesh (<see cref="NavDoors"/>); null when there are none.</summary>
    public string[]? DoorIds { get; set; }
    /// <summary>Per polygon: the index into <see cref="DoorIds"/> for door polygons, −1 otherwise; null when there are no doors.</summary>
    public int[]? DoorOf { get; set; }

    public int PolygonCount => Polygons.Length;

    /// <summary>The (edge, polygon) pairs of a polygon's <see cref="Links"/>, flattened.</summary>
    public ReadOnlySpan<int> LinksOf(int polygon) => Links is { } l && l[polygon] is { } a ? a : [];

    bool HasLink(int polygon, int edge)
    {
        var l = LinksOf(polygon);
        for (int i = 0; i < l.Length; i += 2) if (l[i] == edge) return true;
        return false;
    }

    bool[]? wallVertices;

    /// <summary>
    /// Whether a vertex lies on the edge of the walkable surface (an obstacle, a drop, the end of the mesh): the places a footprint has to keep clear of.
    /// Edges along the zone border do not count (the next zone continues there).
    /// </summary>
    public bool IsWallVertex(int vertex)
    {
        var w = wallVertices;
        if (w is null)
        {
            w = new bool[Vertices.Length];
            for (int p = 0; p < PolygonCount; p++)
            {
                var poly = Polygons[p];
                for (int k = 0; k < poly.Length; k++)
                {
                    if (Neighbours[p][k] >= 0 || HasLink(p, k)) continue;
                    var a = Vertices[poly[k]];
                    var b = Vertices[poly[(k + 1) % poly.Length]];
                    bool onBorder = (Math.Abs(a.X - BoundsMin.X) < 0.05f && Math.Abs(b.X - BoundsMin.X) < 0.05f) || (Math.Abs(a.X - BoundsMax.X) < 0.05f && Math.Abs(b.X - BoundsMax.X) < 0.05f)
                        || (Math.Abs(a.Z - BoundsMin.Y) < 0.05f && Math.Abs(b.Z - BoundsMin.Y) < 0.05f) || (Math.Abs(a.Z - BoundsMax.Y) < 0.05f && Math.Abs(b.Z - BoundsMax.Y) < 0.05f);
                    if (onBorder) continue;
                    w[poly[k]] = true;
                    w[poly[(k + 1) % poly.Length]] = true;
                }
            }
            wallVertices = w;
        }
        return w[vertex];
    }

    /// <summary>A copy without the pruned polygons (and the vertices only they used), neighbours renumbered; what the game keeps and caches.</summary>
    public ZoneNavMesh WithoutPruned()
    {
        var map = new int[PolygonCount];
        int count = 0;
        for (int p = 0; p < PolygonCount; p++) map[p] = Kept[p] ? count++ : -1;
        var vertexMap = new Dictionary<int, int>();
        var vertices = new List<Vector3>();
        var polygons = new int[count][];
        var neighbours = new int[count][];
        var areas = new byte[count];
        var doorOf = DoorOf is null ? null : new int[count];
        var links = new int[]?[count];
        for (int p = 0; p < PolygonCount; p++)
        {
            if (!Kept[p]) continue;
            int n = map[p];
            polygons[n] = Polygons[p].Select(v =>
            {
                if (!vertexMap.TryGetValue(v, out int i)) { i = vertices.Count; vertexMap[v] = i; vertices.Add(Vertices[v]); }
                return i;
            }).ToArray();
            neighbours[n] = Neighbours[p].Select(q => q >= 0 ? map[q] : -1).ToArray();
            areas[n] = Areas[p];
            if (doorOf is not null) doorOf[n] = DoorOf![p];
            var l = LinksOf(p);
            if (l.Length > 0)
            {
                var kept = new List<int>();
                for (int i = 0; i < l.Length; i += 2) if (map[l[i + 1]] >= 0) { kept.Add(l[i]); kept.Add(map[l[i + 1]]); }
                if (kept.Count > 0) links[n] = [.. kept];
            }
        }
        return new ZoneNavMesh
        {
            ZoneX = ZoneX, ZoneZ = ZoneZ, Vertices = [.. vertices], Polygons = polygons, Neighbours = neighbours, Areas = areas, Links = links,
            Kept = Enumerable.Repeat(true, count).ToArray(), BoundsMin = BoundsMin, BoundsMax = BoundsMax, DoorIds = DoorIds, DoorOf = doorOf,
        };
    }

    // ---- point queries ----

    readonly Lazy<PointIndex> index;

    public ZoneNavMesh() => index = new Lazy<PointIndex>(() => new PointIndex(this), LazyThreadSafetyMode.ExecutionAndPublication);

    sealed class PointIndex
    {
        public const float Cell = 64;
        public readonly int Columns, Rows;
        public readonly int[][] Cells;
        public readonly Vector2[] Min, Max;

        public PointIndex(ZoneNavMesh m)
        {
            Columns = (int)MathF.Ceiling((m.BoundsMax.X - m.BoundsMin.X) / Cell) + 1;
            Rows = (int)MathF.Ceiling((m.BoundsMax.Y - m.BoundsMin.Y) / Cell) + 1;
            var lists = new List<int>?[Columns * Rows];
            Min = new Vector2[m.PolygonCount];
            Max = new Vector2[m.PolygonCount];
            for (int p = 0; p < m.PolygonCount; p++)
            {
                var lo = new Vector2(float.MaxValue);
                var hi = new Vector2(float.MinValue);
                foreach (int i in m.Polygons[p]) { var v = m.Vertices[i]; lo = Vector2.Min(lo, new(v.X, v.Z)); hi = Vector2.Max(hi, new(v.X, v.Z)); }
                Min[p] = lo; Max[p] = hi;
                int x0 = Col(lo.X - m.BoundsMin.X), x1 = Col(hi.X - m.BoundsMin.X), z0 = Row(lo.Y - m.BoundsMin.Y), z1 = Row(hi.Y - m.BoundsMin.Y);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++) (lists[z * Columns + x] ??= []).Add(p);
            }
            Cells = lists.Select(l => l?.ToArray() ?? []).ToArray();
        }

        public int Col(float v) => Math.Clamp((int)(v / Cell), 0, Columns - 1);
        public int Row(float v) => Math.Clamp((int)(v / Cell), 0, Rows - 1);
    }

    /// <summary>Appends the kept polygons that contain (x, z) seen from above.</summary>
    public void PolygonsAt(float x, float z, List<int> result)
    {
        var ix = index.Value;
        if (x < BoundsMin.X - 1 || x > BoundsMax.X + 1 || z < BoundsMin.Y - 1 || z > BoundsMax.Y + 1) return;
        foreach (int p in ix.Cells[ix.Row(z - BoundsMin.Y) * ix.Columns + ix.Col(x - BoundsMin.X)])
            if (Kept[p] && x >= ix.Min[p].X - 1e-3f && x <= ix.Max[p].X + 1e-3f && z >= ix.Min[p].Y - 1e-3f && z <= ix.Max[p].Y + 1e-3f && Contains(p, x, z))
                result.Add(p);
    }

    /// <summary>The kept polygon nearest to (x, y, z) in XZ distance (ties and containing polygons by height), within <paramref name="maxDistance"/>; −1 when none.</summary>
    public int Nearest(float x, float y, float z, float maxDistance, out float distance)
    {
        var ix = index.Value;
        int best = -1;
        float bestScore = float.MaxValue;
        distance = float.MaxValue;
        int x0 = ix.Col(x - maxDistance - BoundsMin.X), x1 = ix.Col(x + maxDistance - BoundsMin.X);
        int z0 = ix.Row(z - maxDistance - BoundsMin.Y), z1 = ix.Row(z + maxDistance - BoundsMin.Y);
        for (int cz = z0; cz <= z1; cz++)
            for (int cx = x0; cx <= x1; cx++)
                foreach (int p in ix.Cells[cz * ix.Columns + cx])
                {
                    if (!Kept[p]) continue;
                    if (x < ix.Min[p].X - maxDistance || x > ix.Max[p].X + maxDistance || z < ix.Min[p].Y - maxDistance || z > ix.Max[p].Y + maxDistance) continue;
                    float d = SeedPruner.DistanceXZ(this, p, x, z);
                    if (d > maxDistance) continue;
                    float py = HeightAt(p, Math.Clamp(x, ix.Min[p].X, ix.Max[p].X), Math.Clamp(z, ix.Min[p].Y, ix.Max[p].Y));
                    // XZ distance first, then how far the surface is from the wanted height.
                    float score = d * 4 + Math.Abs(py - y);
                    if (score < bestScore) { bestScore = score; best = p; distance = d; }
                }
        return best;
    }

    public int KeptCount
    {
        get
        {
            int n = 0;
            foreach (var k in Kept) if (k) n++;
            return n;
        }
    }

    public Vector3 Centre(int polygon)
    {
        var sum = Vector3.Zero;
        var p = Polygons[polygon];
        foreach (var i in p) sum += Vertices[i];
        return sum / p.Length;
    }

    /// <summary>Height of the polygon's surface at (x, z) (its plane through the first three vertices), assuming the point lies inside.</summary>
    public float HeightAt(int polygon, float x, float z)
    {
        var p = Polygons[polygon];
        // The polygon is convex and nearly planar: interpolate over the fan triangle containing the point.
        var a = Vertices[p[0]];
        for (int k = 1; k + 1 < p.Length; k++)
        {
            var b = Vertices[p[k]];
            var c = Vertices[p[k + 1]];
            if (Barycentric(x, z, a, b, c, out float u, out float v, out float w) && u >= -1e-4f && v >= -1e-4f && w >= -1e-4f)
                return u * a.Y + v * b.Y + w * c.Y;
        }
        // Outside every fan triangle (a point just beyond an edge): the nearest vertex height.
        float best = float.MaxValue, y = a.Y;
        foreach (var i in p)
        {
            float d = (Vertices[i].X - x) * (Vertices[i].X - x) + (Vertices[i].Z - z) * (Vertices[i].Z - z);
            if (d < best) { best = d; y = Vertices[i].Y; }
        }
        return y;
    }

    public static bool Barycentric(float x, float z, Vector3 a, Vector3 b, Vector3 c, out float u, out float v, out float w)
    {
        float det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(det) < 1e-9f) { u = v = w = 0; return false; }
        u = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / det;
        v = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / det;
        w = 1 - u - v;
        return true;
    }

    /// <summary>Whether (x, z) lies inside the polygon seen from above.</summary>
    public bool Contains(int polygon, float x, float z)
    {
        var p = Polygons[polygon];
        for (int k = 0; k < p.Length; k++)
        {
            var a = Vertices[p[k]];
            var b = Vertices[p[(k + 1) % p.Length]];
            if ((b.X - a.X) * (z - a.Z) - (b.Z - a.Z) * (x - a.X) < -1e-4f) return false;
        }
        return true;
    }
}
