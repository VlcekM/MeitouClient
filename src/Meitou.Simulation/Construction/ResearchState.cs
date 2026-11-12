using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Simulation.Construction;

/// <summary>
/// A queued research: its RESEARCH id and the game seconds put into it (the save's <c>current prog&lt;k&gt;</c>).
/// </summary>
public sealed class ResearchItem(string id, float seconds)
{
    /// <summary>
    /// The RESEARCH id.
    /// </summary>
    public string Id { get; } = id;
    /// <summary>
    /// The game seconds put into it.
    /// </summary>
    public float Seconds { get; set; } = seconds;
}

/// <summary>
/// The player's research (docs/game/buildings-production.md "Research", docs/simulation.md "Production"): the finished RESEARCH ids and the buildings
/// they unlock, the queue, the ids already paid for and the progress of ones taken off the queue. Set at a new game
/// (<see cref="ResearchGate.StartResearch"/>) and when a save loads. <see cref="Unlocked"/> is replaced, never changed in place, so a snapshot may
/// hold it. Hashed only once something changed in play (<see cref="Changed"/>), so worlds without research hash as before.
/// </summary>
public sealed class ResearchState
{
    /// <summary>
    /// The empty set <see cref="Unlocked"/> starts as.
    /// </summary>
    static readonly IReadOnlySet<string> Nothing = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The finished RESEARCH ids in finishing order, a repeated one once per finish.
    /// </summary>
    readonly List<string> _completed = [];
    /// <summary>
    /// The research queue, worked first to last.
    /// </summary>
    readonly List<ResearchItem> _queue = [];
    /// <summary>
    /// The ids already paid for.
    /// </summary>
    readonly List<string> _paid = [];
    /// <summary>
    /// The game seconds of researches taken off the queue, by id.
    /// </summary>
    readonly Dictionary<string, float> _partial = new(StringComparer.Ordinal);

    /// <summary>
    /// The finished RESEARCH ids, in the order they were finished (the save's <c>finished&lt;k&gt;</c> order).
    /// </summary>
    public IReadOnlyList<string> Completed => _completed;
    /// <summary>
    /// The BUILDING ids the completed research unlocks.
    /// </summary>
    public IReadOnlySet<string> Unlocked { get; private set; } = Nothing;
    /// <summary>
    /// The research queue; the first item is worked.
    /// </summary>
    public IReadOnlyList<ResearchItem> Queue => _queue;
    /// <summary>
    /// Research whose cost was paid (the save's <c>paid&lt;k&gt;</c>): queuing it again costs nothing (<b>Observed</b>, <c>FUN_1408343c0</c>).
    /// </summary>
    public IReadOnlyList<string> Paid => _paid;
    /// <summary>
    /// The seconds of research taken off the queue, kept for when it is queued again.
    /// </summary>
    public IReadOnlyDictionary<string, float> Partial => _partial;
    /// <summary>
    /// The manager's tech level: 1, raised to L + 1 by each finished level upgrade of level L (<b>Observed</b>, <c>FUN_140830880</c>).
    /// </summary>
    public int TechLevel { get; private set; } = 1;
    /// <summary>
    /// The highest <c>tech level</c> the player's complete research benches reached: raised by the production system, never lowered, 0 again on
    /// <see cref="Set(GameDatabase, IEnumerable{string}, IEnumerable{ValueTuple{string, float}}, IEnumerable{string}, IEnumerable{ValueTuple{string, float}})"/>
    /// (<c>FUN_140834390</c>; not saved, not hashed).
    /// </summary>
    public int BenchLevel { get; internal set; }
    /// <summary>
    /// The research rate of the last tick: the sum of the bench contributions after the 0.6 falloff (the window's CURRENT RATE).
    /// </summary>
    public float Rate { get; internal set; }
    /// <summary>
    /// Something changed in play: the state joins the world hash from then on.
    /// </summary>
    public bool Changed { get; private set; }
    /// <summary>
    /// Bumped on every change the host shows.
    /// </summary>
    public long Version { get; private set; }

    /// <summary>
    /// Notes a change: bumps <see cref="Version"/> and marks the state changed in play.
    /// </summary>
    internal void Touch()
    {
        Version++;
        Changed = true;
    }

    /// <summary>
    /// Replaces the completed research (and clears the queue).
    /// </summary>
    public void Set(GameDatabase db, IEnumerable<string> ids) => Set(db, ids, [], [], []);

