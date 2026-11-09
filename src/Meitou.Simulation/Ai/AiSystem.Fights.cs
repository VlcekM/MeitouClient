using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The combat goals of an NPC's lists as priority-3 goals of the chooser (<c>chooseGoal</c> <c>FUN_140510ef0</c>, docs/game/ai-tasks.md "Combat
/// goals"): 16, 21, 144 and 34 attack whom their finder takes, 143, 15 and 288 the attacker of a squad mate or an ally in a fight (row 13,
/// <c>AI::findAttackerOf</c>). The think weighs them with every other goal (desire x finder factor x weight x crowding, x1.25 for the current one)
/// and writes only its own mind's request (<see cref="AiMind.FightTarget"/>); the serial step starts the fight.
/// </summary>
public sealed partial class AiSystem
{
    #region State

    /// <summary>
    /// Resolved in the serial step, read by the think (null before the first tick: no finder finds anything).
    /// </summary>
    AggroSystem? _aggro;

    /// <summary>
    /// Set by a think that asked for a fight (AiMind.FightTarget).
    /// </summary>
    int _fighting;

    #endregion

    #region Desire

    /// <summary>
    /// Row 41's desire (<c>DAT_141682ad0</c>).
    /// </summary>
    const float MasterDesire = 2;

    /// <summary>
    /// A combat goal's desire before its finder (docs/game/ai-tasks.md "Combat goals" and "Desire and commitment"): 16 <c>scoreAttackEnemies</c> is 0
    /// while Passive, sneaking outside a fight or not standing; 15 / 288 <c>scoreProtectAllies</c> (<c>FUN_140599b90</c>, <b>Verified</b>) 0 while
    /// Passive (even in a fight), while sneaking and not in combat mode and for a slave holding job 187 (Meitou's mark of it: a slave with a master
    /// squad), x0.5 while carrying (a carrier on a town errand, <see cref="Preempts"/>); the desire of 15 / 288 is the finder's first ally above 0,
    /// uncapped (<see cref="ProtectScan"/>), the rest 1 (144's 0.5 is its profile's). The finder's factor multiplies it (<see cref="FightPick"/>).
    /// Engine gates for all: a character that cannot fight (no working fighter, crawling, or one Self preservation runs away with,
    /// <see cref="SelfPreservation.Flees"/>) and a turret's gunner (it keeps to its turret) want none.
    /// </summary>
    float FightDesire(World world, int slot, TaskType type, AiMind mind)
    {
        if (world.Characters.Cold(slot) is not { Fighter.CanAttack: true, Medical: { Crawling: false } medical, Gunning: null } cold)
            return 0;

        if (SelfPreservation.Flees(cold, 1, SelfPreservation.Walled(world, slot))) return 0;

        bool passive = (cold.Orders & CharacterOrders.Passive) != 0;
        bool sneaking = (cold.Orders & CharacterOrders.Sneak) != 0 && !cold.InCombat;
        return type switch
        {
            TaskType.AttackEnemies => passive || sneaking || medical.Prone ? 0 : 1,
            TaskType.ProtectAllies or TaskType.ProtectAlliesStayInTown => ProtectGoalDesire(world, slot, type, mind, cold, passive || sneaking),
            // 41's desire is the constant 2 (AI slot 0x178, FUN_1406415d0, Verified): the slave's Slave list weighs it above the other fighting goals.
            TaskType.AttackEnemiesOfMySlavemaster => MasterDesire,
            _ => 1,
        };
    }

    /// <summary>
    /// The protect goals' desire: 0 for an idle character (passive or sneaking, or a slave holding a master's squad), else the finder's first ally
    /// value x0.5 while carrying (<see cref="FightDesire"/>).
    /// </summary>
    float ProtectGoalDesire(World world, int slot, TaskType type, AiMind mind, CharacterCold cold, bool idle)
    {
        if (idle || cold.SlaveState == 1 && cold.SlaveMaster >= 0) return 0;

        return ProtectScan(world, slot, type, mind).First * (cold.Carrying.IsNone ? 1 : 0.5f);
    }

