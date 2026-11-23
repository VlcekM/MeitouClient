namespace Meitou.Simulation.Items;

/// <summary>
/// The world's systems that replace inventory arranges (<see cref="IInventoryArrangeFilter"/>).
/// </summary>
sealed class InventoryArrangeFilters
{
    /// <summary>
    /// The filtering systems, each with its index and whether it is still asked.
    /// </summary>
    List<(IInventoryArrangeFilter Filter, int SystemIndex, bool Enabled)>? _filters;
    /// <summary>
    /// The filters still asked, and the ones disabled after a failure.
    /// </summary>
    int _active, _faulted;

    /// <summary>
    /// Whether any filter is asked; an arrange builds no ask without one.
    /// </summary>
    internal bool Active => _active > 0;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not IInventoryArrangeFilter filter) return;

        (_filters ??= []).Add((filter, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Adds the disabled filters to the world's state hash. Working filters add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(0x49415246);
        hash.Add(_faulted);
        foreach (var entry in _filters!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// Asks the enabled filters in system order until one arranges; true when one did (its arrangement applied), false when all left it to the game.
    /// </summary>
    internal bool Arrange(World world, InventoryArrangeAsk ask)
    {
        var filters = _filters!;
        for (int i = 0; i < filters.Count; i++)
        {
            if (filters[i].Enabled && Ask(world, i, ask)) return true;
        }

        return false;
    }

    /// <summary>
    /// One filter's arrangement applied (true), or the grids as they came (false) when it answers null, throws or answers an invalid one (which
    /// disables it).
    /// </summary>
    bool Ask(World world, int i, InventoryArrangeAsk ask)
    {
        var snapshot = new ArrangeSnapshot(ask.Grids);
        string? fault;
        try
        {
            fault = Answer(world, i, ask);
        }
        catch (Exception e)
        {
            fault = $"{e.GetType().Name}: {e.Message}";
            SimulationExtensions.TellOutdated(_filters![i].Filter, e);
        }

        if (fault is null) return true;

        snapshot.Restore();
        if (fault.Length > 0) Disable(i, fault);
        return false;
    }

    /// <summary>
    /// Asks filter <paramref name="i"/>, merging first when it says so, and applies a valid arrangement: null when applied, "" when it answered null,
    /// else the fault.
    /// </summary>
    string? Answer(World world, int i, InventoryArrangeAsk ask)
    {
        var filter = _filters![i].Filter;
        var items = ArrangePlacements.Items(ask.Grids);
        var answer = filter.Arrange(world, ask);
        if (answer is { Merge: true })
        {
            MergeStacks(ask.Grids);
            items = ArrangePlacements.Items(ask.Grids);
            answer = filter.Arrange(world, ask with { Merged = true });
            if (answer is { Merge: true }) return "asked to merge the stacks twice";
        }

        if (answer is null) return "";
        if (ArrangePlacements.Invalid(ask.Grids, items, answer.Placements) is { } invalid) return invalid;

        ArrangePlacements.Apply(ask.Grids, items, answer.Placements);
        return null;
    }

    /// <summary>
    /// The game's stack join over each grid: its items in list order, each topping up the stacks before it (<see cref="Inventory.AddTo"/>'s
    /// top-up); an item with units left keeps its cell.
    /// </summary>
    static void MergeStacks(IReadOnlyList<ArrangeGrid> grids)
    {
        foreach (var (grid, section) in grids)
        {
            var items = grid.Items.Where(i => i.Section == section).ToArray();
            grid.Items.RemoveAll(i => i.Section == section);
            foreach (var item in items)
            {
                if (!grid.TopUpIn(section, item)) grid.Items.Add(item);
            }
        }
    }

    /// <summary>
    /// Disables filter <paramref name="i"/> for this world and logs why.
    /// </summary>
    void Disable(int i, string fault)
    {
        var entry = _filters![i];
        _filters[i] = (entry.Filter, entry.SystemIndex, false);
        _active--;
        _faulted++;
        LogFault(entry.Filter, fault);
    }

    /// <summary>
    /// A disabled filter's log line: the mod and the name it registered the system under, as the mods' other log lines; else its type.
    /// </summary>
    static void LogFault(IInventoryArrangeFilter filter, string fault)
    {
        if (SimulationExtensions.FactoryOf(filter) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: arrange filter '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"arrange filter '{filter.GetType().FullName}' disabled for this world: {fault}");
    }
}

/// <summary>
/// The arranged grids as they were before a filter was asked: their item lists and each item's place, units and charges, to put back.
/// </summary>
sealed class ArrangeSnapshot
{
    readonly (Inventory Grid, ItemInstance[] Items)[] _lists;
    readonly (ItemInstance Item, int X, int Y, bool Rotated, int Quantity, float Charges)[] _values;

    public ArrangeSnapshot(IReadOnlyList<ArrangeGrid> grids)
    {
        _lists = [.. grids.Select(g => g.Grid).Distinct().Select(g => (g, g.Items.ToArray()))];
        _values = [.. _lists.SelectMany(l => l.Items).Select(i => (i, i.X, i.Y, i.Rotated, i.Quantity, i.Charges))];
    }

    /// <summary>
    /// Puts the lists and the items' values back.
    /// </summary>
    public void Restore()
    {
        foreach (var (grid, items) in _lists)
        {
            grid.Items.Clear();
            grid.Items.AddRange(items);
        }

        foreach (var v in _values) (v.Item.X, v.Item.Y, v.Item.Rotated, v.Item.Quantity, v.Item.Charges) = (v.X, v.Y, v.Rotated, v.Quantity, v.Charges);
    }
}

/// <summary>
/// Checks and applies a filter's placements (<see cref="InventoryArrangement.Placements"/>).
/// </summary>
static class ArrangePlacements
{
    /// <summary>
    /// Each item of the grids with the index of its grid.
    /// </summary>
    public static Dictionary<ItemInstance, int> Items(IReadOnlyList<ArrangeGrid> grids)
    {
        var items = new Dictionary<ItemInstance, int>(ReferenceEqualityComparer.Instance);
        for (int g = 0; g < grids.Count; g++)
        {
            foreach (var item in grids[g].Items) items[item] = g;
        }

        return items;
    }

    /// <summary>
    /// Why the placements are not an arrangement of the grids' items, or null: an item missing, foreign or placed twice, outside its grid, or
    /// overlapping another.
    /// </summary>
    public static string? Invalid(IReadOnlyList<ArrangeGrid> grids, Dictionary<ItemInstance, int> items, IReadOnlyList<ItemPlacement> placements)
    {
        if (placements.Count != items.Count) return $"{placements.Count} placements for {items.Count} items";

        var cells = grids.Select(g => new bool[g.Grid.Width * g.Grid.Height]).ToArray();
        var seen = new HashSet<ItemInstance>(ReferenceEqualityComparer.Instance);
        foreach (var p in placements)
        {
            if (p.Item is null || !items.TryGetValue(p.Item, out int g)) return "a placement of an item not in the grids";
            if (!seen.Add(p.Item)) return $"item '{p.Item.Record}' placed twice";
            if (!Occupy(grids[g].Grid, cells[g], p)) return $"item '{p.Item.Record}' at ({p.X}, {p.Y}) is outside its grid or overlaps another";
        }

        return null;
    }

    /// <summary>
    /// Marks the placement's cells taken; false when it leaves the grid or meets a taken cell.
    /// </summary>
    static bool Occupy(Inventory grid, bool[] cells, ItemPlacement p)
    {
        var (w, h) = Footprint(p);
        if (w <= 0 || h <= 0 || p.X < 0 || p.Y < 0 || p.X > grid.Width - w || p.Y > grid.Height - h) return false;

        for (int y = p.Y; y < p.Y + h; y++)
        {
            for (int x = p.X; x < p.X + w; x++)
            {
                ref bool cell = ref cells[y * grid.Width + x];
                if (cell) return false;

                cell = true;
            }
        }

        return true;
    }

    /// <summary>
    /// The item's footprint as the placement lays it.
    /// </summary>
    static (int W, int H) Footprint(ItemPlacement p)
    {
        var item = p.Item;
        bool turned = (p.Rotated ?? item.Rotated) != item.Rotated;
        return turned ? (item.Height, item.Width) : (item.Width, item.Height);
    }

    /// <summary>
    /// Moves every item to its placement and lists each grid's items in placement order, after the grid's other sections' items.
    /// </summary>
    public static void Apply(IReadOnlyList<ArrangeGrid> grids, Dictionary<ItemInstance, int> items, IReadOnlyList<ItemPlacement> placements)
    {
        foreach (var (grid, section) in grids) grid.Items.RemoveAll(i => i.Section == section);

        foreach (var p in placements)
        {
            var item = p.Item;
            item.Rotated = p.Rotated ?? item.Rotated;
            (item.X, item.Y) = (p.X, p.Y);
            grids[items[item]].Grid.Items.Add(item);
        }
    }
}
