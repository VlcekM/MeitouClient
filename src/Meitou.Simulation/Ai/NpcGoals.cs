using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Ai;

/// <summary>
/// One NPC and one of its package's goals, as a mod's goal is asked about it (<see cref="INpcGoal"/>): the character's slot and id, its squad (its
/// blackboard is <see cref="Squad.Ai"/>), the goal type and the goal's <c>targeting</c>.
/// </summary>
public readonly record struct NpcGoalAsk(int Slot, CharacterId Id, Squad Squad, TaskType Type, TaskTargetType Target);

/// <summary>
/// A goal type of a code mod's own that NPCs weigh in the AI's chooser beside their package's other goals (docs/modding.md "NPC goals"), for the type
/// the mod claimed (<c>ModContext.AddGoalType</c>) and registered (<see cref="NpcGoals.Register"/>).
/// </summary>
public interface INpcGoal
{
    /// <summary>
    /// How much the NPC wants the goal now (its desire times what its target finder found): above 0 the chooser weighs it by the package's weight,
    /// tier, hours and continuity; 0 or below, not wanted. Asked in the AI's parallel think, on the asker's own partition: a pure read of the world
    /// (the character's own slot and cold state, <c>Previous</c> for others, the squad's blackboard) and of the mod's own state that only its serial
    /// steps change. A throw or an answer that is not a finite number drops the goal for the world from the next tick.
    /// </summary>
    float Desire(World world, in NpcGoalAsk ask);

    /// <summary>
    /// The NPC chose the goal: start its act-out (serial, slot order, the same tick, only while the AI still holds the character). True when it
    /// started: the mod has set <see cref="CharacterCold.Engaged"/> and started its task (a row of its own from 1000 up,
    /// <see cref="TaskSource.Goal"/>). False: nothing started; the character weighs the goal at 0 for a few thinks and chooses again at once. A throw
    /// counts as false and drops the goal for the world.
    /// </summary>
    bool Start(World world, in NpcGoalAsk ask);
}

/// <summary>
/// A system that registers NPC goals for the goal types its mod claimed, once when the world is made.
/// </summary>
public interface INpcGoalOwner
{
    /// <summary>
    /// Registers its goals with <paramref name="goals"/> (<see cref="NpcGoals.Register"/>).
    /// </summary>
    void RegisterGoals(NpcGoals goals);
}

/// <summary>
/// The code mods' NPC goals in one world (docs/modding.md "NPC goals"): made when the world is made, only when a mod claimed a goal type and one of
/// its systems registered a goal, and never changed after (the think's threads read it without a lock). A goal whose <see cref="INpcGoal.Desire"/>
/// throws or answers a number that is not finite faults at that tick: it still answers the rest of that tick and is not asked from the next tick on,
/// so the cut falls on a tick boundary whatever the threads did, and no start of it is asked from then (that tick's included); one whose
/// <see cref="INpcGoal.Start"/> throws is not started or asked again. The first fault of each goal is logged with the mod's id and the player gets a
/// notice; the faults are hashed. A goal whose registering system the world stopped after a fault (<see cref="ModSystemFaults"/>) is dropped the
/// same way, and the characters it holds (a record per started character, kept until the mod lets it go) are let go by the AI.
/// </summary>
public sealed class NpcGoals
{
    #region Registry

    /// <summary>
    /// A registered goal: the mod's object, its type, the claiming mod and the claim's name, the registering system's index in the world, the tick of
    /// its first fault (<see cref="long.MaxValue"/>: none) and whether its system was found stopped.
    /// </summary>
    sealed class Entry(INpcGoal goal, TaskType type, ModGoalType claim, int system)
    {
        /// <summary>
        /// The mod's goal.
        /// </summary>
        public INpcGoal Goal { get; } = goal;
        /// <summary>
        /// The goal type.
        /// </summary>
        public TaskType Type { get; } = type;
        /// <summary>
        /// The claiming mod and the claim's name.
        /// </summary>
        public ModGoalType Claim { get; } = claim;
        /// <summary>
        /// The registering system's index among the world's systems (<see cref="ModSystemFaults"/>' index).
        /// </summary>
        public int System { get; } = system;
        /// <summary>
        /// The tick of the goal's first fault (<see cref="long.MaxValue"/>: none); set from the think's threads.
        /// </summary>
        public long Fault = long.MaxValue;
        /// <summary>
        /// Its system was found stopped and its characters let go (serial only).
        /// </summary>
        public bool OwnerStopped;
        /// <summary>
        /// The starts the mod refused in this world (serial only; for the one-time log line, not hashed).
        /// </summary>
        public int Refusals;
        /// <summary>
        /// The mod's start-contract warnings already logged (<see cref="Warning"/> bits; serial only, not hashed).
        /// </summary>
        public Warning Warned;
    }

