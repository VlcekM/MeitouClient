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
    /// <summary>
    /// Step 5, parallel, right before this system's <see cref="Act"/> with a barrier between: the slots' own state that the system's <see cref="Act"/> reads of other slots (combat's
    /// body step), so each slot's Act sees every slot's update of this tick whatever the partitions and thread count. Writes only its own slots; no effects.
    /// </summary>
    void BeforeAct(World world, Partition partition) { }
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
    /// <summary>Cell size of the neighbour grid, in world units: a query scans whole cells, so near a crowd separation and fight steering pay for every body in them.</summary>
    public float GridCellSize { get; init; } = 25;
    /// <summary>Build a <see cref="WorldSnapshot"/> at the end of each tick. A headless test can turn it off.</summary>
    public bool PublishSnapshots { get; init; } = true;
    /// <summary>Partitions per worker thread: more evens out uneven work, fewer cuts overhead.</summary>
    public int PartitionsPerThread { get; init; } = 2;
    /// <summary>Fewest slots in a partition (small worlds are not cut into crumbs).</summary>
    public int MinPartitionSize { get; init; } = 16;
    /// <summary>The game hours at tick 0 (day x 24 + hour; the default is day 1 at 13:00, the host's new game): <see cref="World.GameHours"/> starts there.</summary>
    public double StartHours { get; init; } = 37;
    /// <summary>The CONSTANTS <c>sunrise</c> and <c>sunset</c> (game hours) the daylight factor ramps at (<see cref="SightMap.Daylight"/>).</summary>
    public float Sunrise { get; init; } = 5;
    public float Sunset { get; init; } = 23;
}

/// <summary>
/// The simulated world: the character table, the systems and the tick (docs/simulation.md, "Threading"). It is headless and
/// deterministic: the same settings, systems, setup, commands and tick count give the same <see cref="StateHash"/> at any
/// <see cref="WorldSettings.Threads"/>. A tick is, in order: inputs, schedule, think, move, act (parallel), commit, slow world, publish.
/// </summary>
/// <summary>A line of the DIALOGUE log (<see cref="World.Said"/>): who said it, the text, and whether the player's side said it.</summary>
public readonly record struct SaidLine(string Owner, string Text, bool Player);

public sealed partial class World : IDisposable
{
    readonly List<ITickSystem> systems;
    readonly WorkerPool pool;
    readonly EffectBuffer[] buffers;
    readonly List<Effect> effects = [];
    Partition[] partitions = [];
    // Per system, built once when it is added: which parallel phases it implements and the delegates that run them (no closure per tick).
    readonly List<SystemPhases> phases = [];
    readonly List<SimCommand> deferred = [];
    readonly List<string> messages = [];
    readonly List<SaidLine> said = [];
    readonly Dictionary<CharacterId, (string Text, long Until)> barks = [];
    IAnimationSource? animations;
    IProjectileSource? projectiles;
    Trading.ITradeSource? trades;
    Production.ProductionSystem? production;
    Dialogue.IDialogueSource? dialogues;
    Ragdolls.IRagdollSource? ragdolls;

    public World(WorldSettings settings, IWalkability walkability, IEnumerable<ITickSystem>? systems = null, CharacterTable? characters = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(walkability);
        Settings = settings;
        GameHours = settings.StartHours;
        Walkability = walkability;
        this.systems = [.. systems ?? []];
        for (int k = 0; k < this.systems.Count; k++) phases.Add(new SystemPhases(this, this.systems[k], k));
        animations = this.systems.OfType<IAnimationSource>().FirstOrDefault();
        projectiles = this.systems.OfType<IProjectileSource>().FirstOrDefault();
        trades = this.systems.OfType<Trading.ITradeSource>().FirstOrDefault();
        production = this.systems.OfType<Production.ProductionSystem>().FirstOrDefault();
        dialogues = this.systems.OfType<Dialogue.IDialogueSource>().FirstOrDefault();
        ragdolls = this.systems.OfType<Ragdolls.IRagdollSource>().FirstOrDefault();
        foreach (var owner in this.systems.OfType<ITaskOwner>()) owner.RegisterTasks(Tasks);
        foreach (var owner in this.systems.OfType<IDecisionOwner>()) owner.RegisterSources(Decisions);
        for (int k = 0; k < this.systems.Count; k++) if (DecidesAfter(this.systems[k])) decideAfter = k;
        Characters = characters ?? new CharacterTable();
        Grid = new SpatialGrid(settings.GridCellSize);
        pool = new WorkerPool(settings.Threads);
        buffers = new EffectBuffer[Math.Max(1, settings.Threads * settings.PartitionsPerThread)];
        for (int i = 0; i < buffers.Length; i++) buffers[i] = new EffectBuffer();
    }

