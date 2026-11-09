using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Rules settled by reverse engineering that a synthetic character can pin (docs/game/perception.md, docs/game/ai-tasks.md).
/// </summary>
public class ReLaneTests
{
    static CharacterCold Cold() => new() { Race = Human, Medical = MedicalState.Create(Human, 50) };

    /// <summary>
    /// The squad-intact able test (<c>FUN_14079acf0</c>): a down member counts only while its first-aid need (Character <c>+0x504</c>) is under 0.2,
    /// or 0.8 for a persistent platoon with a home.
    /// </summary>
    [Theory]
    [InlineData(0.1f, false, true)]
    [InlineData(0.2f, false, false)]
    [InlineData(0.5f, true, true)]
    public void A_down_member_counts_as_able_only_below_the_need_threshold(float need, bool persistentHome, bool able) =>

        Assert.Equal(able, SquadIntact.DownIsAble(need, persistentHome));

    /// <summary>
    /// Row 98's desire by the minutes since the last bed use (<c>FUN_1405a0880</c>): 0.3 within 20, else 1 for a human.
    /// </summary>
    [Theory]
    [InlineData(19.9, 0.3f)]
    [InlineData(20, 1f)]
    public void The_bed_clock_lowers_the_sleeping_desire_just_after_a_bed(double minutes, float desire) =>

        Assert.Equal(desire, AiSystem.BedClockDesire(Cold(), minutes));
}
