namespace Meitou.Simulation.Crime;

/// <summary>
/// The world's systems that hear of goods taken (<see cref="IGoodsTakenObserver"/>).
/// </summary>
sealed class GoodsTakenObservers
{
    /// <summary>
    /// The observing systems, each with its index and whether it still receives takes.
    /// </summary>
    List<(IGoodsTakenObserver Observer, int SystemIndex, bool Enabled)>? _observers;
    /// <summary>
    /// The observers that receive takes, and the ones disabled after a failure.
    /// </summary>
    int _active, _faulted;

    /// <summary>
    /// Whether any observer receives takes; a take site can skip building the payload without one.
    /// </summary>
    internal bool Active => _active > 0;

    /// <summary>
    /// Registers <paramref name="system"/> when it is an observer.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not IGoodsTakenObserver observer) return;

        (_observers ??= []).Add((observer, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Adds the disabled observers to the world's state hash: a failed observer no longer receives takes. Successful observers add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(0x474F4F44);
        hash.Add(_faulted);
        foreach (var entry in _observers!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// Reports goods taken at the world's tick (<see cref="GoodsTaken"/>): only goods with an owner (a faction or a character) other than the
    /// taker itself; nothing, not even the payload, without an observer. Serial phases only.
    /// </summary>
    internal static void Report(World world, CharacterId taker, int ownerFaction, CharacterId owner, string site, string record, int units,
        bool caught)
    {
        if (!world.GoodsTakenObservers.Active || units <= 0 || ownerFaction < 0 && owner.IsNone || owner == taker) return;

        world.GoodsTakenObservers.Observe(world, new GoodsTaken(world.Tick, taker, ownerFaction, owner, site, record, units, caught));
    }

    /// <summary>
    /// Passes goods taken to every enabled observer, disabling one that throws; nothing without one. Serial phases only.
    /// </summary>
    internal void Observe(World world, in GoodsTaken take)
    {
        if (_active == 0) return;

        var observers = _observers!;
        for (int i = 0; i < observers.Count; i++)
        {
            var entry = observers[i];
            if (!entry.Enabled) continue;

            try
            {
                entry.Observer.GoodsTaken(world, take);
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
    static void LogFault(IGoodsTakenObserver observer, Exception e)
    {
        string fault = $"{e.GetType().Name}: {e.Message}";
        if (SimulationExtensions.FactoryOf(observer) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: goods taken observer '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"goods taken observer '{observer.GetType().FullName}' disabled for this world: {fault}");
    }
}
