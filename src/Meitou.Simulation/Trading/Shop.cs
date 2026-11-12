using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Economy;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Trading;

/// <summary>
/// A trader squad's shop: the stock it sells, its trade terms and when it restocks (docs/game/economy.md section 9). The purse is the squad's
/// <see cref="Squad.Money"/>, the game's <c>Platoon+0x88</c>. <b>Engine choice</b>: the stock is one inventory of <see cref="Width"/> x
/// <see cref="Height"/> cells held by the squad, the size of the building shops' storage (<c>storage size</c> 18 x 18 in <c>gamedata.base</c>, the
/// width of the vanilla trader window): the trader window shows its trader's <c>backpack_content</c> section (<c>FUN_140150570</c>,
/// docs/game/ui-screens.md section 6). In the original a squad shop's stock is offered to "the inventory sections" (not decoded) and a building
/// shop's lies in the shop building's containers (here a rectangle of the stock per container, <see cref="ForBuilding"/>).
/// </summary>
public sealed class Shop
{
    /// <summary>
    /// The stock grid's size in cells (<see cref="Width"/> x <see cref="Height"/>).
    /// </summary>
    public const int Width = 18, Height = 18;
    /// <summary>
    /// The stock section's stack bonus (<c>ShopTraderInventory::vfunc_1</c> sets the section's minimum 1 and mult 50, <b>Observed</b> decompile): a
    /// cell holds 50 x the item's <c>stackable</c>.
    /// </summary>
    public const float StockStackMult = 50;

    /// <summary>
    /// The stock: an 18 x 18 grid whose cells hold <see cref="StockStackMult"/> x each item's <c>stackable</c>.
    /// </summary>
    public Inventory Stock { get; init; } = new(Width, Height) { StackBonusMult = StockStackMult };
    /// <summary>
    /// The trader's terms the prices read.
    /// </summary>
    public required Trader Terms { get; init; }
    /// <summary>
    /// A building shop's building (<see cref="Meitou.Data.World.TownBuilding.Key"/>): its stock is the merged storage of the building's containers;
    /// null for a squad shop.
    /// </summary>
    public string? Building { get; init; }
    /// <summary>
    /// The world tick at which the stock is next replaced (<c>Platoon+0x1f8</c>, saved as <c>inventory refresh time</c> in game hours).
    /// </summary>
    public long NextRestockTick { get; set; }
    /// <summary>
    /// How many times the stock was filled: the key of the next fill's rolls.
    /// </summary>
    public int Fills { get; set; }

    /// <summary>
    /// The cells outside every container (<see cref="UnusedCells"/>), made on first read.
    /// </summary>
    List<GridRegion>? _unused;

    /// <summary>
    /// The cells of <see cref="Stock"/> outside every container (<see cref="Unused"/>, made once: the layout never changes).
    /// </summary>
    public IReadOnlyList<GridRegion> UnusedCells => _unused ??= Unused(Stock);

    /// <summary>
    /// The stock containers of a building shop (fixture ids), in the order of <see cref="Inventory.Regions"/> of <see cref="Stock"/>; empty for a
    /// squad shop.
    /// </summary>
    public IReadOnlyList<string> Containers { get; init; } = [];

