using System.Numerics;
using System.Runtime.InteropServices;

namespace Meitou.Data.Ogre;

/// <summary>
/// Welds a mesh's vertices that differ only in their tangent frame: the base game's meshes carry per-face tangents, so most corners are stored once
/// per triangle (docs/formats/ogre-mesh.md, "Vertex sharing"). Vertices with the same bytes in every other element, the same bone assignments, the
/// same pose offsets and the same handedness become one, whose tangent (and binormal) is the normalized sum of theirs. The submesh's indices, its
/// generated LOD levels, bone assignments and poses are renumbered to match. Not lossless: the tangents change by a few degrees on average.
/// </summary>
public static class OgreMeshWeld
{
    /// <summary>
    /// Welds every submesh with vertices of its own in place; returns the number of vertices removed. A mesh with shared vertices is left as it is.
    /// </summary>
    public static int Apply(OgreMesh mesh)
    {
        if (mesh.SharedVertexData is not null) return 0;
        int removed = 0;
        for (int s = 0; s < mesh.SubMeshes.Count; s++)
            if (mesh.SubMeshes[s] is { UseSharedVertices: false, VertexData: { VertexCount: > 1 } data } sub)
                removed += new SubMeshWeld(mesh, s, sub, data).Run();
        return removed;
    }

    /// <summary>
    /// 1 when the frame is left-handed (a Float4 tangent's w below 0, or the binormal against cross(normal, tangent)), else 0.
    /// </summary>
    static byte Handedness(int v, Column? normal, Column? tangent, Column? binormal, bool tangentW)
    {
        if (tangent is not { } t) return 0;
        if (tangentW) return t.Float(v, 3) < 0 ? (byte)1 : (byte)0;
        if (binormal is not { } b || normal is not { } n) return 0;
        return Vector3.Dot(Vector3.Cross(n.Vector(v), t.Vector(v)), b.Vector(v)) < 0 ? (byte)1 : (byte)0;
    }

    /// <summary>
    /// The sum of each group's members' (each normalized) vectors of <paramref name="column"/>; zero-length vectors are left out.
    /// </summary>
    static Vector3[] Sums(Column column, uint[] remap, int count)
    {
        var sums = new Vector3[count];
        for (int v = 0; v < remap.Length; v++)
        {
            var d = column.Vector(v);
            if (d.LengthSquared() > 1e-12f) sums[remap[v]] += Vector3.Normalize(d);
        }
        return sums;
    }

    /// <summary>
    /// Writes each group's normalized sum into its packed vertex, unless the sum is near zero.
    /// </summary>
    static void Write(byte[] packed, int stride, OgreVertexElement element, Vector3[] sums)
    {
        for (int g = 0; g < sums.Length; g++)
        {
            if (sums[g].LengthSquared() < 1e-8f) continue;   // opposite members cancelled: keep the first's
            var d = Vector3.Normalize(sums[g]);
            var at = packed.AsSpan(g * stride + element.Offset);
            BitConverter.TryWriteBytes(at, d.X);
            BitConverter.TryWriteBytes(at[4..], d.Y);
            BitConverter.TryWriteBytes(at[8..], d.Z);
        }
    }

    /// <summary>
    /// One float element's place in its buffer, looked up once.
    /// </summary>
    readonly record struct Column(byte[] Data, int Stride, int Offset)
    {
        /// <summary>
        /// The column of element <paramref name="e"/> in <paramref name="data"/>; null when the element is absent.
        /// </summary>
        public static Column? Of(OgreVertexData data, OgreVertexElement? e) =>
            e is null ? null : new Column(data.Buffers[e.Source].Data, data.Buffers[e.Source].VertexSize, e.Offset);
        /// <summary>
        /// Float <paramref name="component"/> of the element at vertex <paramref name="v"/>.
        /// </summary>
        public float Float(int v, int component) => BitConverter.ToSingle(Data, v * Stride + Offset + component * 4);
        /// <summary>
        /// The element at vertex <paramref name="v"/> as three floats.
        /// </summary>
        public Vector3 Vector(int v) => MemoryMarshal.Read<Vector3>(Data.AsSpan(v * Stride + Offset, 12));
    }

    /// <summary>
    /// The index buffer with every index below the vertex count renumbered to its group.
    /// </summary>
    static OgreIndexBuffer Remapped(OgreIndexBuffer buffer, uint[] remap)
    {
        var indices = new uint[buffer.Indices.Length];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = buffer.Indices[i] < remap.Length ? remap[buffer.Indices[i]] : buffer.Indices[i];
        return buffer with { Indices = indices };
    }

