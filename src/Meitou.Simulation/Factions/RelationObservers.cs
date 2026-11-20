namespace Meitou.Simulation.Factions;

/// <summary>
/// The world's systems that observe changed faction relations (<see cref="IRelationChangeObserver"/>).
/// </summary>
sealed class RelationObservers
{
    /// <summary>
    /// The observing systems, each with its index and whether it still receives changes.
    /// </summary>
    List<(IRelationChangeObserver Observer, int SystemIndex, bool Enabled)>? _observers;
    /// <summary>
    /// The observers that receive changes.
    /// </summary>
    int _active;
    /// <summary>
    /// The observers disabled after a failure.
    /// </summary>
    int _faulted;
    /// <summary>
    /// A callback is running: the changes it makes are not observed again.
    /// </summary>
    bool _observing;

    /// <summary>
    /// Registers <paramref name="system"/> when it is an observer.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not IRelationChangeObserver observer) return;

        (_observers ??= []).Add((observer, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Adds the disabled observers to the world's state hash: a failed observer no longer receives changes. Successful observers add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(0x52454C46);
        hash.Add(_faulted);
        foreach (var entry in _observers!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// Passes a changed relation to every enabled observer; nothing without one or while a callback runs. Serial phases only.
    /// </summary>
    internal void Observe(World world, in RelationChange change)
    {
        if (_active == 0 || _observing) return;

        _observing = true;
        try
        {
            Dispatch(world, change);
        }
        finally
        {
            _observing = false;
        }
    }

    /// <summary>
    /// Calls each enabled observer, disabling one that throws.
    /// </summary>
    void Dispatch(World world, in RelationChange change)
    {
        var observers = _observers!;
        for (int i = 0; i < observers.Count; i++)
        {
            var entry = observers[i];
            if (!entry.Enabled) continue;

            try
            {
                entry.Observer.RelationChanged(world, change);
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
    static void LogFault(IRelationChangeObserver observer, Exception e)
    {
        string fault = $"{e.GetType().Name}: {e.Message}";
        SimulationExtensions.TellOutdated(observer, e);
        if (SimulationExtensions.FactoryOf(observer) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: relation observer '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"relation observer '{observer.GetType().FullName}' disabled for this world: {fault}");
    }
}
