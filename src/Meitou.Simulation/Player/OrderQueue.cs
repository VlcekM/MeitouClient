using System.Collections;
using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Player;

/// <summary>
/// An order command (docs/simulation.md "Order queue"): the characters it names get it as an <see cref="OrderEntry"/> in their
/// <see cref="CharacterCold.OrderQueue"/>. The paused interface applies every order command (<see cref="World.ApplyPausedInterfaceCommands"/>).
/// </summary>
public abstract record OrderCommand : SimCommand
{
    /// <summary>
    /// Added after the characters' queued orders (Shift in the game) instead of replacing them (<c>Character::addOrder</c> <c>FUN_1405c8da0</c>
    /// clears the queue without it).
    /// </summary>
    public bool Queued { get; init; }

    /// <summary>
    /// Given by a quick right click on the building it names: the order is then made at the building's position on the terrain
    /// (<see cref="OrderEntry.AtBuilding"/>); false for the other ways of giving it.
    /// </summary>
    public bool Clicked { get; init; }
}

/// <summary>
/// What an order's check found (<see cref="IOrderOwner.CanRun"/>).
/// </summary>
public enum OrderVerdict : byte
{
    /// <summary>
    /// It can start now.
    /// </summary>
    Run,
    /// <summary>
    /// It cannot start now (the original's plan fails): it stays queued, its failure text is shown, and the character falls through to its jobs and
    /// goals.
    /// </summary>
    Blocked,
    /// <summary>
    /// Its target or site is gone or no longer fits the verb (the original's desire x finder is 0): it leaves the queue.
    /// </summary>
    Gone,
}

/// <summary>
/// An order's check: the verdict and, when blocked, the failure text (null: none), barked over the character or, <see cref="Posted"/>, posted as a
/// message.
/// </summary>
public readonly record struct OrderCheck(OrderVerdict Verdict, string? Why = null, bool Posted = false)
{
    /// <summary>
    /// The order can start now.
    /// </summary>
    public static OrderCheck Run => new(OrderVerdict.Run);
    /// <summary>
    /// The order's target or site is gone: it leaves the queue.
    /// </summary>
    public static OrderCheck Gone => new(OrderVerdict.Gone);
    /// <summary>
    /// The order cannot start now; <paramref name="why"/> is the failure text (null: none), <paramref name="posted"/> posts it as a message instead
    /// of a bark.
    /// </summary>
    public static OrderCheck Blocked(string? why = null, bool posted = false) => new(OrderVerdict.Blocked, why, posted);
    /// <summary>
    /// Whether the verdict is <see cref="OrderVerdict.Run"/>.
    /// </summary>
    public bool CanRun => Verdict == OrderVerdict.Run;
}

/// <summary>
/// The system an order's row belongs to: it checks an <see cref="OrderEntry"/> of the character (<see cref="CanRun"/>, a pure read: the row's
/// preconditions as far as Meitou models them; the queue adds the repertoire and the threat test, <see cref="PlayerOrders.CanRun"/>) and starts it
/// (<see cref="Start"/>, serial phases only, after a <see cref="OrderVerdict.Run"/>).
/// </summary>
public interface IOrderOwner
{
    /// <summary>
    /// Checks the character's order (a pure read: the row's preconditions as far as Meitou models them).
    /// </summary>
    OrderCheck CanRun(World world, int slot, OrderEntry order);
    /// <summary>
    /// Starts the character's order (serial phases only, after a <see cref="OrderVerdict.Run"/>).
    /// </summary>
    void Start(World world, int slot, OrderEntry order);

    /// <summary>
    /// Whether a started order's start conditions still hold (the original plans the front again each update); false ends its task and the order
    /// waits unstarted, its failure text said again.
    /// </summary>
    bool StillHolds(World world, int slot, OrderEntry order) => true;

    /// <summary>
    /// Whether the order stays at the front when its task ends, planned again for its next step (a pure read): the original's order leaves only by
    /// its row's done test or its desire, so an order whose act-out ends one step at a time (101 and 248 on each member of the squad its finder
    /// picks, 22's walks) is started again until its owner's check says it is gone.
    /// </summary>
    bool Repeats(World world, int slot, OrderEntry order) => false;

    /// <summary>
    /// The code of the order's <see cref="OrderEntry.Detail"/> in a save (<see cref="Saving.OrderSave"/>; 0: no detail), or -1 when the order cannot
    /// be saved (a detail that names a runtime object). An owner whose orders carry a detail overrides it together with <see cref="LoadDetail"/>.
    /// </summary>
    int SaveKind(OrderEntry order) => order.Detail is null ? 0 : -1;

