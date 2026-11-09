using System.Numerics;

namespace Meitou.Data.Ogre;

/// <summary>
/// An Ogre <c>.mesh</c> file (Ogre's "v1" mesh format, as used by Ogre 2.0). Layout: docs/formats/ogre-mesh.md.
/// </summary>
public sealed class OgreMesh
{
    /// <summary>
    /// Serializer version from the file header, e.g. <c>[MeshSerializer_v1.100]</c>.
    /// </summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// The header's skeletally-animated flag.
    /// </summary>
    public bool SkeletallyAnimated { get; set; }

    /// <summary>
    /// Vertices shared by submeshes with <see cref="OgreSubMesh.UseSharedVertices"/>.
    /// </summary>
    public OgreVertexData? SharedVertexData { get; set; }

    /// <summary>
    /// The submeshes in file order.
    /// </summary>
    public List<OgreSubMesh> SubMeshes { get; } = [];

    /// <summary>
    /// Name of the <c>.skeleton</c> file, if the mesh is skinned.
    /// </summary>
    public string? SkeletonName { get; set; }

    /// <summary>
    /// Bone weights for <see cref="SharedVertexData"/>.
    /// </summary>
    public List<OgreBoneAssignment> BoneAssignments { get; } = [];

    /// <summary>
    /// The bounding box and radius (chunk 0x9000); null when the file has none.
    /// </summary>
    public OgreBounds? Bounds { get; set; }

    /// <summary>
    /// Submesh names by submesh index.
    /// </summary>
    public Dictionary<int, string> SubMeshNames { get; } = [];

    /// <summary>
    /// The level-of-detail levels (chunk 0x8000); null when the mesh has none.
    /// </summary>
    public OgreLod? Lod { get; set; }

    /// <summary>
    /// Poses (morph targets) in file order (chunk 0xC000; docs/formats/ogre-mesh.md).
    /// </summary>
    public List<OgrePose> Poses { get; } = [];

    /// <summary>
    /// Chunks the reader steps over without decoding: edge lists (for stencil shadows), vertex animations and per-submesh extremes. Id and count.
    /// </summary>
    public Dictionary<OgreMeshChunk, int> SkippedChunks { get; } = [];
}

/// <summary>
/// A pose: per-vertex position (and optionally normal) offsets for one vertex set. <see cref="Target"/> is 0 for the shared vertices, otherwise
/// submesh index + 1 (Ogre's convention).
/// </summary>
public sealed class OgrePose
{
    /// <summary>
    /// The pose's name.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// The vertex set the pose moves: 0 the shared vertices, else submesh index + 1.
    /// </summary>
    public ushort Target { get; set; }
    /// <summary>
    /// Whether the pose's vertices carry normal offsets too.
    /// </summary>
    public bool IncludesNormals { get; set; }
    /// <summary>
    /// The pose's vertex offsets in file order.
    /// </summary>
    public List<OgrePoseVertex> Vertices { get; } = [];
}

/// <summary>
/// One vertex of a pose: index into the target's vertices, position offset, normal offset (zero if none).
/// </summary>
public readonly record struct OgrePoseVertex(uint Index, Vector3 Offset, Vector3 Normal);

/// <summary>
/// A submesh: its material, its indices and its own or the shared vertices.
/// </summary>
public sealed class OgreSubMesh
{
    /// <summary>
    /// The name of the material the submesh is drawn with.
    /// </summary>
    public string MaterialName { get; set; } = "";
    /// <summary>
    /// Whether the submesh's indices refer to the mesh's <see cref="OgreMesh.SharedVertexData"/>.
    /// </summary>
    public bool UseSharedVertices { get; set; }
    /// <summary>
    /// The submesh's index buffer; <see cref="OgreIndexBuffer.Empty"/> until read.
    /// </summary>
    public OgreIndexBuffer Indices { get; set; } = OgreIndexBuffer.Empty;

    /// <summary>
    /// The submesh's own vertices; null when it uses the mesh's shared vertices.
    /// </summary>
    public OgreVertexData? VertexData { get; set; }

    /// <summary>
    /// How the indices form primitives (chunk 0x4010); default triangle list.
    /// </summary>
    public OgreOperationType Operation { get; set; } = OgreOperationType.TriangleList;
    /// <summary>
    /// Bone weights for the submesh's own <see cref="VertexData"/>.
    /// </summary>
    public List<OgreBoneAssignment> BoneAssignments { get; } = [];
    /// <summary>
    /// <c>set_texture_alias</c> pairs of the submesh (chunk 0x4200): alias, texture name.
    /// </summary>
    public Dictionary<string, string> TextureAliases { get; } = [];
}

