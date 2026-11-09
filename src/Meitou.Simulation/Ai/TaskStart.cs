using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The task repertoires a character plans with (<c>FUN_14060e840</c>, <b>Verified</b>, docs/game/ai-tasks.md "Planner"): the AI's mode flags are
/// tested in the order 4 imprisoned, 0x40 crippled, 0x10 chained, 1 human, 2 platoon, else 0x20 animal. Each <see cref="TaskDataRow.Category"/> is
/// the set of repertoires that register the row.
/// </summary>
public static class Repertoire
{
    public const int Human = 1, Platoon = 2, Imprisoned = 4, Chained = 0x10, Animal = 0x20, Crippled = 0x40;

    /// <summary>
    /// The mode flag a cage sets on entry (<c>FUN_140330600</c>): <see cref="Chained"/> when the cage's functionality restrains movement (pole,
    /// peeler), else <see cref="Imprisoned"/>; 0 outside a cage.
    /// </summary>
    public static int Confinement(World world, CharacterCold cold) =>
        cold is { IsImprisoned: true, InWhat: { } cage } ? world.Fixtures.Find(cage) is { Restrains: true } ? Chained : Imprisoned : 0;

    /// <summary>
    /// The repertoire of a character, the first of: caged (<see cref="Imprisoned"/>), prone (<see cref="Crippled"/>: the posture setter
    /// <c>FUN_1405c7e20</c> sets the bit for posture 1 staying low and 2 crippled and clears it for the others, <b>Verified</b>; <see
    /// cref="Bodies.MedicalState.StayingLow"/>, <see cref="Bodies.MedicalState.Crawling"/>), restrained (<see cref="Chained"/>), a human (<see
    /// cref="Human"/>), else <see cref="Animal"/>. The platoon repertoire is not modelled.
    /// </summary>
    public static int Of(World world, CharacterCold cold)
    {
        int confined = Confinement(world, cold);
        if (confined == Imprisoned) return Imprisoned;
        if (cold.Medical is { Crawling: true } or { StayingLow: true }) return Crippled;
        if (confined == Chained) return Chained;
        return SquadRegistry.IsAnimal(cold) ? Animal : Human;
    }
}

/// <summary>
/// Which source of the original's chooser (<c>AITaskSytem::chooseGoal</c> <c>FUN_140510ef0</c>, docs/game/ai-tasks.md "Precedence") a task start
/// stands in for.
/// </summary>
public enum TaskSource : byte
{
    /// <summary>
    /// A player's order (the order queue).
    /// </summary>
    Order,
    /// <summary>
    /// A goal: Self preservation's or Hold's fight, a package goal's.
    /// </summary>
    Goal,
    /// <summary>
    /// A job of the character's list (<see cref="RunningTask.Job"/> its index), or a job pass.
    /// </summary>
    Job,
    /// <summary>
    /// An engine answer to an attack (<see cref="RetaliationSystem"/>).
    /// </summary>
    Reaction,
    /// <summary>
    /// An NPC's engine start (healing, leader rescue, looting, a kidnapper's or a guard's lift).
    /// </summary>
    Npc,
}

/// <summary>
/// The result of <see cref="TaskStart.Start"/>.
/// </summary>
public enum StartResult : byte
{
    Started,
    /// <summary>
    /// The character's <see cref="Repertoire"/> does not register the row; nothing changed.
    /// </summary>
    NotEligible,
}

/// <summary>
/// Why a task ends (<see cref="ITaskHandler.End"/>).
/// </summary>
public enum TaskEnd : byte
{
    /// <summary>
    /// Another task starts: the act-out lets go of what it holds; the new start sets the movement and the task.
    /// </summary>
    Replaced,
    /// <summary>
    /// The Stop order: the act-out lets go and the character stands, idle.
    /// </summary>
    Stopped,
    /// <summary>
    /// The act-out ended by itself (its work is done, or it cannot go on): it lets go, and the character goes back to the task its provenance names
    /// (<see cref="RunningTask.Resume"/>; one the decision step decides stands idle; engine; the end also asks the decision step to decide again,
    /// <see cref="DecisionSystem.Request(CharacterCold)"/>).
    /// </summary>
    Done,
}