    /// <summary>
    /// The weld of one submesh: its vertices grouped by key, then its buffers, indices, LOD levels, bone assignments and poses rewritten.
    /// </summary>
    sealed class SubMeshWeld
    {
        /// <summary>
        /// The mesh.
        /// </summary>
        readonly OgreMesh _mesh;

        /// <summary>
        /// The submesh's index in the mesh.
        /// </summary>
        readonly int _s;

        /// <summary>
        /// The submesh.
        /// </summary>
        readonly OgreSubMesh _sub;

        /// <summary>
        /// The submesh's own vertex data.
        /// </summary>
        readonly OgreVertexData _data;

        /// <summary>
        /// The tangent frame's elements (null: not in the mesh).
        /// </summary>
        OgreVertexElement? _tangent, _binormal, _normal;

        /// <summary>
        /// The vertex count before the weld.
        /// </summary>
        int _n;

        /// <summary>
        /// The vertex buffers by source, in source order.
        /// </summary>
        KeyValuePair<ushort, OgreVertexBuffer>[] _sources = [];

        /// <summary>
        /// The submesh's bone assignments (of vertices below the count) sorted by vertex, bone and weight.
        /// </summary>
        OgreBoneAssignment[] _assignments = [];

        /// <summary>
        /// Where each vertex's assignments start in <see cref="_assignments"/>; vertex v's end where v + 1's start.
        /// </summary>
        int[] _boneStart = [];

        /// <summary>
        /// The mesh's poses that target the submesh.
        /// </summary>
        OgrePose[] _poses = [];

        /// <summary>
        /// Each vertex's offsets in those poses, with the pose's index in <see cref="_poses"/>.
        /// </summary>
        readonly Dictionary<uint, List<(int Pose, OgrePoseVertex Vertex)>> _poseEntries = [];

        /// <summary>
        /// The bytes of the key being built (grown as needed).
        /// </summary>
        byte[] _key = [];

        /// <summary>
        /// The bytes of <see cref="_key"/> in use.
        /// </summary>
        int _length;

        /// <summary>
        /// The tangent and binormal columns, read again for the welded frame.
        /// </summary>
        Column? _tangents0, _binormals0;

        /// <summary>
        /// Each vertex's group.
        /// </summary>
        uint[] _remap = [];

        /// <summary>
        /// Each group's first vertex, the one kept.
        /// </summary>
        List<int> _first = [];

        /// <summary>
        /// The weld of submesh <paramref name="s"/> of <paramref name="mesh"/>.
        /// </summary>
        public SubMeshWeld(OgreMesh mesh, int s, OgreSubMesh sub, OgreVertexData data)
        {
            _mesh = mesh;
            _s = s;
            _sub = sub;
            _data = data;
        }

        /// <summary>
        /// Welds the submesh; returns the number of vertices removed (0: left as it is).
        /// </summary>
        public int Run()
        {
            if (!ReadFrame()) return 0;
            if (!ReadSources()) return 0;

            SortAssignments();
            CollectPoses();

            GroupVertices();
            int count = _first.Count;
            if (count == _n) return 0;

            Repack(count);
            RemapIndices();
            CompactAssignments();
            CompactPoses();
            return _n - count;
        }

        /// <summary>
        /// Finds the tangent frame's elements; false when one is of a type the weld does not handle.
        /// </summary>
        bool ReadFrame()
        {
            _tangent = _data.Find(OgreVertexSemantic.Tangent);
            _binormal = _data.Find(OgreVertexSemantic.Binormal);
            _normal = _data.Find(OgreVertexSemantic.Normal);
            if (_tangent is not null && _tangent.Type is not (OgreVertexElementType.Float3 or OgreVertexElementType.Float4))
                return false;
            if (_binormal is not null && _binormal.Type != OgreVertexElementType.Float3) return false;
            if (_normal is not null && _normal.Type != OgreVertexElementType.Float3) return false;
            return true;
        }

        /// <summary>
        /// Takes the vertex count and the buffers; false when a buffer is shorter than the count says.
        /// </summary>
        bool ReadSources()
        {
            _n = (int)_data.VertexCount;
            _sources = _data.Buffers.OrderBy(b => b.Key).ToArray();
            foreach (var (_, buffer) in _sources) if (buffer.Data.Length < _n * buffer.VertexSize) return false;
            return true;
        }

        /// <summary>
        /// Sorts the submesh's bone assignments and finds each vertex's range of them.
        /// </summary>
        void SortAssignments()
        {
            // Each vertex's bone assignments in a fixed order: one sort, then its range by a running count.
            _assignments = _sub.BoneAssignments.Where(a => a.VertexIndex < _n).ToArray();
            Array.Sort(_assignments, (a, b) =>
                a.VertexIndex != b.VertexIndex ? a.VertexIndex.CompareTo(b.VertexIndex)
                : a.BoneIndex != b.BoneIndex ? a.BoneIndex.CompareTo(b.BoneIndex)
                : a.Weight.CompareTo(b.Weight));
            _boneStart = new int[_n + 1];
            foreach (var a in _assignments) _boneStart[a.VertexIndex + 1]++;
            for (int v = 0; v < _n; v++) _boneStart[v + 1] += _boneStart[v];
        }

