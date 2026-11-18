using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The AI's first aid goals: the Medic goal and Fixing Legs (269), whose treatments a think asks for and the <see cref="FirstAidSystem"/> starts in
/// the serial step.
/// </summary>
public sealed partial class AiSystem
{
    /// <summary>
    /// Resolved in the serial step; starts the treatments the think asked for.
    /// </summary>
    FirstAidSystem? _firstAid;

    /// <summary>
    /// Set by a think that chose the Medic goal (<see cref="AiMind.MedicAsk"/>) or Fixing Legs (<see cref="AiMind.HealLegsAsk"/>).
    /// </summary>
    int _treating;

    /// <summary>
    /// Fixing Legs' desire while a character of another faction fights one of the character's own faction (<c>DAT_141696c90</c>).
    /// </summary>
    const float HealLegsUnderThreat = 0.4f;

    /// <summary>
    /// Whether the character's goal lists (its role's list, or the slave lists it obeys, and its race's) hold <paramref name="type"/>: a mind the AI
    /// runs, not an engine one. Read by the serial sweeps that keep a goal's finder result for the think (the Medic goal's,
    /// <see cref="FirstAidSystem"/>).
    /// </summary>
    internal bool ListsGoal(World world, int slot, TaskType type)
    {
        if (world.Characters.Cold(slot) is not { Ai: { Engine: false, Squad: var squad } mind } cold || squad.Ai is not { } board) return false;
        var roles = ObeyedRoles(world, squad, cold) ?? board.Roles[(int)RoleOf(squad, world.Characters.IdOf(slot), cold)];
        for (int k = 0; k < roles.Count; k++)
            if (roles[k].Task.Type == type) return true;
        for (int k = 0; k < mind.RaceGoals.Count; k++)
            if (mind.RaceGoals[k].Task.Type == type) return true;
        return false;
    }

    /// <summary>
    /// Row 269 Fixing Legs' desire (<c>FUN_1409983b0</c>, <b>Verified</b> by decompile; registered with no finder, <c>FUN_140319a60</c>): 0 unless
    /// the character's legs are messed up (state 103, <see cref="Bodies.MedicalState.LegsMessedUp"/>); then 0.4 while its senses count a threat
    /// towards characters of its faction other than itself (AI <c>+0xb4</c>, <c>totalThreatLevelAllies</c>,
    /// <see cref="Perception.SenseBlock.ThreatToAllies"/>), else 1. A threat towards itself is list B's <c>!39</c>, the planner's.
    /// </summary>
    internal static float HealLegsDesire(World world, int slot)
    {
        if (world.Characters.Cold(slot) is not { Medical.LegsMessedUp: true }) return 0;

        return Bodies.CharacterFacts.Senses(world, slot) is { ThreatToAllies: > 0 } ? HealLegsUnderThreat : 1;
    }

    /// <summary>
    /// Fixing Legs, its plan's own step reached: it stands while the serial step starts the treatment of itself
    /// (<see cref="FirstAidSystem.SendGoalTreatments"/>; the original's <c>Task_FirstAid</c> takes the character itself for a goal with no target,
    /// <c>FUN_14034ecf0</c>).
    /// </summary>
    void ActHealLegs(World world, int slot, AiMind mind)
    {
        Become(world, slot, CharacterTask.Idle);
        mind.HealLegsAsk = true;
        Interlocked.Exchange(ref _treating, 1);
    }
}