/// <summary>
/// Indices as stored (16- or 32-bit), widened to <see cref="uint"/>.
/// </summary>
public sealed record OgreIndexBuffer(bool Is32Bit, uint[] Indices)
{
    /// <summary>
    /// No indices.
    /// </summary>
    public static readonly OgreIndexBuffer Empty = new(false, []);
}

/// <summary>
/// A vertex set: its count, the element declaration and the raw buffers.
/// </summary>
public sealed class OgreVertexData
{
    /// <summary>
    /// How many vertices each buffer holds.
    /// </summary>
    public uint VertexCount { get; set; }
    /// <summary>
    /// The vertex declaration's elements in file order.
    /// </summary>
    public List<OgreVertexElement> Elements { get; } = [];

    /// <summary>
    /// Raw vertex buffers by bind index.
    /// </summary>
    public Dictionary<ushort, OgreVertexBuffer> Buffers { get; } = [];

    /// <summary>
    /// The element with <paramref name="semantic"/> and <paramref name="index"/> (e.g. the second texture coordinate set is index 1); null
    /// when absent.
    /// </summary>
    public OgreVertexElement? Find(OgreVertexSemantic semantic, int index = 0) =>
        Elements.FirstOrDefault(e => e.Semantic == semantic && e.Index == index);

    /// <summary>
    /// Reads one element as floats per vertex (e.g. 3 per vertex for <see cref="OgreVertexElementType.Float3"/>). Supports the float, short/ushort
    /// (unnormalized), byte-4 and colour types.
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

    /// <summary>
    /// Vertex positions, or null if there is no position element.
    /// </summary>
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

/// <summary>
/// One vertex buffer: bytes per vertex and the raw data.
/// </summary>
public sealed record OgreVertexBuffer(ushort VertexSize, byte[] Data);

/// <summary>
/// One element of a vertex declaration: the buffer it reads from, its type and meaning, its byte offset in a vertex and its index among elements of
/// that meaning.
/// </summary>
public sealed record OgreVertexElement(ushort Source, OgreVertexElementType Type, OgreVertexSemantic Semantic, ushort Offset, ushort Index);

/// <summary>
/// One bone weight: the vertex, the bone (index into the skeleton) and the weight.
/// </summary>
public readonly record struct OgreBoneAssignment(uint VertexIndex, ushort BoneIndex, float Weight);

/// <summary>
/// A mesh's bounding box and bounding sphere radius, in mesh space.
/// </summary>
public readonly record struct OgreBounds(Vector3 Min, Vector3 Max, float Radius);

/// <summary>
/// A mesh's level-of-detail levels (chunk 0x8000).
/// </summary>
public sealed class OgreLod
{
    /// <summary>
    /// The LOD strategy name, e.g. distance.
    /// </summary>
    public string Strategy { get; set; } = "";

    /// <summary>
    /// Levels after the full-detail level 0 (which isn't stored).
    /// </summary>
    public List<OgreLodLevel> Levels { get; } = [];
}

/// <summary>
/// One LOD level after the full-detail one.
/// </summary>
public sealed class OgreLodLevel
{
    /// <summary>
    /// The distance (or other strategy value) at which this level starts.
    /// </summary>
    public float UserValue { get; set; }

    /// <summary>
    /// Another mesh file to use for this level, for manual LODs.
    /// </summary>
    public string? ManualMesh { get; set; }

