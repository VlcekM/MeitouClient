using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Ai;

/// <summary>
/// What may still take over a character a hold only Meitou has keeps (<see cref="MeitouHolds"/>): the chooser then weighs only the urgent tier's
/// goals it lets through, with no sources and no committed stop (<see cref="TierRungs.HeldChoice"/>).
/// </summary>
internal enum MeitouHold : byte
{
    /// <summary>
    /// No such hold: the full ladder.
    /// </summary>
    None,
    /// <summary>
    /// Self preservation alone (its run, or its fight through row 13 against its own attacker).
    /// </summary>
    Self,
    /// <summary>
    /// The combat goals and Self preservation: a fight they start ends the held task through its handler; what an <see cref="AiMind.Engine"/> mind
    /// weighs on its routine and in its own fight.
    /// </summary>
    Combat,
    /// <summary>
    /// Every urgent (priority 3) goal: what a held character on its own Fixing Legs treatment weighs.
    /// </summary>
    Urgent,
}

/// <summary>
/// The hold only Meitou has, which keeps its own choice (docs/simulation.md "AI"): a platoon a code mod holds (<see cref="AiMind.Engine"/> minds,
/// whose routine is the engine's). Every other NPC runs the full ladder (<see cref="Ladder.Run"/>). Pure reads of the character's own state.
/// </summary>
internal static class MeitouHolds
{
    /// <summary>
    /// What may take over a character a Meitou hold keeps that the AI does not run now (<see cref="AiSystem.Holds"/>): on an engine mind's routine,
    /// the combat goals; on its own Fixing Legs treatment every urgent goal; in a fight it did not start Self preservation (nothing for a gunner or a
    /// carrier).
    /// Nothing for one fleeing, imprisoned, carried, down or held by its order.
    /// </summary>
    public static MeitouHold Of(in CharacterHot n, CharacterCold cold)
    {
        if (cold.Ai is not { PlayerSide: false } mind || cold.IsPlayer || cold.Fleeing || cold.IsImprisoned || !cold.CarriedBy.IsNone
            || cold.Medical is { Incapacitated: true } || DecisionSystem.Ordered(cold))
            return MeitouHold.None;

        if (cold.InCombat) return cold.Gunning is null && cold.Carrying.IsNone ? MeitouHold.Self : MeitouHold.None;
        if (mind.Engine && EngineRoutine(n)) return MeitouHold.Combat;
        if (AiSystem.TreatsOwnLegs(n, cold)) return MeitouHold.Urgent;

        return n.Task == (byte)CharacterTask.Treat || InterruptibleDuty(n, cold) ? MeitouHold.Combat : MeitouHold.Self;
    }

    /// <summary>
    /// The engine's routine of an <see cref="AiMind.Engine"/> mind, which a fight its combat goals start interrupts: standing, wandering or
    /// following its leader.
    /// </summary>
    public static bool EngineRoutine(in CharacterHot n) => (CharacterTask)n.Task is CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow;

    /// <summary>
    /// The engine's own walk of a town errand no goal holds (<see cref="Kidnap.TownDutySystem.IsErrandRow"/>: it waits for a fight's end and walks
    /// on), a captive job's walk carrying nothing (<see cref="Kidnap.CaptiveSystem"/> gives the job up at the fight's start), or the meal it stands
    /// at (220 Eating, a priority-2 goal's task; a vampiric meal lasts minutes, #623).
    /// </summary>
    static bool InterruptibleDuty(in CharacterHot n, CharacterCold cold)
    {
        if (n.Task == (byte)CharacterTask.Idle) return cold.Carrying.IsNone && cold.ActionClip == Kidnap.CaptiveSystem.EatClip;
        if (n.Task != (byte)CharacterTask.GoTo || cold.Running is not { Source: TaskSource.Goal, Task: CharacterTask.GoTo } run) return false;

        return Kidnap.TownDutySystem.IsErrandRow(run.Row) || cold.Carrying.IsNone && Kidnap.CaptiveSystem.IsJobRow(run.Row);
    }
}
