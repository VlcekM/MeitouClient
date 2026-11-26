using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Crime;

/// <summary>
/// Goal 204 HUNT_BOUNTIES "Arrest bounties" as a goal of the one chooser (docs/simulation.md "Arrest and jail", "Engine goals"). Its effect 129 is
/// provided by 203 Arresting alone, whose list B (7 target down, 118 unarmed, 48 carrying it) makes the plan <c>[1, 4, 203, 204]</c> on a standing
/// target, <c>[67, 181, 203, 204]</c> on an armed downed one and <c>[67, 68, 203, 204]</c> on an unarmed one. A serial look keeps each member's target
/// and its free-cage and town answers for the think; a chosen goal's act-out is the plan's front work on the plan's target, done at that work's end:
/// the fight (the combat system's, which then holds the guard), the strip or the lift. Each ends the original's plan too, its next row 203 not acting,
/// and the chooser plans afresh; after the lift the target is no longer the finder's (carried) and the body is the carrier's own goals' (112 / 281
/// put it in a cage).
/// </summary>
internal sealed class ArrestGoals(ArrestSystem arrests)
{
    #region Picks

    /// <summary>
    /// What a member's look found: who looked, the finder's target (none: nothing) and where, whether the cage finder finds a free cage (204's
    /// desire) and whether 203 is wanted where it stands.
    /// </summary>
    readonly record struct Pick(CharacterId Id, CharacterId Target, Vector3 At, bool Cage, bool Arrests);

    /// <summary>
    /// The pick of each member that weighs 204, by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, Pick> _picks = [];

    /// <summary>
    /// The finder's look (serial, the arrest step), for each character on its slot's turn every <paramref name="every"/> ticks: a member with a mind
    /// of its own that weighs 204 (<see cref="AiSystem.WeighsGoal"/>) keeps its pick; its act-out's target while the goal holds it (engine: the
    /// original's finder runs at each score, so a pick is at most a look period old).
    /// </summary>
    internal void Look(World world, int every)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
            if (world.Turn(s, every)) LookAt(world, s);