    /// <summary>
    /// The detail a saved order of the code <paramref name="kind"/> gets back (the inverse of <see cref="SaveKind"/>); <paramref name="self"/> is the
    /// character holding the order.
    /// </summary>
    object? LoadDetail(World world, CharacterId self, TaskType row, int kind, CharacterId target) => null;

    /// <summary>
    /// Readies what a loaded order needs that a save does not hold, before it is first checked (serial phases, <see cref="Saving.OrderSave.Restore"/>):
    /// what the order's command readies when it is given live.
    /// </summary>
    void Restored(World world, OrderEntry order)
    {
    }
}

/// <summary>
/// A system that holds NPCs on an engine duty (<see cref="CharacterCold.Engaged"/>, or a record of its own that steers them): when an order's task is
/// about to start on an NPC (<see cref="PlayerOrders"/>' front start, serial), the world asks every such system to let the character go
/// (<see cref="World.ReleaseDuties"/>). Found on the world's systems, nothing to register.
/// </summary>
internal interface IDutyRelease
{
    /// <summary>
    /// Lets the character in <paramref name="slot"/> go through the duty's own give-up: its record dropped, its <see cref="CharacterCold.Engaged"/>
    /// cleared, what it carried let go; nothing when the system holds it on nothing. Its task is replaced right after, so it need not stand it.
    /// </summary>
    void ReleaseForOrder(World world, int slot);
}

/// <summary>
/// An order owner whose order's plan runs several tasks one after another (the arrest's fight, strip, lift and cage walk): the original plans the
/// front again on every update, so the end of one step is not the order's. While the owner's act-out is between two steps the front still runs
/// (<see cref="PlayerOrders.Runs"/>), and the plan step asks no list B of the order's row while a step of another row runs.
/// </summary>
internal interface IOrderSteps
{
    /// <summary>
    /// Whether the owner's act-out of <paramref name="order"/> goes on for the character in <paramref name="slot"/> though no task of the order runs
    /// now (a pure read, any serial phase).
    /// </summary>
    bool Between(World world, int slot, OrderEntry order);
}

/// <summary>
/// One order of a character's queue (the original's <c>+0x40</c> deque entry): its row, owner, target, site, point and the owner's
/// <see cref="Detail"/>. <see cref="Started"/> once its start ran; it then stands for the character's task while that task's provenance is an order's
/// (<see cref="PlayerOrders.Runs"/>). Only the order itself is saved, never <see cref="Started"/> (<see cref="Saving.OrderSave"/>).
/// </summary>
public sealed class OrderEntry(TaskType row, IOrderOwner owner)
{
    /// <summary>
    /// The order's TaskType row (what the original's planner runs).
    /// </summary>
    public TaskType Row { get; } = row;
    /// <summary>
    /// The system that checks and starts the order.
    /// </summary>
    public IOrderOwner Owner { get; } = owner;
    /// <summary>
    /// The character or body the order targets (<see cref="CharacterId.None"/>: none).
    /// </summary>
    public CharacterId Target { get; init; } = CharacterId.None;
    /// <summary>
    /// The site the order names (a building or fixture id); null: none.
    /// </summary>
    public string? Site { get; init; }
    /// <summary>
    /// The point the order names (a move's goal).
    /// </summary>
    public Vector3 Point { get; init; }
    /// <summary>
    /// The point the original's order goal is made at (Tasker <c>+0x58</c>, which its plan's steps inherit), zero for none. <b>Verified</b>
    /// (disassembly of the right-click dispatch <c>FUN_1407fc6e0</c> and the dispatchers it calls, <c>FUN_1407fae70</c>, <c>FUN_1407fa650</c>,
    /// <c>FUN_1407f9e20</c>, down to <c>FUN_1405d20d0</c> -> <c>FUN_140508380</c>): a ground move's point and a complete building's default order
    /// on a right click (<see cref="AtBuilding"/>); every other dispatch passes the zero vector (orders on characters <c>FUN_1407fb410</c>, items
    /// <c>FUN_1407fc440</c> / <c>FUN_1407fb3a0</c>, containers and the walks to them, unfinished or damaged buildings 2 / 95, a verb of the held right
    /// button's menu <c>FUN_1407a7f10</c>).
    /// </summary>
    public Vector3 TaskPoint { get; init; }
    /// <summary>
    /// The owner's own data for the order (null: none); see <see cref="IOrderOwner.SaveKind"/> for how it is saved.
    /// </summary>
    public object? Detail { get; init; }
    /// <summary>
    /// Whether the order's start ran: it then stands for the character's task.
    /// </summary>
    public bool Started { get; internal set; }
    /// <summary>
    /// Its check ran at the front once (its failure text was shown); a front not tried yet is tried at once.
    /// </summary>
    public bool Tried { get; internal set; }
    /// <summary>
    /// The order runs its own row (<see cref="ListTests.OrderNeedsFail"/>): its list B held at its start, or since, a step of its plan (a follow
    /// row's fight, 13) being over; the original runs its row only then, so only then does a failing list B end it.
    /// </summary>
    public bool RowRuns { get; internal set; }
    /// <summary>
    /// An engine act on the character's body (getting up, stumbling, getting out of bed, <see cref="Combat.BodyOrders"/>): its start lets no duty
    /// go (<see cref="IDutyRelease"/>), as the original's goals are planned again once it is over.
    /// </summary>
    internal bool Interrupts { get; init; }
    /// <summary>
    /// The engine starts the order's task itself, with no plan (the stagger's 147, <c>AI::forceOrderInsert</c> <c>FUN_140507cd0</c>, which sets the
    /// body task directly): the queue tests only its owner's check, not the repertoire, the threat rule or list B.
    /// </summary>
    internal bool Forced { get; init; }

