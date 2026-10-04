using System.Numerics;

namespace Meitou.Data.Ogre;

/// <summary>
/// An Ogre <c>.mesh</c> file (Ogre's "v1" mesh format, as used by Ogre 2.0). Layout: docs/formats/ogre-mesh.md.
/// </summary>
public sealed class OgreMesh
{
    /// <summary>Serializer version from the file header, e.g. <c>[MeshSerializer_v1.100]</c>.</summary>
    public string Version { get; set; } = "";

    public bool SkeletallyAnimated { get; set; }

    /// <summary>Vertices shared by submeshes with <see cref="OgreSubMesh.UseSharedVertices"/>.</summary>
    public OgreVertexData? SharedVertexData { get; set; }

    public List<OgreSubMesh> SubMeshes { get; } = [];

    /// <summary>Name of the <c>.skeleton</c> file, if the mesh is skinned.</summary>
    public string? SkeletonName { get; set; }

    /// <summary>Bone weights for <see cref="SharedVertexData"/>.</summary>
    public List<OgreBoneAssignment> BoneAssignments { get; } = [];

    public OgreBounds? Bounds { get; set; }

    /// <summary>Submesh names by submesh index.</summary>
    public Dictionary<int, string> SubMeshNames { get; } = [];

    public OgreLod? Lod { get; set; }

    /// <summary>Poses (morph targets) in file order (chunk 0xC000; docs/formats/ogre-mesh.md).</summary>
    public List<OgrePose> Poses { get; } = [];

    /// <summary>
    /// Chunks the reader steps over without decoding: edge lists (for stencil shadows), vertex
    /// animations and per-submesh extremes. Id and count.
    /// </summary>
    public Dictionary<OgreMeshChunk, int> SkippedChunks { get; } = [];
}

/// <summary>
/// A pose: per-vertex position (and optionally normal) offsets for one vertex set. <see cref="Target"/> is 0 for the
/// shared vertices, otherwise submesh index + 1 (Ogre's convention).
/// </summary>
public sealed class OgrePose
{
    public string Name { get; set; } = "";
    public ushort Target { get; set; }
    public bool IncludesNormals { get; set; }
    public List<OgrePoseVertex> Vertices { get; } = [];
}

/// <summary>One vertex of a pose: index into the target's vertices, position offset, normal offset (zero if none).</summary>
public readonly record struct OgrePoseVertex(uint Index, Vector3 Offset, Vector3 Normal);

public sealed class OgreSubMesh
{
    public string MaterialName { get; set; } = "";
    public bool UseSharedVertices { get; set; }
    public OgreIndexBuffer Indices { get; set; } = OgreIndexBuffer.Empty;

    /// <summary>The submesh's own vertices; null when it uses the mesh's shared vertices.</summary>
    public OgreVertexData? VertexData { get; set; }

    public OgreOperationType Operation { get; set; } = OgreOperationType.TriangleList;
    public List<OgreBoneAssignment> BoneAssignments { get; } = [];
    public Dictionary<string, string> TextureAliases { get; } = [];
}

/// <summary>Indices as stored (16- or 32-bit), widened to <see cref="uint"/>.</summary>
public sealed record OgreIndexBuffer(bool Is32Bit, uint[] Indices)
{
    public static readonly OgreIndexBuffer Empty = new(false, []);
}

public sealed class OgreVertexData
{
    public uint VertexCount { get; set; }
    public List<OgreVertexElement> Elements { get; } = [];

    /// <summary>Raw vertex buffers by bind index.</summary>
    public Dictionary<ushort, OgreVertexBuffer> Buffers { get; } = [];

    public OgreVertexElement? Find(OgreVertexSemantic semantic, int index = 0) =>
        Elements.FirstOrDefault(e => e.Semantic == semantic && e.Index == index);

