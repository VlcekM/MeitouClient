namespace Meitou.Simulation.Bodies;

/// <summary>
/// The world's systems told of deaths (<see cref="ICharacterDeathObserver"/>). A loaded character's death is marked on its medical state
/// (<see cref="MedicalState.DeathUnreported"/>) wherever it happens; a site that knows what killed it reports it at once (<see cref="Report"/>),
/// the world sweeps up the rest (<see cref="Sweep"/>) in serial steps, and a character leaving the world with its death unreported is reported
/// first (<see cref="CharacterWatch"/>), so every death is told once and in the same order whatever the threads did.
/// </summary>
sealed class DeathObservers
{
    /// <summary>
    /// The observing systems, each with its index and whether it is still told.
    /// </summary>
    List<(ICharacterDeathObserver Observer, int SystemIndex, bool Enabled)>? _observers;
    /// <summary>
    /// The observers still told, and the ones disabled after a failure.
    /// </summary>
    int _active, _faulted;

    /// <summary>
    /// Whether any observer is told; a death site can skip building the payload without one.
    /// </summary>
    internal bool Active => _active > 0;

    /// <summary>
    /// Registers <paramref name="system"/> when it is an observer.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not ICharacterDeathObserver observer) return;

        (_observers ??= []).Add((observer, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Adds the disabled observers to the world's state hash: a failed observer is no longer told. Working observers add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(0x44454154);
        hash.Add(_faulted);
        foreach (var entry in _observers!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// Reports the death of the character in <paramref name="slot"/> when it has not been reported yet, with the character whose act killed it
    /// (<paramref name="killer"/>, none when the engine names nobody) and the step that killed it; nothing, not even the payload, without an
    /// observer. Serial phases, or the paused interface between ticks.
    /// </summary>
    internal static void Report(World world, int slot, CharacterId killer, DeathSite site)
    {
        var observers = world.DeathObservers;
        var table = world.Characters;
        if (!observers.Active || table.Cold(slot) is not { Medical: { DeathUnreported: true } medical } cold) return;

        medical.DeathUnreported = false;
        var victim = new CharacterId(slot, table.Next[slot].Generation);
        int platoon = world.Squads.Find(cold.SquadId)?.PlatoonId ?? -1;
        int killerFaction = FactionOf(world, killer);
        observers.Observe(world,
            new CharacterDeath(world.Tick, victim, cold.RecordId, cold.Faction, platoon, medical.Cause, site, killer, killerFaction));
    }

    /// <summary>
    /// Reports every death no site has reported (bleeding out, hunger, a wound worsening, the debug kill, another system's kill), in slot order,
    /// with no killer. Serial; the world runs it after the parallel phases and after the slow world.
    /// </summary>
    internal void Sweep(World world)
    {
        if (_active == 0) return;

        var table = world.Characters;
        for (int slot = 0; slot < table.HighWater; slot++)
        {
            if (table.Cold(slot) is { Medical.DeathUnreported: true })
                Report(world, slot, CharacterId.None, DeathSite.Other);
        }
    }

    /// <summary>
    /// Reports a member of platoon <paramref name="platoon"/> killed in an off-screen fight that <paramref name="winnerFaction"/>'s platoon won:
    /// <paramref name="victim"/> when the member is a character in the world, else none, and its <paramref name="record"/> ("" for a member never
    /// made). Serial phases only.
    /// </summary>
    internal static void ReportOffScreen(World world, CharacterId victim, string record, int faction, int platoon, int winnerFaction)
    {
        var observers = world.DeathObservers;
        if (!observers.Active) return;

        observers.Observe(world,
            new CharacterDeath(world.Tick, victim, record, faction, platoon, DeathCause.None, DeathSite.OffScreenFight, CharacterId.None,
                winnerFaction));
    }

    /// <summary>
    /// The faction of <paramref name="id"/> while it names a character in the world; -1 otherwise.
    /// </summary>
    static int FactionOf(World world, CharacterId id) =>
        world.Characters.TryResolveNext(id, out int slot) && world.Characters.Cold(slot) is { } cold ? cold.Faction : -1;

    /// <summary>
    /// Passes a death to every enabled observer, disabling one that throws or removes the dead character from the world (against its contract;
    /// the removal it made stands, and the world's own removal under way is not repeated, <see cref="CharacterTable.Remove"/>). Serial phases only.
    /// </summary>
    void Observe(World world, in CharacterDeath death)
    {
        var observers = _observers!;
        for (int i = 0; i < observers.Count; i++)
        {
            var entry = observers[i];
            if (!entry.Enabled) continue;

            string? fault = null;
            try
            {
                entry.Observer.Died(world, death);
                if (!death.Victim.IsNone && !world.Characters.TryResolveNext(death.Victim, out _))
                    fault = "it removed the character it was told about";
            }
            catch (Exception e)
            {
                fault = $"{e.GetType().Name}: {e.Message}";
                SimulationExtensions.TellOutdated(entry.Observer, e);
            }

            if (fault is null || !observers[i].Enabled) continue;

            observers[i] = (entry.Observer, entry.SystemIndex, false);
            _active--;
            _faulted++;
            LogFault(entry.Observer, fault);
        }
    }

    /// <summary>
    /// A disabled observer's log line: the mod and the name it registered the system under, as the mods' other log lines; else its type.
    /// </summary>
    static void LogFault(ICharacterDeathObserver observer, string fault)
    {
        if (SimulationExtensions.FactoryOf(observer) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: death observer '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"death observer '{observer.GetType().FullName}' disabled for this world: {fault}");
    }
}
