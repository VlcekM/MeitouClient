using System.Globalization;
using System.Text;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Ai;

/// <summary>
/// Which characters the <see cref="ActionLog"/> follows.
/// </summary>
public enum ActionLogScope : byte
{
    /// <summary>
    /// None: the log is off.
    /// </summary>
    Off,
    /// <summary>
    /// The player's characters (<see cref="CharacterCold.IsPlayer"/>), with the decision behind each of their changes.
    /// </summary>
    Squad,
    /// <summary>
    /// Every awake character: the player's, with their decisions, and every NPC's and animal's task changes.
    /// </summary>
    Everyone,
}

/// <summary>
/// The action log (<b>engine</b>, a debugging aid, off by default; the host writes it to log_actions.txt beside log.txt): at each tick's
/// end and after the orders given while paused, a line for every followed character whose task changed: the task it left and why, the task it took up
/// and the provenance of the start (<see cref="CharacterCold.Running"/>: the source, the row, the job, the target and the site); for the player's
/// characters, the decision step's trace of that tick (<see cref="DecisionSystem.Tracing"/>, which the log turns on while it follows them). It only
/// reads the world: nothing it does is saved, hashed or seen by the simulation. Serial (the world's tick boundary).
/// </summary>
public sealed class ActionLog
{
    /// <summary>
    /// A followed character's change since the last boundary: the task it had, whether a new start was recorded (its provenance replaced) and whether
    /// its task was ended through <see cref="TaskStart.End"/>.
    /// </summary>
    readonly record struct Change(CharacterTask Was, bool Started, bool Ended);

    /// <summary>
    /// The characters followed, set from any thread (the host's debug menu); read once per <see cref="Observe"/>.
    /// </summary>
    volatile ActionLogScope _scope;
    /// <summary>
    /// The world the remembered tasks are of; another world (a load) starts afresh.
    /// </summary>
    World? _world;
    /// <summary>
    /// The table's boundary count (<see cref="CharacterTable.Ticks"/>) at the last <see cref="Observe"/>: an end stamped after it is news.
    /// </summary>
    long _boundary = -1;
    /// <summary>
    /// Per slot, the slot's generation at the last <see cref="Observe"/> (-1: not followed then).
    /// </summary>
    int[] _generation = [];
    /// <summary>
    /// The task each slot had at the last <see cref="Observe"/>.
    /// </summary>
    byte[] _task = [];
    /// <summary>
    /// The provenance each slot had at the last <see cref="Observe"/>.
    /// </summary>
    RunningTask?[] _running = [];
    /// <summary>
    /// The decision trace last written for each slot: one is written once, though a paused step and the tick after it share a number.
    /// </summary>
    DecisionTrace?[] _decision = [];
    /// <summary>
    /// Goal 148's note last written for each slot (<see cref="AiMind.BedRescueNote"/>).
    /// </summary>
    BedRescueNote[] _rescue = [];
    /// <summary>
    /// The line being built.
    /// </summary>
    readonly StringBuilder _line = new();

    /// <summary>
    /// The log written to (the host's file); null: nothing is written.
    /// </summary>
    public TextWriter? Writer { get; set; }

    /// <summary>
    /// Which characters are followed (<see cref="ActionLogScope.Off"/> by default).
    /// </summary>
    public ActionLogScope Scope
    {
        get => _scope;
        set => _scope = value;
    }

    /// <summary>
    /// Writes the followed characters' task changes since the last call (the world's tick boundary: the end of a tick, or the orders given while
    /// paused). The first call on a world, or the first after the scope widened, only remembers the tasks.
    /// </summary>
    public void Observe(World world)
    {
        var scope = _scope;
        world.Decisions.Tracing = scope != ActionLogScope.Off;
        if (scope == ActionLogScope.Off || Writer is not { } writer)
        {
            _world = null;   // switched on again, it starts from what the characters do then
            return;
        }

        Follow(world);
        var table = world.Characters;
        var state = table.Previous;   // the tick boundary: the state just computed
        bool wrote = false;
        for (int slot = 0; slot < state.Length; slot++)
        {
            var cold = table.Cold(slot);
            if (cold is null || !Followed(state[slot], cold, scope))
            {
                _generation[slot] = -1;
                _rescue[slot] = BedRescueNote.None;
                continue;
            }

            if (scope == ActionLogScope.Everyone) wrote |= WriteRescue(writer, world, slot, cold);
            if (Remember(slot, state[slot], cold.Running) is not { } change) continue;
            var now = (CharacterTask)state[slot].Task;
            WriteChange(writer, world, slot, cold, change with { Ended = cold.EndedTick > _boundary }, now);
            wrote = true;
        }

        _boundary = table.Ticks;
        if (wrote) writer.Flush();
    }