        /// <summary>
        /// The poses that target the submesh and each vertex's offsets in them, by pose.
        /// </summary>
        void CollectPoses()
        {
            _poses = _mesh.Poses.Where(p => p.Target == _s + 1).ToArray();
            for (int p = 0; p < _poses.Length; p++)
                foreach (var v in _poses[p].Vertices)
                    (_poseEntries.TryGetValue(v.Index, out var list) ? list : _poseEntries[v.Index] = []).Add((p, v));
        }

        /// <summary>
        /// Groups the vertices by key: <see cref="_remap"/> gets each vertex's group, <see cref="_first"/> each group's first vertex.
        /// </summary>
        void GroupVertices()
        {
            // The key: every buffer's bytes with the tangent frame's zeroed, the handedness, the bone assignments and the pose offsets.
            var groups = new Dictionary<byte[], int>(_n, ByteKey.Instance);
            var lookup = groups.GetAlternateLookup<ReadOnlySpan<byte>>();
            _remap = new uint[_n];
            _first = new List<int>(_n);
            _key = new byte[256];
            _length = 0;
            Span<byte> scratch = stackalloc byte[4];
            Column? normals = Column.Of(_data, _normal);
            _tangents0 = Column.Of(_data, _tangent);
            _binormals0 = Column.Of(_data, _binormal);
            for (int v = 0; v < _n; v++)
            {
                var k = Key(v, normals, scratch);
                if (!lookup.TryGetValue(k, out int g))
                {
                    lookup[k] = g = _first.Count;
                    _first.Add(v);
                }
                _remap[v] = (uint)g;
            }
        }

        /// <summary>
        /// Vertex <paramref name="v"/>'s key (valid until the next one is built).
        /// </summary>
        ReadOnlySpan<byte> Key(int v, Column? normals, Span<byte> scratch)
        {
            _length = 0;
            foreach (var (source, buffer) in _sources)
            {
                int start = _length;
                Put(buffer.Data.AsSpan(v * buffer.VertexSize, buffer.VertexSize));
                if (_tangent?.Source == source) _key.AsSpan(start + _tangent.Offset, _tangent.Type.Size()).Clear();
                if (_binormal?.Source == source) _key.AsSpan(start + _binormal.Offset, _binormal.Type.Size()).Clear();
            }
            scratch[0] = Handedness(v, normals, _tangents0, _binormals0, _tangent?.Type == OgreVertexElementType.Float4);
            Put(scratch[..1]);
            PutBones(v, scratch);
            PutPoses(v, scratch);
            return _key.AsSpan(0, _length);
        }

        /// <summary>
        /// Adds vertex <paramref name="v"/>'s bone assignments to the key.
        /// </summary>
        void PutBones(int v, Span<byte> scratch)
        {
            for (int b = _boneStart[v]; b < _boneStart[v + 1]; b++)
            {
                BitConverter.TryWriteBytes(scratch, _assignments[b].BoneIndex);
                Put(scratch[..2]);
                BitConverter.TryWriteBytes(scratch, _assignments[b].Weight);
                Put(scratch);
            }
        }

        /// <summary>
        /// Adds vertex <paramref name="v"/>'s pose offsets to the key.
        /// </summary>
        void PutPoses(int v, Span<byte> scratch)
        {
            if (_poseEntries.TryGetValue((uint)v, out var moves))
                foreach (var (p, m) in moves)
                {
                    BitConverter.TryWriteBytes(scratch, p);
                    Put(scratch);
                    var offset = m.Offset;
                    var turn = m.Normal;
                    Put(MemoryMarshal.AsBytes(new ReadOnlySpan<Vector3>(ref offset)));
                    Put(MemoryMarshal.AsBytes(new ReadOnlySpan<Vector3>(ref turn)));
                }
        }

        /// <summary>
        /// Appends <paramref name="bytes"/> to the key, growing it when needed.
        /// </summary>
        void Put(ReadOnlySpan<byte> bytes)
        {
            if (_length + bytes.Length > _key.Length)
                Array.Resize(ref _key, Math.Max(_key.Length * 2, _length + bytes.Length));
            bytes.CopyTo(_key.AsSpan(_length));
            _length += bytes.Length;
        }

