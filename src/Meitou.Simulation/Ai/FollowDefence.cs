using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The fight a follow row (31 Staying close, 45 Bodyguard, 289) plans before itself (docs/game/ai-tasks.md "Precedence", <b>Verified</b> but for
/// the plan's order, <b>Observed</b>): 31 and
/// 289 need <c>!16</c> and 45 <c>!16 !39</c> (a threat within 40 of the followed character, of the follower itself: <c>FUN_14059de10</c> on their
/// <c>+0x2b0</c>), which 13 Attacking attackers of provides on the row's target: its finder (<c>AI::findAttackerOf</c>,
/// <see cref="CombatSystem.BestAttackerOf"/>) takes the followed character's attacker and the chain goes on to 4 Attacking, whose desire
/// (<c>FUN_1405a4260</c>) must be above 0. The chooser plans it for a follow job (the job pass) and a follow order (the order step); the plan step
/// ends a running follow whose list B fails so that the chooser plans again (<see cref="Due"/>). Nothing else starts it: the task's own update and
/// the attacked character's alert start no fight for a follower. Serial phases.
/// </summary>
public static class FollowDefence
{
    /// <summary>
    /// The event log's cause of the step's fight.
    /// </summary>
    const string Cause = "defending the character it follows";

    /// <summary>
    /// Whether the row's plan starts with 13: the first entry of its list B that fails (the planner follows that one, <see cref="RowStates.FirstUnmet"/>)
    /// is 16, the followed character <paramref name="charge"/> threatened, or 39, the follower itself (45 only).
    /// </summary>
    public static bool Wanted(World world, int slot, TaskType row, CharacterId charge)
    {
        if (TaskDataTable.Of(row) is not { } data) return false;

        var unmet = RowStates.FirstUnmet(world, slot, data, new RowTarget(charge, null));
        return unmet is { State: StateType.IsAggressionTowards or StateType.IsAggressionTowardsMe };
    }

    /// <summary>
    /// The attacker the row's 13 step fights (<see cref="Wanted"/>): the followed character's attacker its finder scores best for the follower,
    /// none when the step is not wanted, the finder takes no one, or the chain's attack is worth nothing (<see cref="Fights"/>, <see cref="Reaches"/>):
    /// then the row's plan fails.
    /// </summary>
    public static CharacterId Foe(World world, int slot, TaskType row, CharacterId charge)
    {
        var table = world.Characters;
        if (!Wanted(world, slot, row, charge) || world.SystemOf<CombatSystem>() is not { } combat) return CharacterId.None;
        if (!table.TryResolveNext(charge, out int c) || table.Cold(slot) is not { } cold || !Fights(world, slot, cold, combat)) return CharacterId.None;

        int foe = combat.BestAttackerOf(world, c, slot);
        if (foe < 0 || foe == slot) return CharacterId.None;

        var id = table.IdOf(foe);
        return Reaches(world, slot, cold, foe, combat.StateOf(id)) ? id : CharacterId.None;
    }

    /// <summary>
    /// Starts the step: the fight on <paramref name="foe"/> for the next tick under the row's provenance (<paramref name="row"/> from
    /// <paramref name="source"/>, job <paramref name="job"/>), a defence that books no assault; the row's follow is taken up again once it is over
    /// (<see cref="MovementSystem.FollowAgain"/>).
    /// </summary>
    public static void Start(World world, int slot, CharacterId foe, TaskType row, TaskSource source, int job = -1) =>
        world.Defer(new AttackOrder([world.Characters.IdOf(slot)], foe)
        {
            Tick = world.Tick + 1, Defensive = true, Source = source, Row = row, Job = job, Cause = Cause,
        });

    /// <summary>
    /// The plan step on a running follow (<c>FUN_14050d970</c> tests the running row's list B on every AI update and ends the plan, which asks a
    /// replan): a job's or an order's follow row runs its follow and its list B fails on 16 or 39 (<see cref="Wanted"/>), so the chooser is due. A
    /// follow order running its own row is not one: the order step pops it (<see cref="PlayerOrders.FollowRowRuns"/>). A pure read.
    /// </summary>
    public static bool Due(World world, int slot, CharacterCold cold)
    {
        if (cold.Running is not { Source: TaskSource.Job or TaskSource.Order } run || !PermaJobs.Follows(run.Row)) return false;
        if (world.Characters.Next[slot].Task != (byte)CharacterTask.Follow || !run.Runs(CharacterTask.Follow)) return false;

        return !PlayerOrders.FollowRowRuns(cold) && Wanted(world, slot, run.Row, cold.FollowOrder);
    }

    /// <summary>
    /// Whether the chain 13 to 4 has a value above 0 for the follower (row 4's desire, <c>FUN_1405a4260</c>, <b>Verified</b>): 13 in its repertoire
    /// (not imprisoned or chained), a working fighter with a working arm; crippled (crawling), only of the player's side or with a threat within 40
    /// of itself (its <c>+0x2b0</c>; a non-unique NPC with none answers 0). Row 4's desire reads neither Passive nor sneaking (those zero 15's).
    /// </summary>
    static bool Fights(World world, int slot, CharacterCold cold, CombatSystem combat)
    {
        if (cold.Fighter is not { CanAttack: true } || cold.Medical is not { } medical) return false;
        if (!TaskStart.Eligible(world, cold, TaskType.AttackAttackersOf) || SelfPreservation.AttackDesire(cold, immediate: true) <= 0) return false;

        return !medical.Crawling || DecisionSystem.Decides(cold) || CharacterFacts.ImmediateThreat(combat, world, slot);
    }

    /// <summary>
    /// Whether the follower may take <paramref name="foe"/> on: neither of them in deep water (4 needs <c>!me swim</c>,
    /// <see cref="CombatSystem.SwimBarred"/>), and on Hold the foe passes the post test (4's desire on Hold, <see cref="Orders.HoldPost"/>).
    /// </summary>
    static bool Reaches(World world, int slot, CharacterCold cold, int foe, in CombatSlot state)
    {
        var table = world.Characters;
        if (CombatSystem.SwimBarred(cold, table.Cold(foe), focused: false)) return false;
        if (!Orders.Holding(cold)) return true;

        var next = table.Next;
        bool held = table.Cold(foe)?.Medical?.Crawling == true || state.AttackTech >= 0;
        var post = Orders.HoldPointOf(cold, next[slot].Position);
        return Orders.HoldPost(next[slot].Position, post, next[foe].Position, next[foe].Velocity, next[foe].Yaw, held, true);
    }
}
