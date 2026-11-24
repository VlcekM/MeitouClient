namespace Meitou.Simulation.Crime;

/// <summary>
/// Goods taken from someone else's keeping (<see cref="IGoodsTakenObserver"/>): the world's tick, the taker, the faction owning the goods (-1
/// none), the character they were taken from (none for a container or machine), the building's machine or container site they came out of ("" for
/// a character), the item record's string id, the units and whether the taker was caught (the take was stopped and the goods stayed).
/// </summary>
public readonly record struct GoodsTaken(long Tick, CharacterId Taker, int OwnerFaction, CharacterId Owner, string Site, string Record, int Units,
    bool Caught);

/// <summary>
/// A system that hears of the player's characters taking goods that are not their own by hand (docs/modding.md "Goods taken"): a held item
/// dragged or given out of an owned container or another character, a click take out of an owned container or a town machine, a right-click loot
/// take off a body; not the take-all loot, NPCs' own looting or money. It hears of takes the game makes no crime of too, such as the player's own
/// characters taking from a container of the player's faction or out of the player's own machines (a storage's grid, a bench's output: owned by
/// the player's faction), so a mod can judge them by its own rules or track where its items went. Called serially on the tick thread, in
/// system order, once the goods have moved (with the units that moved) or the taker was caught; it may read the world and change its own state,
/// and must not change items, characters or other world state in the call (gameplay changes go through commands). One that throws is disabled
/// for that world, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface IGoodsTakenObserver : ITickSystem
{
    /// <summary>
    /// Goods were taken, or a take of them was caught.
    /// </summary>
    void GoodsTaken(World world, in GoodsTaken take);
}
