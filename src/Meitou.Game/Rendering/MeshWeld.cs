namespace Meitou.Rendering;

/// <summary>
/// The <c>weld</c> switch's Meitou side: meshes are welded as they load (<see cref="Meitou.Data.Ogre.OgreMeshWeld"/>): the corners split only by per-face
/// tangents become one vertex with the averaged tangent, smoother normal-mapped shading and about 60% fewer vertices; off: the meshes as the game stores
/// them. Read when a mesh is decoded, so a change applies to meshes loaded after it.
/// </summary>
public static class MeshWeld
{
    public static bool Enabled { get; set; } = true;

    /// <summary>The disk caches' name part for a mesh decoded with <paramref name="weld"/> (its indices number the welded vertices).</summary>
    public static string CacheVariant(bool weld) => weld ? "_weld" : "";
}
