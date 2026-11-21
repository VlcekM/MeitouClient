namespace Meitou.Simulation.Trading;

/// <summary>
/// The world's systems that adjust trade prices (<see cref="ITradePriceFilter"/>).
/// </summary>
sealed class TradePriceFilters
{
    /// <summary>
    /// The filtering systems, each with its index and whether it is still asked.
    /// </summary>
    List<(ITradePriceFilter Filter, int SystemIndex, bool Enabled)>? _filters;
    /// <summary>
    /// The filters still asked, and the ones disabled after a failure.
    /// </summary>
    int _active, _faulted;

    /// <summary>
    /// Whether any filter is asked; a price site can skip building the deal without one.
    /// </summary>
    internal bool Active => _active > 0;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not ITradePriceFilter filter) return;

        (_filters ??= []).Add((filter, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Adds the disabled filters to the world's state hash: a failed filter no longer changes prices. Working filters add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(0x50524346);
        hash.Add(_faulted);
        foreach (var entry in _filters!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// <paramref name="price"/> after every enabled filter, in system order; the price itself without one.
    /// </summary>
    internal long Pass(World world, in TradeDeal deal, long price)
    {
        if (_active == 0) return price;

        var filters = _filters!;
        for (int i = 0; i < filters.Count; i++)
        {
            if (filters[i].Enabled) price = Ask(world, i, deal, price);
        }

        return price;
    }

    /// <summary>
    /// One filter's price; the price as it came when the filter throws or answers a negative one, which disables it.
    /// </summary>
    long Ask(World world, int i, in TradeDeal deal, long before)
    {
        var entry = _filters![i];
        string fault;
        try
        {
            long price = entry.Filter.Price(world, deal, before);
            if (price >= 0) return price;

            fault = $"price {price} is negative";
        }
        catch (Exception e)
        {
            fault = $"{e.GetType().Name}: {e.Message}";
            SimulationExtensions.TellOutdated(entry.Filter, e);
        }

        _filters[i] = (entry.Filter, entry.SystemIndex, false);
        _active--;
        _faulted++;
        LogFault(entry.Filter, fault);
        return before;
    }

    /// <summary>
    /// A disabled filter's log line: the mod and the name it registered the system under, as the mods' other log lines; else its type.
    /// </summary>
    static void LogFault(ITradePriceFilter filter, string fault)
    {
        if (SimulationExtensions.FactoryOf(filter) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: price filter '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"price filter '{filter.GetType().FullName}' disabled for this world: {fault}");
    }
}
