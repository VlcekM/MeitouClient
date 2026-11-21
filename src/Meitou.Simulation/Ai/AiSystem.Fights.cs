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

    #region Hold's goal

    /// <summary>
    /// Goal 16 Attacking enemies as the Hold standing order adds it to a player-faction character's own list (<c>FUN_1405caa50</c> case 0xc,
    /// <b>Verified</b>: priority 3, weight 1.0 <c>DAT_14167c308</c>, hours 0 to 100, after the entries setupAI made, unless the list holds a 16,
    /// <c>FUN_140507440</c>; turned off, the list's 16 goes, <c>FUN_140507390</c>). Standing orders are the player faction's only (the function returns
    /// at its top for any other).
    /// </summary>
    internal static readonly AiGoal HoldAttackGoal =
        new(new AiTask("", "ATTACK_ENEMIES", TaskType.AttackEnemies, TaskPriority.Urgent, TaskTargetType.Specific, TaskEndEvent.Nothing), 0, 100, 1f);

    /// <summary>
    /// The Hold entry of a player-faction mind's own list (<see cref="HoldAttackGoal"/>), null without it: there while the Hold toggle is on
    /// (CharStats <c>+0x12b</c>, which the standing order writes with the entry; sneaking leaves it, 16's desire is 0 then) and the list holds no 16 of
    /// its own. Derived from the toggle, so nothing more is kept, saved or hashed. A pure read.
    /// </summary>
    internal static AiGoal? HoldGoalOf(CharacterCold cold, AiMind mind)
    {
        if (!mind.Player || (cold.Orders & CharacterOrders.Hold) == 0) return null;

        foreach (var g in mind.RaceGoals)
        {
            if (g.Task.Type == TaskType.AttackEnemies) return null;
        }

        return HoldAttackGoal;
    }

    #endregion

    #region Desire

    /// <summary>
    /// Row 41's desire (<c>DAT_141682ad0</c>).
    /// </summary>
    const float MasterDesire = 2;

    /// <summary>
    /// A combat goal's desire before its finder (docs/game/ai-tasks.md "Combat goals" and "Desire and commitment"): 16 <c>scoreAttackEnemies</c> is 0
    /// while Passive, sneaking outside a fight or not standing; 15 / 288 <c>scoreProtectAllies</c> (<c>FUN_140599b90</c>, <b>Verified</b>) 0 while
    /// Passive (even in a fight), while sneaking and not in combat mode and for a slave holding job 187 (<see cref="HoldsJob187"/>), x0.5 while
    /// carrying (a carrier on a town errand); the desire of 15 / 288 is the finder's first ally above 0,
    /// uncapped (<see cref="ProtectScan"/>), and 143's the same (AI slot 14 asks 15's finder whatever the row; 143's own finder only multiplies),
    /// the rest 1 (144's 0.5 is its profile's). The finder's factor multiplies it (<see cref="FightPick"/>).
    /// A character that cannot fight wants none: no working fighter, or no working arm (row 4's desire 0, <see cref="SelfPreservation.AttackDesire"/>:
    /// every combat goal plans through 4); a crawler and a turret's gunner none but a protect goal, on row 4's terms (<see cref="ProtectsCrawling"/>).
    /// </summary>
    float FightDesire(World world, int slot, TaskType type, Squad squad)
    {
        if (world.Characters.Cold(slot) is not { Fighter.CanAttack: true, Medical: { } medical } cold) return 0;
        if (Protects(type) ? !ProtectsCrawling(world, slot, cold) : medical.Crawling || cold.Gunning is not null) return 0;

        if (SelfPreservation.AttackDesire(cold, immediate: true) <= 0) return 0;

        bool passive = (cold.Orders & CharacterOrders.Passive) != 0;
        bool sneaking = (cold.Orders & CharacterOrders.Sneak) != 0 && !cold.InCombat;
        return type switch
        {
            TaskType.AttackEnemies => passive || sneaking || medical.Prone ? 0 : 1,
            TaskType.ProtectAllies or TaskType.ProtectAlliesStayInTown => ProtectGoalDesire(world, slot, type, squad, cold, passive || sneaking),
            // 143 registers the same desire, AI slot 14 (FUN_140599b90, Verified): it asks 15's finder, not 143's own squad scan.
            TaskType.ProtectOwnSquad => ProtectGoalDesire(world, slot, TaskType.ProtectAllies, squad, cold, passive || sneaking),
            // 41's desire is the constant 2 (AI slot 0x178, FUN_1406415d0, Verified): the slave's Slave list weighs it above the other fighting goals.
            TaskType.AttackEnemiesOfMySlavemaster => MasterDesire,
            TaskType.Bodyguard => BodyguardDesire(world, slot, squad),
            _ => 1,
        };
    }

    /// <summary>
    /// Whether a crawling character can take up a protect goal: rows 15 and 13 and their attack 4 are in the crippled repertoire (0x40), and no
    /// desire, finder or state of the chain tests posture but row 4's (<c>FUN_1405a4260</c>, <b>Verified</b>): a crippled character answers 0.1
    /// with a threat within 40 (Character <c>+0x2b0</c>) or when it is of the player's faction; an NPC without one 0 (unless its CharStats
    /// <c>+0x450</c> flags <c>FUN_1402b2b50</c> / <c>FUN_1402b2b90</c> or <c>FUN_140506960</c> hold, not modelled). Any other character can; a
    /// turret's gunner too, whose turret goal or job competes with them on its rung.
    /// </summary>
    bool ProtectsCrawling(World world, int slot, CharacterCold cold) =>
        cold.Medical is not { Crawling: true } || DecisionSystem.Decides(cold)
            || _combat is { } combat && Bodies.CharacterFacts.ImmediateThreat(combat, world, slot);

    /// <summary>
    /// The protect goals' desire: 0 for an idle character (passive or sneaking, or a slave holding job 187: a player character's list holds it, an
    /// NPC has a master's squad and its job not split off), else the finder's first ally value x0.5 while carrying (<see cref="FightDesire"/>).
    /// </summary>
    float ProtectGoalDesire(World world, int slot, TaskType type, Squad squad, CharacterCold cold, bool idle)
    {
        if (idle || HoldsJob187(cold)) return 0;

        return ProtectScan(world, slot, type, squad).First * (cold.Carrying.IsNone ? 1 : 0.5f);
    }

    /// <summary>
    /// A slave (state 1) holding job 187 (<c>FUN_140597380</c> with 0xbb in <c>FUN_140599b90</c>): a player character's list has it, an NPC has a
    /// master's squad and its job was not split off (<see cref="HoldsObedience"/>).
    /// </summary>
    static bool HoldsJob187(CharacterCold cold) => cold.SlaveState == 1 && (cold.IsPlayer || cold.SlaveMaster >= 0) && HoldsObedience(cold);

    /// <summary>
    /// 32 Self preservation's desire (AI slot 17 <c>FUN_140599d20</c>, <b>Verified</b>): the threat level
    /// (<see cref="Combat.CombatSystem.ThreatLevel"/>: 1 with a threat within 40, 0.7 with one within 181.7, else 0). Not the current goal, it is no
    /// candidate while no threat is on the senses' list (<see cref="Combat.CombatSystem.Threatened"/>): the scorer drops it before any plan, its list
    /// A <c>!159</c> holding, its finished test the base one and its effect test passing unasked (<c>FUN_14050df10</c>, <b>Verified</b>), so an
    /// <c>attackTarget</c> push alone (an alarm's investigator, a failed knock-out's target) weighs nothing; a crime's victim that witnesses the crime
    /// has the culprit on the list (<see cref="Perception.SenseBlock.Witnessed"/>). In its own fight the goal is kept while it is not
    /// done (its list A, a threat on its list at any distance, <c>FUN_140436720</c>; the plan step, <c>FUN_14050d970</c>): <b>engine</b>, a threat
    /// only beyond 181.7 keeps it at 0.7 at Meitou's re-think (the original does not weigh a running goal again).
    /// </summary>
    float SelfPreservationDesire(World world, int slot, AiMind mind)
    {
        if ((ThreatSource ?? _combat) is not { } source) return 0;

        bool holdsGoal = mind.Goal?.Task.Type == TaskType.SelfPreservation;
        if (!holdsGoal && !source.Threatened(world, slot)) return 0;

        float level = source.ThreatLevel(world, slot, out _);
        if (level > 0) return level;

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
    (float Factor, CharacterId Target) FightPick(World world, int slot, TaskType type, Squad squad, IReadOnlyDictionary<int, int>? pending = null)
    {
        // An animal in a bed cannot plan any fight: the melee rows need it ready for action and standing, and no row of its repertoire gets it out of
        // the bed (docs/game/ai-tasks.md "Animals in beds").
        if (world.Characters.Cold(slot) is { } lying && Repertoire.StuckInBed(world, lying))
            return (0, CharacterId.None);

        if (type == TaskType.Bodyguard) return BodyguardPick(world, slot, squad, pending);
        if (Protects(type)) return ProtectPick(world, slot, type, squad, pending: pending);
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
        bool blocked = !foe.IsNone && (AttacksThroughRow(type) ? AttackRowBlocked(world, slot, foe) : SwimBarred(world, slot, foe));
        if (blocked) return (0, CharacterId.None);
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
    /// (16's effect, state 6 <c>isNoEnemiesInVicinity</c>, <c>numEnemies</c>; engine reading for 21, 144 and 34) and its fight has a target
    /// (<see cref="FightHasTarget"/>), or its finder still picks one; a protect goal while its finder values a mate or ally above 0 (its own fight
    /// counts for 143: <c>FUN_14099a190</c>'s 0.01 floor), row 13 not asked again. A fight without a target is not kept by the enemy count: the
    /// scorer scores the current goal as desire x finder too (<c>FUN_14050df10</c> → <c>FUN_14060fd80</c>, <b>Observed</b>), a goal scoring 0
    /// is no candidate, and the pick (<c>FUN_140510650</c>) then plans another goal over the fight, which the plan step does not end; the count
    /// also holds enemies no longer seen or heard, which no finder picks, so the fighter stood in its stance until their entries decayed (#1035;
    /// docs/game/combat.md "Leaving a fight").
    /// </summary>
    float FightFactor(World world, int slot, TaskType type, AiMind mind)
    {
        if (mind.Goal?.Task.Type == type && world.Characters.Cold(slot) is { InCombat: true } cold && OwnsFight(cold, mind))
        {
            if (type == TaskType.Bodyguard) return BodyguardPick(world, slot, mind.Squad).Factor;
            if (Protects(type)) return ProtectPick(world, slot, type, mind.Squad, attacker: false).Factor;
            if (_aggro?.SensesOf(slot) is { Enemies: > 0 } && FightHasTarget(world, slot)) return 1;
        }

        return FightPick(world, slot, type, mind.Squad).Factor;
    }

    /// <summary>
    /// Whether the character's fight has a target as of the last tick; true without a combat system (nothing then tells a fight without one).
    /// </summary>
    bool FightHasTarget(World world, int slot)
    {
        if ((ThreatSource ?? _combat) is not { } source) return true;

        return source.TargetOf(slot, world.Characters.Previous[slot].Generation).Slot >= 0;
    }

    /// <summary>
    /// Row 13 Attacking attackers of under 32 Self preservation (<c>AI::findAttackerOf</c> on the character itself, <c>FUN_14099c3b0</c>,
    /// <b>Verified</b>; <see cref="Combat.CombatSystem.BestAttackerOf"/> with the character as both subject and looker: its engaged list, its threats
    /// list and its own target, at any distance, best by the target score): the attacker it fights, <see cref="CharacterId.None"/> when none, when it cannot fight
    /// (no working fighter) or when deep water bars the fight (<see cref="SwimBarred(World, int, CharacterId)"/>). A crawling character's
    /// attacker is taken too: row 13 and its 4 are in the crippled repertoire, and 4's desire is 0.1 for it (<c>FUN_1405a4260</c>); a crippled NPC's
    /// 32 runs before (<see cref="SelfPreservationPlans"/>). A turret's gunner takes no shooter (its turret row wants only a melee threat gone;
    /// <b>engine</b>, kept from the old answer). <paramref name="pending"/>: the fights the serial step started this tick. Reads only the last
    /// tick's state (any thread in the think).
    /// </summary>
    internal CharacterId SelfDefencePick(World world, int slot, IReadOnlyDictionary<int, int>? pending = null) =>
        SelfDefencePick(world, slot, pending, out _);

    /// <summary>
    /// <see cref="SelfDefencePick(World, int, IReadOnlyDictionary{int, int}?)"/>; <paramref name="noneFound"/>: row 13's finder ran and took no one
    /// (<c>findAttackerOf</c> answers 0), the engine's own refusals (deep water, Hold's post, a gunner's shooter) aside.
    /// </summary>
    CharacterId SelfDefencePick(World world, int slot, IReadOnlyDictionary<int, int>? pending, out bool noneFound)
    {
        noneFound = false;
        var table = world.Characters;
        if ((ThreatSource ?? _combat) is not { } source) return CharacterId.None;

        if (table.Cold(slot) is not { Fighter.CanAttack: true } cold) return CharacterId.None;

        int foe = source.BestAttackerOf(world, slot, slot, pending);
        noneFound = foe < 0;
        if (noneFound) return CharacterId.None;

        var id = new CharacterId(foe, table.Previous[foe].Generation);
        if (SwimBarred(world, slot, id) || !PassesHoldPost(world, slot, cold, foe, source.StateOf(id))) return CharacterId.None;
        return cold.Gunning is not null && source.StateOf(id).Ranged ? CharacterId.None : id;
    }

    /// <summary>
    /// Whether 32 Self preservation's plan has a provider (the planner's search from 32's effect <c>!39</c>, docs/game/ai-tasks.md "Combat goals",
    /// <b>Verified</b>): <paramref name="runs"/> when 35 wins (<see cref="SelfPreservation.RunsAt"/> at row 35's desire, a player-faction mind's
    /// with its vetoes; with row 4 worth nothing it wins whenever it can run), else 13 on an attacker of its own, else 35 when row 13's finder takes
    /// no one (the chain through 13 dead-ends; 35 is made at any desire above 0); false when none of them plans. Reads only the last tick's state.
    /// </summary>
    internal bool SelfPreservationPlans(World world, int slot, AiMind mind, float desire, out bool runs)
    {
        runs = false;
        if (world.Characters.Cold(slot) is not { Medical: { } medical } me) return false;

        float run = mind.PlayerSide
            ? PlayerSideRowRunDesire(world, slot, me, medical)
            : SelfPreservation.RunDesire(me, SelfPreservation.Walled(world, slot));
        runs = SelfPreservation.RunsAt(me, desire, run);
        if (runs) return true;

        // Row 4 worth nothing: 13 dead-ends, and 35 cannot run either (RunsAt would have taken it).
        if (!AttackRowWanted(me, medical, mind, desire)) return false;

        if (!SelfDefencePick(world, slot, null, out bool noneFound).IsNone) return true;

        runs = noneFound && SelfPreservation.CanRunAt(me, run);
        return runs;
    }

    /// <summary>
    /// Whether row 4 under 13 is worth anything (AI slot 53 <c>FUN_1405a4260</c>, <b>Verified</b>; <see cref="SelfPreservation.AttackDesire"/>): a
    /// player-faction mind's crippled character with a working arm is worth 0.1 whether or not a threat is within 40.
    /// </summary>
    static bool AttackRowWanted(CharacterCold me, Bodies.MedicalState medical, AiMind mind, float desire)
    {
        if (SelfPreservation.AttackDesire(me, desire >= 1) > 0) return true;

        return mind.PlayerSide && medical.Crawling && (medical.ArmWorks(left: true) || medical.ArmWorks(left: false));
    }

    /// <summary>
    /// A character on Hold (standing order 12, <see cref="Orders.Holding"/>; only the player's have it) takes on only an attacker its post test
    /// accepts (<see cref="Orders.HoldPost"/>), in its own defence and in an ally's (row 13 under 32 and under the protect goals), as when the decision
    /// step's sources ran them (<b>engine</b>, kept with the move of 32 and 15 to the chooser). Reads the last tick's positions.
    /// </summary>
    static bool PassesHoldPost(World world, int slot, CharacterCold cold, int foe, in CombatSlot state)
    {
        if (!Orders.Holding(cold)) return true;

        var prev = world.Characters.Previous;
        bool crawls = world.Characters.Cold(foe)?.Medical?.Crawling == true || state.AttackTech >= 0;
        var post = Orders.HoldPointOf(cold, prev[slot].Position);
        return Orders.HoldPost(prev[slot].Position, post, prev[foe].Position, prev[foe].Velocity, prev[foe].Yaw, crawls, true);
    }

    /// <summary>
    /// Asks the serial step for the fight of a combat goal or of Self preservation (<see cref="FightPick"/>; <see cref="SendFights"/>).
    /// Writes only its own mind.
    /// </summary>
    void AskFight(World world, int slot, TaskType type, AiMind mind)
    {
        mind.FightTarget = FightPick(world, slot, type, mind.Squad).Target;
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
    /// row 4 needs neither itself nor its target deep in the water (<see cref="CombatSystem.SwimBarred"/>); an animal's row 227 has no swim test but
    /// refuses an imprisoned target (78) and needs it within 500 (41). The accessibility and readiness states (27, 59, 60, 90, 37, 69) are the
    /// engine's own checks.
    /// </summary>
    static bool AttackRowBlocked(World world, int slot, CharacterId foe)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } me || !table.TryResolveNext(foe, out int f) || table.Cold(f) is not { } target)
            return false;

        if (me.Fighter?.IsAnimal != true) return CombatSystem.SwimBarred(me, target, focused: false);

        if (target.IsImprisoned) return true;

        var mine = Flat(table.Previous[slot].Position);
        var theirs = Flat(table.Previous[f].Position);
        return System.Numerics.Vector2.Distance(mine, theirs) >= Within50Meters;
    }

    /// <summary>
    /// The other fights' plans (13 Attacking attackers of under Self preservation and the protect goals, 41's) reach the melee row through state 7,
    /// whose providers 4, 5 and 61 all need the target out of deep water (<see cref="CombatSystem.SwimBarred"/>): no plan on such a foe.
    /// </summary>
    static bool SwimBarred(World world, int slot, CharacterId foe)
    {
        var table = world.Characters;
        return table.Cold(slot) is { } me && table.TryResolveNext(foe, out int f) && CombatSystem.SwimBarred(me, table.Cold(f), focused: false);
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
    static bool Protects(TaskType type) =>
        type is TaskType.ProtectOwnSquad or TaskType.ProtectAllies or TaskType.ProtectAlliesStayInTown or TaskType.Bodyguard;

    /// <summary>
    /// The protect goals' finders and their plan row 13 (docs/game/ai-tasks.md "Combat goals", <b>Verified</b> finders): 143 PROTECT_OWN_SQUAD
    /// (<c>FUN_14099d510</c>) values each squad mate that is the looker or that its last look sensed now (seen or heard, <c>FUN_140855960</c> with
    /// flag 1), 15 / 288 PROTECT_ALLIES (<c>FUN_14099ca10</c>, the same for both) each entry of its senses tagged ALLY (same faction or relation 50
    /// or more), not down, seen or heard now, a slave or escaped slave only where the looker may help one (<see cref="SlaveHelpAllowed"/>), by its
    /// fight threat (<c>FUN_14099a190</c>, <see cref="Combat.CombatSystem.FightThreat"/>), weighed by distance and Help Allies for a player-faction
    /// looker only (<see cref="ScanAllies"/>). The best (the first on a tie), capped at 1.2, is the factor (<see cref="ProtectPick"/>). 143 falls back
    /// to the looker's owned characters at x0.1 (<see cref="OwnedFallback"/>), 15 / 288 to theirs (<see cref="OwnedScan"/>). The first entry above
    /// 0 comes back too: 15 / 288's desire (<c>FUN_140599b90</c> asks the finder to stop at it, uncapped). Reads only the last tick's state (any
    /// thread in the think).
    /// </summary>
    internal (int Best, float Value, float First) ProtectScan(World world, int slot, TaskType type, Squad squad)
    {
        var table = world.Characters;
        var prev = table.Previous;
        if (_combat is not { } fights || _aggro is not { } look || look.SensesOf(slot) is not { } senses || table.Cold(slot) is not { } me)
            return (-1, 0, 0);

        int best = -1;
        float value = 0, first = 0;
        if (type == TaskType.ProtectOwnSquad)
        {
            foreach (var m in squad.Members)
            {
                if ((uint)m.Slot >= (uint)prev.Length || !prev[m.Slot].Alive || prev[m.Slot].Generation != m.Generation
                    || m.Slot != slot && !senses.Knows(m, now: true))
                    continue;

                float v = fights.FightThreat(world, m.Slot, look.SensesOf(m.Slot));
                if (v > value) (best, value) = (m.Slot, v);
            }

            if (best < 0) (best, value, first) = OwnedFallback(world, slot, squad, fights, look, senses);
        }
        else
        {
            (best, value, first) = ScanAllies(world, slot, me, fights, look, senses);
            if (best < 0) (best, value, first) = OwnedScan(world, slot, squad, fights, look, senses);
        }

        return (best, value, first);
    }

    /// <summary>
    /// The first loop of 15 / 288's finder (<c>FUN_14099ca10</c>, <b>Verified</b>): over the sensed entries tagged ALLY, not KO or imprisoned, seen
    /// or heard at the last look, alive, a slave or escaped slave only where the looker may help one (<see cref="SlaveHelpAllowed"/>, asked once,
    /// for the first), each worth its fight threat (<see cref="CombatSystem.FightThreat"/>), weighed for a player-faction looker
    /// (<see cref="PlayerFactionWeigh"/>); 0 or less skips it. The best (the first on a tie), and the first above 0, which the desire
    /// <c>FUN_140599b90</c> takes (the finder called to stop at it, uncapped).
    /// </summary>
    (int Best, float Value, float First) ScanAllies(World world, int slot, CharacterCold me, CombatSystem fights, AggroSystem look, SenseBlock senses)
    {
        var table = world.Characters;
        var prev = table.Previous;
        bool player = me.IsPlayer || me.Faction == PlayerFactionOf(world);
        int best = -1;
        float value = 0, first = 0;
        bool slaveChecked = false, slaveAllowed = false;
        foreach (var e in senses.Entries)
        {
            int a = e.Target;
            if (a == slot || !e.Now || (e.Tags & (SenseTag.Ally | SenseTag.Ko | SenseTag.Imprisoned)) != SenseTag.Ally || (uint)a >= (uint)prev.Length
                || !prev[a].Alive || prev[a].Generation != e.Generation || table.Cold(a) is not { } ally)
                continue;

            if (ally.SlaveState is 1 or 2)
            {
                if (!slaveChecked) (slaveChecked, slaveAllowed) = (true, SlaveHelpAllowed(world, look, me, prev[slot].Position));
                if (!slaveAllowed) continue;
            }

            float v = fights.FightThreat(world, a, look.SensesOf(a));
            if (player) v = PlayerFactionWeigh(world, slot, a, me, ally, v);
            if (v <= 0) continue;

            if (first <= 0) first = v;
            if (v > value) (best, value) = (a, v);
        }

        return (best, value, first);
    }

    /// <summary>
    /// A player-faction looker's weighing of an ally in 15 / 288's finder (<c>FUN_14099ca10</c>, its branch for a looker whose faction is the
    /// player's, <c>+0x250</c>; <b>Verified</b>): the threat x <c>clamp((1000 - d^2) / 990, 0.1, 1)</c> by the squared distance d^2 between the two
    /// (<c>FUN_1409b2380</c> with 10, 1000 and 0.1: 1 within 3.2 units, 0.1 from 30); an ally of another faction counts only with the player's Help
    /// Allies setting (<see cref="SquadAi.Help"/>, <c>DAT_142134690 + 0x111</c>), and then squared. An NPC looker skips the branch: no distance factor,
    /// and an ally of any faction counts at its threat.
    /// </summary>
    static float PlayerFactionWeigh(World world, int slot, int other, CharacterCold me, CharacterCold ally, float threat)
    {
        var prev = world.Characters.Previous;
        float distanceSquared = System.Numerics.Vector3.DistanceSquared(prev[slot].Position, prev[other].Position);
        float v = threat * Math.Clamp((distanceSquared - 1000f) / (10f - 1000f), 0.1f, 1f);
        if (ally.Faction == me.Faction) return v;

        return (world.Player.SquadAi & SquadAi.Help) != 0 ? v * v : 0;
    }

    /// <summary>
    /// The slave-defender gate of 15 / 288's finder (<c>FUN_140997e80</c>, <b>Verified</b>): whether the helper standing at <paramref name="at"/> may
    /// come to the defence of a slave or escaped slave: its faction is a bandit one, or it stands in no town, or that town's owner books no bounties
    /// (a lawful town's people leave slaves to their fate). True without towns or relations.
    /// </summary>
    bool SlaveHelpAllowed(World world, AggroSystem look, CharacterCold helper, System.Numerics.Vector3 at)
    {
        var relations = look.Relations;
        if (relations is null) return true;
        if (relations.Contains(helper.Faction) && relations.Factions[helper.Faction].FundamentalType == Meitou.Data.Gameplay.FundamentalType.Bandit)
            return true;

        var town = TownAt(data, world, at);
        if (town.Site < 0) return true;

        int owner = town.Site < data.SiteFactions.Count ? data.SiteFactions[town.Site] : -1;
        return !(relations.Contains(owner) && relations.Factions[owner].BooksBounties);
    }

    /// <summary>
    /// The second loop of 15 / 288's finder (<c>FUN_14099ca10</c>, <b>Verified</b>), run only when no sensed ally had a value above 0: the characters
    /// of the Ownerships list (<c>FUN_1407956a0(character) + 0x10</c>; for a squad of NPCs the slaves it owns,
    /// <see cref="CharacterCold.SlaveMaster"/>; what fills it is <b>Observed</b> from the medic finder's slave pass) that the looker senses now, that
    /// are not down and below slave state 2, valued at their fight threat x 0.1 (<c>DAT_14167fad0</c>), not those whose own target is an ally of the
    /// looker (<c>FUN_140998790</c>, relation 50). The best, the first above 0 for the desire's stop-at-first call.
    /// </summary>
    (int Best, float Value, float First) OwnedScan(World world, int slot, Squad squad, CombatSystem fights, AggroSystem look, SenseBlock senses)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int best = -1;
        float value = 0, first = 0;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == slot || !prev[s].Alive) continue;
            if (table.Cold(s) is not { SlaveState: < 2, SlaveMaster: >= 0 } owned || owned.SlaveMaster != squad.Id) continue;
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
    (int Best, float Value, float First) OwnedFallback(World world, int slot, Squad squad, CombatSystem fights, AggroSystem look, SenseBlock senses)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int best = -1;
        float value = 0, first = 0;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == slot || !prev[s].Alive) continue;
            if (table.Cold(s) is not { SlaveMaster: >= 0 } owned || owned.SlaveMaster != squad.Id) continue;
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
    /// that is not the looker's ally, best by the target score; none, one deep water bars (<see cref="SwimBarred(World, int, CharacterId)"/>), or
    /// one a holding looker's post test refuses (<see cref="PassesHoldPost"/>) fails the plan (factor 0). Without <paramref name="attacker"/> only
    /// the finder's value (row 13 not asked, the target none). Reads only the last tick's state (any thread in the think).
    /// </summary>
    (float Factor, CharacterId Target) ProtectPick(
        World world, int slot, TaskType type, Squad squad, bool attacker = true, IReadOnlyDictionary<int, int>? pending = null)
    {
        var (best, value, _) = ProtectScan(world, slot, type, squad);
        var prev = world.Characters.Previous;
        if (best < 0 || _combat is not { } fights) return (0, CharacterId.None);
        if (!attacker) return (MathF.Min(value, CombatSystem.ProtectCap), CharacterId.None);
        int foe = fights.BestAttackerOf(world, best, slot, pending);
        if (foe < 0) return (0, CharacterId.None);

        var id = new CharacterId(foe, prev[foe].Generation);
        bool barred = SwimBarred(world, slot, id) || world.Characters.Cold(slot) is not { } me
            || !PassesHoldPost(world, slot, me, foe, fights.StateOf(id));
        return barred ? (0, CharacterId.None) : (MathF.Min(value, CombatSystem.ProtectCap), id);
    }

    /// <summary>
    /// 45 Bodyguard's desire (AI slot 44 <c>FUN_1405991c0</c>, <b>Verified</b>): 1 when its target, the squad leader (targeting LEADER), is the
    /// character itself or one its last look sensed now (seen or heard), else 0.
    /// </summary>
    float BodyguardDesire(World world, int slot, Squad squad)
    {
        var leader = squad.Leader;
        if (leader.Slot == slot && world.Characters.IdOf(slot) == leader) return 1;

        return _aggro?.SensesOf(slot) is { } senses && senses.Knows(leader, now: true) ? 1 : 0;
    }

    /// <summary>
    /// 45 Bodyguard's plan (row 45, list B !16 !39, <b>Verified</b> from the registry): with neither its target, the squad leader, nor itself under
    /// a melee threat (state 16 on the leader, 39 on itself: <see cref="CharacterFacts.ImmediateThreat"/>) the goal neither acts nor has an effect
    /// and the scorer drops it (factor 0); else row 13 Attacking attackers of, the only provider of !16 and !39, on the goal's target: the
    /// leader's attacker (<c>AI::findAttackerOf</c>, <see cref="Combat.CombatSystem.BestAttackerOf"/> with the looker counted), none or one deep
    /// water bars failing the plan. Reads only the last tick's state (any thread in the think).
    /// </summary>
    (float Factor, CharacterId Target) BodyguardPick(World world, int slot, Squad squad, IReadOnlyDictionary<int, int>? pending = null)
    {
        var table = world.Characters;
        if (_combat is not { } fights || !table.TryResolveNext(squad.Leader, out int lead)) return (0, CharacterId.None);

        bool threatened = CharacterFacts.ImmediateThreat(fights, world, lead) || CharacterFacts.ImmediateThreat(fights, world, slot);
        if (!threatened) return (0, CharacterId.None);

        int foe = fights.BestAttackerOf(world, lead, slot, pending);
        if (foe < 0) return (0, CharacterId.None);

        var id = new CharacterId(foe, table.Previous[foe].Generation);
        return SwimBarred(world, slot, id) ? (0, CharacterId.None) : (1, id);
    }

    #endregion

    #region Starting and ending fights

    /// <summary>
    /// The targets of the fights SendFights started in its pass, with how many (derived, not hashed).
    /// </summary>
    readonly Dictionary<int, int> _pendingFights = [];

    /// <summary>
    /// The goal causes made so far (<see cref="GoalCause"/>), one string per row.
    /// </summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<TaskType, string> GoalCauses = new();

    /// <summary>
    /// The cause the event log gives an AI goal's fight (<see cref="AttackOrder.Cause"/>): its goal row.
    /// </summary>
    static string GoalCause(TaskType goal) => GoalCauses.GetOrAdd(goal, MakeGoalCause);

    /// <summary>
    /// The cause text of a goal row.
    /// </summary>
    static string MakeGoalCause(TaskType goal) =>
        goal == TaskType.SelfPreservation
            ? "self preservation: it was threatened"
            : $"its goal {TaskDataTable.Of(goal)?.Text ?? goal.ToString()} ({(int)goal})";

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
            if (_pendingFights.Count > 0 && FightPick(world, s, goal, mind.Squad, _pendingFights).Target is { IsNone: false } again)
                foe = again;

            if (!table.TryResolveNext(foe, out int f) || table.Cold(f) is not { Medical.Incapacitated: false }) continue;
            bool self = goal == TaskType.SelfPreservation, protects = self || Protects(goal);
            world.Defer(new AttackOrder([table.IdOf(s)], foe)
            {
                Tick = world.Tick + 1, Source = TaskSource.Goal, Defensive = protects, Cause = GoalCause(goal),
            });
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
    /// Ends the fights whose fighter's think chose a goal that is no fight (<see cref="InFight"/>), and the treatments and the tasks a
    /// player-faction character's goal waits on that a winner outscored (<see cref="LeaveTask"/>; serial, slot order): the task ends as any task's
    /// start ends it (<see cref="TaskStart.Become"/>, the character idle), and the character thinks at the next tick, which acts the chosen goal out.
    /// </summary>
    void LeaveFights(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { LeaveFight: true } mind } cold) continue;
            mind.LeaveFight = false;
            if (table.Next[s].Alive && Leaves(table.Next[s], cold, mind))
                TaskStart.Become(world, s, CharacterTask.Idle, TaskType.Idle, TaskSource.Goal);
            mind.Replan = true;
        }
    }

    /// <summary>
    /// Whether the task the think asked to leave is still there to end: a fight, its own Fixing Legs treatment, an NPC's treatment an engine system
    /// runs (<see cref="TakenOver"/>), or a player-faction character's task that is not the AI's, an NPC's job's task (an order given since stays).
    /// </summary>
    static bool Leaves(in CharacterHot n, CharacterCold cold, AiMind mind)
    {
        if (cold.InCombat || TreatsOwnLegs(n, cold)) return true;
        if (!mind.PlayerSide && TakenOver(n, cold) && cold.Running is not { Source: TaskSource.Order }) return true;

        bool foreign = mind.PlayerSide || cold.Running is { Source: TaskSource.Job };
        return foreign && cold.Running is not { Source: TaskSource.Order } && !PlayerSideHold.OwnsTask(n, cold, mind);
    }

    #endregion
}