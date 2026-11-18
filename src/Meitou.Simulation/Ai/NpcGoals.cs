using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Factions;

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
    /// started: the mod has set <see cref="CharacterCold.Engaged"/> and started its task (a row of its own from 1000 up). False: nothing started; the
    /// character stands and its next think chooses again. A throw counts as false and drops the goal for the world.
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
/// The code mods' NPC goals in one world (docs/modding.md "NPC goals"): made when the world is made, only when a mod claimed a goal type and one of its
/// systems registered a goal, and never changed after (the think's threads read it without a lock). A goal whose <see cref="INpcGoal.Desire"/> throws
/// or answers a number that is not finite faults at that tick: it still answers the rest of that tick and is not asked from the next tick on, so the
/// cut falls on a tick boundary whatever the threads did; one whose <see cref="INpcGoal.Start"/> throws is not started or asked again. The first
/// fault of each goal is logged with the mod's id; the faults are hashed.
/// </summary>
public sealed class NpcGoals
{
    #region Registry

    /// <summary>
    /// A registered goal: the mod's object, the claiming mod and the claim's name, and the tick of its first fault (<see cref="long.MaxValue"/>: none).
    /// </summary>
    sealed class Entry(INpcGoal goal, TaskType type, ModGoalType claim)
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
        /// The tick of the goal's first fault (<see cref="long.MaxValue"/>: none); set from the think's threads.
        /// </summary>
        public long Fault = long.MaxValue;
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
    /// The mod of the system registering now (<see cref="INpcGoalOwner.RegisterGoals"/>; null: a system no mod made), while
    /// <see cref="_open"/>.
    /// </summary>
    string? _registrant;

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
    /// The goals of a world's systems (<see cref="INpcGoalOwner"/>, asked in tick order); null when no mod claimed a goal type or none registered a goal,
    /// so a world without them builds nothing.
    /// </summary>
    internal static NpcGoals? Build(SimulationExtensions? extensions, IReadOnlyList<ITickSystem> systems)
    {
        if (extensions is not { GoalTypes.Count: > 0 } mods) return null;

        var goals = new NpcGoals(mods.GoalTypes);
        for (int k = 0; k < systems.Count; k++)
        {
            if (systems[k] is INpcGoalOwner owner) goals.Collect(systems[k], owner);
        }

        return goals._entries.Count > 0 ? goals : null;
    }

