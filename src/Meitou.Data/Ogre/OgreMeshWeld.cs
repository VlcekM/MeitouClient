using System.Numerics;
using System.Runtime.InteropServices;

namespace Meitou.Data.Ogre;

/// <summary>
/// Welds a mesh's vertices that differ only in their tangent frame: the base game's meshes carry per-face tangents, so most corners are stored once per
/// triangle (docs/formats/ogre-mesh.md, "Vertex sharing"). Vertices with the same bytes in every other element, the same bone assignments, the same pose
/// offsets and the same handedness become one, whose tangent (and binormal) is the normalized sum of theirs. The submesh's indices, its generated LOD
/// levels, bone assignments and poses are renumbered to match. Not lossless: the tangents change by a few degrees on average.
/// </summary>
public static class OgreMeshWeld
{
    /// <summary>Welds every submesh with vertices of its own in place; returns the number of vertices removed. A mesh with shared vertices is left as it is.</summary>
    public static int Apply(OgreMesh mesh)
    {
        if (mesh.SharedVertexData is not null) return 0;
        int removed = 0;
        for (int s = 0; s < mesh.SubMeshes.Count; s++)
            if (mesh.SubMeshes[s] is { UseSharedVertices: false, VertexData: { VertexCount: > 1 } data } sub)
                removed += Weld(mesh, s, sub, data);
        return removed;
    }

    static int Weld(OgreMesh mesh, int s, OgreSubMesh sub, OgreVertexData data)
    {
        var tangent = data.Find(OgreVertexSemantic.Tangent);
        var binormal = data.Find(OgreVertexSemantic.Binormal);
        var normal = data.Find(OgreVertexSemantic.Normal);
        if (tangent is not null && tangent.Type is not (OgreVertexElementType.Float3 or OgreVertexElementType.Float4)) return 0;
        if (binormal is not null && binormal.Type != OgreVertexElementType.Float3) return 0;
        if (normal is not null && normal.Type != OgreVertexElementType.Float3) return 0;
        int n = (int)data.VertexCount;
        var sources = data.Buffers.OrderBy(b => b.Key).ToArray();
        foreach (var (_, buffer) in sources) if (buffer.Data.Length < n * buffer.VertexSize) return 0;

        // Each vertex's bone assignments in a fixed order: one sort, then its range by a running count.
        var assignments = sub.BoneAssignments.Where(a => a.VertexIndex < n).ToArray();
        Array.Sort(assignments, (a, b) => a.VertexIndex != b.VertexIndex ? a.VertexIndex.CompareTo(b.VertexIndex) : a.BoneIndex != b.BoneIndex ? a.BoneIndex.CompareTo(b.BoneIndex) : a.Weight.CompareTo(b.Weight));
        var boneStart = new int[n + 1];
        foreach (var a in assignments) boneStart[a.VertexIndex + 1]++;
        for (int v = 0; v < n; v++) boneStart[v + 1] += boneStart[v];
        var poses = mesh.Poses.Where(p => p.Target == s + 1).ToArray();
        var poseEntries = new Dictionary<uint, List<(int Pose, OgrePoseVertex Vertex)>>();
        for (int p = 0; p < poses.Length; p++)
            foreach (var v in poses[p].Vertices)
                (poseEntries.TryGetValue(v.Index, out var list) ? list : poseEntries[v.Index] = []).Add((p, v));

        // The key: every buffer's bytes with the tangent frame's zeroed, the handedness, the bone assignments and the pose offsets.
        var groups = new Dictionary<byte[], int>(n, ByteKey.Instance);
        var lookup = groups.GetAlternateLookup<ReadOnlySpan<byte>>();
        var remap = new uint[n];
        var first = new List<int>(n);
        var key = new byte[256];
        int length = 0;
        void Put(ReadOnlySpan<byte> bytes)
        {
            if (length + bytes.Length > key.Length) Array.Resize(ref key, Math.Max(key.Length * 2, length + bytes.Length));
            bytes.CopyTo(key.AsSpan(length));
            length += bytes.Length;
        }
        Span<byte> scratch = stackalloc byte[4];
        Column? normals = Column.Of(data, normal), tangents0 = Column.Of(data, tangent), binormals0 = Column.Of(data, binormal);
        for (int v = 0; v < n; v++)
        {
            length = 0;
            foreach (var (source, buffer) in sources)
            {
                int start = length;
                Put(buffer.Data.AsSpan(v * buffer.VertexSize, buffer.VertexSize));
                if (tangent?.Source == source) key.AsSpan(start + tangent.Offset, tangent.Type.Size()).Clear();
                if (binormal?.Source == source) key.AsSpan(start + binormal.Offset, binormal.Type.Size()).Clear();
            }
            scratch[0] = Handedness(v, normals, tangents0, binormals0, tangent?.Type == OgreVertexElementType.Float4);
            Put(scratch[..1]);
            for (int b = boneStart[v]; b < boneStart[v + 1]; b++)
                {
                    BitConverter.TryWriteBytes(scratch, assignments[b].BoneIndex); Put(scratch[..2]);
                    BitConverter.TryWriteBytes(scratch, assignments[b].Weight); Put(scratch);
                }
            if (poseEntries.TryGetValue((uint)v, out var moves))
                foreach (var (p, m) in moves)
                {
                    BitConverter.TryWriteBytes(scratch, p); Put(scratch);
                    var offset = m.Offset; var turn = m.Normal;
                    Put(MemoryMarshal.AsBytes(new ReadOnlySpan<Vector3>(ref offset)));
                    Put(MemoryMarshal.AsBytes(new ReadOnlySpan<Vector3>(ref turn)));
                }
            var k = key.AsSpan(0, length);
            if (!lookup.TryGetValue(k, out int g)) { lookup[k] = g = first.Count; first.Add(v); }
            remap[v] = (uint)g;
        }
        int count = first.Count;
        if (count == n) return 0;

        // The welded tangent frame: the normalized sum of the members' (each normalized), on the first member's bytes.
        Vector3[]? tangents = tangents0 is { } tc ? Sums(tc, remap, count) : null;
        Vector3[]? binormals = binormals0 is { } bc ? Sums(bc, remap, count) : null;
        foreach (var (source, buffer) in sources)
        {
            var packed = new byte[count * buffer.VertexSize];
            for (int g = 0; g < count; g++)
                buffer.Data.AsSpan(first[g] * buffer.VertexSize, buffer.VertexSize).CopyTo(packed.AsSpan(g * buffer.VertexSize));
            if (tangent?.Source == source) Write(packed, buffer.VertexSize, tangent, tangents!);
            if (binormal?.Source == source) Write(packed, buffer.VertexSize, binormal, binormals!);
            data.Buffers[source] = new OgreVertexBuffer(buffer.VertexSize, packed);
        }
        data.VertexCount = (uint)count;

        sub.Indices = Remapped(sub.Indices, remap);
        if (mesh.Lod is { } lod)
        {
            var done = new Dictionary<OgreIndexBuffer, OgreIndexBuffer>(ReferenceEqualityComparer.Instance);
            foreach (var level in lod.Levels)
                if (s < level.Faces.Count && level.Faces[s].Buffer is { } buffer)
                    level.Faces[s] = level.Faces[s] with { Buffer = done.TryGetValue(buffer, out var made) ? made : done[buffer] = Remapped(buffer, remap) };
        }
        bool Kept(uint v) => v < n && first[(int)remap[v]] == v;
        int w = 0;
        var weights = sub.BoneAssignments;
        for (int i = 0; i < weights.Count; i++)
            if (Kept(weights[i].VertexIndex)) weights[w++] = weights[i] with { VertexIndex = remap[weights[i].VertexIndex] };
        weights.RemoveRange(w, weights.Count - w);
        foreach (var pose in poses)
        {
            w = 0;
            var moves = pose.Vertices;
            for (int i = 0; i < moves.Count; i++)
                if (Kept(moves[i].Index)) moves[w++] = moves[i] with { Index = remap[moves[i].Index] };
            moves.RemoveRange(w, moves.Count - w);
        }
        return n - count;
    }

