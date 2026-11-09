using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Items;

/// <summary>
/// A character's inventory: its items in sections, with their grid cells. The main section is a grid of <see cref="MainWidth"/> x
/// <see cref="MainHeight"/> cells (the size observed in the vanilla inventory screenshot, docs/game/ui-screens.md); a backpack's contents are an
/// inventory of the CONTAINER's storage size. Only serial phases change it.
/// </summary>
public sealed class Inventory(int width, int height)
{
    /// <summary>
    /// The main grid's size in cells.
    /// </summary>
    public const int MainWidth = 8, MainHeight = 10;

    /// <summary>
    /// A character's inventory with the main grid's size.
    /// </summary>
    public Inventory() : this(MainWidth, MainHeight) { }

    /// <summary>
    /// The width of the grid in cells.
    /// </summary>
    public int Width { get; } = width;
    /// <summary>
    /// The height of the grid in cells.
    /// </summary>
    public int Height { get; } = height;
    /// <summary>
    /// The items, each with its section and cell.
    /// </summary>
    public List<ItemInstance> Items { get; } = [];

    /// <summary>
    /// The worn backpack, if any.
    /// </summary>
    public ItemInstance? Backpack => Items.Find(i => i.Section == "backpack_attach");

    /// <summary>
    /// The weight of everything in this inventory (not including a carried person or fitted prosthetics, which vanilla keeps outside the inventory).
    /// </summary>
    public float ContentWeight()
    {
        float w = 0;
        foreach (var i in Items)
            if (!InventoryCommands.IsLimbSection(i.Section)) w += i.Weight;
        return w;
    }

    /// <summary>
    /// <see cref="ContentWeight()"/> with the item on the mouse when it was lifted from this inventory: the original keeps counting a held item in
    /// the section it came from (<c>FUN_140749160</c>), times the worn bag's factor when that is the bag's contents.
    /// </summary>
    public float ContentWeight(HeldItem? held)
    {
        float w = ContentWeight();
        if (held is null || held.Grid != this || InventoryCommands.IsLimbSection(held.Section)) return w;
        float factor = held.Section == "backpack_content" ? Backpack?.ContentsFactor ?? 1 : 1;
        return w + held.Item.Weight * factor;
    }

    /// <summary>
    /// First cell where a w x h footprint fits among the items of <paramref name="section"/>, or false.
    /// </summary>
    public bool FindCell(string section, int w, int h, out int x, out int y)
    {
        for (y = 0; y + h <= Height; y++)
            for (x = 0; x + w <= Width; x++)
                if (IsFree(section, x, y, w, h)) return true;
        x = y = 0;
        return false;
    }

    /// <summary>
    /// The rectangles the grid is split into (a building shop's stock, one per stock container, <see cref="Trading.Shop.ForBuilding"/>): an item lies
    /// wholly inside one, the cells outside them hold nothing. Null: the whole grid is one.
    /// </summary>
    public IReadOnlyList<GridRegion>? Regions { get; init; }

    /// <summary>
    /// Whether a w x h footprint at (x, y) lies inside one of the <see cref="Regions"/> (always without them).
    /// </summary>
    public bool InRegion(int x, int y, int w, int h) => Regions is not { } regions || regions.Any(r => r.Contains(x, y, w, h));

    /// <summary>
    /// Whether a w x h footprint at (x, y) of <paramref name="section"/> overlaps no item and lies inside one of the <see cref="Regions"/> (other
    /// bounds are the caller's to check).
    /// </summary>
    public bool IsFree(string section, int x, int y, int w, int h)
    {
        if (!InRegion(x, y, w, h)) return false;
        foreach (var i in Items)
        {
            if (i.Section != section) continue;
            if (x < i.X + i.Width && i.X < x + w && y < i.Y + i.Height && i.Y < y + h) return false;
        }
        return true;
    }

    /// <summary>
    /// A backpack's contents: the CONTAINER's <c>stackable bonus minimum</c> and <c>stackable bonus mult</c> (1 and 1 for a character's own grid),
    /// see <see cref="LimitOf"/>.
    /// </summary>
    public int StackBonusMinimum { get; init; } = 1;
    /// <summary>
    /// A backpack's <c>stackable bonus mult</c> (1 for a character's own grid).
    /// </summary>
    public float StackBonusMult { get; init; } = 1;
    /// <summary>
    /// A storage limited to listed items (a resource store's <c>storage limit</c>): its stacks start from 1 instead of the record's <c>stackable</c>
    /// (<see cref="LimitOf"/>).
    /// </summary>
    public bool Limited { get; init; }

