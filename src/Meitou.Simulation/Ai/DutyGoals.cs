using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Ai;

/// <summary>
/// One NPC and one of its goals that an engine system acts out (<see cref="IDutyGoal"/>), as the chooser or the planner asks about it: the character's
/// slot and id, its squad, the goal type and its record's <c>targeting</c>, the row asked about (the goal's own type, or a row of its plan) and the
/// target the plan carries there (none: <see cref="PlanTarget.IsNone"/>).
/// </summary>
internal readonly record struct DutyAsk(
    int Slot, CharacterId Id, Squad Squad, TaskType Type, TaskTargetType Targeting, TaskType Row, PlanTarget Target);

/// <summary>
/// Why an engine-acted goal ended (<see cref="IDutyGoal.End"/>).
/// </summary>
internal enum DutyEnd : byte
{
    /// <summary>
    /// Another goal of the character scored higher at a think: the next tick acts that one out.
    /// </summary>
    Displaced,
    /// <summary>
    /// An order's task starts on the character (<see cref="IDutyRelease"/>, a faction change too).
    /// </summary>
    Order,
    /// <summary>
    /// The character's plan was reset: a knock-out, death or pick-up wiped it, an order on its body (a stumble, a get-up) started, its mind was made
    /// anew, or the plan step ended the goal (its hours, its list B).
    /// </summary>
    Replanned,
    /// <summary>
    /// Something else holds the character now: another system engaged it, it flees, fights, is carried or caged, or a task another system started
    /// replaced the act-out.
    /// </summary>
    Taken,
    /// <summary>
    /// The character left the table or died.
    /// </summary>
    Gone,
}

/// <summary>
/// A goal of the NPCs' packages that an engine system acts out (docs/simulation.md "Engine goals"): the chooser weighs it with every other goal of
/// the character by the original's desire, finder, tier and weight, and the system starts its act-out once it is chosen and ends it when the goal goes.
/// <see cref="Desire"/>, <see cref="Find"/> and <see cref="State"/> run in the AI's parallel think on the asker's own partition: they may read the
/// character's own slot and cold state, <c>Previous</c> for other slots, and the system's state that only its serial steps change (build candidate
/// lists in a serial step, such as the system's own <see cref="ITickSystem.Schedule"/>, and read them here). <see cref="Start"/> and
/// <see cref="End"/> are serial, in slot order.
/// </summary>
internal interface IDutyGoal
{
    /// <summary>
    /// The desire of <see cref="DutyAsk.Row"/> (the goal's own type, or a row of its plan on the way) for the asker: the original's desire function. 0
    /// or below, not wanted. The finder's factor is the chooser's to apply (<see cref="Find"/>).
    /// </summary>
    float Desire(World world, in DutyAsk ask);

    /// <summary>
    /// The target finder of <see cref="DutyAsk.Row"/> (asked only for a row whose TaskData has one, <see cref="TaskDataFlags.Finder"/>): its factor
    /// (0: found nothing) and the target, a place, a character or a fixture (<see cref="PlanTarget"/>). The chooser multiplies the goal's desire by the
    /// factor when its row has an effect, and the planner plans to the target. The default finds nothing.
    /// </summary>
    float Find(World world, in DutyAsk ask, out PlanTarget target)
    {
        target = default;
        return 0;
    }

    /// <summary>
    /// A state of the goal's plan rows that the planner does not evaluate itself, for <see cref="DutyAsk.Target"/>; null: not known here (the
    /// planner's own answer stands). Asked before the planner's held-true and false defaults, so a goal's states need no edit of the shared planner.
    /// </summary>
    bool? State(World world, in DutyAsk ask, StateType state) => null;

