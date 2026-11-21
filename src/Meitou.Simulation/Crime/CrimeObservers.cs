namespace Meitou.Simulation.Crime;

/// <summary>
/// The world's systems told of crimes (<see cref="ICrimeObserver"/>).
/// </summary>
sealed class CrimeObservers
{
    /// <summary>
    /// The observing systems, each with its index and whether it is still told.
    /// </summary>
    List<(ICrimeObserver Observer, int SystemIndex, bool Enabled)>? _observers;
    /// <summary>
    /// The observers still told, and the ones disabled after a failure.
    /// </summary>
    int _active, _faulted;

    /// <summary>
    /// Whether any observer is told; the crime system builds no event for it without one.
    /// </summary>
    internal bool Active => _active > 0;

    /// <summary>
    /// Registers <paramref name="system"/> when it is an observer.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not ICrimeObserver observer) return;

        (_observers ??= []).Add((observer, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Adds the disabled observers to the world's state hash: a failed observer is no longer told. Working observers add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(0x4352494D);
        hash.Add(_faulted);
        foreach (var entry in _observers!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// Passes a crime event to every enabled observer, disabling one that throws; nothing without one. Serial phases only.
    /// </summary>
    internal void Observe(World world, in CrimeEvent crime)
    {
        if (_active == 0) return;

        var observers = _observers!;
        for (int i = 0; i < observers.Count; i++)
        {
            var entry = observers[i];
            if (!entry.Enabled) continue;

            try
            {
                entry.Observer.CrimeNoted(world, crime);
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
    static void LogFault(ICrimeObserver observer, Exception e)
    {
        string fault = $"{e.GetType().Name}: {e.Message}";
        SimulationExtensions.TellOutdated(observer, e);
        if (SimulationExtensions.FactoryOf(observer) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: crime observer '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"crime observer '{observer.GetType().FullName}' disabled for this world: {fault}");
    }
}
