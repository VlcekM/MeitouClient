namespace Meitou.Simulation.Items;

/// <summary>
/// What carry-off a ditch item filter is asked about (<see cref="DitchItemAsk.Kind"/>); room for more kinds.
/// </summary>
public enum DitchKind : byte
{
    /// <summary>
    /// The game's collection of what a character carries off to a storage (Ditch all resources, 242; the excess-loot store of 114 and a town
    /// errand's ditch): carried ITEMs of item function 0 or 15, and food but the first.
    /// </summary>
    Resources,
}

/// <summary>
/// One carried item the game would carry off (<see cref="IDitchItemFilter"/>): the world's tick, the carrier, the kind and the item (read only).
/// </summary>
public readonly record struct DitchItemAsk(long Tick, CharacterId Character, DitchKind Kind, ItemInstance Item);

/// <summary>
/// A system asked, per carried item, whether the game's automatic carry-off leaves it with its carrier (docs/modding.md "Ditch items"): every
/// character the collection runs for, the player's own on the squad AI's Ditch items and NPCs on a town errand. Kept items are neither looked for
/// a storage for nor put in one; the trip goes on for the rest. The collection is read by the AI's planning on worker threads too, so a filter
/// must be quick, thread-safe and a pure read of the ask, of the carrier's own state and of world state the tick has settled. A throw answers
/// "carry it off" for the rest of that tick; the filter is not asked from the next, and the fault enters the state hash. Engine API, not in the
/// original.
/// </summary>
public interface IDitchItemFilter : ITickSystem
{
    /// <summary>
    /// Whether <paramref name="ask"/>'s item stays with its carrier (true) or goes with the rest (false).
    /// </summary>
    bool Keeps(World world, in DitchItemAsk ask);
}

/// <summary>
/// The world's ditch item filters (<see cref="IDitchItemFilter"/>); asked from the parallel phases.
/// </summary>
sealed class DitchItemFilters
{
    /// <summary>
    /// The registered filters.
    /// </summary>
    readonly ParallelHooks<IDitchItemFilter> _hooks = new(0x44495443, "ditch item filter");

    /// <summary>
    /// Whether any filter is registered; every item the collection lists goes without one.
    /// </summary>
    internal bool Active => _hooks.Active;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is IDitchItemFilter filter) _hooks.Add(filter, system, systemIndex);
    }

    /// <summary>
    /// Adds the faulted filters to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash) => _hooks.HashFaults(ref hash);

    /// <summary>
    /// Whether a filter not faulted before this tick keeps <paramref name="item"/> with <paramref name="character"/>; the first that keeps it
    /// wins. Any thread.
    /// </summary>
    internal bool Keeps(World world, CharacterId character, DitchKind kind, ItemInstance item)
    {
        long tick = world.Tick;
        var ask = new DitchItemAsk(tick, character, kind, item);
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (!_hooks.Asked(i, tick)) continue;

            try
            {
                if (_hooks[i].Keeps(world, ask)) return true;
            }
            catch (Exception e)
            {
                _hooks.Threw(i, tick, e);
            }
        }

        return false;
    }
}