    /// <summary>
    /// A start that broke <see cref="INpcGoal.Start"/>'s contract, logged once per goal.
    /// </summary>
    [Flags]
    enum Warning : byte
    {
        /// <summary>
        /// None.
        /// </summary>
        None = 0,
        /// <summary>
        /// It answered true without engaging the character.
        /// </summary>
        NotEngaged = 1,
        /// <summary>
        /// It answered true without starting a task of a row from 1000 up with <see cref="TaskSource.Goal"/>.
        /// </summary>
        NoTask = 2,
        /// <summary>
        /// It engaged the character and answered false.
        /// </summary>
        EngagedRefusal = 4,
        /// <summary>
        /// Its starts keep answering false (<see cref="RefusalsLogged"/>).
        /// </summary>
        Refusing = 8,
    }

    /// <summary>
    /// A character a mod's goal holds (started, not yet let go): who, the goal, the row and start tick of the task the start left running, and
    /// whether its system stopped, so the AI lets the character go itself.
    /// </summary>
    sealed class Held(CharacterId id, Entry goal, TaskType row, long tick)
    {
        /// <summary>
        /// The character.
        /// </summary>
        public CharacterId Id { get; } = id;
        /// <summary>
        /// The goal.
        /// </summary>
        public Entry Goal { get; } = goal;
        /// <summary>
        /// The row of the task the start left running (<see cref="TaskType.None"/>: none).
        /// </summary>
        public TaskType Row { get; } = row;
        /// <summary>
        /// The start tick of that task.
        /// </summary>
        public long Tick { get; } = tick;
        /// <summary>
        /// The goal's system stopped: the AI releases the character once nothing else holds it (<see cref="TryRelease"/>).
        /// </summary>
        public bool Orphaned { get; set; }
    }

    /// <summary>
    /// The goal types the mods claimed, by type.
    /// </summary>
    readonly IReadOnlyDictionary<TaskType, ModGoalType> _claims;

    /// <summary>
    /// The registered goals by type.
    /// </summary>
    readonly Dictionary<TaskType, Entry> _byType = [];

    /// <summary>
    /// The registered goals in registration order (systems in tick order), for the hash.
    /// </summary>
    readonly List<Entry> _entries = [];

    /// <summary>
    /// The mod of the system registering now (<see cref="INpcGoalOwner.RegisterGoals"/>; null: a system no mod made, such as a test's or a tool's
    /// own wiring, which may register any claimed type), while <see cref="_open"/>.
    /// </summary>
    string? _registrant;

    /// <summary>
    /// The index of the system registering now.
    /// </summary>
    int _system;

    /// <summary>
    /// Whether a system's <see cref="INpcGoalOwner.RegisterGoals"/> runs now: registering is allowed only then.
    /// </summary>
    bool _open;

    /// <summary>
    /// The goals the system registering now added, dropped again when it throws.
    /// </summary>
    readonly List<TaskType> _added = [];

    /// <summary>
    /// A registry over the claimed goal types.
    /// </summary>
    NpcGoals(IReadOnlyDictionary<TaskType, ModGoalType> claims) => _claims = claims;

    /// <summary>
    /// The goals of a world's systems (<see cref="INpcGoalOwner"/>, asked in tick order); null when no mod claimed a goal type or none registered a
    /// goal, so a world without them builds nothing.
    /// </summary>
    internal static NpcGoals? Build(SimulationExtensions? extensions, IReadOnlyList<ITickSystem> systems)
    {
        if (extensions is not { GoalTypes.Count: > 0 } mods) return null;

        var goals = new NpcGoals(mods.GoalTypes);
        for (int k = 0; k < systems.Count; k++)
        {
            if (systems[k] is INpcGoalOwner owner) goals.Collect(systems[k], k, owner);
        }

        return goals._entries.Count > 0 ? goals : null;
    }