    /// <summary>
    /// Starts afresh on another world (a load) and makes room for every slot of the table.
    /// </summary>
    void Follow(World world)
    {
        if (!ReferenceEquals(_world, world))
        {
            _world = world;
            Array.Fill(_generation, -1);
            Array.Fill(_rescue, BedRescueNote.None);
        }

        int high = world.Characters.HighWater;
        if (_generation.Length >= high) return;
        int size = Math.Max(high, _generation.Length * 2);
        int old = _generation.Length;
        Array.Resize(ref _generation, size);
        Array.Resize(ref _task, size);
        Array.Resize(ref _running, size);
        Array.Resize(ref _decision, size);
        Array.Resize(ref _rescue, size);
        Array.Fill(_generation, -1, old, size - old);
    }

    /// <summary>
    /// Whether the scope follows the character: an awake one, the player's only for <see cref="ActionLogScope.Squad"/>.
    /// </summary>
    static bool Followed(in CharacterHot hot, CharacterCold cold, ActionLogScope scope) =>
        hot.Alive && !hot.Dormant && (scope == ActionLogScope.Everyone || cold.IsPlayer);

    /// <summary>
    /// Remembers the slot's task and provenance; returns the change when either changed since the last call, else null (also for one newly followed:
    /// spawned, woken, or the scope widened, with nothing to compare with).
    /// </summary>
    Change? Remember(int slot, in CharacterHot hot, RunningTask? running)
    {
        bool known = _generation[slot] == hot.Generation;
        if (known && _task[slot] == hot.Task && ReferenceEquals(_running[slot], running)) return null;

        var change = new Change((CharacterTask)_task[slot], running is not null && !ReferenceEquals(_running[slot], running), false);
        (_generation[slot], _task[slot], _running[slot]) = (hot.Generation, hot.Task, running);
        return known ? change : null;
    }

    /// <summary>
    /// A line when goal 148's note of a character whose lists weigh it changed since the last one written (<see cref="AiMind.BedRescueNote"/>): whether it
    /// is eligible, wanted but not chosen (with the goal chosen instead), or lifting. True when it wrote.
    /// </summary>
    bool WriteRescue(TextWriter writer, World world, int slot, CharacterCold cold)
    {
        var note = cold.Ai?.BedRescueNote ?? BedRescueNote.None;
        if (note == _rescue[slot]) return false;

        _rescue[slot] = note;
        if (note == BedRescueNote.None) return false;

        var line = _line.Clear();
        line.Append(EventLog.Stamp(world)).Append(EventLog.Who(world, slot)).Append(": rescue 148, ").Append(RescueText(note));
        if (note == BedRescueNote.NotChosen && cold.Ai?.Goal is { } goal) line.Append(": its goal is ").Append(RowText(goal.Task.Type));
        writer.WriteLine(line.ToString());
        return true;
    }

    /// <summary>
    /// What a goal 148 note means.
    /// </summary>
    static string RescueText(BedRescueNote note) => note switch
    {
        BedRescueNote.CannotLift => "not eligible: on Hold, carrying, or not able to lift",
        BedRescueNote.NobodyDown => "not eligible: nobody is down",
        BedRescueNote.NoBody => "not eligible: no downed ally its finder takes",
        BedRescueNote.NoBed => "not eligible: no bed for it",
        BedRescueNote.NoPutIn => "not eligible: the ally found is one it would not lay in a bed",
        BedRescueNote.NotChosen => "wanted, not chosen",
        BedRescueNote.Lifting => "chosen, lifting",
        _ => note.ToString(),
    };