    /// <summary>
    /// Whether the order is a move (row <c>MoveCusOrdered</c>).
    /// </summary>
    public bool IsMove => Row == TaskType.MoveCusOrdered;

    /// <summary>
    /// The <see cref="TaskPoint"/> of a building's order given by <paramref name="order"/>: on a right click (<see cref="OrderCommand.Clicked"/>)
    /// the building's position with the terrain's height there (<c>FUN_1407fc6e0</c> passes the clicked object's position, vfunc <c>0x40</c>, at
    /// the height <c>FUN_1409b32f0</c> gives), else zero.
    /// </summary>
    public static Vector3 AtBuilding(World world, OrderCommand order, Vector3 building) =>
        order.Clicked ? new Vector3(building.X, world.Walkability.TerrainHeight(building.X, building.Z), building.Z) : default;
}

/// <summary>
/// A character's order queue (<see cref="CharacterCold.OrderQueue"/>, the original's <c>+0x40</c> deque, count <c>+0x60</c>): the front is the order
/// in hand, the rest wait (Shift). Changed by the character's own partition (a move's arrival) or a serial phase. Saved by
/// <see cref="Saving.OrderSave"/> (docs/simulation.md "Order queue").
/// </summary>
public sealed class OrderQueue : IReadOnlyList<OrderEntry>
{
    /// <summary>
    /// The orders, the front first.
    /// </summary>
    readonly List<OrderEntry> _entries = [];

    /// <summary>
    /// How many orders are queued, the one in hand included.
    /// </summary>
    public int Count => _entries.Count;
    /// <summary>
    /// The order at <paramref name="index"/> (0: the front).
    /// </summary>
    public OrderEntry this[int index] => _entries[index];
    /// <summary>
    /// The order in hand; null when the queue is empty.
    /// </summary>
    public OrderEntry? Front => _entries.Count > 0 ? _entries[0] : null;
    /// <summary>
    /// Empties the queue.
    /// </summary>
    public void Clear() => _entries.Clear();
    /// <summary>
    /// Adds an order at the back.
    /// </summary>
    internal void Add(OrderEntry entry) => _entries.Add(entry);
    /// <summary>
    /// Adds an order at the front.
    /// </summary>
    internal void AddFront(OrderEntry entry) => _entries.Insert(0, entry);
    /// <summary>
    /// Removes the front order.
    /// </summary>
    internal void PopFront() => _entries.RemoveAt(0);
    /// <summary>
    /// The orders from the front.
    /// </summary>
    public IEnumerator<OrderEntry> GetEnumerator() => _entries.GetEnumerator();
    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Adds the queue to the state hash as the move-only queue did (the moves after the one in hand) while it holds only what that queue could:
    /// nothing, a started order alone, or started moves. Anything else (an order waiting, a queued order other than a move) is added under its own
    /// mark, so worlds without them hash as before.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        bool legacy = _entries.Count == 0 || _entries[0].Started && (_entries.Count == 1 || _entries.TrueForAll(e => e.IsMove));
        hasher.Add(Math.Max(_entries.Count - 1, 0));
        for (int k = 1; k < _entries.Count; k++)
            if (_entries[k].IsMove) hasher.Add(new Vector2(_entries[k].Point.X, _entries[k].Point.Z));
        if (legacy) return;
        hasher.Add(0x4F717565);
        foreach (var e in _entries)
        {
            hasher.Add((int)e.Row);
            hasher.Add(e.Started);
            hasher.Add(e.Tried);
            hasher.Add(e.Target);
            hasher.Add(e.Site is { } site ? Rng.StableHash(site) : 0UL);
            hasher.Add(e.Point);
        }
    }
}

