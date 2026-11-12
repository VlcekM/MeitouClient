using System.Numerics;
using DotRecast.Recast;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Stitches the tiles' Recast meshes into one zone mesh: the tiles' polygons in tile order, their open inner edges that Recast left unwelded joined
/// as neighbours or links, the winding flipped to counter-clockwise, and the facing border edges of neighbouring tiles linked
/// (docs/game/pathfinding.md, "Own builder"). One instance per stitch.
/// </summary>
internal sealed class NavTileStitcher
{
    /// <summary>
    /// How long (cells) the part of two coinciding edges within the climb must be for the stitcher to join them as neighbours (engine choice: 4
    /// units).
    /// </summary>
    const float SeamClimbCells = 2;

    /// <summary>
    /// A polygon edge on a tile's border, with its two end points once the winding is final.
    /// </summary>
    sealed class BorderEdge
    {
        /// <summary>
        /// The zone polygon and its edge.
        /// </summary>
        public int Polygon, Edge;

        /// <summary>
        /// The edge's start and end.
        /// </summary>
        public Vector3 A, B;
    }

    /// <summary>
    /// The build settings.
    /// </summary>
    readonly NavBuildSettings _s;

    /// <summary>
    /// The tiles' meshes, null for a tile without one.
    /// </summary>
    readonly RcPolyMesh?[] _meshes;

    /// <summary>
    /// The tiles' polygon height samples, when the build has them.
    /// </summary>
    readonly HeightGrid?[]?[]? _details;

    /// <summary>
    /// The zone mesh's vertices.
    /// </summary>
    readonly List<Vector3> _vertices = [];

    /// <summary>
    /// The zone mesh's polygons (vertex indices).
    /// </summary>
    readonly List<int[]> _polygons = [];

    /// <summary>
    /// The neighbour of each polygon edge, -1 for none.
    /// </summary>
    readonly List<int[]> _neighbours = [];

    /// <summary>
    /// The area of each polygon.
    /// </summary>
    readonly List<byte> _areas = [];

    /// <summary>
    /// Each polygon's links (edge, polygon pairs), null for none.
    /// </summary>
    readonly List<int>?[] _links;

    /// <summary>
    /// Border edges per tile and side (0: -X, 1: +Z, 2: +X, 3: -Z, as Recast numbers them).
    /// </summary>
    readonly List<BorderEdge>[,] _borders;

    /// <summary>
    /// The climb in height cells.
    /// </summary>
    readonly int _climbCells;

    /// <summary>
    /// The current tile's open inner edges not yet joined, by their end cells.
    /// </summary>
    readonly Dictionary<(int, int, int, int), (int Polygon, int Edge, int Ia, int Ib)> _open = [];

    /// <summary>
    /// Open edges joined as neighbours.
    /// </summary>
    readonly List<(int P, int E, int Q, int F)> _seams = [];

    /// <summary>
    /// Open edges joined as links: contour simplification can also leave a T at a seam, one edge against two whose middle vertex is off its line.
    /// </summary>
    readonly List<(int P, int E, int Q, int F)> _nearSeams = [];

    /// <summary>
    /// The climb and the half cell the stitched vertices stand under their span's top.
    /// </summary>
    readonly float _climb;

    /// <summary>
    /// A stitcher of <paramref name="meshes"/>.
    /// </summary>
    NavTileStitcher(NavBuildSettings s, RcPolyMesh?[] meshes, HeightGrid?[]?[]? details)
    {
        _s = s;
        _meshes = meshes;
        _details = details;
        _links = new List<int>?[meshes.Sum(m => m?.npolys ?? 0)];
        _borders = new List<BorderEdge>[meshes.Length, 4];

        // Recast welds two polygons' vertices only within 2 height cells, and a step's corner height can come out differently from either side,
        // so a stair foot and the ground beside it may share an edge in X/Z yet not be neighbours. Such open edges of a tile, coinciding in reverse
        // and within the climb at both ends, are joined here (docs/game/pathfinding.md, "Own builder").
        _climbCells = (int)MathF.Floor(s.MaxClimb / s.CellHeight);
        _climb = s.MaxClimb + s.CellHeight / 2;
    }

