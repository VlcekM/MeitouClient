namespace Meitou.Simulation.Bodies;

/// <summary>
/// Why the engine is about to take a character or body out of the world (<see cref="RemovalAsk.Kind"/>); room for more kinds.
/// </summary>
public enum RemovalKind : byte
{
    /// <summary>
    /// A corpse lay its 12 game hours ("corpse decayed").
    /// </summary>
    CorpseDecayed,
    /// <summary>
    /// A corpse lies in a zone of the map that is not loaded ("corpse unloaded"), carried or held or not.
    /// </summary>
    CorpseUnloaded,
    /// <summary>
    /// A platoon the population does not keep leaves the loaded world: despawned as it unloads, or thinned member by member while it stays (its
    /// members out of the zones destroyed), and no knocked-out member holds it loaded. Kept, it unloads with its characters stored, as a persistent
    /// one. Asked serially.
    /// </summary>
    PlatoonDespawned,
    /// <summary>
    /// An off-screen platoon the population does not keep and nobody wants counts towards its despawn (the orphan timer, 60 s). Kept, the timer
    /// starts again. Asked on any thread.
    /// </summary>
    PlatoonOrphaned,
}

/// <summary>
/// A removal about to happen (<see cref="IRemovalVeto"/>): the world's tick, the character or body (none for a platoon kind), why, and the platoon's
/// id (<see cref="World.Platoons"/>; -1 for a corpse kind).
/// </summary>
public readonly record struct RemovalAsk(long Tick, CharacterId Character, RemovalKind Kind, int Platoon = -1);

/// <summary>
/// A system asked before the engine takes a character or body out of the world for a <see cref="RemovalKind"/> (docs/modding.md "Removals"):
/// true keeps it this time (it is asked again the next time the rule would take it, every tick for a corpse). Called in system order until one
/// keeps it: serially for the corpse kinds (in slot order) and <see cref="RemovalKind.PlatoonDespawned"/>, on any thread and concurrently for
/// <see cref="RemovalKind.PlatoonOrphaned"/>, so a veto must be quick, thread-safe and a pure read of the ask and of world state the tick has
/// settled; it must not change characters or other world state. A throw lets the removal go ahead; the veto is still asked for the rest of that
/// tick, not from the next, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface IRemovalVeto : ITickSystem
{
    /// <summary>
    /// Whether to keep the character or body <paramref name="ask"/> names.
    /// </summary>
    bool Keep(World world, in RemovalAsk ask);
}

/// <summary>
/// The world's removal vetoes (<see cref="IRemovalVeto"/>); any thread.
/// </summary>
sealed class RemovalVetoes
{
    /// <summary>
    /// The registered vetoes.
    /// </summary>
    readonly ParallelHooks<IRemovalVeto> _hooks = new(0x52454D56, "removal veto");

    /// <summary>
    /// Registers <paramref name="system"/> when it is a veto.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is IRemovalVeto veto) _hooks.Add(veto, system, systemIndex);
    }

    /// <summary>
    /// Adds the disabled vetoes to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash) => _hooks.HashFaults(ref hash);

    /// <summary>
    /// Whether a veto keeps <paramref name="character"/> from a removal of <paramref name="kind"/>; false without one.
    /// </summary>
    internal bool Keeps(World world, CharacterId character, RemovalKind kind) =>
        _hooks.Active && Keeps(world, new RemovalAsk(world.Tick, character, kind));

    /// <summary>
    /// Whether a veto keeps the platoon <paramref name="platoon"/> from a removal of <paramref name="kind"/>; false without one. Any thread.
    /// </summary>
    internal bool KeepsPlatoon(World world, int platoon, RemovalKind kind) =>
        _hooks.Active && Keeps(world, new RemovalAsk(world.Tick, CharacterId.None, kind, platoon));

    /// <summary>
    /// Whether a veto not faulted before this tick keeps what <paramref name="ask"/> names.
    /// </summary>
    bool Keeps(World world, in RemovalAsk ask)
    {
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks.Asked(i, ask.Tick) && Keeps(world, i, ask)) return true;
        }

        return false;
    }

    /// <summary>
    /// One veto's answer; false (and faulted) when it throws.
    /// </summary>
    bool Keeps(World world, int i, in RemovalAsk ask)
    {
        try
        {
            return _hooks[i].Keep(world, ask);
        }
        catch (Exception e)
        {
            _hooks.Threw(i, ask.Tick, e);
            return false;
        }
    }
}
