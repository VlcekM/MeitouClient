using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// A part of a <see cref="FoliageRayMesh"/>: its buffers (the drawn ones), its index count, and the alpha threshold of a cut-out part (0: opaque).
/// </summary>
internal readonly record struct FoliageRayPart(DeviceBuffer Vertices, DeviceBuffer Indices, int IndexCount, float AlphaThreshold);

/// <summary>
/// A resident foliage mesh as the traced scene takes it: the main mesh's parts and then the leaves', keyed by the asset's main
/// <see cref="FoliageGpuMesh"/> (a reload makes a new one). <see cref="Textures"/>: per part, this frame's bindless indices of its diffuse map and
/// of the map whose alpha cuts it out (the normal map, as the draws cut: <c>AlphaSource</c> 2), 0 when absent or not resident
/// (<see cref="FoliageRayScene.RefreshTextures"/>).
/// </summary>
internal sealed class FoliageRayMesh
{
    /// <summary>
    /// The asset's main mesh it was made for.
    /// </summary>
    public required object Key;

    /// <summary>
    /// The main mesh's parts, then the leaves'.
    /// </summary>
    public required FoliageRayPart[] Parts;

    /// <summary>
    /// A TERRAIN-mode rock.
    /// </summary>
    public required bool Rock;

    /// <summary>
    /// Per part this frame's bindless indices of its diffuse map and of its cut-out map (0: absent or not resident).
    /// </summary>
    public required (uint Diffuse, uint Alpha)[] Textures;

    /// <summary>
    /// Per part its diffuse map and its cut-out map.
    /// </summary>
    internal required (WorldTexture? Diffuse, WorldTexture? Alpha)[] Maps;

    /// <summary>
    /// The frame <see cref="Textures"/> were set in (-1: never).
    /// </summary>
    internal long Stamp = -1;
}

/// <summary>
/// The foliage in the ray-traced scene of the global illumination (docs/render-gi.md "Foliage"): the instances big enough to matter, and the
/// meshes as the traced scene takes them.
/// </summary>
sealed class FoliageRayScene(IGpuContext gpu, WorldTextureCache textures, Dictionary<ZoneCoordinate, FoliageZoneState> zones)
{
    #region Traced foliage

    /// <summary>
    /// The traced meshes by asset (made again when the asset's main mesh is a new one).
    /// </summary>
    readonly Dictionary<FoliageMeshAsset, FoliageRayMesh> _rayMeshes = [];

    /// <summary>
    /// Counts the foliage meshes deleted: a traced scene built before a change holds structures and records of freed buffers.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>
    /// The usage a mesh buffer needs to be built into the traced scene (none without ray queries).
    /// </summary>
    public BufferUse Input => gpu.HasRayQuery ? BufferUse.RayInput : 0;

    /// <summary>
    /// A foliage mesh's buffers are being deleted: the traced scene drops what it built from them (<see cref="Generation"/>).
    /// </summary>
    public void MeshDeleted() => Generation++;

    /// <summary>
    /// Adds every laid-out instance whose bounding radius is at least <paramref name="minRadius"/> and whose bounding sphere comes within
    /// <paramref name="range"/> of <paramref name="eye"/>, with its mesh, transform and distance. Render thread (it may register bindless entries).
    /// </summary>
    public void Instances(Vector3 eye, float range, float minRadius, List<(FoliageRayMesh Mesh, Matrix4x4 Transform, float Distance)> into)
    {
        foreach (var zone in zones.Values)
        {
            if (!zone.Ready) continue;
            foreach (var g in zone.Groups)
            {
                if (!g.BoundsReady || !g.SpheresReady || g.MaxRadius < minRadius) continue;
                var nearest = Vector3.Clamp(eye, g.BoundMin, g.BoundMax);
                if (Vector3.Distance(nearest, eye) > range) continue;
                if (MeshFor(g.Asset) is not { } mesh) continue;
                foreach (ref readonly var r in g.Instances.AsSpan())
                {
                    if (r.Sphere.W < minRadius) continue;
                    float d = Vector3.Distance(new Vector3(r.Sphere.X, r.Sphere.Y, r.Sphere.Z), eye) - r.Sphere.W;
                    if (d <= range) into.Add((mesh, r.Transform, d));
                }
            }
        }
    }

    /// <summary>
    /// Sets <paramref name="mesh"/>'s <see cref="FoliageRayMesh.Textures"/> for this frame (once a frame). Render thread (it may register bindless
    /// entries).
    /// </summary>
    public void RefreshTextures(FoliageRayMesh mesh)
    {
        long stamp = gpu.Frame.Number;
        if (mesh.Stamp == stamp) return;
        mesh.Stamp = stamp;
        for (int i = 0; i < mesh.Parts.Length; i++)
            mesh.Textures[i] = (Texture(mesh.Maps[i].Diffuse), Texture(mesh.Maps[i].Alpha));
    }

    /// <summary>
    /// The traced mesh of a resident asset (made on first use and when its main mesh was replaced), null when not resident.
    /// </summary>
    FoliageRayMesh? MeshFor(FoliageMeshAsset a)
    {
        if (!a.Resident || a.Main is not { } main) return null;
        if (_rayMeshes.TryGetValue(a, out var m) && ReferenceEquals(m.Key, main)) return m;
        var parts = new List<FoliageRayPart>();
        var maps = new List<(WorldTexture?, WorldTexture?)>();
        float mainCut = a.MainMaterial?.AlphaThreshold ?? 0;
        foreach (var p in main.Parts)
        {
            parts.Add(new FoliageRayPart(p.Vertices, p.Indices, p.Count, mainCut));
            maps.Add((a.MainMaterial?.Diffuse, mainCut > 0 ? a.MainMaterial?.Normal : null));
        }
        if (a.Leaves is { } leaves)
            foreach (var p in leaves.Parts)
            {
                // Leaves without a threshold still cut out at a half: an opaque card would shade like a wall.
                parts.Add(new FoliageRayPart(p.Vertices, p.Indices, p.Count,
                    a.LeavesMaterial is { AlphaThreshold: > 0 } lm ? lm.AlphaThreshold : 0.5f));
                maps.Add((a.LeavesMaterial?.Diffuse, a.LeavesMaterial?.Normal));
            }
        m = new FoliageRayMesh { Key = main, Parts = [.. parts], Rock = a.Terrain, Textures = new (uint, uint)[parts.Count], Maps = [.. maps] };
        _rayMeshes[a] = m;
        return m;
    }

    /// <summary>
    /// A texture's bindless index at the context's LOD bias (0: none, or not resident).
    /// </summary>
    uint Texture(WorldTexture? t) => t is { Key: > 0 } ? textures.Index(t.Key, gpu.LodBias, 0) : 0;

    #endregion
}
