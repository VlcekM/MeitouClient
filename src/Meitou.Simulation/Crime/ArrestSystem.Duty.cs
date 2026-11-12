using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Crime;

/// <summary>
/// The arrest system's goal 204: guards on duty look for a bounty to arrest.
/// </summary>
public sealed partial class ArrestSystem
{
    /// <summary>
    /// Ticks between a guard's looks for a bounty to arrest (engine choice: the AI's cadence).
    /// </summary>
    public const int DutyEveryTicks = 15;

    /// <summary>
    /// Goal 204 HUNT_BOUNTIES, "Arrest bounties" (<b>Verified</b>, docs/game/economy.md 11.8): a character whose running package lists it (law
    /// enforcement add-ons, gate guards) and that is free to act wants it while a free cage exists (AI slot 33, <c>FUN_140598a90</c>); its target is
    /// <see cref="BountyTarget"/> (<c>FUN_14099bfc0</c>). The plan 203 Arresting (list B: 7 target down, 118 unarmed, 48 carrying) leads to the
    /// fight, the lift and the cage goals, which the arrest state machine carries out (engine choice, as for the dialogue's DA 29).
    /// </summary>
    void BountyDuty(World world)
    {
        if (world.Tick % DutyEveryTicks != 0 || world.SystemOf<PopulationSystem>() is not { } population) return;
        var table = world.Characters;
        Span<int> listed = stackalloc int[5];
        foreach (var squad in world.Squads.All)
        {
            if (squad.Members.Count == 0) continue;
            listed.Fill(-1);
            foreach (var member in squad.Members)
            {
                if (!table.TryResolveNext(member, out int g) || table.Cold(g) is not { InCombat: false } gc || !Ai.AiSystem.Holds(table.Next[g], gc)
                    || gc.Fighter is not { CanAttack: true } || Busy(member))
                {
                    continue;
                }
                int role = (int)Ai.AiSystem.RoleOf(squad, member, gc);
                if (listed[role] < 0) listed[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.HuntBounties) ? 1 : 0;
                if (listed[role] == 0 || !FreeCageExists(world, g) || BountyTarget(world, g, gc) is not (>= 0 and var t)) continue;
                Start(world, g, t, force: true);
            }
        }
    }

    /// <summary>
    /// The desire of goal 204 (slot 33): the cage finder finds a free cage for the guard (<see cref="GuardCage"/>, the cages other arrests reserved
    /// left out).
    /// </summary>
    bool FreeCageExists(World world, int g) => GuardCage(world, g, null) is not null;

    /// <summary>
    /// The target finder of goal 204 (<c>FUN_14099bfc0</c>, <b>Verified</b>): the first sensed character (the nearest in sight here) that is not
    /// carried, of no <c>not real</c> faction, no animal and not imprisoned, and that carries a perceived bounty with the guard
    /// (<see cref="PerceivedBounty"/>) or is regarded as an enemy by it (the squad's flag 7 mark is not modelled). -1: none.
    /// </summary>
    int BountyTarget(World world, int g, CharacterCold gc)
    {
        var table = world.Characters;
        var scan = new TargetScan { System = this, World = world, Self = g, Guard = gc, Best = -1, BestD2 = float.MaxValue };
        var p = table.Next[g].Position;
        world.Grid.Query(p.X, p.Z, AggroSystem.RangeOf(gc, AggroSystem.OutdoorSight), ref scan, static (int v, ref TargetScan st) =>
        {
            var tc = st.World.Characters.Cold(v);
            if (v == st.Self || !st.World.Characters.Next[v].Alive || tc is not { Medical.Dead: false } || !tc.CarriedBy.IsNone || tc.IsImprisoned
                || SquadRegistry.IsAnimal(tc)
                || st.System.Crimes.Factions.NotReal(tc.Faction) || st.System.Busy(st.World.Characters.IdOf(v))
                    || !st.System.WantedBy(st.Guard, tc)) return;
            float d2 = Vector3.DistanceSquared(st.World.Characters.Next[st.Self].Position, st.World.Characters.Next[v].Position);
            if (d2 < st.BestD2 || d2 == st.BestD2 && v < st.Best) (st.Best, st.BestD2) = (v, d2);
        });
        return scan.Best;
    }

    /// <summary>
    /// The state of the goal 204 target finder's grid query: the nearest wanted character so far.
    /// </summary>
    struct TargetScan
    {
        /// <summary>
        /// The arrest system the tests go through.
        /// </summary>
        public ArrestSystem System;
        /// <summary>
        /// The world queried.
        /// </summary>
        public World World;
        /// <summary>
        /// The guard's slot.
        /// </summary>
        public int Self;
        /// <summary>
        /// The guard.
        /// </summary>
        public CharacterCold Guard;
        /// <summary>
        /// The nearest target's slot so far, -1: none.
        /// </summary>
        public int Best;
        /// <summary>
        /// Its squared distance.
        /// </summary>
        public float BestD2;
    }

    /// <summary>
    /// Whether the guard goes after the target: a bounty it perceives (<see cref="PerceivedBounty"/>), or the target's faction is its enemy.
    /// </summary>
    bool WantedBy(CharacterCold guard, CharacterCold target) =>
        PerceivedBounty(guard, target)
            || Crimes.Relations.Contains(guard.Faction) && Crimes.Relations.Contains(target.Faction)
            && AggroSystem.IsEnemy(Crimes.Relations, guard.Faction, target.Faction);

    /// <summary>
    /// A bounty the target carries that the guard sees (<c>FUN_140853fd0</c>, <b>Verified</b>): the entry of the guard's booking faction is above 0,
    /// and either the target is recently wanted (the status marker above 0) or it is down and the guard regards it as an enemy.
    /// </summary>
    internal bool PerceivedBounty(CharacterCold guard, CharacterCold target)
    {
        if ((target.Bounties?.BookedAmount(Crimes.Relations, guard.Faction) ?? 0) <= 0) return false;
        if (target.RecentlyWanted > 0) return true;
        return target.Medical is { Incapacitated: true } && Crimes.Relations.Contains(guard.Faction) && Crimes.Relations.Contains(target.Faction)
            && AggroSystem.IsEnemy(Crimes.Relations, guard.Faction, target.Faction);
    }
}