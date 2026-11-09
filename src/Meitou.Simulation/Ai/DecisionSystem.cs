using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The steps of the original's chooser (<c>AITaskSytem::chooseGoal</c> <c>FUN_140510ef0</c>, docs/game/ai-tasks.md "Precedence"), in the order they
/// are tried.
/// </summary>
public enum DecisionStep : byte
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
    /// 6: the priority-2 goals, then the priority-1 goals (14 Idleness), then the default task (<c>+0x190</c>).
    /// </summary>
    Fallback = 6,
}

/// <summary>
/// A source's answer for one character (<see cref="IDecisionSource.CanRun"/>, a pure read): it can run (<see cref="Can"/>; the original's desire x
/// finder above 0), it runs the character's task already (<see cref="Running"/>: the plan goes on, nothing is started), or why not
/// (<see cref="Why"/>: an order's failure text, "no attacker in range"; null: none to show).
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
public interface IDecisionSource
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
public interface IDecisionOwner
{
    /// <summary>
    /// Registers its decision sources and job contributors with <paramref name="decisions"/>.
    /// </summary>
    void RegisterSources(DecisionSystem decisions);
}

/// <summary>
/// The contributor of the jobs of one or more types (<see cref="DecisionSystem.RegisterJob"/>). The decision step's job sources (steps 3 and 5) walk
/// the character's list in order (the original's keys N+8, N+7, ...) and ask the contributor of each job's type; the jobs switch, the repertoire and
/// the walk's order are theirs, everything else the contributor's.
/// </summary>
public interface IJobSource
{
    /// <summary>
    /// Whether job <paramref name="k"/> of the character's list can run now (a pure read: the original's desire x finder above 0);
    /// <see cref="DecisionCheck.Runs"/> when the character's task is that job's already.
    /// </summary>
    DecisionCheck CanRun(World world, int slot, int k);

    /// <summary>
    /// Starts job <paramref name="k"/> (serial phases only), after <see cref="CanRun"/> said it can.
    /// </summary>
    void Start(World world, int slot, int k);

    /// <summary>
    /// Step 3: whether job <paramref name="k"/> is urgent now (the kind-2 jobs while enemies are sensed, Obedient Slave 187 while its gate holds;
    /// <c>FUN_14050e5b0</c>). Default: never.
    /// </summary>
    bool Urgent(World world, int slot, int k) => false;

    /// <summary>
    /// The character's task is job <paramref name="k"/>'s, which can no longer run, and no other job of the list can: the task ends (the original's
    /// idleness takes over at that choice). Default: nothing (the act-out ends by itself).
    /// </summary>
    void Stop(World world, int slot, int k) { }
}

/// <summary>
/// The last decision of a character, filled only while <see cref="DecisionSystem.Tracing"/> is on: the tick, the source that won (or what the step
/// yielded to) and, per source asked, its answer. Display only: not saved, not hashed.
/// </summary>
public sealed record DecisionTrace(long Tick, string Winner, IReadOnlyList<string> Lines);

/// <summary>
/// The single decision step for the player's characters (docs/simulation.md "Decision step"; the original's chooser <c>FUN_140510ef0</c>). Serial, in
/// slot order. A character is decided when its replan bit is set (<see cref="Request(World, int)"/>: a task ended, an order added or cleared, a
/// stagger outside a fight, the melee alert with the fight off, the jobs changed, a load), when its front order's task ended, and (<b>engine</b>)
/// every <see cref="Production.ProductionSystem.JobCheckTicks"/> while it is idle or on a job's task. The sources are asked in the original's order
/// and the first that can run starts (step 4: the best score); a source already running ends the step. Steps 3 and 5 walk the character's job list
/// through the systems' job contributors (<see cref="RegisterJob"/>); step 6 ends in 14 Idleness, which always answers. NPCs keep
/// <see cref="Ai.AiSystem"/>. Run in the slow world after retaliation, aggro and flight (<see cref="World.RunTick"/>), and for orders only between
/// ticks (<see cref="World.ApplyPausedInterfaceCommands"/>).
/// </summary>
public sealed class DecisionSystem
{
    /// <summary>
    /// The decision sources in the order they are asked (by step, then key).
    /// </summary>
    readonly List<IDecisionSource> _sources = [];
    /// <summary>
    /// The job contributor of each job type.
    /// </summary>
    readonly Dictionary<TaskType, IJobSource> _jobs = [];
    /// <summary>
    /// The tick + 1 at which each slot was claimed by an engine start (<see cref="Claim"/>), by slot.
    /// </summary>
    long[] _claimed = [];
    /// <summary>
    /// The trace lines of the decision being made (kept only while <see cref="Tracing"/> is on).
    /// </summary>
    readonly List<string> _lines = [];

