using System.Numerics;
using System.Runtime.InteropServices;

namespace Meitou.Data.Ogre;

/// <summary>
/// Procedural load-time armour clipping fixes on <see cref="OgreMesh"/> vertex buffers: pushing female-worn male meshes clear of the female body,
/// and tucking bulky leg meshes under long coats by collapsing detached outer armour islands and pulling remaining cloth inward.
/// </summary>
public static class OgreMeshArmourFit
{
    #region Constants

    /// <summary>
    /// Quantisation scale for welding coincident positions so UV and tangent seams move together (1e-4 model units).
    /// </summary>
    const float WeldScale = 10000f;

    /// <summary>
    /// Spatial-grid cell edge for body nearest-vertex queries, in model units.
    /// </summary>
    const float GridCell = 0.5f;

    /// <summary>
    /// Lowest Y height an outer body-slot mesh must reach to count as a long coat or chestplate that overlaps leg armour.
    /// </summary>
    const float CoatOverlapMaxBottomY = 9.0f;

    /// <summary>
    /// Upper Y bound of the coat overlap band (waist), in model units.
    /// </summary>
    const float CoatBandMaxY = 11.5f;

    /// <summary>
    /// Pelvis centre point that collapsed outer-plate islands degenerate to (zero-area triangles discarded by the GPU).
    /// </summary>
    static readonly Vector3 CollapsedPoint = new(0f, 9.0f, 0f);

    #endregion

    #region Entry points

    /// <summary>
    /// Pushes vertices of <paramref name="mesh"/> that sit inside <paramref name="body"/> outward along the nearest body surface normal, welded by
    /// position so UV/tangent seams never tear and smoothed outward across adjacent rings. Returns the number of vertices moved.
    /// </summary>
    public static int PushOutFromBody(OgreMesh mesh, OgreMesh body, float tol = 0.05f, float reach = 1.0f, float margin = 0.06f,
        float maxPush = 0.90f, int smoothRings = 2, int passes = 3)
    {
        var surface = ReadSurface(body);
        if (surface.Positions.Length == 0) return 0;

        var grid = new SpatialGrid(surface.Positions, GridCell);
        int moved = 0;
        foreach (var (data, tris) in GeometryBlocks(mesh))
            moved += PushBlockOut(data, tris, surface, grid, tol, reach, margin, maxPush, smoothRings, passes);

        return moved;
    }

    /// <summary>
    /// Adapts <paramref name="legs"/> to sit under <paramref name="coat"/> without poking through it or clipping into <paramref name="body"/>:
    /// detached outer armour-plate and chain islands inside the coat's Y band are collapsed to zero area, and remaining base cloth is tucked
    /// radially inward while keeping <paramref name="bodyClearance"/> outside <paramref name="body"/>. Returns the number of vertices moved.
    /// </summary>
    public static int TuckUnderCoat(OgreMesh legs, OgreMesh coat, OgreMesh body, float coatMargin = 0.16f, float islandMargin = 0.30f,
        float bodyClearance = 0.06f, float maxShrink = 1.20f, int smoothRings = 2)
    {
        var coatPts = ReadPositions(coat);
        if (coatPts.Length == 0) return 0;

        var profile = CoatProfile.Build(coatPts);
        if (profile.YMin > CoatOverlapMaxBottomY) return 0;

        var surface = ReadSurface(body);
        if (surface.Positions.Length == 0) return 0;

        var grid = new SpatialGrid(surface.Positions, GridCell);
        int moved = 0;
        foreach (var (data, tris) in GeometryBlocks(legs))
            moved += TuckBlockUnderCoat(data, tris, profile, surface, grid, coatMargin, islandMargin, bodyClearance, maxShrink, smoothRings);

        return moved;
    }

    #endregion

    #region Push-out step