    /// <summary>
    /// Registers <paramref name="goal"/> for <paramref name="type"/>, a goal type the registering system's mod claimed
    /// (<c>ModContext.AddGoalType</c>). Only inside <see cref="INpcGoalOwner.RegisterGoals"/>; a type that is not claimed, claimed by another mod or
    /// registered already is refused (a throw, which drops that system's goals).
    /// </summary>
    public void Register(TaskType type, INpcGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        if (!_open) throw new InvalidOperationException("NPC goals are registered only in INpcGoalOwner.RegisterGoals.");
        if (!_claims.TryGetValue(type, out var claim))
            throw new ArgumentException($"Goal type {(int)type} is not claimed (AddGoalType).", nameof(type));
        if (_registrant is not null && claim.Owner != _registrant)
            throw new ArgumentException($"Goal type {(int)type} is claimed by mod '{claim.Owner}'.", nameof(type));
        if (_byType.ContainsKey(type)) throw new ArgumentException($"Goal type {(int)type} is registered already.", nameof(type));

        var entry = new Entry(goal, type, claim, _system);
        _byType[type] = entry;
        _entries.Add(entry);
        _added.Add(type);
    }

    /// <summary>
    /// Whether a mod registered a goal for <paramref name="type"/>.
    /// </summary>
    internal bool Has(TaskType type) => _byType.ContainsKey(type);

    /// <summary>
    /// One system's goals; a throw drops the ones it registered, with a log line.
    /// </summary>
    void Collect(ITickSystem system, int index, INpcGoalOwner owner)
    {
        var factory = SimulationExtensions.FactoryOf(system);
        _registrant = factory?.Owner;
        _system = index;
        _added.Clear();
        _open = true;
        try
        {
            owner.RegisterGoals(this);
        }
        catch (Exception e)
        {
            Drop();
            string who = factory is null ? system.GetType().FullName ?? "?" : $"{factory.Owner} '{factory.Name}'";
            Console.Error.WriteLine($"mod       {who}: NPC goals not registered for this world: {e}");
        }
        finally
        {
            _open = false;
        }
    }

    /// <summary>
    /// Drops the goals the system registering now added.
    /// </summary>
    void Drop()
    {
        foreach (var type in _added)
        {
            _entries.Remove(_byType[type]);
            _byType.Remove(type);
        }
    }

    #endregion

    #region Asking

    /// <summary>
    /// The refusals of one goal after which the log says its starts keep answering false (once per goal).
    /// </summary>
    const int RefusalsLogged = 10;

    /// <summary>
    /// Set by a think that chose a mod's goal (<see cref="AiMind.ModGoalAsk"/>); the serial step looks for them only then.
    /// </summary>
    int _asking;

    /// <summary>
    /// The goals that faulted, for the state hash.
    /// </summary>
    int _faults;

    /// <summary>
    /// The goals whose system was found stopped (<see cref="Entry.OwnerStopped"/>).
    /// </summary>
    int _stopped;

    /// <summary>
    /// The characters the goals hold, by slot (hashed while any).
    /// </summary>
    readonly SortedDictionary<int, Held> _held = [];

    /// <summary>
    /// Scratch: the slots whose record ended in a sweep.
    /// </summary>
    readonly List<int> _ended = [];

    /// <summary>
    /// The mod goal's desire (<see cref="INpcGoal.Desire"/>) for the asker; 0 for a type no mod registered, a goal faulted before this tick or whose
    /// system the world stopped, an answer at or below 0, or a fault now. Parallel: the asker's own partition.
    /// </summary>
    internal float Desire(World world, in NpcGoalAsk ask)
    {
        if (!_byType.TryGetValue(ask.Type, out var entry) || Volatile.Read(ref entry.Fault) < world.Tick) return 0;

        // A stopped system changes only at a serial step (ModSystemFaults.Settle), so every thread of a tick reads the same answer.
        if (world.ModFaults.IsStopped(entry.System)) return 0;

        float desire;
        try
        {
            desire = entry.Goal.Desire(world, ask);
        }
        catch (Exception e)
        {
            Fault(world, entry, "desire", e);
            return 0;
        }

        if (float.IsFinite(desire)) return desire > 0 ? desire : 0;

        Fault(world, entry, "desire", new InvalidOperationException($"the desire {desire} is not a finite number"));
        return 0;
    }