    /// <summary>
    /// Replaces the whole state (a save's): finished, queued with their seconds, and paid ids.
    /// </summary>
    public void Set(GameDatabase db, IEnumerable<string> ids, IEnumerable<(string Id, float Seconds)> queued, IEnumerable<string> paidIds,
        IEnumerable<(string Id, float Seconds)> kept)
    {
        _completed.Clear();
        BenchLevel = 0;

        // A repeatable research is listed once per time it was finished (engine reading: the save format of repetitions is Unknown); extra listings
        // are dropped.
        foreach (var id in ids)
            if (Times(id) < RepetitionsOf(db, id)) _completed.Add(id);
        _queue.Clear();
        foreach (var (id, s) in queued)
        {
            if (db.Find(id) is { Type: FcsRecordType.Research } && Times(id) < RepetitionsOf(db, id) && _queue.TrueForAll(q => q.Id != id))
                _queue.Add(new ResearchItem(id, s));
        }

        _paid.Clear();
        foreach (var id in paidIds)
            if (!_paid.Contains(id, StringComparer.Ordinal)) _paid.Add(id);
        _partial.Clear();
        foreach (var (id, s) in kept) _partial[id] = s;
        Refresh(db);
        Version++;
        Changed = false;
    }

    /// <summary>
    /// Works out from the finished ids what they unlock: the buildings, the crafts (by repetition) and the buildings' improvements.
    /// </summary>
    void Refresh(GameDatabase db)
    {
        Unlocked = ResearchGate.Unlocked(db, _completed);
        var crafts = new HashSet<(CraftKind, string)>();
        foreach (var id in _completed.Distinct(StringComparer.Ordinal))
        {
            if (db.Find(id) is { Type: FcsRecordType.Research } record)
            {
                // With repeats above 0 repetition j keeps the entries whose v0 is j; without repeats the lists are not filtered (Verified,
                // FUN_1408320a0, FUN_140831e60).
                int times = Times(id);
                var data = ResearchData.From(record);
                foreach (var (kind, item, repetition) in data.EnableCrafts)
                    if (data.Repeats <= 0 || repetition >= 0 && repetition < times) crafts.Add((kind, item));
            }
        }

        UnlockedCrafts = crafts;

        // Each finished repetition applies its improvements in finishing order: the multiplier is replaced, the increases add (Observed,
        // FUN_140831a60).
        var upgrades = new Dictionary<string, BuildingUpgrade>(StringComparer.Ordinal);
        foreach (var id in _completed)
        {
            if (db.Find(id) is { Type: FcsRecordType.Research } record && ResearchData.From(record) is { ImproveBuildings.Count: > 0 } r)
            {
                foreach (var building in r.ImproveBuildings)
                {
                    var u = upgrades.GetValueOrDefault(building, BuildingUpgrade.None);
                    upgrades[building] = new BuildingUpgrade(r.ProductionMult, u.PowerIncrease + r.PowerIncrease,
                        u.CapacityIncrease + r.CapacityIncrease);
                }
            }
        }

        Upgrades = upgrades;
        int level = 1;
        foreach (var id in _completed)
        {
            if (db.Find(id) is { Type: FcsRecordType.Research } r && r.GetBool("is level upgrade") && r.GetInt("level") is var l && l >= level)
                level = l + 1;
        }

        TechLevel = level;
    }

    /// <summary>
    /// Finishes one research (nothing when it has no repetition left): it leaves the queue and its unlocks apply; a repeatable one is charged again
    /// next time (engine reading).
    /// </summary>
    public bool Complete(GameDatabase db, string id)
    {
        int repetitions = RepetitionsOf(db, id);
        if (Times(id) >= repetitions) return false;
        _completed.Add(id);
        _queue.RemoveAll(q => q.Id == id);
        _partial.Remove(id);
        if (repetitions > 1) _paid.Remove(id);
        Refresh(db);
        Touch();
        return true;
    }

    /// <summary>
    /// Finishes every RESEARCH record and every repetition left (the debug menu's cheat); the queue empties. Returns how many were finished.
    /// </summary>
    public int CompleteAll(GameDatabase db)
    {
        int n = 0;
        foreach (var record in db.Records.Values)
        {
            if (record.Type == FcsRecordType.Research)
                for (int repetitions = RepetitionsOf(db, record.StringId); Times(record.StringId) < repetitions; n++) _completed.Add(record.StringId);
        }

        if (n == 0) return 0;
        _queue.Clear();
        _partial.Clear();
        Refresh(db);
        Touch();
        return n;
    }