    /// <summary>
    /// Stitches the tiles' meshes (<paramref name="tilesPerSide"/> by <paramref name="tilesPerSide"/>, in tile order) into the zone's mesh.
    /// </summary>
    internal static ZoneNavMesh Stitch(
        ZoneGeometry g, NavBuildSettings s, RcPolyMesh?[] meshes, int tilesPerSide, float tileWorld, HeightGrid?[]?[]? details = null) =>
        new NavTileStitcher(s, meshes, details).Run(g, tilesPerSide);

    /// <summary>
    /// The stitch's steps: the tiles, the seams, the winding and the links between tiles.
    /// </summary>
    ZoneNavMesh Run(ZoneGeometry g, int tilesPerSide)
    {
        for (int tile = 0; tile < _meshes.Length; tile++)
            AddTile(tile);

        JoinSeams();
        bool flip = FlipWinding();
        SetBorderEdges(flip);
        LinkNearSeams(flip);
        LinkTiles(tilesPerSide);
        return Result(g);
    }

    /// <summary>
    /// Adds one tile's vertices and polygons, collecting its border edges and its open inner edges.
    /// </summary>
    void AddTile(int tile)
    {
        var m = _meshes[tile];
        if (m is null) return;

        _open.Clear();
        int vBase = _vertices.Count;
        int pBase = _polygons.Count;
        for (int i = 0; i < m.nverts; i++)
        {
            // A span's top is the surface rounded up to a cell: take the middle of the cell it fell in.
            _vertices.Add(new Vector3(
                m.bmin.X + m.verts[i * 3] * m.cs,
                m.bmin.Y + (m.verts[i * 3 + 1] - 0.5f) * m.ch,
                m.bmin.Z + m.verts[i * 3 + 2] * m.cs));
        }

        for (int p = 0; p < m.npolys; p++)
            AddPolygon(tile, m, p, vBase, pBase);

        JoinNearMisses(vBase, pBase, _details?[tile]);
    }

    /// <summary>
    /// Adds polygon <paramref name="p"/> of a tile's mesh with its neighbours inside the tile.
    /// </summary>
    void AddPolygon(int tile, RcPolyMesh m, int p, int vBase, int pBase)
    {
        int nvp = m.nvp;
        int count = 0;
        while (count < nvp && m.polys[p * nvp * 2 + count] != RcRecast.RC_MESH_NULL_IDX)
            count++;

        var vi = new int[count];
        var nb = new int[count];
        for (int k = 0; k < count; k++)
        {
            vi[k] = vBase + m.polys[p * nvp * 2 + k];
            int n = m.polys[p * nvp * 2 + nvp + k];
            if (n == RcRecast.RC_MESH_NULL_IDX)
            {
                nb[k] = -1;
                AddOpenEdge(tile, m, p, k, count, pBase);
            }
            else if ((n & 0x8000) != 0)
            {
                nb[k] = -1;
                int side = n & 0xf;
                _borders[tile, side] ??= [];
                _borders[tile, side].Add(new BorderEdge { Polygon = pBase + p, Edge = k });
            }
            else
            {
                nb[k] = pBase + n;
            }
        }

        _polygons.Add(vi);
        _neighbours.Add(nb);
        _areas.Add((byte)m.areas[p]);
    }

