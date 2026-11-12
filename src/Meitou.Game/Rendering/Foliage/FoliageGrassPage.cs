using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// One 576-unit grass page of a zone (<see cref="FoliageZoneState.Pages"/>): the job generating its blades, then its patches' buffers in the store.
/// </summary>
sealed class FoliageGrassPage
{
    /// <summary>
    /// The blade generation in flight: per patch the blades, the floats in use and the density prefixes.
    /// </summary>
    public Task<(float[][] Blades, int[] Floats, int[][] Prefixes)>? Job;

    /// <summary>
    /// Per patch its blades in the store, once the page's last upload step is done (null before).
    /// </summary>
    public FoliageGrassBuffer[]? Buffers;

    /// <summary>
    /// The page went out of range (or the store was replaced): its pending upload steps do nothing and give its ranges back.
    /// </summary>
    public bool Dropped;

    /// <summary>
    /// The heights the page's blades span, the lowest root to the highest tip (<see cref="FoliageGrassStore.QueuePage"/>): the box the views test.
    /// The page's ground height at its centre with 2000 units either way let every page under the view's line of sight draw, far past where it
    /// meets the ground.
    /// </summary>
    public float MinY, MaxY;

    /// <summary>
    /// The generated blades while the arena or the slot table had no room (<see cref="FoliageGrassGpu.Misses"/>): placed again by
    /// <see cref="FoliageRenderer.UpdateGrass"/>. Rented (<see cref="FoliageGrassField.RentBladesWithPrefixes"/>), the first
    /// <see cref="PendingFloats"/> of each in use; the page's last upload step returns them.
    /// </summary>
    public float[][]? PendingBlades;

    /// <summary>
    /// The floats in use of each of <see cref="PendingBlades"/>.
    /// </summary>
    public int[]? PendingFloats;

    /// <summary>
    /// The density prefixes of each of <see cref="PendingBlades"/>.
    /// </summary>
    public int[][]? PendingPrefixes;
}

/// <summary>
/// One patch's blades of a grass page in the store (<see cref="FoliageGrassGpu"/>), and what its draws cache.
/// </summary>
sealed class FoliageGrassBuffer
{
    /// <summary>
    /// Where the blades are in the grass arena (<see cref="FoliageGrassGpu"/>): the draw's first instance is <see cref="FirstBlade"/>;
    /// <see cref="Slot"/> is its place in the GPU cull's slot table (-1 for a patch without blades).
    /// </summary>
    public ArenaRange Range;

    /// <summary>
    /// The <see cref="FoliageRenderer.GrassFarCover"/> version (FoliageRenderer's <c>_grassSpriteVersion</c>) its sprite was looked up at.
    /// </summary>
    public int SpriteVersion = -1;

    /// <summary>
    /// The draw's first instance (<see cref="FoliageGrassGpu.FirstBlade"/> of <see cref="Range"/>).
    /// </summary>
    public uint FirstBlade;

    /// <summary>
    /// The place in the GPU cull's slot table (-1 for a patch without blades).
    /// </summary>
    public int Slot = -1;

    /// <summary>
    /// The store <see cref="Range"/> and <see cref="Slot"/> are in: a store replaced by a larger one (<see cref="FoliageGrassStore.Grow"/>)
    /// is not given them back.
    /// </summary>
    public FoliageGrassGpu? Store;

    /// <summary>
    /// The blades.
    /// </summary>
    public int Count;

    /// <summary>
    /// The patch the cached textures and count belong to (<see cref="Patch"/>): its sprite and colour map looked up once, and the blades shown at
    /// <see cref="ShownDensity"/>.
    /// </summary>
    public FoliageGrassPatch? Patch;

    /// <summary>
    /// <see cref="Patch"/>'s sprite and colour map.
    /// </summary>
    public WorldTexture? Sprite, ColourMap;

    /// <summary>
    /// The density <see cref="Shown"/> was counted at (NaN: not yet).
    /// </summary>
    public float ShownDensity = float.NaN;

    /// <summary>
    /// The blades shown at <see cref="ShownDensity"/>.
    /// </summary>
    public int Shown;

    /// <summary>
    /// Blades after each 1/64 of the candidates (<see cref="FoliageGrassField.BladesWithPrefixes"/>): the density setting draws a prefix.
    /// </summary>
    public int[] Prefixes = [];
}