    /// <summary>
    /// Reads one element as floats per vertex (e.g. 3 per vertex for <see cref="OgreVertexElementType.Float3"/>).
    /// Supports the float, short/ushort (unnormalized), byte-4 and colour types.
    /// </summary>
    public float[] ReadFloats(OgreVertexElement element)
    {
        var buffer = Buffers[element.Source];
        int components = element.Type.ComponentCount();
        var result = new float[VertexCount * components];
        var data = buffer.Data.AsSpan();
        for (int v = 0; v < VertexCount; v++)
        {
            var at = data[(v * buffer.VertexSize + element.Offset)..];
            for (int c = 0; c < components; c++)
                result[v * components + c] = element.Type switch
                {
                    >= OgreVertexElementType.Float1 and <= OgreVertexElementType.Float4 => BitConverter.ToSingle(at[(c * 4)..]),
                    >= OgreVertexElementType.Short1 and <= OgreVertexElementType.Short4 => BitConverter.ToInt16(at[(c * 2)..]),
                    >= OgreVertexElementType.UShort1 and <= OgreVertexElementType.UShort4 => BitConverter.ToUInt16(at[(c * 2)..]),
                    OgreVertexElementType.UByte4 or OgreVertexElementType.Colour
                        or OgreVertexElementType.ColourArgb or OgreVertexElementType.ColourAbgr => at[c],
                    _ => throw new NotSupportedException($"Reading {element.Type} as floats is not supported."),
                };
        }
        return result;
    }

    /// <summary>Vertex positions, or null if there is no position element.</summary>
    public Vector3[]? ReadPositions()
    {
        if (Find(OgreVertexSemantic.Position) is not { } e) return null;
        var f = ReadFloats(e);
        int n = e.Type.ComponentCount();
        var result = new Vector3[VertexCount];
        for (int i = 0; i < result.Length; i++)
            result[i] = new Vector3(f[i * n], n > 1 ? f[i * n + 1] : 0, n > 2 ? f[i * n + 2] : 0);
        return result;
    }
}

public sealed record OgreVertexBuffer(ushort VertexSize, byte[] Data);

public sealed record OgreVertexElement(ushort Source, OgreVertexElementType Type, OgreVertexSemantic Semantic, ushort Offset, ushort Index);

public readonly record struct OgreBoneAssignment(uint VertexIndex, ushort BoneIndex, float Weight);

public readonly record struct OgreBounds(Vector3 Min, Vector3 Max, float Radius);

public sealed class OgreLod
{
    public string Strategy { get; set; } = "";

    /// <summary>Levels after the full-detail level 0 (which isn't stored).</summary>
    public List<OgreLodLevel> Levels { get; } = [];
}

public sealed class OgreLodLevel
{
    /// <summary>The distance (or other strategy value) at which this level starts.</summary>
    public float UserValue { get; set; }

    /// <summary>Another mesh file to use for this level, for manual LODs.</summary>
    public string? ManualMesh { get; set; }

    /// <summary>Reduced indices per submesh, for generated LODs.</summary>
    public List<OgreLodFaces> Faces { get; } = [];
}

/// <param name="IndexStart">First index used within <see cref="Buffer"/>.</param>
/// <param name="SharedBufferLevel">1-based LOD level whose buffer this level reuses, or null for its own <see cref="Buffer"/>.</param>
public sealed record OgreLodFaces(uint IndexCount, uint IndexStart, int? SharedBufferLevel, OgreIndexBuffer? Buffer);

public enum OgreOperationType : ushort
{
    PointList = 1,
    LineList = 2,
    LineStrip = 3,
    TriangleList = 4,
    TriangleStrip = 5,
    TriangleFan = 6,
}

public enum OgreVertexSemantic : ushort
{
    Position = 1,
    BlendWeights = 2,
    BlendIndices = 3,
    Normal = 4,
    Diffuse = 5,
    Specular = 6,
    TextureCoordinates = 7,
    Binormal = 8,
    Tangent = 9,
}