    /// <summary>
    /// 32 Self preservation's desire (AI slot 17 <c>FUN_140599d20</c>, <b>Verified</b>): the threat level
    /// (<see cref="Combat.CombatSystem.ThreatLevel"/>: 1 with a threat within 40, 0.7 with one within 181.7, else 0). In its own fight the goal is
    /// kept while it is not done (its list A <c>!159</c>, a threat on its list at any distance, <c>FUN_140436720</c>; the plan step,
    /// <c>FUN_14050d970</c>): <b>engine</b>, a threat only beyond 181.7 keeps it at 0.7 at Meitou's re-think (the original does not weigh a
    /// running goal again).
    /// </summary>
    float SelfPreservationDesire(World world, int slot, AiMind mind)
    {
        if ((ThreatSource ?? _combat) is not { } source) return 0;

        float level = source.ThreatLevel(world, slot, out _);
        if (level > 0) return level;

        bool holdsGoal = mind.Goal?.Task.Type == TaskType.SelfPreservation;
        bool ownFight = holdsGoal && world.Characters.Cold(slot) is { InCombat: true } cold && OwnsFight(cold, mind);
        return ownFight && source.Threatened(world, slot) ? SelfPreservation.Attacking : 0;
    }

    #endregion

    #region Finders

    /// <summary>
    /// A combat goal's finder: its factor and the character it would fight (<see cref="CharacterId.None"/> with the factor 0). Attack goals:
    /// <see cref="AggroSystem.FindFoe"/>, factor 1 with a pick. Protect goals: <see cref="ProtectPick"/>. <paramref name="pending"/>: the fights the
    /// serial step started this tick, by target (<see cref="SendFights"/>). Reads only the last tick's state (any thread in the think).
    /// </summary>
    (float Factor, CharacterId Target) FightPick(World world, int slot, TaskType type, AiMind mind, IReadOnlyDictionary<int, int>? pending = null)
    {
        if (Protects(type)) return ProtectPick(world, slot, type, mind, pending: pending);
        var foe = type switch
        {
            TaskType.SelfPreservation => SelfDefencePick(world, slot, pending),
            TaskType.AttackEnemies => _aggro?.FindFoe(world, slot, AggroSystem.FoeFinder.Enemies, pending),
            TaskType.AttackEnemiesAndNeutrals => _aggro?.FindFoe(world, slot, AggroSystem.FoeFinder.EnemiesAndNeutrals, pending),
            TaskType.TerritorialAggressionButDontLeaveHome => _aggro?.FindFoe(world, slot, AggroSystem.FoeFinder.Territory, pending),
            TaskType.AttackTroubleMakers => _aggro?.FindFoe(world, slot, AggroSystem.FoeFinder.TroubleMakers, pending),
            TaskType.AttackEnemiesOfMySlavemaster => _aggro?.FindFoe(world, slot, AggroSystem.FoeFinder.MasterEnemies, pending),
            _ => null,
        } ?? CharacterId.None;
        if (!foe.IsNone && AttacksThroughRow(type) && AttackRowBlocked(world, slot, foe)) return (0, CharacterId.None);
        return (foe.IsNone ? 0 : 1, foe);
    }

    /// <summary>
    /// The attack goals whose plan is the melee row (4 Attacking, or 227 for an animal): 16, 21, 144 and 34 (docs/game/ai-tasks.md "Combat goals").
    /// </summary>
    static bool AttacksThroughRow(TaskType type) =>
        type is TaskType.AttackEnemies
            or TaskType.AttackEnemiesAndNeutrals
            or TaskType.TerritorialAggressionButDontLeaveHome
            or TaskType.AttackTroubleMakers;

    /// <summary>
    /// A combat goal's finder factor for the chooser (<see cref="FightPick"/>). The current goal whose fight runs (<see cref="OwnsFight"/>) is kept
    /// while its goal is not done (the original's plan step ends a goal only when its list A holds, its hours end or its row cannot run,
    /// <c>FUN_14050d970</c>, <b>Observed</b>; the running fight is the queued task its plan accepts): an attack goal while the looker senses an enemy
    /// (16's effect, state 6 <c>isNoEnemiesInVicinity</c>, <c>numEnemies</c>; engine reading for 21, 144 and 34) or its finder still picks one; a
    /// protect goal while its finder values a mate or ally above 0 (its own fight counts for 143: <c>FUN_14099a190</c>'s 0.01 floor), row 13 not
    /// asked again.
    /// </summary>
    float FightFactor(World world, int slot, TaskType type, AiMind mind)
    {
        if (mind.Goal?.Task.Type == type && world.Characters.Cold(slot) is { InCombat: true } cold && OwnsFight(cold, mind))
        {
            if (Protects(type)) return ProtectPick(world, slot, type, mind, attacker: false).Factor;
            if (_aggro?.SensesOf(slot) is { Enemies: > 0 }) return 1;
        }

        return FightPick(world, slot, type, mind).Factor;
    }