    /// <summary>
    /// Pushes one geometry block's vertices outward from <paramref name="surface"/> and writes changed positions back into its vertex buffer.
    /// </summary>
    static int PushBlockOut(OgreVertexData data, uint[] tris, BodySurface surface, SpatialGrid grid, float tol, float reach, float margin,
        float maxPush, int smoothRings, int passes)
    {
        if (!TryLocatePositions(data, out var buf, out int stride, out int offset)) return 0;

        int n = (int)data.VertexCount;
        var orig = ReadVertexPositions(buf, stride, offset, n);
        var welded = Weld(orig, tris);
        int nw = welded.Positions.Length;
        var wOrig = (Vector3[])welded.Positions.Clone();
        var w = welded.Positions;
        var nearestIdx = new int[nw];
        var signedDepth = new float[nw];
        var inside = new bool[nw];

        for (int pass = 0; pass < passes; pass++)
        {
            bool anyInside = false;
            for (int i = 0; i < nw; i++)
            {
                int j = grid.Nearest(w[i], out float dist);
                nearestIdx[i] = j;
                float s = Vector3.Dot(w[i] - surface.Positions[j], surface.Normals[j]);
                signedDepth[i] = s;
                inside[i] = dist < reach && s < -tol;
                if (inside[i]) anyInside = true;
            }

            if (!anyInside) break;

            var disp = new Vector3[nw];
            for (int i = 0; i < nw; i++)
            {
                if (!inside[i]) continue;
                float req = Math.Min(-signedDepth[i] + margin, maxPush);
                disp[i] = surface.Normals[nearestIdx[i]] * req;
            }

            disp = SmoothOutward(disp, welded.Adjacency, inside, nearestIdx, signedDepth, surface.Normals, margin, maxPush, smoothRings);
            for (int i = 0; i < nw; i++)
            {
                var total = w[i] + disp[i] - wOrig[i];
                float mag = total.Length();
                if (mag > maxPush) total *= maxPush / mag;
                w[i] = wOrig[i] + total;
            }
        }

        return WriteMovedPositions(buf, stride, offset, orig, w, welded.Inverse);
    }

    /// <summary>
    /// Smooths outward push vectors across adjacent welded rings while keeping inside vertices at or above their required outward projection and
    /// never pulling outside neighbours inward toward the body.
    /// </summary>
    static Vector3[] SmoothOutward(Vector3[] disp, int[][] adj, bool[] inside, int[] nearestIdx, float[] signedDepth, Vector3[] bodyNormals,
        float margin, float maxPush, int rings)
    {
        int nw = disp.Length;
        var cur = disp;
        for (int ring = 0; ring < rings; ring++)
        {
            var nxt = new Vector3[nw];
            for (int i = 0; i < nw; i++)
            {
                var nb = adj[i];
                if (nb.Length == 0)
                {
                    nxt[i] = cur[i];
                    continue;
                }

                var avg = Vector3.Zero;
                for (int k = 0; k < nb.Length; k++) avg += cur[nb[k]];
                avg /= nb.Length;

                var nrm = bodyNormals[nearestIdx[i]];
                if (inside[i])
                {
                    var blended = cur[i] * 0.7f + avg * 0.3f;
                    float proj = Vector3.Dot(blended, nrm);
                    float req = Math.Min(-signedDepth[i] + margin, maxPush);
                    if (proj < req) blended += nrm * (req - proj);
                    nxt[i] = blended;
                }
                else
                {
                    nxt[i] = avg.LengthSquared() > 1e-12f && Vector3.Dot(avg, nrm) > 0f ? cur[i] * 0.5f + avg * 0.5f : cur[i];
                }
            }

            cur = nxt;
        }

        return cur;
    }

    #endregion

    #region Coat-overlap step