/// <summary>
/// A task start (<see cref="TaskStart.Start"/>): the act-out <see cref="Task"/>, the original's <see cref="Row"/> it runs (the row the repertoire is
/// tested on) and its <see cref="Source"/>, with the job index, target character, site and point the act-out reads, and the task it goes back to when
/// it ends by itself (<see cref="Resume"/>; engine: the characters the decision step does not decide).
/// </summary>
public readonly record struct TaskRequest(CharacterTask Task, TaskType Row, TaskSource Source)
{
    public int Job { get; init; } = -1;
    public CharacterId Target { get; init; } = CharacterId.None;
    public string? Site
    {
        get;
        init;
    }
    public System.Numerics.Vector3 Point
    {
        get;
        init;
    }
    public CharacterTask Resume
    {
        get;
        init;
    }

    /// <summary>
    /// An implicit job (a squad AI option, <see cref="SquadAi"/>: 242, 153, 275) rather than a job of the list (<see cref="TaskSource.Job"/> with
    /// <see cref="Job"/> -1).
    /// </summary>
    public bool Implicit
    {
        get;
        init;
    }

    /// <summary>
    /// The act-out's own parameters the fields above do not hold (a shackle job, a ground item, a warden's round); its handler reads them.
    /// </summary>
    public object? Detail
    {
        get;
        init;
    }

    /// <summary>
    /// An engine walk's goal (X/Z): a <see cref="CharacterTask.GoTo"/> goes there as given (<see cref="MovementSystem.GoTo"/>); without one it goes
    /// onto the floor at <see cref="Point"/>.
    /// </summary>
    public System.Numerics.Vector2? Goal
    {
        get;
        init;
    }

    /// <summary>
    /// The speed mode of a walk (null: the character's run speed).
    /// </summary>
    public byte? Mode
    {
        get;
        init;
    }
}

/// <summary>
/// The provenance of a character's task (<see cref="CharacterCold.Running"/>), recorded by <see cref="TaskStart.Start"/>: the act-out it started
/// (<see cref="Task"/>), the row, the source, the job index (or <see cref="Implicit"/>), the target or site, the tick and the task it goes back to
/// when it ends by itself (<see cref="Resume"/>). It speaks for the task only while <see cref="Task"/> is the character's task (<see cref="Runs"/>);
/// a follow row's (31, 45, 289) is kept through the fight that is a step of it (<see cref="TaskStart.Start"/>). Not saved (tasks are not); hashed
/// only as the markers it replaced were.
/// </summary>
public sealed record RunningTask(CharacterTask Task, TaskType Row, TaskSource Source, int Job, CharacterId Target, string? Site, long Tick)
{
    /// <summary>
    /// The task the act-out goes back to when it ends by itself (engine): the AI's and the engine's characters only; one the decision step decides
    /// stands idle and is decided again.
    /// </summary>
    public CharacterTask Resume
    {
        get;
        init;
    }

    /// <summary>
    /// Started by an implicit job of the squad AI options (<see cref="TaskRequest.Implicit"/>).
    /// </summary>
    public bool Implicit
    {
        get;
        init;
    }

    /// <summary>
    /// Whether this is the provenance of <paramref name="task"/>, the character's task now (a step of the same act-out counts, <see
    /// cref="TaskStart.ActOut"/>).
    /// </summary>
    public bool Runs(CharacterTask task) => TaskStart.ActOut(Task) == TaskStart.ActOut(task);
}

/// <summary>
/// The act-out of one <see cref="CharacterTask"/>, registered by its owning system (<see cref="ITaskOwner"/>, <see cref="World.Tasks"/>). Serial
/// phases only.
/// </summary>
public interface ITaskHandler
{
    /// <summary>
    /// Starts the act-out: the start is eligible, the fight and the task before it have ended and the provenance is recorded.
    /// </summary>
    void Start(World world, int slot, in TaskRequest request);