    /// <summary>
    /// Reduced indices per submesh, for generated LODs.
    /// </summary>
    public List<OgreLodFaces> Faces { get; } = [];
}

/// <param name="IndexStart">First index used within <see cref="Buffer"/>.</param>
/// <param name="SharedBufferLevel">1-based LOD level whose buffer this level reuses, or null for its own <see cref="Buffer"/>.</param>
public sealed record OgreLodFaces(uint IndexCount, uint IndexStart, int? SharedBufferLevel, OgreIndexBuffer? Buffer);

/// <summary>
/// How a submesh's indices form primitives (Ogre's <c>RenderOperation::OperationType</c>).
/// </summary>
public enum OgreOperationType : ushort
{
    /// <summary>
    /// Each index is a point.
    /// </summary>
    PointList = 1,
    /// <summary>
    /// Each pair of indices is a line.
    /// </summary>
    LineList = 2,
    /// <summary>
    /// The indices form one connected line.
    /// </summary>
    LineStrip = 3,
    /// <summary>
    /// Each three indices are a triangle.
    /// </summary>
    TriangleList = 4,
    /// <summary>
    /// Each index after the first two makes a triangle with the previous two.
    /// </summary>
    TriangleStrip = 5,
    /// <summary>
    /// Each index after the first two makes a triangle with the first and the previous one.
    /// </summary>
    TriangleFan = 6,
}

/// <summary>
/// What a vertex element holds (Ogre's <c>VertexElementSemantic</c>).
/// </summary>
public enum OgreVertexSemantic : ushort
{
    /// <summary>
    /// The vertex position.
    /// </summary>
    Position = 1,
    /// <summary>
    /// Skinning weights.
    /// </summary>
    BlendWeights = 2,
    /// <summary>
    /// Skinning bone indices.
    /// </summary>
    BlendIndices = 3,
    /// <summary>
    /// The vertex normal.
    /// </summary>
    Normal = 4,
    /// <summary>
    /// The diffuse vertex colour.
    /// </summary>
    Diffuse = 5,
    /// <summary>
    /// The specular vertex colour.
    /// </summary>
    Specular = 6,
    /// <summary>
    /// A texture coordinate set (<see cref="OgreVertexElement.Index"/> picks which).
    /// </summary>
    TextureCoordinates = 7,
    /// <summary>
    /// The binormal (bitangent).
    /// </summary>
    Binormal = 8,
    /// <summary>
    /// The tangent.
    /// </summary>
    Tangent = 9,
}

/// <summary>
/// How a vertex element is stored (Ogre's <c>VertexElementType</c>).
/// </summary>
public enum OgreVertexElementType : ushort
{
    /// <summary>
    /// One float.
    /// </summary>
    Float1 = 0,

    /// <summary>
    /// Two floats.
    /// </summary>
    Float2 = 1,

    /// <summary>
    /// Three floats.
    /// </summary>
    Float3 = 2,

    /// <summary>
    /// Four floats.
    /// </summary>
    Float4 = 3,

    /// <summary>
    /// A packed colour of four bytes in the render system's order.
    /// </summary>
    Colour = 4,

    /// <summary>
    /// One 16-bit signed integer.
    /// </summary>
    Short1 = 5,

    /// <summary>
    /// Two 16-bit signed integers.
    /// </summary>
    Short2 = 6,

    /// <summary>
    /// Three 16-bit signed integers.
    /// </summary>
    Short3 = 7,

    /// <summary>
    /// Four 16-bit signed integers.
    /// </summary>
    Short4 = 8,

    /// <summary>
    /// Four bytes.
    /// </summary>
    UByte4 = 9,

    /// <summary>
    /// A packed colour of four bytes, ARGB.
    /// </summary>
    ColourArgb = 10,

    /// <summary>
    /// A packed colour of four bytes, ABGR.
    /// </summary>
    ColourAbgr = 11,

    /// <summary>
    /// One double.
    /// </summary>
    Double1 = 12,

    /// <summary>
    /// Two doubles.
    /// </summary>
    Double2 = 13,

    /// <summary>
    /// Three doubles.
    /// </summary>
    Double3 = 14,

    /// <summary>
    /// Four doubles.
    /// </summary>
    Double4 = 15,

    /// <summary>
    /// One 16-bit unsigned integer.
    /// </summary>
    UShort1 = 16,

    /// <summary>
    /// Two 16-bit unsigned integers.
    /// </summary>
    UShort2 = 17,

    /// <summary>
    /// Three 16-bit unsigned integers.
    /// </summary>
    UShort3 = 18,

    /// <summary>
    /// Four 16-bit unsigned integers.
    /// </summary>
    UShort4 = 19,

    /// <summary>
    /// One 32-bit signed integer.
    /// </summary>
    Int1 = 20,

    /// <summary>
    /// Two 32-bit signed integers.
    /// </summary>
    Int2 = 21,

    /// <summary>
    /// Three 32-bit signed integers.
    /// </summary>
    Int3 = 22,

    /// <summary>
    /// Four 32-bit signed integers.
    /// </summary>
    Int4 = 23,

