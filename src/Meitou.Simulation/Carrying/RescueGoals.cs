using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Carrying;

/// <summary>
/// The rescue goals of the one chooser, for any character whose lists weigh them (docs/simulation.md "Carrying, rescue and looting"): 148 Find and
/// rescue if there's beds and 142 Find and rescue leader. Both are <c>Task_Blank</c> rows with effect 48 (carrying the target), so the plan is
/// <c>[1, 68, goal]</c>: 68 Lifting is 48's only provider (docs/game/ai-tasks.md "Arrest chain"). The finders' picks are kept in a serial look for
/// the think to read; a goal the chooser starts is acted out by the lift of the plan's body as the goal's task, and is done when the lift ends.
/// </summary>
internal sealed class RescueGoals
{
    #region Picks

    /// <summary>
    /// What a character's finder found at its last look: who looked, the body and where it lay.
    /// </summary>
    readonly record struct Pick(CharacterId Id, CharacterId Body, Vector3 At)
    {
        /// <summary>
        /// The pick as a plan target.
        /// </summary>
        public PlanTarget Target => PlanTarget.OfCharacter(Body, At);
    }

    /// <summary>
    /// The body each character that weighs 148 found at its last look (148's finder <c>FUN_1408cd550</c>, with its desire's bed search), by slot
    /// (hashed).
    /// </summary>
    readonly SortedDictionary<int, Pick> _bodies = [];

    /// <summary>
    /// The squad leader each character that weighs 142 found down at its last look (142's finder <c>FUN_140598540</c>), by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, Pick> _leaders = [];

    /// <summary>
    /// The finders' look (serial, the carry step's rescue beat), for every character with a mind: one whose lists weigh a rescue goal
    /// (<see cref="AiSystem.WeighsGoal"/>) keeps what its finder finds; the think's desire, finder and states read the picks (engine: the
    /// original's finders run at each score, so a pick is at most a beat old). Goal 148's note for the action log is written here.
    /// </summary>
    internal void Look(World world, CarrySystem carry)
    {
        var table = world.Characters;
        var taken = CarrySystem.Lifting(table);
        bool? down = null;
        for (int s = 0; s < table.HighWater; s++)
        {
            var cold = table.Next[s].Alive ? table.Cold(s) : null;
            if (cold?.Ai is not { } mind)
            {
                _bodies.Remove(s);
                _leaders.Remove(s);
                continue;
            }

            LookForLeader(world, s, cold);
            mind.BedRescueNote = LookForBody(world, carry, s, cold, taken, ref down);
        }

        Prune(table, _bodies);
        Prune(table, _leaders);
    }

    /// <summary>
    /// 142's pick of the character at <paramref name="slot"/>: its squad leader while <see cref="CarrySystem.LeaderToRescue"/> finds it down and the
    /// character is able to lift (<see cref="CarrySystem.CouldLift"/>, engine choice: one with no usable arm does not choose a goal it cannot act
    /// out), else none.
    /// </summary>
    void LookForLeader(World world, int slot, CharacterCold cold)
    {
        var table = world.Characters;
        int leader = AiSystem.WeighsGoal(world, slot, TaskType.FindAndRescueLeader) ? CarrySystem.LeaderToRescue(world, slot) : -1;
        if (leader < 0 || !CarrySystem.CouldLift(world, cold))
        {
            _leaders.Remove(slot);
            return;
        }

        _leaders[slot] = new Pick(table.IdOf(slot), table.IdOf(leader), table.Next[leader].Position);
    }

    /// <summary>
    /// 148's pick of the character at <paramref name="slot"/> (<see cref="CarrySystem.RescueTarget"/>: the desire <c>FUN_1405994c0</c> and the
    /// finder), or none; returns where the goal stands for the action log. One the goal holds keeps its act-out's body.
    /// </summary>
    BedRescueNote LookForBody(World world, CarrySystem carry, int slot, CharacterCold cold, HashSet<int> taken, ref bool? down)
    {
        var table = world.Characters;
        if (!AiSystem.WeighsGoal(world, slot, TaskType.FindAndRescueIfTheresBeds))
        {
            _bodies.Remove(slot);
            return BedRescueNote.None;
        }

        if (Acts(slot, table.IdOf(slot), TaskType.FindAndRescueIfTheresBeds)) return BedRescueNote.Lifting;

        int body = carry.RescueTarget(world, slot, cold, taken, ref down, out var note);
        if (body < 0)
        {
            _bodies.Remove(slot);
            return note;
        }

        _bodies[slot] = new Pick(table.IdOf(slot), table.IdOf(body), table.Next[body].Position);
        return BedRescueNote.NotChosen;
    }

    /// <summary>
    /// Drops the picks of slots that no longer hold the character that looked.
    /// </summary>
    static void Prune(CharacterTable table, SortedDictionary<int, Pick> picks)
    {
        List<int>? stale = null;
        foreach (var (slot, pick) in picks)
        {
            if (!table.TryResolveNext(pick.Id, out int at) || at != slot) (stale ??= []).Add(slot);
        }

        if (stale is not null) foreach (int slot in stale) picks.Remove(slot);
    }