    /// <summary>
    /// An edge without a neighbour: a border edge when it lies on the tile's edge, else an open inner edge, joined as a seam with the one facing it
    /// when there is one.
    /// </summary>
    void AddOpenEdge(int tile, RcPolyMesh m, int p, int k, int count, int pBase)
    {
        int nvp = m.nvp;
        int ia = m.polys[p * nvp * 2 + k];
        int ib = m.polys[p * nvp * 2 + (k + 1 == count ? 0 : k + 1)];
        int xa = m.verts[ia * 3];
        int za = m.verts[ia * 3 + 2];
        int xb = m.verts[ib * 3];
        int zb = m.verts[ib * 3 + 2];
        int side = xa == 0 && xb == 0
            ? 0
            : za == _s.TileCells && zb == _s.TileCells
                ? 1
                : xa == _s.TileCells && xb == _s.TileCells
                    ? 2
                    : za == 0 && zb == 0
                        ? 3
                        : -1;
        if (side >= 0)
        {
            _borders[tile, side] ??= [];
            _borders[tile, side]!.Add(new BorderEdge { Polygon = pBase + p, Edge = k });
            return;
        }

        // An open inner edge may face one whose end vertex Recast did not weld (see the seam pass below).
        if (_open.TryGetValue((xb, zb, xa, za), out var other) && SeamMeets(
                Math.Abs(m.verts[ia * 3 + 1] - m.verts[other.Ib * 3 + 1]),
                Math.Abs(m.verts[ib * 3 + 1] - m.verts[other.Ia * 3 + 1]),
                MathF.Sqrt((xb - xa) * (xb - xa) + (zb - za) * (zb - za)), out bool partial)
            && (!partial || SurfacesMeet(m, xa, za, xb, zb, _details?[tile]?[p], _details?[tile]?[other.Polygon - pBase])))
        {
            _open.Remove((xb, zb, xa, za));
            _seams.Add((pBase + p, k, other.Polygon, other.Edge));
        }
        else
        {
            _open[(xa, za, xb, zb)] = (pBase + p, k, ia, ib);
        }
    }

    /// <summary>
    /// Makes the joined seams' polygons neighbours where both are walkable.
    /// </summary>
    void JoinSeams()
    {
        foreach (var (p, e, q, f) in _seams)
        {
            if (_areas[p] != NavArea.Null && _areas[q] != NavArea.Null)
            {
                _neighbours[p][e] = q;
                _neighbours[q][f] = p;
            }
        }
    }

    /// <summary>
    /// Recast winds polygons clockwise seen from above (+Y toward the viewer, Z down the page); our tests want the other way. Whether the polygons
    /// were flipped.
    /// </summary>
    bool FlipWinding()
    {
        bool flip = ClockwiseOverall(_polygons, _vertices);
        if (flip)
        {
            for (int p = 0; p < _polygons.Count; p++)
            {
                var vi = _polygons[p];
                var nb = _neighbours[p];
                int n = vi.Length;
                var rv = new int[n];
                var rn = new int[n];

                // Reverse the vertex order. Edge j of the reversed polygon runs from old vertex n - 1 - j to n - 2 - j, which is the old edge n - 2 -
                // j (wrapping), so that is where its neighbour is read from.
                for (int k = 0; k < n; k++)
                    rv[k] = vi[n - 1 - k];

                for (int k = 0; k < n; k++)
                    rn[k] = nb[(n - 2 - k + n) % n];

                _polygons[p] = rv;
                _neighbours[p] = rn;
            }
        }

        return flip;
    }

    /// <summary>
    /// Border edge indices follow the winding's reversal; then each border edge takes its end points.
    /// </summary>
    void SetBorderEdges(bool flip)
    {
        for (int tile = 0; tile < _meshes.Length; tile++)
        {
            for (int side = 0; side < 4; side++)
            {
                if (_borders[tile, side] is { } list && flip)
                {
                    foreach (var be in list)
                        be.Edge = (_polygons[be.Polygon].Length - 2 - be.Edge + _polygons[be.Polygon].Length) % _polygons[be.Polygon].Length;
                }
            }
        }

        foreach (var list in _borders)
        {
            if (list is null) continue;

            foreach (var be in list)
            {
                var p = _polygons[be.Polygon];
                be.A = _vertices[p[be.Edge]];
                be.B = _vertices[p[(be.Edge + 1) % p.Length]];
            }
        }
    }

