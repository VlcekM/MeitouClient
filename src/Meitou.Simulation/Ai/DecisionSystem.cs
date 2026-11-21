using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The steps of the original's chooser (<c>AITaskSytem::chooseGoal</c> <c>FUN_140510ef0</c>, docs/game/ai-tasks.md "Precedence"), in the order they
/// are tried.
/// </summary>
internal enum DecisionStep : byte
{
    /// <summary>
    /// 1: the front of the order queue (<c>checkOrders</c> <c>FUN_140510920</c>).
    /// </summary>
    Orders = 1,
    /// <summary>
    /// 2: a committing current goal of priority above 2 keeps its plan.
    /// </summary>
    Commitment = 2,
    /// <summary>
    /// 3: the urgent job pass (187 Obedient Slave, kind-2 jobs while enemies are sensed).
    /// </summary>
    UrgentJobs = 3,
    /// <summary>
    /// 4: the priority-3 goals, one pick by desire x weight.
    /// </summary>
    Goals = 4,
    /// <summary>
    /// 5: every job in list order, then the implicit jobs.
    /// </summary>
    Jobs = 5,
    /// <summary>
    /// 6: the priority-2 goals (keys 1000 + type), then the priority-1 goals (2000 + type), then the default task (<c>+0x190</c>, 3000 up: 14
    /// Idleness).
    /// </summary>
    Fallback = 6,
}

/// <summary>
/// The chooser's answer from a source or a job contributor for one character (<see cref="IJobSource.CanRun"/>, a pure read): it can run
/// (<see cref="Can"/>; the original's desire x finder above 0), it runs the character's task already (<see cref="Running"/>: the plan goes on,
/// nothing is started), or why not (<see cref="Why"/>: an order's failure text, "no attacker in range"; null: none to show).
/// </summary>
public readonly record struct DecisionCheck(bool Can, bool Running = false, string? Why = null)
{
    /// <summary>
    /// It can run and is not running yet.
    /// </summary>
    public static DecisionCheck Run => new(true);
    /// <summary>
    /// It runs the character's task already: the plan goes on, nothing is started.
    /// </summary>
    public static DecisionCheck Runs => new(true, true);
    /// <summary>
    /// It cannot run, for <paramref name="why"/> (null: nothing to show).
    /// </summary>
    public static DecisionCheck No(string? why) => new(false, false, why);
}

/// <summary>
/// One source of the decision step (<see cref="DecisionSystem"/>): an order queue, a goal, a job. <see cref="CanRun"/> and <see cref="Desire"/> are
/// pure reads of the world (serial phases), so the same answer serves the step, the UI and a command gate; <see cref="Start"/> starts it through
/// <see cref="TaskStart"/> (or a deferred fight). Sources are registered in an open list (<see cref="DecisionSystem.Register"/>), sorted by the
/// original's <see cref="Step"/> and, within it, its <see cref="Key"/>.
/// </summary>
internal interface IDecisionSource
{
    /// <summary>
    /// A short name for the trace ("32 Self preservation").
    /// </summary>
    string Name { get; }
    /// <summary>
    /// The step of the decision it belongs to (the original's step order).
    /// </summary>
    DecisionStep Step { get; }

    /// <summary>
    /// The original's order within the step (the goal list's order, a job's list position); lower first, equal ones in registration order.
    /// </summary>
    int Key { get; }

    /// <summary>
    /// The goal's weight (step 4 scores desire x weight; the current goal x1.25).
    /// </summary>
    float Weight => 1;
    /// <summary>
    /// Whether it can run for the character at <paramref name="slot"/> now (a pure read of the world).
    /// </summary>
    DecisionCheck CanRun(World world, int slot);

    /// <summary>
    /// How much it is wanted (above 0), asked only after <see cref="CanRun"/> said it can run.
    /// </summary>
    float Desire(World world, int slot) => 1;

    /// <summary>
    /// Starts it (serial phases only).
    /// </summary>
    void Start(World world, int slot);

    /// <summary>
    /// It could not run and the step went on (an order shows its failure text once).
    /// </summary>
    void Passed(World world, int slot, in DecisionCheck check) { }
}

/// <summary>
/// A system that contributes decision sources registers them when the world is made (<see cref="World.Decisions"/>).
/// </summary>
internal interface IDecisionOwner
{
    /// <summary>
    /// Registers its decision sources and job contributors with <paramref name="decisions"/>.
    /// </summary>
    void RegisterSources(DecisionSystem decisions);
}

/// <summary>
/// The contributor of the jobs of one or more types (<see cref="ChooserRegistry.Jobs"/>), asked for any character with a job list, player or NPC
/// (docs/modding.md "Chooser goals and jobs"). The chooser's job passes walk the character's list in order (the original's keys N+8, N+7, ...) and
/// ask the contributor of each job's type; the jobs switch, the repertoire and the walk's order are theirs, everything else the contributor's.
/// </summary>
public interface IJobSource
{
    /// <summary>
    /// Whether job <paramref name="k"/> of the character's list can run now (a read of the world: the original's desire x finder above 0);
    /// <see cref="DecisionCheck.Runs"/> when the character's task is that job's already. Called serially, in slot order, at most once per character
    /// per tick before that tick's choice, and again right before <see cref="Start"/>.
    /// </summary>
    DecisionCheck CanRun(World world, int slot, int k);

    /// <summary>
    /// Starts job <paramref name="k"/>, after <see cref="CanRun"/> said it can. Called serially, in slot order, in the tick the job wins or the next.
    /// </summary>
    void Start(World world, int slot, int k);

    /// <summary>
    /// Whether job <paramref name="k"/> is urgent now (the kind-2 jobs while enemies are sensed; <c>FUN_14050e5b0</c>): an urgent job is weighed
    /// before the urgent tier's goals. Called as <see cref="CanRun"/>. Default: never.
    /// </summary>
    bool Urgent(World world, int slot, int k) => false;

    /// <summary>
    /// The character's task is job <paramref name="k"/>'s, which can no longer run, and no other job of the list can: the task ends (the original's
    /// idleness takes over at that choice). Called serially, in slot order, in the tick that is seen or the next. Default: nothing (the act-out ends by
    /// itself).
    /// </summary>
    void Stop(World world, int slot, int k) { }
}