    /// <summary>
    /// A building shop of <paramref name="building"/> (the shop wrapper <c>FUN_140953850</c>, <b>Observed</b>, merges its stock containers into one
    /// section): null when the building has no stock container. <b>Engine choice</b>: the stock is one inventory as wide as the widest container (at
    /// least 18) in which each container's grid is a rectangle of its own (<see cref="Inventory.Regions"/>), so each container holds its own items
    /// and the trade window shows them all (the cells outside every container greyed, <see cref="Unused"/>). The rectangles are packed
    /// (<see cref="Pack"/>) in a few orders (by height, area, width, longer side, largest first; ties in the building's order) and the lowest packing
    /// is kept. The original's merged section is ceil(cells / width) high; this is up to ~5 % more when the containers do not tile (Squin's shops: 3
    /// to 13 rows, and 300 random orders found none lower).
    /// </summary>
    public static Shop? ForBuilding(Meitou.Data.World.TownBuilding building, Trader terms)
    {
        var boxes = building.Fixtures.Where(f => f.HoldsShopStock).ToList();
        if (boxes.Count == 0) return null;

        int width = Math.Max(Width, boxes.Max(f => f.StorageWidth));
        Func<Meitou.Data.World.TownFixture, int>[] orders =
            [f => f.StorageHeight, f => f.StorageWidth * f.StorageHeight, f => f.StorageWidth, f => Math.Max(f.StorageWidth, f.StorageHeight)];
        (List<Meitou.Data.World.TownFixture> Boxes, List<GridRegion> Regions, int Height)? best = null;

        foreach (var order in orders)
        {
            var sorted = boxes.OrderByDescending(order).ToList();
            var (regions, height) = Pack(sorted, width);
            if (best is null || height < best.Value.Height) best = (sorted, regions, height);
        }

        var (placed, layout, rows) = best!.Value;
        return new Shop
        {
            Terms = terms,
            Building = building.Key,
            Containers = [.. placed.Select(f => f.Id)],
            Stock = new Inventory(width, rows) { StackBonusMult = StockStackMult, Regions = layout },
        };
    }

    /// <summary>
    /// Bottom-left maximal-rectangles packing: the free space is kept as every largest free rectangle (overlapping), each box goes where its bottom
    /// is highest up (lowest y + height), then leftmost, so it also fills holes left beside and under taller boxes.
    /// </summary>
    static (List<GridRegion> Regions, int Height) Pack(List<Meitou.Data.World.TownFixture> boxes, int width)
    {
        var free = new List<GridRegion> { new(0, 0, width, boxes.Sum(f => f.StorageHeight)) };
        var regions = new List<GridRegion>(boxes.Count);
        int height = 0;

        foreach (var f in boxes)
        {
            GridRegion? at = null;

            foreach (var r in free)
            {
                if (f.StorageWidth <= r.Width && f.StorageHeight <= r.Height
                    && (at is not { } a || r.Y + f.StorageHeight < a.Y + a.Height || r.Y + f.StorageHeight == a.Y + a.Height && r.X < a.X))
                {
                    at = new GridRegion(r.X, r.Y, f.StorageWidth, f.StorageHeight);
                }
            }

            var p = at!.Value;   // the free space is as tall as all the boxes stacked, so every box fits
            regions.Add(p);
            height = Math.Max(height, p.Y + p.Height);
            var split = new List<GridRegion>();

            foreach (var r in free)
            {
                if (p.X >= r.X + r.Width || p.X + p.Width <= r.X || p.Y >= r.Y + r.Height || p.Y + p.Height <= r.Y)
                {
                    split.Add(r);
                    continue;
                }

                if (p.X > r.X) split.Add(r with { Width = p.X - r.X });
                if (p.X + p.Width < r.X + r.Width)
                    split.Add(r with { X = p.X + p.Width, Width = r.X + r.Width - p.X - p.Width });
                if (p.Y > r.Y) split.Add(r with { Height = p.Y - r.Y });
                if (p.Y + p.Height < r.Y + r.Height)
                    split.Add(r with { Y = p.Y + p.Height, Height = r.Y + r.Height - p.Y - p.Height });
            }

            free = [.. split.Where((r, i) => !split.Where((o, j) => j != i && o.Contains(r.X, r.Y, r.Width, r.Height) && (o != r || j < i)).Any())];
        }

        return (regions, height);
    }

