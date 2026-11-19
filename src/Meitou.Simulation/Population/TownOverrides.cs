using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Population;

/// <summary>
/// The population's state for towns that change version away from their zone's load (docs/simulation.md "Town overrides"): each TOWN's
/// <c>override town</c> entries read once, the answers of the world states they test, the towns code mods hold, the towns that need no new look
/// until an answer changes, and the versions each town held since then (so a town is not switched back and forth at every look).
/// </summary>
sealed class TownOverrides(GameDatabase db)
{
    #region Entries

    /// <summary>
    /// One <c>override town</c> entry: the TOWN it names and the distinct WORLD_EVENT_STATEs it tests (its weight is their count).
    /// </summary>
    readonly record struct Entry(TownData Town, RecordLink[] States);

    /// <summary>
    /// Each TOWN's entries, by its string id, read on first use (the records do not change in a world).
    /// </summary>
    readonly Dictionary<string, Entry[]> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// The town's winning <c>override town</c> entry now (docs/game/factions-squads-towns.md "Town overrides"): of the entries whose every
    /// <c>world state</c> holds, weighted by how many they have (none: never), other than the replacement in place, the heaviest, the first of
    /// equals; null when none qualifies. An unreplaced town whose list names its own TOWN may take it, which then is the replacement in place.
    /// </summary>
    public TownData? Choose(TownSite site, IWorldStates states)
    {
        string? inPlace = site.Replacement?.Id;
        TownData? best = null;
        int weight = 0;
        foreach (var entry in EntriesOf(PopulationSystem.TownIdOf(site), site.Town.OverrideTown))
        {
            if (entry.Town.Id == inPlace || entry.States.Length <= weight) continue;
            if (!entry.States.All(w => IWorldStates.Passes(states, db, w.Id, w.V0))) continue;

            (best, weight) = (entry.Town, entry.States.Length);
        }

        return best;
    }

    /// <summary>
    /// The entries of TOWN <paramref name="town"/>, whose list is <paramref name="links"/>: the links naming a TOWN, each with its states.
    /// </summary>
    Entry[] EntriesOf(string town, IReadOnlyList<RecordLink> links)
    {
        if (_entries.TryGetValue(town, out var entries)) return entries;

        entries = links
            .Select(l => db.Find(l.Id))
            .OfType<GameRecord>()
            .Where(r => r.Type == FcsRecordType.Town)
            .Select(EntryOf)
            .ToArray();
        _entries[town] = entries;
        return entries;
    }

    /// <summary>
    /// The entry naming <paramref name="record"/>: the TOWN and the distinct WORLD_EVENT_STATEs it lists.
    /// </summary>
    Entry EntryOf(GameRecord record)
    {
        var town = TownData.From(record);
        var states = town.WorldState
            .Where(w => db.Find(w.Id) is { Type: FcsRecordType.WorldEventState })
            .DistinctBy(w => w.Id)
            .ToArray();
        return new Entry(town, states);
    }

    #endregion

    #region Answers

    /// <summary>
    /// The hash of the answers of every state an entry tests, as last computed; null before the first look and once a TOWN adds tested states.
    /// </summary>
    long? _answers;
    /// <summary>
    /// Every WORLD_EVENT_STATE an entry of a TOWN a town has been or can become tests (<see cref="Reach"/>), in ordinal order.
    /// </summary>
    readonly SortedSet<string> _tested = new(StringComparer.Ordinal);
    /// <summary>
    /// The TOWNs whose entries' states are in <see cref="_tested"/>, by string id.
    /// </summary>
    readonly HashSet<string> _reached = new(StringComparer.Ordinal);
    /// <summary>
    /// Whether the sites' own TOWNs and their replacements were reached (at the first look).
    /// </summary>
    bool _sitesReached;
    /// <summary>
    /// The towns looked at since the answers last changed that had no entry to take (looked at again only once their TOWN changes).
    /// </summary>
    readonly HashSet<int> _settled = [];
    /// <summary>
    /// For each town changed off screen, the versions it held since the answers last changed ("": its own record).
    /// </summary>
    readonly Dictionary<int, HashSet<string>> _visited = [];

    /// <summary>
    /// Takes the world states' answers now: when an answer of a state an entry tests changed, every town is looked at again and may move again,
    /// also back to a version it held.
    /// </summary>
    public void Answers(IWorldStates states, IReadOnlyList<TownSite> sites)
    {
        long answers = AnswersHash(states, sites);
        if (answers == _answers) return;

        _answers = answers;
        _settled.Clear();
        _visited.Clear();
    }