    /// <summary>1 when the frame is left-handed (a Float4 tangent's w below 0, or the binormal against cross(normal, tangent)), else 0.</summary>
    static byte Handedness(int v, Column? normal, Column? tangent, Column? binormal, bool tangentW)
    {
        if (tangent is not { } t) return 0;
        if (tangentW) return t.Float(v, 3) < 0 ? (byte)1 : (byte)0;
        if (binormal is not { } b || normal is not { } n) return 0;
        return Vector3.Dot(Vector3.Cross(n.Vector(v), t.Vector(v)), b.Vector(v)) < 0 ? (byte)1 : (byte)0;
    }

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

    /// <summary>One float element's place in its buffer, looked up once.</summary>
    readonly record struct Column(byte[] Data, int Stride, int Offset)
    {
        public static Column? Of(OgreVertexData data, OgreVertexElement? e) => e is null ? null : new Column(data.Buffers[e.Source].Data, data.Buffers[e.Source].VertexSize, e.Offset);
        public float Float(int v, int component) => BitConverter.ToSingle(Data, v * Stride + Offset + component * 4);
        public Vector3 Vector(int v) => MemoryMarshal.Read<Vector3>(Data.AsSpan(v * Stride + Offset, 12));
    }

    static OgreIndexBuffer Remapped(OgreIndexBuffer buffer, uint[] remap)
    {
        var indices = new uint[buffer.Indices.Length];
        for (int i = 0; i < indices.Length; i++) indices[i] = buffer.Indices[i] < remap.Length ? remap[buffer.Indices[i]] : buffer.Indices[i];
        return buffer with { Indices = indices };
    }

    sealed class ByteKey : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        public static readonly ByteKey Instance = new();
        public bool Equals(byte[]? a, byte[]? b) => a.AsSpan().SequenceEqual(b);
        public int GetHashCode(byte[] k) => GetHashCode(k.AsSpan());
        public bool Equals(ReadOnlySpan<byte> a, byte[] b) => a.SequenceEqual(b);
        public int GetHashCode(ReadOnlySpan<byte> k)
        {
            // Eight bytes at a time (a vertex key is some 50 to 100 bytes, hashed once per vertex).
            ulong h = 14695981039346656037UL ^ (ulong)k.Length;
            var words = MemoryMarshal.Cast<byte, ulong>(k);
            foreach (var w in words) h = (BitOperations.RotateLeft(h ^ w, 29) * 0x9E3779B97F4A7C15UL);
            for (int i = words.Length * 8; i < k.Length; i++) h = (h ^ k[i]) * 0x100000001B3UL;
            return (int)(h ^ (h >> 32));
        }
        public byte[] Create(ReadOnlySpan<byte> k) => k.ToArray();
    }
}