    /// <summary>
    /// The cells of <paramref name="grid"/> outside its <see cref="Inventory.Regions"/> (none without them), as rectangles: runs of a row, joined
    /// down while they repeat.
    /// </summary>
    public static List<GridRegion> Unused(Inventory grid)
    {
        var result = new List<GridRegion>();
        if (grid.Regions is null) return result;

        var open = new Dictionary<(int X, int Width), int>();   // a run of the previous row -> the index of its rectangle

        for (int y = 0; y < grid.Height; y++)
        {
            var next = new Dictionary<(int X, int Width), int>();

            for (int x = 0; x < grid.Width;)
            {
                if (grid.InRegion(x, y, 1, 1))
                {
                    x++;
                    continue;
                }

                int start = x;
                while (x < grid.Width && !grid.InRegion(x, y, 1, 1)) x++;

                var run = (start, x - start);

                if (open.TryGetValue(run, out int k)) result[k] = result[k] with { Height = result[k].Height + 1 };
                else
                {
                    k = result.Count;
                    result.Add(new GridRegion(start, y, x - start, 1));
                }

                next[run] = k;
            }

            open = next;
        }

        return result;
    }

    /// <summary>
    /// The rectangle of <see cref="Stock"/> that is container <paramref name="fixture"/>'s grid; false when it is not one of this shop's stock
    /// containers.
    /// </summary>
    public bool RegionOf(string fixture, out GridRegion region)
    {
        for (int k = 0; k < Containers.Count; k++)
        {
            if (Containers[k] == fixture)
            {
                region = Stock.Regions![k];
                return true;
            }
        }

        region = default;
        return false;
    }

    /// <summary>
    /// Adds the next restock tick, the fill count and the stock to the world hash.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(NextRestockTick);
        h.Add(Fills);
        Stock.Hash(ref h);
    }
}

/// <summary>
/// Fills a shop as the squad filler <c>FUN_14095ab10</c> does (docs/game/economy.md 9.2, <b>Observed</b>; the constants <b>Verified</b>):
/// <list type="number">
/// <item>n = round(U(0.8, 1.2) x <c>vendors fill total amount</c> x wealth), half away from zero; wealth is 1 (the game's faction wealth function is
/// a stub returning 1).</item>
/// <item>The vendor lists share n by their weights (v0; 0 counts as 100, engine reading), round(n x w / sum), the last taking what the rounding
/// left.</item>
/// <item>Each unit is a weighted pick from its list (<see cref="VendorList"/>), skipping the <c>forbidden items</c> of the trader's town and faction
/// cultures. Armour gets a grade from the faction's armour chances and the grade's quality (5 / 20 / 40 / 60 / 80 / 95); a weapon a manufacturer from
/// the list and a model from the manufacturer (how the original rolls a shop weapon's quality is <b>Unknown</b>; this is the character generator's
/// rule); a robotic limb a level from <see cref="LimbGrade"/>, a crossbow from <see cref="CrossbowGrade"/> (no manufacturer).</item>
/// <item>The units are merged into stacks up to their stack limit, sorted by unit price (dearest first: the sort key is the price, its direction
/// <b>Unknown</b>) and put into the stock; what does not fit is destroyed.</item>
/// <item>The purse is trunc(clamp(0.25 x the stock's value at full price, 3000, 25000) x wealth); the template's <c>vendor money</c> is a building
/// shop's and is not read.</item>
/// <item>A building shop (<see cref="Shop.Building"/>, the filler <c>FUN_14095b210</c>) makes at least one unit and its purse is <c>vendor money</c>
/// +/- 0.4 x it, or the rule above when that is 0 (its money rolls are one stack of the money item in the stock in the original; the purse here, as
/// for a squad).</item>
/// <item>Each list's money roll (<c>money item prob</c> against U(0, 1), so 1 or more always pays) adds U[min, max] cats. The game adds them as a
/// stack of the money item; here they go straight to the purse (<b>engine choice</b>: a shop selling its own cats would be a no-op trade).</item>
/// </list>
/// </summary>
public static class ShopFiller
{
    /// <summary>
    /// The armour grade chances when a faction has none (the editor's defaults, economy.md 9.2).
    /// </summary>
    static readonly int[] DefaultGradeChances = [50, 100, 50, 2, 0, 0];

