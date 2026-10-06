namespace Meitou.Data.World;

/// <summary>
/// The viewer's terrain LOD rule (docs/formats/terrain.md, "Terrain LOD"): a screen-space error measured in pixels of the
/// picture actually rendered. A level's grid stands in for the next finer one with a height error of about
/// <see cref="Roughness"/> Ã— its vertex spacing; a quadtree node is split while that error, projected at the node's
/// distance, is more than <see cref="PixelError"/> pixels. Optionally the allowed error grows with distance like the game's
/// near/far thresholds (<see cref="FarPixelError"/> reached at twice <see cref="RampStart"/>). CDLOD needs one range per
/// level, not per node, so the error is one number per level instead of each node's own (the game's).
/// </summary>
public readonly record struct TerrainLod(float PixelScale, float PixelError, float FarPixelError, float RampStart)
{
    /// <summary>
    /// Height error of a level's grid in units of its vertex spacing: the 75th percentile, over the land, of the largest
    /// vertical distance any vertex of a 64-cell node moves when it slides onto the next coarser grid (the game's own node
    /// error). <b>Observed</b> on fullmap.tif: 1.5 to 2.1 at every level from 18 to 2304 units of spacing (the median 0.8 to
    /// 1.2, the 90th percentile 2.6 to 3.7), so one number serves all levels.
    /// </summary>
    public const float Roughness = 2f;

    /// <summary>Where in a level's distance band its vertices start sliding onto the coarser grid (the last 30% morph).</summary>
    public const float MorphFraction = 0.7f;

    /// <summary>
    /// Least distance between consecutive ranges, in node sizes of the finer level. A square of level l touching one of level
    /// l + 1 lies within (range of l + its node's diagonal) of the eye, and the coarser square's vertices on the shared edge
    /// must not have started morphing there. Morphing starts at range(l) + <see cref="MorphFraction"/> Ã— (range(l + 1) âˆ’
    /// range(l)), so range(l + 1) âˆ’ range(l) â‰¥ diagonal / 0.7 = âˆš(2 + hÂ²) / 0.7 node sizes, h the node's height range over
    /// its width. 2.5 covers h up to 1 (45Â° on average over a whole node); the old fixed rule's floor (2 node sizes) was
    /// the flat-ground bound.
    /// </summary>
    public const float MinimumGap = 2.5f;

    /// <summary>Pixels per world unit at distance 1 for a picture <paramref name="height"/> pixels high and a vertical field of view in radians.</summary>
    public static float ProjectionScale(float height, float fieldOfView) => height / (2 * MathF.Tan(fieldOfView / 2));

    /// <summary>The allowed error at a distance: <see cref="PixelError"/> up to <see cref="RampStart"/>, <see cref="FarPixelError"/> from twice it, the game's ramp in between.</summary>
    public float Threshold(float distance) => PixelError / Factor(distance);

    /// <summary>The game's factor on the metric, 1 âˆ’ t + t Ã— near / far with t = (d âˆ’ D) / D clamped to 0..1 (settings.md).</summary>
    float Factor(float distance)
    {
        if (!(FarPixelError > PixelError) || RampStart <= 0) return 1;
        float t = Math.Clamp((distance - RampStart) / RampStart, 0, 1);
        return 1 - t + t * (PixelError / FarPixelError);
    }

    /// <summary>The distance within which a node whose height error is <paramref name="error"/> world units is split.</summary>
    public float SplitDistance(double error)
    {
        double ke = PixelScale * error;
        double near = ke / PixelError;
        if (!(FarPixelError > PixelError) || RampStart <= 0 || near <= RampStart) return (float)near;
        // In the ramp: ke (1 âˆ’ t (1 âˆ’ r)) / d = T with t = (d âˆ’ D) / D, r = T / T_far.
        double r = PixelError / FarPixelError, d = ke * (2 - r) / (PixelError + ke * (1 - r) / RampStart);
        return (float)(d <= 2 * RampStart ? d : ke / FarPixelError);
    }

    /// <summary>
    /// The range of each level (within it the level is drawn rather than the next coarser one) for nodes of
    /// <paramref name="leafSize"/> Ã— 2^level drawn with <paramref name="gridCells"/> cells: the next coarser level's split
    /// distance, raised where needed so consecutive ranges stay <see cref="MinimumGap"/> node sizes apart. The last level (the
    /// root) has no range: it is drawn wherever nothing finer is.
    /// </summary>
    public float[] Ranges(int levels, double leafSize, int gridCells)
    {
        var ranges = new float[levels];
        float previous = 0;
        for (int l = 0; l + 1 < levels; l++)
        {
            double node = leafSize * (1 << l);
            double coarserSpacing = 2 * node / gridCells;
            // The gap is in node sizes of the finer level (l âˆ’ 1); the finest level reaches at least as far itself.
            double least = l == 0 ? MinimumGap * node : previous + MinimumGap * node / 2;
            float r = Math.Max(SplitDistance(Roughness * coarserSpacing), (float)least);
            ranges[l] = previous = r;
        }
        ranges[levels - 1] = float.MaxValue;
        return ranges;
    }
}
