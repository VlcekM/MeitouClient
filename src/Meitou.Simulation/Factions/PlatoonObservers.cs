namespace Meitou.Simulation.Factions;

/// <summary>
/// The world's systems told of the platoons' changes (<see cref="IPlatoonObserver"/>). The registry notes its additions and removals as they
/// happen (<see cref="PlatoonRegistry.Note"/>, only while an observer is registered) and the population its held platoons' arrivals; once a tick,
/// after the slow world, <see cref="Tell"/> adds the loads and unloads since the last tick and passes them all on.
/// </summary>
sealed class PlatoonObservers
{
    /// <summary>
    /// The observing systems, each with its index and whether it is still told.
    /// </summary>
    List<(IPlatoonObserver Observer, int SystemIndex, bool Enabled)>? _observers;
    /// <summary>
    /// The observers still told, and the ones disabled after a failure.
    /// </summary>
    int _active, _faulted;
    /// <summary>
    /// The changes being told this tick (reused).
    /// </summary>
    readonly List<PlatoonChange> _telling = [];

    /// <summary>
    /// Whether any observer is told.
    /// </summary>
    internal bool Active => _active > 0;

    /// <summary>
    /// Registers <paramref name="system"/> when it is an observer.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not IPlatoonObserver observer) return;

        (_observers ??= []).Add((observer, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Adds the disabled observers to the world's state hash: a failed observer is no longer told. Working observers add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(0x504C544E);
        hash.Add(_faulted);
        foreach (var entry in _observers!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// Tells the observers the tick's changes: the registry's noted ones in the order they happened, then every platoon whose loaded state differs
    /// from the last one told, in id order. Nothing without an observer. Serial, once a tick after the slow world.
    /// </summary>
    internal void Tell(World world)
    {
        if (_active == 0) return;

        world.Platoons.TakeNotices(_telling);
        AddLoadChanges(world);
        foreach (var change in _telling)
        {
            Observe(world, new PlatoonNotice(world.Tick, change.Kind, change.Platoon, change.Gone, change.By));
            if (_active == 0) break;
        }

        _telling.Clear();
        if (_active == 0) world.Platoons.SetNoting(false);
    }

    /// <summary>
    /// Adds a <see cref="PlatoonEvent.Loaded"/> or <see cref="PlatoonEvent.Unloaded"/> change for each platoon whose characters came into or left
    /// the world since it was last told, marking it told.
    /// </summary>
    void AddLoadChanges(World world)
    {
        foreach (var p in world.Platoons.All)
        {
            bool loaded = p.State == PlatoonState.Loaded;
            if (loaded == p.ToldLoaded) continue;

            p.ToldLoaded = loaded;
            _telling.Add(new PlatoonChange(loaded ? PlatoonEvent.Loaded : PlatoonEvent.Unloaded, p, PlatoonGone.None, -1));
        }
    }

    /// <summary>
    /// Passes a notice to every enabled observer, disabling one that throws. Serial phases only.
    /// </summary>
    void Observe(World world, in PlatoonNotice notice)
    {
        var observers = _observers!;
        for (int i = 0; i < observers.Count; i++)
        {
            var entry = observers[i];
            if (!entry.Enabled) continue;

            try
            {
                entry.Observer.PlatoonChanged(world, notice);
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
    static void LogFault(IPlatoonObserver observer, Exception e)
    {
        string fault = $"{e.GetType().Name}: {e.Message}";
        SimulationExtensions.TellOutdated(observer, e);
        if (SimulationExtensions.FactoryOf(observer) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: platoon observer '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"platoon observer '{observer.GetType().FullName}' disabled for this world: {fault}");
    }
}