    public WorldSettings Settings { get; private set; }
    public ulong Seed => Settings.Seed;

    /// <summary>The act-out handlers <see cref="TaskStart"/> starts and ends tasks through (filled by the systems that own them, <see cref="ITaskOwner"/>; open to later additions).</summary>
    public TaskHandlers Tasks { get; } = new();

    /// <summary>
    /// The decision step for the player's characters (docs/simulation.md "Decision step"): run in the slow world right after the last of retaliation, aggro and flight (after every system
    /// when the world has none of them), and for orders between ticks. Its sources are registered by the systems that own them (<see cref="IDecisionOwner"/>; open to later additions).
    /// </summary>
    public DecisionSystem Decisions { get; } = new();
    int decideAfter = -1;

    static bool DecidesAfter(ITickSystem system) => system is RetaliationSystem or AggroSystem or FleeSystem;

    /// <summary>
    /// Sets the world seed before the first tick: a loaded save's (docs/formats/save.md CAMERA <c>meitou seed</c>), so its seeded layout rolls as before the save, or a new
    /// game's fresh one, so its names and other rolls differ from the last game's.
    /// </summary>
    public void RestoreSeed(ulong seed)
    {
        if (Tick != 0) throw new InvalidOperationException("The seed is restored before the world's first tick.");
        Settings = Settings with { Seed = seed };
    }
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
    /// <summary>The NPC platoons of the save this world was loaded from, made when their zones load (null: a world that started new). Not hashed: the platoons carry what matters.</summary>
    public Saving.SavedPopulation? Saved { get; set; }
    /// <summary>Each faction's <c>squad formation</c> (<see cref="SquadFormation"/>), by faction position; set by the population. Null (no faction data): RANDOM, the field's default.</summary>
    public IReadOnlyList<int>? FactionFormations { get; set; }
    /// <summary>The squad templates whose <c>force speed</c> is 0, 1 or 2 (<see cref="Squad.ForceSpeed"/>), by record id; set by the population. Derived from the data: not saved or hashed.</summary>
    public IReadOnlyDictionary<string, byte>? ForcedSpeeds { get; set; }
    /// <summary>The player: faction, money, selection.</summary>
    public PlayerState Player { get; } = new();
    /// <summary>Doors, lockable containers, cages and beds of the registered towns, with their occupants (docs/simulation.md "Cages, beds and locks").</summary>
    public Locks.FixtureRegistry Fixtures { get; } = new();
    /// <summary>The characters' places and light levels for the sight tests (docs/game/perception.md "Sight"), worked out each tick.</summary>
    public SightMap Sight { get; } = new();
    /// <summary>The unique (story) characters made, dead or captured (docs/game/factions-squads-towns.md "Unique characters").</summary>
    public UniqueCharacters Uniques { get; } = new();
    /// <summary>The player-built buildings and construction sites, in creation order (docs/simulation.md "Buildings").</summary>
    public Construction.BuildingRegistry Buildings { get; } = new();
    /// <summary>Loose items lying in the world, so far the severed limbs combat drops (docs/simulation.md "Ground items").</summary>
    public Items.GroundItems GroundItems { get; } = new();
    /// <summary>The loose-item slots of the towns' interiors (docs/simulation.md "Ground items"); hashed once a town with any registered.</summary>
    public Items.ItemSlots ItemSlots { get; } = new();
    /// <summary>
    /// Every zone the population has had active in this world (docs/formats/save.md "Zone files": the original writes a file for each zone it loaded): a save writes a zone file for
    /// each that its loaded save has none of. Only read by saves, so not hashed.
    /// </summary>
    public HashSet<Meitou.Data.World.ZoneCoordinate> VisitedZones { get; } = [];
    /// <summary>
    /// The zones active as of the population's last look, which are the loaded zones (null before one, so no zone is judged inactive): squads, loose items and the navmesh are kept there. The collection is the population's own and swapped by it: read within a tick only. Not hashed.
    /// </summary>
    public IReadOnlyCollection<Meitou.Data.World.ZoneCoordinate>? ActiveZones { get; set; }
    /// <summary>
    /// The zones the original has loaded as of the population's last look (<see cref="PopulationSystem"/>: the camera's block and the player's box; docs/game/character-stats.md "Corpses"; null before one): a corpse in a zone not in it
    /// is removed at once (<see cref="BodySystem"/>, "corpse unloaded"). Not hashed.
    /// </summary>
    public IReadOnlyCollection<Meitou.Data.World.ZoneCoordinate>? LoadedZones { get; set; }
    /// <summary>The player's research: finished, queued, paid (docs/simulation.md "Production"); hashed once it changed in play.</summary>
    public Construction.ResearchState Research { get; } = new();
    /// <summary>The machines of the player's complete buildings: recipe slots, storage, farms (docs/simulation.md "Production").</summary>
    public Production.MachineRegistry Machines { get; } = new();
    public IReadOnlyList<ITickSystem> Systems => systems;
    /// <summary>The number of ticks run; during a tick, the number of the tick being run.</summary>
    public long Tick { get; private set; }