/// <summary>
/// The last decision of a character, filled only while <see cref="DecisionSystem.Tracing"/> is on: the tick, the source that won (or what the step
/// yielded to) and, per source asked, its answer. Display only: not saved, not hashed.
/// </summary>
public sealed record DecisionTrace(long Tick, string Winner, IReadOnlyList<string> Lines);

/// <summary>
/// The host of the chooser's sources (docs/simulation.md "Decision step"; the original's one chooser <c>FUN_140510ef0</c>, <see cref="Ladder"/>):
/// the engine's <see cref="IDecisionSource"/>s and the job contributors (the mods' through <see cref="ChooserRegistry.Jobs"/>), the serial look
/// that asks them before the parallel think (<see cref="Look"/>: no source is ever called from the think), and the serial pass in slot order that
/// starts what the think's ladder picked (<see cref="Run"/>), passing the sources it went past. Every character with a squad runs the ladder; the
/// player's characters (<see cref="Decides"/>) are asked every source, the others the order step and the job passes. The look runs in the schedule
/// step, the starts in the slow world after retaliation, aggro and flight (<see cref="World.RunTick"/>), and orders alone also between ticks
/// (<see cref="World.ApplyPausedInterfaceCommands"/>), serially, as no think runs there.
/// </summary>
public sealed class DecisionSystem
{
    #region Sources

    /// <summary>
    /// The decision sources in the order they are asked (by step, then key).
    /// </summary>
    readonly List<IDecisionSource> _sources = [];
    /// <summary>
    /// The job contributor of each job type.
    /// </summary>
    readonly Dictionary<TaskType, IJobSource> _jobs = [];
    /// <summary>
    /// The code mods' job contributors, caught (<see cref="RegisterModJob"/>).
    /// </summary>
    readonly ModDecisionFaults _modFaults = new();
    /// <summary>
    /// The sources' index ranges by step (<see cref="Steps"/>), made again at each registration.
    /// </summary>
    SourceSteps _steps;

    /// <summary>
    /// Makes the host with its own sources: the order queue, the urgent jobs, the jobs and the default task 14 Idleness.
    /// </summary>
    public DecisionSystem()
    {
        Register(new OrderSource());
        Register(new JobSource(this, urgent: true));
        Register(new JobSource(this, urgent: false));
        Register(new Idleness());
    }

    /// <summary>
    /// The characters every source is asked for: those of the player's faction, the player's own (<see cref="CharacterCold.IsPlayer"/>) and the ones
    /// Meitou makes without that flag, which hold a player-faction mind (<see cref="AiMind.PlayerSide"/>). The sources stand for the original's
    /// player-faction branches (setupAI's own lists, the job pass's implicit jobs, the standing orders); any other character is asked the order step
    /// and the job passes alone (docs/simulation.md "Decision step"). A pure read of the character's own state.
    /// </summary>
    public static bool Decides(CharacterCold cold) => cold.IsPlayer || cold.Ai is { PlayerSide: true };

    /// <summary>
    /// Whether the chooser runs for the character, so its order queue is asked (<see cref="Ai.OrderStep.Runs"/>). A pure read.
    /// </summary>
    internal static bool TakesOrders(CharacterCold cold) => Ai.OrderStep.Runs(cold);

    /// <summary>
    /// Whether the chooser is off for the character (<see cref="Ai.OrderStep.Down"/>). A pure read.
    /// </summary>
    internal static bool Down(CharacterCold cold) => Ai.OrderStep.Down(cold);

    /// <summary>
    /// Whether an NPC's order holds it now (<see cref="Ai.OrderStep.Ordered"/>). Any phase: it reads only the character's own state.
    /// </summary>
    public static bool Ordered(CharacterCold cold) => Ai.OrderStep.Ordered(cold);

    /// <summary>
    /// Registers (or replaces) the engine's contributor of the jobs of <paramref name="type"/> (a job type without one is kept in the list but never
    /// run).
    /// </summary>
    internal void RegisterJob(TaskType type, IJobSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _jobs[type] = source;
    }

    /// <summary>
    /// Registers mod <paramref name="mod"/>'s contributor of the jobs of <paramref name="type"/> (<see cref="ChooserRegistry.Jobs"/>), its calls caught:
    /// one that throws is dropped for this world (<see cref="ModDecisionFaults"/>).
    /// </summary>
    internal void RegisterModJob(TaskType type, IJobSource source, string mod) => _jobs[type] = _modFaults.Wrap(source, mod, type);

    /// <summary>
    /// Takes the contributor of <paramref name="type"/> back (a mod system's registration that threw).
    /// </summary>
    internal void RemoveJob(TaskType type) => _jobs.Remove(type);

    /// <summary>
    /// The contributor of the jobs of <paramref name="type"/>; null when none is registered.
    /// </summary>
    internal IJobSource? JobSourceOf(TaskType type) => _jobs.GetValueOrDefault(type);

    /// <summary>
    /// The sources in the order they are asked.
    /// </summary>
    internal IReadOnlyList<IDecisionSource> Sources => _sources;

    /// <summary>
    /// The sources' index ranges by step, for the ladder (<see cref="Ladder.Run"/>).
    /// </summary>
    internal ref readonly SourceSteps Steps => ref _steps;

    /// <summary>
    /// Record each decision in <see cref="CharacterCold.Decision"/> (a debug toggle; off by default, never hashed).
    /// </summary>
    public bool Tracing { get; set; }

    /// <summary>
    /// Adds a source after those of a lower step or key and after equal ones (the engine's own sources).
    /// </summary>
    internal void Register(IDecisionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        int at = _sources.FindLastIndex(s => s.Step < source.Step || s.Step == source.Step && s.Key <= source.Key);
        _sources.Insert(at + 1, source);
        _steps = new SourceSteps(_sources);
    }

    /// <summary>
    /// Has an engine system register its sources and contributors (<see cref="IDecisionOwner"/>).
    /// </summary>
    internal void RegisterFrom(IDecisionOwner owner) => owner.RegisterSources(this);

    /// <summary>
    /// Adds the code mods' dropped contributors to the state hash; nothing while none has dropped.
    /// </summary>
    internal void HashFaults(ref StateHasher hasher) => _modFaults.Hash(ref hasher);

