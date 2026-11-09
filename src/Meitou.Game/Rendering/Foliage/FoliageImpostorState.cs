using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Impostors;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// Where a mesh's impostor atlas is: read from the cache (or its source made), baking, uploading, drawable, or none (refused or unloaded).
/// </summary>
enum FoliageImpostorStage
{
    /// <summary>
    /// Being read from the disk cache (or its source made).
    /// </summary>
    Loading,
    /// <summary>
    /// Waiting for or in its bake.
    /// </summary>
    Baking,
    /// <summary>
    /// Its textures uploading.
    /// </summary>
    Uploading,
    /// <summary>
    /// Resident and drawable.
    /// </summary>
    Ready,
    /// <summary>
    /// No atlas: never one (<c>RetryAt</c> 0), or refused or unloaded.
    /// </summary>
    None
}

/// <summary>
/// A mesh's impostor (<see cref="FoliageMeshAsset.Impostor"/>): its atlas through loading, baking, uploading and refining, and the residency plan's
/// view of it.
/// </summary>
sealed class FoliageImpostorState
{
    /// <summary>
    /// Where the atlas is.
    /// </summary>
    public FoliageImpostorStage Stage;

    /// <summary>
    /// The cache read (or the source's making) in flight.
    /// </summary>
    public Task<(ImpostorSource? Source, ImpostorMeshes? Meshes, ImpostorClass? Class, ImpostorAtlas? Atlas)>? Load;

    /// <summary>
    /// What a bake renders from (null when the atlas came from the cache).
    /// </summary>
    public ImpostorSource? Source;

    /// <summary>
    /// The meshes the bake renders.
    /// </summary>
    public ImpostorMeshes? Meshes;

    /// <summary>
    /// The atlas's class.
    /// </summary>
    public ImpostorClass Class;

    /// <summary>
    /// The atlas's CPU copy (dropped once uploaded).
    /// </summary>
    public ImpostorAtlas? Atlas;

    /// <summary>
    /// The resident textures.
    /// </summary>
    public ImpostorTextures? Textures;

    /// <summary>
    /// When it was last drawn (tick).
    /// </summary>
    public long LastUsed;

    /// <summary>
    /// A refused atlas (<see cref="FoliageImpostorStage.None"/>): the tick from which it may ask again; 0 for one that never can (too small,
    /// ineligible).
    /// </summary>
    public long RetryAt;

    /// <summary>
    /// The work list that last numbered this mesh's impostor batch, and its number among the impostor batches.
    /// </summary>
    public int Stamp, Index;

    /// <summary>
    /// How soon the nearest zone that wants this atlas is close (units, smaller first) as of the scan at <see cref="PriorityScan"/>: the order
    /// bakes run in.
    /// </summary>
    public float Priority;

    /// <summary>
    /// The scan <see cref="Priority"/> is of.
    /// </summary>
    public long PriorityScan;

    /// <summary>
    /// The atlas's shape once its class is known (kept after the CPU copy is dropped): frame pixels, grid, levels, and the world radius of its
    /// largest instance.
    /// </summary>
    public int FramePixels, Grid, Levels;

    /// <summary>
    /// The world radius of the atlas's largest instance.
    /// </summary>
    public float WorldRadius;

    /// <summary>
    /// The nearest ground distance (units) at which an instance of the mesh is drawn as this impostor, as of the last scan, and the levels the
    /// plan takes off beyond what that distance needs.
    /// </summary>
    public float Need;

    /// <summary>
    /// The levels the plan takes off beyond what <see cref="Need"/> needs.
    /// </summary>
    public int Extra;

    /// <summary>
    /// Top albedo levels left out of the resident textures (<see cref="ImpostorTextures.SkipOf"/>), and what the plan wants.
    /// </summary>
    public int Skip, WantSkip;

    /// <summary>
    /// Since when the atlas has held a finer level than the plan wants (tick; 0 when it does not).
    /// </summary>
    public long FinerSince;

    /// <summary>
    /// Scans in a row the plan left this atlas out.
    /// </summary>
    public int DropScans;

    /// <summary>
    /// The atlas was baked here, not read from the cache (a loading screen does not wait for its upload:
    /// <see cref="FoliageRenderer.ImpostorUploadsAwaited"/>).
    /// </summary>
    public bool Baked;

    /// <summary>
    /// A refine in flight: the cache read, the atlas, the new textures and the albedo skip they are made with.
    /// </summary>
    public Task<ImpostorAtlas?>? RefineLoad;

    /// <summary>
    /// The refine's atlas.
    /// </summary>
    public ImpostorAtlas? RefineAtlas;

    /// <summary>
    /// The refine's new textures.
    /// </summary>
    public ImpostorTextures? Next;

    /// <summary>
    /// The albedo skip the refine's textures are made with.
    /// </summary>
    public int RefineSkip;

    /// <summary>
    /// The textures being made on a worker (<see cref="ImpostorTextures.MakeOffThread"/>) for the first upload or a refine, and the albedo skip.
    /// </summary>
    public Task<GpuTexture?[]>? Making;

    /// <summary>
    /// The albedo skip <see cref="Making"/> makes the textures with.
    /// </summary>
    public int MakingSkip;

    /// <summary>
    /// The tick from which a failed refine may be tried again.
    /// </summary>
    public long RefineRetryAt;
}