    /// <summary>
    /// Whether a research unlocked <paramref name="building"/>.
    /// </summary>
    public bool IsUnlocked(string building) => Unlocked.Contains(building);
    /// <summary>
    /// Whether research <paramref name="id"/> was finished at least once.
    /// </summary>
    public bool IsDone(string id) => _completed.Contains(id, StringComparer.Ordinal);
    /// <summary>
    /// How many times a research was finished.
    /// </summary>
    public int Times(string id)
    {
        int n = 0;
        foreach (var c in _completed) if (c == id) n++;
        return n;
    }

    /// <summary>
    /// Every repetition is finished.
    /// </summary>
    public bool Exhausted(GameDatabase db, string id) => Times(id) >= RepetitionsOf(db, id);
    /// <summary>
    /// The finished research's <c>improve buildings</c> by BUILDING record.
    /// </summary>
    public IReadOnlyDictionary<string, BuildingUpgrade> Upgrades { get; private set; } = new Dictionary<string, BuildingUpgrade>();
    /// <summary>
    /// The research improvements of <paramref name="building"/> (<see cref="BuildingUpgrade.None"/> without any).
    /// </summary>
    public BuildingUpgrade Upgrade(string building) => Upgrades.GetValueOrDefault(building, BuildingUpgrade.None);
    /// <summary>
    /// The upgrade of a building owned by <paramref name="owner"/>: only the player's buildings have one, others the default (<b>Verified</b>,
    /// <c>FUN_14082ec70</c>).
    /// </summary>
    public BuildingUpgrade Upgrade(World world, string building, int owner) =>
        owner >= 0 && owner == world.Player.Faction ? Upgrade(building) : BuildingUpgrade.None;

    /// <summary>
    /// The crafts the finished research unlocks (kind and record; a weapon type or model by its own id).
    /// </summary>
    public IReadOnlySet<(CraftKind Kind, string Id)> UnlockedCrafts { get; private set; } = new HashSet<(CraftKind, string)>();

    /// <summary>
    /// How many times a research can be finished in all: its <c>repeats</c>, at least 1 (<b>Verified</b>, <c>FUN_140834b70</c>).
    /// </summary>
    static int RepetitionsOf(GameDatabase db, string id) => db.Find(id) is { Type: FcsRecordType.Research } r ? Math.Max(1, r.GetInt("repeats")) : 1;
    /// <summary>
    /// Whether research <paramref name="id"/> is queued.
    /// </summary>
    public bool IsQueued(string id) => _queue.Exists(q => q.Id == id);

    /// <summary>
    /// Queues research <paramref name="id"/> with the seconds it had when taken off, and marks it paid.
    /// </summary>
    internal void Enqueue(string id)
    {
        _queue.Add(new ResearchItem(id, _partial.Remove(id, out float s) ? s : 0));
        if (!_paid.Contains(id, StringComparer.Ordinal)) _paid.Add(id);
        Touch();
    }

    /// <summary>
    /// Takes research <paramref name="id"/> off the queue, keeping its seconds; false when it is not queued.
    /// </summary>
    internal bool Dequeue(string id)
    {
        int i = _queue.FindIndex(q => q.Id == id);
        if (i < 0) return false;
        if (_queue[i].Seconds > 0) _partial[id] = _queue[i].Seconds;
        _queue.RemoveAt(i);
        Touch();
        return true;
    }

    /// <summary>
    /// Adds the finished, queued, paid and partial researches to the world's state hash.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(0x52657365);
        h.Add(_completed.Count);
        foreach (var id in _completed) h.Add(Rng.StableHash(id));
        h.Add(_queue.Count);
        foreach (var q in _queue) { h.Add(Rng.StableHash(q.Id)); h.Add(q.Seconds); }
        h.Add(_paid.Count);
        foreach (var id in _paid) h.Add(Rng.StableHash(id));
        foreach (var (id, s) in _partial.OrderBy(p => p.Key, StringComparer.Ordinal)) { h.Add(Rng.StableHash(id)); h.Add(s); }
    }
}

/// <summary>
/// A building's research improvements (<b>Observed</b>, <c>FUN_14082ec70</c>, default 1, 0, 0): production multiplier, power output and capacity
/// increases.
/// </summary>
public readonly record struct BuildingUpgrade(float ProductionMult, float PowerIncrease, float CapacityIncrease)
{
    /// <summary>
    /// No improvement: multiplier 1, no increases.
    /// </summary>
    public static BuildingUpgrade None { get; } = new(1, 0, 0);
}