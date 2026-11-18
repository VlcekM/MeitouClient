namespace Meitou.Simulation.Factions;

/// <summary>
/// A WORLD_EVENT_STATE whose answer a code mod sets (docs/modding.md "World states"): while the mod has set a value in a world, every reader of the
/// state (dialogue, squad spawns, town overrides, campaigns, nests) gets that value; until then, and wherever the mod is not running, the record's own
/// conditions answer, as the original answers it.
/// </summary>
public sealed class ModWorldState
{
    /// <summary>
    /// Made for the mod <paramref name="owner"/> (its id) claiming the WORLD_EVENT_STATE <paramref name="record"/>.
    /// </summary>
    public ModWorldState(string owner, string record)
    {
        Owner = owner;
        Record = record;
    }

    /// <summary>
    /// The id of the mod that claimed the state.
    /// </summary>
    public string Owner { get; }
    /// <summary>
    /// The WORLD_EVENT_STATE's string id.
    /// </summary>
    public string Record { get; }

    /// <summary>
    /// The value the mod set in <paramref name="world"/>; null while it has set none (the record's conditions answer).
    /// </summary>
    public bool? Value(World world) => world.ModStatesOrNull?.Get(Record);

    /// <summary>
    /// Sets the state's answer in <paramref name="world"/>; from the mod's system in a serial phase (or <see cref="IWorldReady"/>), never a parallel
    /// one. Readers see it from the population's next refresh of the world states (that tick's slow-world step at the latest).
    /// </summary>
    public void Set(World world, bool value) => world.ModStates.Set(Record, value);

    /// <summary>
    /// Drops the mod's value in <paramref name="world"/>, so the record's conditions answer again; as <see cref="Set"/>.
    /// </summary>
    public void Clear(World world) => world.ModStatesOrNull?.Clear(Record);
}

/// <summary>
/// The values code mods set on their world states in one world (<see cref="ModWorldState"/>), by record id; made at the first value, so a world
/// without one has none, hashes as before and answers every state from the data.
/// </summary>
public sealed class ModWorldStates
{
    /// <summary>
    /// The values by record id.
    /// </summary>
    readonly SortedDictionary<string, bool> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// How many states have a value.
    /// </summary>
    public int Count => _values.Count;
    /// <summary>
    /// Raised by every change, so the world states' copy is taken again (<see cref="WorldStates.Refresh"/>).
    /// </summary>
    public long Version { get; private set; }
    /// <summary>
    /// The values, in record id order.
    /// </summary>
    public IEnumerable<KeyValuePair<string, bool>> All => _values;

    /// <summary>
    /// The value of <paramref name="record"/>; null when none is set.
    /// </summary>
    public bool? Get(string record) => _values.TryGetValue(record, out bool value) ? value : null;

    /// <summary>
    /// Sets <paramref name="record"/>'s value.
    /// </summary>
    public void Set(string record, bool value)
    {
        if (_values.TryGetValue(record, out bool old) && old == value) return;
        _values[record] = value;
        Version++;
    }

    /// <summary>
    /// Drops <paramref name="record"/>'s value.
    /// </summary>
    public void Clear(string record)
    {
        if (_values.Remove(record)) Version++;
    }

    /// <summary>
    /// A copy of the values for readers on other threads.
    /// </summary>
    internal IReadOnlyDictionary<string, bool> Copy() => new Dictionary<string, bool>(_values, StringComparer.Ordinal);

    /// <summary>
    /// Adds the values to the world's state hash, in record id order.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(_values.Count);
        foreach (var (record, value) in _values)
        {
            h.Add(Rng.StableHash(record));
            h.Add(value);
        }
    }
}
