using System.Numerics;
using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

/// <summary>What a plan step acts on: a kind (<see cref="None"/>: no target) and the place. Two targets are the same when both agree.</summary>
public readonly record struct PlanTarget(byte Kind, Vector3 Position)
{
    public const byte None = 0;
    public bool IsNone => Kind == None;
}

/// <summary>One step of a plan: the row's type and its target.</summary>
public readonly record struct PlanStep(TaskType Type, PlanTarget Target);

/// <summary>What the planner asks of the world about one character (docs/simulation.md "Planner").</summary>
public interface IPlanWorld
{
    /// <summary>The category registry it plans with (a registration flag bit, docs/game/ai-tasks.md "Planner").</summary>
    int Category { get; }
    /// <summary>Whether restricted rows (lockpicking, cutting, opening gates) are offered: the original offers them to a flagged search (the orders step) or when the acting character's faction is not the player's (<c>FUN_14051bae0</c>). Named for an older reading (no squad).</summary>
    bool Squadless { get; }
    /// <summary>The desire of a row the plan passes through, for that target (the goal's own is given to <see cref="TaskPlanner.Plan"/>).</summary>
    float Desire(TaskDataRow row, in PlanTarget target);
    /// <summary>Whether a state has the wanted value for a target.</summary>
    bool Holds(TaskCondition condition, in PlanTarget target);
    /// <summary>The row's target finder (only called for rows with one): the target, <see cref="PlanTarget.None"/> when it found nothing.</summary>
    PlanTarget Find(TaskDataRow row, in PlanTarget target);
}

/// <summary>
/// The original's task planner (docs/game/ai-tasks.md "Planner", Observed): a best-first search from a goal row through the rows whose list A achieves
/// its effect or an unmet list-B state, taking the open node of the highest g + h (g the least desire on the way, h the node's own), until a node that
/// acts and can run, or one already queued, is reached. The plan is that node's chain up to the goal, leaf first. One planner per worker thread: it keeps
/// its node buffers between calls.
/// </summary>
public sealed class TaskPlanner
{
    /// <summary>Nodes a search may make before it gives up (engine choice: the original has no cap; the goal rows the data uses plan in a handful).</summary>
    public const int MaxNodes = 64;

    /// <summary>Ancestors looked through for a repeat (the original's 50).</summary>
    const int MaxDepth = 50;

    [ThreadStatic] static TaskPlanner? s_local;

    /// <summary>This thread's planner.</summary>
    public static TaskPlanner Local => s_local ??= new TaskPlanner();

    struct Node
    {
        public TaskDataRow Row;
        public PlanTarget Target, EffectTarget;
        public float G, H;
        public int Parent;
        public bool Closed;
    }

    readonly Node[] _nodes = new Node[MaxNodes];
    readonly List<int> _open = new(MaxNodes);
    int _count;

    /// <summary>
    /// Plans a goal row of a desire for a target; fills <paramref name="plan"/> leaf first and returns its value (the least desire on it, above 0), or 0
    /// when there is no plan. <paramref name="queued"/> is the plan being carried out: a node equal to one of its steps is accepted where it is.
    /// </summary>
    public float Plan(TaskDataRow goal, float desire, PlanTarget target, IPlanWorld world, IReadOnlyList<PlanStep> queued, List<PlanStep> plan)
    {
        plan.Clear();
        _count = 0;
        _open.Clear();
        int root = Make(goal, target, desire, -1);
        if (root < 0) return 0;
        // A goal without a finder that can run is the plan by itself.
        if (!goal.Is(TaskDataFlags.Finder) && CanRun(root, world, out _)) return Accept(root, plan);
        _open.Add(root);
        while (_open.Count > 0)
        {
            int best = 0;
            for (int k = 1; k < _open.Count; k++)
                if (Value(_open[k]) > Value(_open[best])) best = k;
            int at = _open[best];
            _open.RemoveAt(best);
            ref var n = ref _nodes[at];
            n.Closed = true;
            if (Queued(at, queued, world) || Repeats(at)) return Accept(at, plan);
            if (!n.Row.Is(TaskDataFlags.Finder) && n.Row.Is(TaskDataFlags.Acts) && CanRun(at, world, out _)) return Accept(at, plan);
            Expand(at, world);
            if (_count >= MaxNodes) break;
        }
        return 0;
    }

    float Value(int i) => _nodes[i].G + _nodes[i].H;