    /// <summary>
    /// A think chose a mod's goal for <paramref name="mind"/>: the serial step of the same tick starts it (<see cref="Step"/>).
    /// </summary>
    internal void Ask(AiMind mind)
    {
        mind.ModGoalAsk = true;
        Interlocked.Exchange(ref _asking, 1);
    }

    /// <summary>
    /// The AI's serial step: the goals of systems stopped since the last step are dropped (<see cref="LetGoStopped"/>), the records of the held
    /// characters are swept (<see cref="Sweep"/>: let go by their mod, taken by another mod's act-out, released for a stopped system), then the goals
    /// the thinks chose this tick start (<see cref="SendStarts"/>); a refused start keeps the goal at 0 for that character for
    /// <paramref name="backoffTicks"/>.
    /// </summary>
    internal void Step(World world, int backoffTicks)
    {
        if (_stopped < _entries.Count) LetGoStopped(world);
        if (_held.Count > 0) Sweep(world);
        if (Interlocked.Exchange(ref _asking, 0) != 0) SendStarts(world, backoffTicks);
    }

    /// <summary>
    /// Starts the mod goals the thinks chose this tick (serial, slot order), for each character the AI still holds whose goal has not faulted
    /// (<see cref="INpcGoal.Start"/>); a start is recorded (<see cref="Take"/>), a refusal backs the goal off for the character
    /// (<see cref="Refuse"/>), and a character skipped chooses again at the next tick.
    /// </summary>
    void SendStarts(World world, int backoffTicks)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { ModGoalAsk: true } mind } cold) continue;

            mind.ModGoalAsk = false;
            if (!table.Next[s].Alive) continue;
            if (mind.Goal is not { } goal || !AiSystem.Holds(table.Next[s], cold) || !_byType.TryGetValue(goal.Task.Type, out var entry)
                || entry.Fault != long.MaxValue || entry.OwnerStopped)
            {
                mind.Replan = true;
                continue;
            }

            if (StartOne(world, entry, new NpcGoalAsk(s, table.IdOf(s), mind.Squad, goal.Task.Type, goal.Task.Target)))
                Take(world, s, cold, mind, goal, entry);
            else
                Refuse(world, cold, mind, entry, backoffTicks);
        }
    }

    /// <summary>
    /// One start; a throw is a fault and counts as no start.
    /// </summary>
    bool StartOne(World world, Entry entry, in NpcGoalAsk ask)
    {
        try
        {
            return entry.Goal.Start(world, ask);
        }
        catch (Exception e)
        {
            Fault(world, entry, "start", e);
            return false;
        }
    }

    /// <summary>
    /// A started mod goal is the mind's current goal, its own one-step plan, with no commitment, and the character is recorded as the goal's with the
    /// row and start tick of the task the start left running, which tell the act-out from any later hold (<see cref="AiSystem.OnModTask"/>). A start
    /// that did not engage the character or start a task of a row from 1000 up with <see cref="TaskSource.Goal"/> is logged once per goal.
    /// </summary>
    void Take(World world, int slot, CharacterCold cold, AiMind mind, AiGoal goal, Entry entry)
    {
        var run = cold.Running;
        var row = run?.Row ?? TaskType.None;
        long tick = run?.Tick ?? world.Tick;
        if (!cold.Engaged) Warn(entry, Warning.NotEngaged, "answered true without engaging the character (CharacterCold.Engaged)");
        if (run is not { Source: TaskSource.Goal } || run.Tick != world.Tick || (int)row < SimulationExtensions.FirstModCode)
            Warn(entry, Warning.NoTask, "answered true without starting a task of its own row from 1000 up with TaskSource.Goal");

        mind.Goal = goal;
        mind.Behaviour = AiBehaviour.Mod;
        mind.CommitUntil = 0;
        mind.ModGoalStarted = true;
        mind.ModGoalRow = row;
        mind.ModGoalTick = tick;
        mind.Plan.Clear();
        mind.Plan.Add(new PlanStep(goal.Task.Type, default));
        _held[slot] = new Held(world.Characters.IdOf(slot), entry, row, tick);
    }

    /// <summary>
    /// A refused start: the character weighs the goal at 0 for <paramref name="backoffTicks"/> and thinks again at the next tick, so it takes another
    /// goal rather than standing for the one the mod will not start. A goal whose starts keep answering false, and a refusal that left the character
    /// engaged, are logged once per goal.
    /// </summary>
    static void Refuse(World world, CharacterCold cold, AiMind mind, Entry entry, int backoffTicks)
    {
        mind.BackOff(entry.Type, world.Tick + backoffTicks);
        mind.Replan = true;
        if (cold.Engaged) Warn(entry, Warning.EngagedRefusal, "engaged the character (CharacterCold.Engaged) and answered false");
        if (++entry.Refusals == RefusalsLogged)
            Warn(entry, Warning.Refusing, $"has refused {RefusalsLogged} starts; each keeps the goal off that character for four think periods");
    }

    /// <summary>
    /// Logs a broken start contract once per goal (not a fault: the goal keeps running).
    /// </summary>
    static void Warn(Entry entry, Warning warning, string text)
    {
        if ((entry.Warned & warning) != 0) return;

        entry.Warned |= warning;
        Console.Error.WriteLine($"mod       {entry.Claim.Owner}: NPC goal {(int)entry.Type} '{entry.Claim.Name}': its start {text}");
    }

    /// <summary>
    /// The goals whose registering system the world has stopped (a fault in one of its tick phases): each is dropped as a fault would drop it (no
    /// player notice: the system's own stop gave one), and every character it holds is marked for the AI to let go (<see cref="TryRelease"/>), since
    /// the stopped system will never do it.
    /// </summary>
    void LetGoStopped(World world)
    {
        foreach (var entry in _entries)
        {
            if (entry.OwnerStopped || !world.ModFaults.IsStopped(entry.System)) continue;

            entry.OwnerStopped = true;
            _stopped++;
            Fault(world, entry, "system", new InvalidOperationException("the mod's system that registered it stopped"), notice: false);
            foreach (var held in _held.Values)
            {
                if (held.Goal == entry) held.Orphaned = true;
            }
        }
    }

    /// <summary>
    /// The records of the held characters, in slot order: one whose character is gone, dead or no longer engaged (the mod let it go), or whose
    /// running task is another mod's act-out started since, ends; an orphaned one ends once the AI let its character go (<see cref="TryRelease"/>).
    /// A record that ends also ends the goal on the character's mind when the mind is still the one it was started on.
    /// </summary>
    void Sweep(World world)
    {
        var table = world.Characters;
        _ended.Clear();
        foreach (var (slot, held) in _held)
        {
            if (!table.TryResolveNext(held.Id, out int at) || at != slot || !table.Next[slot].Alive || table.Cold(slot) is not { } cold)
                _ended.Add(slot);
            else if (held.Orphaned ? TryRelease(world, slot, cold, held) : Ended(cold, held))
            {
                EndOnMind(cold, held);
                _ended.Add(slot);
            }
        }

        foreach (int slot in _ended) _held.Remove(slot);
    }

    /// <summary>
    /// Whether a live record ended: its character is no longer engaged, or runs a task of another row from 1000 up with <see cref="TaskSource.Goal"/>
    /// started since the record's (another mod's act-out, or the mod's own duty route on another row).
    /// </summary>
    static bool Ended(CharacterCold cold, Held held) =>
        !cold.Engaged || cold.Running is { Source: TaskSource.Goal } run && (int)run.Row >= SimulationExtensions.FirstModCode && run.Row != held.Row
            && run.Tick > held.Tick;

    /// <summary>
    /// An orphaned record (its goal's system stopped): true when it is done. One no longer engaged is done. One in a fight, an open conversation or
    /// a trade waits for it to end. Otherwise, when the character's provenance is still the act-out's (the start's row from its tick, walking,
    /// arrived or woken), a fight's started since, or none, the AI lets it go: no longer engaged, a walk on that row stood still (no handler runs:
    /// the mod's system is stopped); a task another system started since is left to it.
    /// </summary>
    static bool TryRelease(World world, int slot, CharacterCold cold, Held held)
    {
        if (!cold.Engaged) return true;
        if (cold.InCombat || HeldElsewhere(world, held.Id)) return false;

        var run = cold.Running;
        bool own = run is null || run.Tick >= held.Tick && (run.Row == held.Row || run.Task == CharacterTask.Attack);
        if (!own) return true;

        cold.Engaged = false;
        if (run is not null && world.Characters.Next[slot].Task == (byte)CharacterTask.GoTo && run.Runs(CharacterTask.GoTo))
            TaskStart.Stand(world.Characters, slot);

        if (cold.Ai is { } mind) mind.Replan = true;
        return true;
    }

    /// <summary>
    /// Whether an open conversation (a bubble or the player's) or a trade holds the character, which a release must not break.
    /// </summary>
    static bool HeldElsewhere(World world, CharacterId id) =>
        world.SystemOf<Dialogue.DialogueSystem>()?.Talking(id) == true || world.SystemOf<Trading.TradeSystem>()?.HoldsTrader(id) == true;

    /// <summary>
    /// Ends the goal on the character's mind when that mind still carries this record's start (<see cref="AiSystem.EndModGoal"/>), and asks it to
    /// choose again.
    /// </summary>
    static void EndOnMind(CharacterCold cold, Held held)
    {
        if (cold.Ai is not { ModGoalStarted: true } mind || mind.ModGoalRow != held.Row || mind.ModGoalTick != held.Tick) return;

        AiSystem.EndModGoal(mind);
        mind.Replan = true;
    }

    /// <summary>
    /// Records a goal's fault at the world's tick (the earliest wins; the threads of one tick all write the same tick), logs the first and gives the
    /// player a notice of it, as a mod system's fault does (<see cref="ModSystemFaults"/>).
    /// </summary>
    void Fault(World world, Entry entry, string phase, Exception e, bool notice = true)
    {
        long tick = world.Tick;
        long seen = Volatile.Read(ref entry.Fault);
        while (tick < seen)
        {
            long was = Interlocked.CompareExchange(ref entry.Fault, tick, seen);
            if (was == seen)
            {
                if (seen != long.MaxValue) return;

                Interlocked.Increment(ref _faults);
                Report(world, entry, phase, e, notice);
                return;
            }

            seen = was;
        }
    }

    /// <summary>
    /// A goal's first fault: the log line and, unless <paramref name="notice"/> is false, the player's notice.
    /// </summary>
    static void Report(World world, Entry entry, string phase, Exception e, bool notice)
    {
        var (owner, name) = (entry.Claim.Owner, entry.Claim.Name);
        Console.Error.WriteLine(
            $"mod       {owner}: NPC goal {(int)entry.Type} '{name}' dropped for this world, its {phase} failed at tick {world.Tick}: {e}");
        if (!notice) return;

        string text = $"Mod {owner}: its NPC goal {name} stopped after an error ({e.GetType().Name}); the rest of the game goes on.";
        if (e is MissingMemberException or TypeLoadException) text += " The mod was built for another version of the game and needs an update.";
        world.ModFaults.Notify(text);
    }

    /// <summary>
    /// Adds the held characters' records, the faulted goals and their ticks to the state hash; nothing while none is held and none has faulted.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        if (_held.Count > 0)
        {
            hasher.Add(0x4E50484C);   // "NPHL"
            foreach (var (slot, held) in _held)
            {
                hasher.Add(slot);
                hasher.Add(held.Id);
                hasher.Add((int)held.Goal.Type);
                hasher.Add((int)held.Row);
                hasher.Add(held.Tick);
                hasher.Add(held.Orphaned);
            }
        }

        if (Volatile.Read(ref _faults) == 0) return;

        hasher.Add(0x4E50474C);   // "NPGL"
        hasher.Add(_faults);
        foreach (var entry in _entries)
        {
            if (entry.Fault == long.MaxValue) continue;

            hasher.Add((int)entry.Type);
            hasher.Add(entry.Fault);
        }
    }

    #endregion
}