    /// <summary>
    /// Makes the step with its own sources: the order queue, the urgent jobs, the jobs and 14 Idleness.
    /// </summary>
    public DecisionSystem()
    {
        Register(new OrderSource());
        Register(new JobSource(this, urgent: true));
        Register(new JobSource(this, urgent: false));
        Register(new Idleness());
    }

    /// <summary>
    /// The characters the step decides: the player's (<see cref="CharacterCold.IsPlayer"/>); the others are the AI's and the engine's
    /// (docs/simulation.md "Decision step").
    /// </summary>
    public static bool Decides(CharacterCold cold) => cold.IsPlayer;

    /// <summary>
    /// Registers (or replaces) the contributor of the jobs of <paramref name="type"/> (an open list: a job type without one is kept in the list but
    /// never run).
    /// </summary>
    public void RegisterJob(TaskType type, IJobSource source) => _jobs[type] = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>
    /// The contributor of the jobs of <paramref name="type"/>; null when none is registered.
    /// </summary>
    public IJobSource? JobSourceOf(TaskType type) => _jobs.GetValueOrDefault(type);

    /// <summary>
    /// The sources in the order they are asked.
    /// </summary>
    public IReadOnlyList<IDecisionSource> Sources => _sources;

    /// <summary>
    /// Record each decision in <see cref="CharacterCold.Decision"/> (a debug toggle; off by default, never hashed).
    /// </summary>
    public bool Tracing { get; set; }

    /// <summary>
    /// Adds a source after those of a lower step or key and after equal ones (an open list: a mod may add its own).
    /// </summary>
    public void Register(IDecisionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int at = _sources.FindLastIndex(s => s.Step < source.Step || s.Step == source.Step && s.Key <= source.Key);
        _sources.Insert(at + 1, source);
    }

    /// <summary>
    /// Asks for the character to be decided again (a replan trigger): any phase, its own slot or a serial one. Only the player's characters keep the
    /// bit.
    /// </summary>
    public static void Request(CharacterCold cold)
    {
        if (Decides(cold)) cold.Replan = true;
    }

    /// <summary>
    /// <see cref="Request(CharacterCold)"/> for the character at <paramref name="slot"/>, if any.
    /// </summary>
    public static void Request(World world, int slot)
    {
        if (world.Characters.Cold(slot) is { } cold) Request(cold);
    }

    /// <summary>
    /// The engine started a task for the character this tick outside the step (a follow row's fight, a bodyguard's own defence within 40): the step
    /// leaves it alone this tick. Serial phases only.
    /// </summary>
    public void Claim(World world, int slot)
    {
        if (_claimed.Length <= slot) Array.Resize(ref _claimed, Math.Max(slot + 1, _claimed.Length * 2));
        _claimed[slot] = world.Tick + 1;
    }

    /// <summary>
    /// Whether an engine start claimed the slot this tick (<see cref="Claim"/>).
    /// </summary>
    bool Claimed(World world, int slot) => slot < _claimed.Length && _claimed[slot] == world.Tick + 1;