    int Make(TaskDataRow row, PlanTarget target, float desire, int parent)
    {
        if (_count >= MaxNodes) return -1;
        if (desire <= 0) return -1;   // a dead end: never expanded
        // A row without a finder wants its effect for its own target (engine reading: the original reads the task's effect hand).
        _nodes[_count] = new Node { Row = row, Target = target, EffectTarget = row.Is(TaskDataFlags.Finder) ? default : target, G = desire, H = desire, Parent = parent };
        if (parent >= 0) _nodes[_count].G = MathF.Min(desire, _nodes[parent].G);
        return _count++;
    }

    /// <summary>
    /// Whether the node's list B all holds and its effect, if any, holds for its effect target (none found: false); <paramref name="unmet"/> is the first
    /// B entry that does not hold, else the effect, when false.
    /// </summary>
    bool CanRun(int i, IPlanWorld world, out TaskCondition? unmet)
    {
        ref var n = ref _nodes[i];
        foreach (var c in n.Row.Needs)
            if (!world.Holds(c, n.Target)) { unmet = c; return false; }
        unmet = null;
        if (n.Row.Effect is not { } effect) return true;
        if (n.EffectTarget.IsNone) return false;
        if (world.Holds(effect, n.EffectTarget)) return true;
        unmet = effect;
        return false;
    }

    /// <summary>
    /// Opens the children of the node's first unmet B entry, else of its effect (its finder picks the effect's target first): one per provider of that
    /// state not yet in the search; a restricted provider only when the world offers them, one that needs a target only when there is one.
    /// </summary>
    void Expand(int i, IPlanWorld world)
    {
        ref var n = ref _nodes[i];
        if (n.Row.Effect is not null && n.Row.Is(TaskDataFlags.Finder)) n.EffectTarget = world.Find(n.Row, n.Target);
        TaskCondition? follow = null;
        var target = n.Target;
        foreach (var c in n.Row.Needs)
            if (!world.Holds(c, n.Target)) { follow = c; break; }
        if (follow is null && n.Row.Effect is { } effect)
        {
            if (n.EffectTarget.IsNone) return;   // the finder found nothing: no way to the effect
            (follow, target) = (effect, n.EffectTarget);
        }
        if (follow is not { } state) return;
        foreach (var row in TaskDataTable.Providers(world.Category, state))
        {
            if (row.Is(TaskDataFlags.Restricted) && !world.Squadless) continue;
            if (row.Is(TaskDataFlags.NeedsTarget) && target.IsNone) continue;
            if (Seen(row.Type, target)) continue;
            int child = Make(row, target, world.Desire(row, target), i);
            if (child < 0) { if (_count >= MaxNodes) return; continue; }
            _open.Add(child);
        }
    }

    bool Seen(TaskType type, PlanTarget target)
    {
        for (int k = 0; k < _count; k++)
            if (_nodes[k].Row.Type == type && _nodes[k].Target == target) return true;
        return false;
    }

    /// <summary>
    /// Whether the node is the step being carried out and can still run. The original accepts any queued task and keeps the queue below it, whose steps
    /// end on their own; Meitou's steps have no end of their own, so only the current one is kept, and only while it can run (engine reading).
    /// </summary>
    bool Queued(int i, IReadOnlyList<PlanStep> queued, IPlanWorld world)
    {
        ref var n = ref _nodes[i];
        return queued.Count > 0 && queued[0].Type == n.Row.Type && queued[0].Target == n.Target && CanRun(i, world, out _);
    }

    /// <summary>Whether an ancestor is the same row on the same target (moving and running never count).</summary>
    bool Repeats(int i)
    {
        ref var n = ref _nodes[i];
        if (n.Row.Type is TaskType.MoveOnFreeWill or TaskType.MoveOnFreeWillFast) return false;
        int depth = 0;
        for (int p = n.Parent; p >= 0 && depth < MaxDepth; p = _nodes[p].Parent, depth++)
            if (_nodes[p].Row.Type == n.Row.Type && _nodes[p].Target == n.Target) return true;
        return false;
    }

    /// <summary>The chain from a node up to the goal, leaf first; its value is the least desire on it.</summary>
    float Accept(int i, List<PlanStep> plan)
    {
        float value = 10;
        for (int p = i; p >= 0; p = _nodes[p].Parent)
        {
            plan.Add(new PlanStep(_nodes[p].Row.Type, _nodes[p].Target));
            value = MathF.Min(value, _nodes[p].H);
        }
        return value;
    }
}