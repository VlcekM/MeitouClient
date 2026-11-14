using Meitou.Simulation.Items;

namespace Meitou.Simulation.Player;

/// <summary>
/// A right click on an item a code mod handles (docs/modding.md "Item use"): an item of a player character's own window, with no other character's
/// window to move it to, whose record a mod registered (<see cref="SimulationExtensions.ItemUses"/>). The handler runs in the player system's serial
/// step, before the click would equip or move the item (<see cref="QuickMoves"/>).
/// </summary>
sealed class ItemUses
{
    /// <summary>
    /// The mods' item use handlers (null: no mods).
    /// </summary>
    internal SimulationExtensions? Extensions { get; set; }

    /// <summary>
    /// Runs the handler of the clicked item's record; true when it used the click, false when the click goes on as a quick move.
    /// </summary>
    internal bool Use(World world, QuickMoveItem quick)
    {
        if (quick.Into != CharacterId.None || Extensions is not { ItemUses.Count: > 0 } extensions) return false;
        if (!extensions.ItemUses.TryGetValue(quick.Item.Record, out var handler)) return false;

        var table = world.Characters;
        if (!table.TryResolveNext(quick.Character, out int s) || table.Cold(s) is not { IsPlayer: true, Inventory: { } root }) return false;

        var item = root.All().ElementAtOrDefault(quick.Item.Index);
        if (!HeldItems.Matches(item, quick.Item)) return false;

        return handler(new ItemUse(world, quick.Character, item!, root));
    }
}
