namespace Meitou.Simulation.Factions;

/// <summary>
/// A WORLD_EVENT_STATE whose answer a code mod sets (docs/modding.md "World states"): while the mod has set a value in a world, every reader of the
/// state (dialogue, squad spawns, town overrides, campaigns, nests) gets that value; until then, and wherever the mod is not running, the record's own
/// conditions answer, as the original answers it.
/// </summary>
public sealed class ModWorldState
{
    /// <summary>
    /// Set once a refused <see cref="Set"/> or <see cref="Clear"/> was logged, so the log gets one line per handle.
    /// </summary>
    bool _refusalLogged;

    /// <summary>
    /// Made for the mod <paramref name="owner"/> (its id) claiming the WORLD_EVENT_STATE <paramref name="record"/>; only the handle
    /// <c>ModContext.AddWorldState</c> returns is the claim (<see cref="ModWorldStates.Claim"/>), and only it sets values.
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
    /// The value the mod set in <paramref name="world"/>; null while it has set none (the record's conditions answer). Like <see cref="Set"/>, for the
    /// tick's serial phases: interface code (a key press, a menu source) reads a copy the mod's system publishes.
    /// </summary>
    public bool? Value(World world) => world.ModStatesOrNull?.Get(Record);

    /// <summary>
    /// Sets the state's answer in <paramref name="world"/>; from the mod's system in a serial phase (or <see cref="IWorldReady"/>), never a parallel
    /// one. Readers see it from the population's next refresh of the world states: that tick's slow-world step when the setting system runs before
    /// the population's, else the next tick's. A decision already made (a nest laid, a town replaced, a squad spawned) stays. A handle that is not
    /// the record's claim (one made outside <c>ModContext.AddWorldState</c>) sets nothing, with one log line.
    /// </summary>
    public void Set(World world, bool value)
    {
        if (Refused()) return;
        world.ModStates.Set(Record, value);
    }

    /// <summary>
    /// Drops the mod's value in <paramref name="world"/>, so the record's conditions answer again; as <see cref="Set"/>.
    /// </summary>
    public void Clear(World world)
    {
        if (Refused()) return;
        world.ModStatesOrNull?.Clear(Record);
    }

    /// <summary>
    /// Whether this handle is not its record's claim; logs that once.
    /// </summary>
    bool Refused()
    {
        if (ModWorldStates.IsClaimedBy(this)) return false;

        if (!_refusalLogged)
        {
            _refusalLogged = true;
            string reason = $"world state '{Record}' was not claimed through ModContext.AddWorldState; its value is not set";
            Console.Error.WriteLine($"mod       {Owner}: {reason}");
        }
        return true;
    }
}

/// <summary>
/// The values code mods set on their world states in one world (<see cref="ModWorldState"/>), by record id; made at the first value, so a world
/// without one has none, hashes as before and answers every state from the data. Read-only outside this assembly: values are set through the
/// claimed handles.
/// </summary>
public sealed class ModWorldStates
{
    /// <summary>
    /// The claimed states by record id, filled at startup as the mods' registrations are committed and never changed while a world runs.
    /// </summary>
    static readonly Dictionary<string, ModWorldState> Claims = new(StringComparer.Ordinal);

    /// <summary>
    /// The values by record id.
    /// </summary>
    readonly SortedDictionary<string, bool> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// Records <paramref name="state"/> as its record's claim (the mod host does, once the mod's registration is committed; it checks the owner).
    /// </summary>
    public static void Claim(ModWorldState state) => Claims[state.Record] = state;

    /// <summary>
    /// Whether <paramref name="state"/> is its record's claim (the same handle, so a handle made for another mod's record is not).
    /// </summary>
    internal static bool IsClaimedBy(ModWorldState state) => Claims.TryGetValue(state.Record, out var claim) && ReferenceEquals(claim, state);

    /// <summary>
    /// Forgets every claim (tests).
    /// </summary>
    internal static void ClearClaims() => Claims.Clear();

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
    /// Sets <paramref name="record"/>'s value; only through its claimed handle (<see cref="ModWorldState.Set"/>), so every value is saved.
    /// </summary>
    internal void Set(string record, bool value)
    {
        if (_values.TryGetValue(record, out bool old) && old == value) return;
        _values[record] = value;
        Version++;
    }

    /// <summary>
    /// Drops <paramref name="record"/>'s value; only through its claimed handle.
    /// </summary>
    internal void Clear(string record)
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
