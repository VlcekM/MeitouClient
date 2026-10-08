namespace Meitou.Data.World;

/// <summary>How big a foliage mesh is, for the Meitou <c>range</c> switch (docs/viewer.md, "Foliage"; not a game concept).</summary>
public enum FoliageSizeClass
{
    /// <summary>Litter and small plants: skeleton parts, small boulders, junk pieces, skulls, cacti, small leaves.</summary>
    Small,
    /// <summary>Junk, boulders, bushes, ruin blocks, palms and other plants up to a few metres.</summary>
    Medium,
    /// <summary>Trees, rocks, cliffs, pillars, rock stacks, hoodoos, resource rocks, ruins, wrecks, houses and walls (trees take the normal impostor distance: <see cref="FoliageSizes.LargeBillboard"/>).</summary>
    Large,
}

/// <summary>
/// Size classes of foliage meshes from their bounds (docs/formats/foliage.md, "Mesh sizes"): a mesh's size is its bounding radius (half
/// the diagonal of the box around the mesh and its leaves mesh, at least 1) times the largest scale its record allows. The thresholds
/// follow from the first class ranges (<see cref="ThresholdSmallRange"/> and the others, 800 / 2500 / 5000): the largest small mesh at the end of the small range looks as big as the
/// largest medium one at the end of the medium range (40 / 800 = 125 / 2500), and the smallest large mesh ends at twice that.
/// </summary>
public static class FoliageSizes
{
    /// <summary>Sizes from this up are medium, and from <see cref="LargeFrom"/> up large (units, radius × largest scale).</summary>
    public const float MediumFrom = 40, LargeFrom = 125;

    /// <summary>The ranges the thresholds were chosen from (the first Meitou defaults), 800 / 2500 / 5000.</summary>
    public const float ThresholdSmallRange = 800, ThresholdMediumRange = 2500, ThresholdLargeRange = 5000;

    /// <summary>
    /// The Tab sliders' defaults: how far each class is drawn (units along the ground). Large and medium meshes reach further than the
    /// thresholds' ranges because beyond the impostor distance (4000) they are billboards, which cost a quad per tree instead of its
    /// triangles, in the colour pass and in the shadow cascades (docs/impostors.md, section 8).
    /// </summary>
    public const float DefaultSmallRange = 3500, DefaultMediumRange = 12000, DefaultLargeRange = 50000;

    /// <summary>A mesh's size: its bounding radius (unscaled) times the larger of its record's two scale limits (some records have them swapped).</summary>
    public static float Size(float boundsRadius, FoliageMesh mesh) => boundsRadius * Math.Max(mesh.MinScale, mesh.MaxScale);

    public static FoliageSizeClass Classify(float size) => size >= LargeFrom ? FoliageSizeClass.Large : size >= MediumFrom ? FoliageSizeClass.Medium : FoliageSizeClass.Small;

    /// <summary>Vegetation at least this big (sizes of the giant trees and swamp canopies, 1339 to 3169) keeps the large impostor distance: it is a landmark.</summary>
    public const float GiantFrom = 1000;

    /// <summary>
    /// Whether a mesh file is in the game's own vegetation folders, <c>Assets/Plants</c> (trees, ferns, palms, grass clumps) or <c>foliage/Trees</c>; rocks are in
    /// <c>Assets/Rocks</c>, ruins and wrecks in <c>Assets/Things</c>, houses and walls in <c>Assets/Buildings</c> (docs/formats/foliage.md, "Mesh sizes").
    /// </summary>
    public static bool IsVegetation(string meshPath)
    {
        var p = meshPath.Replace('\\', '/');
        return p.Contains("/Assets/Plants/", StringComparison.OrdinalIgnoreCase) || p.Contains("/foliage/Trees/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a mesh becomes a billboard at the large impostor distance (FoliageRenderer.LargeImpostorDistance, 12000) rather than the
    /// normal one (4000): large meshes, except vegetation (<see cref="IsVegetation"/>) under <see cref="GiantFrom"/>. Rocks, cliffs, ruins and
    /// pillars keep their mesh longer because their flat billboards showed up close (docs/impostors.md, section 15); a tree's sparse crown
    /// reads as a billboard at 4000. The trees stay in the large size class, so they are still drawn to the large range (50000).
    /// </summary>
    public static bool LargeBillboard(float boundsRadius, FoliageMesh mesh)
    {
        float size = Size(boundsRadius, mesh);
        return Classify(size) == FoliageSizeClass.Large && !(size < GiantFrom && IsVegetation(mesh.MeshPath));
    }
}
