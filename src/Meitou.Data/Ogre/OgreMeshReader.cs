using System.Numerics;
using static Meitou.Data.Ogre.OgreMeshChunk;

namespace Meitou.Data.Ogre;

/// <summary>
/// Reads Ogre <c>.mesh</c> files, serializer versions 1.100, 1.8 and 1.41 (everything Kenshi ships) and 1.40 (common in mods). Follows the structure
/// of Ogre's own MeshSerializerImpl (MIT): sections are parsed field by field, not by trusting chunk lengths, which old exporters sometimes got
/// wrong.
/// </summary>
public static class OgreMeshReader
{
    /// <summary>
    /// The header of serializer version 1.100 (Ogre 2.0's), which stores LOD levels as manual or generated chunks.
    /// </summary>
    public const string Version1100 = "[MeshSerializer_v1.100]";
    /// <summary>
    /// The header of serializer version 1.8.
    /// </summary>
    public const string Version18 = "[MeshSerializer_v1.8]";
    /// <summary>
    /// The header of serializer version 1.41: poses have no normals.
    /// </summary>
    public const string Version141 = "[MeshSerializer_v1.41]";
    /// <summary>
    /// The header of serializer version 1.40 (common in mods): no LOD strategy name, LOD distances squared.
    /// </summary>
    public const string Version140 = "[MeshSerializer_v1.40]";

    /// <summary>
    /// <see cref="Read"/> of the file at <paramref name="path"/>, its case resolved as on disk.
    /// </summary>
    public static OgreMesh ReadFile(string path) => Read(File.ReadAllBytes(Content.GameFiles.Resolve(path)));