    /// <summary>
    /// Row 13 Attacking attackers of under 32 Self preservation (<c>AI::findAttackerOf</c> on the character itself, <c>FUN_14099c3b0</c>,
    /// <b>Verified</b>; <see cref="Combat.CombatSystem.BestAttackerOf"/> with the character as both subject and looker: its attackers and its own
    /// target, at any distance, best by the target score): the attacker it fights, <see cref="CharacterId.None"/> when none or when it cannot fight
    /// (no working fighter, crawling). A turret's gunner takes no shooter (its turret row wants only a melee threat gone; <b>engine</b>, kept from
    /// the old answer). <paramref name="pending"/>: the fights the serial step started this tick. Reads only the last tick's state (any thread in
    /// the think).
    /// </summary>
    internal CharacterId SelfDefencePick(World world, int slot, IReadOnlyDictionary<int, int>? pending = null)
    {
        var table = world.Characters;
        if ((ThreatSource ?? _combat) is not { } source) return CharacterId.None;

        if (table.Cold(slot) is not { Fighter.CanAttack: true, Medical.Crawling: false } cold) return CharacterId.None;

        int foe = source.BestAttackerOf(world, slot, slot, pending);
        if (foe < 0) return CharacterId.None;
        var id = new CharacterId(foe, table.Previous[foe].Generation);
        return cold.Gunning is not null && source.StateOf(id).Ranged ? CharacterId.None : id;
    }

    /// <summary>
    /// Asks the serial step for the fight of a combat goal or of Self preservation (<see cref="FightPick"/>; <see cref="SendFights"/>).
    /// Writes only its own mind.
    /// </summary>
    void AskFight(World world, int slot, TaskType type, AiMind mind)
    {
        mind.FightTarget = FightPick(world, slot, type, mind).Target;
        mind.FightGoal = type;
        if (!mind.FightTarget.IsNone) Interlocked.Exchange(ref _fighting, 1);
    }

    #endregion

    #region States

    /// <summary>
    /// The reaches of the states <c>isWithin10Meters</c> (40) and <c>isWithin50Meters</c> (41): world units are decimetres, so 100 and 500 (the exe's
    /// literals, docs/game/ai-tasks.md "States").
    /// </summary>
    internal const float Within10Meters = 100, Within50Meters = 500;

