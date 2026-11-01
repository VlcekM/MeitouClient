using System.Numerics;
using DotRecast.Recast;

namespace Meitou.Navigation;

internal static partial class ZoneNavMeshBuilder
{
    /// <summary>How long (cells) the part of two coinciding edges within the climb must be for the stitcher to join them as neighbours (engine choice: 4 units).</summary>
    const float SeamClimbCells = 2;

    // ---- stitching ----

    sealed class BorderEdge
    {
        public int Polygon, Edge;
        public Vector3 A, B;
    }

    internal static ZoneNavMesh Stitch(ZoneGeometry g, NavBuildSettings s, RcPolyMesh?[] meshes, int tilesPerSide, float tileWorld)
    {
        var vertices = new List<Vector3>();
        var polygons = new List<int[]>();
        var neighbours = new List<int[]>();
        var areas = new List<byte>();
        var links = new List<int>?[meshes.Sum(m => m?.npolys ?? 0)];
        // Border edges per tile and side (0: -X, 1: +Z, 2: +X, 3: -Z, as Recast numbers them).
        var borders = new List<BorderEdge>[meshes.Length, 4];
        // Recast welds two polygons' vertices only within 2 height cells, and a step's corner height can come out differently from either side,
        // so a stair foot and the ground beside it may share an edge in X/Z yet not be neighbours. Such open edges of a tile, coinciding in reverse
        // and within the climb at both ends, are joined here (docs/game/pathfinding.md, "Own builder").
        int climbCells = (int)MathF.Floor(s.MaxClimb / s.CellHeight);
        var open = new Dictionary<(int, int, int, int), (int Polygon, int Edge, int Ia, int Ib)>();
        var seams = new List<(int P, int E, int Q, int F)>();
        // Contour simplification can also leave a T at such a seam: one edge against two whose middle vertex is off its line. Those join as links.
        var nearSeams = new List<(int P, int E, int Q, int F)>();
        float climb = s.MaxClimb + 0.5f;

        for (int tile = 0; tile < meshes.Length; tile++)
        {
            var m = meshes[tile];
            if (m is null) continue;
            open.Clear();
            int nvp = m.nvp;
            int vBase = vertices.Count, pBase = polygons.Count;
            for (int i = 0; i < m.nverts; i++)
                // A span's top is the surface rounded up to a cell: take the middle of the cell it fell in.
                vertices.Add(new Vector3(m.bmin.X + m.verts[i * 3] * m.cs, m.bmin.Y + (m.verts[i * 3 + 1] - 0.5f) * m.ch, m.bmin.Z + m.verts[i * 3 + 2] * m.cs));
            for (int p = 0; p < m.npolys; p++)
            {
                int count = 0;
                while (count < nvp && m.polys[p * nvp * 2 + count] != RcRecast.RC_MESH_NULL_IDX) count++;
                var vi = new int[count];
                var nb = new int[count];
                for (int k = 0; k < count; k++)
                {
                    vi[k] = vBase + m.polys[p * nvp * 2 + k];
                    int n = m.polys[p * nvp * 2 + nvp + k];
                    if (n == RcRecast.RC_MESH_NULL_IDX)
                    {
                        nb[k] = -1;
                        int ia = m.polys[p * nvp * 2 + k], ib = m.polys[p * nvp * 2 + (k + 1 == count ? 0 : k + 1)];
                        int xa = m.verts[ia * 3], za = m.verts[ia * 3 + 2], xb = m.verts[ib * 3], zb = m.verts[ib * 3 + 2];
                        int side = xa == 0 && xb == 0 ? 0 : za == s.TileCells && zb == s.TileCells ? 1 : xa == s.TileCells && xb == s.TileCells ? 2 : za == 0 && zb == 0 ? 3 : -1;
                        if (side >= 0)
                        {
                            borders[tile, side] ??= [];
                            borders[tile, side]!.Add(new BorderEdge { Polygon = pBase + p, Edge = k });
                        }
                        else
                        {
                            // An open inner edge may face one whose end vertex Recast did not weld (see the seam pass below).
                            if (open.TryGetValue((xb, zb, xa, za), out var other) && SeamMeets(Math.Abs(m.verts[ia * 3 + 1] - m.verts[other.Ib * 3 + 1]),
                                Math.Abs(m.verts[ib * 3 + 1] - m.verts[other.Ia * 3 + 1]), MathF.Sqrt((xb - xa) * (xb - xa) + (zb - za) * (zb - za))))
                            {
                                open.Remove((xb, zb, xa, za));
                                seams.Add((pBase + p, k, other.Polygon, other.Edge));
                            }
                            else open[(xa, za, xb, zb)] = (pBase + p, k, ia, ib);
                        }
                    }
                    else if ((n & 0x8000) != 0)
                    {
                        nb[k] = -1;
                        int side = n & 0xf;
                        borders[tile, side] ??= [];
                        borders[tile, side].Add(new BorderEdge { Polygon = pBase + p, Edge = k });
                    }
                    else nb[k] = pBase + n;
                }
                polygons.Add(vi);
                neighbours.Add(nb);
                areas.Add((byte)m.areas[p]);
            }
            JoinNearMisses(vBase);
        }

        // Open edges of the tile still unjoined that face each other off line: opposite directions, sharing one end in X/Z (so not two floors either
        // side of a thin wall), the other overlap end within NavGeometry.NearMissGap of the line, and within the climb at both overlap ends.
        void JoinNearMisses(int vBase)
        {
            var left = open.Values.ToArray();
            for (int i = 0; i < left.Length; i++)
                for (int j = i + 1; j < left.Length; j++)
                {
                    var (p, e, ia, ib) = left[i];
                    var (q, f, ja, jb) = left[j];
                    if (p == q) continue;
                    Vector3 a0 = vertices[vBase + ia], a1 = vertices[vBase + ib], b0 = vertices[vBase + ja], b1 = vertices[vBase + jb];
                    if (!SameXZ(a0, b1) && !SameXZ(a1, b0)) continue;
                    if (!NavGeometry.FacingOverlap(a0, a1, b0, b1, NavGeometry.NearMissGap, out float lo, out float hi, out float yLo, out float yHi)) continue;
                    if ((hi - lo) * Vector2.Distance(new(a0.X, a0.Z), new(a1.X, a1.Z)) < s.CellSize / 2) continue;
                    if (Math.Abs(float.Lerp(a0.Y, a1.Y, lo) - yLo) > climb || Math.Abs(float.Lerp(a0.Y, a1.Y, hi) - yHi) > climb) continue;
                    nearSeams.Add((p, e, q, f));
                }
        }

        static bool SameXZ(Vector3 a, Vector3 b) => a.X == b.X && a.Z == b.Z;

        // Two coinciding edges (height gaps at their ends in cells, length in cells) meet when the gap stays within the climb along at least SeamClimbCells of
        // them: a corner can take a higher neighbouring cell's height on one side (a stair's top beside a raised cheek, Storm House 27.22:1821-), which
        // left one end over the climb although the rest of the edge steps less.
        bool SeamMeets(int gapA, int gapB, float cells)
        {
            if (gapA <= climbCells && gapB <= climbCells) return true;
            int lo = Math.Min(gapA, gapB), hi = Math.Max(gapA, gapB);
            if (lo > climbCells) return false;
            float within = cells * (climbCells - lo) / (float)(hi - lo);
            return within >= SeamClimbCells;
        }

        foreach (var (p, e, q, f) in seams)
            if (areas[p] != NavArea.Null && areas[q] != NavArea.Null) { neighbours[p][e] = q; neighbours[q][f] = p; }

        // Recast winds polygons clockwise seen from above (+Y toward the viewer, Z down the page); our tests want the other way.
        bool flip = false;
        if (polygons.Count > 0)
        {
            double area2 = 0;
            var first = polygons.First(p => p.Length >= 3);
            for (int k = 0; k < first.Length; k++)
            {
                var a = vertices[first[k]];
                var b = vertices[first[(k + 1) % first.Length]];
                area2 += (b.X - a.X) * (b.Z + a.Z);
            }
            // Positive sum of (x2 - x1)(z2 + z1) means clockwise in an (X right, Z up) plane.
            flip = area2 > 0;
        }
        if (flip)
            for (int p = 0; p < polygons.Count; p++)
            {
                var vi = polygons[p];
                var nb = neighbours[p];
                int n = vi.Length;
                var rv = new int[n];
                var rn = new int[n];
                // Reverse the vertex order. Edge j of the reversed polygon runs from old vertex n - 1 - j to n - 2 - j, which is the old edge n - 2 - j
                // (wrapping), so that is where its neighbour is read from.
                for (int k = 0; k < n; k++) rv[k] = vi[n - 1 - k];
                for (int k = 0; k < n; k++) rn[k] = nb[(n - 2 - k + n) % n];
                polygons[p] = rv;
                neighbours[p] = rn;
            }
        // Border edge indices follow the same reversal.
        for (int tile = 0; tile < meshes.Length; tile++)
            for (int side = 0; side < 4; side++)
                if (borders[tile, side] is { } list && flip)
                    foreach (var be in list)
                        be.Edge = (polygons[be.Polygon].Length - 2 - be.Edge + polygons[be.Polygon].Length) % polygons[be.Polygon].Length;
        foreach (var list in borders)
            if (list is not null)
                foreach (var be in list)
                {
                    var p = polygons[be.Polygon];
                    be.A = vertices[p[be.Edge]];
                    be.B = vertices[p[(be.Edge + 1) % p.Length]];
                }

        // Near-miss seams were found before the flip: their edge indices follow the same reversal.
        foreach (var (p, e, q, f) in nearSeams)
        {
            if (areas[p] == NavArea.Null || areas[q] == NavArea.Null) continue;
            int np = polygons[p].Length, nq = polygons[q].Length;
            int ep = flip ? (np - 2 - e + np) % np : e, fq = flip ? (nq - 2 - f + nq) % nq : f;
            (links[p] ??= []).Add(ep); links[p]!.Add(q);
            (links[q] ??= []).Add(fq); links[q]!.Add(p);
        }

        // Link facing border edges of neighbouring tiles where they overlap along the shared line.
        for (int tz = 0; tz < tilesPerSide; tz++)
            for (int tx = 0; tx < tilesPerSide; tx++)
            {
                int tile = tz * tilesPerSide + tx;
                if (tx + 1 < tilesPerSide) Link(borders[tile, 2], borders[tile + 1, 0], alongX: false);
                if (tz + 1 < tilesPerSide) Link(borders[tile, 1], borders[tile + tilesPerSide, 3], alongX: true);
            }

        void Link(List<BorderEdge>? left, List<BorderEdge>? right, bool alongX)
        {
            if (left is null || right is null) return;
            foreach (var a in left)
            {
                var spanA = NavGeometry.Span(a.A, a.B, alongX);
                foreach (var b in right)
                {
                    if (!NavGeometry.SpansMeet(spanA, NavGeometry.Span(b.A, b.B, alongX), climb)) continue;
                    if (areas[a.Polygon] == NavArea.Null || areas[b.Polygon] == NavArea.Null) continue;
                    // An edge can face several polygons: links, with the portal recomputed from the geometry at query time.
                    (links[a.Polygon] ??= []).Add(a.Edge); links[a.Polygon]!.Add(b.Polygon);
                    (links[b.Polygon] ??= []).Add(b.Edge); links[b.Polygon]!.Add(a.Polygon);
                }
            }
        }

        return new ZoneNavMesh
        {
            ZoneX = g.Zone.X,
            ZoneZ = g.Zone.Y,
            Vertices = [.. vertices],
            Polygons = [.. polygons],
            Neighbours = [.. neighbours],
            Links = links.Select(l => l?.ToArray()).ToArray(),
            Areas = [.. areas],
            Kept = Enumerable.Repeat(true, polygons.Count).ToArray(),
            BoundsMin = g.ZoneMin,
            BoundsMax = g.ZoneMax,
        };
    }
}