    /// <summary>
    /// Collapses detached outer plate/chain islands of one leg geometry block inside the coat's Y band and tucks remaining cloth radially inward.
    /// </summary>
    static int TuckBlockUnderCoat(OgreVertexData data, uint[] tris, CoatProfile profile, BodySurface surface, SpatialGrid grid, float coatMargin,
        float islandMargin, float bodyClearance, float maxShrink, int smoothRings)
    {
        if (!TryLocatePositions(data, out var buf, out int stride, out int offset)) return 0;

        int n = (int)data.VertexCount;
        var orig = ReadVertexPositions(buf, stride, offset, n);
        var welded = Weld(orig, tris);
        var w = welded.Positions;
        int nw = w.Length;

        var jb = new int[nw];
        var sBody = new float[nw];
        var r = new float[nw];
        var yb = new int[nw];
        var ab = new int[nw];
        for (int i = 0; i < nw; i++)
        {
            int j = grid.Nearest(w[i], out _);
            jb[i] = j;
            sBody[i] = Vector3.Dot(w[i] - surface.Positions[j], surface.Normals[j]);
            r[i] = MathF.Sqrt(w[i].X * w[i].X + w[i].Z * w[i].Z);
            (yb[i], ab[i]) = profile.Bin(w[i]);
        }

        var hidden = FindCollapsedIslands(w, welded.Adjacency, profile, r, yb, ab, sBody, islandMargin);
        var pull = ComputeTuckPull(nw, hidden, welded.Adjacency, profile, r, yb, ab, sBody, coatMargin, bodyClearance, maxShrink, smoothRings);

        var wNew = (Vector3[])w.Clone();
        for (int i = 0; i < nw; i++)
        {
            if (hidden[i])
            {
                wNew[i] = CollapsedPoint;
                continue;
            }

            if (pull[i] <= 1e-6f || r[i] <= 1e-4f) continue;

            float scale = Math.Max(0.25f, (r[i] - pull[i]) / r[i]);
            var movedPt = new Vector3(w[i].X * scale, w[i].Y, w[i].Z * scale);
            float sNew = Vector3.Dot(movedPt - surface.Positions[jb[i]], surface.Normals[jb[i]]);
            if (sNew < bodyClearance && sBody[i] > bodyClearance)
                movedPt += surface.Normals[jb[i]] * (bodyClearance - sNew);

            wNew[i] = movedPt;
        }

        return WriteMovedPositions(buf, stride, offset, orig, wNew, welded.Inverse);
    }

    /// <summary>
    /// Marks welded vertices belonging to detached outer plate/chain islands (smaller than 25% of the mesh and smaller than the largest island)
    /// that overlap the coat's Y band and either poke within <paramref name="islandMargin"/> of the coat shell or sit far off the body.
    /// </summary>
    static bool[] FindCollapsedIslands(Vector3[] w, int[][] adj, CoatProfile profile, float[] r, int[] yb, int[] ab, float[] sBody,
        float islandMargin)
    {
        int nw = w.Length;
        var comp = new int[nw];
        Array.Fill(comp, -1);
        var islands = new List<List<int>>();
        var queue = new Queue<int>();

        for (int i = 0; i < nw; i++)
        {
            if (comp[i] != -1) continue;
            int cid = islands.Count;
            var nodes = new List<int>();
            comp[i] = cid;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                nodes.Add(u);
                foreach (int v in adj[u])
                {
                    if (comp[v] != -1) continue;
                    comp[v] = cid;
                    queue.Enqueue(v);
                }
            }

            islands.Add(nodes);
        }

        int maxIsland = 0;
        foreach (var nd in islands) if (nd.Count > maxIsland) maxIsland = nd.Count;

        var hidden = new bool[nw];
        float bandTop = Math.Min(profile.YMax, CoatBandMaxY);
        foreach (var nodes in islands)
        {
            if (nodes.Count >= maxIsland || nodes.Count > nw * 0.25f) continue;

            float yMin = float.MaxValue, yMax = float.MinValue;
            int inBand = 0, poking = 0;
            var heights = new float[nodes.Count];
            for (int k = 0; k < nodes.Count; k++)
            {
                int idx = nodes[k];
                float y = w[idx].Y;
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
                heights[k] = sBody[idx];

                float rc = profile.RadiusAt(yb[idx], ab[idx]);
                if (!float.IsNaN(rc))
                {
                    inBand++;
                    if (r[idx] > rc - islandMargin) poking++;
                }
            }

            if (yMax < profile.YMin || yMin > bandTop || inBand == 0) continue;

            Array.Sort(heights);
            float sP90 = heights[(int)((heights.Length - 1) * 0.9f)];
            if (poking >= 1 || sP90 > 0.28f)
                foreach (int idx in nodes) hidden[idx] = true;
        }

