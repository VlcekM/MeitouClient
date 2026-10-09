using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>The foliage in the ray-traced scene of the global illumination (docs/render-gi.md "Foliage"): the instances big enough to matter.</summary>
public sealed partial class FoliageRenderer
{
    /// <summary>A part of a <see cref="RayMesh"/>: its buffers (the drawn ones), its index count, and the alpha threshold of a cut-out part (0: opaque).</summary>
    internal readonly record struct RayPart(DeviceBuffer Vertices, DeviceBuffer Indices, int IndexCount, float AlphaThreshold);

    /// <summary>
    /// A resident foliage mesh as the traced scene takes it: the main mesh's parts and then the leaves', keyed by the asset's main
    /// <see cref="GpuMesh"/> (a reload makes a new one). <see cref="Textures"/>: per part, this frame's bindless indices of its diffuse map and of
    /// the map whose alpha cuts it out (the normal map, as the draws cut: <c>AlphaSource</c> 2), 0 when absent or not resident (<see cref="RefreshRayTextures"/>).
    /// </summary>
    internal sealed class RayMesh
    {
        public required object Key;
        public required RayPart[] Parts;
        public required bool Rock;
        public required (uint Diffuse, uint Alpha)[] Textures;
        internal required (WorldTexture? Diffuse, WorldTexture? Alpha)[] Maps;
        internal long Stamp = -1;
    }

    readonly Dictionary<MeshAsset, RayMesh> rayMeshes = [];
    /// <summary>The usage a mesh buffer needs to be built into the traced scene (none without ray queries).</summary>
    BufferUse RayInput => Gpu.Device.HasRayQuery ? BufferUse.RayInput : 0;

    /// <summary>
    /// Adds every laid-out instance whose bounding radius is at least <paramref name="minRadius"/> and whose bounding sphere comes within
    /// <paramref name="range"/> of <paramref name="eye"/>, with its mesh, transform and distance. Render thread (it may register bindless entries).
    /// </summary>
    internal void RayInstances(Vector3 eye, float range, float minRadius, List<(RayMesh Mesh, Matrix4x4 Transform, float Distance)> into)
    {
        foreach (var zone in zones.Values)
        {
            if (!zone.Ready) continue;
            foreach (var g in zone.Groups)
            {
                if (!g.BoundsReady || !g.SpheresReady || g.MaxRadius < minRadius) continue;
                var nearest = Vector3.Clamp(eye, g.BoundMin, g.BoundMax);
                if (Vector3.Distance(nearest, eye) > range) continue;
                if (RayMeshFor(g.Asset) is not { } mesh) continue;
                foreach (ref readonly var r in g.Instances.AsSpan())
                {
                    if (r.Sphere.W < minRadius) continue;
                    float d = Vector3.Distance(new Vector3(r.Sphere.X, r.Sphere.Y, r.Sphere.Z), eye) - r.Sphere.W;
                    if (d <= range) into.Add((mesh, r.Transform, d));
                }
            }
        }
    }

    /// <summary>Counts the foliage meshes deleted: a traced scene built before a change holds structures and records of freed buffers.</summary>
    internal int RayGeneration { get; private set; }

    /// <summary>Sets <paramref name="mesh"/>'s <see cref="RayMesh.Textures"/> for this frame (once a frame). Render thread (it may register bindless entries).</summary>
    internal void RefreshRayTextures(RayMesh mesh)
    {
        long stamp = Gpu.Frame.Number;
        if (mesh.Stamp == stamp) return;
        mesh.Stamp = stamp;
        for (int i = 0; i < mesh.Parts.Length; i++)
            mesh.Textures[i] = (RayTexture(mesh.Maps[i].Diffuse), RayTexture(mesh.Maps[i].Alpha));
    }

    RayMesh? RayMeshFor(MeshAsset a)
    {
        if (!a.Resident || a.Main is not { } main) return null;
        if (rayMeshes.TryGetValue(a, out var m) && ReferenceEquals(m.Key, main)) return m;
        var parts = new List<RayPart>();
        var maps = new List<(WorldTexture?, WorldTexture?)>();
        float mainCut = a.MainMaterial?.AlphaThreshold ?? 0;
        foreach (var p in main.Parts)
        {
            parts.Add(new RayPart(p.Vertices, p.Indices, p.Count, mainCut));
            maps.Add((a.MainMaterial?.Diffuse, mainCut > 0 ? a.MainMaterial?.Normal : null));
        }
        if (a.Leaves is { } leaves)
            foreach (var p in leaves.Parts)
            {
                // Leaves without a threshold still cut out at a half: an opaque card would shade like a wall.
                parts.Add(new RayPart(p.Vertices, p.Indices, p.Count, a.LeavesMaterial is { AlphaThreshold: > 0 } lm ? lm.AlphaThreshold : 0.5f));
                maps.Add((a.LeavesMaterial?.Diffuse, a.LeavesMaterial?.Normal));
            }
        m = new RayMesh { Key = main, Parts = [.. parts], Rock = a.Terrain, Textures = new (uint, uint)[parts.Count], Maps = [.. maps] };
        rayMeshes[a] = m;
        return m;
    }

    uint RayTexture(WorldTexture? t) => t is { Key: > 0 } ? textures.Index(t.Key, Gpu.LodBias, 0) : 0;
}
