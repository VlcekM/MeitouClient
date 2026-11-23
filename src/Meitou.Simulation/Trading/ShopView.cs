using Meitou.Simulation.Items;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Trading;

/// <summary>
/// A shop's trader window (docs/game/economy.md "Building shops", <b>Observed</b> decompile): its goods stay in the stock containers (a squad
/// shop's in its <see cref="Shop.Stock"/>) and the window (<see cref="Shop.Window"/>) shows copies of them. Each time the window opens (<c>FUN_1406e6af0</c> makes the shop wrapper <c>FUN_140953850</c> anew) the grid
/// takes a copy of every item of the containers but money (item function 14) by auto-add (<c>FUN_1409528d0</c>; an item with no room grows it by
/// twice its footprint height, <c>FUN_140954ba0</c>) and is arranged (<c>FUN_140749d30</c>). What the trade changes in the grid goes back to the
/// containers (the section callbacks <c>FUN_140952c70</c>, <c>FUN_140952cf0</c>, <c>FUN_140952da0</c>): units taken out leave the containers'
/// matching items (<c>FUN_140952a70</c>), units put in go to the first container that takes them (<c>FUN_1409529e0</c>), and what none takes is
/// destroyed (<c>FUN_140799af0</c>) while its copy stays in the grid. <b>Engine choice</b>: the write-back compares the grid with what it last
/// mirrored (<see cref="Write"/>) where the original reacts to each change; the units that left are taken from the containers' items alike to
/// them (<see cref="Alike"/>), as the original takes them.
/// </summary>
sealed class ShopView
{
    /// <summary>
    /// What the grid held when it last matched the containers, by kind of stack (each sample a copy).
    /// </summary>
    List<Kind> _mirrored = [];

    /// <summary>
    /// Whether the grid holds copies the containers follow (built, or a save's goods not yet handed to the containers).
    /// </summary>
    public bool Live { get; private set; }
    /// <summary>
    /// Whether the grid holds an older save's goods that no container has yet (<see cref="Adopt"/>): they wait for all the containers.
    /// </summary>
    public bool Pending { get; private set; }

    /// <summary>
    /// Builds the trader window from what <paramref name="boxes"/> hold.
    /// </summary>
    public void Build(World world, Shop shop, IReadOnlyList<StockBox> boxes)
    {
        var (columns, rows) = shop.ViewSize;
        shop.Window = new Inventory(columns, rows) { StackBonusMult = Shop.StockStackMult };

        foreach (var box in boxes)
        {
            foreach (var item in box.Grid.Items)
                if (item.ItemFunction != PlayerSystem.MoneyFunction) Put(shop, item.Copy());
        }

        InventoryCommands.Arrange(world, shop.Window, ItemOwner.Stock, InventoryArrangeReason.StockShown);
        _mirrored = Frozen(Tally(shop.Window.Items));
        Live = true;
        Pending = false;
    }

    /// <summary>
    /// The grid holds goods the containers do not (an older save's stock, read into it): the next <see cref="Write"/> hands them all over.
    /// </summary>
    public void Adopt()
    {
        _mirrored = [];
        Live = true;
        Pending = true;
    }

    /// <summary>
    /// Writes what changed in the grid since it last matched into <paramref name="boxes"/>: units gone leave them, new units go in.
    /// </summary>
    public void Write(Shop shop, IReadOnlyList<StockBox> boxes)
    {
        if (!Live) return;

        var now = Tally(shop.Window.Items);
        bool changed = false;

        foreach (var was in _mirrored)
        {
            int left = UnitsOf(now, was);
            if (left >= was.Units) continue;

            Remove(boxes, was, was.Units - left);
            changed = true;
        }

        foreach (var kind in now)
        {
            int was = UnitsOf(_mirrored, kind);
            if (kind.Units == was) continue;

            if (kind.Units > was) Add(boxes, kind, kind.Units - was);
            changed = true;
        }

        if (changed) _mirrored = Frozen(now);
        Pending = false;
    }

    /// <summary>
    /// Empties the window back to its first size (its goods are the containers').
    /// </summary>
    public void Drop(Shop shop)
    {
        var (columns, rows) = shop.ViewSize;
        shop.Window = new Inventory(columns, rows) { StackBonusMult = Shop.StockStackMult };
        _mirrored = [];
        Live = false;
        Pending = false;
    }