    /// <summary>
    /// Links the near-miss seams' polygons. They were found before the flip: their edge indices follow the same reversal.
    /// </summary>
    void LinkNearSeams(bool flip)
    {
        foreach (var (p, e, q, f) in _nearSeams)
        {
            if (_areas[p] == NavArea.Null || _areas[q] == NavArea.Null) continue;

            int np = _polygons[p].Length;
            int nq = _polygons[q].Length;
            int ep = flip ? (np - 2 - e + np) % np : e;
            int fq = flip ? (nq - 2 - f + nq) % nq : f;
            (_links[p] ??= []).Add(ep);
            _links[p]!.Add(q);
            (_links[q] ??= []).Add(fq);
            _links[q]!.Add(p);
        }
    }

    /// <summary>
    /// Links facing border edges of neighbouring tiles where they overlap along the shared line.
    /// </summary>
    void LinkTiles(int tilesPerSide)
    {
        for (int tz = 0; tz < tilesPerSide; tz++)
        {
            for (int tx = 0; tx < tilesPerSide; tx++)
            {
                int tile = tz * tilesPerSide + tx;
                if (tx + 1 < tilesPerSide) Link(_borders[tile, 2], _borders[tile + 1, 0], alongX: false);

                if (tz + 1 < tilesPerSide) Link(_borders[tile, 1], _borders[tile + tilesPerSide, 3], alongX: true);
            }
        }
    }

    /// <summary>
    /// Links the edges of one tile's side with those of the neighbouring tile's facing side that meet them.
    /// </summary>
    void Link(List<BorderEdge>? left, List<BorderEdge>? right, bool alongX)
    {
        if (left is null || right is null) return;

        foreach (var a in left)
        {
            var spanA = NavGeometry.Span(a.A, a.B, alongX);
            foreach (var b in right)
            {
                if (!NavGeometry.SpansMeet(spanA, NavGeometry.Span(b.A, b.B, alongX), _climb)) continue;

                if (_areas[a.Polygon] == NavArea.Null || _areas[b.Polygon] == NavArea.Null) continue;

                // An edge can face several polygons: links, with the portal recomputed from the geometry at query time.
                (_links[a.Polygon] ??= []).Add(a.Edge);
                _links[a.Polygon]!.Add(b.Polygon);
                (_links[b.Polygon] ??= []).Add(b.Edge);
                _links[b.Polygon]!.Add(a.Polygon);
            }
        }
    }

    /// <summary>
    /// The stitched zone mesh, every polygon kept.
    /// </summary>
    ZoneNavMesh Result(ZoneGeometry g) => new()
    {
        ZoneX = g.Zone.X,
        ZoneZ = g.Zone.Y,
        Vertices = [.. _vertices],
        Polygons = [.. _polygons],
        Neighbours = [.. _neighbours],
        Links = _links.Select(l => l?.ToArray()).ToArray(),
        Areas = [.. _areas],
        Kept = Enumerable.Repeat(true, _polygons.Count).ToArray(),
        BoundsMin = g.ZoneMin,
        BoundsMax = g.ZoneMax,
    };

    /// <summary>
    /// Whether the polygons wind clockwise with X right and Z up, by the sum of their signed areas (<see cref="NavGeometry.SignedArea"/>, in double
    /// from each first vertex). The first polygon's own sign, summed in float at world coordinates, came out wrong for a thin one and left a whole
    /// interior clockwise, so no point was inside it (Observed: Snailhouse Lagoon, zone 35.42, docs/game/pathfinding.md "Interiors").
    /// </summary>
    static bool ClockwiseOverall(List<int[]> polygons, List<Vector3> vertices)
    {
        double sum = 0;
        foreach (var p in polygons)
        {
            if (p.Length >= 3) sum += NavGeometry.SignedArea([.. p.Select(i => new Vector2(vertices[i].X, vertices[i].Z))]);
        }

        return sum < 0;
    }