public enum OgreVertexElementType : ushort
{
    Float1 = 0, Float2 = 1, Float3 = 2, Float4 = 3,
    Colour = 4,
    Short1 = 5, Short2 = 6, Short3 = 7, Short4 = 8,
    UByte4 = 9,
    ColourArgb = 10, ColourAbgr = 11,
    Double1 = 12, Double2 = 13, Double3 = 14, Double4 = 15,
    UShort1 = 16, UShort2 = 17, UShort3 = 18, UShort4 = 19,
    Int1 = 20, Int2 = 21, Int3 = 22, Int4 = 23,
    UInt1 = 24, UInt2 = 25, UInt3 = 26, UInt4 = 27,
}

public static class OgreVertexElementTypeExtensions
{
    public static int ComponentCount(this OgreVertexElementType t) => t switch
    {
        OgreVertexElementType.Colour or OgreVertexElementType.ColourArgb or OgreVertexElementType.ColourAbgr
            or OgreVertexElementType.UByte4 => 4,
        >= OgreVertexElementType.Float1 and <= OgreVertexElementType.Float4 => (int)t - (int)OgreVertexElementType.Float1 + 1,
        >= OgreVertexElementType.Short1 and <= OgreVertexElementType.Short4 => (int)t - (int)OgreVertexElementType.Short1 + 1,
        >= OgreVertexElementType.Double1 and <= OgreVertexElementType.Double4 => (int)t - (int)OgreVertexElementType.Double1 + 1,
        >= OgreVertexElementType.UShort1 and <= OgreVertexElementType.UShort4 => (int)t - (int)OgreVertexElementType.UShort1 + 1,
        >= OgreVertexElementType.Int1 and <= OgreVertexElementType.Int4 => (int)t - (int)OgreVertexElementType.Int1 + 1,
        >= OgreVertexElementType.UInt1 and <= OgreVertexElementType.UInt4 => (int)t - (int)OgreVertexElementType.UInt1 + 1,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, null),
    };

    /// <summary>Size of one element in bytes.</summary>
    public static int Size(this OgreVertexElementType t) => t switch
    {
        OgreVertexElementType.Colour or OgreVertexElementType.ColourArgb or OgreVertexElementType.ColourAbgr
            or OgreVertexElementType.UByte4 => 4,
        >= OgreVertexElementType.Float1 and <= OgreVertexElementType.Float4 => 4 * t.ComponentCount(),
        >= OgreVertexElementType.Short1 and <= OgreVertexElementType.Short4 => 2 * t.ComponentCount(),
        >= OgreVertexElementType.Double1 and <= OgreVertexElementType.Double4 => 8 * t.ComponentCount(),
        >= OgreVertexElementType.UShort1 and <= OgreVertexElementType.UShort4 => 2 * t.ComponentCount(),
        _ => 4 * t.ComponentCount(),
    };
}

/// <summary>Chunk ids of the <c>.mesh</c> format.</summary>
public enum OgreMeshChunk : ushort
{
    Header = 0x1000,
    Mesh = 0x3000,
    SubMesh = 0x4000,
    SubMeshOperation = 0x4010,
    SubMeshBoneAssignment = 0x4100,
    SubMeshTextureAlias = 0x4200,
    Geometry = 0x5000,
    GeometryVertexDeclaration = 0x5100,
    GeometryVertexElement = 0x5110,
    GeometryVertexBuffer = 0x5200,
    GeometryVertexBufferData = 0x5210,
    MeshSkeletonLink = 0x6000,
    MeshBoneAssignment = 0x7000,
    MeshLodLevel = 0x8000,
    MeshLodUsage = 0x8100,
    MeshLodManual = 0x8110,
    MeshLodGenerated = 0x8120,
    MeshBounds = 0x9000,
    SubMeshNameTable = 0xA000,
    SubMeshNameTableElement = 0xA100,
    EdgeLists = 0xB000,
    EdgeListLod = 0xB100,
    EdgeGroup = 0xB110,
    Poses = 0xC000,
    Pose = 0xC100,
    PoseVertex = 0xC111,
    Animations = 0xD000,
    TableExtremes = 0xE000,
}
