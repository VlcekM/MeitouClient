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
    List<Entry>? _filters;

    /// <summary>
    /// Whether any filter is registered; a search asks nothing without one.
    /// </summary>
    internal bool Active => _filters is not null;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is IStorageFilter filter) (_filters ??= []).Add(new Entry(filter, systemIndex));
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
                if (!entry.Filter.Allows(world, who, storage, use, record)) return false;
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

    /// <summary>
    /// One filter and the tick it first threw in (-1 none).
    /// </summary>
    sealed class Entry(IStorageFilter filter, int systemIndex)
    {
        /// <summary>
        /// The tick the filter first threw in; -1 while it never has.
        /// </summary>
        long _faultTick = -1;

        /// <summary>
        /// The filter.
        /// </summary>
        public IStorageFilter Filter { get; } = filter;

        /// <summary>
        /// The filter's system index (what the hash records).
        /// </summary>
        public int SystemIndex { get; } = systemIndex;

        /// <summary>
        /// Whether the filter has thrown.
        /// </summary>
        public bool Faulted => FaultTick >= 0;

        /// <summary>
        /// The tick the filter first threw in; -1 while it never has.
        /// </summary>
        public long FaultTick => Interlocked.Read(ref _faultTick);

        /// <summary>
        /// Whether the filter is asked at <paramref name="tick"/>: never threw, or threw in this tick (it goes on being asked there, so the tick's
        /// answers do not depend on which worker threw first).
        /// </summary>
        public bool AskedAt(long tick)
        {
            long fault = Interlocked.Read(ref _faultTick);
            return fault < 0 || fault == tick;
        }

        /// <summary>
        /// Records the first throw at <paramref name="tick"/> with its log line; later throws in the same tick are not logged again.
        /// </summary>
        public void Fault(long tick, Exception e)
        {
            if (Interlocked.CompareExchange(ref _faultTick, tick, -1) != -1) return;

            string fault = $"{e.GetType().Name}: {e.Message}";
            SimulationExtensions.TellOutdated(Filter, e);
            if (SimulationExtensions.FactoryOf(Filter) is { } factory)
                Console.Error.WriteLine($"mod       {factory.Owner}: storage filter '{factory.Name}' disabled for this world: {fault}");
            else
                Console.Error.WriteLine($"storage filter '{Filter.GetType().FullName}' disabled for this world: {fault}");
        }
    }
}