    /// <summary>
    /// The character chose the goal: start its act-out (serial, slot order, the same tick, only while the AI still holds the character). The plan is
    /// the chooser's for the goal, leaf first, each step with its target. Tasks the act-out starts carry <see cref="TaskSource.Goal"/>. True when it
    /// started; false: nothing started, and the character weighs the goal at 0 for a few thinks and chooses again at once.
    /// </summary>
    bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan);

    /// <summary>
    /// The goal ended (serial): drop the act-out's own record and anything it holds (a carried body, a claimed fixture). The character's task is left
    /// as it is: whatever ended the goal (the next goal, the order, the body) replaces it. <paramref name="id"/> is the character it was started for
    /// (for <see cref="DutyEnd.Gone"/> the slot may already hold another).
    /// </summary>
    void End(World world, int slot, CharacterId id, DutyEnd why);
}

/// <summary>
/// A system that registers goals it acts out, once when the world is made (<see cref="DutyGoals.Register"/>).
/// </summary>
internal interface IDutyGoalOwner
{
    /// <summary>
    /// Registers its goals with <paramref name="goals"/>.
    /// </summary>
    void RegisterGoals(DutyGoals goals);
}

/// <summary>
/// The engine-acted goals of one world (docs/simulation.md "Engine goals"): each goal type an engine system acts out, registered by that system from
/// its own file (<see cref="IDutyGoalOwner"/>), and the characters such a goal holds. The goal holds its character, not
/// <see cref="CharacterCold.Engaged"/>: <see cref="AiMind.Duty"/> names the goal record whose act-out runs, the AI keeps thinking for the character
/// (<see cref="AiSystem.Holds"/>), and a goal that scores higher takes it over. Made with the world and never changed after (the threads read it
/// without a lock); the held records change only in the AI's serial step.
/// </summary>
internal sealed class DutyGoals
{
    #region Registry

    /// <summary>
    /// A registered goal: the system's object, its type and the rows of its plan its act-out carries out besides the goal's own.
    /// </summary>
    internal sealed class Entry(IDutyGoal goal, TaskType type, TaskType[] rows)
    {
        /// <summary>
        /// The system's goal.
        /// </summary>
        public IDutyGoal Goal { get; } = goal;
        /// <summary>
        /// The goal type.
        /// </summary>
        public TaskType Type { get; } = type;
        /// <summary>
        /// The rows of its plan its act-out carries out (the goal's own type not among them).
        /// </summary>
        readonly TaskType[] _rows = rows;

        /// <summary>
        /// Whether its act-out carries out <paramref name="row"/>: the goal's own type or one of its registered rows.
        /// </summary>
        public bool Acts(TaskType row) => row == Type || Array.IndexOf(_rows, row) >= 0;
    }

    /// <summary>
    /// The registered goals by type number (<see cref="TaskBehaviours.TypeCount"/> of them).
    /// </summary>
    readonly Entry?[] _byType = new Entry?[TaskBehaviours.TypeCount];

    /// <summary>
    /// The registered goals in registration order (systems in tick order).
    /// </summary>
    readonly List<Entry> _entries = [];

    /// <summary>
    /// Whether a system's <see cref="IDutyGoalOwner.RegisterGoals"/> runs now: registering is allowed only then.
    /// </summary>
    bool _open;

    /// <summary>
    /// The goals of a world's systems (<see cref="IDutyGoalOwner"/>, asked in tick order); null when none registered a goal.
    /// </summary>
    internal static DutyGoals? Build(IReadOnlyList<ITickSystem> systems)
    {
        var goals = new DutyGoals();
        foreach (var system in systems)
        {
            if (system is not IDutyGoalOwner owner) continue;

            goals._open = true;
            owner.RegisterGoals(goals);
            goals._open = false;
        }

        return goals._entries.Count > 0 ? goals : null;
    }

