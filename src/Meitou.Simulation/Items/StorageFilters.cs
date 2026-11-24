namespace Meitou.Simulation.Items;

/// <summary>
/// The world's systems asked about a storage the game picks for a character (<see cref="IStorageFilter"/>). Asked from worker threads too: a filter
/// that throws is answered as allowing for the rest of the tick it threw in, so every partition of that tick sees the same answers at any thread
/// count, and is not asked from the next tick on.
/// </summary>
sealed class StorageFilters
{
    /// <summary>
    /// The filters in system order, each with its system index.
    /// </summary>
    List<StorageHook<IStorageFilter>>? _filters;

    /// <summary>
    /// Whether any filter is registered; a search asks nothing without one.
    /// </summary>
    internal bool Active => _filters is not null;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is IStorageFilter filter) (_filters ??= []).Add(new StorageHook<IStorageFilter>(filter, systemIndex, "storage filter"));
    }

    /// <summary>
    /// Whether every filter still asked allows the use (<see cref="IStorageFilter.Allows"/>); true without one. Thread-safe.
    /// </summary>
    internal bool Allows(World world, CharacterId who, string storage, StorageUse use, string? record)
    {
        if (_filters is not { } filters) return true;

        foreach (var entry in filters)
        {
            if (!entry.AskedAt(world.Tick)) continue;

            try
            {
                if (!entry.Hook.Allows(world, who, storage, use, record)) return false;
            }
            catch (Exception e)
            {
                entry.Fault(world.Tick, e);
            }
        }

        return true;
    }

    /// <summary>
    /// Adds the filters that threw to the world's state hash. Successful filters add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_filters is not { } filters || !filters.Any(f => f.Faulted)) return;

        hash.Add(0x53544F52);
        foreach (var entry in filters)
        {
            if (!entry.Faulted) continue;

            hash.Add(entry.SystemIndex);
            hash.Add(entry.FaultTick);
        }
    }

    /// <summary>
    /// The record every item of <paramref name="items"/> shares, the one a search is asked for; null when they differ or there are none.
    /// </summary>
    internal static string? OneRecord(IEnumerable<ItemInstance> items)
    {
        string? record = null;
        foreach (var item in items)
        {
            if (record is null) record = item.Record;
            else if (item.Record != record) return null;
        }

        return record;
    }
}
