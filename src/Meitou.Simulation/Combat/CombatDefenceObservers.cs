namespace Meitou.Simulation.Combat;

/// <summary>
/// The world's systems that hear of blocked and dodged melee blows (<see cref="ICombatDefenceObserver"/>).
/// </summary>
sealed class CombatDefenceObservers
{
    /// <summary>
    /// The observing systems, each with its index and whether it still receives defences.
    /// </summary>
    List<(ICombatDefenceObserver Observer, int SystemIndex, bool Enabled)>? _observers;
    /// <summary>
    /// The observers that still receive defences.
    /// </summary>
    int _active;
    /// <summary>
    /// The observers disabled after a failure.
    /// </summary>
    int _faulted;

    /// <summary>
    /// Whether any observer receives defences; combat builds no payload without one.
    /// </summary>
    internal bool Active => _active > 0;

    /// <summary>
    /// Registers <paramref name="system"/> when it is an observer.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not ICombatDefenceObserver observer) return;

        (_observers ??= []).Add((observer, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Adds the disabled observers to the world's state hash: a failed observer no longer receives defences. Successful observers add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(0x44454643);
        hash.Add(_faulted);
        foreach (var entry in _observers!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// Passes a defence to every enabled observer, disabling one that throws. Serial (Commit).
    /// </summary>
    internal void Observe(World world, in CombatDefence defence)
    {
        if (_active == 0) return;

        var observers = _observers!;
        for (int i = 0; i < observers.Count; i++)
        {
            var entry = observers[i];
            if (!entry.Enabled) continue;

            try
            {
                entry.Observer.Defended(world, defence);
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
    static void LogFault(ICombatDefenceObserver observer, Exception e)
    {
        string fault = $"{e.GetType().Name}: {e.Message}";
        SimulationExtensions.TellOutdated(observer, e);
        if (SimulationExtensions.FactoryOf(observer) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: defence observer '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"defence observer '{observer.GetType().FullName}' disabled for this world: {fault}");
    }
}
