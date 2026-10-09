using System.Globalization;
using System.Numerics;

namespace Meitou.Rendering;

/// <summary>How <see cref="FoliageCardTrimmer"/> decides (docs/render-foliage.md, "Card trimming").</summary>
public sealed record FoliageCardTrimOptions
{
    /// <summary>The most vertices a trimmed card's outline may have (<c>MEITOU_TRIM_VERTICES</c>): a quad is 4 (two triangles, the same as before), 6 is four triangles.</summary>
    public int MaxVertices { get; init; } = EnvInt("MEITOU_TRIM_VERTICES", 6);
    /// <summary>What a triangle more than the card had costs, as a share of the card's area (<c>MEITOU_TRIM_TRIANGLE_COST</c>): the outline with the least area plus this per extra triangle wins.</summary>
    public float TriangleCost { get; init; } = EnvFloat("MEITOU_TRIM_TRIANGLE_COST", 0.05f);
    /// <summary>A card is trimmed only when its outline plus the extra triangles saves at least this share of its area (<c>MEITOU_TRIM_MIN_SAVING</c>).</summary>
    public float MinSaving { get; init; } = EnvFloat("MEITOU_TRIM_MIN_SAVING", 0.1f);
    /// <summary>Cells the outline is grown by on every side (<c>MEITOU_TRIM_MARGIN</c>).</summary>
    public float Margin { get; init; } = EnvFloat("MEITOU_TRIM_MARGIN", 0.25f);
    /// <summary>The size on screen, in pixels across, below which a card may lose the fringe that the coarser mips of its texture let through (<c>MEITOU_TRIM_MIN_PIXELS</c>): a card that many texels across is trimmed to the mask level that holds the mips down to that many texels per pixel, and a smaller one to a finer level.</summary>
    public float MinPixels { get; init; } = EnvFloat("MEITOU_TRIM_MIN_PIXELS", 16f);

    static int EnvInt(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 3 ? v : fallback;
    static float EnvFloat(string name, float fallback) => float.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
}

/// <summary>What a trim did to one part (areas are the sums over its triangles).</summary>
public readonly record struct FoliageCardTrimStats(int Units, int Trimmed, int Removed, int TrianglesBefore, int TrianglesAfter,
    double UvAreaBefore, double UvAreaAfter, double WorldAreaBefore, double WorldAreaAfter, int OutsideUv = 0, int TinyCards = 0)
{
    public static FoliageCardTrimStats operator +(FoliageCardTrimStats a, FoliageCardTrimStats b) => new(a.Units + b.Units, a.Trimmed + b.Trimmed, a.Removed + b.Removed,
        a.TrianglesBefore + b.TrianglesBefore, a.TrianglesAfter + b.TrianglesAfter, a.UvAreaBefore + b.UvAreaBefore, a.UvAreaAfter + b.UvAreaAfter,
        a.WorldAreaBefore + b.WorldAreaBefore, a.WorldAreaAfter + b.WorldAreaAfter, a.OutsideUv + b.OutsideUv, a.TinyCards + b.TinyCards);
}

/// <summary>
/// Cuts alpha-tested cards down to the part of their texture that is opaque. A card is a triangle, or two triangles that share an edge and map the texture, positions and
/// every other vertex attribute affinely (a leaf quad). Its outline in texture space is the convex hull of the cells of a <see cref="FoliageCardMask"/> it overlaps, simplified
/// to a few vertices by removing edges (each removal extends the neighbours, so the outline only grows), grown by a margin and clipped to the card; the vertices are
/// worked out by the card's own interpolation, so nothing but the transparent border changes.
/// </summary>
public static class FoliageCardTrimmer
{
    /// <summary>The result for a part: <see cref="Vertices"/> are the original vertices followed by the new ones, <see cref="Indices"/> the whole triangle list with the cards trimmed.</summary>
    public sealed record Result(Vertex[] Vertices, uint[] Indices, FoliageCardTrimStats Stats);