    /// <summary>
    /// Runs <paramref name="body"/> for 0..<paramref name="count"/>-1 on the worker pool and waits, in a serial phase (a slow-world pass over independent items: each call may
    /// change only its own item). The result must not depend on the order the items run in.
    /// </summary>
    internal void ParallelFor(int count, Action<int> body) => pool.ForEach(count, body);
    /// <summary>Game seconds per game hour (docs/game/game-loop.md "The clock", Verified; the engine's <c>GameClock</c> has the same).</summary>
    public const double SecondsPerGameHour = 1200.0 / 11;
    /// <summary>
    /// The game hours (day x 24 + time of day): the AI's hour windows read it. It advances by one tick of game time each tick; a host with a clock sets it from
    /// the clock before each tick (the clock can be set). An input like the commands, so not part of the state hash.
    /// </summary>
    public double GameHours { get; set; }
    /// <summary>
    /// Daytime (sunrise &lt; hour &lt; sunset, <c>FUN_14066cb20</c>): a host with a clock sets it with <see cref="GameHours"/>; false (night) without one. An input, not hashed. Light buildings
    /// outdoors work only at night (docs/game/ui-screens.md "Selected building", slot 164).
    /// </summary>
    public bool Daytime { get; set; }
    /// <summary>
    /// How many times faster than real time the host runs ticks (the game speed 1, 2 or 5; 1 without a clock). The ragdoll physics steps in real time, not game time
    /// (<see cref="Ragdolls.RagdollPhysics.StepOf"/>), so a ragdoll's outcome depends on it, as in the original; headless runs and the goldens stay at 1. An input like <see cref="GameHours"/>:
    /// not hashed, not saved.
    /// </summary>
    public float PhysicsSpeed { get; set; } = 1;
    /// <summary>The integer hour of day, 0 to 23 (the game's sky controller hour that the AI compares windows with).</summary>
    public int Hour => Math.Clamp((int)(((GameHours % 24) + 24) % 24), 0, 23);
    public float TickSeconds => Settings.TickSeconds;
    /// <summary>The snapshot the last tick published, and the one before it (for interpolating); <see cref="WorldSnapshot.Empty"/> before.</summary>
    public WorldSnapshot Snapshot { get; private set; } = WorldSnapshot.Empty;
    public WorldSnapshot PreviousSnapshot { get; private set; } = WorldSnapshot.Empty;

    /// <summary>Adds a system after the existing ones; before the first tick.</summary>
    public void AddSystem(ITickSystem system)
    {
        if (Tick != 0) throw new InvalidOperationException("systems are added before the first tick");
        systems.Add(system);
        systemsByType.Clear();
        phases.Add(new SystemPhases(this, system, systems.Count - 1));
        animations ??= system as IAnimationSource;
        projectiles ??= system as IProjectileSource;
        trades ??= system as Trading.ITradeSource;
        dialogues ??= system as Dialogue.IDialogueSource;
        ragdolls ??= system as Ragdolls.IRagdollSource;
        if (system is IDecisionOwner owner) owner.RegisterSources(Decisions);
        if (DecidesAfter(system)) decideAfter = systems.Count - 1;
    }

    /// <summary>The character holds a line it wants to say to the player (<see cref="Dialogue.IDialogueSource.HasInterrupt"/>).</summary>
    public bool WantsToTalk(CharacterId id) => dialogues is not null && Characters.Phase == TablePhase.Outside && dialogues.HasInterrupt(id);

    /// <summary>Whether the character answers the player's Talk (the dialogue system finds event-1 dialogue for it, and with <paramref name="talker"/> given, a line of it passes now); the host asks between ticks.</summary>
    public bool CanTalk(CharacterId id, CharacterId? talker = null) => dialogues is not null && Characters.Phase == TablePhase.Outside && Characters.TryResolveNext(id, out int slot) && dialogues.CanTalk(this, slot, talker);

