using Meitou.Game.Rendering.Foliage;


namespace Meitou.Tests.Rendering;

/// <summary>
/// The reach of the grass's own motion vectors (<c>grass-velocity</c>, docs/render-foliage.md "Grass motion vectors").
/// </summary>
public class GrassMotionReachTests
{
    [Theory]
    [InlineData(0.05f, 6.2f, 0.1331853f)]   // across the wrap: 0.05 and 6.2 are 0.133 apart
    public void The_phase_step_is_the_short_way_round(float a, float b, float expected) =>

        Assert.InRange(GrassMotionReach.PhaseStep(a, b), expected - 1e-4f, expected + 1e-4f);
}
