using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Locks;

/// <summary>
/// Goal 180 Man the gate as a goal of the one chooser (docs/simulation.md "Arrest and jail", "Engine goals"): desire 1.0 (AI slot 19,
/// <b>Verified</b>), no finder, its target the squad's HOME_GATE. Its list B 52 DOOR_IS_OPEN on that gate is met by 72 Opening door, after 78
/// Unlocking door when it is locked (78 needs 70 isObjectMine: the gate's faction is the guard's), so the act-out first unlocks and opens a shut
/// gate (a <see cref="Round"/> of the gate's doors), then walks to the gate guard's place (<c>Task_ManTheGate</c>: 15 out from the leaf,
/// <see cref="AiSystem.FrontOf"/>; its post without a gate) and stands there. 180 has no list A, so the goal holds its guard until another goal wins;
/// a gate shut again meanwhile is opened again.
/// </summary>
internal sealed class GateGoals(LockSystem locks)
{
    #region Act-outs

    /// <summary>
    /// A gate guard 180 holds: who, the round opening its gate (null while it walks to its place or stands there) and whether it walked there.
    /// </summary>
    sealed class ActOut(CharacterId id)
    {
        /// <summary>
        /// The guard.
        /// </summary>
        public CharacterId Id { get; } = id;
        /// <summary>
        /// The unlock round opening the gate; null for none.
        /// </summary>
        public Round? Round { get; set; }
        /// <summary>
        /// A walk to its place was started and it has not stood there since.
        /// </summary>
        public bool Walked { get; set; }
    }

    /// <summary>
    /// The lock system whose rounds open the gates.
    /// </summary>
    readonly LockSystem _locks = locks;

    /// <summary>
    /// The guards 180 holds, by slot: added at the start, removed at the end (hashed).
    /// </summary>
    readonly SortedDictionary<int, ActOut> _acting = [];

    /// <summary>
    /// Scratch: the guards whose walk was lost in a step.
    /// </summary>
    readonly List<int> _lost = [];

    /// <summary>
    /// The engine goals of the world (the AI's), resolved at the first step.
    /// </summary>
    DutyGoals? _duties;

    /// <summary>
    /// The rounds the gate guards work, in slot order.
    /// </summary>
    internal IEnumerable<Round> Rounds => _acting.Values.Where(a => a.Round is not null).Select(a => a.Round!);

    /// <summary>
    /// Registers 180 (<see cref="DutyGoals.Register"/>): its plan is its own row, the door rows its act-out folds in are not planned (the planner
    /// holds 52 true).
    /// </summary>
    internal void Register(DutyGoals goals) => goals.Register(TaskType.ManTheGate, new Manning(this));

    /// <summary>
    /// The act-outs' step (serial, the lock step): each guard 180 still holds works its round, or opens its gate when it is shut, or walks back to its
    /// place. One the goal no longer holds is left to the AI's sweep, which ends it.
    /// </summary>
    internal void Step(World world)
    {
        if (_acting.Count == 0) return;

        _duties ??= world.SystemOf<AiSystem>()?.Duties;
        _lost.Clear();
        foreach (var (slot, act) in _acting)
            if (_duties?.Runs(world, slot, TaskType.ManTheGate) == true && StepGuard(world, slot, act)) _lost.Add(slot);

        foreach (int slot in _lost)
        {
            var key = GateKey(world, slot);
            _acting.Remove(slot);
            _duties!.LostWalk(world, slot, TaskType.ManTheGate, key);
        }
    }

    /// <summary>
    /// One tick of a gate guard: a round runs until it is over; else a shut gate it may open gets a round, else it walks to its place. True when a
    /// walk ended short (the round's to a door, or its own to its place): the goal ends for a replan (<see cref="DutyGoals.LostWalk"/>; no limit on
    /// tries, as the original's plan step).
    /// </summary>
    bool StepGuard(World world, int slot, ActOut act)
    {
        if (act.Round is { } round)
        {
            if (!_locks.Advance(world, round, slot)) return false;

            act.Round = null;
            if (world.Characters.Next[slot].Task == (byte)CharacterTask.GoTo) TaskStart.Stand(world.Characters, slot);
            if (round.Lost) return true;
        }

        if (world.Characters.Next[slot].Task == (byte)CharacterTask.GoTo) return false;

        if (OpenRound(world, slot) is { } open)
        {
            act.Round = open;
            return false;
        }

        return ToPlace(world, slot, act);
    }

    /// <summary>
    /// The ignore-map key of the guard's gate building; null without one.
    /// </summary>
    static IgnoreKey? GateKey(World world, int slot) =>
        world.Characters.Cold(slot) is { Ai.Squad.Ai: { } board } && GateOf(board) is { } gate ? new IgnoreKey(gate.Key, default) : null;

