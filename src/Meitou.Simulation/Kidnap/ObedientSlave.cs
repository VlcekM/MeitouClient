using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// Job 187 Obedient Slave of a player character (docs/game/ai-tasks.md "Obedient Slave"; Meitou: docs/simulation.md "Captives"). The job is a
/// perma-job the slave state setter adds when a character becomes a slave (state 1; <c>FUN_1405a4940</c> -> <c>FUN_14032e0d0</c>, <b>Verified</b>)
/// after destroying its orders and perma-jobs, and removes when it stops being one. While it is a slave and not sneaking (<c>FUN_14050e5b0</c>), the
/// job's branch scores the owner squad's <c>Slave AI Goals</c> on the job passes: it follows its master, works the town its squad's leader is in,
/// gets into its cage, fights for its master or its squad, runs, or stands, instead of acting on its own goals. Only a player character's list holds
/// the job; an NPC slave holds it in effect. Either way <see cref="Ai.AiSystem.ObeyedRoles"/> names the goals and the chooser's job passes score
/// them with the AI's scorer (<see cref="Ai.TierRungs.PickJobPass"/>); the winner is acted out as any goal of the character's.
/// </summary>
public static class ObedientSlave
{
    /// <summary>
    /// The job is in the character's list.
    /// </summary>
    public static bool HasJob(CharacterCold cold) => cold.Jobs.Exists(j => j.Type == TaskType.SlaveObedience);

    /// <summary>
    /// The branch's gate in <c>FUN_14050e5b0</c> (<b>Verified</b>): the character is in slave state 1 and not sneaking
    /// (<c>Character::isStealthMode</c>, <c>FUN_1405c9750</c>, the Sneak order).
    /// </summary>
    public static bool Obeys(CharacterCold cold) => cold.SlaveState == 1 && (cold.Orders & CharacterOrders.Sneak) == 0;

    /// <summary>
    /// The goals the job pass's 187 branch syncs from the master squad (<c>FUN_14026b790</c>, role 4): its blackboard's <c>Slave AI Goals</c>; null
    /// without a master, a blackboard or any goal. The chooser scores those of priorities 1 to 3. A pure read.
    /// </summary>
    internal static IReadOnlyList<AiGoal>? SlaveList(Squad? master) =>
        master is { Ai: { } board } && board.Roles[(int)AiRole.Slave] is { Count: > 0 } list ? list : null;

    /// <summary>
    /// The slave state changed to <paramref name="state"/> (<c>FUN_1405a4940</c>, only when it differs): becoming a slave destroys the orders and the
    /// perma-jobs and adds the job toward the owner; leaving slavery removes the job only, or, with none in the list, destroys the orders and the
    /// perma-jobs (<c>FUN_14032e0d0</c>, <b>Verified</b>). A player character's list only; an NPC slave obeys again
    /// (<see cref="CharacterCold.ObedienceLost"/>).
    /// </summary>
    internal static void StateChanged(World world, CharacterCold cold, byte state)
    {
        cold.ObedienceLost = false;
        if (!cold.IsPlayer) return;
        if (state == 1)
        {
            Reset(world, cold);
            return;
        }

        int at = cold.Jobs.FindIndex(j => j.Type == TaskType.SlaveObedience);
        if (at < 0)
        {
            Destroy(world, cold);
            return;
        }

        if (SlotOf(world, cold) is >= 0 and var slot) ProductionSystem.RemoveJob(world, slot, at);
        else cold.Jobs.RemoveAt(at);
    }

