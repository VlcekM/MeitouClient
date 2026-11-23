namespace Meitou.Simulation.Combat;

/// <summary>
/// Where the engine asks whether a fighter may take on a target (<see cref="EngageAsk.Kind"/>); room for more kinds.
/// </summary>
public enum EngageKind : byte
{
    /// <summary>
    /// A fight is about to start or be ordered again (<c>CombatSystem</c>'s fight start): the player's attack order, a defence, a goal's or a squad's
    /// join, a guard's arrest, an NPC order's assault, a loaded attack order. Asked serially.
    /// </summary>
    Start,

    /// <summary>
    /// A candidate of a target choice: a fighter choosing whom to fight next, or a character joining a fight choosing whom to attack (the attacker of
    /// a protectee, a squad's join). A refused candidate is passed over and the next best taken. Asked on any thread.
    /// </summary>
    Choice,
}

/// <summary>
/// A fighter about to take on a target (<see cref="ICombatEngageFilter"/>): the tick, the two characters, where it is asked, whether the target is
/// the player's pick (an attack order the player gave, or the current target such an order named) and whether the fight is a defence.
/// </summary>
public readonly record struct EngageAsk(long Tick, CharacterId Attacker, CharacterId Target, EngageKind Kind, bool Picked, bool Defensive);

/// <summary>
/// A system asked whether a fighter may take on a target, everywhere a fight starts and for every candidate of a target choice (docs/modding.md
/// "Engagements"): false refuses it (a refused start does not fight; a refused candidate is passed over). Filters run in system order until one
/// refuses. <see cref="EngageKind.Choice"/> is asked on any thread, concurrently, so a filter must be quick, thread-safe and a pure read of the ask
/// and of world state the tick has settled. A throw allows that ask; the filter is still asked for the rest of that tick, not from the next, and the
/// fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface ICombatEngageFilter : ITickSystem
{
    /// <summary>
    /// Whether the attacker may take on the target <paramref name="ask"/> names.
    /// </summary>
    bool Allows(World world, in EngageAsk ask);
}

/// <summary>
/// The world's engagement filters (<see cref="ICombatEngageFilter"/>); any thread.
/// </summary>
sealed class CombatEngageFilters
{
    /// <summary>
    /// The registered filters.
    /// </summary>
    readonly ParallelHooks<ICombatEngageFilter> _hooks = new(0x454E4741, "engagement filter");

    /// <summary>
    /// Whether any filter is registered.
    /// </summary>
    internal bool Active => _hooks.Active;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is ICombatEngageFilter filter) _hooks.Add(filter, system, systemIndex);
    }

    /// <summary>
    /// Adds the faulted filters to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash) => _hooks.HashFaults(ref hash);

    /// <summary>
    /// Whether every filter not faulted before this tick allows the slot <paramref name="attacker"/> to take on <paramref name="target"/>; true
    /// without one. Any thread.
    /// </summary>
    internal bool Allows(World world, int attacker, int target, EngageKind kind, bool picked, bool defensive)
    {
        if (!_hooks.Active) return true;

        var table = world.Characters;
        var ask = new EngageAsk(world.Tick, table.IdOf(attacker), table.IdOf(target), kind, picked, defensive);
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks.Asked(i, ask.Tick) && !Allows(world, i, ask)) return false;
        }

        return true;
    }

    /// <summary>
    /// One filter's answer; true (and faulted) when it throws.
    /// </summary>
    bool Allows(World world, int i, in EngageAsk ask)
    {
        try
        {
            return _hooks[i].Allows(world, ask);
        }
        catch (Exception e)
        {
            _hooks.Threw(i, ask.Tick, e);
            return true;
        }
    }
}
