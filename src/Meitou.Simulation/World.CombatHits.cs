using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

/// <summary>
/// The world's combat hits: the systems that observe them and the host's feed.
/// </summary>
public sealed partial class World
{
    /// <summary>
    /// The systems that observe the hits, each with its index and whether it still receives them.
    /// </summary>
    List<(ICombatHitObserver Observer, int SystemIndex, bool Enabled)>? _combatHitObservers;
    /// <summary>
    /// The observers that receive hits, and the ones disabled after a failure.
    /// </summary>
    int _activeCombatHitObservers, _faultedCombatHitObservers;
    /// <summary>
    /// The host's hit feed (see <see cref="CombatHitFeed"/>).
    /// </summary>
    volatile System.Collections.Concurrent.ConcurrentQueue<CombatHit>? _combatHitFeed;

    /// <summary>
    /// Whether a system observes the hits.
    /// </summary>
    internal bool HasCombatHitObservers => _activeCombatHitObservers > 0;

    /// <summary>
    /// A resolved hit has somewhere to go (an observer or <see cref="CombatHitFeed"/>); the combat paths build no payload otherwise.
    /// </summary>
    internal bool WantsCombatHits => _activeCombatHitObservers > 0 || _combatHitFeed is not null;

    /// <summary>
    /// A copy of every resolved hit for the host (the mods' hit feeds, docs/modding.md); null (the default) records none. The host sets it and
    /// empties it every frame. Display only like <see cref="BloodMarks"/>: nothing in the simulation reads it, so it cannot change a run.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<CombatHit>? CombatHitFeed
    {
        get => _combatHitFeed;
        set => _combatHitFeed = value;
    }

    /// <summary>
    /// Registers <paramref name="system"/> as a hit observer when it is one.
    /// </summary>
    void RegisterCombatHitObserver(ITickSystem system, int systemIndex)
    {
        if (system is not ICombatHitObserver observer) return;
        (_combatHitObservers ??= []).Add((observer, systemIndex, true));
        _activeCombatHitObservers++;
    }

    /// <summary>
    /// Adds the disabled observers to the world's state hash. A failed observer no longer receives hits, so its disabled state belongs in determinism
    /// checks. Successful observers add nothing.
    /// </summary>
    internal void HashCombatHitObserverFaults(ref StateHasher hash)
    {
        if (_faultedCombatHitObservers == 0) return;
        hash.Add(0x43484F46);
        hash.Add(_faultedCombatHitObservers);
        foreach (var entry in _combatHitObservers!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// Serial, after the contact's ordinary consequences; no public emitter lets mods fabricate ordinary combat deliveries.
    /// </summary>
    internal void ObserveCombatHit(in CombatHit hit)
    {
        _combatHitFeed?.Enqueue(hit);
        if (!HasCombatHitObservers) return;

        var observers = _combatHitObservers!;
        for (int i = 0; i < observers.Count; i++)
        {
            var entry = observers[i];
            if (!entry.Enabled) continue;

            try
            {
                entry.Observer.HitResolved(this, hit);
            }
            catch (Exception e)
            {
                observers[i] = (entry.Observer, entry.SystemIndex, false);
                _activeCombatHitObservers--;
                _faultedCombatHitObservers++;
                LogObserverFault(entry.Observer, e);
            }
        }
    }

    /// <summary>
    /// A disabled observer's log line: the mod and the name it registered the system under, as the mods' other log lines; else its type.
    /// </summary>
    static void LogObserverFault(ICombatHitObserver observer, Exception e)
    {
        string fault = $"{e.GetType().Name}: {e.Message}";
        if (SimulationExtensions.FactoryOf(observer) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: combat observer '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"combat observer '{observer.GetType().FullName}' disabled for this world: {fault}");
    }
}