    #endregion

    #region Act-outs

    /// <summary>
    /// A character a rescue goal holds: who, the goal, the body it lifts and where it lay.
    /// </summary>
    readonly record struct ActOut(CharacterId Id, TaskType Goal, CharacterId Body, Vector3 At)
    {
        /// <summary>
        /// The act-out's target as the plan holds it.
        /// </summary>
        public PlanTarget Target => PlanTarget.OfCharacter(Body, At);
    }

    /// <summary>
    /// The characters 148 or 142 holds, by slot: added at the start, removed at the end or when done (hashed).
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
    /// Registers 148 and 142 with their plan row 68 (<see cref="DutyGoals.Register"/>; row 68 resolves to the goal whose plan holds it).
    /// </summary>
    internal void Register(DutyGoals goals)
    {
        goals.Register(TaskType.FindAndRescueIfTheresBeds, new Rescuing(this, _bodies), TaskType.LiftPerson);
        goals.Register(TaskType.FindAndRescueLeader, new Rescuing(this, _leaders), TaskType.LiftPerson);
    }

    /// <summary>
    /// The act-outs' step (serial, the carry step): each character a goal still holds whose lift has ended (the body on its shoulder, the lift
    /// given up) or whose body has died (a corpse is nobody to rescue) is done (<see cref="DutyGoals.Done"/>, which stops a walk still under way);
    /// one whose walk no path gets there ends as a lost walk (<see cref="DutyGoals.LostWalk"/>: as the original's plan step answers an idle mover
    /// away from its target, <c>FUN_14050d970</c>, <b>Verified</b>, the body is ignored for 20 s and the goal replans; the rescue finders read no
    /// ignore map, so the same body is taken again). One the goal no longer holds is left to the AI's sweep, which ends it.
    /// </summary>
    internal void Step(World world, CarrySystem carry)
    {
        if (_acting.Count == 0) return;

        _duties ??= world.SystemOf<AiSystem>()?.Duties;
        _done.Clear();
        foreach (var (slot, act) in _acting)
        {
            if (_duties?.Runs(world, slot, act.Goal) == true && (Over(world, slot, act) || carry.LiftWalkFails(world, slot))) _done.Add(slot);
        }

        foreach (int slot in _done)
        {
            var act = _acting[slot];
            bool lost = !Over(world, slot, act);
            _acting.Remove(slot);

            if (lost)
            {
                _duties!.LostWalk(world, slot, act.Goal, DutyGoals.KeyOf(act.Target));
                continue;
            }

            // Done: its picks go until the next beat looks again, so the think does not weigh the lifted body.
            _bodies.Remove(slot);
            _leaders.Remove(slot);
            _duties!.Done(world, slot, act.Goal);
        }
    }

    /// <summary>
    /// Whether the act-out has ended: the lift of its body is no longer the character's task, or the body has died.
    /// </summary>
    static bool Over(World world, int slot, in ActOut act)
    {
        var table = world.Characters;
        if (table.Next[slot].Task != (byte)CharacterTask.PickUp || table.Cold(slot)!.CarryTarget != act.Body) return true;

        return !table.TryResolveNext(act.Body, out int b) || table.Cold(b) is not { Medical.Dead: false };
    }

    /// <summary>
    /// Whether the goal <paramref name="goal"/> holds the character <paramref name="id"/> at <paramref name="slot"/>.
    /// </summary>
    bool Acts(int slot, CharacterId id, TaskType goal) => _acting.TryGetValue(slot, out var act) && act.Id == id && act.Goal == goal;

    /// <summary>
    /// The act-out's start (serial): the lift of the plan's body (<see cref="CarrySystem.BeginPickUp"/>, the goal's row, the goal's provenance),
    /// back to standing afterwards; false when the body is gone, dead or no longer one the lift takes, another lift already goes for it, or the lift
    /// does not start.
    /// </summary>
    bool Start(World world, CarrySystem carry, in DutyAsk ask, IReadOnlyList<PlanStep> plan)
    {
        var table = world.Characters;
        if (PlanTargetOf(plan) is not { Character: { } body } target || !table.TryResolveNext(body, out int b)) return false;
        if (table.Cold(b) is not { Medical.Dead: false } victim || !CarrySystem.CanBeLifted(victim) || CarrySystem.Lifting(table).Contains(b))
            return false;
        if (!carry.BeginPickUp(world, ask.Slot, body, CharacterTask.Idle, source: TaskSource.Goal, row: ask.Type)) return false;

        _acting[ask.Slot] = new ActOut(ask.Id, ask.Type, body, target.Position);
        return true;
    }

    /// <summary>
    /// The plan's first found target (the body, carried down from the goal's finder).
    /// </summary>
    static PlanTarget? PlanTargetOf(IReadOnlyList<PlanStep> plan)
    {
        foreach (var step in plan)
            if (step.Target.Kind == PlanTarget.Found) return step.Target;
        return null;
    }

