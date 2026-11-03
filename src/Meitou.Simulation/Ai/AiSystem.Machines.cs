using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

// The NPC workers of the town machines (docs/game/buildings-production.md "NPC town machines"): goal 189 is real work, its plan's step Task_OperateMachine (87) or Task_EmptyMachine (92) at the
// town building; goal 190 pretends (221). The production system applies what the think leaves in AiMind.WorkOperate / CollectFrom.
public sealed partial class AiSystem
{
    /// <summary>The faction owning a town's buildings (its machine's owner), -1 for none.</summary>
    int WorkOwnerOf(int town) => town >= 0 && town < data.SiteFactions.Count ? data.SiteFactions[town] : -1;

    /// <summary>
    /// A worker of goal 189 at its building: with row 87 first in its plan it operates the machine, with row 92 first it empties the output. Goal 190 (221) only pretends: nothing for the
    /// production system.
    /// </summary>
    static void WorkAtMachine(AiMind mind, TaskType goal)
    {
        if (goal != TaskType.AUTO_LABOURING_MINES || mind.WorkBuilding is not { } building) return;
        if (mind.Step == TaskType.OPERATE_MACHINERY) mind.WorkOperate = building;
        else if (mind.Step == TaskType.COLLECT_OUTPUT_RESOURCE) mind.CollectFrom = building;
    }
}