    /// <summary>
    /// Open edges of the tile still unjoined that face each other off line: opposite directions, sharing one end in X/Z (so not two floors either
    /// side of a thin wall), the other overlap end within NavGeometry.NearMissGap of the line, and within the climb at both overlap ends: by the
    /// edges' own heights, else by the polygons' surfaces along the overlap (SurfacesStep).
    /// </summary>
    void JoinNearMisses(int vBase, int pBase, HeightGrid?[]? grids)
    {
        var left = _open.Values.ToArray();
        for (int i = 0; i < left.Length; i++)
        {
            for (int j = i + 1; j < left.Length; j++)
            {
                var (p, e, ia, ib) = left[i];
                var (q, f, ja, jb) = left[j];
                if (p == q) continue;

                Vector3 a0 = _vertices[vBase + ia];
                Vector3 a1 = _vertices[vBase + ib];
                Vector3 b0 = _vertices[vBase + ja];
                Vector3 b1 = _vertices[vBase + jb];
                if (!SameXz(a0, b1) && !SameXz(a1, b0)) continue;

                if (!NavGeometry.FacingOverlap(a0, a1, b0, b1, NavGeometry.NearMissGap, out float lo, out float hi, out float yLo, out float yHi))
                    continue;

                if ((hi - lo) * Vector2.Distance(new(a0.X, a0.Z), new(a1.X, a1.Z)) < _s.CellSize / 2) continue;

                if ((Math.Abs(float.Lerp(a0.Y, a1.Y, lo) - yLo) > _climb || Math.Abs(float.Lerp(a0.Y, a1.Y, hi) - yHi) > _climb)
                    && !SurfacesStep(a0, a1, lo, hi, yLo, yHi, grids?[p - pBase], grids?[q - pBase]))
                    continue;

                _nearSeams.Add((p, e, q, f));
            }
        }
    }

    /// <summary>
    /// A long simplified edge runs straight in height across a kink of its polygon's surface (a stair's foot on flat ground: Recast simplifies
    /// contours in X/Z only), so its height at the overlap can be well off the ground beside. Along the overlap each side's surface (its own
    /// samples, else its edge) must be within the climb of the other's, at every cell; only with samples on at least one side
    /// (docs/game/pathfinding.md, "Inner seams").
    /// </summary>
    bool SurfacesStep(Vector3 a0, Vector3 a1, float lo, float hi, float yLo, float yHi, HeightGrid? own, HeightGrid? facing)
    {
        if (own?.Own is null && facing?.Own is null) return false;

        int cells = Math.Max(1, (int)MathF.Ceiling((hi - lo) * Vector2.Distance(new(a0.X, a0.Z), new(a1.X, a1.Z)) / _s.CellSize));
        for (int k = 0; k <= cells; k++)
        {
            float t = float.Lerp(lo, hi, k / (float)cells);
            var at = Vector3.Lerp(a0, a1, t);
            float a = own?.Own is not null && OwnHeightAt(own, at.X, at.Z, out float ha) ? ha : at.Y;
            float b = facing?.Own is not null && OwnHeightAt(facing, at.X, at.Z, out float hb) ? hb : float.Lerp(yLo, yHi, k / (float)cells);
            if (MathF.Abs(a - b) > _climb) return false;
        }

        return true;
    }

    /// <summary>
    /// The height at (x, z) between the four samples round it that are the polygon's own region (a nearest sample on a steep stair can be the
    /// ground beside it, a step's worth off), else the nearest own one.
    /// </summary>
    static bool OwnHeightAt(HeightGrid grid, float x, float z, out float height)
    {
        float u = (x - grid.X0) / grid.Step;
        float v = (z - grid.Z0) / grid.Step;
        int i0 = (int)MathF.Floor(u);
        int j0 = (int)MathF.Floor(v);
        float sum = 0;
        float weight = 0;
        for (int j = j0; j <= j0 + 1; j++)
        {
            for (int i = i0; i <= i0 + 1; i++)
            {
                if (i < 0 || j < 0 || i >= grid.Columns || j >= grid.Rows || !grid.Own![j * grid.Columns + i]) continue;

                float w = (1 - MathF.Abs(u - i)) * (1 - MathF.Abs(v - j));
                sum += w * grid.Values[j * grid.Columns + i];
                weight += w;
            }
        }

        if (weight > 1e-4f)
        {
            height = sum / weight;
            return true;
        }

        return NearestOwn(grid, x, z, out height);
    }