        /// <summary>
        /// The buffers cut to one vertex per group, with the welded tangent frame.
        /// </summary>
        void Repack(int count)
        {
            // The welded tangent frame: the normalized sum of the members' (each normalized), on the first member's bytes.
            Vector3[]? tangents = _tangents0 is { } tc ? Sums(tc, _remap, count) : null;
            Vector3[]? binormals = _binormals0 is { } bc ? Sums(bc, _remap, count) : null;
            foreach (var (source, buffer) in _sources)
            {
                var packed = new byte[count * buffer.VertexSize];
                for (int g = 0; g < count; g++)
                    buffer.Data.AsSpan(_first[g] * buffer.VertexSize, buffer.VertexSize).CopyTo(packed.AsSpan(g * buffer.VertexSize));
                if (_tangent?.Source == source) Write(packed, buffer.VertexSize, _tangent, tangents!);
                if (_binormal?.Source == source) Write(packed, buffer.VertexSize, _binormal, binormals!);
                _data.Buffers[source] = new OgreVertexBuffer(buffer.VertexSize, packed);
            }
            _data.VertexCount = (uint)count;
        }

        /// <summary>
        /// The submesh's indices and its generated LOD levels' renumbered (a buffer shared by levels once).
        /// </summary>
        void RemapIndices()
        {
            _sub.Indices = Remapped(_sub.Indices, _remap);
            if (_mesh.Lod is { } lod)
            {
                var done = new Dictionary<OgreIndexBuffer, OgreIndexBuffer>(ReferenceEqualityComparer.Instance);
                foreach (var level in lod.Levels)
                    if (_s < level.Faces.Count && level.Faces[_s].Buffer is { } buffer)
                        level.Faces[_s] = level.Faces[_s] with
                        {
                            Buffer = done.TryGetValue(buffer, out var made) ? made : done[buffer] = Remapped(buffer, _remap)
                        };
            }
        }

        /// <summary>
        /// The bone assignments of each group's first vertex, renumbered; the others' dropped.
        /// </summary>
        void CompactAssignments()
        {
            int w = 0;
            var weights = _sub.BoneAssignments;
            for (int i = 0; i < weights.Count; i++)
                if (Kept(weights[i].VertexIndex))
                    weights[w++] = weights[i] with { VertexIndex = _remap[weights[i].VertexIndex] };
            weights.RemoveRange(w, weights.Count - w);
        }

        /// <summary>
        /// The pose offsets of each group's first vertex, renumbered; the others' dropped.
        /// </summary>
        void CompactPoses()
        {
            foreach (var pose in _poses)
            {
                int w = 0;
                var moves = pose.Vertices;
                for (int i = 0; i < moves.Count; i++)
                    if (Kept(moves[i].Index)) moves[w++] = moves[i] with { Index = _remap[moves[i].Index] };
                moves.RemoveRange(w, moves.Count - w);
            }
        }

        /// <summary>
        /// Whether vertex <paramref name="v"/> is its group's first (the one kept).
        /// </summary>
        bool Kept(uint v) => v < _n && _first[(int)_remap[v]] == v;
    }

    /// <summary>
    /// Compares and hashes vertex keys by their bytes, with a lookup by span so a key is copied only when it is new.
    /// </summary>
    sealed class ByteKey : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        /// <summary>
        /// The one comparer.
        /// </summary>
        public static readonly ByteKey Instance = new();
        /// <summary>
        /// Whether two keys hold the same bytes.
        /// </summary>
        public bool Equals(byte[]? a, byte[]? b) => a.AsSpan().SequenceEqual(b);
        /// <summary>
        /// The hash of a key's bytes (as for its span).
        /// </summary>
        public int GetHashCode(byte[] k) => GetHashCode(k.AsSpan());
        /// <summary>
        /// Whether a span holds the same bytes as a key.
        /// </summary>
        public bool Equals(ReadOnlySpan<byte> a, byte[] b) => a.SequenceEqual(b);
        /// <summary>
        /// The hash of a span's bytes, mixed eight bytes at a time; the same for a key and its span.
        /// </summary>
        public int GetHashCode(ReadOnlySpan<byte> k)
        {
            // Eight bytes at a time (a vertex key is some 50 to 100 bytes, hashed once per vertex).
            ulong h = 14695981039346656037UL ^ (ulong)k.Length;
            var words = MemoryMarshal.Cast<byte, ulong>(k);
            foreach (var w in words) h = (BitOperations.RotateLeft(h ^ w, 29) * 0x9E3779B97F4A7C15UL);
            for (int i = words.Length * 8; i < k.Length; i++) h = (h ^ k[i]) * 0x100000001B3UL;
            return (int)(h ^ (h >> 32));
        }

        /// <summary>
        /// Copies a new key out of its span.
        /// </summary>
        public byte[] Create(ReadOnlySpan<byte> k) => k.ToArray();
    }
}