    /// <summary>Trims the cards of a part against <paramref name="mask"/>; the result holds the part's own arrays when no card gains (<c>Stats.Trimmed</c> is 0); null when the part has no texture coordinates.</summary>
    public static Result? Trim(ModelPart part, FoliageCardMask mask, FoliageCardTrimOptions options)
    {
        var verts = part.Vertices;
        var src = part.Indices;
        int triangles = src.Length / 3;
        if (triangles == 0 || !part.HasUv) return null;

        // Triangles are matched by what they look like (position and texture coordinate), not by vertex number: exporters often duplicate the corners of a quad.
        var canonical = new int[verts.Length];
        var ids = new Dictionary<(int, int, int, int, int), int>();
        for (int i = 0; i < verts.Length; i++)
        {
            ref readonly var v = ref verts[i];
            var key = ((int)MathF.Round(v.Position.X * 100), (int)MathF.Round(v.Position.Y * 100), (int)MathF.Round(v.Position.Z * 100), (int)MathF.Round(v.Uv.X * 10000), (int)MathF.Round(v.Uv.Y * 10000));
            if (!ids.TryGetValue(key, out int id)) ids[key] = id = ids.Count;
            canonical[i] = id;
        }
        var edges = new Dictionary<(int, int), (int First, int Second, int Count)>();
        for (int t = 0; t < triangles; t++)
            for (int e = 0; e < 3; e++)
            {
                int a = canonical[src[t * 3 + e]], b = canonical[src[t * 3 + (e + 1) % 3]];
                var key = a < b ? (a, b) : (b, a);
                edges[key] = edges.TryGetValue(key, out var found) ? (found.First, t, found.Count + 1) : (t, -1, 1);
            }

        var used = new bool[triangles];
        var vertices = new List<Vertex>(verts);
        var output = new List<uint>(src.Length);
        var memo = new Dictionary<Key, Vector2[]?>();
        var stats = default(FoliageCardTrimStats);
        for (int t = 0; t < triangles; t++)
        {
            if (used[t]) continue;
            used[t] = true;
            int i0 = (int)src[t * 3], i1 = (int)src[t * 3 + 1], i2 = (int)src[t * 3 + 2];
            // A partner: the triangle across one edge, when the two make a convex quad of one affine map.
            int partner = -1, opposite = -1, afterEdge = 0;
            for (int e = 0; e < 3 && partner < 0; e++)
            {
                int a = canonical[src[t * 3 + e]], b = canonical[src[t * 3 + (e + 1) % 3]];
                var key = a < b ? (a, b) : (b, a);
                if (!edges.TryGetValue(key, out var pair) || pair.Count != 2) continue;
                int u = pair.First == t ? pair.Second : pair.First;
                if (u < 0 || used[u]) continue;
                // The vertex of u that is not on the shared edge.
                int o = -1;
                for (int k = 0; k < 3; k++)
                {
                    int c = canonical[src[u * 3 + k]];
                    if (c != a && c != b) { o = (int)src[u * 3 + k]; break; }
                }
                if (o < 0 || !Consistent(verts, i0, i1, i2, (int)src[u * 3], (int)src[u * 3 + 1], (int)src[u * 3 + 2])) continue;
                if (!ConvexQuad(verts, i0, i1, i2, e, o)) continue;
                (partner, opposite, afterEdge) = (u, o, e);
            }
            if (partner >= 0) used[partner] = true;

            // The card's outline in texture space, in cells: a triangle or the quad (the vertices in order around it).
            var corners = new List<int> { i0, i1, i2 };
            if (partner >= 0) corners.Insert(afterEdge + 1, opposite);
            int originalTriangles = partner >= 0 ? 2 : 1;
            var poly = new Vector2[corners.Count];
            for (int k = 0; k < poly.Length; k++) poly[k] = new Vector2(verts[corners[k]].Uv.X * mask.Width, verts[corners[k]].Uv.Y * mask.Height);

            double uvArea = Math.Abs(SignedArea(poly));
            double worldArea = WorldArea(verts, i0, i1, i2) + (partner >= 0 ? WorldArea(verts, (int)src[partner * 3], (int)src[partner * 3 + 1], (int)src[partner * 3 + 2]) : 0);
            stats = stats with { Units = stats.Units + 1, TrianglesBefore = stats.TrianglesBefore + originalTriangles, UvAreaBefore = stats.UvAreaBefore + uvArea, WorldAreaBefore = stats.WorldAreaBefore + worldArea };

            // A card in a repeat of the texture (coordinates outside 0 to 1) is worked out in the first repeat and moved back.
            var tile = new Vector2(MathF.Floor(poly.Min(p => p.X) / mask.Width + 0.01f), MathF.Floor(poly.Min(p => p.Y) / mask.Height + 0.01f));
            for (int k = 0; k < poly.Length; k++) poly[k] -= new Vector2(tile.X * mask.Width, tile.Y * mask.Height);
            Vector2[]? outline = null;
            bool inside = true;
            foreach (var p in poly) inside &= p.X >= -0.01f * mask.Width && p.X <= 1.01f * mask.Width && p.Y >= -0.01f * mask.Height && p.Y <= 1.01f * mask.Height;
            if (!inside) stats = stats with { OutsideUv = stats.OutsideUv + 1 };
            else if (uvArea < 4) stats = stats with { TinyCards = stats.TinyCards + 1 };
            if (inside && uvArea >= 4)
            {
                int level = Math.Clamp((int)MathF.Floor(MathF.Log2(MathF.Max(MathF.Sqrt((float)uvArea) / options.MinPixels, 1))), 0, mask.LevelCount - 1);
                var key = new Key(poly, level);
                if (!memo.TryGetValue(key, out outline)) memo[key] = outline = Outline(poly, mask, level, options, originalTriangles);
            }

            if (outline is null)
            {
                output.Add((uint)i0); output.Add((uint)i1); output.Add((uint)i2);
                if (partner >= 0) for (int k = 0; k < 3; k++) output.Add(src[partner * 3 + k]);
                stats = stats with { TrianglesAfter = stats.TrianglesAfter + originalTriangles, UvAreaAfter = stats.UvAreaAfter + uvArea, WorldAreaAfter = stats.WorldAreaAfter + worldArea };
                continue;
            }
            if (outline.Length == 0)
            {
                // nothing of the card can show
                stats = stats with { Trimmed = stats.Trimmed + 1, Removed = stats.Removed + 1 };
                continue;
            }

            // New corners by the card's own interpolation (the first triangle's affine map; the quad's other corner agreed with it), in the first triangle's winding.
            var uv0 = verts[i0].Uv; var uv1 = verts[i1].Uv; var uv2 = verts[i2].Uv;
            float det = (uv1.X - uv0.X) * (uv2.Y - uv0.Y) - (uv2.X - uv0.X) * (uv1.Y - uv0.Y);
            var ring = (Vector2[])outline.Clone();
            if (Math.Sign(SignedArea(ring)) != Math.Sign(det)) Array.Reverse(ring);
            var indices = new uint[ring.Length];
            for (int k = 0; k < ring.Length; k++)
            {
                var uv = new Vector2(ring[k].X / mask.Width + tile.X, ring[k].Y / mask.Height + tile.Y);
                int same = -1;
                foreach (int c in corners)
                    if (Math.Abs(verts[c].Uv.X - uv.X) < 1e-5f && Math.Abs(verts[c].Uv.Y - uv.Y) < 1e-5f) { same = c; break; }
                if (same >= 0) { indices[k] = (uint)same; continue; }
                vertices.Add(Interpolate(verts[i0], verts[i1], verts[i2], uv, det));
                indices[k] = (uint)(vertices.Count - 1);
            }
            for (int k = 1; k + 1 < ring.Length; k++) { output.Add(indices[0]); output.Add(indices[k]); output.Add(indices[k + 1]); }
            double newUv = Math.Abs(SignedArea(ring));
            double ratio = uvArea > 0 ? newUv / uvArea : 1;
            stats = stats with { Trimmed = stats.Trimmed + 1, TrianglesAfter = stats.TrianglesAfter + ring.Length - 2, UvAreaAfter = stats.UvAreaAfter + newUv, WorldAreaAfter = stats.WorldAreaAfter + worldArea * ratio };
        }
        if (stats.Trimmed == 0) return new Result(verts, src, stats);
        return new Result([.. vertices], [.. output], stats);
    }