    /// <summary>
    /// The sample of the polygon's own region nearest (x, z) within one sample step.
    /// </summary>
    static bool NearestOwn(HeightGrid grid, float x, float z, out float height)
    {
        height = float.NaN;
        float u = (x - grid.X0) / grid.Step;
        float v = (z - grid.Z0) / grid.Step;
        float best = 1.0001f;
        int i0 = (int)MathF.Round(u);
        int j0 = (int)MathF.Round(v);
        for (int j = Math.Max(0, j0 - 1); j <= Math.Min(j0 + 1, grid.Rows - 1); j++)
        {
            for (int i = Math.Max(0, i0 - 1); i <= Math.Min(i0 + 1, grid.Columns - 1); i++)
            {
                float d = (i - u) * (i - u) + (j - v) * (j - v);
                if (!grid.Own![j * grid.Columns + i] || d >= best) continue;

                best = d;
                height = grid.Values[j * grid.Columns + i];
            }
        }

        return !float.IsNaN(height);
    }

    /// <summary>
    /// Whether two points share X and Z.
    /// </summary>
    static bool SameXz(Vector3 a, Vector3 b) => a.X == b.X && a.Z == b.Z;

    /// <summary>
    /// Two coinciding edges (height gaps at their ends in cells, length in cells) meet when the gap stays within the climb along at least
    /// SeamClimbCells of them: a corner can take a higher neighbouring cell's height on one side (a stair's top beside a raised cheek, Storm House
    /// 27.22:1821-), which left one end over the climb although the rest of the edge steps less. partial: within the climb along part of it only,
    /// so the polygons' own surfaces are checked too (SurfacesMeet).
    /// </summary>
    bool SeamMeets(int gapA, int gapB, float cells, out bool partial)
    {
        partial = false;
        if (gapA <= _climbCells && gapB <= _climbCells) return true;

        int lo = Math.Min(gapA, gapB);
        int hi = Math.Max(gapA, gapB);
        if (lo > _climbCells) return false;

        partial = true;
        float within = cells * (_climbCells - lo) / (float)(hi - lo);
        return within >= SeamClimbCells;
    }

    /// <summary>
    /// A seam within the climb along part of it only, by its corners' heights, is joined only when the two polygons' own surfaces (each one's
    /// region's height samples nearest the edge's cells) are within the climb along SeamClimbCells of it too: the corner within it can as well be
    /// the one that took a higher neighbouring cell's height, a floor's for a ramp passing under the floor's edge (engine choice; Observed:
    /// Ancient Lab with TwoStorey, zone 28.60, whose upper floor joined the middle of its spiral stair 8..17 below along the stairwell instead of
    /// the stair's top).
    /// </summary>
    bool SurfacesMeet(RcPolyMesh m, int xa, int za, int xb, int zb, HeightGrid? own, HeightGrid? facing)
    {
        if (own?.Own is null || facing?.Own is null) return true;

        int cells = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt((xb - xa) * (xb - xa) + (zb - za) * (zb - za))));
        int within = 0;
        int measured = 0;
        for (int k = 0; k <= cells; k++)
        {
            float x = m.bmin.X + float.Lerp(xa, xb, k / (float)cells) * m.cs;
            float z = m.bmin.Z + float.Lerp(za, zb, k / (float)cells) * m.cs;
            if (!NearestOwn(own, x, z, out float a) || !NearestOwn(facing, x, z, out float b)) continue;

            measured++;
            if (MathF.Abs(a - b) <= _climb) within++;
        }

        return measured == 0 || within >= SeamClimbCells;
    }
}
