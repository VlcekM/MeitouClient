using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.FirstAid;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The AI's Medic goal: the treatments a think asks for, started by the <see cref="FirstAidSystem"/> in the serial step.
/// </summary>
public sealed partial class AiSystem
{
    /// <summary>
    /// Resolved in the serial step; starts the treatments the think asked for.
    /// </summary>
    FirstAidSystem? _firstAid;

    /// <summary>
    /// Set by a think that chose the Medic goal (<see cref="AiMind.MedicAsk"/>).
    /// </summary>
    int _treating;

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
}