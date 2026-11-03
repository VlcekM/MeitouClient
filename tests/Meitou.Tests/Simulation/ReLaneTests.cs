using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Simulation;

/// <summary>Rules settled by reverse engineering that a synthetic character can pin (docs/game/perception.md, docs/game/ai-tasks.md).</summary>
public class ReLaneTests
{
    static CharacterCold Cold() => new() { Race = Human, Medical = MedicalState.Create(Human, 50) };

    /// <summary>The squad-intact able test (<c>FUN_14079acf0</c>): a down member counts only while its first-aid need (Character <c>+0x504</c>) is under 0.2, or 0.8 for a persistent platoon with a home.</summary>
    [Theory]
    [InlineData(0.1f, false, true)]
    [InlineData(0.2f, false, false)]
    [InlineData(0.5f, false, false)]
    [InlineData(0.5f, true, true)]
    [InlineData(0.8f, true, false)]
    public void A_down_member_counts_as_able_only_below_the_need_threshold(float need, bool persistentHome, bool able) =>
        Assert.Equal(able, SquadIntact.DownIsAble(need, persistentHome));

    [Fact]
    public void A_standing_member_is_able_and_a_dead_one_is_not()
    {
        Assert.True(SquadIntact.Able(null!, Cold(), persistentHome: false));
        var dead = Cold();
        dead.Medical!.Butcher();
        Assert.False(SquadIntact.Able(null!, dead, persistentHome: false));
    }
}