    // The profiled parallel phase (RunParallel): the body run, a timed wrapper made once, and each partition's Stopwatch ticks (a partition is one job, written by one thread).
    Action<int>? timedBody;
    Action<int>? timedRun;
    long[] jobTicks = [];

    /// <summary>One system's parallel phase over the partitions; profiled, it also sums its jobs' time and notes the slowest (<see cref="TickProfile.Jobs"/>).</summary>
    void RunParallel(Action<int> body, TickProfile? prof, int phase, int system)
    {
        if (prof is null) { pool.ForEach(partitions.Length, body); return; }
        int n = partitions.Length;
        if (jobTicks.Length < n) jobTicks = new long[n];
        Array.Clear(jobTicks, 0, n);
        timedBody = body;
        timedRun ??= i =>
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            timedBody!(i);
            jobTicks[i] = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        };
        pool.ForEach(n, timedRun);
        long sum = 0, max = 0;
        for (int i = 0; i < n; i++) { sum += jobTicks[i]; max = Math.Max(max, jobTicks[i]); }
        prof.Jobs(phase, sum, max);
        prof.Lap(phase, system);
    }

    /// <summary>Runs one simulation tick; <paramref name="publish"/> false skips its snapshot (a tick the host will not draw: not one of a frame's last two).</summary>
    public void RunTick(bool publish = true)
    {
        var table = Characters;
        var prof = Profile.Enabled ? Profile : null;
        prof?.Begin(systems.Count);
        table.BeginTick();

        // 1. Inputs (serial).
        table.Phase = TablePhase.Inputs;
        var due = TakeDue();
        prof?.Lap(TickProfile.Inputs);
        for (int k = 0; k < systems.Count; k++) { systems[k].Inputs(this, due); prof?.Lap(TickProfile.Inputs, k); }

        // 2. Schedule (serial): the grid of the last tick's positions, then the systems.
        table.Phase = TablePhase.Schedule;
        Grid.Build(table.Previous);
        prof?.Lap(TickProfile.Grid);
        Sight.Update(this);
        prof?.Lap(TickProfile.Sight);
        MakePartitions();
        prof?.Lap(TickProfile.Schedule);
        for (int k = 0; k < systems.Count; k++) { systems[k].Schedule(this); prof?.Lap(TickProfile.Schedule, k); }

        // 3-5. Think, move, act (parallel; each system's phase is a barrier).
        table.Phase = TablePhase.Parallel;
        // A system that leaves a phase at its empty default is skipped there: a barrier costs more than the nothing it would wait for.
        foreach (var p in phases) if (p.Think is { } think) RunParallel(think, prof, TickProfile.Think, p.Index);
        foreach (var p in phases) if (p.Move is { } move) RunParallel(move, prof, TickProfile.Move, p.Index);
        foreach (var b in buffers) b.Reset();
        prof?.Lap(TickProfile.Act);
        foreach (var p in phases)
        {
            if (p.BeforeAct is { } before) RunParallel(before, prof, TickProfile.Act, p.Index);
            if (p.Act is not { } act) continue;
            foreach (var b in buffers) b.BeginSystem(p.Index);   // Effect.System is the system's real index, so the commit order does not depend on which systems act
            RunParallel(act, prof, TickProfile.Act, p.Index);
        }

        table.Phase = TablePhase.Commit;
        // 6. Commit (serial): the effects in an order the partitioning cannot change.
        effects.Clear();
        for (int i = 0; i < partitions.Length; i++) effects.AddRange(buffers[i].Items);
        effects.Sort(EffectBuffer.Order);
        prof?.Lap(TickProfile.Commit);
        foreach (var e in effects) { systems[e.System].Apply(this, e); prof?.Lap(TickProfile.Commit, e.System); }

        Squads.Refresh(table);
        prof?.Lap(TickProfile.Commit);
        table.Phase = TablePhase.SlowWorld;
        // 7. Slow world (serial).
        for (int k = 0; k < systems.Count; k++)
        {
            systems[k].SlowWorld(this);
            prof?.Lap(TickProfile.SlowWorld, k);
            if (k == decideAfter) Decisions.Run(this, paused: false);
        }
        if (decideAfter < 0) Decisions.Run(this, paused: false);
        TaskStart.CheckStarts(this);

        // 8. Publish.
        Squads.Refresh(table);
        Player.Prune(table);
        table.Phase = TablePhase.Outside;
        table.EndTick();
        Tick++;
        GameHours += TickSeconds / SecondsPerGameHour;
        if (publish && Settings.PublishSnapshots) Publish();
        prof?.Lap(TickProfile.Publish);
    }

    /// <summary>The ticks' time per phase and system (off unless the host turns it on; F12's sim rows).</summary>
    public TickProfile Profile { get; } = new();

    /// <summary>
    /// Queues a command a system decided on inside the tick (retaliation, auto-aggro) for the tick it is stamped with (at least the next one).
    /// Serial phases only. These are world state, not the host's: they never go through <see cref="Commands"/>, so a host enqueuing from
    /// another thread cannot interleave with them. At a tick they come before the host's commands of that tick, in the order they were deferred.
    /// </summary>
    public void Defer(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Characters.Phase == TablePhase.Parallel) throw new InvalidOperationException("commands are deferred in serial phases only");
        if (command.Tick <= Tick) throw new ArgumentException("a deferred command is for a later tick", nameof(command));
        deferred.Add(command);
    }

    /// <summary>The commands deferred for later ticks (for tests).</summary>
    public int DeferredCount => deferred.Count;

    IReadOnlyList<SimCommand> TakeDue()
    {
        var external = Commands.TakeDue(Tick);
        if (deferred.Count == 0) return external;
        var due = new List<SimCommand>(deferred.Count + external.Count);
        int kept = 0;
        for (int k = 0; k < deferred.Count; k++)
        {
            if (deferred[k].Tick <= Tick) due.Add(deferred[k]);
            else deferred[kept++] = deferred[k];
        }
        deferred.RemoveRange(kept, deferred.Count - kept);
        due.AddRange(external);
        return due;
    }

    /// <summary>Posts a message for the player (a relation crossing a threshold...); serial phases only. The host shows them.</summary>
    public void Post(string message)
    {
        lock (messages) messages.Add(message);
    }

    /// <summary>
    /// A short line said over <paramref name="speaker"/>'s head outside dialogue (an order refused: "I don't have a medkit."); serial phases only. Shown as a medium speech bubble
    /// for 2 + 0.12 x its length seconds, the ambient bubbles' time. Display only: not saved or hashed.
    /// </summary>
    public void Bark(CharacterId speaker, string text)
    {
        barks[speaker] = (text, Tick + (long)MathF.Ceiling((2 + 0.12f * text.Length) / TickSeconds));
    }

    /// <summary>Takes the messages posted since the last call (the host, between ticks).</summary>
    public IReadOnlyList<string> TakeMessages()
    {
        lock (messages)
        {
            if (messages.Count == 0) return [];
            string[] all = [.. messages];
            messages.Clear();
            return all;
        }
    }

    /// <summary>
    /// A dialogue line for the Overview's DIALOGUE log (<c>ManagementScreen::addMessage</c>, docs/game/ui-screens.md "Dialogue log"), said by <paramref name="owner"/>
    /// (<paramref name="player"/>: a player character, or the player's reply); serial phases only. Display only: not saved or hashed; the newest 200 are kept until the host takes them.
    /// </summary>
    public void Said(string owner, string text, bool player)
    {
        if (text.Length == 0) return;
        lock (said)
        {
            said.Add(new(owner, text.ReplaceLineEndings(" "), player));
            if (said.Count > 200) said.RemoveAt(0);
        }
    }

    /// <summary>Takes the dialogue lines said since the last call (the host, between ticks).</summary>
    public IReadOnlyList<SaidLine> TakeSaid()
    {
        lock (said)
        {
            if (said.Count == 0) return [];
            SaidLine[] all = [.. said];
            said.Clear();
            return all;
        }
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
        if (Uniques.Count > 0) Uniques.Hash(ref h);   // only once a unique is tracked, so worlds without one hash as before
        if (Fixtures.Count > 0) Fixtures.Hash(ref h);   // only once a town with buildings registered, so worlds without them hash as before
        if (Buildings.Count > 0 || Buildings.Towns.Count > 0) Buildings.Hash(ref h);   // only once the player has a site, so worlds without one hash as before
        if (GroundItems.Count > 0) GroundItems.Hash(ref h);   // only once something lies on the ground, so worlds without hash as before
        if (ItemSlots.Count > 0) ItemSlots.Hash(ref h);   // only once a town with item slots registered
        if (Machines.Count > 0) Machines.Hash(ref h);   // only once the player has a working building
        if (Research.Changed) Research.Hash(ref h);   // only once research changed in play
        foreach (var s in systems) if (s is IStateHashed hashed) hashed.Hash(ref h);
        return h.Value;
    }

    public void Dispose() => pool.Dispose();
}