    /// <summary>
    /// Ends the act-out <paramref name="task"/> of the character.
    /// </summary>
    void End(World world, int slot, CharacterTask task, TaskEnd reason);

    /// <summary>
    /// Whether the act-out has nothing left to do (the original's "plan finished" replan trigger): by default, once its system has taken the
    /// character off it.
    /// </summary>
    bool Done(World world, int slot, CharacterTask task) => world.Characters.Next[slot].Task != (byte)task;
}

/// <summary>
/// A system that owns act-outs registers their handlers when the world is made.
/// </summary>
public interface ITaskOwner
{
    void RegisterTasks(TaskHandlers tasks);
}

/// <summary>
/// The act-out handlers by <see cref="CharacterTask"/> (<see cref="World.Tasks"/>): an open list each owning system fills (<see cref="ITaskOwner"/>)
/// and later code may add to or replace. A task with no handler is started by setting it. A row may have a handler of its own, which takes the row's
/// starts and ends from the task's handler: the lock, door, cage, item and bed rows a walk acts out (<see cref="CharacterTask.GoTo"/>) are their
/// systems', not the movement's.
/// </summary>
public sealed class TaskHandlers
{
    readonly ITaskHandler?[] _handlers = new ITaskHandler?[256];
    readonly Dictionary<TaskType, ITaskHandler> _rows = [];

    public void Register(CharacterTask task, ITaskHandler handler) =>
        _handlers[(byte)task] = handler ?? throw new ArgumentNullException(nameof(handler));

    /// <summary>
    /// Registers the handler of the starts of <paramref name="row"/>, whatever their act-out.
    /// </summary>
    public void Register(TaskType row, ITaskHandler handler) => _rows[row] = handler ?? throw new ArgumentNullException(nameof(handler));

    public ITaskHandler? Of(CharacterTask task) => _handlers[(byte)task];

    /// <summary>
    /// The handler of a start of <paramref name="row"/> acting out <paramref name="task"/>: the row's own, else the task's.
    /// </summary>
    public ITaskHandler? Of(CharacterTask task, TaskType row) => _rows.TryGetValue(row, out var own) ? own : _handlers[(byte)task];

    /// <summary>
    /// The handler of the character's task <paramref name="task"/>: the one of its running row when its provenance runs it (<see
    /// cref="RunningTask.Runs"/>), else the task's.
    /// </summary>
    public ITaskHandler? Of(CharacterCold cold, CharacterTask task) => cold.Running is { } run && run.Runs(task) ? Of(task, run.Row) : Of(task);
}

/// <summary>
/// The one task start and end path (docs/simulation.md "Task eligibility", "Task starts").
/// </summary>
public static class TaskStart
{
    /// <summary>
    /// Whether the character's <see cref="Repertoire"/> registers the row (<c>Category &amp; repertoire != 0</c>). A type with no TaskData row (the
    /// 37 named-only types) has no registration to test and is allowed. Every start has a row: Meitou's <see cref="CharacterTask"/> is the act-out,
    /// the row the <see cref="TaskType"/> (a pick-up is 225 for the order, 148 for the rescue job).
    /// </summary>
    public static bool Eligible(World world, CharacterCold cold, TaskType row) =>
        TaskDataTable.Of(row) is not { } data || (data.Category & Repertoire.Of(world, cold)) != 0;

