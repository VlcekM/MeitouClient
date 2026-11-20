using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Ai;

/// <summary>
/// What holds a player-faction character (<see cref="AiMind.PlayerSide"/>) for the goal its chooser picked, as an NPC is held by the goal it acts
/// out (<see cref="AiSystem.Holds"/>): the original's chooser installs a goal and its tasks run whoever the character belongs to, and an order, an
/// urgent job or a higher goal ends it. Its other rungs are the sources' (a job, the implicit jobs, the default task), whose tasks are not the AI's.
/// Pure reads of the character's own state, safe in any phase.
/// </summary>
internal static class PlayerSideHold
{
    /// <summary>
    /// Whether nothing outside the chooser holds the character: not imprisoned, not carried and not knocked out. The same exits as an NPC's
    /// (<see cref="AiSystem.Holds"/>); a flight is its goal's task, no exit.
    /// </summary>
    public static bool Free(CharacterCold cold) => !cold.IsImprisoned && cold.CarriedBy.IsNone && cold.Medical is not { Incapacitated: true };

    /// <summary>
    /// Whether the goal its chooser picked holds the character now: it has one (a source's pick leaves none), and the task is the AI's own: an engine
    /// goal's act-out while it runs, out of a fight (<see cref="AiSystem.OnDuty"/>); its own fight (<see cref="AiSystem.OwnsFight"/>); else a task
    /// the think started or none (<see cref="OwnsTask"/>).
    /// </summary>
    public static bool Holds(in CharacterHot n, CharacterCold cold, AiMind mind)
    {
        if (mind.Goal is null) return false;
        if (mind.Duty is not null) return !cold.InCombat && AiSystem.OnDuty(cold, mind);
        if (cold.InCombat) return n.Task == (byte)CharacterTask.Attack && AiSystem.OwnsFight(cold, mind);

        return OwnsTask(n, cold, mind);
    }

    /// <summary>
    /// Whether the character's task is the AI's own, so the think may start the goal's next task over it on its own slot: no provenance runs it, or
    /// it is a goal's the think started (<see cref="AiMind.Owns"/>), 14 Idleness's stand (the default task), the engine goal's act-out, its own
    /// fight, its plan's ditch walk (<see cref="AiSystem.RunsDitchStep"/>) or its plan's fetch (<see cref="AiSystem.RunsFetchStep"/>), or the run
    /// its goal asked of the flee step (<see cref="AiSystem.RunsOwnFlight"/>). An order's, a job's or the engine's task is not.
    /// </summary>
    public static bool OwnsTask(in CharacterHot n, CharacterCold cold, AiMind mind)
    {
        if (cold.Running is not { } run || !run.Runs((CharacterTask)n.Task)) return true;
        if (run.Source != TaskSource.Goal) return false;
        if (run.Row == TaskType.Idle || mind.Owns(run) || AiSystem.OwnsFight(cold, mind)) return true;
        if (AiSystem.RunsDitchStep(cold) || AiSystem.RunsFetchStep(cold) || AiSystem.RunsOwnFlight(cold, mind)) return true;

        return mind.Duty is not null && AiSystem.OnDuty(cold, mind);
    }
}
