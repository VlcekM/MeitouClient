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
    /// <summary>
    /// The triangle lists after reducing to each of <paramref name="fractions"/> of the input triangles (descending), in one run: a list is a
    /// snapshot, so each is the previous one reduced further. Stops early at <paramref name="floor"/> triangles (the remaining lists then repeat the
    /// last). With <paramref name="uvPerWorld"/> above 0 (texture-coordinate units per world unit of the part's surface, <see cref="UvPerWorld"/>) a
    /// collapse is refused when a corner it moves has no vertex at the new point whose texture coordinate is about as far from its own as the move is
    /// long: that is a jump to another UV island, which a texture would show as a smear.
    /// </summary>
    public static uint[][] Chain(Vertex[] vertices, uint[] indices, float[] fractions, int floor, float uvPerWorld = 0)
    {
        int triCount = indices.Length / 3;
        var result = new uint[fractions.Length][];
        if (triCount == 0)
        {
            Array.Fill(result, indices);
            return result;
        }

        var run = new Collapses(vertices, indices, uvPerWorld);
        run.AddPlanes();
        run.AddOpenEdges();
        run.QueueEdges();
        run.Reduce(result, fractions, floor);
        return result;
    }

    /// <summary>
    /// One run of <see cref="Chain"/>: the welded topology, the points' quadrics, the queue of candidate collapses and the collapses done.
    /// </summary>
    sealed class Collapses
    {
        #region Topology

        /// <summary>
        /// The part's vertices.
        /// </summary>
        readonly Vertex[] _vertices;

        /// <summary>
        /// Texture-coordinate units per world unit of the surface; above 0 turns the UV guard on.
        /// </summary>
        readonly float _uvPerWorld;

        /// <summary>
        /// The input's triangle count.
        /// </summary>
        readonly int _triCount;

        /// <summary>
        /// Each vertex's welded point.
        /// </summary>
        readonly int[] _groupOf;

        /// <summary>
        /// Each point's vertices.
        /// </summary>
        readonly List<List<int>> _groupVertices = [];

        /// <summary>
        /// The point count.
        /// </summary>
        readonly int _groups;

        /// <summary>
        /// Each triangle corner's point (moved by the collapses).
        /// </summary>
        readonly int[] _tg;

        /// <summary>
        /// Each triangle corner's input vertex.
        /// </summary>
        readonly int[] _tv;

        /// <summary>
        /// Whether a triangle survives (degenerate ones on welded points never do).
        /// </summary>
        readonly bool[] _alive;

        /// <summary>
        /// Each point's position.
        /// </summary>
        readonly Vector3[] _position;

        /// <summary>
        /// The triangles at each point (dead ones are pruned lazily).
        /// </summary>
        readonly List<int>[] _incident;

        /// <summary>
        /// The surviving triangle count.
        /// </summary>
        int _live;

        /// <summary>
        /// Topology on welded points.
        /// </summary>
        public Collapses(Vertex[] vertices, uint[] indices, float uvPerWorld)
        {
            _vertices = vertices;
            _uvPerWorld = uvPerWorld;
            _triCount = indices.Length / 3;
            _groupOf = new int[vertices.Length];
            var points = new Dictionary<Vector3, int>();
            for (int v = 0; v < vertices.Length; v++)
            {
                if (!points.TryGetValue(vertices[v].Position, out int g))
                {
                    points[vertices[v].Position] = g = _groupVertices.Count;
                    _groupVertices.Add([]);
                }

                _groupOf[v] = g;
                _groupVertices[g].Add(v);
            }

            _groups = _groupVertices.Count;
            _tg = new int[_triCount * 3];
            _tv = new int[_triCount * 3];
            _alive = new bool[_triCount];
            _position = new Vector3[_groups];
            for (int g = 0; g < _groups; g++) _position[g] = vertices[_groupVertices[g][0]].Position;
            _incident = new List<int>[_groups];
            for (int g = 0; g < _groups; g++) _incident[g] = [];
            AddTriangles(indices);
            _q = new Quadric[_groups];
            _version = new int[_groups];
            _valid = new bool[_groups];
            Array.Fill(_valid, true);
            _normalWeight = uvPerWorld > 0 ? 1f : 0.05f;
        }

        /// <summary>
        /// The triangles' corners on their points; a triangle with two corners on one point is dead from the start.
        /// </summary>
        void AddTriangles(uint[] indices)
        {
            for (int t = 0; t < _triCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int v = (int)indices[t * 3 + k];
                    _tv[t * 3 + k] = v;
                    _tg[t * 3 + k] = _groupOf[v];
                }

                if (_tg[t * 3] == _tg[t * 3 + 1] || _tg[t * 3 + 1] == _tg[t * 3 + 2] || _tg[t * 3] == _tg[t * 3 + 2]) continue;
                _alive[t] = true;
                _live++;
                for (int k = 0; k < 3; k++) _incident[_tg[t * 3 + k]].Add(t);
            }
        }

        #endregion

        #region Quadrics

        /// <summary>
        /// Each point's error quadric.
        /// </summary>
        readonly Quadric[] _q;

        /// <summary>
        /// How many surviving triangles use each edge (point pair, lower first).
        /// </summary>
        readonly Dictionary<(int, int), int> _edgeUse = [];

        /// <summary>
        /// Quadrics: the planes of the triangles at each point, and heavy planes along open edges (silhouettes and cut-outs).
        /// </summary>
        public void AddPlanes()
        {
            for (int t = 0; t < _triCount; t++)
            {
                if (!_alive[t]) continue;
                var (a, b, c) = (_position[_tg[t * 3]], _position[_tg[t * 3 + 1]], _position[_tg[t * 3 + 2]]);
                var n = Vector3.Cross(b - a, c - a);
                float area2 = n.Length();
                if (area2 < 1e-12f) continue;
                n /= area2;
                var plane = Quadric.Plane(n, -Vector3.Dot(n, a), area2 * 0.5f);
                for (int k = 0; k < 3; k++) _q[_tg[t * 3 + k]].Add(plane);
                for (int k = 0; k < 3; k++)
                {
                    int x = _tg[t * 3 + k], y = _tg[t * 3 + (k + 1) % 3];
                    var key = x < y ? (x, y) : (y, x);
                    _edgeUse[key] = _edgeUse.GetValueOrDefault(key) + 1;
                }
            }
        }

        /// <summary>
        /// The heavy planes along the open edges (used by one triangle), standing on the edge across the triangle's plane.
        /// </summary>
        public void AddOpenEdges()
        {
            for (int t = 0; t < _triCount; t++)
            {
                if (!_alive[t]) continue;
                var (a, b, c) = (_position[_tg[t * 3]], _position[_tg[t * 3 + 1]], _position[_tg[t * 3 + 2]]);
                var cross = Vector3.Cross(b - a, c - a);
                if (cross.LengthSquared() < 1e-18f) continue;
                var n = Vector3.Normalize(cross);
                for (int k = 0; k < 3; k++)
                {
                    int x = _tg[t * 3 + k], y = _tg[t * 3 + (k + 1) % 3];
                    if (_edgeUse[x < y ? (x, y) : (y, x)] != 1) continue;
                    var e = _position[y] - _position[x];
                    var side = Vector3.Cross(e, n);
                    if (side.LengthSquared() < 1e-18f) continue;
                    side = Vector3.Normalize(side);
                    var plane = Quadric.Plane(side, -Vector3.Dot(side, _position[x]), 20 * e.LengthSquared());
                    _q[x].Add(plane);
                    _q[y].Add(plane);
                }
            }
        }

        #endregion

        #region Queue

        /// <summary>
        /// Each point's change count: a queued collapse made before the last change is stale.
        /// </summary>
        readonly int[] _version;

        /// <summary>
        /// Whether a point still exists (not collapsed into another).
        /// </summary>
        readonly bool[] _valid;

        /// <summary>
        /// The candidate collapses by cost.
        /// </summary>
        readonly PriorityQueue<(int From, int To, int VersionFrom, int VersionTo), double> _queue = new();

        /// <summary>
        /// The scratch set of a point's neighbours.
        /// </summary>
        readonly HashSet<int> _neighbours = [];

        /// <summary>
        /// Queues every edge once (from its lower point).
        /// </summary>
        public void QueueEdges()
        {
            for (int g = 0; g < _groups; g++)
            {
                _neighbours.Clear();
                foreach (int t in _incident[g])
                {
                    for (int k = 0; k < 3; k++)
                        if (_tg[t * 3 + k] > g) _neighbours.Add(_tg[t * 3 + k]);
                }

                foreach (int n in _neighbours) Push(g, n);
            }
        }

        /// <summary>
        /// The error of moving point <paramref name="from"/> onto <paramref name="to"/>.
        /// </summary>
        double Cost(int from, int to)
        {
            var sum = _q[from];
            sum.Add(_q[to]);
            double cost = sum.Evaluate(_position[to]);

            // A collapse across different skinning weights stretches the surface once posed: charged like a displacement of that size.
            float stretch = SkinDifference(_vertices[_groupVertices[from][0]], _vertices[_groupVertices[to][0]]);
            return cost + stretch * (_position[from] - _position[to]).LengthSquared() * 0.5;
        }

        /// <summary>
        /// Queues the cheaper direction of the edge <paramref name="a"/>-<paramref name="b"/>.
        /// </summary>
        void Push(int a, int b)
        {
            double forward = Cost(a, b), backward = Cost(b, a);
            if (forward <= backward) _queue.Enqueue((a, b, _version[a], _version[b]), forward);
            else _queue.Enqueue((b, a, _version[b], _version[a]), backward);
        }

        #endregion

        #region Collapse

        /// <summary>
        /// The vertices whose corners sit on each point now (the point's own and those of the points collapsed into it), for the UV check.
        /// </summary>
        List<int>[]? _members;

        /// <summary>
        /// With the texture guard (buildings: front and back of a sheet sit on one point with the same UV and opposite normals) the normal
        /// counts as much as the UV.
        /// </summary>
        readonly float _normalWeight;

        /// <summary>
        /// Collapses the cheapest edges, taking a snapshot into <paramref name="result"/> each time the triangle count reaches the next
        /// fraction's target; the lists left when the queue runs dry repeat the last.
        /// </summary>
        public void Reduce(uint[][] result, float[] fractions, int floor)
        {
            int next = 0;
            int TargetFor(int i) => Math.Max(floor, (int)(_triCount * fractions[i]));
            while (next < fractions.Length)
            {
                while (next < fractions.Length && _live <= TargetFor(next)) result[next++] = Snapshot();
                if (next >= fractions.Length || _queue.Count == 0) break;
                var (from, to, vf, vt) = _queue.Dequeue();
                if (!_valid[from] || !_valid[to] || _version[from] != vf || _version[to] != vt) continue;
                if (!CanCollapse(from, to)) continue;
                Collapse(from, to);
            }

            while (next < fractions.Length) result[next++] = Snapshot();
        }

        /// <summary>
        /// Moves every triangle of <paramref name="from"/> to <paramref name="to"/> (the ones holding both disappear) and queues the merged
        /// point's edges again.
        /// </summary>
        void Collapse(int from, int to)
        {
            foreach (int t in _incident[from])
            {
                if (!_alive[t]) continue;
                bool both = false;
                for (int k = 0; k < 3; k++)
                    if (_tg[t * 3 + k] == to) both = true;
                if (both)
                {
                    _alive[t] = false;
                    _live--;
                    continue;
                }

                for (int k = 0; k < 3; k++)
                    if (_tg[t * 3 + k] == from) _tg[t * 3 + k] = to;
                _incident[to].Add(t);
            }

            _incident[from] = [];
            _valid[from] = false;
            if (_members is not null)
            {
                _members[to].AddRange(_members[from]);
                _members[from] = [];
            }

            _q[to].Add(_q[from]);
            _version[to]++;
            _incident[to].RemoveAll(t => !_alive[t]);
            _neighbours.Clear();
            foreach (int t in _incident[to])
            {
                for (int k = 0; k < 3; k++)
                    if (_tg[t * 3 + k] != to) _neighbours.Add(_tg[t * 3 + k]);
            }

            foreach (int n in _neighbours) Push(to, n);
        }

        /// <summary>
        /// A collapse is refused when it would flip or crush a surviving triangle.
        /// </summary>
        bool CanCollapse(int from, int to)
        {
            if (_uvPerWorld > 0 && !UvConsistent(from, to)) return false;
            foreach (int t in _incident[from])
            {
                if (!_alive[t]) continue;
                bool both = false;
                for (int k = 0; k < 3; k++)
                    if (_tg[t * 3 + k] == to) both = true;
                if (both) continue;
                Vector3 p0 = _position[_tg[t * 3]], p1 = _position[_tg[t * 3 + 1]], p2 = _position[_tg[t * 3 + 2]];
                Vector3 m0 = _tg[t * 3] == from ? _position[to] : p0;
                Vector3 m1 = _tg[t * 3 + 1] == from ? _position[to] : p1;
                Vector3 m2 = _tg[t * 3 + 2] == from ? _position[to] : p2;
                var before = Vector3.Cross(p1 - p0, p2 - p0);
                var after = Vector3.Cross(m1 - m0, m2 - m0);
                if (after.LengthSquared() < 1e-14f || before.LengthSquared() < 1e-14f) continue;
                if (Vector3.Dot(Vector3.Normalize(before), Vector3.Normalize(after)) < 0.25f) return false;
            }

            return true;
        }

        /// <summary>
        /// Whether every vertex on <paramref name="from"/> (the first 32) has one on <paramref name="to"/> about as near in UV as the move
        /// is long.
        /// </summary>
        bool UvConsistent(int from, int to)
        {
            _members ??= [.. _groupVertices.Select(g => new List<int>(g))];
            float allow = _uvPerWorld * (_position[from] - _position[to]).Length() * 1.5f + 0.004f;
            float allowSquared = allow * allow;
            var targets = _groupVertices[to];
            var list = _members[from];
            for (int i = 0; i < list.Count && i < 32; i++)
            {
                float best = float.MaxValue;
                foreach (int c in targets) best = Math.Min(best, Vector2.DistanceSquared(_vertices[c].Uv, _vertices[list[i]].Uv));
                if (best > allowSquared) return false;
            }

            return true;
        }

        #endregion

        #region Snapshot

        /// <summary>
        /// The surviving triangles as an index list, each corner on the vertex of its point nearest its own (per vertex and point, once).
        /// </summary>
        uint[] Snapshot()
        {
            var list = new List<uint>(_live * 3);
            var pick = new Dictionary<(int Vertex, int Group), int>();
            for (int t = 0; t < _triCount; t++)
            {
                if (!_alive[t]) continue;
                int c0 = 0, c1 = 0, c2 = 0;
                for (int k = 0; k < 3; k++)
                {
                    int g = _tg[t * 3 + k], v = _tv[t * 3 + k];
                    if (_groupOf[v] != g)
                    {
                        if (!pick.TryGetValue((v, g), out int chosen))
                            pick[(v, g)] = chosen = Nearest(_vertices[v], _groupVertices[g]);
                        v = chosen;
                    }

                    if (k == 0) c0 = v;
                    else if (k == 1) c1 = v;
                    else c2 = v;
                }

                if (c0 == c1 || c1 == c2 || c0 == c2) continue;
                list.Add((uint)c0);
                list.Add((uint)c1);
                list.Add((uint)c2);
            }

            return [.. list];
        }

        /// <summary>
        /// The candidate vertex nearest <paramref name="like"/> in UV and (weighted) normal.
        /// </summary>
        int Nearest(in Vertex like, List<int> candidates)
        {
            int best = candidates[0];
            float bestScore = float.MaxValue;
            foreach (int c in candidates)
            {
                float score = Vector2.DistanceSquared(_vertices[c].Uv, like.Uv)
                    + _normalWeight * Vector3.DistanceSquared(_vertices[c].Normal, like.Normal);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }

            return best;
        }

        #endregion
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

    /// <summary>
    /// Component <paramref name="i"/> (0 to 3) of a vertex's bone weights.
    /// </summary>
    static float Weight(Vector4 w, int i) => i switch { 0 => w.X, 1 => w.Y, 2 => w.Z, _ => w.W };

    /// <summary>
    /// The symmetric 4x4 error matrix of a set of weighted planes.
    /// </summary>
    struct Quadric
    {
        /// <summary>
        /// The matrix's ten distinct entries, row by row of its upper triangle: <c>_a</c> to <c>_d</c> the first row (xx, xy, xz, x·offset), then yy,
        /// yz, y·offset, zz, z·offset and offset·offset.
        /// </summary>
        double _a, _b, _c, _d, _e, _f, _g, _h, _i, _j;

        /// <summary>
        /// The quadric of the plane n·p + <paramref name="offset"/> = 0, scaled by <paramref name="weight"/>.
        /// </summary>
        public static Quadric Plane(Vector3 n, float offset, float weight)
        {
            double x = n.X, y = n.Y, z = n.Z, o = offset, w = weight;
            return new Quadric { _a = w * x * x, _b = w * x * y, _c = w * x * z, _d = w * x * o, _e = w * y * y, _f = w * y * z, _g = w * y * o,
                _h = w * z * z, _i = w * z * o, _j = w * o * o };
        }

        /// <summary>
        /// Adds another quadric's entries (the sum is the error of both sets of planes).
        /// </summary>
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

        /// <summary>
        /// The weighted sum of the squared distances from <paramref name="p"/> to the planes.
        /// </summary>
        public readonly double Evaluate(Vector3 p)
        {
            double x = p.X, y = p.Y, z = p.Z;
            return _a * x * x + 2 * _b * x * y + 2 * _c * x * z + 2 * _d * x + _e * y * y + 2 * _f * y * z + 2 * _g * y + _h * z * z + 2 * _i * z + _j;
        }
    }
}