    /// <summary>
    /// Starts a task (serial phases only): the row must be <see cref="Eligible"/>; then a fight ends (a new task drops the fight task,
    /// docs/game/combat.md "Fight lifecycle and orders"), the task before ends through its handler, the provenance is recorded (<see
    /// cref="CharacterCold.Running"/>) and the act-out starts. A fight the character did not take up by an order or a job, while it runs a follow row
    /// (31, 45, 289), is a step of that row (31 and 45 need state 16, which 13 "Attacking attackers of" gives; docs/game/ai-tasks.md "Precedence"):
    /// the row's provenance is kept, so the follow is taken up again after it.
    /// </summary>
    public static StartResult Start(World world, int slot, in TaskRequest request)
    {
        var table = world.Characters;
        if (table.Phase == TablePhase.Parallel) throw new InvalidOperationException("tasks start in the serial phases only");
        var cold = table.Cold(slot)!;
        if (!Eligible(world, cold, request.Row)) return StartResult.NotEligible;
        var current = (CharacterTask)table.Next[slot].Task;
        var handler = world.Tasks.Of(request.Task, request.Row);
        bool step = cold.Running is { } kept && Step(kept, current, request);
        PlayerOrders.Replacing(world, slot, request, step);
        if (request.Task != CharacterTask.Attack) world.Tasks.Of(CharacterTask.Attack)?.End(world, slot, CharacterTask.Attack, TaskEnd.Replaced);
        if (current != CharacterTask.Attack && world.Tasks.Of(cold, current) is { } before
            && (current != request.Task || before != handler))
            before.End(world, slot, current, TaskEnd.Replaced);
        cold.Running = step && cold.Running is { } running
            ? running with { Task = request.Task, Tick = world.Tick }
            : new RunningTask(request.Task, request.Row, request.Source, request.Job, request.Target, request.Site, world.Tick)
            {
                Resume = request.Resume,
                Implicit = request.Implicit
            };
        if (handler is not null) handler.Start(world, slot, request);
        else
        {
            ref var n = ref table.Next[slot];
            n.Task = (byte)request.Task;
            n.TaskTime = 0;
        }
        Stamp(table, slot);
        return StartResult.Started;
    }

    /// <summary>
    /// Starts an engine walk to <paramref name="goal"/> at <paramref name="mode"/> as row <paramref name="row"/> (<see cref="Start"/> of a <see
    /// cref="CharacterTask.GoTo"/> with <see cref="TaskRequest.Goal"/>).
    /// </summary>
    public static StartResult Walk(World world, int slot, System.Numerics.Vector2 goal, byte mode, TaskType row, TaskSource source) =>
        Start(world, slot, new TaskRequest(CharacterTask.GoTo, row, source) { Goal = goal, Mode = mode });

    /// <summary>
    /// Starts an engine walk onto the floor at <paramref name="at"/> (<see cref="MovementSystem.GoToFloor"/>), as <see cref="Walk(World, int,
    /// System.Numerics.Vector2, byte, TaskType, TaskSource)"/>.
    /// </summary>
    public static StartResult Walk(World world, int slot, System.Numerics.Vector3 at, byte mode, TaskType row, TaskSource source) =>
        Start(world, slot, new TaskRequest(CharacterTask.GoTo, row, source) { Point = at, Mode = mode });

    /// <summary>
    /// Starts an engine standing task (<see cref="CharacterTask.Idle"/>, <see cref="CharacterTask.Wander"/>, or a <see cref="CharacterTask.Follow"/>
    /// of the squad's leader) as row <paramref name="row"/>: the character stops and takes it up (the movement's handler). One the repertoire does
    /// not register gives way to 14 Idleness, which every repertoire registers (a crippled character does not wander, 24).
    /// </summary>
    public static StartResult Become(World world, int slot, CharacterTask task, TaskType row, TaskSource source) =>
        Start(world, slot, new TaskRequest(task, row, source)) is var started && started == StartResult.NotEligible && task != CharacterTask.Idle
            ? Start(world, slot, new TaskRequest(CharacterTask.Idle, TaskType.Idle, source)) : started;

