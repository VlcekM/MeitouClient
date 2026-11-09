using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Locks;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Simulation;

/// <summary>
/// State 26 IS_CROUCHING_OR_LYING (<c>FUN_140599e90</c>): staying low or in a bed, not when standing or knocked out (docs/game/ai-tasks.md).
/// </summary>
public class CrouchingOrLyingTests
{
    static CharacterCold Cold() => new() { Race = Human, Medical = MedicalState.Create(Human, 50) };

    [Fact]
    public void Staying_low_counts()
    {
        var c = Cold();
        c.Medical!.StayingLow = true;

        Assert.True(CharacterFacts.CrouchingOrLying(c));
    }

    [Fact]
    public void A_knocked_out_character_does_not_count()
    {
        var c = Cold();
        c.Medical!.KnockOut(1000);

        Assert.False(CharacterFacts.CrouchingOrLying(c));
    }
}