    /// <summary>
    /// The owner's orders object reset that processing (<c>FUN_14035aa20</c>), chaining (<c>FUN_14032e590</c>) and the state setter share: the
    /// one-shot orders and the perma-jobs are destroyed and the job is added toward the owner (a character the master squad's leader stands in for).
    /// A player character's only; an NPC slave obeys again (<see cref="CharacterCold.ObedienceLost"/>).
    /// </summary>
    internal static void Reset(World world, CharacterCold cold)
    {
        cold.ObedienceLost = false;
        if (!cold.IsPlayer) return;
        Destroy(world, cold);
        var owner = OwnerOf(world, cold);
        PermaJobs.Add(cold, TaskType.SlaveObedience, owner is { } o ? PermaJob.OfCharacter(TaskType.SlaveObedience, o)
                : PermaJob.Untargeted(TaskType.SlaveObedience));
    }

    /// <summary>
    /// DA 28's split (<c>FUN_1405ce4e0</c>, <b>Verified</b>): the perma-jobs alone are destroyed (<c>FUN_140507280</c>), job 187 with them, so a
    /// slave keeps its state and owner but no longer obeys (an NPC's <see cref="CharacterCold.ObedienceLost"/>).
    /// </summary>
    internal static void Disobey(World world, CharacterCold cold)
    {
        DestroyJobs(world, cold, SlotOf(world, cold));
        if (cold.SlaveState == 1) cold.ObedienceLost = true;
    }

    /// <summary>
    /// Destroys the character's perma-jobs (<c>FUN_140507280</c>, DA 53's call after the state setter).
    /// </summary>
    internal static void DropJobs(World world, CharacterCold cold) => DestroyJobs(world, cold, SlotOf(world, cold));

    /// <summary>
    /// Destroys the character's perma-jobs and second list (<c>FUN_140507280</c>, <c>FUN_140507530</c>: a loaded queue); the order queue is kept
    /// (docs/game/ai-tasks.md "What clears the queue").
    /// </summary>
    static void Destroy(World world, CharacterCold cold)
    {
        DestroyJobs(world, cold, SlotOf(world, cold));
        PlayerOrders.ClearSecond(cold);
    }

    /// <summary>
    /// Destroys the character's perma-jobs (<c>FUN_140507280</c>); <paramref name="slot"/> -1: it is not in the table.
    /// </summary>
    static void DestroyJobs(World world, CharacterCold cold, int slot)
    {
        for (int i = cold.Jobs.Count - 1; i >= 0; i--)
        {
            if (slot >= 0) ProductionSystem.RemoveJob(world, slot, i);
            else cold.Jobs.RemoveAt(i);
        }
    }

    /// <summary>
    /// The slot of a living character (-1: not in the table).
    /// </summary>
    static int SlotOf(World world, CharacterCold cold)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
            if (ReferenceEquals(table.Cold(i), cold) && table.Next[i].Alive) return i;
        return -1;
    }

    /// <summary>
    /// The character the owner handle names (<b>Meitou</b>: the leader of the master squad the slave was sold, arrested or processed into; the
    /// original's handle names a character or a squad); null when the master is not known.
    /// </summary>
    internal static CharacterId? OwnerOf(World world, CharacterCold cold)
    {
        if (cold.SlaveMaster < 0 || world.Squads.Find(cold.SlaveMaster) is not { } master) return null;
        var table = world.Characters;
        return !master.Leader.IsNone && table.IsAlive(master.Leader) ? master.Leader : null;
    }

    /// <summary>
    /// The job in the list of a slave with no master squad whose owner is still only its saved handle (a load), else -1: the owner the original
    /// reads through the owner handle (<c>+0x328</c>), which the job's target copies (<c>FUN_14032e0d0</c>). The job stays pending until the restore
    /// pass adopts the master from it (<see cref="CaptiveSystem"/>'s slaver restore), so the far-escape rule does not take the slave for one without
    /// an owner in between (#813's save: an escaped slave 150 ticks after the load).
    /// </summary>
    internal static int PendingOwnerJob(CharacterCold cold)
    {
        if (cold.SlaveState != 1 || cold.SlaveMaster >= 0) return -1;
        return cold.Jobs.FindIndex(j => j.Type == TaskType.SlaveObedience && !j.Saved.IsNull);
    }
}
