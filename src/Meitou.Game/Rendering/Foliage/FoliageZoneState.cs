using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// What a zone's layout holds (docs/render-foliage.md). <see cref="Far"/>: the FAR layers only (no grass coverage, 9/10 of the cost).
/// <see cref="Meshes"/>: every mesh layer, and, with the Meitou <c>range</c> switch, only the large meshes (the zone is farther than the medium
/// and small ranges and the grass, so nothing else can be drawn there). <see cref="Whole"/>: every layer and the grass.
/// </summary>
enum FoliageZoneTier
{
    /// <summary>
    /// The FAR layers only.
    /// </summary>
    Far,
    /// <summary>
    /// Every mesh layer (with the Meitou <c>range</c> switch: only the large meshes), no grass.
    /// </summary>
    Meshes,
    /// <summary>
    /// Every layer and the grass.
    /// </summary>
    Whole
}

/// <summary>
/// One zone of <see cref="FoliageRenderer"/>'s streaming: its layout (the job, the groups, the grass patches) and its grass pages.
/// </summary>
sealed class FoliageZoneState
{
    /// <summary>
    /// The zone.
    /// </summary>
    public required ZoneCoordinate Zone;

    /// <summary>
    /// The zone's corner (world x and z).
    /// </summary>
    public float X0, Z0;

    /// <summary>
    /// The layout in flight (null when none).
    /// </summary>
    public Task<GroupedFoliageZone>? Job;

    /// <summary>
    /// Laid out (at least the far layers); <see cref="Complete"/>: every layer, grass included, nothing left out. <see cref="MeshLayers"/>: every
    /// mesh layer was laid out (<see cref="Complete"/> implies it). <see cref="Filtered"/>: the groups that cannot be drawn at this zone's
    /// distance (a mesh that is not large) were left out. <see cref="JobTier"/>: what the job in flight lays out.
    /// </summary>
    public bool Ready, Complete, MeshLayers, Filtered, NeedsPrune;

    /// <summary>
    /// What the job in flight lays out.
    /// </summary>
    public FoliageZoneTier JobTier;

    /// <summary>
    /// The laid-out groups: the instances of one mesh each.
    /// </summary>
    public List<FoliageGroup> Groups = [];

    /// <summary>
    /// The zone's ground (heights for the grass pages).
    /// </summary>
    public FoliageGround? Ground;

    /// <summary>
    /// The grass patches of the layout.
    /// </summary>
    public List<FoliageGrassPatch> Patches = [];

    /// <summary>
    /// The sprite and colour map of each of <see cref="Patches"/> (looked up once when the patches are set), for the GPU grass's patch table.
    /// </summary>
    public (WorldTexture? Sprite, WorldTexture? ColourMap)[] PatchTextures = [];

    /// <summary>
    /// This zone's number in the GPU grass tables (<see cref="FoliageGrassGpu"/>'s slots name it), -1 before its first page is placed.
    /// </summary>
    public int GrassZone = -1;

    /// <summary>
    /// The grass pages by their place in the zone (row x pages per zone + column).
    /// </summary>
    public readonly Dictionary<int, FoliageGrassPage> Pages = [];

    /// <summary>
    /// Grass page jobs in flight.
    /// </summary>
    public int GrassRunning;

    /// <summary>
    /// Mesh instances laid out.
    /// </summary>
    public int Instances;

    /// <summary>
    /// The cull box's margin (<see cref="FoliageRenderer.ZoneMargin"/>) and what it was computed for.
    /// </summary>
    public float Margin;

    /// <summary>
    /// The residency stamp <see cref="Margin"/> was computed at (-1: compute again).
    /// </summary>
    public int MarginStamp = -1;

    /// <summary>
    /// The groups <see cref="Margin"/> was computed over.
    /// </summary>
    public List<FoliageGroup>? MarginGroups;

    /// <summary>
    /// Lowest and highest placed instance (for culling the zone as a box).
    /// </summary>
    public float MinY, MaxY;
}
