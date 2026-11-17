using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The life of a kept plan's running step (docs/game/ai-tasks.md "Precedence", <b>Verified</b>): the original plans a goal once, runs its plan's tasks
/// in turn and ends the running one by its own tests, the plan step <c>FUN_14050d970</c> of each AI update (with the per-frame test of
/// <c>FUN_14050f0b0</c> folded in); the next queued task then starts (<c>FUN_14050d540</c>), and with none left the goal goes (<c>FUN_14050d7d0</c>) and
/// the chooser plans afresh. The step is <see cref="AiMind.Plan"/>'s first; its states are the planner's (<see cref="IPlanWorld"/>). Writes only the
/// thinking character's mind (the parallel think).
/// </summary>
static class PlanSteps
{
    /// <summary>
    /// Runs the plan step for the running step of <paramref name="goal"/>'s plan: true while the plan still has a step running, false when the plan is
    /// over (cleared) and the goal is to be chosen afresh.
    /// </summary>
    public static bool Run(World world, int slot, AiMind mind, IPlanWorld states, AiGoal goal, int hour)
    {
        if (!StartNext(mind, states)) return false;

        var step = mind.Plan[0];
        if (TaskDataTable.Of(step.Type) is not { } row || !Ends(world, slot, mind, states, step, row, goal, hour)) return true;

        mind.Plan.RemoveAt(0);
        return StartNext(mind, states);
    }

    /// <summary>
    /// <c>FUN_14050d970</c>'s tests of the running step, in its order: done (its class's slot 4 and its list A, <c>FUN_140513f50</c>); a walk whose
    /// next queued step can already run; then, unless the row keeps its hours (<see cref="KeepsHours"/>), the goal's hours over (the plan's tasks carry
    /// the goal's, <c>FUN_14050fe10</c>) or its own list B no longer met (<c>FUN_14060f940</c>). The walk stopped short of its place (the mover idle,
    /// <c>isAtLocation</c> false: <c>FUN_140796ed0</c>) is the act-out's give-up rule (<see cref="AiSystem.MaxFailedWalks"/>).
    /// </summary>
    static bool Ends(World world, int slot, AiMind mind, IPlanWorld states, PlanStep step, TaskDataRow row, AiGoal goal, int hour)
    {
        if (!TaskFinished.Unfinished(world, slot, step.Type) && Done(states, row, step.Target)) return true;
        if (IsWalk(row.Type) && mind.Plan.Count > 1 && TaskDataTable.Of(mind.Plan[1].Type) is { } next
            && CanRun(states, next, mind.Plan[1].Target, step.Target))
        {
            return true;
        }

        if (KeepsHours(row.Type)) return false;

        return !goal.InWindow(hour) || !CanRun(states, row, step.Target, default);
    }

    /// <summary>
    /// <c>FUN_14050d540</c>: the front step starts when its row acts (or has no TaskData row); one that does not act (a goal-like row with an effect)
    /// is left out when its list A holds (<c>FUN_14060f0c0</c>), else the plan is over. False when no step is left to start (the plan cleared).
    /// </summary>
    static bool StartNext(AiMind mind, IPlanWorld states)
    {
        while (mind.Plan.Count > 0)
        {
            var step = mind.Plan[0];
            if (TaskDataTable.Of(step.Type) is not { } row || row.Is(TaskDataFlags.Acts)) return true;
            if (!Skipped(states, row, step.Target)) break;

            mind.Plan.RemoveAt(0);
        }

        mind.Plan.Clear();
        return false;
    }

    /// <summary>
    /// A running row's list A as <c>FUN_14060f570</c> reads it: every entry holds, and an empty list never does (a row that acts has no effect).
    /// </summary>
    static bool Done(IPlanWorld states, TaskDataRow row, in PlanTarget target)
    {
        if (row.Done.Length == 0) return false;

        foreach (var c in row.Done)
            if (!states.Holds(c, target)) return false;
        return true;
    }

    /// <summary>
    /// A queued row that does not act is left out (<c>FUN_14060f0c0</c>, <b>Verified</b>): every list A entry holds, and the list is not empty or the
    /// row has an effect.
    /// </summary>
    static bool Skipped(IPlanWorld states, TaskDataRow row, in PlanTarget target)
    {
        foreach (var c in row.Done)
            if (!states.Holds(c, target)) return false;
        return row.Done.Length > 0 || row.Effect is not null;
    }

    /// <summary>
    /// "Can run" (<c>FUN_14060f940</c>, asked without the finder): every list B entry holds; a row with an effect also needs it to hold for the target
    /// its provider was planned to (<paramref name="effectTarget"/>, none: false).
    /// </summary>
    static bool CanRun(IPlanWorld states, TaskDataRow row, in PlanTarget target, in PlanTarget effectTarget)
    {
        foreach (var c in row.Needs)
            if (!states.Holds(c, target)) return false;
        if (row.Effect is not { } effect) return true;

        return !effectTarget.IsNone && states.Holds(effect, effectTarget);
    }

    /// <summary>
    /// The walk rows <c>FUN_14050d970</c> ends early once the next queued step can run: 1, 67, 82, 83, 120, 121, 163, 199 and 267 (<b>Verified</b>).
    /// </summary>
    static bool IsWalk(TaskType type) => type is TaskType.MoveOnFreeWill or TaskType.MoveOnFreeWillFast or TaskType.MoveToBuildingDoor
        or TaskType.MoveToCurrentLocationBuildingDoor or TaskType.MoveInsideBuilding or TaskType.MoveToFortificationGate
        or TaskType.MoveToBuildingDoorInsidepos or TaskType.MoveToBuildingDoorOutsidepos or TaskType.MoveInBowRange;

    /// <summary>
    /// The rows whose TaskData <c>+0x16</c> is set (12, 102, 159, 161, <c>FUN_140319a60</c>): the plan step tests neither their hours nor their list B.
    /// </summary>
    internal static bool KeepsHours(TaskType type) => type is TaskType.PlayerTalkTo or TaskType.SeekAndTalkAndSendSignal or TaskType.SendDialogueSignal
        or TaskType.SendDialogueSignalWithoutMoving;
}
