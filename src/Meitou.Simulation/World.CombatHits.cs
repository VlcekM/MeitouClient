using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

public sealed partial class World
{
    List<(ICombatHitObserver Observer, int SystemIndex, bool Enabled)>? combatHitObservers;
    int activeCombatHitObservers, faultedCombatHitObservers;

    internal bool HasCombatHitObservers => activeCombatHitObservers > 0;

    void RegisterCombatHitObserver(ITickSystem system, int systemIndex)
    {
        if (system is not ICombatHitObserver observer) return;
        (combatHitObservers ??= []).Add((observer, systemIndex, true));
        activeCombatHitObservers++;
    }

    // A failed observer no longer receives hits, so its disabled state belongs in determinism checks. Successful observers add nothing.
    internal void HashCombatHitObserverFaults(ref StateHasher hash)
    {
        if (faultedCombatHitObservers == 0) return;
        hash.Add(0x43484F46);
        hash.Add(faultedCombatHitObservers);
        foreach (var entry in combatHitObservers!)
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
    }

    /// <summary>Serial, after the contact's ordinary consequences; no public emitter lets mods fabricate ordinary combat deliveries.</summary>
    internal void ObserveCombatHit(in CombatHit hit)
    {
        if (!HasCombatHitObservers) return;
        var observers = combatHitObservers!;
        for (int i = 0; i < observers.Count; i++)
        {
            var entry = observers[i];
            if (!entry.Enabled) continue;
            try { entry.Observer.HitResolved(this, hit); }
            catch (Exception e)
            {
                observers[i] = (entry.Observer, entry.SystemIndex, false);
                activeCombatHitObservers--;
                faultedCombatHitObservers++;
                Console.Error.WriteLine($"combat observer '{entry.Observer.GetType().FullName}' disabled for this world: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
