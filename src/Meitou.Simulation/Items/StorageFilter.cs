namespace Meitou.Simulation.Items;

/// <summary>
/// What a character would do at a storage the game picks for it (<see cref="IStorageFilter"/>).
/// </summary>
public enum StorageUse : byte
{
    /// <summary>
    /// Take out of it: food to eat, a machine's input to fetch, building materials.
    /// </summary>
    Take,
    /// <summary>
    /// Put into it: loot to stash, resources to ditch, a machine's output to haul.
    /// </summary>
    Put,
}

/// <summary>
/// A system asked whether a character may use a storage when the game itself picks one for it (docs/modding.md "Storage use"): a food store, a
/// stash or ditch store, a machine operator's or a town machine filler's source, an operator's target, a builder's materials, a forager's store.
/// Never asked for what the player does by hand. Asked also on worker threads during the AI's parallel steps, so it must be quick, thread-safe and
/// a pure read of its arguments and of world state the tick has settled (its own system's serial step, the last finished tick). An order given while
/// the game is paused (a build order's materials) asks it between ticks on the host's thread, with the coming tick's number. One that throws is
/// answered as allowing for the rest of that tick and is not asked again from the next tick on; the fault enters the state hash. Engine API, not in
/// the original.
/// </summary>
public interface IStorageFilter : ITickSystem
{
    /// <summary>
    /// Whether <paramref name="who"/> may <paramref name="use"/> the storage <paramref name="storage"/> (a player machine's site id or a
    /// container's fixture id) for <paramref name="record"/> (an item record's string id; null when the search is not for one item: any
    /// food, or whatever the character carries).
    /// </summary>
    bool Allows(World world, CharacterId who, string storage, StorageUse use, string? record);
}