/// <summary>
/// The order queue's rules (docs/simulation.md "Order queue"; the original's <c>checkOrders</c> <c>FUN_140510920</c> and order giver
/// <c>FUN_1405d20d0</c>, docs/game/ai-tasks.md "Precedence"): an order command clears the queue (unless Shift) and queues the order; the front starts
/// through its owner when it can run, else shows its failure text and stays queued while the character's jobs, goals and reactions go on; a front
/// whose task ended leaves the queue and the next starts. The decision step asks the front as its source 1 (<see cref="DecisionSystem"/>), for
/// every character with a queue: the player's and NPCs alike.
/// </summary>
public static class PlayerOrders
{
    /// <summary>
    /// Gives a character an order (serial phases only): without <paramref name="queued"/> the queue is cleared first; the order is queued and, at the
    /// front, started when it can run, else its failure text is shown (<paramref name="show"/>). The character's task is left alone while the order
    /// cannot run. Any character takes orders; an NPC's front starts only while its chooser runs (<see cref="DecisionSystem.TakesOrders"/>), else it
    /// waits queued. Returns the check of the front.
    /// </summary>
    public static OrderCheck Give(World world, int slot, OrderEntry order, bool queued, bool show = true)
    {
        var cold = world.Characters.Cold(slot)!;
        var q = cold.OrderQueue;
        // A queue loaded and not restored yet (OrderSave.Restore): a queued order goes behind it, any other replaces it.
        if (cold.SavedOrders is not null)
        {
            if (queued) Saving.OrderSave.Restore(world, slot, cold);
            else
                cold.SavedOrders = null;
        }
        if (!queued)
        {
            q.Clear();
            cold.SecondList.Clear();   // the order giver empties the second list with the queue
        }
        else
        {
            Settle(world, slot);
        }

        q.Add(order);
        DecisionSystem.Request(cold);   // addOrder replans at once: the order's own start below, the fall-through at the decision step
        bool starts = q.Count == 1 && (DecisionSystem.Decides(cold) || DecisionSystem.TakesOrders(cold));
        var check = starts ? TryFront(world, slot, show) : OrderCheck.Run;
        DecisionSystem.Hold(world, slot);

        return check;
    }

    /// <summary>
    /// Pushes an order to the front of the character's queue (serial phases; <c>AI::forceOrderInsert</c> <c>FUN_140507cd0</c> → <c>FUN_14050b6a0</c>,
    /// docs/game/combat.md "Fight lifecycle and orders"): a front whose task ended leaves first; a running front stays queued behind it, to be
    /// planned again from its start once the new one is over. A running front of the same row keeps its task and the new entry takes it over (the
    /// body task keeps the running stumble, <c>FUN_1405c6ec0</c>), the old one waiting behind it; else the new front starts as <see cref="Give"/>'s,
    /// a <see cref="OrderEntry.Forced"/> one also for a character whose chooser does not run but that is not down (the insert sets the body task
    /// itself).
    /// </summary>
    internal static void GiveFront(World world, int slot, OrderEntry order)
    {
        var cold = world.Characters.Cold(slot)!;
        var q = cold.OrderQueue;
        Settle(world, slot);
        bool adopts = q.Front is { } running && running.Row == order.Row && Runs(world, slot, running);
        Interrupt(cold);
        q.AddFront(order);
        DecisionSystem.Request(cold);

        bool starts = DecisionSystem.Decides(cold) || DecisionSystem.TakesOrders(cold) || order.Forced && !DecisionSystem.Down(cold);
        if (adopts)
        {
            order.Started = true;
            order.Tried = true;
        }
        else if (starts)
        {
            TryFront(world, slot, show: false);
        }

        DecisionSystem.Hold(world, slot);
    }