    /// <summary>
    /// The goal ended (displaced, an order, a replan, gone): the character is no longer held. A lift under way goes on as the displacing task
    /// allows, as every goal's does.
    /// </summary>
    void End(int slot, CharacterId id)
    {
        if (_acting.TryGetValue(slot, out var act) && act.Id == id) _acting.Remove(slot);
    }

    /// <summary>
    /// The target the goal's finder gives the asker: its act-out's while the goal holds it (the original keeps the current goal's finder hand),
    /// else its pick; factor 1.0 with one, else 0.
    /// </summary>
    float Find(SortedDictionary<int, Pick> picks, in DutyAsk ask, out PlanTarget target)
    {
        if (Acts(ask.Slot, ask.Id, ask.Type)) target = _acting[ask.Slot].Target;
        else if (picks.TryGetValue(ask.Slot, out var pick) && pick.Id == ask.Id) target = pick.Target;
        else target = default;

        return target.IsNone ? 0 : 1;
    }

    /// <summary>
    /// The states of row 68 for the body, read live (parallel: the last tick's places, cold state only serial steps write): 1 and 46 at it (the
    /// lift's reach), 7 down, 48 carried by the asker; null for the rest (80, 27, 26 and 142's 101 are the AI's own).
    /// </summary>
    static bool? State(World world, CarrySystem carry, in DutyAsk ask, StateType state)
    {
        if (ask.Target is not { Kind: PlanTarget.Found, Character: { } body }) return null;

        var table = world.Characters;
        var victim = table.IsAlive(body) ? table.Cold(body.Slot) : null;
        return state switch
        {
            StateType.IsAtLocation or StateType.IsAtLocation46 => victim is not null && carry.AtBody(world, ask.Slot, body.Slot),
            StateType.IsTargetDown => CharacterFacts.TargetDown(victim),
            StateType.IsCarryingTarget => table.Cold(ask.Slot)?.Carrying == body,
            _ => null,
        };
    }

    #endregion

    #region Goals

    /// <summary>
    /// Goal 148 or 142 as the chooser weighs it (tier and weight from the package's AI_TASK record): plan <c>[1, 68, goal]</c>.
    /// </summary>
    sealed class Rescuing(RescueGoals goals, SortedDictionary<int, Pick> picks) : IDutyGoal
    {
        /// <summary>
        /// Row 68's desire (AI slot 59, <b>Verified</b>): 0 while the asker carries something, else 1.0 (a character target). 148's
        /// (<c>FUN_1405994c0</c>, <b>Verified</b>): 0 on Hold or while carrying, else the bed search's factor, which the look folds into the pick.
        /// 142's (<c>FUN_1405993f0</c>, <b>Verified</b>): 0 while carrying, else 1.0 for a squad leader that is not the asker, not in a bed and down
        /// or crippled; the finder (<see cref="CarrySystem.LeaderToRescue"/>) refuses every such case already, so 1.0 here.
        /// </summary>
        public float Desire(World world, in DutyAsk ask)
        {
            if (world.Characters.Cold(ask.Slot) is not { } cold) return 0;
            if (ask.Row == TaskType.LiftPerson) return cold.Carrying.IsNone ? 1 : 0;
            if (ask.Type == TaskType.FindAndRescueIfTheresBeds) return CarrySystem.Rescuing(cold) ? 1 : 0;

            return 1;
        }

        /// <summary>
        /// The goal's finder (148 <c>FUN_1408cd550</c>, 142 <c>FUN_140598540</c>): the body, factor 1.0, else 0.
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.Find(picks, ask, out target);

        /// <summary>
        /// Row 68's states for the body (<see cref="RescueGoals.State"/>).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) =>
            world.SystemOf<CarrySystem>() is { } carry ? RescueGoals.State(world, carry, ask, state) : null;

        /// <summary>
        /// Lifts the plan's body.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) =>
            world.SystemOf<CarrySystem>() is { } carry && goals.Start(world, carry, ask, plan);

        /// <summary>
        /// The character is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.End(slot, id);
    }

    #endregion

    /// <summary>
    /// Adds the picks and the act-outs to the state hash, only while there are any.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        if (_bodies.Count == 0 && _leaders.Count == 0 && _acting.Count == 0) return;

        hasher.Add(0x52657363);   // "Resc"
        HashPicks(ref hasher, _bodies);
        HashPicks(ref hasher, _leaders);
        foreach (var (slot, act) in _acting)
        {
            hasher.Add(slot);
            hasher.Add(act.Id);
            hasher.Add((int)act.Goal);
            hasher.Add(act.Body);
            hasher.Add(act.At);
        }
    }

    /// <summary>
    /// Adds one pick list to the state hash.
    /// </summary>
    static void HashPicks(ref StateHasher hasher, SortedDictionary<int, Pick> picks)
    {
        hasher.Add(picks.Count);
        foreach (var (slot, pick) in picks)
        {
            hasher.Add(slot);
            hasher.Add(pick.Id);
            hasher.Add(pick.Body);
            hasher.Add(pick.At);
        }
    }
}
