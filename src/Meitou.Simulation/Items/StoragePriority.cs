namespace Meitou.Simulation.Items;

/// <summary>
/// How much a character prefers a storage the game picks for it (<see cref="IStoragePriority"/>), lowest first: of the storages a search finds,
/// the highest priority wins and the nearest among equals. <see cref="Normal"/> (the default) is the original's order, nearest first;
/// <see cref="Forbidden"/> passes the storage over, as a storage filter's refusal does. Engine API, not in the original.
/// </summary>
public enum StoragePriority : sbyte
{
    /// <summary>
    /// Never used: the search passes the storage over.
    /// </summary>
    Forbidden = -3,
    /// <summary>
    /// Used only when no storage of a higher priority qualifies.
    /// </summary>
    Low = -2,
    /// <summary>
    /// Below normal, above low.
    /// </summary>
    Deferred = -1,
    /// <summary>
    /// The default: what every storage is without a priority system.
    /// </summary>
    Normal = 0,
    /// <summary>
    /// Above normal.
    /// </summary>
    Preferred = 1,
    /// <summary>
    /// Above preferred.
    /// </summary>
    Important = 2,
    /// <summary>
    /// Above important.
    /// </summary>
    High = 3,
    /// <summary>
    /// The highest: used before every other storage that qualifies.
    /// </summary>
    Critical = 4,
}

/// <summary>
/// A system asked how much a character prefers a storage when the game itself picks one for it (docs/modding.md "Storage priority"): asked at the
/// same searches as <see cref="IStorageFilter"/>, for each storage the filters allow. The search takes the highest priority and, among equals, the
/// storage it takes today (the nearest). Never asked for what the player does by hand. Asked also on worker threads during the AI's parallel steps,
/// so it must be quick, thread-safe and a pure read of its arguments and of world state the tick has settled. One that throws is answered as
/// <see cref="StoragePriority.Normal"/> for the rest of that tick and is not asked again from the next tick on; the fault enters the state hash.
/// Engine API, not in the original.
/// </summary>
public interface IStoragePriority : ITickSystem
{
    /// <summary>
    /// How much <paramref name="who"/> prefers the storage <paramref name="storage"/> (a player machine's site id or a container's fixture id)
    /// to <paramref name="use"/> for <paramref name="record"/> (an item record's string id; null when the search is not for one item).
    /// </summary>
    StoragePriority Priority(World world, CharacterId who, string storage, StorageUse use, string? record);
}
