namespace Meitou.Simulation.Items;

/// <summary>
/// The world's systems that observe items used up (<see cref="IItemUseObserver"/>).
/// </summary>
sealed class ItemUseObservers
{
    /// <summary>
    /// The observing systems, each with its index and whether it still receives uses.
    /// </summary>
    List<(IItemUseObserver Observer, int SystemIndex, bool Enabled)>? _observers;
    /// <summary>
    /// The observers that receive uses, and the ones disabled after a failure.
    /// </summary>
    int _active, _faulted;

    /// <summary>
    /// Whether any observer receives uses; a use site can skip building the payload without one.
    /// </summary>
    internal bool Active => _active > 0;

    /// <summary>
    /// Registers <paramref name="system"/> when it is an observer.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not IItemUseObserver observer) return;

        (_observers ??= []).Add((observer, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Adds the disabled observers to the world's state hash: a failed observer no longer receives uses. Successful observers add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(0x49545546);
        hash.Add(_faulted);
        foreach (var entry in _observers!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// Reports an item used up at the world's tick (<see cref="ItemUsed"/>); nothing, not even the payload, without an observer. Serial phases only.
    /// </summary>
    internal static void Report(World world, ItemUseKind kind, string record, CharacterId user, string? site, float amount, bool finished)
    {
        if (!world.ItemUseObservers.Active) return;

        world.ItemUseObservers.Observe(world, new ItemUsed(world.Tick, kind, record, user, site, amount, finished));
    }

    /// <summary>
    /// Passes an item used up to every enabled observer, disabling one that throws; nothing without one. Serial phases only.
    /// </summary>
    internal void Observe(World world, in ItemUsed use)
    {
        if (_active == 0) return;

        var observers = _observers!;
        for (int i = 0; i < observers.Count; i++)
        {
            var entry = observers[i];
            if (!entry.Enabled) continue;

            try
            {
                entry.Observer.ItemUsedUp(world, use);
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
    static void LogFault(IItemUseObserver observer, Exception e)
    {
        string fault = $"{e.GetType().Name}: {e.Message}";
        SimulationExtensions.TellOutdated(observer, e);
        if (SimulationExtensions.FactoryOf(observer) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: item use observer '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"item use observer '{observer.GetType().FullName}' disabled for this world: {fault}");
    }
}
