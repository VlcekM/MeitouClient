using Meitou.Simulation.Items;

namespace Meitou.Simulation.Player;

/// <summary>
/// A right click on an item a code mod handles (docs/modding.md "Item use"): an item of a player character's own window, with no other character's
/// window to move it to, whose record a mod registered (<see cref="SimulationExtensions.ItemUses"/>). The handler runs in the player system's serial
/// step, before the click would equip or move the item (<see cref="QuickMoves"/>), and only for an item a quick move could act on.
/// </summary>
sealed class ItemUses
{
    /// <summary>
    /// The mods' item use handlers (null: no mods).
    /// </summary>
    internal SimulationExtensions? Extensions { get; set; }

    /// <summary>
    /// Runs the handler of the clicked item's record; true when it used the click, false when the click goes on as a quick move. Units the handler
    /// took out of the inventory are reported to observers (<see cref="ItemUseKind.ModUse"/>).
    /// </summary>
    internal bool Use(World world, QuickMoveItem quick)
    {
        if (quick.Into != CharacterId.None || Extensions is not { ItemUses.Count: > 0 } extensions) return false;
        if (!extensions.ItemUses.TryGetValue(quick.Item.Record, out var handler)) return false;

        var table = world.Characters;
        if (!table.TryResolveNext(quick.Character, out int s) || table.Cold(s) is not { IsPlayer: true, Inventory: { } root }) return false;

        var item = root.All().ElementAtOrDefault(quick.Item.Index);
        if (!Eligible(root, item, quick.Item)) return false;

        int before = item!.Quantity;
        bool used = handler(new ItemUse(world, quick.Character, item, root));
        if (!world.ItemUseObservers.Active) return used;

        int gone = root.All().Contains(item) ? before - item.Quantity : before;
        if (gone > 0) ItemUseObservers.Report(world, ItemUseKind.ModUse, item.Record, quick.Character, null, gone, true);

        return used;
    }

    /// <summary>
    /// Whether the click may reach the mod: the item a quick move could act on (<see cref="QuickMoves.Clickable"/>, which also checks the tag the
    /// window showed) and not locked on. Otherwise the click goes on as a quick move, which refuses it on the same checks.
    /// </summary>
    static bool Eligible(Inventory root, ItemInstance? item, InventoryItemToken token)
    {
        if (!QuickMoves.Clickable(root, item, token, out _)) return false;

        return !item!.LockedOn;
    }
}