    /// <summary>
    /// One 32-bit unsigned integer.
    /// </summary>
    UInt1 = 24,

    /// <summary>
    /// Two 32-bit unsigned integers.
    /// </summary>
    UInt2 = 25,

    /// <summary>
    /// Three 32-bit unsigned integers.
    /// </summary>
    UInt3 = 26,

    /// <summary>
    /// Four 32-bit unsigned integers.
    /// </summary>
    UInt4 = 27,
}

/// <summary>
/// Sizes of the vertex element types.
/// </summary>
public static class OgreVertexElementTypeExtensions
{
    /// <summary>
    /// How many values an element of type <paramref name="t"/> holds; a colour or UByte4 counts four.
    /// </summary>
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

    /// <summary>
    /// Size of one element in bytes.
    /// </summary>
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

/// <summary>
/// Chunk ids of the <c>.mesh</c> format.
/// </summary>
public enum OgreMeshChunk : ushort
{
    /// <summary>
    /// The file header with the serializer version.
    /// </summary>
    Header = 0x1000,
    /// <summary>
    /// The mesh: the skeletally-animated flag, then its sub-chunks.
    /// </summary>
    Mesh = 0x3000,
    /// <summary>
    /// A submesh: material, shared-vertices flag and indices.
    /// </summary>
    SubMesh = 0x4000,
    /// <summary>
    /// A submesh's operation type.
    /// </summary>
    SubMeshOperation = 0x4010,
    /// <summary>
    /// A bone weight of a submesh's own vertices.
    /// </summary>
    SubMeshBoneAssignment = 0x4100,
    /// <summary>
    /// A submesh's texture alias.
    /// </summary>
    SubMeshTextureAlias = 0x4200,
    /// <summary>
    /// A vertex set with its vertex count.
    /// </summary>
    Geometry = 0x5000,
    /// <summary>
    /// A vertex set's declaration.
    /// </summary>
    GeometryVertexDeclaration = 0x5100,
    /// <summary>
    /// One element of a declaration.
    /// </summary>
    GeometryVertexElement = 0x5110,
    /// <summary>
    /// A vertex buffer's bind index and vertex size.
    /// </summary>
    GeometryVertexBuffer = 0x5200,
    /// <summary>
    /// A vertex buffer's data.
    /// </summary>
    GeometryVertexBufferData = 0x5210,
    /// <summary>
    /// The name of the mesh's <c>.skeleton</c>.
    /// </summary>
    MeshSkeletonLink = 0x6000,
    /// <summary>
    /// A bone weight of the shared vertices.
    /// </summary>
    MeshBoneAssignment = 0x7000,
    /// <summary>
    /// The LOD block: strategy and level count.
    /// </summary>
    MeshLodLevel = 0x8000,
    /// <summary>
    /// One LOD level's value.
    /// </summary>
    MeshLodUsage = 0x8100,
    /// <summary>
    /// A manual LOD level's mesh name.
    /// </summary>
    MeshLodManual = 0x8110,
    /// <summary>
    /// A generated LOD level's indices of one submesh.
    /// </summary>
    MeshLodGenerated = 0x8120,
    /// <summary>
    /// The bounding box and radius.
    /// </summary>
    MeshBounds = 0x9000,
    /// <summary>
    /// The table of submesh names.
    /// </summary>
    SubMeshNameTable = 0xA000,
    /// <summary>
    /// One submesh's name.
    /// </summary>
    SubMeshNameTableElement = 0xA100,
    /// <summary>
    /// Edge lists for stencil shadows; walked, not kept.
    /// </summary>
    EdgeLists = 0xB000,
    /// <summary>
    /// The edge list of one LOD; not kept.
    /// </summary>
    EdgeListLod = 0xB100,
    /// <summary>
    /// An edge group; not kept.
    /// </summary>
    EdgeGroup = 0xB110,
    /// <summary>
    /// The poses (morph targets).
    /// </summary>
    Poses = 0xC000,
    /// <summary>
    /// One pose.
    /// </summary>
    Pose = 0xC100,
    /// <summary>
    /// One vertex offset of a pose.
    /// </summary>
    PoseVertex = 0xC111,
    /// <summary>
    /// Vertex animations; skipped by length.
    /// </summary>
    Animations = 0xD000,
    /// <summary>
    /// A submesh's extreme points; skipped by length.
    /// </summary>
    TableExtremes = 0xE000,
}