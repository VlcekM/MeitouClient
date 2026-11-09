using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// The instances of one mesh in one zone, with the range of the layer that placed them.
/// </summary>
sealed class FoliageGroup
{
    /// <summary>
    /// The mesh.
    /// </summary>
    public required FoliageMeshAsset Asset;

    /// <summary>
    /// The layer's range at setting 1 and its transition: the drawn range follows the current <see cref="FoliageRenderer.RangeSetting"/>
    /// (<see cref="FoliageRenderer.RangeOf"/>).
    /// </summary>
    public required float BaseRange, Transition;

    /// <summary>
    /// Placed by a FAR layer (<see cref="FoliageLayout.IsFarLayer"/>).
    /// </summary>
    public required bool Far;

    /// <summary>
    /// The largest instance scale: how far a mesh's bounds can reach from its instance's position (the zone's cull box).
    /// </summary>
    public required float MaxScale;

    /// <summary>
    /// The zone the group is in (its cull box: the GPU path's per-cascade batch test, <see cref="FoliageRenderer.ShowBatches"/>).
    /// </summary>
    public required FoliageZoneState Zone;

    /// <summary>
    /// The instances (<see cref="FoliageCull"/>'s records), their bounding spheres filled once the mesh's bounds are known
    /// (<see cref="SpheresReady"/>).
    /// </summary>
    public required FoliageInstanceRecord[] Instances;

    /// <summary>
    /// The instances' bounding spheres are filled.
    /// </summary>
    public bool SpheresReady;

    /// <summary>
    /// Where <see cref="Instances"/> are in the GPU cull's arena, and the arena generation they were placed in (0: never).
    /// </summary>
    public ArenaRange Arena;

    /// <summary>
    /// The arena generation <see cref="Arena"/> was placed in (0: never).
    /// </summary>
    public int ArenaGeneration;

    /// <summary>
    /// A box around every instance's bounding sphere (with a unit of slack for rounding), set with the spheres: the GPU path draws a batch in a
    /// view only when one of its groups' boxes meets the frustum (<see cref="FoliageRenderer.ShowBatches"/>).
    /// </summary>
    public Vector3 BoundMin, BoundMax;

    /// <summary>
    /// <see cref="BoundMin"/> and <see cref="BoundMax"/> are set.
    /// </summary>
    public bool BoundsReady;

    /// <summary>
    /// The smallest and largest bounding radius among the instances (set with <see cref="BoundMin"/>): which generated levels a view can pick in
    /// the group.
    /// </summary>
    public float MinRadius, MaxRadius;

    /// <summary>
    /// A TERRAIN-mode rock group on the GPU cull: <see cref="Instances"/>' <c>Ground.W</c> holds <see cref="FoliageCull.RockBits"/>, made with
    /// the terrain's biome source <see cref="RockBiomes"/> (<see cref="TerrainRenderer.FeatureBiomes"/>); whether it has plain and mirroring
    /// placements, and whether its first one mirrors (the order its two batches are numbered in).
    /// </summary>
    public bool RockReady, RockPlain, RockMirrored, RockFirstMirrored;

    /// <summary>
    /// With rock impostors on (<see cref="FoliageRenderer.RockImpostorsActive"/>): the records are sorted by biome row (stable) and
    /// <see cref="RockSegments"/> are the runs of one row (<c>Row</c> -1: no biome), in order; the rows are what the impostor atlases are made
    /// for (docs/impostors.md section 13).
    /// </summary>
    public bool RockSorted;

    /// <summary>
    /// The runs of one biome row in <see cref="Instances"/> (<see cref="RockSorted"/>).
    /// </summary>
    public (int Row, int Start, int Length)[] RockSegments = [];

    /// <summary>
    /// The terrain's biome source <see cref="RockReady"/>'s bits were made with.
    /// </summary>
    public object? RockBiomes;

    /// <summary>
    /// Sets <see cref="BoundMin"/>, <see cref="BoundMax"/>, <see cref="MinRadius"/> and <see cref="MaxRadius"/> from the instances' spheres.
    /// </summary>
    public void ComputeBounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        (MinRadius, MaxRadius) = (float.MaxValue, 0);
        foreach (ref readonly var r in Instances.AsSpan())
        {
            (MinRadius, MaxRadius) = (Math.Min(MinRadius, r.Sphere.W), Math.Max(MaxRadius, r.Sphere.W));
            var c = new Vector3(r.Sphere.X, r.Sphere.Y, r.Sphere.Z);
            min = Vector3.Min(min, c - new Vector3(r.Sphere.W + 1));
            max = Vector3.Max(max, c + new Vector3(r.Sphere.W + 1));
        }
        (BoundMin, BoundMax, BoundsReady) = (min, max, true);
    }
}
