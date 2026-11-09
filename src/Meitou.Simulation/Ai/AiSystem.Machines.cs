using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;

namespace Meitou.Simulation.Ai;

// The NPC workers of the town machines (docs/game/buildings-production.md "NPC town machines"): goal 189 is real work, its plan's step
// Task_OperateMachine (87), Task_EmptyMachine (92) or Task_FillMachine (93) at a town building; goal 190 pretends (221). The production system
// applies what the think leaves in AiMind.WorkOperate / CollectFrom / FillTo.
public sealed partial class AiSystem
{
    /// <summary>The faction owning a town's buildings (its machine's owner), -1 for none.</summary>
    int WorkOwnerOf(int town) => town >= 0 && town < data.SiteFactions.Count ? data.SiteFactions[town] : -1;

    /// <summary>
    /// The building the plan's current step works at: a machine step (87, 92, 93) or the walk to it names its own building (the fetch from another
    /// machine's output, rows 94 and 92, is at that machine), else the building the goal picked.
    /// </summary>
    TownBuilding? StepBuilding(AiMind mind, int town)
    {
        bool machineStep = mind.Step is TaskType.OperateMachinery or TaskType.CollectOutputResource or TaskType.FillMachine
            or TaskType.MoveOnFreeWill or TaskType.MoveOnFreeWillFast;
        if (!machineStep || mind.Plan[0].Target is not { Kind: Targets.Work } at) return mind.WorkBuilding;

        foreach (var b in Places(town).Workplaces)
            if (b.Position.X == at.Position.X && b.Position.Z == at.Position.Z) return b;

        return mind.WorkBuilding;
    }

    /// <summary>
    /// A worker of goal 189 at its building: with row 87 first in its plan it operates the machine, with row 92 first it empties the output, with row 93
    /// it fills the inputs from what it carries. Goal 190 (221) only pretends: nothing for the production system.
    /// </summary>
    static void WorkAtMachine(AiMind mind, TaskType goal, TownBuilding? building)
    {
        if (goal != TaskType.AutoLabouringMines || building is null) return;
        if (mind.Step == TaskType.OperateMachinery) mind.WorkOperate = building;
        else if (mind.Step == TaskType.CollectOutputResource) mind.CollectFrom = building;
        else if (mind.Step == TaskType.FillMachine) mind.FillTo = building;
    }
}