    /// <summary>
    /// The vendor lists a squad's stock rolls from (<c>FUN_140958560</c>, <b>Verified</b>): the template's <c>vendors</c>; when it has none, by its
    /// building designation the owner FACTION's 2 BARRACKS / 5 ARMOURY <c>item spawns armoury</c>, 3 BAR <c>item spawns bar</c>, 6 TREASURE <c>item
    /// spawns treasure</c>, 8 HQ <c>item spawns HQ</c>, 9 RESIDENTIAL <c>item spawns resident</c>, 11 RESIDENTIAL_SMALL <c>item spawns resident
    /// small</c>; other designations none.
    /// </summary>
    public static IReadOnlyList<RecordLink> VendorLinks(SquadTemplate template, GameRecord? faction)
    {
        if (template.Vendors.Count > 0 || faction is null) return template.Vendors;

        string? list = template.BuildingDesignation switch
        {
            2 or 5 => "item spawns armoury",
            3 => "item spawns bar",
            6 => "item spawns treasure",
            8 => "item spawns HQ",
            9 => "item spawns resident",
            11 => "item spawns resident small",
            _ => null,
        };
        return list is null
            ? [] : [.. faction.GetReferences(list).Select(r => new RecordLink(r.TargetStringId, r.Values.Value0, r.Values.Value1, r.Values.Value2))];
    }

    /// <summary>
    /// Replaces the shop's stock but its unique items and returns the new purse; <paramref name="specials"/> (a squad's first fill) puts its
    /// template's special items in first.
    /// </summary>
    public static long Fill(Shop shop, SquadTemplate template, Market market, PopulationData data, ulong seed, ulong key, bool specials = false)
    {
        var rng = new RandomStream(seed, Rng.Mix(key ^ ((ulong)shop.Fills << 40)), RngPurpose.Trade);
        shop.Fills++;

        // The fills clear everything but unique items (docs/game/economy.md "Artifacts").
        shop.Stock.Items.RemoveAll(i => !i.Unique);
        int n = (int)MathF.Round((0.8f + 0.4f * rng.NextFloat()) * template.VendorsFillTotalAmount, MidpointRounding.AwayFromZero);
        if (shop.Building is not null) n = Math.Max(n, 1);   // the building filler makes at least one

        var faction = (uint)shop.Terms.Faction < (uint)data.Factions.Count ? data.Db.Find(data.Factions[shop.Terms.Faction].Id) : null;
        var lists = new List<(VendorList List, int Weight)>();

        foreach (var link in VendorLinks(template, faction))
        {
            if (data.Db.Find(link.Id) is { Type: FcsRecordType.VendorList } record)
                lists.Add((VendorList.From(data.Db, record), link.V0 == 0 ? 100 : link.V0));
        }

        long totalWeight = lists.Sum(l => (long)l.Weight);
        var town = market.SiteCulture(shop.Terms.Site);
        var own = market.FactionCulture(shop.Terms.Faction);

        var units = new List<ItemInstance>();
        long cats = 0;
        int assigned = 0;

        for (int l = 0; l < lists.Count && totalWeight > 0; l++)
        {
            var (list, weight) = lists[l];

            // FUN_140959800 (Verified): each list's own share is (v / sum) x n rounded half away from zero; the last adds what is left when that is
            // above 0, so it takes max(n - assigned before it, its own share) and the total can exceed n.
            int count = (int)MathF.Round((float)weight / totalWeight * n, MidpointRounding.AwayFromZero);
            if (l == lists.Count - 1) count = Math.Max(n - assigned, count);

            assigned += count;
            var entries = list.Entries.Where(e => town?.Forbidden.Contains(e.Id) != true && own?.Forbidden.Contains(e.Id) != true).ToList();

            for (int k = 0; k < count && entries.Count > 0; k++)
            {
                if (Make(Pick(entries, ref rng), list, faction, template, market, data.Db, ref rng) is { } item)
                    Merge(units, item);
            }

            if (list.MoneyItemProb > 0 && rng.NextFloat() <= list.MoneyItemProb && list.MoneyItemMax >= list.MoneyItemMin)
                cats += list.MoneyItemMin + rng.NextInt(list.MoneyItemMax - list.MoneyItemMin + 1);
        }

        if (specials) AddSpecials(shop, template.Id, data.Db, market);

        // Dearest first, ties by record id so the order never depends on anything but the data.
        foreach (var item in units.OrderByDescending(u => market.UnitPrice(u, false, shop.Terms, shop.Terms.Faction)).ThenBy(u => u.Record,
            StringComparer.Ordinal))
            shop.Stock.Add(item);

        // The purse counts what this fill made, not the unique items it kept (FUN_14095b210 sums its new stock only).
        long value = shop.Stock.All().Where(i => !i.Unique).Sum(i => market.StackPrice(i, false, shop.Terms, shop.Terms.Faction));
        if (shop.Building is null) return (long)Math.Clamp(0.25f * value, 3000, 25000) + cats;

        // A building shop's purse (FUN_14095b210): vendor money with a roll from -0.4 x it (the roll's upper bound is not shown by the decompile;
        // engine: +0.4 x it), else the squad rule.
        int vm = template.VendorMoney;
        if (vm < 1) return (long)Math.Clamp(0.25f * value, 3000, 25000) + cats;

        int spread = (int)(vm * 0.4);
        return vm - spread + rng.NextInt(2 * spread + 1) + cats;
    }

