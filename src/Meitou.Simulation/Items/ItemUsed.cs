namespace Meitou.Simulation.Items;

/// <summary>
/// How an item was used up (<see cref="ItemUsed"/>).
/// </summary>
public enum ItemUseKind
{
    /// <summary>
    /// Food, raw food or a limb eaten (a character feeding itself, from its own, its backpack's or a squad mate's backpack).
    /// </summary>
    Eaten,
    /// <summary>
    /// A first-aid, robot repair or splint kit's charges spent on a patient.
    /// </summary>
    FirstAid,
    /// <summary>
    /// A crossbow bolt loaded: one charge (one shot) of the bolt item; finished when a whole bolt item left the inventory.
    /// </summary>
    Ammunition,
    /// <summary>
    /// A building material delivered into a construction or upgrade (a later dismantle refunds materials unreported).
    /// </summary>
    BuildingMaterial,
    /// <summary>
    /// A tool broken cutting a lock or shackles.
    /// </summary>
    Tool,
    /// <summary>
    /// A map read that revealed something.
    /// </summary>
    Map,
    /// <summary>
    /// A blueprint learned.
    /// </summary>
    Blueprint,
    /// <summary>
    /// A machine's input used by production, crafting, fuel or water, or smelted in a furnace.
    /// </summary>
    MachineInput,
    /// <summary>
    /// A research cost paid from the research benches.
    /// </summary>
    Research,
    /// <summary>
    /// Units of the clicked stack that a mod's item use (<see cref="SimulationExtensions.ItemUses"/>) took out of the user's inventory, whatever
    /// the handler did with them; charges or other items the handler spends are not seen.
    /// </summary>
    ModUse,
}

/// <summary>
/// An item used up, in a serial step of the tick (docs/modding.md "Items used up"): how, the item record's string id, who used it (none for a
/// machine, a research order or an upgrade pulling from the building's own stock), the building it went into or came from (null when none), how
/// much (charges for eating, first aid and crossbow bolts, else units) and whether a whole unit left the inventory or storage.
/// </summary>
public readonly record struct ItemUsed(long Tick, ItemUseKind Kind, string Record, CharacterId User, string? Site, float Amount, bool Finished);

/// <summary>
/// A system that hears of every item used up (docs/modding.md "Items used up"): called serially on the tick thread, in system order, inside
/// the step that used the item, once the item has changed. It may read the world and change its own state; it must not change items,
/// characters or other world state, since the step may still be walking them, so gameplay changes go through commands. One that throws is
/// disabled for that world, and the fault enters the state hash.
/// </summary>
public interface IItemUseObserver : ITickSystem
{
    /// <summary>
    /// An item was used up.
    /// </summary>
    void ItemUsedUp(World world, in ItemUsed use);
}
