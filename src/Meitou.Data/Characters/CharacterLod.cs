using System.Numerics;
using Meitou.Data.Ogre;

namespace Meitou.Data.Characters;

/// <summary>One mesh LOD level: from which distance it is used, and the triangle-list indices of each submesh.</summary>
/// <param name="Distance">The level's user value: used once the LOD value is above it (level 0: 0).</param>
/// <param name="Indices">Per submesh (in submesh order); null for a submesh the level has no indices for.</param>
/// <param name="ManualMesh">For a manual level: the mesh file to show instead (indices are then empty).</param>
public sealed record MeshLodLevel(float Distance, IReadOnlyList<uint[]?> Indices, string? ManualMesh = null);

/// <summary>
/// Mesh LOD as Kenshi's Ogre picks it (docs/formats/ogre-mesh.md, "LOD"): every entity uses the <c>distance_sphere</c>
/// strategy, whose value is the camera's distance to the entity's world bounding-box centre minus its world bounding
/// radius (× the camera's LOD bias); the level is the last one whose distance is below that value.
/// </summary>
public static class CharacterLod
{
    /// <summary>The mesh's levels, level 0 (the full index lists) first. A mesh without LOD has one level.</summary>
    public static List<MeshLodLevel> Levels(OgreMesh mesh)
    {
        var levels = new List<MeshLodLevel> { new(0, mesh.SubMeshes.Select(s => (uint[]?)s.Indices.Indices).ToList()) };
        if (mesh.Lod is not { } lod) return levels;
        for (int l = 0; l < lod.Levels.Count; l++)
        {
            var level = lod.Levels[l];
            if (level.ManualMesh is { } manual)
            {
                levels.Add(new MeshLodLevel(level.UserValue, [], manual));
                continue;
            }
            var subs = new List<uint[]?>();
            for (int s = 0; s < mesh.SubMeshes.Count; s++)
            {
                if (s >= level.Faces.Count) { subs.Add(null); continue; }
                var faces = level.Faces[s];
                // v1.100: a level may reuse the index buffer of an earlier level (1-based), at its own start and count.
                var buffer = faces.Buffer ?? (faces.SharedBufferLevel is { } shared && shared >= 1 && shared <= lod.Levels.Count
                    && shared - 1 < l && s < lod.Levels[shared - 1].Faces.Count ? lod.Levels[shared - 1].Faces[s].Buffer : null);
                if (buffer is null) { subs.Add(null); continue; }
                long start = Math.Min(faces.IndexStart, (uint)buffer.Indices.Length);
                long count = Math.Min(faces.IndexCount, buffer.Indices.Length - start);
                subs.Add(buffer.Indices.AsSpan((int)start, (int)count).ToArray());
            }
            levels.Add(new MeshLodLevel(level.UserValue, subs));
        }
        return levels;
    }

    /// <summary>The <c>distance_sphere</c> LOD value: distance from <paramref name="eye"/> to the bounds' centre minus the radius, × bias.</summary>
    public static float Value(Vector3 eye, Vector3 boundsCentre, float radius, float bias = 1) =>
        (Vector3.Distance(eye, boundsCentre) - radius) * bias;

    /// <summary>
    /// The level for a LOD value: the number of levels after 0 whose distance is strictly below the value (Ogre's
    /// <c>LodStrategy::lodSet</c>, a lower bound over the sorted distances), at least 0.
    /// </summary>
    public static int Select(IReadOnlyList<MeshLodLevel> levels, float value)
    {
        int level = 0;
        for (int i = 1; i < levels.Count; i++)
            if (levels[i].Distance < value) level = i;
            else break;
        return level;
    }
}
