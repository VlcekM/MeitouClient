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
}

/// <summary>
/// A removal about to happen (<see cref="IRemovalVeto"/>): the world's tick, the character or body, and why.
/// </summary>
public readonly record struct RemovalAsk(long Tick, CharacterId Character, RemovalKind Kind);

/// <summary>
/// A system asked before the engine takes a character or body out of the world for a <see cref="RemovalKind"/> (docs/modding.md "Removals"):
/// true keeps it this time (it is asked again the next time the rule would take it, every tick for a corpse). Called serially in system order,
/// in slot order, until one keeps it. It may read the world and change its own hashed state, and must not change characters or other world state.
/// One that throws is disabled for that world (the removal goes ahead), and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface IRemovalVeto : ITickSystem
{
    /// <summary>
    /// Whether to keep the character or body <paramref name="ask"/> names.
    /// </summary>
    bool Keep(World world, in RemovalAsk ask);
}

/// <summary>
/// The world's removal vetoes (<see cref="IRemovalVeto"/>); serial phases only.
/// </summary>
sealed class RemovalVetoes
{
    /// <summary>
    /// The registered vetoes.
    /// </summary>
    readonly SerialHooks<IRemovalVeto> _hooks = new(0x52454D56, "removal veto");

    /// <summary>
    /// Registers <paramref name="system"/> when it is a veto.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex) => _hooks.Register(system, systemIndex);

    /// <summary>
    /// Adds the disabled vetoes to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash) => _hooks.HashFaults(ref hash);

    /// <summary>
    /// Whether an enabled veto keeps <paramref name="character"/> from a removal of <paramref name="kind"/>; false without one. Serial phases.
    /// </summary>
    internal bool Keeps(World world, CharacterId character, RemovalKind kind)
    {
        if (!_hooks.Active) return false;

        var ask = new RemovalAsk(world.Tick, character, kind);
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks.Enabled(i) && Keeps(world, i, ask)) return true;
        }

        return false;
    }

    /// <summary>
    /// One veto's answer; false (and disabled) when it throws.
    /// </summary>
    bool Keeps(World world, int i, in RemovalAsk ask)
    {
        try
        {
            return _hooks[i].Keep(world, ask);
        }
        catch (Exception e)
        {
            _hooks.Threw(i, e);
            return false;
        }
    }
}