    /// <summary>
    /// The <b>engine</b> re-ask (design D4: no periodic replan was found in the original, but Meitou's idleness and job tasks do not end where the
    /// original's plans do): a character not fighting whose task is a job's or 14 Idleness's, or that no start runs (idle, a standing task), is
    /// decided every <see cref="Production.ProductionSystem.JobCheckTicks"/>; so is one on an order whose row needs no melee threat (146,
    /// <see cref="PlayerOrders.NeedsNoThreat"/>), which a threat must take off its turret.
    /// </summary>
    public static bool ReAsked(World world, int slot, CharacterCold cold) =>
        GoalsComeFirst(world.Characters, slot, cold)
        || !cold.InCombat && cold.Running is { Source: TaskSource.Order } run && run.Runs((CharacterTask)world.Characters.Next[slot].Task)
            && cold.OrderQueue.Front is { Started: true } front && PlayerOrders.NeedsNoThreat(front.Row);

    /// <summary>
    /// Whether the character's task is one the step's priority-3 goals (step 4) come before, so a goal that can run takes it over: not a fight, and
    /// no start runs it (idling, a standing task), or it is a job's (list or implicit; steps 3 and 5) or 14 Idleness's (step 6). Not an order's, a
    /// goal's or the engine's. A pure read.
    /// </summary>
    public static bool GoalsComeFirst(CharacterTable table, int slot, CharacterCold cold)
    {
        var task = (CharacterTask)table.Next[slot].Task;
        if (task == CharacterTask.Attack || cold.InCombat) return false;
        if (cold.Running is not { } run || !run.Runs(task)) return true;
        return run.Source == TaskSource.Job || run.Row == TaskType.Idle;
    }

    /// <summary>
    /// Decides every player character that is due, in slot order (serial). <paramref name="paused"/>: between ticks, orders only; the bit stays for
    /// the next tick's full step.
    /// </summary>
    internal void Run(World world, bool paused)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            // a far character decides at its step (the far cadence)
            if (!table.Next[i].Alive || !world.Steps(i) || table.Cold(i) is not { } cold || !Decides(cold)) continue;
            if (cold.SavedOrders is not null) Saving.OrderSave.Restore(world, i, cold);
            bool front = cold.OrderQueue.Front is { } f && (!f.Tried || f.Started && !PlayerOrders.Runs(world, i, f));
            bool due = !paused && (world.Tick + i) % Production.ProductionSystem.JobCheckTicks == 0 && ReAsked(world, i, cold);
            if (!cold.Replan && !front && !due) continue;
            if (!paused && Claimed(world, i))
            {
                cold.Replan = false;
                continue;
            }