    /// <summary>
    /// The guard's gate when it is shut and the guard may open it: not the player's, its first door closed or closing (DOOR_IS_OPEN,
    /// <c>FUN_1405a3ad0</c>, holds for an open door or one opening past 0.8; an opening one gets there by itself), unlocked or locked by the guard's own
    /// faction, and no other round works it. The unlock round of its doors, started; null for none.
    /// </summary>
    Round? OpenRound(World world, int slot)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { Ai.Squad.Ai: { } board } cold || GateOf(board) is not { } gate) return null;
        if (world.Fixtures.OwnsBuilding(gate.Key, world.Player.Faction) || _locks.Targeted(gate.Key)) return null;

        var door = LockSystem.FirstDoor(world, gate.Key);
        if (door is null || door.DoorBroken || door.Motion is not (DoorMotion.Closed or DoorMotion.Closing)) return null;
        if (door.Lock is { EffectivelyLocked: true } && door.Owner != cold.Faction) return null;

        return StartRound(world, slot, cold, gate.Key);
    }

    /// <summary>
    /// The squad's gate: the gate post's building, else its home when that is a gate (the HOME_GATE hand, <see cref="AiSystem"/>'s gate finder).
    /// </summary>
    static TownBuilding? GateOf(Blackboard board) => board.Gate?.Building ?? (board.GateHome is { IsGate: true } home ? home : null);

    /// <summary>
    /// Starts an unlock round on every door of the gate building as the goal's task; null when it has no door or the start is refused.
    /// </summary>
    static Round? StartRound(World world, int slot, CharacterCold cold, string building)
    {
        var doors = new List<string>();
        foreach (var f in world.Fixtures.OfBuilding(building))
            if (f.Kind == FixtureKind.Door) doors.Add(f.Id);
        if (doors.Count == 0) return null;

        var round = new Round
        {
            Kind = RoundKind.UnlockDoors,
            Worker = world.Characters.IdOf(slot),
            Faction = cold.Faction,
            Targets = [.. doors],
            Building = building,
            Reach = LockSystem.DoorWorkReach,
        };
        return LockSystem.Start(world, slot, round, world.Fixtures.Find(doors[0])!, TaskSource.Goal) ? round : null;
    }

    /// <summary>
    /// The guard walks to its place (the post's slack, <see cref="AiSettings.PostSlack"/>) and stands there. True when its last walk there ended
    /// short.
    /// </summary>
    static bool ToPlace(World world, int slot, ActOut act)
    {
        ref var n = ref world.Characters.Next[slot];
        if (Place(world, slot) is not { } place) return false;

        float slack = world.SystemOf<AiSystem>()?.Settings.PostSlack ?? 12;
        if (Vector2.DistanceSquared(new Vector2(n.Position.X, n.Position.Z), new Vector2(place.X, place.Z)) <= slack * slack)
        {
            act.Walked = false;
            return false;
        }

        if (act.Walked) return true;

        act.Walked = true;
        if (float.IsNaN(place.Y)) TaskStart.Walk(world, slot, new Vector2(place.X, place.Z), SpeedMode.Walk, TaskType.ManTheGate, TaskSource.Goal);
        else TaskStart.Walk(world, slot, place, SpeedMode.Walk, TaskType.ManTheGate, TaskSource.Goal);

        return false;
    }

    /// <summary>
    /// Where the guard stands: its squad's gate post (<see cref="AiSystem.FrontOf"/>, on the leaf's floor), else its own post (no floor height:
    /// <c>Y</c> is NaN); null without a mind.
    /// </summary>
    static Vector3? Place(World world, int slot)
    {
        if (world.Characters.Cold(slot)?.Ai is not { Squad.Ai: { } board } mind) return null;

        return board.Gate is { } gate ? AiSystem.FrontOf(gate) : new Vector3(mind.Post.X, float.NaN, mind.Post.Y);
    }

    /// <summary>
    /// The act-out's start (serial): the round opening its shut gate, else the walk to its place (standing where it is when there).
    /// </summary>
    bool Start(World world, in DutyAsk ask)
    {
        var act = new ActOut(ask.Id);
        StepGuard(world, ask.Slot, act);
        _acting[ask.Slot] = act;
        return true;
    }

    /// <summary>
    /// The goal ended (displaced, an order, a replan, taken, gone): the guard is no longer held, its round dropped.
    /// </summary>
    void End(int slot, CharacterId id)
    {
        if (_acting.TryGetValue(slot, out var act) && act.Id == id) _acting.Remove(slot);
    }

    #endregion

    #region The goal

    /// <summary>
    /// Goal 180 MAN_THE_GATE as the chooser weighs it (tier and weight from the package's AI_TASK record; no commitment).
    /// </summary>
    sealed class Manning(GateGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 180's desire, AI slot 19 <c>FUN_140641550</c>: 1.0 (<b>Verified</b>, the registration's stub).
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => 1;

        /// <summary>
        /// Opens its shut gate, then walks to its place and stands there.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Start(world, ask);

        /// <summary>
        /// The guard is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.End(slot, id);
    }

    #endregion

    /// <summary>
    /// Adds the act-outs to the state hash, only while there are any.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        if (_acting.Count == 0) return;

        hasher.Add(0x47617465);   // "Gate"
        hasher.Add(_acting.Count);
        foreach (var (slot, act) in _acting)
        {
            hasher.Add(slot);
            hasher.Add(act.Id);
            hasher.Add(act.Walked ? 1 : 0);
            if (act.Round is { } round) round.Hash(ref hasher);
            else hasher.Add(0);
        }
    }
}
