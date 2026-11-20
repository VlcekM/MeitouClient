namespace Meitou.Simulation.Trading;

/// <summary>
/// The world's systems that hear of completed deals (<see cref="ITradeObserver"/>).
/// </summary>
sealed class TradeObservers
{
    /// <summary>
    /// The observing systems, each with its index and whether it still receives deals.
    /// </summary>
    List<(ITradeObserver Observer, int SystemIndex, bool Enabled)>? _observers;
    /// <summary>
    /// The observers that receive deals, and the ones disabled after a failure.
    /// </summary>
    int _active, _faulted;

    /// <summary>
    /// Whether any observer receives deals; a deal can skip building the payload without one.
    /// </summary>
    internal bool Active => _active > 0;

    /// <summary>
    /// Registers <paramref name="system"/> when it is an observer.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not ITradeObserver observer) return;

        (_observers ??= []).Add((observer, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Adds the disabled observers to the world's state hash: a failed observer no longer receives deals. Successful observers add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(0x53414C45);
        hash.Add(_faulted);
        foreach (var entry in _observers!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// Reports a completed deal at the world's tick (<see cref="TradeSale"/>); nothing, not even the payload, without an observer or with no units.
    /// Serial phases, or between ticks on the host's thread (the paused interface's trades).
    /// </summary>
    internal static void Report(World world, TradeSaleKind kind, string record, int units, long price, TradeParty buyer, TradeParty seller,
        string site = "", bool delivered = true, bool buyback = false)
    {
        if (!world.TradeObservers.Active || units <= 0) return;

        var sale = new TradeSale(world.Tick, kind, record, units, price, buyer, seller, site, delivered, buyback);
        world.TradeObservers.Observe(world, sale);
    }

    /// <summary>
    /// The trade side of a character: it, its squad and its faction; none for an empty slot.
    /// </summary>
    internal static TradeParty Party(World world, int slot)
    {
        if (world.Characters.Cold(slot) is not { } cold) return new TradeParty(CharacterId.None, -1, -1);

        return new TradeParty(world.Characters.IdOf(slot), cold.SquadId, cold.Faction);
    }

    /// <summary>
    /// Passes a deal to every enabled observer, disabling one that throws; nothing without one. Serial phases, or between ticks on the host's thread.
    /// </summary>
    internal void Observe(World world, in TradeSale sale)
    {
        if (_active == 0) return;

        var observers = _observers!;
        for (int i = 0; i < observers.Count; i++)
        {
            var entry = observers[i];
            if (!entry.Enabled) continue;

            try
            {
                entry.Observer.Traded(world, sale);
            }
            catch (Exception e)
            {
                observers[i] = (entry.Observer, entry.SystemIndex, false);
                _active--;
                _faulted++;
                LogFault(entry.Observer, e);
            }
        }
    }

    /// <summary>
    /// A disabled observer's log line: the mod and the name it registered the system under, as the mods' other log lines; else its type.
    /// </summary>
    static void LogFault(ITradeObserver observer, Exception e)
    {
        string fault = $"{e.GetType().Name}: {e.Message}";
        SimulationExtensions.TellOutdated(observer, e);
        if (SimulationExtensions.FactoryOf(observer) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: trade observer '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"trade observer '{observer.GetType().FullName}' disabled for this world: {fault}");
    }
}