    /// <summary>
    /// The task-ended handling of a character whose chooser does not run (no squad) but that is not down (Character slot 27 <c>FUN_1405c7c30</c> →
    /// <c>FUN_14050d920</c> → <c>FUN_14050d7d0</c>, <b>Verified</b>: no platoon test): a started front whose task ended leaves (serial phases).
    /// </summary>
    internal static void DropEnded(World world, int slot) => Settle(world, slot);

    /// <summary>
    /// The running front's task is ended from outside while the order stays queued (the stagger's front insert, the leg collapse's stop
    /// <c>FUN_1405c8330</c>): it waits unstarted and is planned again from its start (serial phases, or the character's own slot).
    /// </summary>
    internal static void Interrupt(CharacterCold cold)
    {
        if (cold.OrderQueue.Front is not { Started: true } front) return;

        front.Started = false;
        front.RowRuns = false;
    }

    /// <summary>
    /// Whether an order on the character's body (<see cref="OrderEntry.Interrupts"/>: a stumble, a get-up) is its started front now: its body task
    /// replaced the goal's, so a duty or a flight holding the character ends and the AI chooses again after it (a pure read, any phase).
    /// </summary>
    internal static bool BodyHolds(CharacterCold cold) => cold.OrderQueue.Front is { Interrupts: true, Started: true };

    /// <summary>
    /// The ragdoll setter's wipe (<c>FUN_1405d0db0</c> turning WHOLE or CARRY on → <c>FUN_1405074a0</c>, <c>FUN_14050d1b0</c>; a knock-out, death or
    /// being picked up, docs/game/ai-tasks.md "What clears the queue"): the order queue and the second list (a loaded queue not restored yet) are
    /// emptied and an AI's plan and goal dropped; the job list stays (serial phases).
    /// </summary>
    internal static void Wipe(CharacterCold cold)
    {
        cold.OrderQueue.Clear();
        cold.SecondList.Clear();
        cold.SavedOrders = null;
        if (cold.Ai is not { } mind) return;

        mind.ResetPlan();
        mind.Goal = null;
    }

    /// <summary>
    /// Whether the order can start for the character now (a pure read; the M6 <c>World.CanOrder</c> rests on it): the row is in the character's
    /// repertoire (<see cref="TaskStart.Eligible"/>), its owner's check, a row needing no melee threat (<see cref="NeedsNoThreat"/>) has none
    /// (<see cref="Threatened"/>), and no failing list B entry leaves its plan without a step (<see cref="ListTests.OrderBlocked"/>).
    /// </summary>
    public static OrderCheck CanRun(World world, int slot, OrderEntry order)
    {
        var cold = world.Characters.Cold(slot)!;
        if (order.Forced) return order.Owner.CanRun(world, slot, order);
        if (!TaskStart.Eligible(world, cold, order.Row)) return OrderCheck.Blocked();
        var own = order.Owner.CanRun(world, slot, order);
        if (!own.CanRun) return own;
        if (Threatened(world, slot, order.Row)) return OrderCheck.Blocked();

        return ListTests.OrderBlocked(world, slot, order, out string? line) ? OrderCheck.Blocked(line is null ? null : Loc.Tr(line)) : own;
    }

    /// <summary>
    /// Whether the row's list B has <c>!158</c> <c>iAmBeingMeleeAttacked_immediate</c>, which no row provides, so the plan fails while the character
    /// has a melee threat (docs/game/ai-tasks.md "Precedence"): of the player's orders only 146 Turret gunner.
    /// </summary>
    public static bool NeedsNoThreat(TaskType row) =>
        TaskDataTable.Of(row) is { } data && Array.Exists(data.Needs, c => c is { State: StateType.IAmBeingMeleeAttackedImmediate, Holds: false });

    /// <summary>
    /// Whether the row needs no melee threat and the character has one within 40 (state 158, <see cref="CharacterFacts.ImmediateThreat"/>).
    /// </summary>
    static bool Threatened(World world, int slot, TaskType row) =>
        NeedsNoThreat(row) && world.SystemOf<Combat.CombatSystem>() is { } combat && CharacterFacts.ImmediateThreat(combat, world, slot);

    /// <summary>
    /// Whether the front order started and its task still runs: the task's provenance is an order's for it (<see cref="RunningTask.Runs"/>; a move's
    /// walk is the order's move) and its handler has more to do, or its owner's act-out is between two steps of its plan
    /// (<see cref="IOrderSteps.Between"/>).
    /// </summary>
    public static bool Runs(World world, int slot, OrderEntry order)
    {
        if (!order.Started) return false;
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var task = (CharacterTask)table.Next[slot].Task;
        bool between = order.Owner is IOrderSteps steps && steps.Between(world, slot, order);
        if (cold.Running is not { Source: TaskSource.Order } run || !run.Runs(task) || order.IsMove && !cold.OrderMove) return between;

        return world.Tasks.Of(cold, task) is not { } handler || !handler.Done(world, slot, task) || between;
    }