    #endregion

    #region Replan

    /// <summary>
    /// Asks for the character to be decided again (a replan trigger): any phase, its own slot or a serial one. The player's characters keep the
    /// bit, and an NPC while it holds an order (its order step is asked again; the AI has replan triggers of its own). The next look makes its
    /// chooser due.
    /// </summary>
    public static void Request(CharacterCold cold)
    {
        if (Decides(cold) || cold.OrderQueue.Count > 0) cold.Replan = true;
    }

    /// <summary>
    /// The original's replan request (<c>FUN_140506cf0</c>, <see cref="AiMind.AskReplan"/>), for its triggers alone: the character is decided again
    /// (<see cref="Request(CharacterCold)"/>) and its mind's next think clears an uncommitted goal and chooses without continuity. Meitou's own asks
    /// stay <see cref="Request(CharacterCold)"/> or <see cref="AiMind.Replan"/>. Any phase, its own slot or a serial one.
    /// </summary>
    internal static void Replan(CharacterCold cold)
    {
        Request(cold);
        cold.Ai?.AskReplan();
    }

    /// <summary>
    /// <see cref="Request(CharacterCold)"/> for the character at <paramref name="slot"/>, if any.
    /// </summary>
    public static void Request(World world, int slot)
    {
        if (world.Characters.Cold(slot) is { } cold) Request(cold);
    }

    /// <summary>
    /// Whether the character's task is one the priority-3 goals come before, so a goal that can run takes it over: not a fight, and no start runs
    /// it (idling, a standing task), or it is a job's (list or implicit) or 14 Idleness's. Not an order's, another goal's or the engine's. A pure
    /// read.
    /// </summary>
    public static bool GoalsComeFirst(CharacterTable table, int slot, CharacterCold cold)
    {
        var task = (CharacterTask)table.Next[slot].Task;
        if (task == CharacterTask.Attack || cold.InCombat) return false;
        if (cold.Running is not { } run || !run.Runs(task)) return true;

        return run.Source == TaskSource.Job || run.Row == TaskType.Idle || run.Source == TaskSource.Goal && Idleness.IsDefault(run.Row);
    }

    /// <summary>
    /// The order step's hold of an NPC (<see cref="Ai.OrderStep.Hold"/>; serial phases).
    /// </summary>
    internal static void Hold(World world, int slot) => Ai.OrderStep.Hold(world, slot);

    #endregion

    #region Look

    /// <summary>
    /// The think's turn period (the AI's <see cref="AiSettings.ThinkEveryTicks"/>, <see cref="Bind"/>; its default until bound).
    /// </summary>
    int _thinkEvery = new AiSettings().ThinkEveryTicks;
    /// <summary>
    /// Each slot's look (<see cref="ChooserRow"/>), reused tick to tick; only the rows of this tick count.
    /// </summary>
    ChooserRow?[] _rows = [];
    /// <summary>
    /// Whether this tick's look made any row (the pass for characters without a mind runs only then).
    /// </summary>
    bool _anyRow;
    /// <summary>
    /// The trace lines of the character being looked at (kept only while <see cref="Tracing"/> is on).
    /// </summary>
    readonly List<string> _lines = [];

    /// <summary>
    /// Takes the AI's think period (<paramref name="thinkEvery"/>), once when the world is made.
    /// </summary>
    internal void Bind(int thinkEvery) => _thinkEvery = thinkEvery;

    /// <summary>
    /// The game seconds between two think turns of a character at the world's speed: the engine's stand-in for one AI update of the original.
    /// </summary>
    internal float ThinkSeconds(World world) => world.Every(_thinkEvery) * world.TickSeconds;

    /// <summary>
    /// Whether this tick's look made any row.
    /// </summary>
    internal bool AnyRow => _anyRow;

    /// <summary>
    /// This tick's look of the character at <paramref name="slot"/>; null when the look made none (its chooser is not due, or no source concerns
    /// it). Any phase: the parallel think reads it and writes only its own slot's row.
    /// </summary>
    internal ChooserRow? RowOf(World world, int slot) =>
        (uint)slot < (uint)_rows.Length && _rows[slot] is { } row && row.Tick == world.Tick ? row : null;

    /// <summary>
    /// The order step for the think's first rung (<see cref="Ladder.Orders"/>).
    /// </summary>
    internal LadderPick? OrderStep(ChooserRow? row) => Ladder.Orders(_steps, row);

    /// <summary>
    /// The serial look (the schedule step, after the systems'): a player character's saved queue is restored; then every character a source
    /// concerns (<see cref="Concerns"/>) whose chooser is due (<see cref="Due"/>) gets this tick's row: the order step's own pops run
    /// (<see cref="PlayerOrders.Prepare"/>), then its sources are asked (<see cref="Ask"/>). The replan bit is used up here: what asks later in the
    /// tick is seen at the next look.
    /// </summary>
    internal void Look(World world)
    {
        var table = world.Characters;
        if (_rows.Length < table.HighWater) Array.Resize(ref _rows, Math.Max(table.HighWater, _rows.Length * 2));

        _anyRow = false;
        int every = world.Every(_thinkEvery);
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Previous[i].Alive || table.Previous[i].Dormant || !world.Steps(i) || table.Cold(i) is not { } cold) continue;
            if (Decides(cold) && Saving.OrderSave.Pending(cold)) Saving.OrderSave.Restore(world, i, cold);
            if (!Concerns(cold) || !Due(world, i, cold, every)) continue;