    // ---- cards ----

    /// <summary>Whether the three corners of the partner triangle are where the first triangle's affine map puts them, for every attribute.</summary>
    static bool Consistent(Vertex[] v, int i0, int i1, int i2, int j0, int j1, int j2)
    {
        ref readonly var a = ref v[i0]; ref readonly var b = ref v[i1]; ref readonly var c = ref v[i2];
        float det = (b.Uv.X - a.Uv.X) * (c.Uv.Y - a.Uv.Y) - (c.Uv.X - a.Uv.X) * (b.Uv.Y - a.Uv.Y);
        if (MathF.Abs(det) < 1e-9f) return false;
        float size = MathF.Max(1, (b.Position - a.Position).Length() + (c.Position - a.Position).Length());
        foreach (int j in (ReadOnlySpan<int>)[j0, j1, j2])
        {
            ref readonly var d = ref v[j];
            var x = Interpolate(a, b, c, d.Uv, det);
            if ((x.Position - d.Position).Length() > 2e-3f * size
                || (x.Normal - d.Normal).Length() > 0.02f
                || (new Vector3(x.Tangent.X, x.Tangent.Y, x.Tangent.Z) - new Vector3(d.Tangent.X, d.Tangent.Y, d.Tangent.Z)).Length() > 0.05f
                || (x.Colour - d.Colour).Length() > 0.02f) return false;
        }
        return true;
    }

