using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Bodies;

/// <summary>
/// The torture rack's per-frame stretch (docs/game/locks.md "Torture racks", <c>FUN_14029af90</c>).
/// </summary>
public class RackTests
{
    [Fact]
    public void Limbs_lose_the_frame_first_and_the_trunk_waits_until_they_have_no_flesh_left()
    {
        var m = Healthy();
        var ctx = Context(options: new BodyOptions { Dismemberment = Dismemberment.Never });

        float head = m.Parts[HeadIx].Flesh;
        float arm = m.Parts[RArm].Flesh;

        var first = m.StretchLimbs(0.5f, ctx, limbLoss: false);
        Assert.True(first.Busy);
        Assert.Equal(arm - 0.5f, m.Parts[RArm].Flesh, 4);
        Assert.Equal(head, m.Parts[HeadIx].Flesh);

        // A body this whole is at ratio about 1 on both sides: progress 0.5 - 0.25 x 2 = 0.
        Assert.InRange(first.Progress, 0f, 0.01f);
    }
}
