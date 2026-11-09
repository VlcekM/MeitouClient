using System.Numerics;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// Reduces a triangle list by edge collapses chosen by quadric error, for the mesh levels a character mesh does not ship with
/// (docs/character-renderer.md, "Generated LOD"). It only ever re-points triangle corners at vertices the part already has, so the
/// vertex buffer, the skinning weights and every level share one upload. Vertices that sit at one position (UV or normal seams) are one
/// point for the topology; a corner moved onto a seamed point takes the vertex of that point with the nearest UV and normal.
/// </summary>
internal static class MeshSimplifier
{
    /// <summary>With the texture guard: the most a corner's texture coordinate may differ from what its triangle's mapping gives at the corner's new position (texture units).</summary>
    const float UvTolerance = 0.01f;

    /// <summary>
    /// The triangle lists after reducing to each of <paramref name="fractions"/> of the input triangles (descending), in one run: a list is a
    /// snapshot, so each is the previous one reduced further. Stops early at <paramref name="floor"/> triangles (the remaining lists then repeat the
    /// last). With <paramref name="uvPerWorld"/> above 0 (a switch; <see cref="UvPerWorld"/> gives the part's value) a collapse is refused when a
    /// corner it moves has no vertex at the new point whose texture coordinate is within <see cref="UvTolerance"/> of what the corner's triangle
    /// mapping gives there, and the corner takes the best such vertex: a texture would show a jump to another UV island, or a stretched mapping, as a
    /// smear.
    /// </summary>
    public static uint[][] Chain(Vertex[] vertices, uint[] indices, float[] fractions, int floor, float uvPerWorld = 0)
    {
        int triCount = indices.Length / 3;
        var result = new uint[fractions.Length][];
        if (triCount == 0) { Array.Fill(result, indices); return result; }

        // Topology on welded points.
        var groupOf = new int[vertices.Length];
        var points = new Dictionary<Vector3, int>();
        var groupVertices = new List<List<int>>();
        for (int v = 0; v < vertices.Length; v++)
        {
            if (!points.TryGetValue(vertices[v].Position,
                    out int g)) { points[vertices[v].Position] = g = groupVertices.Count; groupVertices.Add([]); }
            groupOf[v] = g;
            groupVertices[g].Add(v);
        }
        int groups = groupVertices.Count;
        var tg = new int[triCount * 3];
        var tv = new int[triCount * 3];
        int[]? cv = null;
        var alive = new bool[triCount];
        var position = new Vector3[groups];
        for (int g = 0; g < groups; g++) position[g] = vertices[groupVertices[g][0]].Position;
        var incident = new List<int>[groups];
        for (int g = 0; g < groups; g++) incident[g] = [];
        int live = 0;
        for (int t = 0; t < triCount; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                int v = (int)indices[t * 3 + k];
                tv[t * 3 + k] = v;
                tg[t * 3 + k] = groupOf[v];
            }
            if (tg[t * 3] == tg[t * 3 + 1] || tg[t * 3 + 1] == tg[t * 3 + 2] || tg[t * 3] == tg[t * 3 + 2]) continue;
            alive[t] = true;
            live++;
            for (int k = 0; k < 3; k++) incident[tg[t * 3 + k]].Add(t);
        }

        if (uvPerWorld > 0) cv = (int[])tv.Clone();