    /// <summary>
    /// How many units of <paramref name="item"/> one cell of this inventory holds (<c>FUN_1407466b0</c>, <b>Observed</b>): its
    /// <see cref="ItemInstance.StackLimit"/> (1 in a <see cref="Limited"/> storage), with this inventory's bonus applied
    /// (<see cref="ItemInfo.StackLimitIn"/>; a bag with mult 5 holds five of an item that does not stack elsewhere, <b>Observed</b>); 1 for the types
    /// that never stack (<see cref="InventoryCommands.NeverStacks"/>).
    /// </summary>
    public int LimitOf(ItemInstance item)
    {
        if (InventoryCommands.NeverStacks(item.Type)) return 1;
        int start = Limited ? 1 : item.StackLimit;
        if (StackBonusMinimum == 1 && StackBonusMult == 1) return Math.Max(start, 1);
        return ItemInfo.StackLimitIn(start, StackBonusMinimum, StackBonusMult);
    }

    /// <summary>
    /// Puts an item in, honouring the stack limit of each inventory (<see cref="LimitOf"/>): first it tops up stacks of the same record and quality
    /// in the main grid, then in the backpack; what is left takes the first free cells of <c>main</c>, then of the backpack, at most a full stack per
    /// cell (the item itself goes into the last cell, split-off copies into the others). True when every unit found a place; otherwise what did not
    /// fit stays in <paramref name="item"/>'s <see cref="ItemInstance.Quantity"/> (the caller keeps or drops it) and the units placed stay placed.
    /// Units with a buyback receipt top up only stacks of the same trade and direction; with no free cell left they join plain stacks too and lose
    /// the receipt (docs/simulation.md, trading).
    /// </summary>
    public bool Add(ItemInstance item)
    {
        var bag = Backpack?.Contents;
        if (TopUp(this, item, false) || (bag is not null && TopUp(bag, item, false))) return true;
        if (Place(this, "main", item)) return true;
        if (bag is not null && Place(bag, "backpack_content", item)) return true;
        return item.Buyback is not null && (TopUp(this, item, true) || (bag is not null && TopUp(bag, item, true)));
    }

    /// <summary>
    /// Whether one unit of <paramref name="item"/> would find a place in <see cref="Add"/> (a dry run, nothing changes): a stack of it in the main
    /// grid or the backpack with room left, or a free cell in either. The state <c>gotInventoryRoomForResources</c> (82, <c>FUN_14059bd80</c>: the
    /// inventory, else the worn backpack's).
    /// </summary>
    public bool HasRoomFor(ItemInstance item) => Room(this, "main", item) || Backpack?.Contents is { } bag && Room(bag, "backpack_content", item);

    /// <summary>
    /// Whether <paramref name="inv"/>'s <paramref name="section"/> takes one unit of <paramref name="item"/>: a stack of it with room left, or a
    /// free cell.
    /// </summary>
    static bool Room(Inventory inv, string section, ItemInstance item)
    {
        int limit = inv.LimitOf(item);
        if (limit > 1)
            foreach (var other in inv.Items)
                if (other.Section == section && SameStack(other, item) && other.Contents is null && other.Quantity < limit)
                    return true;
        return inv.FindCell(section, item.Width, item.Height, out _, out _);
    }

    /// <summary>
    /// Whether two items may share a stack: same record, material and quality (as <see cref="InventoryCommands.Stackable"/>, <c>FUN_140715560</c>),
    /// stolen mark, death and unique flags. The material keeps blueprints of different researches apart: they share their look record (#354).
    /// </summary>
    static bool SameStack(ItemInstance a, ItemInstance b) => a.Record == b.Record && a.MaterialId == b.MaterialId && a.Quality == b.Quality
            && a.Mark == b.Mark
        && a.Death == b.Death && a.Unique == b.Unique;

    /// <summary>
    /// The original's auto-add into one section (<c>FUN_14074c8b0</c>): tops up the stacks of <paramref name="section"/> first, then takes the first
    /// free cells row by row from the top, left to right, a full stack per cell. True when every unit found a place; what did not fit stays in
    /// <paramref name="item"/>.
    /// </summary>
    public bool AddTo(string section, ItemInstance item) => TopUp(this, item, false, section) || Place(this, section, item);