    /// <summary>
    /// Whether the character holds an order (the original's queue count <c>+0x60</c> is not 0): one running, waiting to start or queued; a front
    /// whose task ended and that the queue has not dropped yet does not count. A pure read.
    /// </summary>
    public static bool Holds(World world, int slot)
    {
        var q = world.Characters.Cold(slot)!.OrderQueue;
        if (q.Front is not { } front) return false;
        if (!front.Started || Runs(world, slot, front) || front.Owner.Repeats(world, slot, front)) return true;
        return !front.IsMove && q.Count > 1;   // what Settle keeps
    }

    /// <summary>
    /// Drops a front whose task ended: a move's, with the moves queued after it, as an interrupted walk always dropped them; any other order's alone.
    /// </summary>
    static void Settle(World world, int slot)
    {
        var q = world.Characters.Cold(slot)!.OrderQueue;
        if (q.Front is not { Started: true } front || Runs(world, slot, front)) return;

        Leave(world, slot, front);
    }

    /// <summary>
    /// The front's task ended: a repeating order (<see cref="IOrderOwner.Repeats"/>) waits unstarted at the front for its next step; else a move
    /// leaves with the moves queued after it, any other order alone.
    /// </summary>
    static void Leave(World world, int slot, OrderEntry front)
    {
        var q = world.Characters.Cold(slot)!.OrderQueue;
        if (front.Owner.Repeats(world, slot, front))
        {
            front.Started = front.Tried = false;
            return;
        }

        if (front.IsMove) q.Clear();
        else
            q.PopFront();
    }

    /// <summary>
    /// Tries the front: gone ones leave, a blocked one stays (its text shown with <paramref name="show"/> the first time), one that can run starts;
    /// one whose start leaves nothing running leaves.
    /// </summary>
    static OrderCheck TryFront(World world, int slot, bool show)
    {
        var q = world.Characters.Cold(slot)!.OrderQueue;
        while (q.Front is { } front)
        {
            var check = CanRun(world, slot, front);
            bool first = !front.Tried;
            front.Tried = true;
            if (check.Verdict == OrderVerdict.Gone)
            {
                q.PopFront();
                continue;
            }
            if (check.Verdict == OrderVerdict.Blocked)
            {
                if (show && first) Show(world, slot, check);
                return check;
            }
            Ai.OrderStep.TakeOver(world, slot, world.Characters.Cold(slot)!, front.Interrupts);
            front.Started = true;
            front.RowRuns = ListTests.OrderNeedsFailNow(world, slot, front) == false;   // else a step of its plan comes first
            front.Owner.Start(world, slot, front);
            if (q.Front != front || Runs(world, slot, front)) return check;
            q.PopFront();
        }
        return OrderCheck.Gone;
    }

    /// <summary>
    /// A move's walk reached its point (the character's own partition): the move leaves the queue; a queued move after it starts at once, as the
    /// move-only queue did (the path is asked for next tick). False when the walk was no queued order's move.
    /// </summary>
    internal static bool Arrived(CharacterCold cold, ref CharacterHot n)
    {
        var q = cold.OrderQueue;
        if (!cold.OrderMove || q.Front is not { IsMove: true, Started: true }) return false;
        q.PopFront();
        if (q.Front is not { IsMove: true } next) return false;
        next.Started = next.Tried = true;
        next.RowRuns = false;
        n.Goal = new Vector2(next.Point.X, next.Point.Z);
        n.Flags |= (ushort)MoveFlags.NeedPath;
        return true;
    }

    /// <summary>
    /// A move's walk found no path or was given up blocked (the character's own partition): the move leaves the queue and the rest stays, the next
    /// tried at the decision step (state 1 holds once the mover has no path, <c>FUN_14065edb0</c>, so the original's order step pops the front
    /// alone). False when the walk was no queued order's move.
    /// </summary>
    internal static bool WalkFailed(CharacterCold cold)
    {
        var q = cold.OrderQueue;
        if (!cold.OrderMove || q.Front is not { IsMove: true, Started: true }) return false;

        q.PopFront();
        DecisionSystem.Request(cold);
        return true;
    }

