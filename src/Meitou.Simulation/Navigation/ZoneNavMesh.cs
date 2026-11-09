using System.Numerics;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// A built navigation mesh of one zone: convex polygons (up to six vertices, counter-clockwise seen from above) with their areas and neighbours. Kept
/// in world coordinates. <see cref="Neighbours"/> is parallel to the polygon's edges: edge <c>k</c> runs from vertex <c>k</c> to <c>k + 1</c>
/// (wrapping) and leads to polygon <c>Neighbours[k]</c>, or −1 where the mesh ends.
/// </summary>
public sealed class ZoneNavMesh
{
    /// <summary>
    /// The most vertices a polygon has.
    /// </summary>
    public const int MaxVerticesPerPolygon = 6;

    /// <summary>
    /// The zone's X in the zone grid.
    /// </summary>
    public required int ZoneX { get; init; }
    /// <summary>
    /// The zone's Z in the zone grid.
    /// </summary>
    public required int ZoneZ { get; init; }
    /// <summary>
    /// The vertices, world coordinates.
    /// </summary>
    public required Vector3[] Vertices { get; init; }
    /// <summary>
    /// Per polygon: vertex indices.
    /// </summary>
    public required int[][] Polygons { get; init; }
    /// <summary>
    /// Per polygon: the neighbour behind each edge, −1 for none.
    /// </summary>
    public required int[][] Neighbours { get; init; }
    /// <summary>
    /// Per polygon: its area (<see cref="NavArea"/>).
    /// </summary>
    public required byte[] Areas { get; init; }
    /// <summary>
    /// Per polygon: links across edges that face several polygons (the borders between tiles, where the two sides' vertices do not match), as pairs
    /// edge, polygon; null for most polygons. Such edges have <c>Neighbours</c> −1.
    /// </summary>
    public int[]?[]? Links { get; init; }
    /// <summary>
    /// Per polygon: false when the seed pass removed it.
    /// </summary>
    public required bool[] Kept { get; init; }
    /// <summary>
    /// The smallest world X and Z of the zone's box.
    /// </summary>
    public required Vector2 BoundsMin { get; init; }
    /// <summary>
    /// The largest world X and Z of the zone's box.
    /// </summary>
    public required Vector2 BoundsMax { get; init; }
    /// <summary>
    /// Instance ids of the buildings whose doors are in this mesh (<see cref="NavDoors"/>); null when there are none.
    /// </summary>
    public string[]? DoorIds { get; internal set; }
    /// <summary>
    /// Per polygon: the index into <see cref="DoorIds"/> for door polygons, −1 otherwise; null when there are no doors.
    /// </summary>
    public int[]? DoorOf { get; internal set; }
    /// <summary>
    /// The walkable collision triangles the polygons stand for, for exact heights (<see cref="NavSurfaces"/>); null when none were kept.
    /// </summary>
    public NavSurfaces? Surfaces { get; internal set; }
    /// <summary>
    /// Per polygon: the heights of its own surface on a grid (<see cref="HeightGrid"/>), null for one that lies on its plane; the array is null when
    /// none has any (docs/game/pathfinding.md, "Heights").
    /// </summary>
    public HeightGrid?[]? Heights { get; internal set; }
    /// <summary>
    /// The first polygon of the building interiors appended to the zone's exterior (<see cref="NavInteriors.Combine"/>), <see cref="int.MaxValue"/>
    /// when there are none: the exterior's polygons come first and their neighbours are exterior polygons. The seed flood tests portal widths on
    /// exterior edges only (<see cref="SeedPruner.MinPortalWidth"/>).
    /// </summary>
    public int InteriorStart { get; init; } = int.MaxValue;
    /// <summary>
    /// The zone's heightmap samples (exact terrain heights; the simulation's own grid is exact only near where the game started). Read when the zone
    /// loads, not cached.
    /// </summary>
    public Meitou.Data.World.HeightWindow? Terrain { get; internal set; }

    /// <summary>
    /// The number of polygons, kept and pruned.
    /// </summary>
    public int PolygonCount => Polygons.Length;

    /// <summary>
    /// The (edge, polygon) pairs of a polygon's <see cref="Links"/>, flattened.
    /// </summary>
    public ReadOnlySpan<int> LinksOf(int polygon) => Links is { } l && l[polygon] is { } a ? a : [];

    /// <summary>
    /// Whether the edge of a polygon is one of its <see cref="Links"/>.
    /// </summary>
    internal bool HasLink(int polygon, int edge) => NavGeometry.HasLink(LinksOf(polygon), edge);

