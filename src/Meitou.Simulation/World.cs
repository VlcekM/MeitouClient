namespace Meitou.Simulation;

/// <summary>A contiguous range of slots handed to one task of a parallel phase.</summary>
public readonly record struct Partition(int Index, int Start, int End);

/// <summary>
/// One part of the simulation (movement, AI, combat, needs...), taking part in the tick phases of docs/simulation.md "Threading".
/// Every method has an empty default, so a system implements only the phases it uses. The rules that make a tick independent of the
/// thread count: <see cref="Think"/>, <see cref="Move"/> and <see cref="Act"/> run in parallel over <see cref="Partition"/>s; they read
/// <see cref="CharacterTable.Previous"/> and <see cref="World.Grid"/> (the last tick), write only <see cref="CharacterTable.Next"/> of
/// the slots in their partition, take randomness from <see cref="Rng"/>, and send everything else to <see cref="EffectBuffer"/>.
/// The other methods run serially, in the order the systems were added.
/// </summary>
public interface ITickSystem
{
    /// <summary>Step 1, serial: the commands due this tick, then anything else arriving from outside.</summary>
    void Inputs(World world, IReadOnlyList<SimCommand> commands) { }
    /// <summary>Step 2, serial: who gets the full update, whose AI thinks. The spatial grid of the last tick is ready.</summary>
    void Schedule(World world) { }
    /// <summary>Step 3, parallel: AI; output is intents in <c>Next</c>.</summary>
    void Think(World world, Partition partition) { }
    /// <summary>Step 4, parallel: path following, steering, separation, integration on the ground.</summary>
    void Move(World world, Partition partition) { }
    /// <summary>Step 5, parallel: combat timers, needs, production; effects on others go to <paramref name="effects"/>.</summary>
    void Act(World world, Partition partition, EffectBuffer effects) { }
    /// <summary>Step 6, serial: applies one effect this system emitted; effects arrive sorted by (target, source, system, sequence).</summary>
    void Apply(World world, in Effect effect) { }
    /// <summary>Step 7, serial: factions, squads, towns, zones; each on its own cadence (read <see cref="World.Tick"/>).</summary>
    void SlowWorld(World world) { }
}

/// <summary>A system with state of its own that the world's state hash must see (so the determinism tests cover it).</summary>
public interface IStateHashed
{
    void Hash(ref StateHasher hasher);
}

/// <summary>Settings of a <see cref="World"/>.</summary>
public sealed record WorldSettings
{
    /// <summary>The world seed; with the entity and the purpose it keys every random roll.</summary>
    public ulong Seed { get; init; }
    /// <summary>Worker threads for the parallel phases (1 runs everything on the caller). The result never depends on it.</summary>
    public int Threads { get; init; } = 1;
    /// <summary>Game seconds per tick (docs/simulation.md: 1/30).</summary>
    public float TickSeconds { get; init; } = 1f / 30;
    /// <summary>Cell size of the neighbour grid, in world units.</summary>
    public float GridCellSize { get; init; } = 100;
    /// <summary>Build a <see cref="WorldSnapshot"/> at the end of each tick. A headless test can turn it off.</summary>
    public bool PublishSnapshots { get; init; } = true;
    /// <summary>Partitions per worker thread: more evens out uneven work, fewer cuts overhead.</summary>
    public int PartitionsPerThread { get; init; } = 2;
    /// <summary>Fewest slots in a partition (small worlds are not cut into crumbs).</summary>
    public int MinPartitionSize { get; init; } = 16;
}

/// <summary>
/// The simulated world: the character table, the systems and the tick (docs/simulation.md, "Threading"). It is headless and
/// deterministic: the same settings, systems, setup, commands and tick count give the same <see cref="StateHash"/> at any
/// <see cref="WorldSettings.Threads"/>. A tick is, in order: inputs, schedule, think, move, act (parallel), commit, slow world, publish.
/// </summary>
public sealed partial class World : IDisposable
{
    readonly List<ITickSystem> systems;
    readonly WorkerPool pool;
    readonly EffectBuffer[] buffers;
    readonly List<Effect> effects = [];
    Partition[] partitions = [];
    // Per system, built once when it is added: which parallel phases it implements and the delegates that run them (no closure per tick).
    readonly List<SystemPhases> phases = [];
    IAnimationSource? animations;

    public World(WorldSettings settings, IWalkability walkability, IEnumerable<ITickSystem>? systems = null, CharacterTable? characters = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(walkability);
        Settings = settings;
        Walkability = walkability;
        this.systems = [.. systems ?? []];
        for (int k = 0; k < this.systems.Count; k++) phases.Add(new SystemPhases(this, this.systems[k], k));
        animations = this.systems.OfType<IAnimationSource>().FirstOrDefault();
        Characters = characters ?? new CharacterTable();
        Grid = new SpatialGrid(settings.GridCellSize);
        pool = new WorkerPool(settings.Threads);
        buffers = new EffectBuffer[Math.Max(1, settings.Threads * settings.PartitionsPerThread)];
        for (int i = 0; i < buffers.Length; i++) buffers[i] = new EffectBuffer();
    }

