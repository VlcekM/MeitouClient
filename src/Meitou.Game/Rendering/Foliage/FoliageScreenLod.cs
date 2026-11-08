namespace Meitou.Rendering;

/// <summary>
/// The pure rules of the foliage <c>screen-lod</c> switch (docs/render-foliage.md, "Screen-size LOD"): when the triangles of a foliage mesh are so small on the screen that
/// their detail cannot be seen, a coarser generated level is allowed sooner (<see cref="EffectiveErrors"/>) and the billboard takes over sooner (<see cref="Transition"/>).
/// A triangle's size is its mean world area: a mesh unit triangle of edge <c>e</c> (the square root of the mean area) on an instance of bounding radius <c>R</c> (the
/// mesh's radius <c>r</c> times the instance scale) is <c>e / r * R * pixelsPerRadian / distance</c> pixels across.
/// </summary>
public static class FoliageScreenLod
{
    /// <summary>The share of a triangle's area that shows on average: a randomly turned flat triangle projects to half its area.</summary>
    public const float Coverage = 0.5f;

    /// <summary>The mean edge of a mesh's triangles (the square root of their mean area, mesh units); 0 when there are none.</summary>
    public static float TriangleExtent(double surfaceArea, int triangles) => triangles > 0 && surfaceArea > 0 ? (float)Math.Sqrt(surfaceArea / triangles) : 0;

    /// <summary>The mean edge of a mesh's triangles from their areas (mesh units squared): the square root of the mean area; 0 when there are none. (The geometric mean, the size half the triangles are about, was tried first and moved the picture 7 times as much for the same saving: docs/render-foliage.md.)</summary>
    public static float TriangleExtent(IEnumerable<float> areas)
    {
        double sum = 0;
        int n = 0;
        foreach (float a in areas) { sum += a; n++; }
        return n == 0 ? 0 : TriangleExtent(sum, n);
    }

    /// <summary>
    /// The typical triangle edge of a level with <paramref name="triangles"/> triangles of a mesh with <paramref name="fullTriangles"/> and the edge <paramref name="extent"/>: a simplification keeps the
    /// surface, so the triangles grow as their number falls.
    /// </summary>
    public static float LevelExtent(float extent, int fullTriangles, int triangles) => triangles > 0 && fullTriangles > 0 ? extent * MathF.Sqrt((float)fullTriangles / triangles) : extent;

    /// <summary>
    /// <c>s</c> of a level: the distance, in units of <c>R * pixelsPerRadian</c>, beyond which its mean triangle projects to less than <paramref name="triPixels"/> square pixels
    /// (<paramref name="extentOverRadius"/>: <see cref="TriangleExtent"/> over the mesh's radius). Infinity (no push) for a mesh without a known size or with the rule off (<paramref name="triPixels"/> 0).
    /// </summary>
    public static float SubPixelDistance(float extentOverRadius, float triPixels) => triPixels > 0 && extentOverRadius > 0 ? extentOverRadius * MathF.Sqrt(Coverage / triPixels) : float.PositiveInfinity;

    /// <summary>
    /// The deviation (in radii of the mesh) that a level may count as having, given the one before it has triangles under <paramref name="triPixels"/> square pixels beyond
    /// <paramref name="subPixel"/>[level - 1] (<see cref="SubPixelDistance"/> per level, level 0 first): a level is allowed (the cull's rule <c>relative * radius * ppr / distance &lt;= tolerance</c>)
    /// where the old rule allows it, or where the level before is sub-pixel and the deviation stays within <paramref name="multiple"/> times the tolerance. As a
    /// threshold that is <c>min(relative, max(tolerance * subPixel[level - 1], relative / multiple))</c>; never decreasing with the level, so exactly one level takes an instance.
    /// </summary>
    public static float[] EffectiveErrors(IReadOnlyList<float> relative, IReadOnlyList<float> subPixel, float tolerance, float multiple)
    {
        var result = new float[relative.Count];
        for (int k = 1; k < result.Length; k++)
        {
            float pushed = Math.Max(tolerance * subPixel[k - 1], relative[k] / Math.Max(multiple, 1));
            result[k] = Math.Max(result[k - 1], Math.Min(relative[k], pushed));
        }
        return result;
    }

    /// <summary>The level the cull picks for <c>size = R * pixelsPerRadian / distance</c>: the coarsest whose deviation (<paramref name="errors"/>, radii) times the size is within the tolerance.</summary>
    public static int ChooseLevel(IReadOnlyList<float> errors, float size, float tolerance)
    {
        int level = 0;
        for (int k = 1; k < errors.Count; k++) if (errors[k] * size <= tolerance) level = k;
        return level;
    }

    /// <summary>
    /// The ground distance per unit of an instance's bounding radius beyond which a billboard may replace the mesh because the mesh's triangles are under
    /// <paramref name="triPixels"/> square pixels (<paramref name="extentOverRadius"/> as in <see cref="SubPixelDistance"/>, <paramref name="pixelsPerRadian"/> of the render).
    /// </summary>
    public static float TransitionPerRadius(float extentOverRadius, float pixelsPerRadian, float triPixels) => pixelsPerRadian * SubPixelDistance(extentOverRadius, triPixels);

    /// <summary>
    /// The ground distance per unit of radius at which an atlas frame of <paramref name="framePixels"/> is magnified by <paramref name="magnification"/> (a sphere of that
    /// radius is <c>framePixels * magnification</c> pixels across): a billboard is never used nearer, so it never looks coarser than the atlas was made for.
    /// </summary>
    public static float FloorPerRadius(int framePixels, float pixelsPerRadian, float magnification) => 2 * pixelsPerRadian / (framePixels * magnification);

    /// <summary>
    /// The transition of a group of instances with the largest bounding radius <paramref name="radius"/>: the distance the billboard rule gives (<paramref name="baseTransition"/>)
    /// or, where the triangles are sub-pixel sooner, the nearer one: <c>radius * max(perRadius, floorPerRadius)</c>, never beyond the base one.
    /// </summary>
    public static float Transition(float baseTransition, float radius, float perRadius, float floorPerRadius) =>
        perRadius > 0 && radius > 0 ? Math.Min(baseTransition, radius * Math.Max(perRadius, floorPerRadius)) : baseTransition;
}