    /// <summary>
    /// Registers <paramref name="goal"/> for <paramref name="type"/>, a goal type of the original's (below <see cref="TaskBehaviours.TypeCount"/>)
    /// that no other behaviour acts out, with the rows of its plan the act-out carries out besides the goal's own (<paramref name="rows"/>: they pass
    /// the planner's carried-steps test, and their desire, finder and states are asked of <paramref name="goal"/>). Only inside
    /// <see cref="IDutyGoalOwner.RegisterGoals"/>; a type registered twice or acted out by another behaviour throws.
    /// </summary>
    internal void Register(TaskType type, IDutyGoal goal, params TaskType[] rows)
    {
        ArgumentNullException.ThrowIfNull(goal);
        if (!_open) throw new InvalidOperationException("Engine goals are registered only in IDutyGoalOwner.RegisterGoals.");
        if ((uint)type >= TaskBehaviours.TypeCount) throw new ArgumentException($"Goal type {(int)type} is no type of the original's.", nameof(type));
        if (TaskBehaviours.Of(type) != AiBehaviour.None)
            throw new ArgumentException($"Goal type {(int)type} is acted out as {TaskBehaviours.Of(type)} already.", nameof(type));
        if (_byType[(int)type] is not null) throw new ArgumentException($"Goal type {(int)type} is registered already.", nameof(type));

        var entry = new Entry(goal, type, rows);
        _byType[(int)type] = entry;
        _entries.Add(entry);
    }

    /// <summary>
    /// The registered goal of <paramref name="type"/>; null for none.
    /// </summary>
    internal Entry? EntryOf(TaskType type) => (uint)type < TaskBehaviours.TypeCount ? _byType[(int)type] : null;

    /// <summary>
    /// Whether a system registered a goal for <paramref name="type"/>.
    /// </summary>
    internal bool Has(TaskType type) => EntryOf(type) is not null;

    #endregion

    #region Holding

    /// <summary>
    /// A character a goal holds: who, the goal record its act-out runs for, and the start tick of the task the start left running (the act-out's
    /// provenance, <see cref="AiSystem.OnDuty"/>).
    /// </summary>
    sealed class Held(CharacterId id, Entry entry, AiGoal goal, long tick)
    {
        /// <summary>
        /// The character.
        /// </summary>
        public CharacterId Id { get; } = id;
        /// <summary>
        /// The registered goal.
        /// </summary>
        public Entry Entry { get; } = entry;
        /// <summary>
        /// The goal record of the character's lists it runs for (its tier and weight are the record's).
        /// </summary>
        public AiGoal Goal { get; } = goal;
        /// <summary>
        /// The start tick of the task the start left running.
        /// </summary>
        public long Tick { get; } = tick;
    }

    /// <summary>
    /// Set by a think that chose an engine goal (<see cref="AiMind.DutyAsk"/>) or another goal over a running one (<see cref="AiMind.DutyLeave"/>); the
    /// serial step looks for them only then.
    /// </summary>
    int _asking;

    /// <summary>
    /// The characters the goals hold, by slot (hashed while any).
    /// </summary>
    readonly SortedDictionary<int, Held> _held = [];

    /// <summary>
    /// Scratch: the slots whose record ended in a sweep, with why.
    /// </summary>
    readonly List<(int Slot, DutyEnd Why)> _ended = [];

    /// <summary>
    /// The chooser's desire of an engine goal for the asker: the goal's desire times its finder's factor when its TaskData row has a finder and an
    /// effect (<c>FUN_14060fd80</c>, docs/game/ai-tasks.md "Desire and commitment"); 0 for a type no system registered or an answer at or below 0.
    /// Parallel: the asker's own partition.
    /// </summary>
    internal float Desire(World world, in DutyAsk ask)
    {
        if (EntryOf(ask.Type) is not { } entry) return 0;

        float desire = entry.Goal.Desire(world, ask);
        if (!(desire > 0)) return 0;
        if (TaskDataTable.Of(ask.Type) is { Effect: not null } row && row.Is(TaskDataFlags.Finder)) desire *= entry.Goal.Find(world, ask, out _);

        return desire > 0 ? desire : 0;
    }

    /// <summary>
    /// A think chose an engine goal for <paramref name="mind"/>: the serial step of the same tick starts it (<see cref="Step"/>).
    /// </summary>
    internal void Ask(AiMind mind)
    {
        mind.DutyAsk = true;
        Interlocked.Exchange(ref _asking, 1);
    }