    public WorldSettings Settings { get; }
    public ulong Seed => Settings.Seed;
    public IWalkability Walkability { get; }
    public CharacterTable Characters { get; }
    /// <summary>The neighbour grid of the last tick's positions (valid from the Schedule step of a tick on).</summary>
    public SpatialGrid Grid { get; }
    /// <summary>Player and test commands, applied at the tick they are stamped with.</summary>
    public CommandQueue Commands { get; } = new();
    /// <summary>The squads (factory output, kept for the leader and follower logic).</summary>
    public SquadRegistry Squads { get; } = new();
    /// <summary>Roaming squads that persist across the loading of zones (<see cref="PopulationSystem"/>).</summary>
    public PlatoonRegistry Platoons { get; } = new();
    /// <summary>The player: faction, money, selection.</summary>
    public PlayerState Player { get; } = new();
    public IReadOnlyList<ITickSystem> Systems => systems;
    /// <summary>The number of ticks run; during a tick, the number of the tick being run.</summary>
    public long Tick { get; private set; }
    public float TickSeconds => Settings.TickSeconds;
    /// <summary>The snapshot the last tick published, and the one before it (for interpolating); <see cref="WorldSnapshot.Empty"/> before.</summary>
    public WorldSnapshot Snapshot { get; private set; } = WorldSnapshot.Empty;
    public WorldSnapshot PreviousSnapshot { get; private set; } = WorldSnapshot.Empty;

    /// <summary>Adds a system after the existing ones; before the first tick.</summary>
    public void AddSystem(ITickSystem system)
    {
        if (Tick != 0) throw new InvalidOperationException("systems are added before the first tick");
        systems.Add(system);
        phases.Add(new SystemPhases(this, system, systems.Count - 1));
        animations ??= system as IAnimationSource;
    }

    /// <summary>Runs one simulation tick.</summary>
    public void RunTick()
    {
        var table = Characters;
        table.BeginTick();

        // 1. Inputs (serial).
        table.Phase = TablePhase.Inputs;
        var due = Commands.TakeDue(Tick);
        foreach (var s in systems) s.Inputs(this, due);

        // 2. Schedule (serial): the grid of the last tick's positions, then the systems.
        table.Phase = TablePhase.Schedule;
        Grid.Build(table.Previous);
        MakePartitions();
        foreach (var s in systems) s.Schedule(this);

        // 3-5. Think, move, act (parallel; each system's phase is a barrier).
        table.Phase = TablePhase.Parallel;
        // A system that leaves a phase at its empty default is skipped there: a barrier costs more than the nothing it would wait for.
        foreach (var p in phases) if (p.Think is { } think) pool.ForEach(partitions.Length, think);
        foreach (var p in phases) if (p.Move is { } move) pool.ForEach(partitions.Length, move);
        foreach (var b in buffers) b.Reset();
        foreach (var p in phases)
        {
            if (p.Act is not { } act) continue;
            foreach (var b in buffers) b.BeginSystem(p.Index);   // Effect.System is the system's real index, so the commit order does not depend on which systems act
            pool.ForEach(partitions.Length, act);
        }

        table.Phase = TablePhase.Commit;
        // 6. Commit (serial): the effects in an order the partitioning cannot change.
        effects.Clear();
        for (int i = 0; i < partitions.Length; i++) effects.AddRange(buffers[i].Items);
        effects.Sort(EffectBuffer.Order);
        foreach (var e in effects) systems[e.System].Apply(this, e);

        Squads.Refresh(table);
        table.Phase = TablePhase.SlowWorld;
        // 7. Slow world (serial).
        foreach (var s in systems) s.SlowWorld(this);

        // 8. Publish.
        Squads.Refresh(table);
        Player.Prune(table);
        table.Phase = TablePhase.Outside;
        table.EndTick();
        Tick++;
        if (Settings.PublishSnapshots) Publish();
    }

    /// <summary>Runs <paramref name="ticks"/> ticks.</summary>
    public void RunTicks(int ticks)
    {
        for (int i = 0; i < ticks; i++) RunTick();
    }

    void MakePartitions()
    {
        int slots = Characters.HighWater;
        int wanted = Math.Max(1, Math.Min(buffers.Length, slots / Math.Max(Settings.MinPartitionSize, 1)));
        if (partitions.Length != wanted) partitions = new Partition[wanted];
        for (int i = 0; i < wanted; i++)
            partitions[i] = new Partition(i, (int)((long)slots * i / wanted), (int)((long)slots * (i + 1) / wanted));
    }

    /// <summary>A hash of the canonical state after the last tick: the tick number, every character's state in slot order and the free list. Equal worlds give equal hashes at any thread count.</summary>
    public ulong StateHash()
    {
        var h = new StateHasher();
        h.Add(Seed);
        h.Add(Tick);
        Characters.Hash(ref h);
        Squads.Hash(ref h);
        Platoons.Hash(ref h);
        Player.Hash(ref h);
        foreach (var s in systems) if (s is IStateHashed hashed) hashed.Hash(ref h);
        return h.Value;
    }

    public void Dispose() => pool.Dispose();
}