            Decide(world, i, paused);
        }
    }

    /// <summary>
    /// Decides one character now (serial phases): the order queue's own pops first (<see cref="PlayerOrders.Prepare"/>), then the sources in order.
    /// </summary>
    public void Decide(World world, int slot, bool paused = false)
    {
        var cold = world.Characters.Cold(slot)!;
        _lines.Clear();
        PlayerOrders.Prepare(world, slot);
        string winner = paused ? "(paused: orders only)" : "(none)";
        for (int k = 0; k < _sources.Count;)
        {
            var step = _sources[k].Step;
            int end = k;
            while (end < _sources.Count && _sources[end].Step == step) end++;
            if (paused && step != DecisionStep.Orders) break;
            if (step == DecisionStep.Goals ? PickGoal(world, slot, k, end, ref winner) : PickFirst(world, slot, k, end, ref winner)) break;
            k = end;
        }

        if (!paused) cold.Replan = false;   // what its own start ended asks nothing more
        if (Tracing) cold.Decision = new DecisionTrace(world.Tick, winner, _lines.ToArray());
        else if (cold.Decision is not null) cold.Decision = null;
    }

    /// <summary>
    /// Asks the sources <paramref name="from"/> to <paramref name="end"/> in order and starts the first that can run (one already running only ends
    /// the step); true when one answered, its name in <paramref name="winner"/>.
    /// </summary>
    bool PickFirst(World world, int slot, int from, int end, ref string winner)
    {
        for (int k = from; k < end; k++)
        {
            var source = _sources[k];
            var check = source.CanRun(world, slot);
            if (check.Can)
            {
                Note(source, check.Running ? "runs" : "starts");
                winner = source.Name;
                if (!check.Running) source.Start(world, slot);
                return true;
            }

            Note(source, check.Why ?? "cannot run");
            source.Passed(world, slot, check);
        }

        return false;
    }

    /// <summary>
    /// Step 4: the score is desire x weight, x1.25 for the goal running now; the best wins and a tie goes to the later-listed (<c>FUN_14050df10</c>'s
    /// raised keys).
    /// </summary>
    bool PickGoal(World world, int slot, int from, int end, ref string winner)
    {
        int best = -1;
        float bestScore = 0;
        bool bestRuns = false;
        for (int k = from; k < end; k++)
        {
            var source = _sources[k];
            var check = source.CanRun(world, slot);
            if (!check.Can)
            {
                Note(source, check.Why ?? "cannot run");
                source.Passed(world, slot, check);
                continue;
            }

            float score = source.Desire(world, slot) * source.Weight * (check.Running ? 1.25f : 1);
            Note(source, score > 0 ? $"score {score:0.###}{(check.Running ? " (running)" : "")}" : "desire 0");
            if (score > 0 && score >= bestScore) (best, bestScore, bestRuns) = (k, score, check.Running);
        }

        if (best < 0) return false;
        winner = _sources[best].Name;
        if (!bestRuns) _sources[best].Start(world, slot);
        return true;
    }

    /// <summary>
    /// Adds a line on a source to the trace while <see cref="Tracing"/> is on.
    /// </summary>
    void Note(IDecisionSource source, string what)
    {
        if (Tracing) _lines.Add($"{(int)source.Step} {source.Name}: {what}");
    }

    /// <summary>
    /// Steps 3 and 5: the character's job list in order (<c>FUN_14050e5b0</c>, docs/game/ai-tasks.md "Perma-jobs"): the jobs switch gates every job
    /// but 16, 31, 45, 57, 58 and 250 (<c>FUN_14050c1c0</c>) in both passes; a job whose type has no contributor or whose row the repertoire does not
    /// register is skipped; the first job that can run wins (one that runs the character's task already ends the step), so a higher job takes over a
    /// lower one at the next decision. Step 3 asks only the jobs that are urgent now. The implicit jobs are step 5's next source
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
    /// Step 6's priority-1 goal 14 Idleness (<c>Character::setupAI</c> gives it to everyone, weight 0.1): a player character stands idle, which is
    /// its routine (player characters neither wander nor follow a leader by themselves). It runs over idling, a fight (its goal's or row's), and any
    /// task a plan still runs (an order's, a goal's, the engine's: those end by their own rules; a stale job's ended at step 5); a wander or follow
    /// no plan runs is given up for it. The default task (<c>+0x190</c>, tried after the priority-1 goals) is never reached: it is 14 Idleness too
    /// unless the manoeuvre mode is STAY_CLOSE or HOLD_GROUND, which Meitou does not model (docs/game/ai-tasks.md "Precedence").
    /// </summary>
    sealed class Idleness : IDecisionSource
    {
        /// <summary>
        /// The trace name.
        /// </summary>
        public string Name => "14 Idleness";
        /// <summary>
        /// Step 6, the fallback.
        /// </summary>
        public DecisionStep Step => DecisionStep.Fallback;

        /// <summary>
        /// The priority-1 goals come after the priority-2 ones (keys 1000 + type).
        /// </summary>
        public int Key => 2000 + (int)TaskType.Idle;

        /// <summary>
        /// It always answers: it starts over a wander or follow no plan runs, out of a fight; else it counts as running what the character does.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot)
        {
            var cold = world.Characters.Cold(slot)!;
            var task = (CharacterTask)world.Characters.Next[slot].Task;
            bool planned = cold.Running is { } run && run.Runs(task);
            return task is CharacterTask.Wander or CharacterTask.Follow && !planned && !cold.InCombat ? DecisionCheck.Run : DecisionCheck.Runs;
        }

        /// <summary>
        /// Sets the character idle as goal 14 Idleness.
        /// </summary>
        public void Start(World world, int slot) => TaskStart.Become(world, slot, CharacterTask.Idle, TaskType.Idle, TaskSource.Goal);
    }
}