        return hidden;
    }

    /// <summary>
    /// Computes the smoothed radial inward pull for visible base-trouser vertices, clamped by <paramref name="bodyClearance"/> and
    /// <paramref name="maxShrink"/>.
    /// </summary>
    static float[] ComputeTuckPull(int nw, bool[] hidden, int[][] adj, CoatProfile profile, float[] r, int[] yb, int[] ab, float[] sBody,
        float coatMargin, float bodyClearance, float maxShrink, int smoothRings)
    {
        var pull = new float[nw];
        for (int i = 0; i < nw; i++)
        {
            if (hidden[i]) continue;
            float rc = profile.RadiusAt(yb[i], ab[i]);
            if (float.IsNaN(rc)) continue;
            float excess = r[i] - (rc - coatMargin);
            if (excess > 0f && sBody[i] > bodyClearance)
                pull[i] = Math.Min(excess, Math.Min(sBody[i] - bodyClearance, maxShrink));
        }

        var cur = pull;
        for (int ring = 0; ring < smoothRings; ring++)
        {
            var nxt = new float[nw];
            for (int i = 0; i < nw; i++)
            {
                if (hidden[i]) continue;
                var nb = adj[i];
                float sum = 0f;
                int count = 0;
                for (int k = 0; k < nb.Length; k++)
                {
                    if (hidden[nb[k]]) continue;
                    sum += cur[nb[k]];
                    count++;
                }

                if (count == 0)
                {
                    nxt[i] = cur[i];
                    continue;
                }

                float avg = sum / count;
                float safe = Math.Max(0f, sBody[i] - bodyClearance);
                if (pull[i] > 0f) nxt[i] = Math.Min(Math.Max(pull[i], cur[i] * 0.6f + avg * 0.4f), Math.Min(safe, maxShrink));
                else if (avg > 1e-5f && safe > 0f) nxt[i] = Math.Min(cur[i] * 0.5f + avg * 0.5f, Math.Min(safe, maxShrink));
                else nxt[i] = cur[i];
            }

            cur = nxt;
        }

        return cur;
    }

    #endregion

    #region Mesh helpers

    /// <summary>
    /// A body mesh's surface points and unit normals, flattened across its drawable submeshes.
    /// </summary>
    readonly record struct BodySurface(Vector3[] Positions, Vector3[] Normals);

    /// <summary>
    /// Welded unique positions, per-original-vertex weld index and welded triangle adjacency.
    /// </summary>
    readonly record struct WeldedMesh(Vector3[] Positions, int[] Inverse, int[][] Adjacency);

    /// <summary>
    /// Cylindrical radius profile of an outer coat mesh, binned by Y height and XZ azimuth.
    /// </summary>
    sealed class CoatProfile
    {
        /// <summary>
        /// Height step per vertical bin, in model units.
        /// </summary>
        const float YStep = 0.5f;

        /// <summary>
        /// Number of azimuth bins around the Y axis (10 degrees each).
        /// </summary>
        const int AngleBins = 36;

        /// <summary>
        /// 25th-percentile coat radius per (yBin, angleBin) cell; NaN where the coat has an opening (such as the front slit).
        /// </summary>
        readonly float[] _grid;

        /// <summary>
        /// Number of vertical bins in <see cref="_grid"/>.
        /// </summary>
        readonly int _ny;

        /// <summary>
        /// Lowest Y coordinate of the coat mesh.
        /// </summary>
        public float YMin { get; }

        /// <summary>
        /// Highest Y coordinate of the coat mesh.
        /// </summary>
        public float YMax { get; }

        /// <summary>
        /// Stores a built coat profile.
        /// </summary>
        CoatProfile(float[] grid, int ny, float yMin, float yMax) => (_grid, _ny, YMin, YMax) = (grid, ny, yMin, yMax);

        /// <summary>
        /// Builds the cylindrical radius profile of <paramref name="coatPositions"/>.
        /// </summary>
        public static CoatProfile Build(Vector3[] coatPositions)
        {
            float yMin = float.MaxValue, yMax = float.MinValue;
            for (int i = 0; i < coatPositions.Length; i++)
            {
                float y = coatPositions[i].Y;
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
            }

            int ny = Math.Max(1, (int)MathF.Ceiling((yMax - yMin) / YStep));
            var grid = new float[ny * AngleBins];
            Array.Fill(grid, float.NaN);
            var buckets = new Dictionary<int, List<float>>();

            for (int i = 0; i < coatPositions.Length; i++)
            {
                var p = coatPositions[i];
                int yb = Math.Clamp((int)((p.Y - yMin) / YStep), 0, ny - 1);
                float ang = MathF.Atan2(p.Z, p.X);
                int ab = Math.Clamp((int)((ang + MathF.PI) / (2f * MathF.PI) * AngleBins), 0, AngleBins - 1);
                int cell = yb * AngleBins + ab;
                if (!buckets.TryGetValue(cell, out var list)) buckets[cell] = list = [];
                list.Add(MathF.Sqrt(p.X * p.X + p.Z * p.Z));
            }

            foreach (var (cell, list) in buckets)
            {
                if (list.Count == 0) continue;
                list.Sort();
                grid[cell] = list[(int)((list.Count - 1) * 0.25f)];
            }

            return new CoatProfile(grid, ny, yMin, yMax);
        }

        /// <summary>
        /// Returns the vertical and angular bin indices of <paramref name="p"/>.
        /// </summary>
        public (int YBin, int AngleBin) Bin(Vector3 p)
        {
            int yb = (int)MathF.Floor((p.Y - YMin) / YStep);
            float ang = MathF.Atan2(p.Z, p.X);
            int ab = Math.Clamp((int)((ang + MathF.PI) / (2f * MathF.PI) * AngleBins), 0, AngleBins - 1);
            return (yb, ab);
        }

        /// <summary>
        /// Coat radius at (<paramref name="yb"/>, <paramref name="ab"/>), or NaN outside the coat's height range or at an opening.
        /// </summary>
        public float RadiusAt(int yb, int ab) => yb < 0 || yb >= _ny ? float.NaN : _grid[yb * AngleBins + ab];
    }

    /// <summary>
    /// Uniform 3D spatial hash grid for fast nearest-point lookup on a body surface.
    /// </summary>
    sealed class SpatialGrid
    {
        /// <summary>
        /// Indexed surface points.
        /// </summary>
        readonly Vector3[] _pts;

        /// <summary>
        /// Inverse cell edge length.
        /// </summary>
        readonly float _invCell;

        /// <summary>
        /// Point indices per integer cell coordinate.
        /// </summary>
        readonly Dictionary<(int, int, int), List<int>> _cells = [];

        /// <summary>
        /// Builds a grid over <paramref name="points"/> with cell edge <paramref name="cellSize"/>.
        /// </summary>
        public SpatialGrid(Vector3[] points, float cellSize)
        {
            _pts = points;
            _invCell = 1f / cellSize;
            for (int i = 0; i < points.Length; i++)
            {
                var key = CellOf(points[i]);
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = [];
                list.Add(i);
            }
        }

        /// <summary>
        /// Integer cell coordinate containing <paramref name="p"/>.
        /// </summary>
        (int, int, int) CellOf(Vector3 p) =>
            ((int)MathF.Floor(p.X * _invCell), (int)MathF.Floor(p.Y * _invCell), (int)MathF.Floor(p.Z * _invCell));

        /// <summary>
        /// Index of the point nearest <paramref name="p"/> and its Euclidean distance.
        /// </summary>
        public int Nearest(Vector3 p, out float dist)
        {
            var (cx, cy, cz) = CellOf(p);
            int best = -1;
            float bestD2 = float.MaxValue;

            for (int rad = 0; rad <= 3; rad++)
            {
                for (int dx = -rad; dx <= rad; dx++)
                {
                    for (int dy = -rad; dy <= rad; dy++)
                    {
                        for (int dz = -rad; dz <= rad; dz++)
                        {
                            if (rad > 0 && Math.Abs(dx) != rad && Math.Abs(dy) != rad && Math.Abs(dz) != rad) continue;
                            if (!_cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                            for (int k = 0; k < list.Count; k++)
                            {
                                int idx = list[k];
                                float d2 = Vector3.DistanceSquared(p, _pts[idx]);
                                if (d2 < bestD2)
                                {
                                    bestD2 = d2;
                                    best = idx;
                                }
                            }
                        }
                    }
                }

                // Stop expanding once a point is closer than the next shell's minimum distance.
                if (best >= 0 && bestD2 <= (rad / _invCell) * (rad / _invCell)) break;
            }

            if (best < 0)
            {
                for (int i = 0; i < _pts.Length; i++)
                {
                    float d2 = Vector3.DistanceSquared(p, _pts[i]);
                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        best = i;
                    }
                }
            }

            dist = MathF.Sqrt(Math.Max(bestD2, 0f));
            return best;
        }
    }

    /// <summary>
    /// Yields each unique <see cref="OgreVertexData"/> block in <paramref name="mesh"/> with the triangulated indices that reference it.
    /// </summary>
    static List<(OgreVertexData Data, uint[] Tris)> GeometryBlocks(OgreMesh mesh)
    {
        var blocks = new List<(OgreVertexData, uint[])>();
        var sharedTris = new List<uint>();

        foreach (var sub in mesh.SubMeshes)
        {
            var tris = Triangulate(sub);
            if (tris.Length == 0) continue;
            if (sub.UseSharedVertices) sharedTris.AddRange(tris);
            else if (sub.VertexData is { VertexCount: > 0 } vd) blocks.Add((vd, tris));
        }

        if (mesh.SharedVertexData is { VertexCount: > 0 } shared && sharedTris.Count > 0)
            blocks.Insert(0, (shared, sharedTris.ToArray()));

        return blocks;
    }

    /// <summary>
    /// Submesh indices as a triangle list (strips and fans converted; unsupported operations empty).
    /// </summary>
    static uint[] Triangulate(OgreSubMesh sub)
    {
        var src = sub.Indices.Indices;
        if (src.Length < 3) return [];
        if (sub.Operation == OgreOperationType.TriangleList) return src;
        if (sub.Operation == OgreOperationType.TriangleStrip)
        {
            var list = new List<uint>((src.Length - 2) * 3);
            for (int i = 2; i < src.Length; i++)
            {
                if (i % 2 == 0) list.AddRange([src[i - 2], src[i - 1], src[i]]);
                else list.AddRange([src[i - 1], src[i - 2], src[i]]);
            }

            return list.ToArray();
        }

        if (sub.Operation == OgreOperationType.TriangleFan)
        {
            var list = new List<uint>((src.Length - 2) * 3);
            for (int i = 2; i < src.Length; i++) list.AddRange([src[0], src[i - 1], src[i]]);
            return list.ToArray();
        }

        return [];
    }

    /// <summary>
    /// Reads all vertex positions across <paramref name="mesh"/>'s drawable submeshes.
    /// </summary>
    static Vector3[] ReadPositions(OgreMesh mesh)
    {
        var pts = new List<Vector3>();
        foreach (var (data, _) in GeometryBlocks(mesh))
            if (data.ReadPositions() is { } p) pts.AddRange(p);

        return pts.ToArray();
    }

    /// <summary>
    /// Reads all vertex positions and unit normals across <paramref name="mesh"/>'s drawable submeshes.
    /// </summary>
    static BodySurface ReadSurface(OgreMesh mesh)
    {
        var pts = new List<Vector3>();
        var nrms = new List<Vector3>();
        foreach (var (data, _) in GeometryBlocks(mesh))
        {
            var p = data.ReadPositions();
            if (p is null) continue;
            var nEl = data.Find(OgreVertexSemantic.Normal);
            float[]? nf = nEl is null ? null : data.ReadFloats(nEl);
            int nc = nEl?.Type.ComponentCount() ?? 0;

            for (int i = 0; i < p.Length; i++)
            {
                pts.Add(p[i]);
                if (nf is not null && nc >= 3)
                {
                    var n = new Vector3(nf[i * nc], nf[i * nc + 1], nf[i * nc + 2]);
                    float len = n.Length();
                    nrms.Add(len > 1e-6f ? n / len : Vector3.UnitY);
                }
                else
                {
                    nrms.Add(Vector3.UnitY);
                }
            }
        }

        return new BodySurface(pts.ToArray(), nrms.ToArray());
    }

    /// <summary>
    /// Locates the Float3 POSITION buffer, vertex stride and byte offset in <paramref name="data"/>.
    /// </summary>
    static bool TryLocatePositions(OgreVertexData data, out byte[] buffer, out int stride, out int offset)
    {
        buffer = [];
        stride = 0;
        offset = 0;
        var pos = data.Find(OgreVertexSemantic.Position);
        if (pos is null || pos.Type != OgreVertexElementType.Float3) return false;
        if (!data.Buffers.TryGetValue(pos.Source, out var vb)) return false;
        if (vb.Data.Length < data.VertexCount * vb.VertexSize) return false;

        buffer = vb.Data;
        stride = vb.VertexSize;
        offset = pos.Offset;
        return true;
    }

    /// <summary>
    /// Reads <paramref name="count"/> Float3 positions from <paramref name="buffer"/>.
    /// </summary>
    static Vector3[] ReadVertexPositions(byte[] buffer, int stride, int offset, int count)
    {
        var pts = new Vector3[count];
        for (int i = 0; i < count; i++)
            pts[i] = MemoryMarshal.Read<Vector3>(buffer.AsSpan(i * stride + offset, 12));

        return pts;
    }

    /// <summary>
    /// Welds coincident positions at <see cref="WeldScale"/> and builds the deduplicated vertex adjacency graph from <paramref name="tris"/>.
    /// </summary>
    static WeldedMesh Weld(Vector3[] positions, uint[] tris)
    {
        int n = positions.Length;
        var map = new Dictionary<(int, int, int), int>(n);
        var unique = new List<Vector3>(n);
        var inv = new int[n];

        for (int i = 0; i < n; i++)
        {
            var p = positions[i];
            var key = ((int)MathF.Round(p.X * WeldScale), (int)MathF.Round(p.Y * WeldScale), (int)MathF.Round(p.Z * WeldScale));
            if (!map.TryGetValue(key, out int id))
            {
                id = unique.Count;
                map[key] = id;
                unique.Add(p);
            }

            inv[i] = id;
        }

        int nw = unique.Count;
        var sets = new HashSet<int>[nw];
        for (int i = 0; i < nw; i++) sets[i] = [];

        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            uint ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
            if (ia >= n || ib >= n || ic >= n) continue;
            int a = inv[ia], b = inv[ib], c = inv[ic];
            if (a != b)
            {
                sets[a].Add(b);
                sets[b].Add(a);
            }

            if (b != c)
            {
                sets[b].Add(c);
                sets[c].Add(b);
            }

            if (c != a)
            {
                sets[c].Add(a);
                sets[a].Add(c);
            }
        }

        var adj = new int[nw][];
        for (int i = 0; i < nw; i++) adj[i] = sets[i].ToArray();

        return new WeldedMesh(unique.ToArray(), inv, adj);
    }

    /// <summary>
    /// Writes any moved welded vertex positions back into <paramref name="buffer"/> and returns how many vertices moved.
    /// </summary>
    static int WriteMovedPositions(byte[] buffer, int stride, int offset, Vector3[] orig, Vector3[] weldedNew, int[] inv)
    {
        int moved = 0;
        for (int i = 0; i < orig.Length; i++)
        {
            var p = weldedNew[inv[i]];
            if (Vector3.DistanceSquared(p, orig[i]) <= 1e-12f) continue;
            MemoryMarshal.Write(buffer.AsSpan(i * stride + offset, 12), in p);
            moved++;
        }

        return moved;
    }

    #endregion
}