        // Quadrics: the planes of the triangles at each point, and heavy planes along open edges (silhouettes and cut-outs).
        var q = new Quadric[groups];
        var edgeUse = new Dictionary<(int, int), int>();
        for (int t = 0; t < triCount; t++)
        {
            if (!alive[t]) continue;
            var (a, b, c) = (position[tg[t * 3]], position[tg[t * 3 + 1]], position[tg[t * 3 + 2]]);
            var n = Vector3.Cross(b - a, c - a);
            float area2 = n.Length();
            if (area2 < 1e-12f) continue;
            n /= area2;
            var plane = Quadric.Plane(n, -Vector3.Dot(n, a), area2 * 0.5f);
            for (int k = 0; k < 3; k++) q[tg[t * 3 + k]].Add(plane);
            for (int k = 0; k < 3; k++)
            {
                int x = tg[t * 3 + k], y = tg[t * 3 + (k + 1) % 3];
                var key = x < y ? (x, y) : (y, x);
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
            }
        }
        for (int t = 0; t < triCount; t++)
        {
            if (!alive[t]) continue;
            var (a, b, c) = (position[tg[t * 3]], position[tg[t * 3 + 1]], position[tg[t * 3 + 2]]);
            var cross = Vector3.Cross(b - a, c - a);
            if (cross.LengthSquared() < 1e-18f) continue;
            var n = Vector3.Normalize(cross);
            for (int k = 0; k < 3; k++)
            {
                int x = tg[t * 3 + k], y = tg[t * 3 + (k + 1) % 3];
                if (edgeUse[x < y ? (x, y) : (y, x)] != 1) continue;
                var e = position[y] - position[x];
                var side = Vector3.Cross(e, n);
                if (side.LengthSquared() < 1e-18f) continue;
                side = Vector3.Normalize(side);
                var plane = Quadric.Plane(side, -Vector3.Dot(side, position[x]), 20 * e.LengthSquared());
                q[x].Add(plane);
                q[y].Add(plane);
            }
        }

        var version = new int[groups];
        var valid = new bool[groups];
        Array.Fill(valid, true);
        var queue = new PriorityQueue<(int From, int To, int VersionFrom, int VersionTo), double>();

        double Cost(int from, int to)
        {
            var sum = q[from];
            sum.Add(q[to]);
            double cost = sum.Evaluate(position[to]);

            // A collapse across different skinning weights stretches the surface once posed: charged like a displacement of that size.
            float stretch = SkinDifference(vertices[groupVertices[from][0]], vertices[groupVertices[to][0]]);
            return cost + stretch * (position[from] - position[to]).LengthSquared() * 0.5;
        }

        void Push(int a, int b)
        {
            double forward = Cost(a, b), backward = Cost(b, a);
            if (forward <= backward) queue.Enqueue((a, b, version[a], version[b]), forward);
            else queue.Enqueue((b, a, version[b], version[a]), backward);
        }

        int moving = -1;

        // A collapse is refused when it would flip or crush a surviving triangle.
        bool CanCollapse(int from, int to)
        {
            moving = from;
            foreach (int t in incident[from])
            {
                if (!alive[t]) continue;
                bool both = false;
                for (int k = 0; k < 3; k++) if (tg[t * 3 + k] == to) both = true;
                if (both) continue;
                if (cv is not null && !UvHolds(t, to)) return false;
                Vector3 p0 = position[tg[t * 3]], p1 = position[tg[t * 3 + 1]], p2 = position[tg[t * 3 + 2]];
                Vector3 m0 = tg[t * 3] == from ? position[to] : p0, m1 = tg[t * 3 + 1] == from ? position[to] : p1, m2 = tg[t * 3 + 2] == from ? position[to] : p2;
                var before = Vector3.Cross(p1 - p0, p2 - p0);
                var after = Vector3.Cross(m1 - m0, m2 - m0);
                if (after.LengthSquared() < 1e-14f || before.LengthSquared() < 1e-14f) continue;
                if (Vector3.Dot(Vector3.Normalize(before), Vector3.Normalize(after)) < 0.25f) return false;
            }
            return true;
        }

