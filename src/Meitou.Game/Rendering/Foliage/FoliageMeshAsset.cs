using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Impostors;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// One foliage mesh record as <see cref="FoliageRenderer"/> holds it: its decode, its meshes and materials on the GPU, its bounds and size class,
/// its generated levels and its impostor.
/// </summary>
sealed class FoliageMeshAsset
{
    /// <summary>
    /// The mesh record.
    /// </summary>
    public required FoliageMesh Mesh;

    /// <summary>
    /// Its meshes are welded (<see cref="MeshWeld"/>, as it was when the decode began): its LOD levels must be made from the same vertices.
    /// </summary>
    public bool Weld;

    /// <summary>
    /// The decode in flight: the main mesh and the leaves mesh.
    /// </summary>
    public Task<(Model? Main, Model? Leaves)>? Job;

    /// <summary>
    /// The main mesh and the leaves mesh on the GPU (null while not resident).
    /// </summary>
    public FoliageGpuMesh? Main, Leaves;

    /// <summary>
    /// On the GPU; its decode failed; its upload steps are queued.
    /// </summary>
    public bool Resident, Failed, Uploading;

    /// <summary>
    /// When it was last drawn (tick), and the GPU bytes it holds.
    /// </summary>
    public long LastUsed, Bytes;

    /// <summary>
    /// The materials of the main mesh and of the leaves.
    /// </summary>
    public FoliageMaterial? MainMaterial, LeavesMaterial;

    /// <summary>
    /// World length per texture-coordinate unit of the mesh and of its leaves, before the instance's scale (<see cref="FoliageRenderer.TexelScale"/>;
    /// infinity: unknown, the textures keep every mip). Known with <see cref="HasBounds"/>.
    /// </summary>
    public float MainTexel = float.PositiveInfinity, LeavesTexel = float.PositiveInfinity;

    /// <summary>
    /// A TERRAIN-mode mesh (material type 2).
    /// </summary>
    public bool Terrain => Mesh.MaterialType == 2;

    /// <summary>
    /// The bounding sphere's centre in mesh units (known with <see cref="HasBounds"/>).
    /// </summary>
    public Vector3 Centre;

    /// <summary>
    /// The bounding sphere's radius in mesh units (known with <see cref="HasBounds"/>).
    /// </summary>
    public float Radius = 1;

    /// <summary>
    /// The bounding box's half extents (main and leaves meshes), for the tight billboard quad (<see cref="Impostors.ImpostorQuad"/>).
    /// </summary>
    public Vector3 HalfExtent;

    /// <summary>
    /// Radius × largest scale (<see cref="FoliageSizes.Size"/>), known with <see cref="HasBounds"/>: the shadow cascades skip meshes too small
    /// for their texels.
    /// </summary>
    public float Size;

    /// <summary>
    /// <see cref="Centre"/>, <see cref="Radius"/> and <see cref="SizeClass"/> are known (the mesh was decoded once; they never change).
    /// </summary>
    public bool HasBounds;

    /// <summary>
    /// The size class (<see cref="FoliageSizes"/>), known with <see cref="HasBounds"/>.
    /// </summary>
    public FoliageSizeClass SizeClass;

    /// <summary>
    /// Billboarded from <see cref="FoliageRenderer.LargeImpostorDistance"/> rather than <see cref="FoliageRenderer.ImpostorDistance"/>
    /// (<see cref="FoliageSizes.LargeBillboard"/>; trees are not).
    /// </summary>
    public bool LargeBillboard;

    /// <summary>
    /// The triangles of the mesh and its leaves mesh (known with <see cref="HasBounds"/>): the small impostor class is for meshes with many.
    /// </summary>
    public int Triangles;

    /// <summary>
    /// The mean triangle edge of the mesh and its leaves mesh in mesh units: the square root of the mean triangle area (known with
    /// <see cref="HasBounds"/>; <c>screen-lod</c>, <see cref="FoliageScreenLod.TriangleExtent(IEnumerable{float})"/>).
    /// </summary>
    public float TriExtent;

    /// <summary>
    /// The <c>screen-lod</c> transition terms of this mesh (ground distance per unit of instance radius, and the atlas frame floor) and what they
    /// were worked out for.
    /// </summary>
    public float ScreenPerRadian, ScreenFloor;

    /// <summary>
    /// What <see cref="ScreenPerRadian"/> and <see cref="ScreenFloor"/> were worked out for.
    /// </summary>
    public (float, float, int, int) ScreenKey;

    /// <summary>
    /// The impostor class estimated from the mesh's own bounds, once (<see cref="FoliageRenderer.EstimateClass"/>).
    /// </summary>
    public ImpostorClass? EstimatedClass;

    /// <summary>
    /// <see cref="EstimatedClass"/> was worked out.
    /// </summary>
    public bool ClassEstimated;

    /// <summary>
    /// The GPU work list that last numbered this mesh's batch (<see cref="FoliageRenderer.BuildGpuWork"/>), and the batch's number in it.
    /// </summary>
    public int WorkStamp, WorkIndex;

    /// <summary>
    /// For a TERRAIN-mode mesh: the work list that last numbered its rock batches, and their numbers among the rock batches (plain placements,
    /// mirroring placements; -1: none in the work list).
    /// </summary>
    public int RockStamp, RockPlainIndex, RockMirroredIndex;

    /// <summary>
    /// Generated levels (<see cref="FoliageRockLod"/>, TERRAIN-mode meshes only): uploaded, and the set the current GPU work list was built with.
    /// </summary>
    public FoliageRockLod? Lod, WorkLod;

    /// <summary>
    /// The level job ran for the current residency.
    /// </summary>
    public bool LodDone;

    /// <summary>
    /// Its impostor (<see cref="FoliageRenderer.Impostors"/>): null until asked for.
    /// </summary>
    public FoliageImpostorState? Impostor;

    /// <summary>
    /// A TERRAIN-mode rock's impostors are per biome (the terrain material is the biome's at each instance, docs/impostors.md section 13). The
    /// rock's own asset has <see cref="RockVariants"/>, one holder per biome row; a holder (<see cref="RockOf"/> the rock's asset,
    /// <see cref="RockRow"/> the row) carries only the <see cref="Impostor"/> state and a copy of the bounds, and is never in
    /// <c>assetsByMesh</c>.
    /// </summary>
    public FoliageMeshAsset? RockOf;

    /// <summary>
    /// A holder's biome row (-1: the rock's own asset).
    /// </summary>
    public int RockRow = -1;

    /// <summary>
    /// The rock's holders by biome row.
    /// </summary>
    public Dictionary<int, FoliageMeshAsset>? RockVariants;
}

/// <summary>
/// A foliage mesh's material (main or leaves): its textures, cut-out threshold, sidedness, mapping, tiling, emission, specular and surface flags.
/// </summary>
sealed record FoliageMaterial(WorldTexture? Diffuse, WorldTexture? Normal, WorldTexture? Diffuse2, WorldTexture? Normal2,
    float AlphaThreshold, bool DoubleSided, bool Triplanar, Vector2 Tile, bool Emissive, float Specular, uint Surface);