    /// <summary>
    /// The mesh's bounds box without reading its geometry: walks the <c>M_MESH</c> sub-chunks by their length fields (seeking over the vertex and
    /// index data) to <c>M_MESH_BOUNDS</c>. False when the walk meets a chunk it does not know, runs off the file, or the file has no bounds: the
    /// lengths of old exporters can be wrong (see the class comment), so the caller then reads the whole file with <see cref="ReadFile"/>.
    /// </summary>
    public static bool TryReadBounds(string path, out OgreBounds bounds)
    {
        bounds = default;
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            Span<byte> head = stackalloc byte[6];
            if (f.Read(head) < 6 || BitConverter.ToUInt16(head) != 0x1000) return false;
            // The version line: "[MeshSerializer_v1.x]" and a newline; its bytes sit between the id and the first chunk.
            f.Position = 2;
            var line = new byte[40];
            int n = f.Read(line, 0, line.Length);
            int end = Array.IndexOf(line, (byte)'\n', 0, n);
            if (end < 0) return false;
            string version = System.Text.Encoding.ASCII.GetString(line, 0, end).TrimEnd('\r');
            if (version is not (Version1100 or Version18 or Version141 or Version140)) return false;
            f.Position = 2 + end + 1;
            Span<byte> chunk = stackalloc byte[6];
            if (f.Read(chunk) < 6 || BitConverter.ToUInt16(chunk) != (ushort)Mesh) return false;
            if (f.ReadByte() < 0) return false;   // skeletally animated
            while (f.Position + 6 <= f.Length)
            {
                if (f.Read(chunk) < 6) return false;
                ushort id = BitConverter.ToUInt16(chunk);
                long length = BitConverter.ToUInt32(chunk[2..]);
                if (id == (ushort)MeshBounds)
                {
                    Span<byte> box = stackalloc byte[28];
                    if (f.Read(box) < 28) return false;
                    var min = new Vector3(BitConverter.ToSingle(box), BitConverter.ToSingle(box[4..]), BitConverter.ToSingle(box[8..]));
                    var max = new Vector3(BitConverter.ToSingle(box[12..]), BitConverter.ToSingle(box[16..]), BitConverter.ToSingle(box[20..]));
                    bounds = new OgreBounds(min, max, BitConverter.ToSingle(box[24..]));
                    return float.IsFinite(min.X + min.Y + min.Z + max.X + max.Y + max.Z);
                }
                if (id is not ((ushort)Geometry or (ushort)SubMesh or (ushort)MeshSkeletonLink or (ushort)MeshBoneAssignment
                    or (ushort)OgreMeshChunk.MeshLodLevel or (ushort)SubMeshNameTable or (ushort)EdgeLists or (ushort)Poses or (ushort)Animations
                    or (ushort)TableExtremes)) return false;
                long next = f.Position + length - 6;
                if (length < 6 || next > f.Length) return false;
                f.Position = next;
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a whole mesh file; <see cref="OgreFormatException"/> for an unsupported version, an unexpected chunk or a file cut short.
    /// </summary>
    public static OgreMesh Read(byte[] data)
    {
        var s = new OgreStream(data);
        try
        {
            var mesh = new OgreMesh { Version = s.ReadHeader() };
            if (mesh.Version is not (Version1100 or Version18 or Version141 or Version140))
                throw new OgreFormatException($"Unsupported mesh version {mesh.Version}.", 0);

            while (!s.Eof)
            {
                var (id, _) = s.ReadChunk();
                if (id != (ushort)Mesh)
                    throw new OgreFormatException($"Unexpected top-level chunk 0x{id:X4}.", s.Position - 6);
                ReadMesh(s, mesh);
            }
            return mesh;
        }
        catch (IndexOutOfRangeException e)
        {
            throw new OgreFormatException("Unexpected end of file.", s.Position, e);
        }
        catch (ArgumentException e)
        {
            throw new OgreFormatException("Unexpected end of file.", s.Position, e);
        }
    }

    /// <summary>
    /// Reads an <c>M_MESH</c> chunk: its flag, then its sub-chunks; edge lists are walked and animations and extremes skipped, each counted in
    /// <see cref="OgreMesh.SkippedChunks"/>.
    /// </summary>
    static void ReadMesh(OgreStream s, OgreMesh mesh)
    {
        mesh.SkeletallyAnimated = s.ReadBool();
        while (s.TryReadChunk([Geometry, SubMesh, MeshSkeletonLink, MeshBoneAssignment, OgreMeshChunk.MeshLodLevel, MeshBounds,
                   SubMeshNameTable, EdgeLists, Poses, Animations, TableExtremes], out var chunk, out int length))
        {
            switch (chunk)
            {
                case Geometry:
                    mesh.SharedVertexData = ReadGeometry(s);
                    break;
                case SubMesh:
                    mesh.SubMeshes.Add(ReadSubMesh(s));
                    break;
                case MeshSkeletonLink:
                    mesh.SkeletonName = s.ReadString();
                    break;
                case MeshBoneAssignment:
                    mesh.BoneAssignments.Add(ReadBoneAssignment(s));
                    break;
                case OgreMeshChunk.MeshLodLevel:
                    mesh.Lod = mesh.Version == Version1100 ? ReadLod(s, mesh) : ReadLodV1_8(s, mesh);
                    break;
                case MeshBounds:
                    mesh.Bounds = new OgreBounds(s.ReadVector3(), s.ReadVector3(), s.ReadFloat());
                    break;
                case SubMeshNameTable:
                    while (s.TryReadChunk([SubMeshNameTableElement], out _, out _))
                    {
                        int index = s.ReadUInt16();
                        mesh.SubMeshNames[index] = s.ReadString();
                    }
                    break;
                case EdgeLists:
                    SkipEdgeLists(s);
                    Count(mesh, chunk);
                    break;
                case Poses:
                    ReadPoses(s, mesh);
                    break;
                default:
                    // Animations, extremes: not needed yet, skipped by length.
                    s.Skip(length - OgreStream.ChunkHeaderSize);
                    Count(mesh, chunk);
                    break;
            }
        }
    }

    /// <summary>
    /// Ogre's readPoses / readPose (ogre-next v2-0): POSE chunks with name, target and (from 1.8) an includes-normals flag, each followed by
    /// POSE_VERTEX chunks (vertex index, offset, normal if flagged). 1.41 has no normals. The poses go to <see cref="OgreMesh.Poses"/>.
    /// </summary>
    static void ReadPoses(OgreStream s, OgreMesh mesh)
    {
        while (s.TryReadChunk([Pose], out _, out _))
        {
            var pose = new OgrePose { Name = s.ReadString(), Target = s.ReadUInt16() };
            pose.IncludesNormals = mesh.Version is not (Version141 or Version140) && s.ReadBool();
            while (s.TryReadChunk([PoseVertex], out _, out _))
            {
                uint index = s.ReadUInt32();
                var offset = s.ReadVector3();
                var normal = pose.IncludesNormals ? s.ReadVector3() : Vector3.Zero;
                pose.Vertices.Add(new OgrePoseVertex(index, offset, normal));
            }
            mesh.Poses.Add(pose);
        }
    }

    /// <summary>
    /// Counts one more skipped <paramref name="chunk"/> in <see cref="OgreMesh.SkippedChunks"/>.
    /// </summary>
    static void Count(OgreMesh mesh, OgreMeshChunk chunk) =>
        mesh.SkippedChunks[chunk] = mesh.SkippedChunks.GetValueOrDefault(chunk) + 1;

    /// <summary>
    /// Reads a submesh: material, shared-vertices flag, indices, its own geometry when it has some, then its operation, bone weights and
    /// texture aliases.
    /// </summary>
    static OgreSubMesh ReadSubMesh(OgreStream s)
    {
        var sub = new OgreSubMesh
        {
            MaterialName = s.ReadString(),
            UseSharedVertices = s.ReadBool(),
        };
        uint indexCount = s.ReadUInt32();
        bool is32 = s.ReadBool();
        sub.Indices = ReadIndices(s, indexCount, is32);

        if (!sub.UseSharedVertices)
        {
            var (id, _) = s.ReadChunk();
            if (id != (ushort)Geometry)
                throw new OgreFormatException("Submesh without its own geometry.", s.Position - 6);
            sub.VertexData = ReadGeometry(s);
        }

        while (s.TryReadChunk([SubMeshBoneAssignment, SubMeshOperation, SubMeshTextureAlias], out var chunk, out _))
        {
            switch (chunk)
            {
                case SubMeshOperation:
                    sub.Operation = (OgreOperationType)s.ReadUInt16();
                    break;
                case SubMeshBoneAssignment:
                    sub.BoneAssignments.Add(ReadBoneAssignment(s));
                    break;
                case SubMeshTextureAlias:
                    var alias = s.ReadString();
                    sub.TextureAliases[alias] = s.ReadString();
                    break;
            }
        }
        return sub;
    }

    /// <summary>
    /// Reads a vertex set: the vertex count, the declaration and each buffer, whose vertex size must match its declared elements.
    /// </summary>
    static OgreVertexData ReadGeometry(OgreStream s)
    {
        var data = new OgreVertexData { VertexCount = s.ReadUInt32() };
        while (s.TryReadChunk([GeometryVertexDeclaration, GeometryVertexBuffer], out var chunk, out _))
        {
            if (chunk == GeometryVertexDeclaration)
            {
                while (s.TryReadChunk([GeometryVertexElement], out _, out _))
                    data.Elements.Add(new OgreVertexElement(
                        Source: s.ReadUInt16(),
                        Type: (OgreVertexElementType)s.ReadUInt16(),
                        Semantic: (OgreVertexSemantic)s.ReadUInt16(),
                        Offset: s.ReadUInt16(),
                        Index: s.ReadUInt16()));
                continue;
            }

            ushort bindIndex = s.ReadUInt16(), vertexSize = s.ReadUInt16();
            var (id, _) = s.ReadChunk();
            if (id != (ushort)GeometryVertexBufferData)
                throw new OgreFormatException("Vertex buffer without data.", s.Position - 6);
            int declared = data.Elements.Where(e => e.Source == bindIndex).Sum(e => e.Type.Size());
            if (declared != vertexSize)
                throw new OgreFormatException($"Vertex buffer {bindIndex} is {vertexSize} bytes per vertex, its declaration {declared}.", s.Position);
            data.Buffers[bindIndex] = new OgreVertexBuffer(vertexSize, s.ReadBytes(checked((int)(data.VertexCount * vertexSize))));
        }
        return data;
    }

    /// <summary>
    /// Reads one bone weight: vertex index, bone index, weight.
    /// </summary>
    static OgreBoneAssignment ReadBoneAssignment(OgreStream s) => new(s.ReadUInt32(), s.ReadUInt16(), s.ReadFloat());

    /// <summary>
    /// Reads <paramref name="count"/> indices of 32 bits when <paramref name="is32"/>, else 16.
    /// </summary>
    static OgreIndexBuffer ReadIndices(OgreStream s, uint count, bool is32)
    {
        var indices = new uint[count];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = is32 ? s.ReadUInt32() : s.ReadUInt16();
        return new OgreIndexBuffer(is32, indices);
    }

    /// <summary>
    /// Version 1.100: each level is a MANUAL or GENERATED chunk (usage value first); generated levels can share buffers. Level 0, the full mesh, is
    /// not stored.
    /// </summary>
    static OgreLod ReadLod(OgreStream s, OgreMesh mesh)
    {
        var lod = new OgreLod { Strategy = s.ReadString() };
        int levels = s.ReadUInt16();
        for (int level = 1; level < levels; level++)
        {
            var (id, _) = s.ReadChunk();
            var entry = new OgreLodLevel { UserValue = s.ReadFloat() };
            switch ((OgreMeshChunk)id)
            {
                case MeshLodManual:
                    entry.ManualMesh = s.ReadString();
                    break;
                case MeshLodGenerated:
                    foreach (var _ in mesh.SubMeshes)
                    {
                        uint count = s.ReadUInt32(), start = s.ReadUInt32(), bufferIndex = s.ReadUInt32();
                        if (bufferIndex == uint.MaxValue)
                        {
                            bool is32 = s.ReadBool();
                            uint bufferCount = s.ReadUInt32();
                            entry.Faces.Add(new OgreLodFaces(count, start, null, ReadIndices(s, bufferCount, is32)));
                        }
                        else
                        {
                            entry.Faces.Add(new OgreLodFaces(count, start, (int)bufferIndex, null));
                        }
                    }
                    break;
                default:
                    throw new OgreFormatException($"Unknown LOD chunk 0x{id:X4}.", s.Position - 10);
            }
            lod.Levels.Add(entry);
        }
        return lod;
    }

    /// <summary>
    /// Versions 1.8 / 1.41: a "manual" flag for all levels; each level is a USAGE chunk holding a MANUAL chunk or one GENERATED chunk per submesh.
    /// </summary>
    static OgreLod ReadLodV1_8(OgreStream s, OgreMesh mesh)
    {
        // 1.40 has no strategy name and stores each level's distance squared (docs/formats/ogre-mesh.md).
        bool v140 = mesh.Version == Version140;
        var lod = new OgreLod { Strategy = v140 ? "Distance" : s.ReadString() };
        int levels = s.ReadUInt16();
        bool manual = s.ReadBool();
        for (int level = 1; level < levels; level++)
        {
            Expect(s, MeshLodUsage);
            float value = s.ReadFloat();
            var entry = new OgreLodLevel { UserValue = v140 ? MathF.Sqrt(value) : value };
            if (manual)
            {
                Expect(s, MeshLodManual);
                entry.ManualMesh = s.ReadString();
            }
            else
            {
                foreach (var _ in mesh.SubMeshes)
                {
                    Expect(s, MeshLodGenerated);
                    uint count = s.ReadUInt32();
                    bool is32 = s.ReadBool();
                    entry.Faces.Add(new OgreLodFaces(count, 0, null, ReadIndices(s, count, is32)));
                }
            }
            lod.Levels.Add(entry);
        }
        return lod;
    }

    /// <summary>
    /// Walks the edge list chunks field by field without keeping them.
    /// </summary>
    static void SkipEdgeLists(OgreStream s)
    {
        while (s.TryReadChunk([EdgeListLod], out _, out _))
        {
            s.ReadUInt16(); // LOD index
            if (s.ReadBool()) continue; // manual LOD: edges live in the other mesh
            s.ReadBool(); // closed
            uint triangles = s.ReadUInt32(), groups = s.ReadUInt32();
            s.Skip(checked((int)(triangles * (8 * 4 + 4 * 4))));
            for (uint g = 0; g < groups; g++)
            {
                Expect(s, EdgeGroup);
                s.Skip(3 * 4); // vertex set, first triangle, triangle count
                uint edges = s.ReadUInt32();
                s.Skip(checked((int)(edges * (6 * 4 + 1))));
            }
        }
    }

    /// <summary>
    /// Reads a chunk header that must be <paramref name="chunk"/>; <see cref="OgreFormatException"/> otherwise.
    /// </summary>
    static void Expect(OgreStream s, OgreMeshChunk chunk)
    {
        var (id, _) = s.ReadChunk();
        if (id != (ushort)chunk)
            throw new OgreFormatException($"Expected chunk {chunk}, found 0x{id:X4}.", s.Position - 6);
    }
}

/// <summary>
/// Little-endian cursor over an Ogre serializer stream.
/// </summary>
sealed class OgreStream(byte[] data)
{
    /// <summary>
    /// Bytes of a chunk header: a ushort id and a uint length that counts the header too.
    /// </summary>
    public const int ChunkHeaderSize = 6;

    /// <summary>
    /// The read position in the file.
    /// </summary>
    public int Position { get; private set; }
    /// <summary>
    /// Whether the whole file has been read.
    /// </summary>
    public bool Eof => Position >= data.Length;

    /// <summary>
    /// Reads the 0x1000 header id and the version string.
    /// </summary>
    public string ReadHeader()
    {
        ushort id = ReadUInt16();
        if (id == 0x0010)
            throw new OgreFormatException("Big-endian Ogre files are not supported.", 0);
        if (id != 0x1000)
            throw new OgreFormatException("Not an Ogre serializer file (no 0x1000 header).", 0);
        return ReadString();
    }

    /// <summary>
    /// Reads a chunk header: its id and length (header included).
    /// </summary>
    public (ushort Id, int Length) ReadChunk() => (ReadUInt16(), checked((int)ReadUInt32()));

    /// <summary>
    /// Reads the next chunk header if it is one of <paramref name="allowed"/>; otherwise leaves the position unchanged (Ogre's "backpedal") so the
    /// parent can handle it.
    /// </summary>
    public bool TryReadChunk(ReadOnlySpan<OgreMeshChunk> allowed, out OgreMeshChunk chunk, out int length)
    {
        chunk = default;
        length = 0;
        if (data.Length - Position < ChunkHeaderSize) return false;
        var id = (OgreMeshChunk)BitConverter.ToUInt16(data, Position);
        if (!allowed.Contains(id)) return false;
        chunk = id;
        Position += 2;
        length = checked((int)ReadUInt32());
        return true;
    }

    /// <inheritdoc cref="TryReadChunk(ReadOnlySpan{OgreMeshChunk}, out OgreMeshChunk, out int)"/>
    public bool TryReadChunk(ReadOnlySpan<OgreSkeletonChunk> allowed, out OgreSkeletonChunk chunk, out int length)
    {
        chunk = default;
        length = 0;
        if (data.Length - Position < ChunkHeaderSize) return false;
        var id = (OgreSkeletonChunk)BitConverter.ToUInt16(data, Position);
        if (!allowed.Contains(id)) return false;
        chunk = id;
        Position += 2;
        length = checked((int)ReadUInt32());
        return true;
    }

    /// <summary>
    /// Reads a one-byte boolean.
    /// </summary>
    public bool ReadBool() => data[Position++] != 0;

    /// <summary>
    /// Reads a little-endian ushort.
    /// </summary>
    public ushort ReadUInt16()
    {
        var v = BitConverter.ToUInt16(data, Position);
        Position += 2;
        return v;
    }

    /// <summary>
    /// Reads a little-endian uint.
    /// </summary>
    public uint ReadUInt32()
    {
        var v = BitConverter.ToUInt32(data, Position);
        Position += 4;
        return v;
    }

    /// <summary>
    /// Reads a little-endian float.
    /// </summary>
    public float ReadFloat()
    {
        var v = BitConverter.ToSingle(data, Position);
        Position += 4;
        return v;
    }

    /// <summary>
    /// Reads three floats as x, y, z.
    /// </summary>
    public Vector3 ReadVector3() => new(ReadFloat(), ReadFloat(), ReadFloat());

    /// <summary>
    /// Ogre writes quaternions as x, y, z, w (its in-memory order is w first; the serializer reorders).
    /// </summary>
    public Quaternion ReadQuaternion() => new(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());

    /// <summary>
    /// Reads <paramref name="count"/> bytes into a new array; <see cref="IndexOutOfRangeException"/> past the end.
    /// </summary>
    public byte[] ReadBytes(int count)
    {
        if (count < 0 || count > data.Length - Position) throw new IndexOutOfRangeException();
        var result = data.AsSpan(Position, count).ToArray();
        Position += count;
        return result;
    }

    /// <summary>
    /// Moves past <paramref name="count"/> bytes; <see cref="IndexOutOfRangeException"/> past the end.
    /// </summary>
    public void Skip(int count)
    {
        if (count < 0 || count > data.Length - Position) throw new IndexOutOfRangeException();
        Position += count;
    }

    /// <summary>
    /// Ogre strings end at a newline (a trailing carriage return is dropped).
    /// </summary>
    public string ReadString()
    {
        int end = Array.IndexOf(data, (byte)'\n', Position);
        if (end < 0) throw new IndexOutOfRangeException();
        int length = end - Position;
        if (length > 0 && data[end - 1] == '\r') length--;
        var s = System.Text.Encoding.UTF8.GetString(data, Position, length);
        Position = end + 1;
        return s;
    }
}

/// <summary>
/// A malformed or unsupported Ogre binary file, with the byte offset where reading stopped.
/// </summary>
public sealed class OgreFormatException(string message, long offset, Exception? inner = null)
    : FormatException($"{message} (at byte {offset})", inner)
{
    /// <summary>
    /// The byte offset in the file where the problem was found.
    /// </summary>
    public long Offset { get; } = offset;
}