            var row = _rows[i] ??= new ChooserRow();
            row.Reset(world.Tick, _sources.Count);
            row.Id = table.IdOf(i);
            row.Due = true;
            _anyRow = true;
            cold.Replan = false;
            LookAt(world, i, cold, row);
        }
    }

    /// <summary>
    /// Whether the look makes a row for the character: its chooser runs (<see cref="TakesOrders"/>) and a source concerns it: a character of the
    /// player's faction (<see cref="Decides"/>; a player-faction mind's chooser runs only with its row), one the order step concerns
    /// (<see cref="Ai.OrderStep.Concerns"/>), or one with a job list.
    /// </summary>
    static bool Concerns(CharacterCold cold)
    {
        if (!TakesOrders(cold)) return false;

        return Decides(cold) || Ai.OrderStep.Concerns(cold) || cold.Jobs.Count > 0;
    }

    /// <summary>
    /// Whether the character's chooser runs this tick: its think's turn (the AI's period anchored at tick + slot, as the think's), or asked (its
    /// replan bit or its mind's, an order that held it, the plan step on a running follow, <see cref="FollowDefence.Due"/>, the order step's own
    /// asks, <see cref="Ai.OrderStep.Asks"/>).
    /// </summary>
    static bool Due(World world, int slot, CharacterCold cold, int every)
    {
        if ((world.Tick + slot) % every < world.StepTicks(slot) && TurnAsks(world, slot, cold)) return true;
        if (cold.Replan || cold.OrderWins || cold.Ai is { Replan: true }) return true;
        if (FollowDefence.Due(world, slot, cold)) return true;

        return Ai.OrderStep.Asks(world, slot, cold, paused: false);
    }

    /// <summary>
    /// Whether the think's turn asks the chooser again (<b>engine</b>: no periodic replan was found in the original, but Meitou's idleness and job
    /// tasks do not end where the original's plans do): any character but the player's; a player character whose task the priority-3 goals come
    /// before (<see cref="GoalsComeFirst"/>), or whose front order or second-list entry runs its task, which the original's order step tests on
    /// every AI update (its list A, <see cref="ListTests.OrderDone"/>), or that its chooser's goal holds (<see cref="AiMind.Goal"/> of a player-faction mind: its
    /// chooser turns as an NPC's, so an urgent job or a higher goal can end the goal and its plan steps run). Not one in a fight or on another plan's
    /// task: those end by their own rules or a replan.
    /// </summary>
    static bool TurnAsks(World world, int slot, CharacterCold cold)
    {
        if (!Decides(cold) || GoalsComeFirst(world.Characters, slot, cold)) return true;
        if (cold.Ai is { PlayerSide: true, Goal: not null }) return true;
        if (cold.SecondList.Count > 0 && PlayerOrders.RunningSecond(world, slot) is not null) return true;

        return cold.OrderQueue.Front is { Started: true } front && PlayerOrders.Runs(world, slot, front);
    }

    /// <summary>
    /// One character's look: a stagger's stumble ended by the update's replan, the order step's pops, then its sources, the trace lines kept while
    /// tracing.
    /// </summary>
    void LookAt(World world, int slot, CharacterCold cold, ChooserRow row)
    {
        _lines.Clear();
        Combat.BodyOrders.UpdateEnds(world, slot);
        PlayerOrders.Prepare(world, slot);
        Ask(world, slot, cold, row);
        LookSecond(world, slot, cold, row);
        if (Tracing) row.Lines = [.. _lines];
    }

    /// <summary>
    /// Scores the second list's entries for the think's rung 3 (<c>FUN_14050df10</c> over <c>+0x70</c>): an entry that runs the character's task, or
    /// can start and is not done (its row's list A, effect and finished test, <see cref="ListTests.OrderDone"/>), scores its desire
    /// (<see cref="OrderDesire"/>) x weight 1 (the loader's), as a goal source does; one whose task ended is no longer started. The plan step
    /// ends a running entry's task as a front's (<see cref="PlayerOrders.EndSecond"/>). A loaded entry waiting for its target
    /// (<see cref="OrderEntry.Awaits"/>) scores 0.
    /// </summary>
    void LookSecond(World world, int slot, CharacterCold cold, ChooserRow row)
    {
        var list = cold.SecondList;
        if (row.SecondScores.Length < list.Count) row.SecondScores = new float[Math.Max(list.Count, row.SecondScores.Length * 2)];
        row.SecondCount = list.Count;
        for (int k = 0; k < list.Count; k++)
        {
            var entry = list[k];
            if (entry.Awaits is not null)
            {
                row.SecondScores[k] = 0;
                if (Tracing) _lines.Add($"3 second list [{k}] {(int)entry.Row}: waits for its target");
                continue;
            }

            var check = PlayerOrders.SecondCheck(world, slot, entry);
            if (entry.Started && !check.Running) entry.Started = false;
            if (check.Running && PlayerOrders.EndSecond(world, slot, entry)) check = PlayerOrders.SecondCheck(world, slot, entry);

            bool done = entry.Spent || !check.Running && ListTests.OrderDone(world, slot, entry, false);
            float desire = check.Can && !done ? OrderDesire.Of(world, slot, entry) ?? 1 : 0;   // a row with no TaskData row (54 Rest) is wanted
            row.SecondScores[k] = desire > 0 ? SecondScore(world, slot, desire, check) : 0;
            if (Tracing) _lines.Add($"3 second list [{k}] {(int)entry.Row}: {(done ? "done" : Verdict(check, row.SecondScores[k]))}");
        }
    }

    /// <summary>
    /// A second-list entry's score: its desire, x1.25 while it runs, and again below 1 while its task is not finished.
    /// </summary>
    static float SecondScore(World world, int slot, float desire, in DecisionCheck check)
    {
        float score = desire * (check.Running ? 1.25f : 1);
        if (check.Running && score < 1 && Unfinished(world, slot)) score *= 1.25f;

        return score;
    }

    /// <summary>
    /// A second-list entry's answer as a trace line's verdict.
    /// </summary>
    static string Verdict(in DecisionCheck check, float score)
    {
        if (!check.Can) return check.Why ?? "cannot run";

        return score > 0 ? $"score {score:0.###}{(check.Running ? " (running)" : "")}" : "desire 0";
    }

    /// <summary>
    /// Asks the sources that concern the character (<see cref="Asks"/>) in order: each one's answer, and a goal source's score when it can run
    /// (<see cref="Score"/>).
    /// </summary>
    void Ask(World world, int slot, CharacterCold cold, ChooserRow row)
    {
        for (int k = 0; k < _sources.Count; k++)
        {
            var source = _sources[k];
            if (!Asks(source, cold)) continue;

            var check = source.CanRun(world, slot);
            row.Asked[k] = true;
            row.Checks[k] = check;
            if (source.Step == DecisionStep.Goals && check.Can) row.Scores[k] = Score(world, slot, source, check);
            Note(source, Verdict(source, check, row.Scores[k]));
        }
    }

    /// <summary>
    /// Whether <paramref name="source"/> is asked for the character: every source for one of the player's faction (<see cref="Decides"/>); for any
    /// other the order step while it holds an order, and the engine's job passes while it has a job list (they have no faction test,
    /// <c>FUN_14050e5b0</c>).
    /// </summary>
    static bool Asks(IDecisionSource source, CharacterCold cold)
    {
        if (Decides(cold)) return true;
        if (source.Step == DecisionStep.Orders) return Ai.OrderStep.Concerns(cold);

        return source is JobSource && cold.Jobs.Count > 0;
    }

    /// <summary>
    /// A goal source's score (<c>FUN_14050df10</c>): desire x weight, x1.25 for the goal running now, and again below 1 while its task is not
    /// finished.
    /// </summary>
    static float Score(World world, int slot, IDecisionSource source, in DecisionCheck check)
    {
        float score = source.Desire(world, slot) * source.Weight * (check.Running ? 1.25f : 1);
        if (check.Running && score < 1 && Unfinished(world, slot)) score *= 1.25f;

        return score;
    }

    /// <summary>
    /// Whether the character's running task is not finished by its class's test (slot 4, <see cref="TaskFinished"/>): the scorer then raises the
    /// running goal's score below 1 once more (<c>FUN_14050df10</c>, <b>Verified</b>, docs/game/ai-tasks.md "Finished tests").
    /// </summary>
    static bool Unfinished(World world, int slot) =>
        world.Characters.Cold(slot)?.Running is { } run && TaskFinished.Unfinished(world, slot, run.Row);

    /// <summary>
    /// A source's answer as a trace line's verdict.
    /// </summary>
    static string Verdict(IDecisionSource source, in DecisionCheck check, float score)
    {
        if (!check.Can) return check.Why ?? "cannot run";
        if (source.Step == DecisionStep.Goals) return score > 0 ? $"score {score:0.###}{(check.Running ? " (running)" : "")}" : "desire 0";

        return check.Running ? "runs" : "can start";
    }

    /// <summary>
    /// Adds a line on a source to the trace while <see cref="Tracing"/> is on.
    /// </summary>
    void Note(IDecisionSource source, string what)
    {
        if (Tracing) _lines.Add($"{(int)source.Step} {source.Name}: {what}");
    }

    /// <summary>
    /// The ladder of every character the look made a row for that no think ran (one with no mind, or a world without an AI): the sources' rungs
    /// alone (<see cref="Ladder.Run"/> with no tiers). A parallel phase after the systems' thinks; each writes its own row.
    /// </summary>
    internal void ThinkWithoutMind(World world, Partition part)
    {
        if (!_anyRow) return;

        var table = world.Characters;
        for (int i = part.Start; i < part.End; i++)
        {
            if (RowOf(world, i) is not { Due: true, Thought: false } row || table.Cold(i) is not { } cold) continue;

            row.Thought = true;
            row.Pick = Ladder.Run(_steps, row, null, cold.Jobs.Count > 0);
        }
    }

    #endregion

    #region Starts

    /// <summary>
    /// The serial pass in slot order (<paramref name="paused"/> false): what each character's ladder picked this tick is started
    /// (<see cref="Apply"/>); an NPC whose chooser is off but not down drops a started front whose task ended; every NPC with an order has its hold
    /// set anew (<see cref="Ai.OrderStep.Hold"/>). Between ticks (<paramref name="paused"/>): the order step alone, serially (<see cref="RunPaused"/>).
    /// </summary>
    internal void Run(World world, bool paused)
    {
        if (paused)
        {
            RunPaused(world);
            return;
        }

        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || !world.Steps(i) || table.Cold(i) is not { } cold) continue;
            if (Decides(cold) && cold.OrderWins) Hold(world, i);   // made a player character while its order held it

            var row = RowOf(world, i) is { Thought: true } r && r.Id == table.IdOf(i) ? r : null;
            if (row is not null && TakesOrders(cold)) Apply(world, i, cold, row);
            else Unstarted(world, i, cold, row);

            HoldNpc(world, i, cold);
        }
    }

    /// <summary>
    /// A character whose pick is not started: one downed or carried since its look keeps the ask for when it is up (the look used its replan bit);
    /// one whose chooser is off but that is not down drops a started front whose task ended (<see cref="PlayerOrders.DropEnded"/>).
    /// </summary>
    static void Unstarted(World world, int slot, CharacterCold cold, ChooserRow? row)
    {
        if (row is not null) Request(cold);
        if (cold.OrderQueue.Count > 0 && !TakesOrders(cold) && !Down(cold)) PlayerOrders.DropEnded(world, slot);
    }

    /// <summary>
    /// An NPC's order hold after its start: set anew while it has an order or its order held it, or while its kept routine waits for the order's
    /// last walk.
    /// </summary>
    static void HoldNpc(World world, int slot, CharacterCold cold)
    {
        if (!Decides(cold) && Ai.OrderStep.HoldDue(cold)) Hold(world, slot);
    }

    /// <summary>
    /// Starts one character's pick: the order step is asked again (<see cref="FreshOrders"/>: an order given or ended since the look still beats
    /// every other pick), then the other sources the ladder went past that could not run are passed (<see cref="Pass"/>), and the picked source is
    /// started (<see cref="StartPick"/>). What its own start asks is dropped; a pick that can no longer run asks again.
    /// </summary>
    void Apply(World world, int slot, CharacterCold cold, ChooserRow row)
    {
        bool asked = cold.Replan;
        var pick = row.Pick;
        int order = FreshOrders(world, slot, cold);
        if (order >= 0) pick = new(order, null, order);
        else if (pick.Source < 0 || pick.Source >= _steps.Commitment) StartAfterOrders(world, slot, row, pick, ref asked);
        else asked = true;   // its order could no longer run

        cold.Replan = asked;
        Trace(world, cold, row, pick);
    }

    /// <summary>
    /// The order step asked again next to the starts (the queue can change between the look and the starts: an order given in the commit, a front
    /// ended): the first order source that can run is started unless it runs already (its index), those before it passed; -1 when none can (all
    /// passed). Only for a character the look asks the order step for (<see cref="Asks"/>).
    /// </summary>
    int FreshOrders(World world, int slot, CharacterCold cold)
    {
        for (int k = _steps.Orders; k < _steps.Commitment; k++)
        {
            var source = _sources[k];
            if (!Asks(source, cold)) continue;

            var check = source.CanRun(world, slot);
            if (!check.Can)
            {
                source.Passed(world, slot, check);
                continue;
            }

            if (!check.Running) source.Start(world, slot);
            return k;
        }

        return -1;
    }

    /// <summary>
    /// A pick past the order step: the sources the ladder went past are passed, and the picked source or second-list entry started.
    /// </summary>
    void StartAfterOrders(World world, int slot, ChooserRow row, in LadderPick pick, ref bool asked)
    {
        Pass(world, slot, row, pick);
        if (pick.Source >= 0 && !StartPick(world, slot, pick.Source)) asked = true;
        if (pick.Second >= 0 && !StartSecond(world, slot, pick.Second)) asked = true;
    }

    /// <summary>
    /// Starts second-list entry <paramref name="k"/> after asking it again next to its start; nothing when it runs already. False when it is gone
    /// or can no longer run.
    /// </summary>
    static bool StartSecond(World world, int slot, int k)
    {
        var list = world.Characters.Cold(slot)!.SecondList;
        if ((uint)k >= (uint)list.Count) return false;

        var check = PlayerOrders.SecondCheck(world, slot, list[k]);
        if (!check.Can) return false;

        if (!check.Running) PlayerOrders.StartSecond(world, slot, list[k]);
        return true;
    }

    /// <summary>
    /// Passes, in order, the sources after the order step and below <see cref="LadderPick.Through"/> that the look asked and found cannot run
    /// (<see cref="IDecisionSource.Passed"/>: a stale job's end), as the decision step passed them.
    /// </summary>
    void Pass(World world, int slot, ChooserRow row, in LadderPick pick)
    {
        int end = Math.Min(pick.Through, _sources.Count);
        for (int k = _steps.Commitment; k < end; k++)
        {
            if (k != pick.Source && row.Asked[k] && !row.Checks[k].Can) _sources[k].Passed(world, slot, row.Checks[k]);
        }
    }

    /// <summary>
    /// Starts source <paramref name="k"/> after asking it again next to its start (a source keeps its pick per call, not per slot); it starts
    /// nothing when it runs already. False when it can no longer run (it is passed).
    /// </summary>
    bool StartPick(World world, int slot, int k)
    {
        var source = _sources[k];
        var check = source.CanRun(world, slot);
        if (!check.Can)
        {
            source.Passed(world, slot, check);
            return false;
        }

        if (!check.Running) source.Start(world, slot);
        return true;
    }

    /// <summary>
    /// The decision trace (<see cref="CharacterCold.Decision"/>) while tracing: the winner (a source's name, a goal's, or what the ladder left to the
    /// AI) and the look's lines.
    /// </summary>
    void Trace(World world, CharacterCold cold, ChooserRow row, in LadderPick pick)
    {
        if (!Tracing)
        {
            if (cold.Decision is not null) cold.Decision = null;
            return;
        }

        cold.Decision = new DecisionTrace(world.Tick, WinnerOf(cold, row, pick), row.Lines ?? []);
    }

    /// <summary>
    /// The trace's winner: the picked source's name, else the goal's the think noted, else what the ladder left.
    /// </summary>
    string WinnerOf(CharacterCold cold, ChooserRow row, in LadderPick pick)
    {
        if (pick.Source >= 0) return _sources[pick.Source].Name;
        if (pick.Second >= 0 && pick.Second < cold.SecondList.Count) return $"second list {(int)cold.SecondList[pick.Second].Row}";
        if (row.Winner is { } goal) return goal;

        return Decides(cold) ? "(none)" : "(orders only: the AI decides the rest)";
    }

    #endregion

    #region Paused orders

    /// <summary>
    /// Between ticks (serial, slot order): the order step alone for every character it concerns, as an order given while paused applies at once
    /// (no think runs between ticks). The replan bit stays for the next tick's look.
    /// </summary>
    void RunPaused(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || !world.Steps(i) || table.Cold(i) is not { } cold) continue;
            if (Decides(cold) && cold.OrderWins) Hold(world, i);

            if (Decides(cold)) PausedPlayer(world, i, cold);
            else if (Ai.OrderStep.Concerns(cold)) PausedNpc(world, i, cold);
            else if (Ai.OrderStep.HoldDue(cold)) Hold(world, i);   // its routine waits for the order's last walk
        }
    }

    /// <summary>
    /// A player character between ticks: its saved queue restored once, its order step run when asked; not while it is down.
    /// </summary>
    void PausedPlayer(World world, int slot, CharacterCold cold)
    {
        if (Saving.OrderSave.Pending(cold)) Saving.OrderSave.Restore(world, slot, cold);
        if (Down(cold) || !PausedDue(world, slot, cold)) return;

        PausedOrders(world, slot, cold);
    }

    /// <summary>
    /// An NPC with an order between ticks: its order step when its chooser runs and it is asked (one whose chooser is off but not down only drops a
    /// started front whose task ended, <see cref="PlayerOrders.DropEnded"/>), then its hold set anew.
    /// </summary>
    void PausedNpc(World world, int slot, CharacterCold cold)
    {
        if (!TakesOrders(cold))
        {
            if (!Down(cold)) PlayerOrders.DropEnded(world, slot);
        }
        else if (PausedDue(world, slot, cold))
        {
            PausedOrders(world, slot, cold);
        }

        Hold(world, slot);
    }

    /// <summary>
    /// Whether the order step is asked between ticks: the replan bit, or a front not tried yet or whose task ended.
    /// </summary>
    static bool PausedDue(World world, int slot, CharacterCold cold) => cold.Replan || Ai.OrderStep.Asks(world, slot, cold, paused: true);

    /// <summary>
    /// The order step run serially: its pops (<see cref="PlayerOrders.Prepare"/>), then the order sources in order, the first that can run started
    /// (one that runs already ends it), those before it passed.
    /// </summary>
    void PausedOrders(World world, int slot, CharacterCold cold)
    {
        _lines.Clear();
        PlayerOrders.Prepare(world, slot);
        string winner = "(paused: orders only)";
        for (int k = _steps.Orders; k < _steps.Commitment; k++)
        {
            var source = _sources[k];
            var check = source.CanRun(world, slot);
            Note(source, Verdict(source, check, 0));
            if (!check.Can)
            {
                source.Passed(world, slot, check);
                continue;
            }

            winner = source.Name;
            if (!check.Running) source.Start(world, slot);
            break;
        }

        if (Tracing) cold.Decision = new DecisionTrace(world.Tick, winner, _lines.ToArray());
    }

    #endregion

    #region Engine sources

    /// <summary>
    /// Steps 3 and 5: the character's job list in order (<c>FUN_14050e5b0</c>, docs/game/ai-tasks.md "Perma-jobs"): the jobs switch gates every job
    /// but 16, 31, 45, 57, 58 and 250 (<c>FUN_14050c1c0</c>) in both passes; a job whose type has no contributor or whose row the repertoire does not
    /// register is skipped, and so is one the general list tests pass over (<see cref="ListTests.JobSkipped"/>: done, or nothing to do); the first
    /// job that can run wins (one that runs the character's task already ends the step), so a higher job takes over a lower one at the next decision.
    /// Step 3 asks only the jobs that are urgent now. The implicit jobs are step 5's next source
    /// (<see cref="Production.ProductionSystem"/>).
    /// </summary>
    sealed class JobSource(DecisionSystem owner, bool urgent) : IDecisionSource
    {
        /// <summary>
        /// The job index of the character's list that the last <see cref="CanRun"/> found can run (-1: none).
        /// </summary>
        int _picked = -1;

        /// <summary>
        /// The trace name ("Urgent jobs" or "Jobs").
        /// </summary>
        public string Name => urgent ? "Urgent jobs" : "Jobs";
        /// <summary>
        /// Step 3 for the urgent pass, else step 5.
        /// </summary>
        public DecisionStep Step => urgent ? DecisionStep.UrgentJobs : DecisionStep.Jobs;
        /// <summary>
        /// One source per step: no order within it.
        /// </summary>
        public int Key => 0;

        /// <summary>
        /// Walks the character's job list for the first job that can run (or runs already) and keeps its index for <see cref="Start"/>.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot)
        {
            _picked = -1;
            var cold = world.Characters.Cold(slot)!;
            bool on = (cold.Orders & CharacterOrders.Jobs) != 0;
            for (int k = 0; k < cold.Jobs.Count; k++)
            {
                var type = cold.Jobs[k].Type;
                if (owner._jobs.GetValueOrDefault(type) is not { } source || urgent && !source.Urgent(world, slot, k)) continue;
                DecisionCheck check;
                if (!on && !Production.PermaJobs.IgnoresSwitch(type)) check = DecisionCheck.No("jobs switch off");
                else if (!TaskStart.Eligible(world, cold, type)) check = DecisionCheck.No("not in its repertoire");
                else if (ListTests.JobSkipped(world, slot, cold.Jobs[k], out var why)) check = DecisionCheck.No(why);
                else check = source.CanRun(world, slot, k);
                if (owner.Tracing)
                {
                    string verdict = check.Can ? check.Running ? "runs" : "starts" : check.Why ?? "cannot run";
                    owner._lines.Add($"{(int)Step} {Name} [{k}] {(int)type}: {verdict}");
                }

                if (!check.Can) continue;
                _picked = k;
                return check;
            }

            return DecisionCheck.No(urgent ? "no urgent job" : "no job can run");
        }

        /// <summary>
        /// Starts the job <see cref="CanRun"/> picked through its type's contributor.
        /// </summary>
        public void Start(World world, int slot)
        {
            var cold = world.Characters.Cold(slot)!;
            if ((uint)_picked < (uint)cold.Jobs.Count
                && owner._jobs.GetValueOrDefault(cold.Jobs[_picked].Type) is { } source)
                source.Start(world, slot, _picked);
        }

        /// <summary>
        /// Step 5 found nothing: the list job the character's task runs, which could not run on, ends (<see cref="IJobSource.Stop"/>; not a fight,
        /// which is its row's step).
        /// </summary>
        public void Passed(World world, int slot, in DecisionCheck check)
        {
            if (urgent) return;
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var task = (CharacterTask)table.Next[slot].Task;
            if (task == CharacterTask.Attack || cold.InCombat
                || cold.Running is not { Source: TaskSource.Job, Implicit: false, Job: >= 0 } run
                || !run.Runs(task) || run.Job >= cold.Jobs.Count)
                return;

            owner._jobs.GetValueOrDefault(cold.Jobs[run.Job].Type)?.Stop(world, slot, run.Job);
        }
    }

    /// <summary>
    /// Source 1: the front of the order queue (<see cref="PlayerOrders"/>). One that cannot run shows its failure text once and stays queued; the
    /// step goes on.
    /// </summary>
    sealed class OrderSource : IDecisionSource
    {
        /// <summary>
        /// The trace name.
        /// </summary>
        public string Name => "Orders";
        /// <summary>
        /// Step 1, the order queue.
        /// </summary>
        public DecisionStep Step => DecisionStep.Orders;
        /// <summary>
        /// One source per step: no order within it.
        /// </summary>
        public int Key => 0;

        /// <summary>
        /// Whether the front order can run (or runs its task already); else why not.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot)
        {
            if (world.Characters.Cold(slot)!.OrderQueue.Front is not { } front) return DecisionCheck.No("no order");
            if (front.Started) return PlayerOrders.Runs(world, slot, front) ? DecisionCheck.Runs : DecisionCheck.No("its task ended");
            var check = PlayerOrders.CanRun(world, slot, front);
            return check.CanRun ? DecisionCheck.Run : DecisionCheck.No(check.Why ?? $"{front.Row} cannot run");
        }

        /// <summary>
        /// Starts the front order (<see cref="PlayerOrders.StartFront"/>).
        /// </summary>
        public void Start(World world, int slot) => PlayerOrders.StartFront(world, slot);

        /// <summary>
        /// The front order could not run: it is refused (<see cref="PlayerOrders.Refused"/>).
        /// </summary>
        public void Passed(World world, int slot, in DecisionCheck check) => PlayerOrders.Refused(world, slot);
    }

    /// <summary>
    /// The default task (<c>+0x190</c>, picked at key 1.0 without the scorer after the priority-1 tier, <c>FUN_140510ef0</c>, <b>Verified</b>) of a
    /// player-faction character, one slot chosen by the manoeuvre mode (<c>FUN_140599970</c> / <c>FUN_1405966a0</c>, <b>Verified</b>; the standing
    /// orders FAR and NEAR, <see cref="ManoeuvreCommand"/>): ROAM_FAR 14 Idleness, STAY_CLOSE 31 Staying close (Task_Follow with no target, whose
    /// update does nothing without one: it stands), HOLD_GROUND 30 Holding position (Task_Blank with the effect isAtLocation at the centre of
    /// movement: it walks back there, then stands; no input of 1.0.68 sets HOLD_GROUND, only standing orders 8 FAR and 9 NEAR set the mode, so this
    /// slot is reached only by a mod). The own-list 14 (setupAI, priority 1, weight 0.1) is dropped by the scorer while its list B holds (<c>!162
    /// !157</c>, no act, no effect): it is kept while the character is prone or holds a weapon. It runs over idling, a fight (its goal's or row's), and any task a plan still runs (an order's, a goal's, the
    /// engine's: those end by their own rules; a stale job's ended at the job pass); a wander or follow no plan runs is given up for it, and so is
    /// an idle of another default row.
    /// </summary>
    sealed class Idleness : IDecisionSource
    {
        /// <summary>
        /// The trace name: the default task's for ROAM_FAR, every NPC's and a loaded character's mode (STAY_CLOSE and HOLD_GROUND run 31 and 30
        /// under it).
        /// </summary>
        public string Name => "14 Idleness";
        /// <summary>
        /// Step 6, the fallback.
        /// </summary>
        public DecisionStep Step => DecisionStep.Fallback;

        /// <summary>
        /// The default task's rung, after the priority-1 goals (<see cref="Ladder.DefaultKeys"/>).
        /// </summary>
        public int Key => Ladder.DefaultKeys + (int)TaskType.Idle;

        /// <summary>
        /// The squared flat reach and the height of <c>isAtLocation</c> for a point (<c>FUN_1405a2450</c>, <b>Verified</b>: 144 and 20).
        /// </summary>
        const float AtReach2 = 144, AtHeight = 20;

        /// <summary>
        /// It always answers: it starts over a wander or follow no plan runs, out of a fight, over an idle of another default row, and for HOLD_GROUND
        /// away from the centre; else it counts as running what the character does. A task the think started for a goal the player-faction mind no
        /// longer holds (<see cref="Left"/>) is no plan's.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot)
        {
            var cold = world.Characters.Cold(slot)!;
            var task = (CharacterTask)world.Characters.Next[slot].Task;
            bool runs = cold.Running is { } run && run.Runs(task);
            bool left = runs && Left(cold, cold.Running!);
            bool planned = runs && !left;
            if (cold.InCombat) return DecisionCheck.Runs;
            if (task is CharacterTask.Wander or CharacterTask.Follow && !planned) return DecisionCheck.Run;
            if (task != CharacterTask.Idle) return DecisionCheck.Runs;

            // ROAM_FAR stands as it is, but a left goal's stand is taken over (its row then the default task's, which the think's turn asks again).
            if (cold.Manoeuvre == ManoeuvreMode.RoamFar) return left ? DecisionCheck.Run : DecisionCheck.Runs;

            bool other = planned && !IsDefault(cold.Running!.Row);
            if (other) return DecisionCheck.Runs;
            bool settled = planned && cold.Running!.Row == Row(cold) && !Away(world, slot, cold);
            return settled ? DecisionCheck.Runs : DecisionCheck.Run;
        }

        /// <summary>
        /// Starts the slot's task: HOLD_GROUND away from the centre walks there under row 30; else the character stands idle under the slot's row.
        /// </summary>
        public void Start(World world, int slot)
        {
            var cold = world.Characters.Cold(slot)!;
            var row = Row(cold);
            if (row == TaskType.HoldPosition && Away(world, slot, cold) && cold.CentreOfMovement is { } centre)
            {
                TaskStart.Walk(world, slot, new System.Numerics.Vector2(centre.X, centre.Z), SpeedMode.Free, row, TaskSource.Goal);
                return;
            }

            TaskStart.Become(world, slot, CharacterTask.Idle, row, TaskSource.Goal);
        }

        /// <summary>
        /// The slot's row by the manoeuvre mode.
        /// </summary>
        static TaskType Row(CharacterCold cold) => cold.Manoeuvre switch
        {
            ManoeuvreMode.StayClose => TaskType.StayCloseToTarget,
            ManoeuvreMode.HoldGround => TaskType.HoldPosition,
            _ => TaskType.Idle,
        };

        /// <summary>
        /// Whether <paramref name="run"/> is the task a player-faction mind's think started for a goal it holds no longer (<see cref="AiMind.Owns"/>,
        /// no goal: its ladder left it for this default task, a Follow of 171 or a stand of 62 after the slavery or the request ended): no plan runs
        /// it, so the default task takes it over: the original's pick keeps only the current goal's running task and plans and installs any other
        /// winner, the default task too (<c>FUN_140510650</c>, <b>Verified</b>).
        /// </summary>
        static bool Left(CharacterCold cold, RunningTask run) => cold.Ai is { PlayerSide: true, Goal: null } mind && mind.Owns(run);

        /// <summary>
        /// Whether <paramref name="row"/> is a row of the default task's slot.
        /// </summary>
        internal static bool IsDefault(TaskType row) => row is TaskType.Idle or TaskType.StayCloseToTarget or TaskType.HoldPosition;

        /// <summary>
        /// Whether HOLD_GROUND's effect does not hold: the character is not at its centre of movement (flat squared distance 144, height 20).
        /// </summary>
        static bool Away(World world, int slot, CharacterCold cold)
        {
            if (cold.Manoeuvre != ManoeuvreMode.HoldGround || cold.CentreOfMovement is not { } centre) return false;

            var at = world.Characters.Next[slot].Position;
            float dx = at.X - centre.X, dz = at.Z - centre.Z;
            return dx * dx + dz * dz >= AtReach2 || MathF.Abs(at.Y - centre.Y) >= AtHeight;
        }
    }

    #endregion
}
