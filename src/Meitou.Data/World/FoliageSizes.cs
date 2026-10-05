namespace Meitou.Data.World;

/// <summary>How big a foliage mesh is, for the Meitou <c>range</c> switch (docs/viewer.md, "Foliage"; not a game concept).</summary>
public enum FoliageSizeClass
{
    /// <summary>Litter and small plants: skeleton parts, small boulders, junk pieces, skulls, cacti, small leaves.</summary>
    Small,
    /// <summary>Junk, boulders, bushes, ruin blocks, palms and other plants up to a few metres.</summary>
    Medium,
    /// <summary>Trees, ruins, wrecks, rock stacks and hoodoos, resource rocks.</summary>
    Large,
}

/// <summary>
/// Size classes of foliage meshes from their bounds (docs/formats/foliage.md, "Mesh sizes"): a mesh's size is its bounding radius (half
/// the diagonal of the box around the mesh and its leaves mesh, at least 1) times the largest scale its record allows. The thresholds
/// follow from the default class ranges (800 / 2500 / 5000): the largest small mesh at the end of the small range looks as big as the
/// largest medium one at the end of the medium range (40 / 800 = 125 / 2500), and the smallest large mesh ends at twice that.
/// </summary>
public static class FoliageSizes
{
    /// <summary>Sizes from this up are medium, and from <see cref="LargeFrom"/> up large (units, radius × largest scale).</summary>
    public const float MediumFrom = 40, LargeFrom = 125;

    /// <summary>The Tab sliders' defaults: how far each class is drawn (units along the ground).</summary>
    public const float DefaultSmallRange = 800, DefaultMediumRange = 2500, DefaultLargeRange = 5000;

    /// <summary>A mesh's size: its bounding radius (unscaled) times the larger of its record's two scale limits (some records have them swapped).</summary>
    public static float Size(float boundsRadius, FoliageMesh mesh) => boundsRadius * Math.Max(mesh.MinScale, mesh.MaxScale);

    public static FoliageSizeClass Classify(float size) => size >= LargeFrom ? FoliageSizeClass.Large : size >= MediumFrom ? FoliageSizeClass.Medium : FoliageSizeClass.Small;
}