        Prune(table);
    }

    /// <summary>
    /// The pick of the character at <paramref name="s"/>, or none.
    /// </summary>
    void LookAt(World world, int s)
    {
        var table = world.Characters;
        var cold = table.Next[s].Alive ? table.Cold(s) : null;
        if (cold is not { IsPlayer: false, Ai.Player: false } || !AiSystem.WeighsGoal(world, s, TaskType.HuntBounties))
        {
            _picks.Remove(s);
            return;
        }

        var id = table.IdOf(s);
        var target = ActOf(s, id) is { } act ? act.Target : Target(world, s, cold);
        var at = table.TryResolveNext(target, out int t) ? table.Next[t].Position : default;
        _picks[s] = new Pick(id, target, at, arrests.GuardCage(world, s, null) is not null, ArrestsHere(world, s, cold));
    }

    /// <summary>
    /// 204's target finder (<c>FUN_14099bfc0</c>, <b>Verified</b>): the first sensed character (the nearest the guard sees here: the witnesses' sight
    /// test, <see cref="ArrestSystem.SeesSuspect"/>) that is not carried, of no <c>not real</c> faction, no animal, not imprisoned and no slave (the
    /// mask leaves out slave state 1), and that carries a bounty the guard perceives (<see cref="ArrestSystem.PerceivedBounty"/>), or that the guard's
    /// squad marks with short-term tag 7 (<see cref="Dialogue.MemoryTag.Criminal"/>), or that the guard regards as an enemy. None: nothing.
    /// </summary>
    CharacterId Target(World world, int g, CharacterCold gc)
    {
        var table = world.Characters;
        float range = AggroSystem.RangeOf(gc, AggroSystem.OutdoorSight);
        var scan = new TargetScan { Goals = this, World = world, Self = g, Guard = gc, Range = range, Best = -1, BestD2 = float.MaxValue };
        var p = table.Next[g].Position;
        world.Grid.Query(p.X, p.Z, range, ref scan, static (int v, ref TargetScan st) => st.Goals.Consider(ref st, v));
        return scan.Best >= 0 ? table.IdOf(scan.Best) : CharacterId.None;
    }

    /// <summary>
    /// One character of the finder's grid query: kept when it passes the finder's tests and is nearer than the best so far and seen.
    /// </summary>
    void Consider(ref TargetScan st, int v)
    {
        var table = st.World.Characters;
        if (v == st.Self || !table.Next[v].Alive || table.Cold(v) is not { Medical.Dead: false } tc || !Findable(tc)) return;
        if (!WantedBy(st.World, st.Self, st.Guard, v, tc)) return;

        float d2 = Vector3.DistanceSquared(table.Next[st.Self].Position, table.Next[v].Position);
        if (d2 > st.BestD2 || d2 == st.BestD2 && v > st.Best) return;
        if (arrests.SeesSuspect(st.World, st.Self, v, tc, st.Range)) (st.Best, st.BestD2) = (v, d2);
    }

    /// <summary>
    /// The finder's tests of the character itself: alive (a dead entry carries no tag the include mask takes), not carried, not imprisoned, no
    /// slave (state 1), no animal, of a real faction. A kept pick is tested again with them, so a target that died since the look is not lifted.
    /// </summary>
    bool Findable(CharacterCold tc) =>
        tc.Medical is not { Dead: true } && tc.CarriedBy.IsNone && !tc.IsImprisoned && tc.SlaveState != 1 && !SquadRegistry.IsAnimal(tc)
        && !arrests.Crimes.Factions.NotReal(tc.Faction);

    /// <summary>
    /// Whether the guard goes after the target: a bounty it perceives, its squad's tag 7 on it, or it regards the target as an enemy (the full
    /// enemy test, squad memory first: <see cref="ArrestSystem.Enemy"/>).
    /// </summary>
    bool WantedBy(World world, int g, CharacterCold guard, int t, CharacterCold tc)
    {
        if (arrests.PerceivedBounty(world, g, guard, t, tc)) return true;
        if (arrests.Talk?.Memory.HasShort(world, guard.SquadId, world.Characters.IdOf(t), Dialogue.MemoryTag.Criminal) == true) return true;

        return arrests.Enemy(world, g, guard, t, tc);
    }

    /// <summary>
    /// The state of the finder's grid query: the nearest wanted character so far.
    /// </summary>
    struct TargetScan
    {
        /// <summary>
        /// The goals the tests go through.
        /// </summary>
        public ArrestGoals Goals;
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
        /// The guard's sight range.
        /// </summary>
        public float Range;
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
    /// 203's desire (AI slot 32, <c>FUN_140598a40</c>, <b>Verified</b>): 0 while the guard stands in no town (its status town unset) and has an
    /// ownership town (a squad that belongs to a town), else 1. Without town data the status town is never set: <b>engine</b> stand-in, the guard's
    /// own town's extent (<see cref="ArrestSystem.InOwnTown"/>).
    /// </summary>
    internal static bool ArrestsHere(World world, int s, CharacterCold cold)
    {
        if (world.Squads.Find(cold.SquadId) is not { TownOwn: true }) return true;
        if (world.SystemOf<ArrestSystem>() is not { } arrests) return !cold.StatusTown.IsNone;

        return arrests.Towns is not null ? !cold.StatusTown.IsNone : arrests.InOwnTown(world, s);
    }

    /// <summary>
    /// Drops the picks of slots that no longer hold the character that looked.
    /// </summary>
    void Prune(CharacterTable table)
    {
        List<int>? stale = null;
        foreach (var (slot, pick) in _picks)
            if (!table.TryResolveNext(pick.Id, out int at) || at != slot) (stale ??= []).Add(slot);

        if (stale is not null) foreach (int slot in stale) _picks.Remove(slot);
    }

    /// <summary>
    /// The pick of the asker; null for none or another character's.
    /// </summary>
    Pick? PickOf(in DutyAsk ask) => _picks.TryGetValue(ask.Slot, out var pick) && pick.Id == ask.Id ? pick : null;

    #endregion

    #region Act-outs

    /// <summary>
    /// The work of an act-out: the fight that downs the target (4 or 262, the combat system's), the strip (181), the lift (68).
    /// </summary>
    enum Work : byte
    {
        /// <summary>
        /// The fight, until the combat system takes the guard.
        /// </summary>
        Fight,
        /// <summary>
        /// Row 181 Stripping weapons.
        /// </summary>
        Strip,
        /// <summary>
        /// Row 68 Lifting.
        /// </summary>
        Lift,
    }

    /// <summary>
    /// A member 204 holds: who, the target and where the plan had it, the work and its game seconds (the strip's kneel, the wait for the fight) and
    /// the game seconds spent walking up to strip.
    /// </summary>
    sealed class ActOut(CharacterId id, CharacterId target, Vector3 at, Work work)
    {
        /// <summary>
        /// The guard.
        /// </summary>
        public CharacterId Id { get; } = id;
        /// <summary>
        /// The target.
        /// </summary>
        public CharacterId Target { get; } = target;
        /// <summary>
        /// Where the plan had the target.
        /// </summary>
        public Vector3 At { get; } = at;
        /// <summary>
        /// The work now.
        /// </summary>
        public Work Work { get; set; } = work;
        /// <summary>
        /// Game seconds in the work.
        /// </summary>
        public float Time { get; set; }
        /// <summary>
        /// Game seconds walking up to strip.
        /// </summary>
        public float Walked { get; set; }
        /// <summary>
        /// The work's walk ended short of the target or found no path to it (a lost walk).
        /// </summary>
        public bool Lost { get; set; }
    }

    /// <summary>
    /// The members 204 holds, by slot: added at the start, removed at the end or when done (hashed).
    /// </summary>
    readonly SortedDictionary<int, ActOut> _acting = [];

    /// <summary>
    /// The engine goals of the world (the AI's), resolved at the first step.
    /// </summary>
    DutyGoals? _duties;

    /// <summary>
    /// Scratch: the act-outs done in a step.
    /// </summary>
    readonly List<int> _done = [];

    /// <summary>
    /// Registers 204 with the rows of its plan the act-out carries out: 203 Arresting, the fights 4 and 262 that provide its 7, 181 and 68
    /// (<see cref="DutyGoals.Register"/>).
    /// </summary>
    internal void Register(DutyGoals goals) => goals.Register(TaskType.HuntBounties, new Arresting(this), TaskType.ArrestTarget, TaskType.MeleeAttack,
        TaskType.RangedAttack, TaskType.StripTargetsWeapons, TaskType.LiftPerson);

    /// <summary>
    /// The act-outs' step (serial, the arrest step): each member 204 still holds goes on with its work; one whose work is over is done
    /// (<see cref="DutyGoals.Done"/>), one whose walk to the target was lost is a lost walk on it (<see cref="DutyGoals.LostWalk"/>). One the goal
    /// no longer holds is left to the AI's sweep, which ends it.
    /// </summary>
    internal void Step(World world)
    {
        if (_acting.Count == 0) return;

        _duties ??= world.SystemOf<AiSystem>()?.Duties;
        _done.Clear();
        foreach (var (slot, act) in _acting)
        {
            if (_duties?.Runs(world, slot, TaskType.HuntBounties) == true && !StepAct(world, slot, act)) _done.Add(slot);
        }

        foreach (int slot in _done)
        {
            var act = _acting[slot];
            _acting.Remove(slot);
            if (world.Characters.Cold(slot) is { } cold) ArrestSystem.DropStripClip(cold);

            if (act.Lost) _duties!.LostWalk(world, slot, TaskType.HuntBounties, IgnoreKey.Of(act.Target));
            else _duties!.Done(world, slot, TaskType.HuntBounties);
        }
    }

    /// <summary>
    /// One tick of an act-out; false when its work is over: the fight did not start within a second, the strip failed or the lift ended.
    /// </summary>
    bool StepAct(World world, int g, ActOut act)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(act.Target, out int t)) return false;

        act.Time += world.TickSeconds;
        return act.Work switch
        {
            Work.Fight => table.Next[g].Task == (byte)CharacterTask.Attack || table.Cold(g)!.InCombat || act.Time < 1,
            Work.Strip => StepStrip(world, g, t, act),
            _ => Lifting(world, g, act),
        };
    }

    /// <summary>
    /// The strip (<see cref="ArrestSystem.Strip"/>); false when it is over: the weapons taken (181's list A holds, and the plan's next row 203 does
    /// not act, so the original's plan is over and the chooser plans afresh: <c>FUN_14050d540</c>), the target got up or was taken, the guard has no
    /// working arm, or the walk up ended short of the target (a lost walk). The walk has no time limit: the plan step ends it only as a lost walk
    /// (<c>FUN_14050d970</c>, <b>Verified</b>).
    /// </summary>
    bool StepStrip(World world, int g, int t, ActOut act)
    {
        if (act.Walked > 0 && world.Characters.Next[g].Task != (byte)CharacterTask.GoTo && !arrests.InStripReach(world, g, t))
        {
            act.Lost = true;
            return false;
        }

        switch (arrests.Strip(world, g, t, act.Time, TaskSource.Goal))
        {
            case StripStep.Walk:
                act.Time = 0;
                act.Walked += world.TickSeconds;
                return true;
            case StripStep.Kneel:
                act.Time = 0;
                return true;
            case StripStep.Wait:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The lift runs (one whose walk no path reaches the target is a lost walk, <see cref="Carrying.CarrySystem.LiftWalkFails"/>); once it ended, a
    /// body on the guard's shoulder is logged as lifted.
    /// </summary>
    bool Lifting(World world, int g, ActOut act)
    {
        var table = world.Characters;
        if (table.Next[g].Task == (byte)CharacterTask.PickUp)
        {
            act.Lost = arrests.Carry?.LiftWalkFails(world, g) == true;
            return !act.Lost;
        }

        if (table.Cold(g)!.Carrying == act.Target) arrests.Note(world, ArrestEventKind.Lifted, act.Id, act.Target, table.Cold(g)!.Faction, 0);
        return false;
    }

    /// <summary>
    /// The act-out's start (serial): the front work of the plan on the plan's target; false when the plan has no such work or target, another
    /// member's act-out or an ordered arrest works the target (the finders' <c>FUN_1402690e0</c>), or the work cannot start.
    /// </summary>
    bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan)
    {
        if (TargetOf(plan) is not { Character: { } target } found || FrontWork(plan) is not { } work || Worked(ask.Slot, target)) return false;
        if (!world.Characters.TryResolveNext(target, out int t)) return false;

        var act = new ActOut(ask.Id, target, found.Position, work);
        bool started = work switch
        {
            Work.Fight => BeginFight(world, ask.Slot, act, FightRow(plan)),
            Work.Strip => BeginStrip(world, ask.Slot, t),
            _ => BeginLift(world, ask.Slot, t, act),
        };
        if (!started) return false;

        _acting[ask.Slot] = act;
        return true;
    }

    /// <summary>
    /// The finder's target in the plan: the target of its first step on a character.
    /// </summary>
    static PlanTarget? TargetOf(IReadOnlyList<PlanStep> plan)
    {
        foreach (var step in plan)
            if (step.Target.Kind == PlanTarget.Found && step.Target.Character is not null) return step.Target;
        return null;
    }

    /// <summary>
    /// The work of the plan's first step that is one (the walks before it are the work's own); null for none.
    /// </summary>
    static Work? FrontWork(IReadOnlyList<PlanStep> plan)
    {
        foreach (var step in plan)
        {
            switch (step.Type)
            {
                case TaskType.MeleeAttack or TaskType.RangedAttack: return Work.Fight;
                case TaskType.StripTargetsWeapons: return Work.Strip;
                case TaskType.LiftPerson: return Work.Lift;
            }
        }

        return null;
    }

    /// <summary>
    /// The plan's fight row (4 or 262).
    /// </summary>
    static TaskType FightRow(IReadOnlyList<PlanStep> plan)
    {
        foreach (var step in plan)
            if (step.Type == TaskType.RangedAttack) return TaskType.RangedAttack;
        return TaskType.MeleeAttack;
    }

    /// <summary>
    /// Whether another member's act-out or an ordered arrest works <paramref name="target"/>.
    /// </summary>
    bool Worked(int slot, CharacterId target)
    {
        foreach (var (other, act) in _acting)
            if (other != slot && act.Target == target) return true;

        return arrests.Busy(target);
    }

    /// <summary>
    /// The fight: the guard attacks the target from the next tick on the plan's row with the goal's provenance; the combat system closes in, and its
    /// fight takes the guard from the goal (<see cref="DutyEnd.Taken"/>).
    /// </summary>
    bool BeginFight(World world, int g, ActOut act, TaskType row)
    {
        var table = world.Characters;
        var guard = table.IdOf(g);
        world.Defer(new AttackOrder([guard], act.Target) { Tick = world.Tick + 1, Source = TaskSource.Goal, Row = row, Cause = "an arrest" });
        int bounty = table.TryResolveNext(act.Target, out int t)
            ? table.Cold(t)!.Bounties?.BookedAmount(arrests.Crimes.Factions.Relations, table.Cold(g)!.Faction) ?? 0 : 0;
        arrests.Note(world, ArrestEventKind.Arrest, guard, act.Target, table.Cold(g)!.Faction, bounty);
        return true;
    }

    /// <summary>
    /// The strip's start: the walk up to the target when it is out of reach (the step kneels in reach).
    /// </summary>
    bool BeginStrip(World world, int g, int t)
    {
        var table = world.Characters;
        var to = table.Next[t].Position;
        if (arrests.InStripReach(world, g, t)) return true;

        TaskStart.Walk(world, g, new Vector2(to.X, to.Z), SpeedMode.Free, TaskType.StripTargetsWeapons, TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// The lift (row 68) of the target, back to standing; false when the guard's left arm cannot carry, the target is no longer down or someone
    /// carries it, or the lift cannot start.
    /// </summary>
    bool BeginLift(World world, int g, int t, ActOut act)
    {
        var table = world.Characters;
        if (arrests.Carry is not { } carry || table.Cold(g) is not { } gc || !ArrestSystem.CanLift(gc)) return false;
        if (table.Cold(t) is not { CarriedBy.IsNone: true } tc || !CharacterFacts.TargetDown(tc)) return false;
        return carry.BeginPickUp(world, g, act.Target, CharacterTask.Idle, source: TaskSource.Goal, row: TaskType.LiftPerson);
    }

    /// <summary>
    /// The goal ended (displaced, an order, a replan, taken, gone): the member is no longer held; a body already on its shoulder stays there, the
    /// strip's kneel is taken off.
    /// </summary>
    void End(World world, int slot, CharacterId id)
    {
        if (!_acting.TryGetValue(slot, out var act) || act.Id != id) return;

        _acting.Remove(slot);
        if (world.Characters.IdOf(slot) == id && world.Characters.Cold(slot) is { } cold) ArrestSystem.DropStripClip(cold);
    }

    /// <summary>
    /// The act-out of the asker; null for none or another character's.
    /// </summary>
    ActOut? ActOf(int slot, CharacterId id) => _acting.TryGetValue(slot, out var act) && act.Id == id ? act : null;

    /// <summary>
    /// The finder for the asker: its act-out's target while 204 holds it, else its pick while the target still passes the finder's own tests (the
    /// original's finder runs at each score); factor 1.0 with one (<c>DAT_14167c308</c>), else 0.
    /// </summary>
    float Find(World world, in DutyAsk ask, out PlanTarget target)
    {
        var table = world.Characters;
        if (ActOf(ask.Slot, ask.Id) is { } act) target = PlanTarget.OfCharacter(act.Target, act.At);
        else if (PickOf(ask) is { Target.IsNone: false } pick && table.IsAlive(pick.Target) && Findable(table.Cold(pick.Target.Slot)!))
            target = PlanTarget.OfCharacter(pick.Target, pick.At);
        else target = default;

        return target.IsNone ? 0 : 1;
    }

    #endregion

    #region Desires and states

    /// <summary>
    /// The desires of 204 and its rows for the asker (parallel: its own cold state, its pick, the target's cold state).
    /// </summary>
    float Desire(World world, in DutyAsk ask)
    {
        if (world.Characters.Cold(ask.Slot) is not { } cold) return 0;

        return ask.Row switch
        {
            TaskType.ArrestTarget => PickOf(ask) is { Arrests: true } ? 1 : 0,
            TaskType.MeleeAttack => MeleeDesire(world, ask, cold),
            TaskType.RangedAttack => ShootDesire(world, ask, cold),
            TaskType.StripTargetsWeapons => 1,
            TaskType.LiftPerson => LiftDesire(world, ask, cold),
            _ => PickOf(ask) is { Cage: true } ? 1 : 0,
        };
    }

    /// <summary>
    /// Row 4's desire (AI slot 53, <c>FUN_1405a4260</c>, docs/game/combat.md "Melee or shooting", <b>Verified</b>): 0 against a caged target, else
    /// the body's (<see cref="SelfPreservation.AttackDesire"/>), x0.1 with Ranged on (always for an NPC, <see cref="Orders.RangedMode"/>), x0.5 while
    /// the squad is not intact.
    /// </summary>
    static float MeleeDesire(World world, in DutyAsk ask, CharacterCold cold)
    {
        if (Caged(world, ask.Target)) return 0;

        bool immediate = world.SystemOf<CombatSystem>() is { } combat && CharacterFacts.ImmediateThreat(combat, world, ask.Slot);
        float desire = SelfPreservation.AttackDesire(cold, immediate);
        if (Orders.RangedMode(cold)) desire *= 0.1f;
        if (ask.Squad.Ai is { Intact: false }) desire *= 0.5f;

        return desire;
    }

    /// <summary>
    /// Row 262's desire (AI slot 54, <c>FUN_140598260</c>, <b>Verified</b>): 0 with Ranged off, no working arm, crippled or no crossbow in a weapon
    /// section; 0.1 while meleed now (<c>+0x2b0</c>); 0 against a caged target or without its bolts; else 1, 0.5 while the squad is not intact.
    /// </summary>
    static float ShootDesire(World world, in DutyAsk ask, CharacterCold cold)
    {
        if (!Orders.RangedMode(cold) || cold.Medical is not { Crawling: false } medical) return 0;
        if (!medical.ArmWorks(left: true) && !medical.ArmWorks(left: false)) return 0;
        if (cold.Fighter?.Crossbow is not { } bow) return 0;
        if (world.SystemOf<CombatSystem>() is { } combat && CharacterFacts.ImmediateThreat(combat, world, ask.Slot)) return 0.1f;
        if (Caged(world, ask.Target) || !bow.InfiniteAmmo && Ammunition.FirstCarried(cold.Inventory, bow.Data) < 0) return 0;

        return ask.Squad.Ai is { Intact: false } ? 0.5f : 1;
    }

    /// <summary>
    /// Whether the target is a character in a cage.
    /// </summary>
    static bool Caged(World world, in PlanTarget target) =>
        target.Character is { } who && world.Characters.IsAlive(who) && world.Characters.Cold(who.Slot) is { IsImprisoned: true };

    /// <summary>
    /// 68 Lifting's desire, AI slot 59 <c>FUN_14059e1a0</c> (<b>Verified</b>): 0 while the guard carries someone; 0.5 for a target that is no
    /// character; 0 for one it carries already; else 1.
    /// </summary>
    static float LiftDesire(World world, in DutyAsk ask, CharacterCold cold)
    {
        if (!cold.Carrying.IsNone) return 0;
        if (ask.Target.Character is not { } body || !world.Characters.IsAlive(body)) return 0.5f;

        return world.Characters.Cold(body.Slot)!.CarriedBy == ask.Id ? 0 : 1;
    }

    /// <summary>
    /// The states of the rows for the target (parallel: <c>Previous</c> places, cold state only serial steps write): 1 / 46 within the strip's and
    /// lift's reach, 7 down (<see cref="CharacterFacts.TargetDown"/>), 48 carried by the guard, 118 unarmed
    /// (<see cref="CharacterFacts.TargetUnarmed"/>), 129 arrested (<c>FUN_14059a380</c>, <b>Verified</b>: caged, or carried by a character of another
    /// faction than its own), 140 swimming (water state 3); null for the rest.
    /// </summary>
    bool? State(World world, in DutyAsk ask, StateType state)
    {
        if (ask.Target.Kind != PlanTarget.Found || ask.Target.Character is not { } who) return null;

        var table = world.Characters;
        var c = table.IsAlive(who) ? table.Cold(who.Slot) : null;
        return state switch
        {
            StateType.IsAtLocation or StateType.IsAtLocation46 => c is not null && arrests.InStripReach(world, ask.Slot, who.Slot, previous: true),
            StateType.IsTargetDown => CharacterFacts.TargetDown(c),
            StateType.IsCarryingTarget => table.Cold(ask.Slot)?.Carrying == who,
            StateType.TargetIsUnarmed => c is not null && CharacterFacts.TargetUnarmed(c),
            StateType.TargetIsArrested => c is not null && Arrested(world, c),
            StateType.TargetSwimming => c is { WaterState: 3 },
            _ => null,
        };
    }

    /// <summary>
    /// State 129 TARGET_IS_ARRESTED of a character target: caged, or carried by a character of another faction than its own.
    /// </summary>
    static bool Arrested(World world, CharacterCold c)
    {
        if (c.IsImprisoned) return true;

        var table = world.Characters;
        return table.IsAlive(c.CarriedBy) && table.Cold(c.CarriedBy.Slot) is { } carrier && carrier.Faction != c.Faction;
    }

    #endregion

    #region The goal

    /// <summary>
    /// Goal 204 HUNT_BOUNTIES as the chooser weighs it (tier and weight from the package's AI_TASK record; no commitment).
    /// </summary>
    sealed class Arresting(ArrestGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 204's desire (AI slot 33 <c>FUN_140598a90</c>: the free-cage finder <c>FUN_1408d0d30</c>'s 1.0 or 0) and its rows'
        /// (<see cref="ArrestGoals.Desire"/>).
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => goals.Desire(world, ask);

        /// <summary>
        /// 204's finder (<see cref="ArrestGoals.Find"/>).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.Find(world, ask, out target);

        /// <summary>
        /// The target's states (<see cref="ArrestGoals.State"/>).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) => goals.State(world, ask, state);

        /// <summary>
        /// Starts the plan's front work.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Start(world, ask, plan);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.End(world, slot, id);
    }

    #endregion

    /// <summary>
    /// Adds the picks and the act-outs to the state hash, only while there are any.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        if (_picks.Count == 0 && _acting.Count == 0) return;

        hasher.Add(0x41476F6C);   // "AGol"
        hasher.Add(_picks.Count);
        foreach (var (slot, pick) in _picks)
        {
            hasher.Add(slot);
            hasher.Add(pick.Id);
            hasher.Add(pick.Target);
            hasher.Add(pick.At);
            hasher.Add(pick.Cage ? 1 : 0);
            hasher.Add(pick.Arrests ? 1 : 0);
        }

        HashActing(ref hasher);
    }

    /// <summary>
    /// Adds the act-outs to the state hash.
    /// </summary>
    void HashActing(ref StateHasher hasher)
    {
        hasher.Add(_acting.Count);
        foreach (var (slot, act) in _acting)
        {
            hasher.Add(slot);
            hasher.Add(act.Id);
            hasher.Add(act.Target);
            hasher.Add(act.At);
            hasher.Add((int)act.Work);
            hasher.Add(act.Time);
            hasher.Add(act.Walked);
        }
    }
}
