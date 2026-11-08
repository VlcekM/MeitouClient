using System.Numerics;

namespace Meitou.Rendering.Characters;

/// <summary>
/// The cap over a headless body's neck (Meitou decapitation, docs/game/character-stats.md "Decapitation (Meitou)"): no race has a stump mesh for the head, so it is made
/// from the body. The body's visible triangles are those with no head vertex (<see cref="CharacterContent.HeadBit"/>, hidden by the shader); each of their edges that a
/// hidden triangle shares is on the rim of the hole. Each connected rim is fanned to its centre, every triangle wound against the body triangle beside it, so the cap
/// faces out as the surface around it does. The rim's vertices keep their skinning and face-morph slot, so the cap follows the neck exactly; the centre takes the rim's
/// weights summed (the four largest). Its texture coordinates are a planar projection, (0, 0) at the centre and length 1 at the farthest rim point.
/// </summary>
internal static class NeckCap
{
    /// <summary>The cap of <paramref name="body"/> (its vertices marked by <c>CharacterContent.MarkBody</c>), one skinned part; null when the body has no head vertices.
    /// <paramref name="head"/>: the cut-off head's cap instead, over the rim of its triangles that have only head vertices (the triangles across the cut are drawn by neither side).</summary>
    public static ModelPart? Build(Model body, bool head = false)
    {
        var all = new List<Vertex>();
        var visibleEdges = new List<(int A, int B)>();
        var hiddenEdges = new HashSet<(Vector3, Vector3)>();
        foreach (var part in body.Parts)
        {
            if (!part.Skinned) continue;
            int first = all.Count;
            all.AddRange(part.Vertices);
            var idx = part.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = first + (int)idx[t], b = first + (int)idx[t + 1], c = first + (int)idx[t + 2];
                bool hidden = head ? !(IsHead(all[a]) && IsHead(all[b]) && IsHead(all[c])) : IsHead(all[a]) || IsHead(all[b]) || IsHead(all[c]);
                foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                    if (hidden) hiddenEdges.Add(Key(all[p].Position, all[q].Position));
                    else visibleEdges.Add((p, q));
            }
        }
        var rim = visibleEdges.Where(e => hiddenEdges.Contains(Key(all[e.A].Position, all[e.B].Position))).ToList();
        if (rim.Count < 3) return null;

        // The rims: edges joined where they share a point.
        var points = new Dictionary<Vector3, int>();
        int Point(Vector3 p) { var k = Round(p); if (!points.TryGetValue(k, out int i)) points[k] = i = points.Count; return i; }
        var parent = new List<int>();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        foreach (var (a, b) in rim)
        {
            int pa = Point(all[a].Position), pb = Point(all[b].Position);
            while (parent.Count < points.Count) parent.Add(parent.Count);
            parent[Find(pa)] = Find(pb);
        }

        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        foreach (var group in rim.GroupBy(e => Find(Point(all[e.A].Position))))
        {
            var edges = group.ToList();
            if (edges.Count < 3) continue;
            var centre = Vector3.Zero;
            foreach (var (a, _) in edges) centre += all[a].Position;
            centre /= edges.Count;
            // The cap's normal: the fan's triangles (b, a, centre) summed, which face out as the body beside them does.
            var normal = Vector3.Zero;
            foreach (var (a, b) in edges) normal += Vector3.Cross(all[a].Position - all[b].Position, centre - all[b].Position);
            if (normal.LengthSquared() < 1e-12f) continue;
            normal = Vector3.Normalize(normal);
            var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, normal));
            var w = Vector3.Cross(normal, u);
            float reach = edges.Max(e => (all[e.A].Position - centre).Length());
            if (reach < 1e-4f) continue;
            Vertex Cap(Vertex source, Vector3 at)
            {
                var d = (at - centre) / reach;
                return source with { Position = at, Normal = normal, Uv = new Vector2(Vector3.Dot(d, u), Vector3.Dot(d, w)), Tangent = new Vector4(u, 1), Colour = source.Colour with { Y = 0, Z = 0 } };
            }
            uint centreIndex = (uint)vertices.Count;
            vertices.Add(Cap(default, centre));
            var weights = new Dictionary<uint, float>();
            var own = new Dictionary<Vector3, uint>();
            uint RimVertex(int i)
            {
                var k = Round(all[i].Position);
                if (own.TryGetValue(k, out uint at)) return at;
                own[k] = at = (uint)vertices.Count;
                vertices.Add(Cap(all[i], all[i].Position));
                for (int j = 0; j < 4; j++)
                {
                    float wt = j switch { 0 => all[i].Weights.X, 1 => all[i].Weights.Y, 2 => all[i].Weights.Z, _ => all[i].Weights.W };
                    uint bone = (all[i].Bones >> (8 * j)) & 0xFF;
                    if (wt > 0) weights[bone] = weights.GetValueOrDefault(bone) + wt;
                }
                return at;
            }
            foreach (var (a, b) in edges)
            {
                uint ia = RimVertex(a), ib = RimVertex(b);
                indices.Add(ib); indices.Add(ia); indices.Add(centreIndex);
            }
            // The centre: the rim's bones, the four heaviest, normalised.
            var top = weights.OrderByDescending(x => x.Value).Take(4).ToList();
            float sum = top.Sum(x => x.Value);
            uint bones = 0;
            var wv = new float[4];
            for (int j = 0; j < top.Count; j++) { bones |= top[j].Key << (8 * j); wv[j] = top[j].Value / sum; }
            vertices[(int)centreIndex] = vertices[(int)centreIndex] with { Bones = bones, Weights = new Vector4(wv[0], wv[1], wv[2], wv[3]) };
        }
        if (indices.Count == 0) return null;
        return new ModelPart
        {
            SubMeshIndex = 0, MaterialName = "neck cap", Vertices = [.. vertices], Indices = [.. indices], HasUv = true, HasTangents = true, Skinned = true,
        };
    }

    static bool IsHead(in Vertex v) => ((int)(v.Colour.Y + 0.5f) & CharacterContent.HeadBit) != 0;

    static Vector3 Round(Vector3 p) => new(MathF.Round(p.X, 3), MathF.Round(p.Y, 3), MathF.Round(p.Z, 3));

    /// <summary>An undirected edge by its ends' positions (a UV seam splits vertices, not the surface).</summary>
    static (Vector3, Vector3) Key(Vector3 a, Vector3 b)
    {
        a = Round(a); b = Round(b);
        return a.X < b.X || a.X == b.X && (a.Y < b.Y || a.Y == b.Y && a.Z <= b.Z) ? (a, b) : (b, a);
    }
}