    /// <summary>
    /// SQUAD_TEMPLATE <c>special items</c> (ITEM references), then <c>special map items</c> (MAP_ITEM references), on a fill with specials
    /// (<c>FUN_140958740</c> from the shop refresh <c>FUN_14095b210</c>, <b>Verified</b>): both lists are stocked when both exist (the original skips
    /// both only when both are empty). Each entry's v0 items go into the stock before the vendor items in stacks of at most the record's raw
    /// <c>stackable</c> (at least 1: a 0 loops forever in the original), also for a map item whose stack limit elsewhere is 1; what fits nowhere is
    /// lost. Engine: the original also marks them unique (<c>Item::isUnique</c>), which is not modelled.
    /// </summary>
    static void AddSpecials(Shop shop, string templateId, GameDatabase db, Market market)
    {
        if (db.Find(templateId) is not { } template) return;

        AddSpecialList(shop, template.GetReferences("special items"), FcsRecordType.Item, db, market);
        AddSpecialList(shop, template.GetReferences("special map items"), FcsRecordType.MapItem, db, market);
    }

    /// <summary>
    /// Puts each <paramref name="type"/> record of <paramref name="list"/> into the stock, its v0 units in stacks of at most its <c>stackable</c> (at
    /// least 1).
    /// </summary>
    static void AddSpecialList(Shop shop, IReadOnlyList<GameReference> list, FcsRecordType type, GameDatabase db, Market market)
    {
        foreach (var r in list)
        {
            if (db.Find(r.TargetStringId) is not { } record || record.Type != type) continue;

            int perStack = Math.Max(record.GetInt("stackable"), 1);

            for (int left = r.Values.Value0; left > 0;)
            {
                int stack = Math.Min(left, perStack);
                if (market.Items.Create(r.TargetStringId, stack) is { } item) shop.Stock.Add(item);

                left -= stack;
            }
        }
    }

    /// <summary>
    /// A weighted pick of <paramref name="entries"/> by one float draw (entries of weight 0 or less never picked; the first when no weight is
    /// above 0).
    /// </summary>
    internal static VendorEntry Pick(IReadOnlyList<VendorEntry> entries, ref RandomStream rng)
    {
        long total = entries.Sum(e => (long)Math.Max(e.Weight, 0));
        if (total <= 0) return entries[0];

        long roll = (long)(rng.NextFloat() * total);

        foreach (var e in entries)
        {
            if (roll < e.Weight) return e;

            roll -= Math.Max(e.Weight, 0);
        }

        return entries[^1];
    }