    /// <summary>
    /// Per vertex, whether it is a wall vertex; made by the first <see cref="IsWallVertex"/>.
    /// </summary>
    bool[]? _wallVertices;

    /// <summary>
    /// Whether a vertex lies on the edge of the walkable surface (an obstacle, a drop, the end of the mesh): the places a footprint has to keep clear
    /// of. Edges along the zone border do not count (the next zone continues there).
    /// </summary>
    public bool IsWallVertex(int vertex)
    {
        var w = _wallVertices;
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
                    bool onBorder = NavGeometry.OnLine(a.X, b.X, BoundsMin.X) || NavGeometry.OnLine(a.X, b.X, BoundsMax.X)
                        || NavGeometry.OnLine(a.Z, b.Z, BoundsMin.Y) || NavGeometry.OnLine(a.Z, b.Z, BoundsMax.Y);
                    if (onBorder) continue;
                    w[poly[k]] = true;
                    w[poly[(k + 1) % poly.Length]] = true;
                }
            }
            _wallVertices = w;
        }
        return w[vertex];
    }

    /// <summary>
    /// <see cref="Heights"/> from a list with one entry per polygon (null when every entry is).
    /// </summary>
    internal static HeightGrid?[]? HeightsOf(List<HeightGrid?> list) => list.Any(h => h is not null) ? [.. list] : null;

    /// <summary>
    /// This mesh with another kept set over the same polygons (the arrays are shared).
    /// </summary>
    internal ZoneNavMesh WithKept(bool[] kept) => new()
    {
        ZoneX = ZoneX,
        ZoneZ = ZoneZ,
        Vertices = Vertices,
        Polygons = Polygons,
        Neighbours = Neighbours,
        Areas = Areas,
        Links = Links,
        Kept = kept,
        BoundsMin = BoundsMin,
        BoundsMax = BoundsMax,
        DoorIds = DoorIds,
        DoorOf = DoorOf,
        Surfaces = Surfaces,
        Terrain = Terrain,
        Heights = Heights,
        InteriorStart = InteriorStart,
    };

    /// <summary>
    /// A copy without the pruned polygons (and the vertices only they used), neighbours renumbered; what the game keeps and caches. Arrays for the
    /// maps, no LINQ: it runs whenever a loaded zone's kept set changes, and a growing vertex dictionary and an iterator per polygon were most of its
    /// garbage.
    /// </summary>
    public ZoneNavMesh WithoutPruned()
    {
        var map = new int[PolygonCount];
        int count = 0;
        for (int p = 0; p < PolygonCount; p++) map[p] = Kept[p] ? count++ : -1;

        var vertexMap = new int[Vertices.Length];
        Array.Fill(vertexMap, -1);
        var vertices = new List<Vector3>();
        var heights = new List<HeightGrid?>(count);
        var polygons = new int[count][];
        var neighbours = new int[count][];
        var areas = new byte[count];
        var doorOf = DoorOf is null ? null : new int[count];
        var links = new int[]?[count];
        for (int p = 0; p < PolygonCount; p++)
        {
            if (!Kept[p]) continue;
            int n = map[p];
            heights.Add(Heights?[p]);
            polygons[n] = KeptVertices(Polygons[p], vertexMap, vertices);
            neighbours[n] = Renumbered(Neighbours[p], map);
            areas[n] = Areas[p];
            if (doorOf is not null) doorOf[n] = DoorOf![p];
            links[n] = KeptLinks(LinksOf(p), map);
        }

        var kept = new bool[count];
        Array.Fill(kept, true);
        return new ZoneNavMesh
        {
            ZoneX = ZoneX,
            ZoneZ = ZoneZ,
            Vertices = [.. vertices],
            Polygons = polygons,
            Neighbours = neighbours,
            Areas = areas,
            Links = links,
            Kept = kept,
            BoundsMin = BoundsMin,
            BoundsMax = BoundsMax,
            DoorIds = DoorIds,
            DoorOf = doorOf,
            Surfaces = Surfaces,
            Terrain = Terrain,
            Heights = HeightsOf(heights),
            InteriorStart = KeptInteriorStart(map),
        };
    }

    /// <summary>
    /// <see cref="InteriorStart"/> in the copy's numbering (<paramref name="map"/>: the copy's index per polygon, −1 if pruned).
    /// </summary>
    int KeptInteriorStart(int[] map)
    {
        if (InteriorStart >= PolygonCount) return InteriorStart;
        int kept = 0;
        for (int p = 0; p < InteriorStart; p++) if (map[p] >= 0) kept++;
        return kept;
    }

    /// <summary>
    /// A polygon's vertices in the copy's numbering, each vertex copied the first time a kept polygon uses it.
    /// </summary>
    int[] KeptVertices(int[] polygon, int[] vertexMap, List<Vector3> vertices)
    {
        var result = new int[polygon.Length];
        for (int k = 0; k < polygon.Length; k++)
        {
            int v = polygon[k];
            if (vertexMap[v] < 0)
            {
                vertexMap[v] = vertices.Count;
                vertices.Add(Vertices[v]);
            }
            result[k] = vertexMap[v];
        }
        return result;
    }

    /// <summary>
    /// <paramref name="neighbours"/> with each neighbour moved to its new number in <paramref name="map"/> (-1 stays -1).
    /// </summary>
    static int[] Renumbered(int[] neighbours, int[] map)
    {
        var result = new int[neighbours.Length];
        for (int k = 0; k < neighbours.Length; k++) result[k] = neighbours[k] >= 0 ? map[neighbours[k]] : -1;
        return result;
    }

    /// <summary>
    /// A polygon's links (pairs of edge and target) to kept polygons, renumbered; null when none is left.
    /// </summary>
    static int[]? KeptLinks(ReadOnlySpan<int> links, int[] map)
    {
        int keptPairs = 0;
        for (int i = 0; i < links.Length; i += 2) if (map[links[i + 1]] >= 0) keptPairs++;
        if (keptPairs == 0) return null;
        var result = new int[keptPairs * 2];
        int at = 0;
        for (int i = 0; i < links.Length; i += 2)
            if (map[links[i + 1]] >= 0)
            {
                result[at++] = links[i];
                result[at++] = map[links[i + 1]];
            }
        return result;
    }

    // ---- point queries ----

    /// <summary>
    /// Makes <see cref="Index"/> on first use (thread-safe).
    /// </summary>
    readonly Lazy<PointIndex> _index;

    /// <summary>
    /// The grid over the polygons' XZ boxes (64 units a cell, ascending polygon order in a cell); built on first use, independent of
    /// <see cref="Kept"/>.
    /// </summary>
    internal PointIndex Index => _index.Value;

    /// <summary>
    /// A mesh whose point index is built on first use.
    /// </summary>
    public ZoneNavMesh() => _index = new Lazy<PointIndex>(() => new PointIndex(this), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// A grid over the polygons' X/Z boxes: for each cell the polygons whose box meets it, in ascending order.
    /// </summary>
    internal sealed class PointIndex
    {
        /// <summary>
        /// A grid cell's edge (units).
        /// </summary>
        public const float Cell = 64;
        /// <summary>
        /// The grid's columns (along X) and rows (along Z), from the mesh's box.
        /// </summary>
        public readonly int Columns, Rows;
        /// <summary>
        /// The polygons of each cell, row-major (z x <see cref="Columns"/> + x).
        /// </summary>
        public readonly int[][] Cells;
        /// <summary>
        /// Each polygon's box on X/Z: smallest and largest corner.
        /// </summary>
        public readonly Vector2[] Min, Max;

        /// <summary>
        /// Indexes the polygons of <paramref name="m"/>: their boxes, then each cell's polygons in two passes (count, then fill).
        /// </summary>
        public PointIndex(ZoneNavMesh m)
        {
            Columns = (int)MathF.Ceiling((m.BoundsMax.X - m.BoundsMin.X) / Cell) + 1;
            Rows = (int)MathF.Ceiling((m.BoundsMax.Y - m.BoundsMin.Y) / Cell) + 1;
            Min = new Vector2[m.PolygonCount];
            Max = new Vector2[m.PolygonCount];
            for (int p = 0; p < m.PolygonCount; p++)
            {
                var lo = new Vector2(float.MaxValue);
                var hi = new Vector2(float.MinValue);
                foreach (int i in m.Polygons[p])
                {
                    var v = m.Vertices[i];
                    lo = Vector2.Min(lo, new(v.X, v.Z));
                    hi = Vector2.Max(hi, new(v.X, v.Z));
                }
                Min[p] = lo;
                Max[p] = hi;
            }
            // Two passes over the boxes: count the polygons per cell, then fill exact arrays (ascending polygon order in every cell).
            var counts = new int[Columns * Rows];
            for (int p = 0; p < m.PolygonCount; p++)
            {
                var (x0, x1, z0, z1) = CellRange(m, p);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++) counts[z * Columns + x]++;
            }
            Cells = new int[counts.Length][];
            for (int c = 0; c < counts.Length; c++) Cells[c] = counts[c] == 0 ? [] : new int[counts[c]];
            Array.Clear(counts);
            for (int p = 0; p < m.PolygonCount; p++)
            {
                var (x0, x1, z0, z1) = CellRange(m, p);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int c = z * Columns + x;
                        Cells[c][counts[c]++] = p;
                    }
            }
        }

        /// <summary>
        /// The cells polygon <paramref name="p"/>'s box covers.
        /// </summary>
        (int X0, int X1, int Z0, int Z1) CellRange(ZoneNavMesh m, int p) =>
            (Col(Min[p].X - m.BoundsMin.X), Col(Max[p].X - m.BoundsMin.X), Row(Min[p].Y - m.BoundsMin.Y), Row(Max[p].Y - m.BoundsMin.Y));

        /// <summary>
        /// The column of an X offset from the mesh's box, clamped into the grid.
        /// </summary>
        public int Col(float v) => Math.Clamp((int)(v / Cell), 0, Columns - 1);
        /// <summary>
        /// The row of a Z offset from the mesh's box, clamped into the grid.
        /// </summary>
        public int Row(float v) => Math.Clamp((int)(v / Cell), 0, Rows - 1);
    }

    /// <summary>
    /// Appends the kept polygons that contain (x, z) seen from above.
    /// </summary>
    public void PolygonsAt(float x, float z, List<int> result)
    {
        if (!TryCell(x, z, out var ix, out var cell)) return;
        foreach (int p in cell)
            if (Covers(ix, p, x, z)) result.Add(p);
    }

    /// <summary>
    /// The height of the highest kept surface at (x, z); false when no kept polygon contains the point. <see cref="PolygonsAt"/> and
    /// <see cref="HeightAt"/> without the list.
    /// </summary>
    public bool TryHighestAt(float x, float z, out float height)
    {
        height = float.MinValue;
        if (!TryCell(x, z, out var ix, out var cell)) return false;
        bool any = false;
        foreach (int p in cell)
            if (Covers(ix, p, x, z))
            {
                height = Math.Max(height, HeightAt(p, x, z));
                any = true;
            }
        return any;
    }

    /// <summary>
    /// Appends the heights at (x, z) of every kept polygon containing the point (stacked floors give several).
    /// </summary>
    public void HeightsAt(float x, float z, List<float> heights)
    {
        if (!TryCell(x, z, out var ix, out var cell)) return;
        foreach (int p in cell)
            if (Covers(ix, p, x, z)) heights.Add(HeightAt(p, x, z));
    }

    /// <summary>
    /// The point index and the cell of (<paramref name="x"/>, <paramref name="z"/>); false (an empty cell) for a point more than 1 unit outside the
    /// mesh's box.
    /// </summary>
    bool TryCell(float x, float z, out PointIndex ix, out int[] cell)
    {
        ix = _index.Value;
        cell = [];
        if (x < BoundsMin.X - 1 || x > BoundsMax.X + 1 || z < BoundsMin.Y - 1 || z > BoundsMax.Y + 1) return false;
        cell = ix.Cells[ix.Row(z - BoundsMin.Y) * ix.Columns + ix.Col(x - BoundsMin.X)];
        return true;
    }

    /// <summary>
    /// Whether kept polygon <paramref name="p"/> contains (<paramref name="x"/>, <paramref name="z"/>): its box first (1e-3 slack), then the
    /// exact test.
    /// </summary>
    bool Covers(PointIndex ix, int p, float x, float z) =>
        Kept[p] && x >= ix.Min[p].X - 1e-3f && x <= ix.Max[p].X + 1e-3f && z >= ix.Min[p].Y - 1e-3f && z <= ix.Max[p].Y + 1e-3f && Contains(p, x, z);

    /// <summary>
    /// The kept polygon nearest to (x, y, z) in XZ distance (ties and containing polygons by height), within <paramref name="maxDistance"/>, not one
    /// <paramref name="skip"/> rules out; −1 when none.
    /// </summary>
    public int Nearest(float x, float y, float z, float maxDistance, out float distance, Func<int, bool>? skip = null)
    {
        var ix = _index.Value;
        int best = -1;
        float bestScore = float.MaxValue;
        distance = float.MaxValue;
        int x0 = ix.Col(x - maxDistance - BoundsMin.X), x1 = ix.Col(x + maxDistance - BoundsMin.X);
        int z0 = ix.Row(z - maxDistance - BoundsMin.Y), z1 = ix.Row(z + maxDistance - BoundsMin.Y);
        for (int cz = z0; cz <= z1; cz++)
            for (int cx = x0; cx <= x1; cx++)
                foreach (int p in ix.Cells[cz * ix.Columns + cx])
                {
                    if (!Kept[p] || skip?.Invoke(p) == true) continue;
                    if (x < ix.Min[p].X - maxDistance || x > ix.Max[p].X + maxDistance || z < ix.Min[p].Y - maxDistance
                            || z > ix.Max[p].Y + maxDistance)
                        continue;
                    float d = DistanceXz(p, x, z);
                    if (d > maxDistance) continue;
                    float py = HeightAt(p, Math.Clamp(x, ix.Min[p].X, ix.Max[p].X), Math.Clamp(z, ix.Min[p].Y, ix.Max[p].Y));
                    // XZ distance first, then how far the surface is from the wanted height.
                    float score = d * 4 + Math.Abs(py - y);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = p;
                        distance = d;
                    }
                }
        return best;
    }

    /// <summary>
    /// The number of polygons the seed pass kept.
    /// </summary>
    public int KeptCount
    {
        get
        {
            int n = 0;
            foreach (var k in Kept) if (k) n++;
            return n;
        }
    }

    /// <summary>
    /// The vertex average of a polygon.
    /// </summary>
    public Vector3 Centre(int polygon)
    {
        var sum = Vector3.Zero;
        var p = Polygons[polygon];
        foreach (var i in p) sum += Vertices[i];
        return sum / p.Length;
    }

    /// <summary>
    /// Height of the polygon's surface at (x, z), assuming the point lies inside: its height samples there (<see cref="Heights"/>), else its fan over
    /// the corners.
    /// </summary>
    public float HeightAt(int polygon, float x, float z)
    {
        if (Heights?[polygon] is { } grid)
        {
            if (grid == HeightGrid.OnTerrain)
            {
                if (Terrain is { } terrain && terrain.TryHeightAt(x, z, out float ground))
                    return MathF.Max(ground, Data.World.WorldWater.Height);
            }
            else if (grid.TryHeightAt(x, z, out float sampled)) return sampled;
        }
        var p = Polygons[polygon];
        // The polygon is convex and nearly planar: interpolate over the fan triangle containing the point.
        var a = Vertices[p[0]];
        for (int k = 1; k + 1 < p.Length; k++)
        {
            var b = Vertices[p[k]];
            var c = Vertices[p[k + 1]];
            if (NavGeometry.Barycentric(x, z, a, b, c, out float u, out float v, out float w) && u >= -1e-4f && v >= -1e-4f && w >= -1e-4f)
                return u * a.Y + v * b.Y + w * c.Y;
        }
        // Outside every fan triangle (a point just beyond an edge): the nearest vertex height.
        float best = float.MaxValue, y = a.Y;
        foreach (var i in p)
        {
            float d = (Vertices[i].X - x) * (Vertices[i].X - x) + (Vertices[i].Z - z) * (Vertices[i].Z - z);
            if (d < best)
            {
                best = d;
                y = Vertices[i].Y;
            }
        }
        return y;
    }

    /// <summary>
    /// Whether (x, z) lies inside the polygon seen from above.
    /// </summary>
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

    /// <summary>
    /// Distance on the XZ plane from the point to the polygon (0 inside).
    /// </summary>
    internal float DistanceXz(int polygon, float x, float z)
    {
        if (Contains(polygon, x, z)) return 0;
        var poly = Polygons[polygon];
        float best = float.MaxValue;
        for (int k = 0; k < poly.Length; k++)
        {
            var a = Vertices[poly[k]];
            var b = Vertices[poly[(k + 1) % poly.Length]];
            best = Math.Min(best, NavGeometry.SegmentDistanceXz(x, z, a.X, a.Z, b.X, b.Z));
        }
        return best;
    }

    /// <summary>
    /// The polygons a polygon leads to inside this mesh: behind its edges, then across its <see cref="Links"/>.
    /// </summary>
    public IEnumerable<int> NeighboursOf(int polygon)
    {
        foreach (int q in Neighbours[polygon]) if (q >= 0) yield return q;
        var l = Links is { } links && links[polygon] is { } own ? own : [];
        for (int i = 1; i < l.Length; i += 2) yield return l[i];
    }

    /// <summary>
    /// The edges of kept polygons (of every polygon when <paramref name="all"/>) that lie on the border line of this zone facing the direction
    /// (<paramref name="dx"/>, <paramref name="dz"/>) and lead nowhere inside the mesh (edges that have a link are left out when
    /// <paramref name="skipLinked"/>).
    /// </summary>
    /// <param name="keep">The kept set to read instead of <see cref="Kept"/>; null with <paramref name="all"/> false reads
    /// <see cref="Kept"/>.</param>
    /// <param name="all">Every polygon, kept or not.</param>
    internal List<(int Polygon, int Edge)> BorderEdges(int dx, int dz, bool skipLinked, bool[]? keep = null, bool all = false)
    {
        var result = new List<(int, int)>();
        float line = dx > 0 ? BoundsMax.X : dx < 0 ? BoundsMin.X : dz > 0 ? BoundsMax.Y : BoundsMin.Y;
        var kept = keep ?? Kept;
        for (int p = 0; p < PolygonCount; p++)
        {
            if (!all && !kept[p]) continue;
            var poly = Polygons[p];
            for (int k = 0; k < poly.Length; k++)
            {
                if (Neighbours[p][k] >= 0 || (skipLinked && HasLink(p, k))) continue;
                var va = Vertices[poly[k]];
                var vb = Vertices[poly[(k + 1) % poly.Length]];
                float ca = dx != 0 ? va.X : va.Z, cb = dx != 0 ? vb.X : vb.Z;
                if (NavGeometry.OnLine(ca, cb, line)) result.Add((p, k));
            }
        }
        return result;
    }
}

