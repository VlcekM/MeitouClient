namespace Meitou.Simulation.Combat;

/// <summary>
/// The world's systems asked about attack and block play rates (<see cref="ICombatSpeedFilter"/>). Asked from the parallel phases, so a fault is
/// kept as the first tick it happened in (a lock-free minimum) and the filter is skipped from the next tick, as the experience filters are.
/// </summary>
sealed class CombatSpeedFilters
{
    /// <summary>
    /// The filtering systems with their index.
    /// </summary>
    List<(ICombatSpeedFilter Filter, int SystemIndex)>? _filters;
    /// <summary>
    /// Each filter's first faulted tick; <see cref="long.MaxValue"/> while it has none.
    /// </summary>
    long[] _faults = [];

    /// <summary>
    /// Whether any filter is registered; the rate is used as it is without one.
    /// </summary>
    internal bool Active => _filters is not null;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not ICombatSpeedFilter filter) return;

        (_filters ??= []).Add((filter, systemIndex));
        Array.Resize(ref _faults, _filters.Count);
        _faults[^1] = long.MaxValue;
    }

    /// <summary>
    /// Adds the faulted filters (with the tick of their first fault) to the world's state hash. Healthy filters add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        for (int i = 0; i < _faults.Length; i++)
        {
            if (_faults[i] == long.MaxValue) continue;

            hash.Add(0x53504446);
            hash.Add(_filters![i].SystemIndex);
            hash.Add(_faults[i]);
        }
    }

    /// <summary>
    /// The rate after every filter not faulted before this tick, each seeing the one before's answer. Any thread.
    /// </summary>
    internal float Pass(World world, CharacterId character, CombatSpeedKind kind, float rate)
    {
        var filters = _filters!;
        long tick = world.Tick;
        for (int i = 0; i < filters.Count; i++)
        {
            if (Volatile.Read(ref _faults[i]) < tick) continue;

            rate = Ask(world, i, new CombatSpeedAsk(tick, character, kind, rate));
        }

        return rate;
    }

    /// <summary>
    /// One filter's answer; the rate as it came when the filter throws or answers a rate that is not a finite number above 0.
    /// </summary>
    float Ask(World world, int i, in CombatSpeedAsk ask)
    {
        string fault;
        try
        {
            float rate = _filters![i].Filter.Rate(world, ask);
            if (float.IsFinite(rate) && rate > 0) return rate;

            fault = $"rate {rate} is not a finite number above 0";
        }
        catch (Exception e)
        {
            fault = $"{e.GetType().Name}: {e.Message}";
            SimulationExtensions.TellOutdated(_filters![i].Filter, e);
        }

        Fault(i, ask.Tick, fault);
        return ask.Rate;
    }

    /// <summary>
    /// Keeps the earliest faulted tick (concurrent callers may fault the same filter); logs the first.
    /// </summary>
    void Fault(int i, long tick, string fault)
    {
        long seen = Volatile.Read(ref _faults[i]);
        while (tick < seen)
        {
            long was = Interlocked.CompareExchange(ref _faults[i], tick, seen);
            if (was == seen)
            {
                if (seen == long.MaxValue) LogFault(_filters![i].Filter, tick, fault);
                return;
            }

            seen = was;
        }
    }

    /// <summary>
    /// A faulted filter's log line: the mod and the name it registered the system under, as the mods' other log lines; else its type.
    /// </summary>
    static void LogFault(ICombatSpeedFilter filter, long tick, string fault)
    {
        string what = $"failed at tick {tick}, dropped from the next: {fault}";
        if (SimulationExtensions.FactoryOf(filter) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: combat speed filter '{factory.Name}' {what}");
        else
            Console.Error.WriteLine($"combat speed filter '{filter.GetType().FullName}' {what}");
    }
}
