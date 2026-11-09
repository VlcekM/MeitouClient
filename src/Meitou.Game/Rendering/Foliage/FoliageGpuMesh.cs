using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// A foliage mesh (the main one or the leaves) on the GPU: its parts.
/// </summary>
sealed class FoliageGpuMesh
{
    /// <summary>
    /// The parts with indices, in the mesh's order.
    /// </summary>
    public List<FoliageGpuPart> Parts = [];
}

/// <summary>
/// A mesh part on the GPU: its vertex and index buffers (native, "foliage meshes"), the vertex attributes at locations 0 to 4 as the GL vertex
/// array had them, and for the TERRAIN-mode rocks the mesh as the terrain's mesh path takes it, made when first asked for
/// (<see cref="FoliageRenderer.PlainMesh"/>).
/// </summary>
sealed class FoliageGpuPart
{
    /// <summary>
    /// The vertex and index buffers.
    /// </summary>
    public required DeviceBuffer Vertices, Indices;

    /// <summary>
    /// The vertex attributes at locations 0 to 4 (<see cref="FoliageRenderer.VertexAttributes"/>).
    /// </summary>
    public LegacyProgram.Attribute?[] Attributes = [];

    /// <summary>
    /// The mesh as the terrain's mesh path takes it (TERRAIN-mode rocks; made when first asked for).
    /// </summary>
    public MeshBindings? Plain;

    /// <summary>
    /// What a native draw needs, per program (<see cref="FoliageNativeMesh"/>).
    /// </summary>
    public FoliageNativeMesh ColourNative, DepthNative, ColourSolidNative, PrepassNative, PrepassSolidNative;

    /// <summary>
    /// The original indices.
    /// </summary>
    public int Count;

    /// <summary>
    /// Blades after each 1/64 of the candidates (<see cref="FoliageGrassField.BladesWithPrefixes"/>): the density setting draws a prefix.
    /// </summary>
    public int[] Prefixes = [];

    /// <summary>
    /// The vertices carry tangents, and colours.
    /// </summary>
    public bool HasTangents, HasColours;

    /// <summary>
    /// The cards-trimmed index list after the original <see cref="Count"/> indices in <see cref="Indices"/> (0: none; card trimming).
    /// </summary>
    public int TrimCount;
}

/// <summary>
/// A mesh's native state for one program: its vertex layout and own vertex buffers, its indices, and the pipelines for the last two segment
/// states (the reflection's multisampled target alternates with the scene's).
/// </summary>
struct FoliageNativeMesh
{
    /// <summary>
    /// The vertex layout (null before the first draw).
    /// </summary>
    public VertexLayout? Layout;

    /// <summary>
    /// The program's own vertex buffers.
    /// </summary>
    public GpuBufferBinding[] Vertices;

    /// <summary>
    /// The indices.
    /// </summary>
    public GpuBufferBinding Elements;

    /// <summary>
    /// The segment states <see cref="PipeA"/> and <see cref="PipeB"/> were made for.
    /// </summary>
    public int SegA, SegB;

    /// <summary>
    /// The pipelines for the last two segment states.
    /// </summary>
    public GpuPipeline? PipeA, PipeB;
}