    /// <summary>
    /// A think chose another goal over the engine goal holding <paramref name="mind"/>'s character: the serial step of the same tick ends it
    /// (<see cref="DutyEnd.Displaced"/>), and the character thinks again at the next tick to act the winner out.
    /// </summary>
    internal void Leave(AiMind mind)
    {
        mind.DutyLeave = true;
        Interlocked.Exchange(ref _asking, 1);
    }

    /// <summary>
    /// The AI's serial step: the held records are swept (<see cref="Sweep"/>: displaced, ordered, replanned, taken, gone), then the goals the thinks
    /// chose this tick start (<see cref="SendStarts"/>); a refused start keeps the goal at 0 for that character for <paramref name="backoffTicks"/>.
    /// </summary>
    internal void Step(World world, int backoffTicks)
    {
        if (_held.Count > 0) Sweep(world);
        if (Interlocked.Exchange(ref _asking, 0) != 0) SendStarts(world, backoffTicks);
    }

    /// <summary>
    /// Whether the goal <paramref name="type"/> holds the character at <paramref name="slot"/> and its act-out may go on now (a serial read for the
    /// registering system's own steps): its record runs, the AI holds the character and no order on its body runs. False until the next sweep ends a
    /// record that no longer holds.
    /// </summary>
    internal bool Runs(World world, int slot, TaskType type) =>
        _held.TryGetValue(slot, out var held) && held.Entry.Type == type && world.Characters.Cold(slot) is { } cold
        && Ends(world, slot, cold, held) is null;

    /// <summary>
    /// The act-out is over by the registering system's own rule (serial): the record goes and the goal ends on the mind, which chooses again at its
    /// next think. <see cref="IDutyGoal.End"/> is not called (the system ended it itself). Nothing when the goal does not hold the character.
    /// </summary>
    internal void Done(World world, int slot, TaskType type)
    {
        if (!_held.TryGetValue(slot, out var held) || held.Entry.Type != type) return;

        _held.Remove(slot);
        if (world.Characters.Cold(slot)?.Ai is { } mind) EndOnMind(mind, held);
    }

    /// <summary>
    /// Ends the goal holding the character at <paramref name="slot"/> for <paramref name="why"/> (serial): an order's start
    /// (<see cref="IDutyRelease"/>), an order on its body. Nothing when no goal holds it.
    /// </summary>
    internal void Release(World world, int slot, DutyEnd why)
    {
        if (!_held.Remove(slot, out var held)) return;

        held.Entry.Goal.End(world, slot, held.Id, why);
        if (world.Characters.Cold(slot)?.Ai is { } mind) EndOnMind(mind, held);
    }

