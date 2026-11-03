using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Bodies;

/// <summary>The torture rack's per-frame stretch (docs/game/locks.md "Torture racks", <c>FUN_14029af90</c>).</summary>
public class RackTests
{
    [Fact]
    public void Limbs_lose_the_frame_first_and_the_trunk_waits_until_they_have_no_flesh_left()
    {
        var m = Healthy();
        var ctx = Context(options: new BodyOptions { Dismemberment = Dismemberment.Never });
        float head = m.Parts[HeadIx].Flesh, arm = m.Parts[RArm].Flesh;
        var first = m.StretchLimbs(0.5f, ctx, limbLoss: false);
        Assert.True(first.Busy);
        Assert.Equal(arm - 0.5f, m.Parts[RArm].Flesh, 4);
        Assert.Equal(head, m.Parts[HeadIx].Flesh);
        // A body this whole is at ratio about 1 on both sides: progress 0.5 - 0.25 x 2 = 0.
        Assert.InRange(first.Progress, 0f, 0.01f);
    }

    [Fact]
    public void A_body_stretched_past_every_limit_is_done_with_the_full_progress()
    {
        var m = Healthy();
        var ctx = Context(options: new BodyOptions { Dismemberment = Dismemberment.Never });
        var step = m.StretchLimbs(50, ctx, limbLoss: false);
        for (int i = 0; i < 40 && step.Busy; i++) step = m.StretchLimbs(50, ctx, limbLoss: false);
        Assert.False(step.Busy);
        Assert.All(m.Parts, p => Assert.True(p.Flesh - 1.1f * p.Stun <= -p.MaxHp));
        Assert.True(step.Progress >= 0.999f);
    }

    [Fact]
    public void A_limb_past_its_limit_is_cut_off_only_with_the_setting()
    {
        var cut = Healthy();
        cut.Parts[RArm].Flesh = -cut.Parts[RArm].MaxHp;
        cut.StretchLimbs(0.1f, Context(), limbLoss: true);
        Assert.Equal(LimbState.Stump, cut.Parts[RArm].Limb);

        var kept = Healthy();
        kept.Parts[RArm].Flesh = -kept.Parts[RArm].MaxHp;
        kept.StretchLimbs(0.1f, Context(), limbLoss: false);
        Assert.Equal(LimbState.Original, kept.Parts[RArm].Limb);
    }
}