    /// <summary>
    /// A start in a parallel phase, on the caller's own slot (the AI's think): it records the provenance and starts the walk to <see
    /// cref="TaskRequest.Goal"/>, or stops the character on the standing task (its <see cref="CharacterHot.TaskTime"/> restarted); nothing outside
    /// the slot is touched, so no fight or task before it is ended (the AI acts out nothing for a fighting character, and its walks and standing
    /// tasks hold nothing) and no handler runs. The row's repertoire is not tested: the AI's planner chose the row (<see cref="Ai.AiSystem"/>).
    /// </summary>
    public static void StartOwn(World world, int slot, in TaskRequest request)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        cold.Running = new RunningTask(request.Task, request.Row, request.Source, request.Job, request.Target, request.Site, world.Tick)
        {
            Resume = request.Resume,
            Implicit = request.Implicit
        };
        if (request.Goal is { } goal) MovementSystem.GoTo(table, slot, goal, request.Mode ?? cold.RunSpeed);
        else
        {
            MovementSystem.Stop(table, slot);
            ref var n = ref table.Next[slot];
            n.Task = (byte)request.Task;
            n.TaskTime = 0;
        }
        Stamp(table, slot);
    }

    /// <summary>
    /// The act-out's own system moves its character on (any phase, its own slot): to another task of the act-out, or back to a standing task once its
    /// work is over, keeping the provenance (which then no longer runs the task). Only <see cref="CharacterHot.Task"/> is written.
    /// </summary>
    public static void Step(CharacterTable table, int slot, CharacterTask task)
    {
        table.Next[slot].Task = (byte)task;
        Stamp(table, slot);
    }

    /// <summary>
    /// The character stops and stands idle, keeping its provenance (any phase, its own slot; <see cref="MovementSystem.Stop"/>): a walk the act-out's
    /// system ends at its goal or gives up, a knock-out. No handler runs: the act-out's own system lets go of what it holds.
    /// </summary>
    public static void Stand(CharacterTable table, int slot)
    {
        MovementSystem.Stop(table, slot);
        Stamp(table, slot);
    }

    /// <summary>
    /// Whether the character walks for its AI (a goal's act-out, <see cref="TaskSource.Goal"/>): a walk another system may stop with <see
    /// cref="Stand"/>, the AI walking on once it decides again (the stop counts as neither arrival nor failure).
    /// </summary>
    public static bool OnAiWalk(CharacterTable table, int slot) =>
        table.Next[slot].Task == (byte)CharacterTask.GoTo && table.Cold(slot)!.Running is { Source: TaskSource.Goal };

    /// <summary>
    /// The act-out's own system walks its character on (any phase, its own slot; <see cref="MovementSystem.GoTo"/>), keeping the provenance: a walk
    /// to the act-out's next spot.
    /// </summary>
    public static void StepTo(CharacterTable table, int slot, System.Numerics.Vector2 goal, byte mode)
    {
        MovementSystem.GoTo(table, slot, goal, mode);
        Stamp(table, slot);
    }

    /// <summary>
    /// Debug builds: the task the start path has just set (<see cref="CheckStarts"/>).
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    static void Stamp(CharacterTable table, int slot)
    {
        var cold = table.Cold(slot)!;
        cold.TaskSetTick = table.Ticks;
        cold.TaskSet = table.Next[slot].Task;
    }

    /// <summary>
    /// The task a start of <paramref name="task"/> goes back to: when the character runs that act-out already (a medic changing patients, a builder
    /// changing sites) the running one's, else <paramref name="resume"/>.
    /// </summary>
    public static CharacterTask ResumeOf(World world, int slot, CharacterTask task, CharacterTask resume)
    {
        var current = (CharacterTask)world.Characters.Next[slot].Task;
        return ActOut(current) == ActOut(task) && world.Characters.Cold(slot)!.Running is { } run && run.Runs(current) ? run.Resume : resume;
    }

    /// <summary>
    /// The standing task an engine start of a character on <paramref name="task"/> goes back to (<see cref="RunningTask.Resume"/>): wandering or
    /// following when it does that, else idle; idle for a character the decision step decides (its routine is the step's, step 6).
    /// </summary>
    public static CharacterTask Standing(CharacterCold cold, CharacterTask task) =>
        !DecisionSystem.Decides(cold) && task is CharacterTask.Wander or CharacterTask.Follow ? task : CharacterTask.Idle;

    static bool Step(RunningTask running, CharacterTask current, in TaskRequest request) =>
        request.Task == CharacterTask.Attack && request.Source is TaskSource.Goal or TaskSource.Reaction && Production.PermaJobs.Follows(running.Row)
        && running.Runs(current) && current is CharacterTask.Follow or CharacterTask.Attack;

    /// <summary>
    /// The act-out a task is a step of: a builder's delivering, fetching and dismantling are steps of its work (<see cref="CharacterTask.Build"/>;
    /// the construction system switches between them by itself); any other task is its own.
    /// </summary>
    public static CharacterTask ActOut(CharacterTask task) =>
        task is CharacterTask.DeliverMaterials or CharacterTask.FetchMaterials or CharacterTask.Dismantle ? CharacterTask.Build : task;

    /// <summary>
    /// Debug builds, at the end of a tick: a character whose task changed this tick had it set by the start path (<see cref="Start"/>, <see
    /// cref="End"/>, <see cref="StartOwn"/>, <see cref="Step"/>, <see cref="Stand"/>: the task it stamped is the task now) or moved on to another
    /// step of its act-out (<see cref="ActOut"/>); a direct write of <see cref="CharacterHot.Task"/> outside them throws. A character spawned or
    /// loaded this tick is not checked.
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    internal static void CheckStarts(World world)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!next[i].Alive || !prev[i].Alive || prev[i].Generation != next[i].Generation || next[i].Task == prev[i].Task) continue;
            var cold = table.Cold(i)!;
            if (cold.TaskSetTick == table.Ticks && cold.TaskSet == next[i].Task) continue;
            // a step of the act-out
            if (ActOut((CharacterTask)next[i].Task) == ActOut((CharacterTask)prev[i].Task)
                && cold.Running is { } step && step.Runs((CharacterTask)prev[i].Task))
                continue;

            throw new InvalidOperationException(
                $"slot {i} went from {(CharacterTask)prev[i].Task} to {(CharacterTask)next[i].Task} at tick {world.Tick} outside the start path");
        }
    }

    /// <summary>
    /// Ends the character's task (serial phases only) and drops its provenance. <see cref="TaskEnd.Stopped"/> (from outside the act-out): the fight
    /// and the task end through their handlers and the character stands, idle (<see cref="MovementSystem.Stop"/>). <see cref="TaskEnd.Done"/> (the
    /// act-out's own end: a treatment, carry, site's work, machine's work, haul trip or prospecting): the task's handler lets go, the character
    /// stands and takes up the task its provenance goes back to (<see cref="RunningTask.Resume"/>; idle when the provenance does not run the task). A
    /// character the decision step decides stands idle: the step's next decision (the end asks for one) puts it on its routine (step 6), as the
    /// original re-plans.
    /// </summary>
    public static void End(World world, int slot, TaskEnd reason)
    {
        var table = world.Characters;
        if (table.Phase == TablePhase.Parallel) throw new InvalidOperationException("tasks end in the serial phases only");
        var cold = table.Cold(slot)!;
        PlayerOrders.Ending(world, slot, reason);
        var current = (CharacterTask)table.Next[slot].Task;
        var resume = !DecisionSystem.Decides(cold) && cold.Running is { } run && run.Runs(current) ? run.Resume : CharacterTask.Idle;
        if (reason != TaskEnd.Done) world.Tasks.Of(CharacterTask.Attack)?.End(world, slot, CharacterTask.Attack, reason);
        if (current != CharacterTask.Attack) world.Tasks.Of(cold, current)?.End(world, slot, current, reason);
        if (reason != TaskEnd.Replaced) MovementSystem.Stop(table, slot);
        if (reason == TaskEnd.Done)
        {
            ref var n = ref table.Next[slot];
            n.Task = (byte)resume;
            n.TaskTime = 0;
        }
        cold.Running = null;
        DecisionSystem.Request(cold);   // the plan finished or was stopped: the character is decided again
        Stamp(table, slot);
    }
}