    /// <summary>
    /// Starts the engine goals the thinks chose this tick (serial, slot order), for each character the AI still holds whose current goal is that goal
    /// and that no goal holds yet; a start is recorded (<see cref="Take"/>), a refusal backs the goal off for the character, and a character skipped
    /// chooses again at the next tick.
    /// </summary>
    void SendStarts(World world, int backoffTicks)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { DutyAsk: true } mind } cold) continue;

            mind.DutyAsk = false;
            if (!table.Next[s].Alive) continue;
            if (mind.Goal is not { } goal || EntryOf(goal.Task.Type) is not { } entry || mind.Duty is not null || !AiSystem.Holds(table.Next[s], cold))
            {
                mind.Replan = true;
                continue;
            }

            var ask = new DutyAsk(s, table.IdOf(s), mind.Squad, goal.Task.Type, goal.Task.Target, goal.Task.Type, GoalTarget(mind, goal.Task.Type));
            if (entry.Goal.Start(world, ask, mind.Plan)) Take(world, s, cold, mind, goal, entry);
            else
            {
                mind.BackOff(goal.Task.Type, world.Tick + backoffTicks);
                mind.Replan = true;
            }
        }
    }

    /// <summary>
    /// The target the plan carries for the goal's own step (none without one).
    /// </summary>
    static PlanTarget GoalTarget(AiMind mind, TaskType type)
    {
        foreach (var step in mind.Plan)
            if (step.Type == type) return step.Target;
        return default;
    }

    /// <summary>
    /// A started goal holds its character: the mind names the goal record and the start tick of the task the start left running, and the record is
    /// kept by slot.
    /// </summary>
    void Take(World world, int slot, CharacterCold cold, AiMind mind, AiGoal goal, Entry entry)
    {
        long tick = cold.Running?.Tick ?? world.Tick;
        mind.Duty = goal;
        mind.DutyTick = tick;
        _held[slot] = new Held(world.Characters.IdOf(slot), entry, goal, tick);
    }

    /// <summary>
    /// The records of the held characters, in slot order: each that ended (<see cref="Ends"/>) is told to its system (<see cref="IDutyGoal.End"/>)
    /// and the goal ends on the mind.
    /// </summary>
    void Sweep(World world)
    {
        var table = world.Characters;
        _ended.Clear();
        foreach (var (slot, held) in _held)
        {
            if (!table.TryResolveNext(held.Id, out int at) || at != slot || !table.Next[slot].Alive || table.Cold(slot) is not { } cold)
                _ended.Add((slot, DutyEnd.Gone));
            else if (Ends(world, slot, cold, held) is { } why)
                _ended.Add((slot, why));
        }

        foreach (var (slot, why) in _ended)
        {
            _held.Remove(slot, out var held);
            held!.Entry.Goal.End(world, slot, held.Id, why);
            if (why != DutyEnd.Gone && table.Cold(slot)?.Ai is { } mind) EndOnMind(mind, held);
        }
    }

    /// <summary>
    /// Why a live record ended, or null while it holds: its think chose another goal (displaced); an order runs; an order on its body runs, it is
    /// down, its mind was made anew or its plan wiped or ended (replanned); the AI no longer holds it (taken: engaged by another system, fleeing,
    /// fighting, carried, caged, or a task another system started replaced the act-out, <see cref="AiSystem.OnDuty"/>).
    /// </summary>
    static DutyEnd? Ends(World world, int slot, CharacterCold cold, Held held)
    {
        if (cold.Ai is not { Duty: { } duty } mind || !Same(duty, held.Goal)) return DutyEnd.Replanned;
        if (mind.DutyLeave) return DutyEnd.Displaced;
        if (PlayerOrders.BodyHolds(cold) || cold.Medical is { Incapacitated: true }) return DutyEnd.Replanned;
        if (DecisionSystem.Ordered(cold)) return DutyEnd.Order;
        if (mind.Goal is not { } goal || !Same(goal, held.Goal)) return DutyEnd.Replanned;

        return AiSystem.Holds(world.Characters.Next[slot], cold) ? null : DutyEnd.Taken;
    }

    /// <summary>
    /// Whether two goal records are the same goal as the chooser's keep reads it (<c>FUN_140506c00</c>: type and target hand).
    /// </summary>
    internal static bool Same(AiGoal a, AiGoal b) => a.Task.Type == b.Task.Type && a.Task.Target == b.Task.Target;

    /// <summary>
    /// The goal ends on the mind that still runs this record: the mind no longer names it, its goal and plan go (no continuity) and it chooses again.
    /// </summary>
    static void EndOnMind(AiMind mind, Held held)
    {
        if (mind.Duty is not { } duty || !Same(duty, held.Goal)) return;

        mind.Duty = null;
        mind.DutyTick = 0;
        mind.DutyLeave = false;
        if (mind.Goal is { } goal && Same(goal, held.Goal))
        {
            mind.Goal = null;
            mind.Plan.Clear();
        }

        mind.Replan = true;
    }

    /// <summary>
    /// Adds the held characters' records to the state hash; nothing while none is held.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        if (_held.Count == 0) return;

        hasher.Add(0x44757479);   // "Duty"
        foreach (var (slot, held) in _held)
        {
            hasher.Add(slot);
            hasher.Add(held.Id);
            hasher.Add((int)held.Goal.Task.Type);
            hasher.Add((int)held.Goal.Task.Target);
            hasher.Add(held.Tick);
        }
    }

    #endregion
}
