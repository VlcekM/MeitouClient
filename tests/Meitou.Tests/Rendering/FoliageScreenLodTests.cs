using Meitou.Game.Rendering.Foliage;


namespace Meitou.Tests.Rendering;

/// <summary>
/// The rules of the foliage <c>screen-lod</c> switch (docs/render-foliage.md, "Screen-size LOD"): which generated level an instance takes and where
/// the billboard replaces the mesh.
/// </summary>
public class FoliageScreenLodTests
{
    // A mesh of radius 1 with levels of 100 %, 50 %, 25 % and 10 % of 4000 triangles; their deviations (radii) and mean triangle edges (radii).
    static readonly float[] Relative = [0, 0.005f, 0.02f, 0.075f];
    static readonly float[] Extent = [0.01f, 0.014f, 0.02f, 0.032f];

    static float[] SubPixel(float triPixels) => [.. Extent.Select(e => FoliageScreenLod.SubPixelDistance(e, triPixels))];

    [Fact]
    public void A_sub_pixel_rule_only_ever_picks_a_coarser_or_equal_level_and_exactly_one_level_takes_each_size()
    {
        var plain = Relative;
        var effective = FoliageScreenLod.EffectiveErrors(plain, SubPixel(1.5f), 4, 2);
        int pushed = 0;
        for (float size = 0.1f; size < 1e5f; size *= 1.01f)   // size = R * pixelsPerRadian / distance
        {
            int before = FoliageScreenLod.ChooseLevel(plain, size, 4), after = FoliageScreenLod.ChooseLevel(effective, size, 4);

            Assert.True(after >= before, $"size {size}");

            // Exactly one level: the chosen one is allowed and the next is not (the cull's two-sided test).
            int count = 0;
            for (int k = 0; k < effective.Length; k++)
            {
                bool takes = effective[k] * size <= 4 && (k + 1 == effective.Length || !(effective[k + 1] * size <= 4));
                if (takes)
                {
                    count++;

                    Assert.Equal(after, k);
                }
            }

            Assert.Equal(1, count);

            if (after > before) pushed++;
        }

        Assert.True(pushed > 0);
    }

    [Fact]
    public void The_billboard_comes_sooner_for_small_triangles_but_never_nearer_than_the_atlas_frame_allows_nor_later_than_before()
    {
        float ppr = 1158;

        // Tiny triangles (edge 0.005 radii): sub-pixel at 1.5 px2 from 1158 * 0.005 * 0.577 = 3.3 radii.
        float perRadius = FoliageScreenLod.TransitionPerRadius(0.005f, ppr, 1.5f);

        Assert.Equal(ppr * 0.005f * MathF.Sqrt(0.5f / 1.5f), perRadius, 1e-3f);

        float floor = FoliageScreenLod.FloorPerRadius(256, ppr, 1.4f);   // 2 * 1158 / (256 * 1.4) = 6.46 radii

        Assert.Equal(6.46f, floor, 0.01f);

        // The floor wins over a rule that would switch nearer: a radius-100 group switches at 646, not at 330.
        Assert.Equal(646f, FoliageScreenLod.Transition(4000, 100, perRadius, floor), 1f);

        // Not later than the base transition, however large the triangles.
        Assert.Equal(4000f, FoliageScreenLod.Transition(4000, 1000, 50, floor));

        // Sooner for a smaller instance group, in proportion to the radius.
        float big = FoliageScreenLod.Transition(4000, 400, 8, floor), small = FoliageScreenLod.Transition(4000, 200, 8, floor);

        Assert.Equal(big / 2, small, 1f);
        Assert.True(small < 4000);

        // Never nearer than the floor for the group's radius, at any triangle size.
        for (float e = 0.0001f; e < 1; e *= 1.5f)

            Assert.True(FoliageScreenLod.Transition(4000, 150, FoliageScreenLod.TransitionPerRadius(e, ppr, 1.5f), floor) >= Math.Min(4000,
                    150 * floor) - 1e-3f);
    }
}