    /// <summary>
    /// The state tests of the melee row's list B that can fail without a plan step in front of them (docs/game/ai-tasks.md, TaskData rows 4 and 227,
    /// <b>Verified</b>; states 139 and 140 <c>FUN_140598e90</c> / <c>FUN_140598ec0</c>: water state 3 of the character, of the target): a person's
    /// row 4 needs neither itself nor its target deep in the water; an animal's row 227 has no swim test but refuses an imprisoned target (78) and
    /// needs it within 500 (41). The accessibility and readiness states (27, 59, 60, 90, 37, 69) are the engine's own checks.
    /// </summary>
    static bool AttackRowBlocked(World world, int slot, CharacterId foe)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } me || !table.TryResolveNext(foe, out int f) || table.Cold(f) is not { } target)
            return false;

        if (me.Fighter?.IsAnimal != true)
            return me is { WaterState: 3, InSomething: Locks.HeldState.None } || target.WaterState == 3;

        if (target.IsImprisoned) return true;

        var mine = Flat(table.Previous[slot].Position);
        var theirs = Flat(table.Previous[f].Position);
        return System.Numerics.Vector2.Distance(mine, theirs) >= Within50Meters;
    }

    /// <summary>
    /// State 12 <c>isAlliesFighting</c> (<c>FUN_14099a350</c>, <b>Verified</b>): any entry of the looker's senses tagged ALLY and not KO (sense
    /// filter 1, 8; seen, heard or remembered) whose fight threat (<c>FUN_14099a190</c>, <see cref="Combat.CombatSystem.FightThreat"/>) is above 0.
    /// Row 244 Grabbing food needs it false; only the food errand (<see cref="Kidnap.TownDutySystem"/>) asks it, the planner's rows 13, 15, 288 and
    /// 234 have their own finders (<see cref="ProtectPick"/>), so <c>PlanContext.Holds</c> leaves the state false.
    /// </summary>
    internal bool AlliesFighting(World world, int slot)
    {
        var prev = world.Characters.Previous;
        if (_combat is not { } fights || _aggro is not { } look || look.SensesOf(slot) is not { } senses) return false;
        foreach (var e in senses.Entries)
        {
            int a = e.Target;
            if (a == slot || (e.Tags & (SenseTag.Ally | SenseTag.Ko)) != SenseTag.Ally) continue;
            if ((uint)a >= (uint)prev.Length || !prev[a].Alive || prev[a].Generation != e.Generation) continue;
            if (fights.FightThreat(world, a, look.SensesOf(a)) > 0) return true;
        }

        return false;
    }

    #endregion

    #region Protect goals

    /// <summary>
    /// The x0.1 on an owned character's fight threat (<c>DAT_14167fad0</c>).
    /// </summary>
    const float OwnedFactor = 0.1f;

    /// <summary>
    /// Whether a combat goal defends someone (its plan row is 13 Attacking attackers of, a defence) rather than attacking (row 4 / 16).
    /// </summary>
    static bool Protects(TaskType type) => type is TaskType.ProtectOwnSquad or TaskType.ProtectAllies or TaskType.ProtectAlliesStayInTown;

    /// <summary>
    /// The protect goals' finders and their plan row 13 (docs/game/ai-tasks.md "Combat goals", <b>Verified</b> finders): 143 PROTECT_OWN_SQUAD
    /// (<c>FUN_14099d510</c>) values each squad mate that is the looker or that its last look sensed now (seen or heard, <c>FUN_140855960</c> with
    /// flag 1), 15 / 288 PROTECT_ALLIES (<c>FUN_14099ca10</c>, the same for both) each entry of its senses tagged ALLY (same faction or relation 50
    /// or more), not down, seen or heard now, a slave or escaped slave only where the looker may help one (<c>FUN_140997e80</c>,
    /// <see cref="RetaliationSystem.SlaveHelpAllowed(World, PopulationData?, Meitou.Data.Gameplay.FactionRelations?, CharacterCold, System.Numerics.Vector3)"/>),
    /// by its fight threat (<c>FUN_14099a190</c>, <see cref="Combat.CombatSystem.FightThreat"/>; no distance factor for an NPC looker). The best (the
    /// first on a tie), capped at 1.2, is the factor; then row 13 (<c>AI::findAttackerOf</c>, <see cref="Combat.CombatSystem.BestAttackerOf"/>) takes
    /// that one's attacker within 150 of it that is not the looker's ally, best by the target score; none fails the plan (factor 0). 143 falls back
    /// to the looker's owned characters at x0.1 (<see cref="OwnedFallback"/>). Without <paramref name="attacker"/> only the finder's value (row 13
    /// not asked, the target none). The first entry above 0 comes back too: 15 / 288's desire (<c>FUN_140599b90</c> asks the finder to stop at it,
    /// uncapped). Reads only the last tick's state (any thread in the think).
    /// </summary>
    internal (int Best, float Value, float First) ProtectScan(World world, int slot, TaskType type, AiMind mind)
    {
        var table = world.Characters;
        var prev = table.Previous;
        if (_combat is not { } fights || _aggro is not { } look || look.SensesOf(slot) is not { } senses || table.Cold(slot) is not { } me)
            return (-1, 0, 0);

        int best = -1;
        float value = 0, first = 0;
        if (type == TaskType.ProtectOwnSquad)
        {
            foreach (var m in mind.Squad.Members)
            {
                if ((uint)m.Slot >= (uint)prev.Length || !prev[m.Slot].Alive || prev[m.Slot].Generation != m.Generation
                    || m.Slot != slot && !senses.Knows(m, now: true))
                    continue;

                float v = fights.FightThreat(world, m.Slot, look.SensesOf(m.Slot));
                if (v > value) (best, value) = (m.Slot, v);
            }

            if (best < 0) (best, value, first) = OwnedFallback(world, slot, mind, fights, look, senses);
        }
        else
        {
            var scan = RetaliationSystem.ScanAllies(
                world, slot, fights, look, senses, SenseTag.Ko | SenseTag.Imprisoned,
                () => RetaliationSystem.SlaveHelpAllowed(world, data, look.Relations, me, world.Characters.Previous[slot].Position),
                (_, threat) => threat);
            (best, value, first) = (scan.Best, scan.Value, scan.First);
            if (best < 0) (best, value, first) = OwnedScan(world, slot, mind, fights, look, senses);
        }

        return (best, value, first);
    }

    /// <summary>
    /// The second loop of 15 / 288's finder (<c>FUN_14099ca10</c>, <b>Verified</b>), run only when no sensed ally had a value above 0: the characters
    /// of the Ownerships list (<c>FUN_1407956a0(character) + 0x10</c>; for a squad of NPCs the slaves it owns,
    /// <see cref="CharacterCold.SlaveMaster"/>; what fills it is <b>Observed</b> from the medic finder's slave pass) that the looker senses now, that
    /// are not down and below slave state 2, valued at their fight threat x 0.1 (<c>DAT_14167fad0</c>), not those whose own target is an ally of the
    /// looker (<c>FUN_140998790</c>, relation 50). The best, the first above 0 for the desire's stop-at-first call.
    /// </summary>
    (int Best, float Value, float First) OwnedScan(World world, int slot, AiMind mind, CombatSystem fights, AggroSystem look, SenseBlock senses)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int best = -1;
        float value = 0, first = 0;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == slot || !prev[s].Alive) continue;
            if (table.Cold(s) is not { SlaveState: < 2, SlaveMaster: >= 0 } owned || owned.SlaveMaster != mind.Squad.Id) continue;
            var id = new CharacterId(s, prev[s].Generation);
            if (!senses.Knows(id, now: true) || CharacterFacts.Down(owned) || TargetsAnAlly(world, slot, s)) continue;
            float v = fights.FightThreat(world, s, look.SensesOf(s)) * OwnedFactor;
            if (v > value) (best, value) = (s, v);
            if (first == 0 && v > 0) first = v;
        }

        return (best, value, first);
    }

    /// <summary>
    /// 143's fallback when no squad mate scores (<c>FUN_14099d510</c>, <b>Verified</b>): the looker's Ownerships list, the characters the looker
    /// senses now (no state or down filter, unlike 15 / 288's <see cref="OwnedScan"/>), by their fight threat x 0.1 (<see cref="OwnedFactor"/>); the
    /// best (the first on a tie), the first above 0 for the stop-at-first call.
    /// </summary>
    (int Best, float Value, float First) OwnedFallback(World world, int slot, AiMind mind, CombatSystem fights, AggroSystem look, SenseBlock senses)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int best = -1;
        float value = 0, first = 0;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == slot || !prev[s].Alive) continue;
            if (table.Cold(s) is not { SlaveMaster: >= 0 } owned || owned.SlaveMaster != mind.Squad.Id) continue;
            if (!senses.Knows(new CharacterId(s, prev[s].Generation), now: true)) continue;
            float v = fights.FightThreat(world, s, look.SensesOf(s)) * OwnedFactor;
            if (v > value) (best, value) = (s, v);
            if (first == 0 && v > 0) first = v;
        }

        return (best, value, first);
    }

    /// <summary>
    /// Whether the character in <paramref name="owned"/> fights someone whose faction is the looker's or its ally (relation 50; <c>FUN_140998790</c>
    /// over <c>FUN_1406b2630</c>).
    /// </summary>
    bool TargetsAnAlly(World world, int looker, int owned)
    {
        var fights = _combat;
        var id = new CharacterId(owned, world.Characters.Previous[owned].Generation);
        if (fights?.TargetOf(owned, id.Generation) is not { Slot: >= 0 } t) return false;

        if ((uint)t.Slot >= (uint)world.Characters.HighWater || world.Characters.Cold(t.Slot) is not { } target)
            return false;

        if (world.Characters.Cold(looker) is not { } me) return false;

        if (target.Faction == me.Faction) return true;

        return data.Relations.Contains(target.Faction) && data.Relations.Contains(me.Faction) && data.Relations.IsAlly(me.Faction, target.Faction);
    }

    /// <summary>
    /// The protect goals' finder factor and their plan row 13 (<see cref="ProtectScan"/> is the finder). The best's value, capped at 1.2, is the
    /// factor; then row 13 (<c>AI::findAttackerOf</c>, <see cref="Combat.CombatSystem.BestAttackerOf"/>) takes that one's attacker within 150 of it
    /// that is not the looker's ally, best by the target score; none fails the plan (factor 0). 143's fallback hand list (+0x2f8, x0.1) is not
    /// modelled; 15 / 288's is <see cref="OwnedScan"/>. Without <paramref name="attacker"/> only the finder's value (row 13 not asked, the target
    /// none). Reads only the last tick's state (any thread in the think).
    /// </summary>
    (float Factor, CharacterId Target) ProtectPick(
        World world, int slot, TaskType type, AiMind mind, bool attacker = true, IReadOnlyDictionary<int, int>? pending = null)
    {
        var (best, value, _) = ProtectScan(world, slot, type, mind);
        var prev = world.Characters.Previous;
        if (best < 0 || _combat is not { } fights) return (0, CharacterId.None);
        if (!attacker) return (MathF.Min(value, CombatSystem.ProtectCap), CharacterId.None);
        int foe = fights.BestAttackerOf(world, best, slot, pending);
        if (foe < 0) return (0, CharacterId.None);

        return (MathF.Min(value, CombatSystem.ProtectCap), new CharacterId(foe, prev[foe].Generation));
    }

    #endregion

    #region Starting and ending fights

    /// <summary>
    /// The targets of the fights SendFights started in its pass, with how many (derived, not hashed).
    /// </summary>
    readonly Dictionary<int, int> _pendingFights = [];

    /// <summary>
    /// Starts the fights the thinks asked for (serial, slot order; docs/game/ai-tasks.md "Combat goals"): an attack goal's as a goal's attack (row
    /// 16), a protect goal's and Self preservation's as a defence (row 13; a self-defence is not in the aggro log), both of the goal's provenance,
    /// for the next tick; the fight is the AI's own (<see cref="AiMind.FightFrom"/>), so the character thinks in it. A character fighting by then, or
    /// whose pick is gone or down, starts none (it thinks again). Each character asks for one fight at most, so none joins two in a tick. The squad
    /// mates of one that starts are asked to think at the next tick, when its fight holds a target, so 143 finds it at once. The original's choosers
    /// run one character after another, and a fight's start puts the fighter on its target's engaged list at once (<c>setAttackTarget</c>), which the
    /// next chooser's target score reads (<c>FUN_14059e300</c>: minus the candidate's attackers; <b>Observed</b>); the thinks here read the last
    /// tick's fights, so every mate picking from one sighting would take the same target. So once a fight is started here, a later starter of this
    /// pass picks again with the fights started so far counted as attackers of their targets.
    /// </summary>
    void SendFights(World world)
    {
        var table = world.Characters;
        _pendingFights.Clear();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { } mind } cold || mind.FightTarget.IsNone) continue;
            var foe = mind.FightTarget;
            var goal = mind.FightGoal;
            mind.FightTarget = CharacterId.None;
            if (!table.Next[s].Alive || cold.InCombat) continue;
            if (_pendingFights.Count > 0 && FightPick(world, s, goal, mind, _pendingFights).Target is { IsNone: false } again)
                foe = again;

            if (!table.TryResolveNext(foe, out int f) || table.Cold(f) is not { Medical.Incapacitated: false }) continue;
            bool self = goal == TaskType.SelfPreservation, protects = self || Protects(goal);
            world.Defer(new AttackOrder([table.IdOf(s)], foe) { Tick = world.Tick + 1, Source = TaskSource.Goal, Defensive = protects });
            mind.FightFrom = world.Tick + 1;
            _pendingFights[f] = _pendingFights.GetValueOrDefault(f) + 1;

            // A self-defence is no combat goal's fight: not in the aggro log.
            if (!self) _aggro?.GoalFight(world, s, f, voice: !protects);
            foreach (var m in mind.Squad.Members)
            {
                if (m.Slot != s && table.TryResolveNext(m, out int ms) && table.Cold(ms) is { InCombat: false, Ai: { } mate })
                    mate.Replan = true;
            }
        }
    }

    /// <summary>
    /// Ends the fights whose fighter's think chose a goal that is no fight (<see cref="InFight"/>; serial, slot order): the fight task ends as any
    /// task's start ends it (<see cref="TaskStart.Become"/>, the character idle), and the character thinks at the next tick, which acts the chosen
    /// goal out.
    /// </summary>
    void LeaveFights(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { LeaveFight: true } mind } cold) continue;
            mind.LeaveFight = false;
            if (table.Next[s].Alive && cold.InCombat) TaskStart.Become(world, s, CharacterTask.Idle, TaskType.Idle, TaskSource.Goal);
            mind.Replan = true;
        }
    }

    #endregion
}