    /// <summary>
    /// Moves units of <paramref name="item"/> into stacks of <paramref name="into"/> with room left (of <paramref name="section"/>, else of its
    /// grids; <paramref name="dropReceipt"/>: plain stacks too); true when none are left.
    /// </summary>
    static bool TopUp(Inventory into, ItemInstance item, bool dropReceipt, string? section = null)
    {
        int limit = into.LimitOf(item);
        if (limit <= 1) return false;
        foreach (var other in into.Items)
        {
            if (ReferenceEquals(other, item) || !SameStack(other, item) || other.Contents is not null
                || (section is null ? other.Section is not ("main" or "backpack_content") : other.Section != section)) continue;
            var (kept, coming) = (other.Buyback, item.Buyback);
            if (kept is null ? coming is not null && !dropReceipt : coming is null || !kept.Joins(coming)) continue;
            int move = Math.Min(limit - other.Quantity, item.Quantity);
            if (move <= 0) continue;
            other.Quantity += move;   // clears other's receipt; the joined one is set after
            if (kept is not null) other.Buyback = kept.Append(coming!.Take(move));
            item.Quantity -= move;
            if (item.Quantity == 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Puts the units of <paramref name="item"/> into free cells of <paramref name="into"/>, a full stack per cell; true when none are left.
    /// </summary>
    static bool Place(Inventory into, string section, ItemInstance item)
    {
        int limit = into.LimitOf(item);
        while (into.FindCell(section, item.Width, item.Height, out int x, out int y))
        {
            var unit = item.Quantity <= limit ? item : item.WithQuantity(limit);
            unit.Section = section;
            unit.X = x;
            unit.Y = y;
            into.Items.Add(unit);
            if (ReferenceEquals(unit, item)) return true;
            item.Quantity -= limit;
        }
        return false;
    }

    /// <summary>
    /// Every item of this inventory and of the backpack, in order.
    /// </summary>
    public IEnumerable<ItemInstance> All()
    {
        foreach (var i in Items)
        {
            yield return i;
            if (i.Contents is { } bag)
                foreach (var inner in bag.All()) yield return inner;
        }
    }

    /// <summary>
    /// The items the windows list, each with its index in <see cref="All"/> (the number the drag tokens carry): this inventory's own and the worn
    /// backpack's contents. The contents of a container carried in the grid are not listed (their cells would draw over the worn bag's in its window;
    /// the original opens only the worn backpack's grid).
    /// </summary>
    public IEnumerable<(int Index, ItemInstance Item)> Shown()
    {
        int index = 0;
        var worn = Backpack;
        foreach (var i in Items)
        {
            yield return (index++, i);
            if (i.Contents is not { } bag) continue;
            foreach (var inner in bag.All())
            {
                if (ReferenceEquals(i, worn)) yield return (index, inner);
                index++;
            }
        }
    }

    /// <summary>
    /// Removes one unit of an item (the whole item when it is the last), wherever it is.
    /// </summary>
    public bool RemoveOne(ItemInstance item)
    {
        if (Items.Contains(item))
        {
            if (item.Quantity > 1) item.Quantity--;
            else Items.Remove(item);
            return true;
        }
        foreach (var i in Items)
            if (i.Contents is { } bag && bag.RemoveOne(item)) return true;
        return false;
    }

    /// <summary>
    /// Removes an item whole (every unit of its stack), wherever it is (this inventory or the backpack).
    /// </summary>
    public bool Remove(ItemInstance item)
    {
        if (Items.Remove(item)) return true;
        foreach (var i in Items)
            if (i.Contents is { } bag && bag.Remove(item)) return true;
        return false;
    }

    /// <summary>
    /// Adds the items (record, place, quantity, level, quality, charges, the unique flag, buy-back coins, a bag's contents) to the world hash.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(Items.Count);
        foreach (var i in Items)
        {
            h.Add(Rng.StableHash(i.Record));
            h.Add(Rng.StableHash(i.Section));
            h.Add(i.X);
            h.Add(i.Y);
            h.Add(i.Quantity);
            h.Add(i.Level);
            h.Add(i.Quality);
            h.Add(i.Charges);
            if (i.Unique) h.Add(Rng.StableHash("Unique"));
            if (i.Buyback is { } receipt)
            {
                h.Add(Rng.StableHash("Buyback"));
                h.Add(receipt.Sell);
                h.Add(receipt.Coins.Length);
                foreach (long coins in receipt.Coins) h.Add(coins);
            }
            i.Contents?.Hash(ref h);
        }
    }
}

/// <summary>
/// A rectangle of an inventory's cells (<see cref="Inventory.Regions"/>).
/// </summary>
public readonly record struct GridRegion(int X, int Y, int Width, int Height)
{
    /// <summary>
    /// Whether the <paramref name="w"/> x <paramref name="h"/> cells at (<paramref name="x"/>, <paramref name="y"/>) lie inside the region.
    /// </summary>
    public bool Contains(int x, int y, int w, int h) => x >= X && y >= Y && x + w <= X + Width && y + h <= Y + Height;

    /// <summary>
    /// Whether <paramref name="item"/>'s top-left cell lies inside.
    /// </summary>
    public bool Holds(ItemInstance item) => item.X >= X && item.X < X + Width && item.Y >= Y && item.Y < Y + Height;
}