    /// <summary>
    /// <see cref="TaskStart.Start"/>'s hook, before the new start: a start from another source than an order replaces the running front order, which
    /// leaves the queue as <see cref="Settle"/> drops it (a step of a follow row keeps its provenance and is no replacement).
    /// </summary>
    internal static void Replacing(World world, int slot, in TaskRequest request, bool step)
    {
        if (request.Source == TaskSource.Order || step) return;
        var q = world.Characters.Cold(slot)!.OrderQueue;
        if (q.Front is not { } front || !Runs(world, slot, front)) return;

        Leave(world, slot, front);
    }

    /// <summary>
    /// <see cref="TaskStart.End"/>'s hook, before the end: the running front order's task is done (<see cref="TaskEnd.Done"/>: it leaves the queue,
    /// the next starts at the decision step) or stopped from outside (<see cref="TaskEnd.Stopped"/>: the queue is dropped, as a stop always dropped
    /// the queued moves).
    /// </summary>
    internal static void Ending(World world, int slot, TaskEnd reason)
    {
        if (reason == TaskEnd.Replaced) return;
        var q = world.Characters.Cold(slot)!.OrderQueue;
        if (q.Front is not { } front || !Runs(world, slot, front)) return;

        if (reason == TaskEnd.Stopped) q.Clear();
        else
            Leave(world, slot, front);
    }

    /// <summary>
    /// Shows a blocked check's failure text: posted as a message, or barked over the character; nothing without one, and nothing for a character
    /// not of the player's faction or an animal (the order step's vfunc <c>0x2d8</c> call, docs/game/ai-tasks.md "Precedence": its blocked order
    /// waits silently).
    /// </summary>
    static void Show(World world, int slot, in OrderCheck check)
    {
        var cold = world.Characters.Cold(slot)!;
        bool says = DestinationAccess.PlayerSide(world, cold) && !Factions.SquadRegistry.IsAnimal(cold);
        if (check.Why is not { } why || !says) return;

        if (check.Posted) world.Post(why);
        else
            world.Bark(world.Characters.IdOf(slot), why);
    }

    /// <summary>
    /// The decision step's source 1 before it is asked (<c>checkOrders</c> pops the front when done, or when its desire x finder is 0; serial): a
    /// front whose task ended leaves, gone ones leave; a running front whose row needs no melee threat (<see cref="NeedsNoThreat"/>) ends when one
    /// comes, or whose start conditions fail (<see cref="IOrderOwner.StillHolds"/>), and waits unstarted (its plan fails, so the character falls
    /// through: S3, the turret gunner). A Loot order's threat test is its task's,
    /// once at the body (<c>CarrySystem.HoldWindow</c>), not the walk's.
    /// </summary>
    internal static void Prepare(World world, int slot)
    {
        var q = world.Characters.Cold(slot)!.OrderQueue;
        PopDone(world, slot);
        while (q.Front is { } front)
        {
            if (front.Started)
            {
                if (!Runs(world, slot, front))
                {
                    // A follow order whose fight was a step of its row (31, 45) takes up its follow again once the fight is over.
                    var cold = world.Characters.Cold(slot)!;
                    if (cold.Running is { Source: TaskSource.Order } && MovementSystem.FightStepOver(world.Characters, slot, cold)
                            && MovementSystem.FollowAgain(world, slot, cold))
                        return;
                    Settle(world, slot);
                    continue;
                }
                bool unmet = !front.Owner.StillHolds(world, slot, front);
                if (unmet || Threatened(world, slot, front.Row))
                {
                    front.Started = false;   // waits in the queue: its end below is not the order's
                    if (unmet) front.Tried = false;
                    TaskStart.End(world, slot, TaskEnd.Stopped);
                }
                return;
            }
            if (CanRun(world, slot, front).Verdict != OrderVerdict.Gone) return;
            q.PopFront();
        }
    }

