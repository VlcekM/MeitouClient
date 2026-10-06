namespace Meitou.Rendering.Gpu;

/// <summary>A vertex attribute of an interleaved mesh vertex: its location, component count and byte offset; floats, or with
/// <paramref name="Integer"/> unsigned bytes read as integers.</summary>
public readonly record struct MeshAttribute(uint Location, int Size, int Offset, bool Integer = false);

/// <summary>
/// A mesh as a draw path that takes meshes of several owners reads it (<see cref="TerrainRenderer.DrawMeshes"/>): its vertex attributes by
/// location and its index buffer. Compared by reference: the owner makes one per mesh (and level) and keeps it while the buffers live.
/// </summary>
public sealed class MeshBindings(LegacyProgram.Attribute?[] attributes, BufferBinding elements)
{
    public LegacyProgram.Attribute?[] Attributes { get; } = attributes;
    public BufferBinding Elements { get; } = elements;

    /// <summary>The attributes over the owner's vertex buffer, and the whole of <paramref name="elements"/> as the indices.</summary>
    public static MeshBindings Of(LegacyProgram.Attribute?[] attributes, DeviceBuffer elements) =>
        new(attributes, new BufferBinding(elements.Handle, 0, elements.Size));
}