        // The texture guard: a corner moved onto point `to` takes the vertex there whose texture coordinate best matches what the triangle's own mapping
        // (extended over its plane) gives at that position, and the collapse is refused when even that one is further off than UvTolerance.
        (int Vertex, float Error) MoveCorner(int t, int k, int to)
        {
            var p0 = position[tg[t * 3]];
            var e1 = position[tg[t * 3 + 1]] - p0;
            var e2 = position[tg[t * 3 + 2]] - p0;
            var q = position[to] - p0;
            float d00 = Vector3.Dot(e1, e1), d01 = Vector3.Dot(e1, e2), d11 = Vector3.Dot(e2, e2);
            float den = d00 * d11 - d01 * d01;
            var own = vertices[cv![t * 3 + k]];
            var expected = own.Uv;
            if (den > 1e-20f * Math.Max(1f, d00 * d11))
            {
                float d20 = Vector3.Dot(q, e1), d21 = Vector3.Dot(q, e2);
                float s = (d11 * d20 - d01 * d21) / den, u = (d00 * d21 - d01 * d20) / den;
                var uv0 = vertices[cv[t * 3]].Uv;
                expected = uv0 + s * (vertices[cv[t * 3 + 1]].Uv - uv0) + u * (vertices[cv[t * 3 + 2]].Uv - uv0);
            }
            int best = groupVertices[to][0];
            float bestKey = float.MaxValue, bestError = float.MaxValue;
            foreach (int c in groupVertices[to])
            {
                float error = Vector2.Distance(vertices[c].Uv, expected);
                float key = (error > UvTolerance ? 1000f + error : 0f) + Vector3.DistanceSquared(vertices[c].Normal, own.Normal);
                if (key < bestKey) { bestKey = key; best = c; bestError = error; }
            }
            return (best, bestError);
        }

        bool UvHolds(int t, int to)
        {
            for (int k = 0; k < 3; k++)
                if (tg[t * 3 + k] != to && alive[t] && IsFrom(t, k) && MoveCorner(t, k, to).Error > UvTolerance) return false;
            return true;
        }

        bool IsFrom(int t, int k) => tg[t * 3 + k] == moving;

