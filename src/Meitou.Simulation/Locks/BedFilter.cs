namespace Meitou.Simulation.Locks;

/// <summary>
/// A bed the bed search weighs for a sleeper (<see cref="IBedFilter"/>): the tick, the sleeper, the bed (<see cref="Fixture.Id"/>, its building and
/// owner faction) and whether the game's own rules take it.
/// </summary>
public readonly record struct BedAsk(long Tick, CharacterId Sleeper, string Bed, string Building, int Owner, bool Allowed);

/// <summary>
/// A system asked about the beds the bed search (<see cref="FixtureRegistry.FindBed"/>) weighs (docs/modding.md "Bed search"): it answers whether
/// the bed may be taken, so a mod refuses a bed the game takes or admits one it refuses (a rented inn bed for an autonomous squad). Filters run in
/// system order, each seeing the one before's answer, on any thread and concurrently (the AI's think searches too): quick, thread-safe, a pure read
/// of the ask and of world state the tick has settled. A throw keeps the answer as it came; the filter is still asked for the rest of that tick,
/// not from the next, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface IBedFilter : ITickSystem
{
    /// <summary>
    /// Whether the sleeper may take the bed <paramref name="ask"/> names.
    /// </summary>
    bool Allows(World world, in BedAsk ask);
}

/// <summary>
/// The world's bed filters (<see cref="IBedFilter"/>); any thread.
/// </summary>
sealed class BedFilters
{
    /// <summary>
    /// The registered filters.
    /// </summary>
    readonly ParallelHooks<IBedFilter> _hooks = new(0x42444646, "bed filter");

    /// <summary>
    /// Whether any filter is registered; the game's verdict stands without one.
    /// </summary>
    internal bool Active => _hooks.Active;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is IBedFilter filter) _hooks.Add(filter, system, systemIndex);
    }

    /// <summary>
    /// Adds the faulted filters to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash) => _hooks.HashFaults(ref hash);

    /// <summary>
    /// Whether the sleeper in <paramref name="slot"/> may take <paramref name="bed"/>: <paramref name="allowed"/> (the game's verdict) after every
    /// filter not faulted before this tick. Any thread.
    /// </summary>
    internal bool Pass(World world, int slot, Fixture bed, bool allowed)
    {
        if (!_hooks.Active) return allowed;

        long tick = world.Tick;
        var sleeper = world.Characters.IdOf(slot);
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks.Asked(i, tick))
                allowed = Ask(world, i, new BedAsk(tick, sleeper, bed.Id, bed.Building, bed.Owner, allowed));
        }

        return allowed;
    }

    /// <summary>
    /// One filter's answer; the answer as it came (and faulted) when the filter throws.
    /// </summary>
    bool Ask(World world, int i, in BedAsk ask)
    {
        try
        {
            return _hooks[i].Allows(world, ask);
        }
        catch (Exception e)
        {
            _hooks.Threw(i, ask.Tick, e);
        }

        return ask.Allowed;
    }
}