/// <summary>
/// The heights of one polygon's surface every <see cref="Step"/> units from (<see cref="X0"/>, <see cref="Z0"/>), row-major, NaN where the
/// heightfield had none of the polygon's own surface (docs/game/pathfinding.md, "Heights").
/// </summary>
public sealed class HeightGrid(float x0, float z0, float step, int columns, int rows, float[] values)
{
    /// <summary>
    /// For a polygon lying on the terrain: its height is the zone's heightmap (<see cref="ZoneNavMesh.Terrain"/>, the water's height over the sea),
    /// no samples kept.
    /// </summary>
    public static readonly HeightGrid OnTerrain = new(0, 0, 1, 0, 0, []);

    /// <summary>
    /// The world X of the first sample.
    /// </summary>
    public float X0 { get; } = x0;
    /// <summary>
    /// The world Z of the first sample.
    /// </summary>
    public float Z0 { get; } = z0;
    /// <summary>
    /// Units between samples.
    /// </summary>
    public float Step { get; } = step;
    /// <summary>
    /// Samples per row (along X).
    /// </summary>
    public int Columns { get; } = columns;
    /// <summary>
    /// Rows of samples (along Z).
    /// </summary>
    public int Rows { get; } = rows;
    /// <summary>
    /// The heights, row-major; NaN where the polygon's own surface had none.
    /// </summary>
    public float[] Values { get; } = values;
    /// <summary>
    /// While the zone is built: per sample, whether it is a span of the polygon's own region (not the span nearest its plane); null once built or
    /// loaded.
    /// </summary>
    internal bool[]? Own { get; set; }