    /// <summary>
    /// Auto-adds <paramref name="item"/> to the grid, which grows by twice the item's height while it has no room; an item wider than the grid stays
    /// out of it.
    /// </summary>
    static void Put(Shop shop, ItemInstance item)
    {
        if (item.Width > shop.Window.Width) return;

        while (!shop.Window.AddTo("main", item)) shop.Window = Taller(shop.Window, 2 * item.Height);
    }

    /// <summary>
    /// <paramref name="grid"/> with <paramref name="rows"/> more rows, holding its items.
    /// </summary>
    static Inventory Taller(Inventory grid, int rows)
    {
        var taller = new Inventory(grid.Width, grid.Height + rows) { StackBonusMult = grid.StackBonusMult };
        taller.Items.AddRange(grid.Items);
        return taller;
    }

    /// <summary>
    /// The units of <paramref name="items"/> by kind of stack (<see cref="Kind.Is"/>), each sample one of the items.
    /// </summary>
    static List<Kind> Tally(IEnumerable<ItemInstance> items)
    {
        var tally = new List<Kind>();

        foreach (var item in items)
        {
            bool bag = item.Contents is not null;
            int k = tally.FindIndex(t => t.Is(item, bag));

            if (k < 0) tally.Add(new Kind(item, bag, item.Quantity));
            else tally[k] = tally[k] with { Units = tally[k].Units + item.Quantity };
        }

        return tally;
    }

    /// <summary>
    /// <paramref name="tally"/> with each sample copied, so later changes to the grid's items leave it alone.
    /// </summary>
    static List<Kind> Frozen(List<Kind> tally) => [.. tally.Select(k => k with { Sample = k.Sample.WithQuantity(k.Units) })];

    /// <summary>
    /// The units of <paramref name="kind"/>'s kind in <paramref name="tally"/>.
    /// </summary>
    static int UnitsOf(List<Kind> tally, Kind kind)
    {
        foreach (var k in tally)
            if (k.Is(kind.Sample, kind.Bag)) return k.Units;

        return 0;
    }

    /// <summary>
    /// Takes <paramref name="units"/> of <paramref name="kind"/> out of the containers, the first ones first, from any item alike to its sample
    /// (<see cref="Alike"/>), as the original's removal callback does (<c>FUN_140952a70</c>).
    /// </summary>
    static void Remove(IReadOnlyList<StockBox> boxes, Kind kind, int units)
    {
        foreach (var box in boxes)
        {
            foreach (var item in box.Grid.Items.Where(i => Alike(i, kind.Sample)).ToList())
            {
                int take = Math.Min(units, item.Quantity);

                if (take == item.Quantity) box.Grid.Items.Remove(item);
                else item.Quantity -= take;

                units -= take;
                if (units == 0) return;
            }
        }
    }

    /// <summary>
    /// Whether the original's item likeness (<c>FUN_14075d510</c>, <b>Verified</b> by decompile) holds: the same record, company (<c>Item+0xc0</c>,
    /// the save's <c>company sid</c>) and material (<c>+0xc8</c>, <c>material sid</c>); quality, condition and marks are not compared.
    /// </summary>
    static bool Alike(ItemInstance a, ItemInstance b) => a.Record == b.Record && a.CompanyId == b.CompanyId && a.MaterialId == b.MaterialId;

    /// <summary>
    /// Puts <paramref name="units"/> of <paramref name="kind"/> (a sample of the grid's own items) into the first containers that take them, a bag
    /// one by one with a copy of what it holds; what none takes is lost.
    /// </summary>
    static void Add(IReadOnlyList<StockBox> boxes, Kind kind, int units)
    {
        for (int k = 0; k < (kind.Bag ? units : 1); k++)
        {
            var copy = kind.Bag ? kind.Sample.Copy() : kind.Sample.WithQuantity(units);
            copy.Buyback = null;

            foreach (var box in boxes)
                if (box.Take(copy)) break;
        }
    }

    /// <summary>
    /// A kind of stack in the grid and its units: items that would share a stack (<see cref="Inventory.SameStack"/>), bags apart from the rest.
    /// </summary>
    readonly record struct Kind(ItemInstance Sample, bool Bag, int Units)
    {
        /// <summary>
        /// Whether <paramref name="item"/> (<paramref name="bag"/>: a bag) is of this kind.
        /// </summary>
        public bool Is(ItemInstance item, bool bag) => bag == Bag && Inventory.SameStack(Sample, item);
    }
}
