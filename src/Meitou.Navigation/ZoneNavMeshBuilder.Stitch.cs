using System.Numerics;
using DotRecast.Recast;

namespace Meitou.Navigation;

public static partial class ZoneNavMeshBuilder
{
    // ---- stitching ----

    sealed class BorderEdge
    {
        public int Polygon, Edge;
        public Vector3 A, B;
    }

    static ZoneNavMesh Stitch(ZoneGeometry g, NavBuildSettings s, RcPolyMesh?[] meshes, int tilesPerSide, float tileWorld)
    {
        var vertices = new List<Vector3>();
        var polygons = new List<int[]>();
        var neighbours = new List<int[]>();
        var areas = new List<byte>();
        var links = new List<int>?[meshes.Sum(m => m?.npolys ?? 0)];
        // Border edges per tile and side (0: -X, 1: +Z, 2: +X, 3: -Z, as Recast numbers them).
        var borders = new List<BorderEdge>[meshes.Length, 4];

        for (int tile = 0; tile < meshes.Length; tile++)
        {
            var m = meshes[tile];
            if (m is null) continue;
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
        }

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

        // Link facing border edges of neighbouring tiles where they overlap along the shared line.
        float climb = s.MaxClimb + 0.5f;
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
