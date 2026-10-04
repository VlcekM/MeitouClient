using System.Numerics;
using Meitou.Data.Ogre;

namespace Meitou.Data.Characters;

/// <summary>
/// Mesh LOD for character parts: Kenshi's <c>distance_sphere</c> rule, shared with the world's objects
/// (<see cref="MeshLod"/>; docs/formats/ogre-mesh.md, "LOD").
/// </summary>
public static class CharacterLod
{
    /// <inheritdoc cref="MeshLod.Levels"/>
    public static List<MeshLodLevel> Levels(OgreMesh mesh) => MeshLod.Levels(mesh);

    /// <inheritdoc cref="MeshLod.Value"/>
    public static float Value(Vector3 eye, Vector3 boundsCentre, float radius, float bias = 1) => MeshLod.Value(eye, boundsCentre, radius, bias);

    /// <inheritdoc cref="MeshLod.Select(IReadOnlyList{MeshLodLevel}, float)"/>
    public static int Select(IReadOnlyList<MeshLodLevel> levels, float value) => MeshLod.Select(levels, value);
}
