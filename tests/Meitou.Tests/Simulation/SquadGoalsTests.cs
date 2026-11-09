using Meitou.Simulation;
using Meitou.Simulation.Ai;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The squad goal list's counts (Blackboard::howManyGuysWorkingOnThisSubTarget, isMySubTarget; docs/game/ai-tasks.md).
/// </summary>
public class SquadGoalsTests
{
    [Fact]
    public void Working_counts_the_squads_other_workers_on_a_target_but_not_the_asker_nor_the_target()
    {
        var a = new CharacterId(1, 0);
        var b = new CharacterId(2, 0);
        var patient = new CharacterId(3, 0);
        var stranger = new CharacterId(4, 0);
        var goals = new SquadGoals();
        goals.Add(5, a, GoalTarget.Of(patient));
        goals.Add(5, patient, GoalTarget.Of(patient));
        goals.Add(6, stranger, GoalTarget.Of(patient));
        goals.Add(5, b, GoalTarget.At("cage"));

        Assert.Equal(0, goals.Working(5, a, GoalTarget.Of(patient)));   // the asker's own entry, the target's own and another squad's do not count
        Assert.Equal(1, goals.Working(5, b, GoalTarget.Of(patient)));
        Assert.Equal(1, goals.Working(5, a, GoalTarget.At("cage")));
        Assert.True(goals.IsMine(a, GoalTarget.Of(patient)));
        Assert.False(goals.IsMine(b, GoalTarget.Of(patient)));
    }
}