    /// <summary>The quad's corners in order (the triangle's with <paramref name="o"/> after edge <paramref name="edge"/>) turn the same way at every corner.</summary>
    static bool ConvexQuad(Vertex[] v, int i0, int i1, int i2, int edge, int o)
    {
        int[] ring = [i0, i1, i2];
        var list = new List<int>(ring);
        list.Insert(edge + 1, o);
        float sign = 0;
        for (int k = 0; k < 4; k++)
        {
            var p = v[list[k]].Uv; var q = v[list[(k + 1) % 4]].Uv; var r = v[list[(k + 2) % 4]].Uv;
            float cross = (q.X - p.X) * (r.Y - q.Y) - (q.Y - p.Y) * (r.X - q.X);
            if (MathF.Abs(cross) < 1e-12f) return false;
            if (sign == 0) sign = MathF.Sign(cross);
            else if (MathF.Sign(cross) != sign) return false;
        }
        return true;
    }

    /// <summary>The vertex at texture coordinate <paramref name="uv"/> by the affine map of the triangle a, b, c (barycentric, so it also extrapolates).</summary>
    static Vertex Interpolate(in Vertex a, in Vertex b, in Vertex c, Vector2 uv, float det)
    {
        float l1 = ((uv.X - a.Uv.X) * (c.Uv.Y - a.Uv.Y) - (c.Uv.X - a.Uv.X) * (uv.Y - a.Uv.Y)) / det;
        float l2 = ((b.Uv.X - a.Uv.X) * (uv.Y - a.Uv.Y) - (uv.X - a.Uv.X) * (b.Uv.Y - a.Uv.Y)) / det;
        float l0 = 1 - l1 - l2;
        var n = a.Normal * l0 + b.Normal * l1 + c.Normal * l2;
        var t = a.Tangent * l0 + b.Tangent * l1 + c.Tangent * l2;
        var tangent = new Vector3(t.X, t.Y, t.Z);
        return new Vertex
        {
            Position = a.Position * l0 + b.Position * l1 + c.Position * l2,
            Normal = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : a.Normal,
            Uv = uv,
            Tangent = tangent.LengthSquared() > 1e-12f ? new Vector4(Vector3.Normalize(tangent), a.Tangent.W) : a.Tangent,
            Colour = a.Colour * l0 + b.Colour * l1 + c.Colour * l2,
            Bones = a.Bones,
            Weights = a.Weights,
        };
    }

    static double WorldArea(Vertex[] v, int a, int b, int c) => 0.5 * Vector3.Cross(v[b].Position - v[a].Position, v[c].Position - v[a].Position).Length();

    // ---- outlines ----

    readonly record struct Key(Vector2[] Points, int Level)
    {
        public bool Equals(Key other)
        {
            if (Level != other.Level || Points.Length != other.Points.Length) return false;
            for (int i = 0; i < Points.Length; i++) if (Points[i] != other.Points[i]) return false;
            return true;
        }

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(Level);
            foreach (var p in Points) h.Add(p);
            return h.ToHashCode();
        }
    }

    /// <summary>
    /// The outline for a card: null when it should stay as it is, an empty array when nothing of it can show, else the vertices of the convex polygon (in cells) to draw.
    /// </summary>
    public static Vector2[]? Outline(Vector2[] card, FoliageCardMask mask, int level, FoliageCardTrimOptions options, int originalTriangles)
    {
        var hull = Hull(card, mask, level, options.Margin);
        if (hull.Length == 0) return [];
        double area = Math.Abs(SignedArea(card));
        Vector2[]? best = null;
        double bestCost = double.MaxValue;
        for (int cap = 3; cap <= options.MaxVertices; cap++)
        {
            var poly = Clip(Simplify(hull, cap), card);
            if (poly.Length < 3 || poly.Length > options.MaxVertices) continue;
            double ratio = Math.Abs(SignedArea(poly)) / area;
            double cost = ratio + options.TriangleCost * (poly.Length - 2 - originalTriangles);
            if (cost < bestCost) (best, bestCost) = (poly, cost);
        }
        if (best is null) return Math.Abs(SignedArea(Clip(hull, card))) < 1e-6 ? [] : null;
        return bestCost <= 1 - options.MinSaving ? best : null;
    }

