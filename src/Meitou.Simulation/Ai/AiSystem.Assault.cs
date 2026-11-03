using Meitou.Simulation.Combat;

namespace Meitou.Simulation.Ai;

public sealed partial class AiSystem
{
    // Resolved in the serial step; the think only reads it (null before the first tick: only the mission target itself is then known).
    CombatSystem? combat;

    // Set by a think that picked someone to assault (<see cref="AiMind.AssaultTarget"/>).
    int assaulting;

    /// <summary>
    /// The finder of DEFEAT_SQUAD 101 (<c>FUN_140793eb0</c>, docs/game/ai-tasks.md "Combat goals"): of the squad of the contract's mission target, the member it would attack, or
    /// <see cref="CharacterId.None"/> (the factor 0). A member counts when the AI knows it (<c>FUN_140855960</c>), it passes the area test (<c>FUN_140599f30</c>), is not
    /// imprisoned (Character +0x2f8 != 2) and is standing (vfunc 0x368 == 0: not crawling or ragdolled); the best by the target score (<c>FUN_14059e300</c>) wins, which drops
    /// the faction's allies (unless slaves) and members it found no path to. Reads the last tick's state only (any thread in the think).
    /// </summary>
    CharacterId AssaultTargetFor(World world, int slot, Blackboard board)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var mission = board.MissionTarget;
        if (mission.IsNone || !table.IsAlive(mission) || table.Cold(slot) is not { Medical: { } medical } me || table.Cold(mission.Slot) is not { } target
            || world.Squads.Find(target.SquadId) is not { } squad) return CharacterId.None;
        var best = CharacterId.None;
        float bestScore = float.NegativeInfinity;
        var here = prev[slot].Position;
        foreach (var m in squad.Members)
        {
            if (m.Slot == slot || !table.IsAlive(m) || table.Cold(m.Slot) is not { IsImprisoned: false, Medical: { Incapacitated: false, Prone: false } } c) continue;
            var fight = combat?.StateOf(m) ?? CombatSlot.Fresh(m.Generation);
            var at = prev[m.Slot].Position;
            // Known: an entry of its senses, any tag or age (FUN_140855960 without its flag), or the contract's own target (the branch of the test that reads a stored hand is
            // Unknown, docs/game/ai-tasks.md).
            if (m != mission && !CharacterFacts.Knows(world, slot, m)) continue;
            // Area: its home town, or ROAM_FAR's reach, or fighting it.
            if (!(board.Town >= 0 && TownAt(at) == board.Town) && System.Numerics.Vector3.DistanceSquared(here, at) >= Production.ProductionSystem.JobRangeSq
                && !(fight.TargetSlot == slot && fight.TargetGeneration == prev[slot].Generation)) continue;
            // Allies by faction are left out of the choice, a slave never is.
            if (c.SlaveState != 1 && (c.Faction == me.Faction || Relations.Contains(c.Faction) && Relations.Contains(me.Faction) && Relations.IsAlly(me.Faction, c.Faction))) continue;
            if (PursuitSystem.PathImpossible(me, m, world.Tick)) continue;
            var candidate = new TargetCandidate
            {
                Staggered = fight.StumbleClip >= 0 && world.Tick < fight.StunUntil,
                Attackers = combat?.AttackerCount(world, m.Slot) ?? 0,
                Taunt = (c.Orders & CharacterOrders.Taunt) != 0,
                Passive = (c.Orders & CharacterOrders.Passive) != 0,
                Distance = MathF.Sqrt((here.X - at.X) * (here.X - at.X) + (here.Z - at.Z) * (here.Z - at.Z)),
                Fighting = c.InCombat,
                Gunner = c.Gunning is not null,
                Crippled = c.Fighter is not { CanAttack: true },
                Peaceful = !c.InCombat && !AggroSystem.IsEnemy(Relations, c.Faction, me.Faction),
                GettingUp = CharacterFacts.GettingUp(c, world.Tick),
            };
            // The first of equal scores keeps it.
            if (TargetScore.Score(candidate, false, false, medical.Prone, me.Fighter?.IsAnimal == true, me.Manoeuvre) is { } score && score > bestScore) (best, bestScore) = (m, score);
        }
        return best;
    }

    /// <summary>Orders each assaulter that picked someone to attack it (serial; the attack is a new fight, not a defence). Fighting, it is the combat system's until the fight ends.</summary>
    void SendAssaults(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s)?.Ai is not { } mind || mind.AssaultTarget.IsNone) continue;
            var foe = mind.AssaultTarget;
            mind.AssaultTarget = CharacterId.None;
            if (table.Next[s].Alive && table.TryResolveNext(foe, out _)) world.Defer(new AttackOrder([table.IdOf(s)], foe) { Tick = world.Tick + 1 });
        }
    }
}