    /// <summary>
    /// The height at (x, z) between the four samples round it, those that have none left out; false when none of the four has one.
    /// </summary>
    public bool TryHeightAt(float x, float z, out float height)
    {
        float u = Math.Clamp((x - X0) / Step, 0, Columns - 1), v = Math.Clamp((z - Z0) / Step, 0, Rows - 1);
        int i = Math.Min((int)u, Math.Max(0, Columns - 2)), j = Math.Min((int)v, Math.Max(0, Rows - 2));
        float fu = Math.Clamp(u - i, 0, 1), fv = Math.Clamp(v - j, 0, 1);
        int i1 = Math.Min(i + 1, Columns - 1), j1 = Math.Min(j + 1, Rows - 1);
        float sum = 0, weight = 0, plain = 0;
        int found = 0;
        Add(Values[j * Columns + i], (1 - fu) * (1 - fv));
        Add(Values[j * Columns + i1], fu * (1 - fv));
        Add(Values[j1 * Columns + i], (1 - fu) * fv);
        Add(Values[j1 * Columns + i1], fu * fv);
        height = weight > 1e-4f ? sum / weight : found > 0 ? plain / found : 0;
        return found > 0;

        void Add(float h, float w)
        {
            if (float.IsNaN(h)) return;
            sum += h * w;
            weight += w;
            plain += h;
            found++;
        }
    }
}