    /// <summary>
    /// The convex hull of the set cells a card overlaps, as the corners of the extreme set cell of each row it crosses (grown by <paramref name="margin"/>): the hull of all
    /// of them is the hull of the first and last cell of every row. Empty when no set cell is under the card.
    /// </summary>
    public static Vector2[] Hull(Vector2[] card, FoliageCardMask mask, int level, float margin)
    {
        float minY = card.Min(p => p.Y), maxY = card.Max(p => p.Y);
        var cells = mask.Levels[Math.Clamp(level, 0, mask.LevelCount - 1)];
        int row0 = Math.Clamp((int)MathF.Floor(minY + 1e-4f), 0, mask.Height - 1), row1 = Math.Clamp((int)MathF.Ceiling(maxY - 1e-4f) - 1, row0, mask.Height - 1);
        var points = new List<Vector2>();
        for (int r = row0; r <= row1; r++)
        {
            // The card's extent along x inside the strip r to r + 1.
            var strip = ClipHalf(ClipHalf(card, new Vector2(0, 1), -r), new Vector2(0, -1), r + 1);
            if (strip.Length == 0) continue;
            float minX = strip.Min(p => p.X), maxX = strip.Max(p => p.X);
            int c0 = Math.Clamp((int)MathF.Floor(minX + 1e-4f), 0, mask.Width - 1), c1 = Math.Clamp((int)MathF.Ceiling(maxX - 1e-4f) - 1, c0, mask.Width - 1);
            int first = -1, last = -1;
            for (int c = c0; c <= c1; c++)
                if (cells[r * mask.Width + c]) { if (first < 0) first = c; last = c; }
            if (first < 0) continue;
            foreach (float x in (ReadOnlySpan<float>)[first - margin, last + 1 + margin])
                foreach (float y in (ReadOnlySpan<float>)[r - margin, r + 1 + margin]) points.Add(new Vector2(x, y));
        }
        return points.Count == 0 ? [] : ConvexHull(points);
    }