    /// <summary>
    /// The line of one change: when, who, the task left and why, the task taken up and its provenance; then the decision behind it.
    /// </summary>
    void WriteChange(TextWriter writer, World world, int slot, CharacterCold cold, Change change, CharacterTask now)
    {
        var line = _line.Clear();
        line.Append(EventLog.Stamp(world)).Append(EventLog.Who(world, slot)).Append(": ");
        if (change.Was != now) line.Append(change.Was).Append(" -> ").Append(now);
        else line.Append(now).Append(" (started again)");

        if (change.Ended) line.Append(", left it: ").Append(EndText(cold.Ended));
        AppendCause(line, world, cold, change, now);
        writer.WriteLine(line.ToString());
        WriteDecision(writer, world, slot, cold);
    }

    /// <summary>
    /// What took the character to its task: the new start's provenance, a step of the running row, else its own system (unless an end explains it).
    /// </summary>
    static void AppendCause(StringBuilder line, World world, CharacterCold cold, Change change, CharacterTask now)
    {
        var run = cold.Running;
        if (run is not null && run.Runs(now))
        {
            if (change.Started) AppendStart(line, world, cold, run);
            else line.Append(", a step of ").Append(RowText(run.Row));
        }
        else if (!change.Ended)
        {
            line.Append(", ended by itself (a walk arrived, a fight or a work step ended)");
        }
    }

    /// <summary>
    /// The decision step's trace behind the change (the player's characters), once: the one of this boundary, not written before.
    /// </summary>
    void WriteDecision(TextWriter writer, World world, int slot, CharacterCold cold)
    {
        if (cold.Decision is not { } decision || decision.Tick != world.Tick || ReferenceEquals(decision, _decision[slot]))
            return;

        _decision[slot] = decision;
        writer.WriteLine($"    decided: {decision.Winner}");
        foreach (var reason in decision.Lines) writer.WriteLine("      " + reason);
    }

    /// <summary>
    /// The start's provenance: what asked for it, the row, the job of the list, the target and the site.
    /// </summary>
    static void AppendStart(StringBuilder line, World world, CharacterCold cold, RunningTask run)
    {
        line.Append(", started by ").Append(SourceText(run.Source));
        if (run.Source == TaskSource.Job)
        {
            if (run.Implicit) line.Append(" (implicit)");
            else if (run.Job >= 0 && run.Job < cold.Jobs.Count)
                line.Append(' ').Append((run.Job + 1).ToString(CultureInfo.InvariantCulture)).Append(" of the list");
        }
        line.Append(" as ").Append(RowText(run.Row));
        if (run.Target != CharacterId.None)
        {
            var target = world.Characters.IsAlive(run.Target) ? world.Characters.Cold(run.Target.Slot) : null;
            line.Append(", target ").Append(target?.Name is { Length: > 0 } name ? name : "(gone)")
                .Append(" #").Append(run.Target.Slot.ToString(CultureInfo.InvariantCulture));
        }
        if (run.Site is { } site) line.Append(", at ").Append(site);
    }

    /// <summary>
    /// A row as the game names it, with its number: "Staying close (31)".
    /// </summary>
    static string RowText(TaskType row) =>
        TaskDataTable.Of(row) is { Text.Length: > 0 } data ? $"{data.Text} ({(int)row})" : $"{row} ({(int)row})";

    /// <summary>
    /// What a <see cref="TaskSource"/> stands for.
    /// </summary>
    static string SourceText(TaskSource source) => source switch
    {
        TaskSource.Order => "an order",
        TaskSource.Goal => "a goal",
        TaskSource.Job => "a job",
        TaskSource.Reaction => "an answer to an attack",
        TaskSource.Npc => "the engine (NPC)",
        _ => source.ToString(),
    };

    /// <summary>
    /// Why a task ended (<see cref="TaskEnd"/>).
    /// </summary>
    static string EndText(TaskEnd end) => end switch
    {
        TaskEnd.Done => "done",
        TaskEnd.Stopped => "stopped by order",
        TaskEnd.Replaced => "replaced",
        _ => end.ToString(),
    };
}