    /// <summary>
    /// Puts one unit into the list: onto a stack of the same record and quality with room, else as a new item.
    /// </summary>
    static void Merge(List<ItemInstance> units, ItemInstance item)
    {
        foreach (var u in units)
        {
            if (u.Record == item.Record && u.Quality == item.Quality && u.Level == item.Level && u.CompanyId == item.CompanyId
                && u.Quantity < u.StackLimit)
            {
                u.Quantity++;
                return;
            }
        }

        units.Add(item);
    }

    /// <summary>
    /// A new unit of <paramref name="entry"/>: armour of a rolled grade's quality, a limb or crossbow of a rolled level, a weapon with a manufacturer
    /// and model; null when the record makes none.
    /// </summary>
    static ItemInstance? Make(VendorEntry entry, VendorList list, GameRecord? faction, SquadTemplate template, Market market, GameDatabase db,
        ref RandomStream rng)
    {
        float quality = entry.Type == FcsRecordType.Armour ? CharacterGenerator.QualityOfGrade(Grade(faction, ref rng)) : 0;
        if (market.Items.Create(entry.Id, 1, quality) is not { } item) return null;
        if (entry.Type == FcsRecordType.LimbReplacement)
            item.Level = CharacterGenerator.QualityOfGrade(LimbGrade(template.RoboticsLevels, ref rng));
        if (entry.Type == FcsRecordType.Crossbow)
            item.Level = CharacterGenerator.QualityOfGrade(CrossbowGrade(template.CrossbowLevels, ref rng));

        if (entry.Type == FcsRecordType.Weapon && Maker(list, db, ref rng) is { } made)
        {
            item.CompanyId = made.Maker.StringId;
            item.MaterialId = made.Model ?? "";
            item.Level = made.Level;
        }

        return item;
    }

    /// <summary>
    /// A shop limb's grade around the template's <c>robotics levels</c> g (<c>FUN_140955370</c>, <b>Verified</b>): g weighs 100, g - 2 22 when g > 1,
    /// g - 1 44 when g > 0, g + 1 44 when g &lt; 5, g + 2 22 when g &lt; 4; the roll is an int in [0, total) against the running sums in that order
    /// (engine reading of the roll's range).
    /// </summary>
    public static int LimbGrade(int g, ref RandomStream rng) =>
        Spread([(g, 100), (g - 2, g > 1 ? 22 : 0), (g - 1, g > 0 ? 44 : 0), (g + 1, g < 5 ? 44 : 0), (g + 2, g < 4 ? 22 : 0)], g, ref rng);

    /// <summary>
    /// A shop crossbow's grade around the template's <c>crossbow levels</c> g (<c>FUN_140955210</c>, <b>Verified</b>): g weighs 100; unless g is 5, g
    /// - 2 20 when g > 1, g - 1 33 when g > 0, g + 1 33 when g &lt; 5, g + 2 5 when g &lt; 4. Rolled as <see cref="LimbGrade"/>.
    /// </summary>
    public static int CrossbowGrade(int g, ref RandomStream rng) => g == 5 ? g :
        Spread([(g, 100), (g - 2, g > 1 ? 20 : 0), (g - 1, g > 0 ? 33 : 0), (g + 1, g < 5 ? 33 : 0), (g + 2, g < 4 ? 5 : 0)], g, ref rng);

    /// <summary>
    /// The grade of <paramref name="w"/> an int draw in [0, total) falls on against the running sums; <paramref name="g"/> when none.
    /// </summary>
    static int Spread(ReadOnlySpan<(int Grade, int Weight)> w, int g, ref RandomStream rng)
    {
        int total = 0;
        foreach (var (_, weight) in w) total += weight;

        int roll = rng.NextInt(total);

        foreach (var (grade, weight) in w)
        {
            if (roll < weight) return grade;

            roll -= weight;
        }

        return g;
    }