    /// <summary>
    /// Registers <paramref name="goal"/> for <paramref name="type"/>, a goal type the registering system's mod claimed (<c>ModContext.AddGoalType</c>).
    /// Only inside <see cref="INpcGoalOwner.RegisterGoals"/>; a type that is not claimed, claimed by another mod or registered already is refused (a
    /// throw, which drops that system's goals).
    /// </summary>
    public void Register(TaskType type, INpcGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        if (!_open) throw new InvalidOperationException("NPC goals are registered only in INpcGoalOwner.RegisterGoals.");
        if (!_claims.TryGetValue(type, out var claim)) throw new ArgumentException($"Goal type {(int)type} is not claimed (AddGoalType).", nameof(type));
        if (_registrant is not null && claim.Owner != _registrant)
            throw new ArgumentException($"Goal type {(int)type} is claimed by mod '{claim.Owner}'.", nameof(type));
        if (_byType.ContainsKey(type)) throw new ArgumentException($"Goal type {(int)type} is registered already.", nameof(type));

        var entry = new Entry(goal, type, claim);
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
    void Collect(ITickSystem system, INpcGoalOwner owner)
    {
        _registrant = SimulationExtensions.FactoryOf(system)?.Owner;
        _added.Clear();
        _open = true;
        try
        {
            owner.RegisterGoals(this);
        }
        catch (Exception e)
        {
            Drop();
            string who = _registrant is null ? system.GetType().FullName ?? "?" : $"{_registrant} '{SimulationExtensions.FactoryOf(system)?.Name}'";
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
    /// Set by a think that chose a mod's goal (<see cref="AiMind.ModGoalAsk"/>); the serial step looks for them only then.
    /// </summary>
    int _asking;

    /// <summary>
    /// The goals that faulted, for the state hash.
    /// </summary>
    int _faults;

    /// <summary>
    /// The mod goal's desire (<see cref="INpcGoal.Desire"/>) for the asker; 0 for a type no mod registered, a goal faulted before this tick, an answer
    /// at or below 0, or a fault now. Parallel: the asker's own partition.
    /// </summary>
    internal float Desire(World world, in NpcGoalAsk ask)
    {
        if (!_byType.TryGetValue(ask.Type, out var entry) || Volatile.Read(ref entry.Fault) < world.Tick) return 0;

        float desire;
        try
        {
            desire = entry.Goal.Desire(world, ask);
        }
        catch (Exception e)
        {
            Fault(entry, world.Tick, "desire", e);
            return 0;
        }

        if (float.IsFinite(desire)) return desire > 0 ? desire : 0;

        Fault(entry, world.Tick, "desire", new InvalidOperationException($"the desire {desire} is not a finite number"));
        return 0;
    }

    /// <summary>
    /// A think chose a mod's goal for <paramref name="mind"/>: the serial step of the same tick starts it (<see cref="SendStarts"/>).
    /// </summary>
    internal void Ask(AiMind mind)
    {
        mind.ModGoalAsk = true;
        Interlocked.Exchange(ref _asking, 1);
    }

    /// <summary>
    /// Starts the mod goals the thinks chose this tick (serial, slot order), for each character the AI still holds whose goal has not faulted
    /// (<see cref="INpcGoal.Start"/>); a start marks the mind (<see cref="AiMind.ModGoalStarted"/>). Only when a think asked.
    /// </summary>
    internal void SendStarts(World world)
    {
        if (Interlocked.Exchange(ref _asking, 0) == 0) return;

        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { ModGoalAsk: true } mind } cold) continue;

            mind.ModGoalAsk = false;
            if (!table.Next[s].Alive || mind.Goal is not { } goal || !AiSystem.Holds(table.Next[s], cold)) continue;
            if (!_byType.TryGetValue(goal.Task.Type, out var entry) || entry.Fault != long.MaxValue) continue;

            if (StartOne(world, entry, new NpcGoalAsk(s, table.IdOf(s), mind.Squad, goal.Task.Type, goal.Task.Target)))
                Take(mind, goal);
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
            Fault(entry, world.Tick, "start", e);
            return false;
        }
    }

    /// <summary>
    /// A started mod goal is the mind's current goal, its own one-step plan, with no commitment, until the AI holds the character again.
    /// </summary>
    static void Take(AiMind mind, AiGoal goal)
    {
        mind.Goal = goal;
        mind.Behaviour = AiBehaviour.Mod;
        mind.CommitUntil = 0;
        mind.ModGoalStarted = true;
        mind.Plan.Clear();
        mind.Plan.Add(new PlanStep(goal.Task.Type, default));
    }

    /// <summary>
    /// Records a goal's fault at <paramref name="tick"/> (the earliest wins; the threads of one tick all write the same tick) and logs the first.
    /// </summary>
    void Fault(Entry entry, long tick, string phase, Exception e)
    {
        long seen = Volatile.Read(ref entry.Fault);
        while (tick < seen)
        {
            long was = Interlocked.CompareExchange(ref entry.Fault, tick, seen);
            if (was == seen)
            {
                if (seen != long.MaxValue) return;

                Interlocked.Increment(ref _faults);
                Console.Error.WriteLine(
                    $"mod       {entry.Claim.Owner}: NPC goal {(int)entry.Type} '{entry.Claim.Name}' dropped for this world, its {phase} failed at tick {tick}: {e}");
                return;
            }

            seen = was;
        }
    }

    /// <summary>
    /// Adds the faulted goals and their ticks to the state hash; nothing while none has faulted.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
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