    /// <summary>
    /// Adds the states tested by the entries of <paramref name="town"/> and of every TOWN its lists lead to, once each: a version a code mod put in
    /// place may lie outside the TOWNs the sites reach. When that adds a state, every town is looked at again at the next look.
    /// </summary>
    public void Reach(TownData town)
    {
        if (Reach([town])) _answers = null;
    }

    /// <summary>
    /// Whether <paramref name="site"/> had no entry to take since the answers and its TOWN last changed.
    /// </summary>
    public bool IsSettled(int site) => _settled.Contains(site);

    /// <summary>
    /// Records that <paramref name="site"/> had no entry to take.
    /// </summary>
    public void Settle(int site) => _settled.Add(site);

    /// <summary>
    /// Records that <paramref name="site"/>'s TOWN changed (at a load or off screen): it is looked at again.
    /// </summary>
    public void Changed(int site) => _settled.Remove(site);

    /// <summary>
    /// Whether <paramref name="site"/> already held <paramref name="version"/> ("": its own record) since the answers last changed: it would be
    /// switched back.
    /// </summary>
    public bool Revisits(int site, string version) => _visited.TryGetValue(site, out var held) && held.Contains(version);

    /// <summary>
    /// Records that <paramref name="site"/> went off screen from <paramref name="from"/> to <paramref name="to"/> ("": its own record).
    /// </summary>
    public void Visited(int site, string from, string to)
    {
        if (!_visited.TryGetValue(site, out var held)) _visited[site] = held = new HashSet<string>(StringComparer.Ordinal);
        held.Add(from);
        held.Add(to);
    }

    /// <summary>
    /// The hash of the answer of every tested state (<see cref="_tested"/>), in their order; the sites' TOWNs are reached first.
    /// </summary>
    long AnswersHash(IWorldStates states, IReadOnlyList<TownSite> sites)
    {
        if (!_sitesReached)
        {
            Reach(sites.Select(s => s.Record).Concat(sites.Select(s => s.Replacement).OfType<TownData>()));
            _sitesReached = true;
        }

        var h = new StateHasher();
        foreach (string id in _tested) h.Add(states.IsTrue(id));
        return (long)h.Value;
    }

    /// <summary>
    /// Adds the states tested by the entries of <paramref name="towns"/> and of every TOWN their lists lead to, each TOWN once. Whether a state was
    /// added.
    /// </summary>
    bool Reach(IEnumerable<TownData> towns)
    {
        int before = _tested.Count;
        var open = new Queue<TownData>(towns);
        while (open.TryDequeue(out var town))
        {
            if (!_reached.Add(town.Id)) continue;

            foreach (var entry in EntriesOf(town.Id, town.OverrideTown))
            {
                foreach (var state in entry.States) _tested.Add(state.Id);
                open.Enqueue(entry.Town);
            }
        }

        return _tested.Count > before;
    }

    #endregion

    #region Holds

    /// <summary>
    /// The towns a code mod drives itself, by site index (<see cref="PopulationSystem.HoldTown"/>).
    /// </summary>
    readonly SortedSet<int> _held = [];

    /// <summary>
    /// Whether a code mod holds <paramref name="site"/>.
    /// </summary>
    public bool IsHeld(int site) => _held.Contains(site);

    /// <summary>
    /// Holds or releases <paramref name="site"/>.
    /// </summary>
    public void Hold(int site, bool held)
    {
        if (held) _held.Add(site);
        else _held.Remove(site);
    }

    /// <summary>
    /// Whether a town is held (so worlds without holds hash as before).
    /// </summary>
    public bool AnyHeld => _held.Count > 0;

    /// <summary>
    /// Adds the holds to the world's state hash.
    /// </summary>
    public void Hash(ref StateHasher h)
    {
        h.Add(0x54534F56);
        h.Add(_held.Count);
        foreach (int site in _held) h.Add(site);
    }

    #endregion

    #region Counter

    /// <summary>
    /// How many towns changed version off screen in this world (a statistic: not hashed, not saved).
    /// </summary>
    public int OffscreenSwitches { get; private set; }

    /// <summary>
    /// Counts a town changed off screen.
    /// </summary>
    public void CountOffscreen() => OffscreenSwitches++;

    #endregion
}