    /// <summary>
    /// A grade 0..5 by the faction's <c>armors 0</c> .. <c>armors 5</c> chances (<c>FUN_140954c80</c> builds the table, <c>FUN_14063cea0</c> draws,
    /// <b>Verified</b>): only chances above 0 count (a faction with none: grade 0), no faction gives 50 / 80 / 25 / 5 for grades 0..3; a single grade
    /// is taken with no draw, else one float(0, total) draw against the running sums.
    /// </summary>
    internal static int Grade(GameRecord? faction, ref RandomStream rng)
    {
        Span<int> w = stackalloc int[6];
        int total = 0, kinds = 0, only = 0;

        for (int g = 0; g < 6; g++)
        {
            w[g] = Math.Max(faction is null ? NoFactionGradeChances[g] : faction.GetInt($"armors {g}", DefaultGradeChances[g]), 0);
            total += w[g];

            if (w[g] > 0)
            {
                kinds++;
                only = g;
            }
        }

        if (kinds <= 1) return only;

        float roll = rng.NextFloat() * total;

        for (int g = 0; g < 6; g++)
        {
            if (w[g] > 0 && roll < w[g]) return g;

            roll -= w[g];
        }

        return only;
    }

    /// <summary>
    /// The grade chances of an item list made with no faction (<c>FUN_140954c80</c>, constants read from the exe).
    /// </summary>
    static readonly int[] NoFactionGradeChances = [50, 80, 25, 5, 0, 0];

    /// <summary>
    /// A manufacturer from the list (v0, 0 counting as 100), then a model from its <c>weapon models</c> (v1, 0 counting as 100) whose v0 is the
    /// level; none or level 0: the fixed fallback model.
    /// </summary>
    internal static (GameRecord Maker, string? Model, int Level)? Maker(VendorList list, GameDatabase db, ref RandomStream rng)
    {
        var makers = list.Manufacturers
            .Select(m => (Record: db.Find(m.Id), Weight: m.V0 == 0 ? 100 : m.V0))
            .Where(m => m.Record is not null && m.Weight > 0)
            .ToList();
        if (makers.Count == 0) return null;

        var maker = PickWeighted(makers, m => m.Weight, ref rng).Record!;   // the list is not empty and every weight is above 0
        var models = maker.GetReferences("weapon models").Where(r => db.Find(r.TargetStringId) is not null).ToList();

        // No model or a model of level 0: the factory substitutes manufacturer 1057 and model 1058 (FUN_140580750, Verified).
        if (models.Count == 0 || Model(models, ref rng) is not { Values.Value0: not 0 } picked)
        {
            var (fallback, model, level) = CharacterGenerator.FallbackWeaponMaker(db, maker);
            return (fallback, model?.StringId, level);
        }

        return (maker, picked.TargetStringId, picked.Values.Value0);
    }

    /// <summary>
    /// The weighted pick of a weapon model (v1 the weight, 0 counting 100; negative ones left out).
    /// </summary>
    static GameReference? Model(List<GameReference> models, ref RandomStream rng) =>
        PickWeighted(models.Where(r => r.Values.Value1 >= 0).ToList(), r => r.Values.Value1 == 0 ? 100 : r.Values.Value1, ref rng);

    /// <summary>
    /// The factory's weighted choice (<c>FUN_14057fd40</c>, <b>Verified</b>): candidates of weight above 0 (a weight of 0 counts 100); none gives
    /// none, one is taken with no draw, else one float(0, total) draw against the running sums. Used for the manufacturer (v0) and the model (v1).
    /// </summary>
    static T? PickWeighted<T>(List<T> candidates, Func<T, int> weight, ref RandomStream rng)
    {
        var live = candidates.Where(c => weight(c) > 0).ToList();
        if (live.Count == 0) return default;
        if (live.Count == 1) return live[0];

        int roll = rng.NextInt(live.Sum(weight));

        foreach (var c in live)
        {
            if (roll < weight(c)) return c;

            roll -= weight(c);
        }

        return live[^1];
    }
}