    /// <summary>
    /// The order step and the plan step on the front order (<c>FUN_14050d970</c>, then <c>checkOrders</c>' first pop <c>FUN_140510920</c>,
    /// <b>Verified</b>; docs/game/ai-tasks.md "Finished tests"). The plan step ends a running front's task when its row is done
    /// (<see cref="ListTests.OrderDone"/>, slot 4 asked of the running task) or its list B fails (<see cref="ListTests.OrderNeedsFail"/>), and
    /// the end pops the order (<c>FUN_14050d7d0</c>); a move's walk goes on (<c>Task_Move</c>'s end, Tasker slot 3 <c>FUN_1403547b0</c>, stops the
    /// mover only once it is done, so a move left within 12 of its point walks on unless the next order's start takes it over). The order step drops
    /// the entry alone when its desire is 0 or less (<see cref="OrderDesire"/>), the act-out going on to its own end (a lift past the attach to its
    /// clip's 0.95), and a front not yet started whose row is done.
    /// </summary>
    static void PopDone(World world, int slot)
    {
        var q = world.Characters.Cold(slot)!.OrderQueue;
        if (Step(world, slot) is not { } step) return;

        if (step.EndsTask && !step.Front.IsMove) TaskStart.End(world, slot, TaskEnd.Done);
        if (q.Front == step.Front) q.PopFront();
    }

    /// <summary>
    /// Whether the front order leaves at this AI update (<see cref="PopDone"/>'s tests; serial, it records <see cref="OrderEntry.RowRuns"/>): the
    /// decision step asks it of every character with a front on every tick it steps, as the original's order step runs on every AI update.
    /// </summary>
    internal static bool PopsNow(World world, int slot) =>
        world.Characters.Cold(slot)!.OrderQueue.Front is not null && Step(world, slot) is not null;

    /// <summary>
    /// The front's pop, if it leaves now: whether it ends its running task with it (the plan step's) or only leaves the queue (the order step's).
    /// </summary>
    static (OrderEntry Front, bool EndsTask)? Step(World world, int slot)
    {
        var q = world.Characters.Cold(slot)!.OrderQueue;
        if (q.Front is not { } front || front.Started && !Runs(world, slot, front)) return null;

        bool running = front.Started;
        bool ends = running && (ListTests.OrderDone(world, slot, front, true) || RowEnds(world, slot, front));
        if (ends) return (front, true);

        bool leaves = OrderDesire.Of(world, slot, front) is <= 0 || !running && ListTests.OrderDone(world, slot, front, false);
        if (leaves) return (front, false);

        return null;
    }

    /// <summary>
    /// Whether the running front's row ends by its list B now (<see cref="ListTests.OrderNeedsFail"/>), after it held since the start
    /// (<see cref="OrderEntry.RowRuns"/>, recorded here). While a step of another row runs for an owner of several steps (<see cref="IOrderSteps"/>:
    /// a provider of the plan, the arrest's fight or strip), or none between two steps, the row's own list B is not asked.
    /// </summary>
    static bool RowEnds(World world, int slot, OrderEntry front)
    {
        var cold = world.Characters.Cold(slot)!;
        if (front.Owner is IOrderSteps && cold.Running?.Row != front.Row) return false;
        if (ListTests.OrderNeedsFail(world, slot, front) is not { } fails) return false;
        if (!fails) front.RowRuns = true;

        return fails && front.RowRuns;
    }

    /// <summary>
    /// Whether the character's follow order (31, 45, 289) runs its own row: its list B has held since its start, so a threat to the followed
    /// character (16) or its going down (7) ends the order (<see cref="RowEnds"/>) instead of making a fight a step of it, as the original's plan
    /// step does (<c>FUN_14050d970</c>, <b>Verified</b> in the running game). A pure read.
    /// </summary>
    public static bool FollowRowRuns(CharacterCold cold) =>
        cold.Running is { Source: TaskSource.Order } run && Production.PermaJobs.Follows(run.Row)
        && cold.OrderQueue.Front is { Started: true, RowRuns: true };

    /// <summary>
    /// The decision step's start of source 1: the front starts (<see cref="TryFront"/>).
    /// </summary>
    internal static void StartFront(World world, int slot) => TryFront(world, slot, show: true);

    /// <summary>
    /// The decision step went past a front that cannot run: its failure text is shown the first time it is tried, and it stays queued.
    /// </summary>
    internal static void Refused(World world, int slot)
    {
        if (world.Characters.Cold(slot)!.OrderQueue.Front is not { Started: false, Tried: false } front) return;
        front.Tried = true;
        Show(world, slot, CanRun(world, slot, front));
    }

    /// <summary>
    /// Whether the character's order wins the decision step's source 1 now (a pure read): its front runs, or can start. A character whose only order
    /// is blocked falls through to its goals and jobs (the original's chooser goes on).
    /// </summary>
    public static bool Wins(World world, int slot)
    {
        if (world.Characters.Cold(slot)!.OrderQueue.Front is not { } front) return false;
        return front.Started ? Runs(world, slot, front) : CanRun(world, slot, front).CanRun;
    }
}