        // With the texture guard (buildings: front and back of a sheet sit on one point with the same UV and opposite normals) the normal counts as
        // much as the UV.
        float normalWeight = uvPerWorld > 0 ? 1f : 0.05f;
        int Nearest(in Vertex like, List<int> candidates)
        {
            int best = candidates[0];
            float bestScore = float.MaxValue;
            foreach (int c in candidates)
            {
                float score = Vector2.DistanceSquared(vertices[c].Uv, like.Uv) + normalWeight * Vector3.DistanceSquared(vertices[c].Normal,
                    like.Normal);
                if (score < bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        uint[] Snapshot()
        {
            var list = new List<uint>(live * 3);
            var pick = new Dictionary<(int Vertex, int Group), int>();
            for (int t = 0; t < triCount; t++)
            {
                if (!alive[t]) continue;
                int c0 = 0, c1 = 0, c2 = 0;
                for (int k = 0; k < 3; k++)
                {
                    int g = tg[t * 3 + k], v = cv is null ? tv[t * 3 + k] : cv[t * 3 + k];
                    if (cv is null && groupOf[v] != g)
                    {
                        if (!pick.TryGetValue((v, g), out int chosen))
                            pick[(v, g)] = chosen = Nearest(vertices[v], groupVertices[g]);
                        v = chosen;
                    }
                    if (k == 0) c0 = v; else if (k == 1) c1 = v; else c2 = v;
                }
                if (c0 == c1 || c1 == c2 || c0 == c2) continue;
                list.Add((uint)c0);
                list.Add((uint)c1);
                list.Add((uint)c2);
            }
            return [.. list];
        }

        var neighbours = new HashSet<int>();
        for (int g = 0; g < groups; g++)
        {
            neighbours.Clear();
            foreach (int t in incident[g])
                for (int k = 0; k < 3; k++) if (tg[t * 3 + k] > g) neighbours.Add(tg[t * 3 + k]);
            foreach (int n in neighbours) Push(g, n);
        }

        int next = 0;
        int TargetFor(int i) => Math.Max(floor, (int)(triCount * fractions[i]));
        while (next < fractions.Length)
        {
            while (next < fractions.Length && live <= TargetFor(next)) result[next++] = Snapshot();
            if (next >= fractions.Length || queue.Count == 0) break;
            var (from, to, vf, vt) = queue.Dequeue();
            if (!valid[from] || !valid[to] || version[from] != vf || version[to] != vt) continue;
            if (!CanCollapse(from, to)) continue;

            // Move every triangle of `from` to `to`; the ones holding both disappear.
            foreach (int t in incident[from])
            {
                if (!alive[t]) continue;
                bool both = false;
                for (int k = 0; k < 3; k++) if (tg[t * 3 + k] == to) both = true;
                if (both) { alive[t] = false; live--; continue; }
                for (int k = 0; k < 3; k++)
                {
                    if (tg[t * 3 + k] != from) continue;
                    if (cv is not null) { moving = from; cv[t * 3 + k] = MoveCorner(t, k, to).Vertex; }
                    tg[t * 3 + k] = to;
                }
                incident[to].Add(t);
            }
            incident[from] = [];
            valid[from] = false;
            q[to].Add(q[from]);
            version[to]++;
            incident[to].RemoveAll(t => !alive[t]);
            neighbours.Clear();
            foreach (int t in incident[to])
                for (int k = 0; k < 3; k++) if (tg[t * 3 + k] != to) neighbours.Add(tg[t * 3 + k]);
            foreach (int n in neighbours) Push(to, n);
        }
        while (next < fractions.Length) result[next++] = Snapshot();
        return result;
    }

    /// <summary>
    /// Texture-coordinate units per world unit over a triangle list: the square root of the summed texture-space area over the summed surface area; 0
    /// for a list without either.
    /// </summary>
    public static float UvPerWorld(Vertex[] vertices, uint[] indices)
    {
        double uv = 0, world = 0;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            ref readonly var a = ref vertices[indices[t]];
            ref readonly var b = ref vertices[indices[t + 1]];
            ref readonly var c = ref vertices[indices[t + 2]];
            world += Vector3.Cross(b.Position - a.Position, c.Position - a.Position).Length() * 0.5;
            var u = b.Uv - a.Uv;
            var v = c.Uv - a.Uv;
            uv += Math.Abs(u.X * v.Y - u.Y * v.X) * 0.5;
        }
        return world > 1e-12 && uv > 0 ? (float)Math.Sqrt(uv / world) : 0;
    }

    /// <summary>
    /// How different two vertices' skinning is, 0 (same bones and weights) to 2.
    /// </summary>
    static float SkinDifference(in Vertex a, in Vertex b)
    {
        float diff = 0;
        for (int i = 0; i < 4; i++)
        {
            uint bone = (a.Bones >> (8 * i)) & 0xff;
            float wa = Weight(a.Weights, i), wb = 0;
            for (int j = 0; j < 4; j++) if (((b.Bones >> (8 * j)) & 0xff) == bone) wb += Weight(b.Weights, j);
            diff += MathF.Abs(wa - wb);
        }
        return diff;
    }

    static float Weight(Vector4 w, int i) => i switch { 0 => w.X, 1 => w.Y, 2 => w.Z, _ => w.W };

    /// <summary>
    /// The symmetric 4x4 error matrix of a set of weighted planes.
    /// </summary>
    struct Quadric
    {
        double _a, _b, _c, _d, _e, _f, _g, _h, _i, _j;

        public static Quadric Plane(Vector3 n, float offset, float weight)
        {
            double x = n.X, y = n.Y, z = n.Z, o = offset, w = weight;
            return new Quadric { _a = w * x * x, _b = w * x * y, _c = w * x * z, _d = w * x * o, _e = w * y * y, _f = w * y * z, _g = w * y * o,
                _h = w * z * z, _i = w * z * o, _j = w * o * o };
        }

        public void Add(in Quadric o)
        {
            _a += o._a;
            _b += o._b;
            _c += o._c;
            _d += o._d;
            _e += o._e;
            _f += o._f;
            _g += o._g;
            _h += o._h;
            _i += o._i;
            _j += o._j;
        }

        public readonly double Evaluate(Vector3 p)
        {
            double x = p.X, y = p.Y, z = p.Z;
            return _a * x * x + 2 * _b * x * y + 2 * _c * x * z + 2 * _d * x + _e * y * y + 2 * _f * y * z + 2 * _g * y + _h * z * z + 2 * _i * z + _j;
        }
    }
}