    /// <summary>Andrew's monotone chain; counter-clockwise in a y-up frame (clockwise on the screen), collinear points dropped.</summary>
    public static Vector2[] ConvexHull(List<Vector2> points)
    {
        var p = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return [.. p];
        var h = new List<Vector2>();
        foreach (var q in p)
        {
            while (h.Count >= 2 && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(q);
        }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--)
        {
            while (h.Count >= lower && Cross(h[^2], h[^1], p[i]) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(p[i]);
        }
        h.RemoveAt(h.Count - 1);
        return [.. h];
    }

    static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>
    /// A convex polygon (counter-clockwise, <see cref="ConvexHull"/>'s order) reduced to at most <paramref name="cap"/> vertices by repeatedly removing the edge whose
    /// removal adds the least area: the two edges next to it are extended until they meet, so the result contains the polygon. Stops early when no edge's neighbours meet.
    /// </summary>
    public static Vector2[] Simplify(Vector2[] polygon, int cap)
    {
        var p = new List<Vector2>(polygon);
        while (p.Count > Math.Max(cap, 3))
        {
            int n = p.Count, bestEdge = -1;
            double bestArea = double.MaxValue;
            Vector2 bestPoint = default;
            for (int i = 0; i < n; i++)
            {
                var a = p[(i + n - 1) % n]; var b = p[i]; var c = p[(i + 1) % n]; var d = p[(i + 2) % n];
                // b + t (b - a) = c + s (c - d), t and s >= 0.
                var d1 = b - a; var d2 = c - d;
                float denom = d1.X * d2.Y - d1.Y * d2.X;
                if (MathF.Abs(denom) < 1e-9f) continue;
                var w = c - b;
                float t = (w.X * d2.Y - w.Y * d2.X) / denom, s = (w.X * d1.Y - w.Y * d1.X) / denom;
                if (t < 0 || s < 0) continue;
                var x = b + d1 * t;
                double added = 0.5 * Math.Abs((x.X - b.X) * (c.Y - b.Y) - (x.Y - b.Y) * (c.X - b.X));
                if (added < bestArea) (bestEdge, bestArea, bestPoint) = (i, added, x);
            }
            if (bestEdge < 0) break;
            int j = (bestEdge + 1) % n;
            p[bestEdge] = bestPoint;
            p.RemoveAt(j);
        }
        return [.. p];
    }

    /// <summary>The part of <paramref name="polygon"/> inside the convex <paramref name="card"/> (either winding); empty when they do not overlap.</summary>
    public static Vector2[] Clip(Vector2[] polygon, Vector2[] card)
    {
        var result = polygon;
        float sign = SignedArea(card) >= 0 ? 1 : -1;
        for (int i = 0; i < card.Length && result.Length > 0; i++)
        {
            var a = card[i]; var b = card[(i + 1) % card.Length];
            // Inside is where sign * cross(b - a, p - a) >= 0.
            var edge = b - a;
            var normal = new Vector2(-edge.Y, edge.X) * sign;   // points inside
            float offset = -Vector2.Dot(normal, a);
            result = ClipHalf(result, normal, offset);
        }
        return result;
    }

    /// <summary>Keeps the part of a polygon where <c>dot(normal, p) + offset &gt;= 0</c> (Sutherland-Hodgman).</summary>
    static Vector2[] ClipHalf(Vector2[] polygon, Vector2 normal, float offset)
    {
        if (polygon.Length == 0) return polygon;
        var result = new List<Vector2>(polygon.Length + 2);
        for (int i = 0; i < polygon.Length; i++)
        {
            var p = polygon[i]; var q = polygon[(i + 1) % polygon.Length];
            float dp = Vector2.Dot(normal, p) + offset, dq = Vector2.Dot(normal, q) + offset;
            if (dp >= 0) result.Add(p);
            if ((dp >= 0) != (dq >= 0)) result.Add(p + (q - p) * (dp / (dp - dq)));
        }
        // Points that coincide (a clip through a corner) are one vertex.
        for (int i = result.Count - 1; i >= 0 && result.Count > 1; i--)
            if ((result[i] - result[(i + 1) % result.Count]).LengthSquared() < 1e-10f && result.Count > 1) result.RemoveAt(i);
        return [.. result];
    }

    /// <summary>The signed area (positive when the vertices go counter-clockwise in a y-up frame).</summary>
    public static float SignedArea(Vector2[] p)
    {
        double a = 0;
        for (int i = 0; i < p.Length; i++) a += (double)p[i].X * p[(i + 1) % p.Length].Y - (double)p[(i + 1) % p.Length].X * p[i].Y;
        return (float)(a / 2);
    }
}

/// <summary>Trimming a decoded model's parts against the alpha of the normal map its material cuts out on.</summary>
public static class FoliageCardTrimming
{
    /// <summary>Whether foliage meshes are trimmed at all; <c>MEITOU_CARD_TRIM=0</c> leaves them as they are (and the <c>card-trim</c> switch has nothing to flip).</summary>
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("MEITOU_CARD_TRIM") != "0";

    /// <summary>
    /// Gives every part of <paramref name="model"/> its <see cref="ModelPart.TrimmedIndices"/> (the parts are replaced in the model's list; its bounds stay the original's).
    /// <paramref name="normalPath"/> is the resolved file of the normal map, <paramref name="threshold"/> the cut-out threshold (0-1). The sum of what was done.
    /// </summary>
    public static FoliageCardTrimStats Apply(Model model, string? normalPath, float threshold, FoliageCardTrimOptions? options = null)
    {
        var total = default(FoliageCardTrimStats);
        if (normalPath is null || !(threshold > 0) || FoliageCardMask.Obtain(normalPath, threshold) is not { } mask) return total;
        options ??= new FoliageCardTrimOptions();
        for (int i = 0; i < model.Parts.Count; i++)
        {
            var part = model.Parts[i];
            if (part.Skinned || part.Indices.Length == 0) continue;
            if (FoliageCardTrimmer.Trim(part, mask, options) is not { } result) continue;
            total += result.Stats;
            if (result.Stats.Trimmed == 0) continue;
            model.Parts[i] = new ModelPart
            {
                SubMeshIndex = part.SubMeshIndex, MaterialName = part.MaterialName, Vertices = result.Vertices, Indices = part.Indices, HasUv = part.HasUv,
                HasTangents = part.HasTangents, HasColours = part.HasColours, Skinned = part.Skinned, TrimmedIndices = result.Indices,
            };
        }
        return total;
    }
}
