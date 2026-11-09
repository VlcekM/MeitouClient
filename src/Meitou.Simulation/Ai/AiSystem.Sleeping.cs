using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Perception;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The AI's sleeping: the bed goals' desire and the minutes between bed uses.
/// </summary>
public sealed partial class AiSystem
{
    /// <summary>
    /// Minutes since the last bed use under which a character is not sent to bed again (an animal: 180, <c>DAT_1416f0128</c>) and under which the
    /// desire is reduced (any: 20, <c>DAT_1416f0118</c>).
    /// </summary>
    const float AnimalBedMinutes = 180, RestedBedMinutes = 20;

    /// <summary>
    /// Row 98 Sleeping's desire once rested (<c>DAT_14168955c</c>).
    /// </summary>
    const float RestedSleepDesire = 0.3f;

    /// <summary>
    /// Row 98 Sleeping's desire (AI slot 30, <c>FUN_1405a0880</c>, <b>Verified</b>; the planner drops a node whose desire is not above 0, so 0 makes
    /// 153 Going to bed a dead end): 0 for a character carrying someone (Character <c>+0x348</c>) or of a robot race; 0 while
    /// <see cref="SleepDanger"/> is above 0; for an NPC 1 while it lies in a bed (status <c>+0xb4</c>), 0 when it is in its home and knows a neutral
    /// or enemy that is in the same building, 0 for an animal that used a bed under 180 minutes ago and 0.3 for any character under 20 minutes ago
    /// (status <c>+0xc0</c>, <see cref="CharacterCold.BedUsedHours"/>), else 1. The player-faction branch is not modelled: the AI plans for no player
    /// character.
    /// </summary>
    internal float SleepingDesire(World world, Blackboard board, int slot)
    {
        if (world.Characters.Cold(slot) is not { } me || !me.Carrying.IsNone || me.Race is { IsRobot: true } || me.IsPlayer) return 0;
        if (SleepDanger(world, board, slot, me) > 0) return 0;
        if (me.InSomething == Locks.HeldState.Bed) return 1;
        if (board.OwnHome is { } home && world.Sight.BuildingOf(slot) == home.Key && StrangerKnown(world, slot)) return 0;
        return BedClockDesire(me, (world.GameHours - me.BedUsedHours) * 60);
    }

    /// <summary>
    /// The last part of <see cref="SleepingDesire"/>: by the minutes since the character last used a bed.
    /// </summary>
    internal static float BedClockDesire(CharacterCold me, double minutes)
    {
        if (me.Fighter?.IsAnimal == true && minutes < AnimalBedMinutes) return 0;
        return minutes < RestedBedMinutes ? RestedSleepDesire : 1;
    }

    /// <summary>
    /// The danger score the sleeping desire reads (AI slot 28, <c>FUN_1405a0b50</c>, <b>Verified</b>): 2 with an enemy sensed (<c>numEnemies</c>; AI
    /// <c>+0x80</c>, meaning <b>Unknown</b>, is not read), 1.5 with an alarm of the character's town near it (<see cref="AlarmOf"/>, stand-in for the
    /// alarm finder <c>FUN_14092a810</c>, <b>Observed</b>), 1 with a stranger it knows in its home building, else 0.
    /// </summary>
    float SleepDanger(World world, Blackboard board, int slot, CharacterCold me)
    {
        if (_aggro?.SensesOf(slot) is { Enemies: > 0 }) return 2;
        if (AlarmOf(world, slot, TaskType.InvestigateAlarms, board) is not null) return 1.5f;
        return board.OwnHome is { } home && world.Sight.BuildingOf(slot) == home.Key && StrangerKnown(world, slot) ? 1 : 0;
    }

    /// <summary>
    /// A sensed neutral or enemy (filter 6, not 0x208: neither KO nor imprisoned) in the same building as the character that it knows
    /// (<c>FUN_140855960</c>, flag clear).
    /// </summary>
    bool StrangerKnown(World world, int slot)
    {
        if (_aggro?.SensesOf(slot) is not { } senses) return false;
        var table = world.Characters;
        string? here = world.Sight.BuildingOf(slot);
        foreach (var e in senses.Entries)
        {
            if ((e.Tags & (SenseTag.Neutral | SenseTag.Enemy)) == 0 || (e.Tags & (SenseTag.Ko | SenseTag.Imprisoned)) != 0) continue;
            if ((uint)e.Target >= (uint)table.HighWater
                || !table.Previous[e.Target].Alive
                || table.Previous[e.Target].Generation != e.Generation)
                continue;

            if (world.Sight.BuildingOf(e.Target) == here) return true;
        }

        return false;
    }
}