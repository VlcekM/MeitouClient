namespace Meitou.Simulation.Items;

/// <summary>
/// Why an inventory is arranged (<see cref="InventoryArrangeAsk.Reason"/>). New reasons are added at the end.
/// </summary>
public enum InventoryArrangeReason : byte
{
    /// <summary>
    /// A window's ARRANGE button: a character's main grid or worn bag, a machine's or container's storage, the trade window's stock.
    /// </summary>
    Button,
    /// <summary>
    /// A held item goes back and its inventory takes it nowhere: the inventory is arranged to make room for it.
    /// </summary>
    MakeRoom,
    /// <summary>
    /// The trade window's stock grid is built from the shop's goods (the window opens, the shop restocks while it shows).
    /// </summary>
    StockShown,
}

/// <summary>
/// One grid section an arrange covers: <see cref="Grid"/>'s items in <see cref="Section"/> (<c>main</c> or <c>backpack_content</c>).
/// </summary>
public readonly record struct ArrangeGrid(Inventory Grid, string Section)
{
    /// <summary>
    /// The section's items, in the grid's list order.
    /// </summary>
    public IEnumerable<ItemInstance> Items
    {
        get
        {
            string section = Section;
            return Grid.Items.Where(i => i.Section == section);
        }
    }
}

/// <summary>
/// An arrange about to run (<see cref="IInventoryArrangeFilter"/>): the tick, the root inventory, the window it belongs to (<see cref="ItemOwner"/>:
/// a character, the trade window's stock, a machine's or container's site), why, the grids arranged (the main grid and the worn bag's, or one of
/// them) and whether the engine has merged their partial stacks for this filter (<see cref="InventoryArrangement.MergeFirst"/>).
/// </summary>
public readonly record struct InventoryArrangeAsk(long Tick, Inventory Root, ItemOwner Owner, InventoryArrangeReason Reason,
    IReadOnlyList<ArrangeGrid> Grids, bool Merged);

/// <summary>
/// Where an arrangement puts one item: its cell in its own grid, and whether it lies turned (null: as it lies now).
/// </summary>
public readonly record struct ItemPlacement(ItemInstance Item, int X, int Y, bool? Rotated = null);

/// <summary>
/// A filter's arrangement (<see cref="IInventoryArrangeFilter.Arrange"/>): every item of the asked grids placed once, each in its own grid, inside it
/// and overlapping none; the grids' items are then listed in placement order. <see cref="MergeFirst"/> instead asks the engine to merge the grids'
/// partial stacks first and ask again.
/// </summary>
public sealed class InventoryArrangement
{
    /// <summary>
    /// The answer that has the engine merge the grids' partial stacks (the game's own stack join, each stack topping up the ones before it in list
    /// order) and ask the same filter again with <see cref="InventoryArrangeAsk.Merged"/> set; answered again then, it is a fault.
    /// </summary>
    public static InventoryArrangement MergeFirst { get; } = new([], merge: true);

    /// <summary>
    /// An arrangement placing the items as <paramref name="placements"/> say.
    /// </summary>
    public InventoryArrangement(IReadOnlyList<ItemPlacement> placements) : this(placements, merge: false) { }

    InventoryArrangement(IReadOnlyList<ItemPlacement> placements, bool merge)
    {
        ArgumentNullException.ThrowIfNull(placements);
        Placements = placements;
        Merge = merge;
    }

    /// <summary>
    /// Where each item goes.
    /// </summary>
    public IReadOnlyList<ItemPlacement> Placements { get; }

    /// <summary>
    /// Whether this is <see cref="MergeFirst"/>.
    /// </summary>
    internal bool Merge { get; }
}

/// <summary>
/// A system that replaces how inventories are arranged (docs/modding.md "Inventory arrange"): asked before every arrange the game runs (a window's
/// ARRANGE, making room for a held item going back, the trade window's stock being built), serially, in system order. It answers its own
/// arrangement, or null to leave the arrange to the next filter and finally the game's. It must be a pure read of the ask and the world: the engine
/// applies the answer. One that throws or answers an invalid arrangement is undone for that arrange and disabled for that world, and the fault
/// enters the state hash. Engine API, not in the original.
/// </summary>
public interface IInventoryArrangeFilter : ITickSystem
{
    /// <summary>
    /// The arrangement for <paramref name="ask"/>; null for the game's (or a later filter's).
    /// </summary>
    InventoryArrangement? Arrange(World world, in InventoryArrangeAsk ask);
}
