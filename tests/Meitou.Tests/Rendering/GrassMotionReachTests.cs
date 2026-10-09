using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The reach of the grass's own motion vectors (<c>grass-velocity</c>, docs/render-foliage.md "Grass motion vectors").</summary>
public class GrassMotionReachTests
{
    [Theory]
    [InlineData(0.1f, 0.2f, 0.1f)]
    [InlineData(0.2f, 0.1f, 0.1f)]
    [InlineData(0.05f, 6.2f, 0.1331853f)]   // across the wrap: 0.05 and 6.2 are 0.133 apart
    public void The_phase_step_is_the_short_way_round(float a, float b, float expected) =>
        Assert.InRange(GrassMotionReach.PhaseStep(a, b), expected - 1e-4f, expected + 1e-4f);

    [Fact]
    public void No_step_or_unknown_focal_length_gives_no_scale()
    {
        Assert.Equal(0, GrassMotionReach.Scale(0, 1000, 0.5f));
        Assert.Equal(0, GrassMotionReach.Scale(0.01f, 0, 0.5f));
        Assert.Equal(0, GrassMotionReach.Scale(0.01f, 1000, 0));
    }

    [Fact]
    public void A_blade_at_the_reach_moves_the_threshold_in_pixels()
    {
        // sway 3 units, phase step 0.01 rad, focal 1000 px: the top edge moves at most 0.03 units, which at distance d is 0.03 * 1000 / d pixels.
        float scale = GrassMotionReach.Scale(0.01f, 1000, 0.5f);
        float reach = GrassMotionReach.Reach(1e9f, 3, scale);
        Assert.Equal(60f, reach, 0.01f);
        Assert.Equal(0.5f, 3 * 0.01f * 1000 / reach, 1e-4f);
    }

    [Fact]
    public void The_reach_never_exceeds_the_patch_range_and_without_a_scale_is_the_range()
    {
        Assert.Equal(40f, GrassMotionReach.Reach(40, 3, GrassMotionReach.Scale(0.01f, 1000, 0.5f)));
        Assert.Equal(40f, GrassMotionReach.Reach(40, 3, 0));
    }
}
