using System.Numerics;
using static Meitou.Data.Ogre.OgreMeshChunk;

namespace Meitou.Data.Ogre;

/// <summary>
/// Reads Ogre <c>.mesh</c> files, serializer versions 1.100, 1.8 and 1.41 (everything Kenshi ships).
/// Follows the structure of Ogre's own MeshSerializerImpl (MIT): sections are parsed field by field,
/// not by trusting chunk lengths, which old exporters sometimes got wrong.
/// </summary>
public static class OgreMeshReader
{
    public const string Version1_100 = "[MeshSerializer_v1.100]";
    public const string Version1_8 = "[MeshSerializer_v1.8]";
    public const string Version1_41 = "[MeshSerializer_v1.41]";

    public static OgreMesh ReadFile(string path) => Read(File.ReadAllBytes(path));

    public static OgreMesh Read(byte[] data)
    {
        var s = new OgreStream(data);
        try
        {
            var mesh = new OgreMesh { Version = s.ReadHeader() };
            if (mesh.Version is not (Version1_100 or Version1_8 or Version1_41))
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

    static void ReadMesh(OgreStream s, OgreMesh mesh)
    {
        mesh.SkeletallyAnimated = s.ReadBool();
        while (s.TryReadChunk([Geometry, SubMesh, MeshSkeletonLink, MeshBoneAssignment, MeshLodLevel, MeshBounds,
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
                case MeshLodLevel:
                    mesh.Lod = mesh.Version == Version1_100 ? ReadLod(s, mesh) : ReadLodV1_8(s, mesh);
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

    // Ogre's readPoses / readPose (ogre-next v2-0): POSE chunks with name, target and (from 1.8) an includes-normals
    // flag, each followed by POSE_VERTEX chunks (vertex index, offset, normal if flagged). 1.41 has no normals.
    static void ReadPoses(OgreStream s, OgreMesh mesh)
    {
        while (s.TryReadChunk([Pose], out _, out _))
        {
            var pose = new OgrePose { Name = s.ReadString(), Target = s.ReadUInt16() };
            pose.IncludesNormals = mesh.Version != Version1_41 && s.ReadBool();
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

    static void Count(OgreMesh mesh, OgreMeshChunk chunk) =>
        mesh.SkippedChunks[chunk] = mesh.SkippedChunks.GetValueOrDefault(chunk) + 1;

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

    static OgreBoneAssignment ReadBoneAssignment(OgreStream s) => new(s.ReadUInt32(), s.ReadUInt16(), s.ReadFloat());

    static OgreIndexBuffer ReadIndices(OgreStream s, uint count, bool is32)
    {
        var indices = new uint[count];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = is32 ? s.ReadUInt32() : s.ReadUInt16();
        return new OgreIndexBuffer(is32, indices);
    }

    // Version 1.100: each level is a MANUAL or GENERATED chunk (usage value first); generated levels can share buffers.
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

    // Versions 1.8 / 1.41: a "manual" flag for all levels; each level is a USAGE chunk holding a MANUAL chunk or one GENERATED chunk per submesh.
    static OgreLod ReadLodV1_8(OgreStream s, OgreMesh mesh)
    {
        var lod = new OgreLod { Strategy = s.ReadString() };
        int levels = s.ReadUInt16();
        bool manual = s.ReadBool();
        for (int level = 1; level < levels; level++)
        {
            Expect(s, MeshLodUsage);
            var entry = new OgreLodLevel { UserValue = s.ReadFloat() };
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

    static void Expect(OgreStream s, OgreMeshChunk chunk)
    {
        var (id, _) = s.ReadChunk();
        if (id != (ushort)chunk)
            throw new OgreFormatException($"Expected chunk {chunk}, found 0x{id:X4}.", s.Position - 6);
    }
}

/// <summary>Little-endian cursor over an Ogre serializer stream.</summary>
sealed class OgreStream(byte[] data)
{
    public const int ChunkHeaderSize = 6;

    public int Position { get; private set; }
    public bool Eof => Position >= data.Length;

    /// <summary>Reads the 0x1000 header id and the version string.</summary>
    public string ReadHeader()
    {
        ushort id = ReadUInt16();
        if (id == 0x0010)
            throw new OgreFormatException("Big-endian Ogre files are not supported.", 0);
        if (id != 0x1000)
            throw new OgreFormatException("Not an Ogre serializer file (no 0x1000 header).", 0);
        return ReadString();
    }

    public (ushort Id, int Length) ReadChunk() => (ReadUInt16(), checked((int)ReadUInt32()));

    /// <summary>
    /// Reads the next chunk header if it is one of <paramref name="allowed"/>; otherwise leaves the position
    /// unchanged (Ogre's "backpedal") so the parent can handle it.
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

    public bool ReadBool() => data[Position++] != 0;

    public ushort ReadUInt16()
    {
        var v = BitConverter.ToUInt16(data, Position);
        Position += 2;
        return v;
    }

    public uint ReadUInt32()
    {
        var v = BitConverter.ToUInt32(data, Position);
        Position += 4;
        return v;
    }

    public float ReadFloat()
    {
        var v = BitConverter.ToSingle(data, Position);
        Position += 4;
        return v;
    }

    public Vector3 ReadVector3() => new(ReadFloat(), ReadFloat(), ReadFloat());

    /// <summary>Ogre writes quaternions as x, y, z, w (its in-memory order is w first; the serializer reorders).</summary>
    public Quaternion ReadQuaternion() => new(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());

    public byte[] ReadBytes(int count)
    {
        if (count < 0 || count > data.Length - Position) throw new IndexOutOfRangeException();
        var result = data.AsSpan(Position, count).ToArray();
        Position += count;
        return result;
    }

    public void Skip(int count)
    {
        if (count < 0 || count > data.Length - Position) throw new IndexOutOfRangeException();
        Position += count;
    }

    /// <summary>Ogre strings end at a newline (a trailing carriage return is dropped).</summary>
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

public sealed class OgreFormatException(string message, long offset, Exception? inner = null)
    : FormatException($"{message} (at byte {offset})", inner)
{
    public long Offset { get; } = offset;
}
