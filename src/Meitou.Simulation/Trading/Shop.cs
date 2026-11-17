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
/// <see cref="Squad.Money"/>, the game's <c>Platoon+0x88</c>. A squad shop's goods are one inventory of <see cref="Width"/> x <see cref="Height"/>
/// cells held by the squad (<b>Engine choice</b>: in the original they lie in its members' inventories); a building shop's lie in the shop
/// building's stock containers (<see cref="Containers"/>), as in the original. Either way the trader window is a copy of the goods
/// (<see cref="Window"/>, <see cref="ShopView"/>), as the original's shop wrapper <c>FUN_140953850</c> is for both.
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
    /// The stock: a squad shop's own goods, a building shop's trader window (<see cref="ShopView"/>); cells hold <see cref="StockStackMult"/> x each
    /// item's <c>stackable</c>. A building shop's grows while its view is built.
    /// </summary>
    public Inventory Stock { get; internal set; } = new(Width, Height) { StackBonusMult = StockStackMult };
    /// <summary>
    /// A squad shop's trader window (<see cref="Window"/>).
    /// </summary>
    Inventory _window = new(Width, Height) { StackBonusMult = StockStackMult };
    /// <summary>
    /// The grid the trade window shows and trades on: a copy of the goods (<see cref="ShopView"/>), so their money items and the cells those take
    /// are not in it; a building shop's is its <see cref="Stock"/>, a squad shop's a grid of its own.
    /// </summary>
    internal Inventory Window
    {
        get => Building is null ? _window : Stock;
        set
        {
            if (Building is null) _window = value;
            else Stock = value;
        }
    }
    /// <summary>
    /// The trader's terms the prices read.
    /// </summary>
    public required Trader Terms { get; init; }
    /// <summary>
    /// A building shop's building (<see cref="Meitou.Data.World.TownBuilding.Key"/>), whose stock containers hold its goods; null for a squad shop.
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
    /// The stock containers of a building shop (fixture ids), in the building's order; empty for a squad shop.
    /// </summary>
    public IReadOnlyList<string> Containers { get; init; } = [];
    /// <summary>
    /// A building shop's trader window: its columns and the rows it starts with.
    /// </summary>
    public (int Columns, int Rows) ViewSize { get; init; } = (Width, Height);
    /// <summary>
    /// The trader window's state (<see cref="ShopView"/>).
    /// </summary>
    internal ShopView View { get; } = new();

    /// <summary>
    /// A building shop of <paramref name="building"/> (the shop wrapper <c>FUN_140953850</c>, <b>Observed</b>): null when the building has no stock
    /// container. Its trader window is one grid as wide as the widest container (at least 18) and ceil(cells / width) high for the containers' cells
    /// together (18 x 18 without cells); <see cref="ShopView"/> fills it with copies of what they hold.
    /// </summary>
    public static Shop? ForBuilding(Meitou.Data.World.TownBuilding building, Trader terms)
    {
        var boxes = building.Fixtures.Where(f => f.HoldsShopStock).ToList();
        if (boxes.Count == 0) return null;

        int columns = Math.Max(Width, boxes.Max(f => f.StorageWidth));
        int cells = boxes.Sum(f => f.StorageWidth * f.StorageHeight);
        int rows = cells > 0 ? (cells + columns - 1) / columns : columns;

        return new Shop
        {
            Terms = terms,
            Building = building.Key,
            Containers = [.. boxes.Select(f => f.Id)],
            ViewSize = (columns, rows),
            Stock = new Inventory(columns, rows) { StackBonusMult = StockStackMult },
        };
    }

    /// <summary>
    /// Whether <paramref name="fixture"/> is one of this building shop's stock containers.
    /// </summary>
    public bool Holds(string fixture) => Containers.Contains(fixture);

    /// <summary>
    /// Adds the next restock tick, the fill count and a squad shop's stock to the world hash (a building shop's goods are hashed with its
    /// containers; its trader window is not state).
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(NextRestockTick);
        h.Add(Fills);
        if (Building is null) Stock.Hash(ref h);
    }
}

/// <summary>
/// The units a fill made, its money stacks first, and the special items it adds (unique), with the purse a building shop's <c>vendor money</c>
/// gives (null: none set, the purse comes from the placed units' value).
/// </summary>
public sealed record ShopFill(List<ItemInstance> Units, List<ItemInstance> Specials, long? VendorPurse);

/// <summary>
/// Fills a shop as the squad filler <c>FUN_14095ab10</c> and the building filler <c>FUN_14095b210</c> do (docs/game/economy.md "Building shops" and
/// 9.2, <b>Verified</b>, decompile and disassembly where cited):
/// <list type="number">
/// <item>n = round(U(0.8, 1.2) x <c>vendors fill total amount</c> x wealth), half away from zero; wealth is 1 (the game's faction wealth function is
/// a stub returning 1); a building fill makes at least one.</item>
/// <item>Every list's money roll (<see cref="Money"/>) goes into one sum, made into money stacks (<see cref="ItemFactory.MoneyStacks"/>) at the
/// head of the units.</item>
/// <item>The vendor lists share n by their raw v0 (<see cref="Shares"/>), the last taking what the rounding left; each draws its share from its
/// table (<see cref="VendorTables"/>, <see cref="Draw"/>).</item>
/// <item>A squad shop (<see cref="StockSquad"/>) puts the units into its stock dearest first; a building shop's are spread over its containers
/// (<c>TradeSystem</c>). The purse is trunc(clamp(0.25 x the placed units' value, 3000, 25000) x wealth), a building shop's <c>vendor money</c>
/// +/- 0.4 x it when that is set (<see cref="Purse"/>); a fill that makes no unit leaves the purse as it was.</item>
/// </list>
/// </summary>
public static class ShopFiller
{
    /// <summary>
    /// The armour grade chances when a faction has none (the editor's defaults, economy.md 9.2).
    /// </summary>
    static readonly int[] DefaultGradeChances = [50, 100, 50, 2, 0, 0];

    /// <summary>
    /// The vendor lists a fill rolls from (<c>FUN_140958560</c>, <b>Verified</b>): the template's <c>vendors</c>; when it has none, for a building fill
    /// only (the squad filler passes no building; a squad shop always has <c>vendors</c>), by its building designation the owner FACTION's 2 BARRACKS
    /// / 5 ARMOURY <c>item spawns armoury</c>, 3 BAR <c>item spawns bar</c>, 6 TREASURE <c>item spawns treasure</c>, 8 HQ <c>item spawns HQ</c>, 9
    /// RESIDENTIAL <c>item spawns resident</c>, 11 RESIDENTIAL_SMALL <c>item spawns resident small</c>; other designations none.
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
    /// Rolls a fill: the units, the special items when <paramref name="specials"/> (the first fill), and a building shop's <c>vendor money</c>
    /// purse. Counts the fill (<see cref="Shop.Fills"/>, the key of its rolls).
    /// </summary>
    public static ShopFill Roll(Shop shop, SquadTemplate template, Market market, PopulationData data, VendorTables tables, ulong seed, ulong key,
        bool specials)
    {
        var rng = new RandomStream(seed, Rng.Mix(key ^ ((ulong)shop.Fills << 40)), RngPurpose.Trade);
        shop.Fills++;

        int n = (int)MathF.Round((0.8f + 0.4f * rng.NextFloat()) * template.VendorsFillTotalAmount, MidpointRounding.AwayFromZero);
        if (shop.Building is not null) n = Math.Max(n, 1);   // the building filler makes at least one

        int factionIndex = shop.Terms.Faction;
        var faction = (uint)factionIndex < (uint)data.Factions.Count ? data.Db.Find(data.Factions[factionIndex].Id) : null;
        var units = new List<ItemInstance>();
        var draw = new DrawContext(units, faction, factionIndex, template, market, data, tables);
        DrawLists(draw, Lists(template, faction, data.Db), n, ref rng);

        var special = specials ? Specials(template.Id, data.Db, market) : [];
        return new ShopFill(units, special, shop.Building is null ? null : VendorPurse(template, ref rng));
    }

    /// <summary>
    /// Rolls every list's money into one sum made into money stacks at the head of the units (<c>FUN_140959800</c> rolls them before the first
    /// draw), then draws every list's share from its table.
    /// </summary>
    static void DrawLists(DrawContext draw, List<(VendorList List, float Weight)> lists, int n, ref RandomStream rng)
    {
        int cats = 0;
        foreach (var (list, _) in lists) cats += Money(list, ref rng);
        draw.Units.AddRange(draw.Market.Items.MoneyStacks(cats));

        var shares = Shares(lists, n);

        for (int l = 0; l < lists.Count; l++)
        {
            var list = lists[l].List;
            Draw(draw, draw.Tables.Fetch(draw.Data, list, draw.FactionIndex), shares[l], list, ref rng);
        }
    }

    /// <summary>
    /// The template's vendor lists with their weights: each link's raw v0 (<c>FUN_140959800</c>, <b>Verified</b> by disassembly: it converts the
    /// int with no test, so a v0 of 0 weighs 0, unlike a list entry's).
    /// </summary>
    static List<(VendorList List, float Weight)> Lists(SquadTemplate template, GameRecord? faction, GameDatabase db)
    {
        var lists = new List<(VendorList List, float Weight)>();

        foreach (var link in VendorLinks(template, faction))
        {
            if (db.Find(link.Id) is { Type: FcsRecordType.VendorList } record)
                lists.Add((VendorList.From(db, record), link.V0));
        }

        return lists;
    }

    /// <summary>
    /// Each list's share of <paramref name="n"/> (<c>FUN_140959800</c>, <b>Verified</b> by disassembly): (w / sum) x n in floats, rounded by
    /// <see cref="RoundAway"/>, taken off what is left; the last list adds what is left when that is above 0, so the total can exceed n. A share below
    /// 0 (a negative weight, or 0 / 0 when every weight is 0) draws the list's own <c>items count</c> (<see cref="Draw"/>).
    /// </summary>
    static int[] Shares(List<(VendorList List, float Weight)> lists, int n)
    {
        float total = 0;
        foreach (var (_, weight) in lists) total += weight;

        var shares = new int[lists.Count];
        int left = n;

        for (int l = 0; l < lists.Count; l++)
        {
            shares[l] = RoundAway(lists[l].Weight / total * n);
            left = unchecked(left - shares[l]);

            if (l == lists.Count - 1 && left > 0) shares[l] = unchecked(shares[l] + left);
        }

        return shares;
    }

    /// <summary>
    /// The exe's rounding (<c>FUN_1409b32b0</c>, <b>Verified</b> by disassembly): ceil(x - 0.5) below 0, else floor(x + 0.5), truncated by
    /// <c>cvttss2si</c>, which gives int.MinValue for NaN and anything out of the int range.
    /// </summary>
    static int RoundAway(float x)
    {
        float r = x < 0 ? MathF.Ceiling(x - 0.5f) : MathF.Floor(x + 0.5f);

        return float.IsNaN(r) || r >= 2147483648f || r < -2147483648f ? int.MinValue : (int)r;
    }

    /// <summary>
    /// One list's money roll (<c>FUN_140956060</c>, <b>Verified</b>: decompile): none when <c>money item prob</c> is below U(0, 1), so 1 or more
    /// always pays; else <c>money item min</c> + an int in [0, max - min] (<c>FUN_1409b3b40</c>, clamped to [min, max], so min when max is below it).
    /// </summary>
    static int Money(VendorList list, ref RandomStream rng)
    {
        if (list.MoneyItemProb < rng.NextFloat()) return 0;

        return list.MoneyItemMax < list.MoneyItemMin
            ? list.MoneyItemMin
            : list.MoneyItemMin + rng.NextInt(list.MoneyItemMax - list.MoneyItemMin + 1);
    }

    /// <summary>
    /// A building shop's purse from <c>vendor money</c> (<c>FUN_14095b210</c>, <b>Verified</b> by disassembly): it + an int in [-trunc(0.4 x it),
    /// +trunc(0.4 x it)]; null when it is below 1.
    /// </summary>
    static long? VendorPurse(SquadTemplate template, ref RandomStream rng)
    {
        int vm = template.VendorMoney;
        if (vm < 1) return null;

        int spread = (int)(vm * 0.4);
        return vm - spread + rng.NextInt(2 * spread + 1);
    }

    /// <summary>
    /// The purse after a fill whose placed units are worth <paramref name="placedValue"/>: a building shop's <c>vendor money</c> purse when set, else
    /// trunc(clamp(0.25 x the value, 3000, 25000)) (<c>FUN_14095ab10</c>, <c>FUN_14095b210</c>: the value of the units placed, money stacks
    /// included, not of the special items nor of what the stock kept).
    /// </summary>
    public static long Purse(ShopFill fill, long placedValue) => fill.VendorPurse ?? (long)Math.Clamp(0.25f * placedValue, 3000, 25000);

    /// <summary>
    /// A squad shop's stock after a fill (<c>FUN_14095ab10</c>): cleared but its unique items, the special items, then the units dearest first by
    /// unit price (a sort over reversed bounds, <c>FUN_140969ff0</c>; ties by record id here, the original's by the items' addresses); what does not
    /// fit is destroyed. Returns the purse, or null when the fill made no unit (the purse stays).
    /// </summary>
    public static long? StockSquad(Shop shop, ShopFill fill, Market market)
    {
        shop.Stock.Items.RemoveAll(i => !i.Unique);
        foreach (var special in fill.Specials) shop.Stock.Add(special);
        if (fill.Units.Count == 0) return null;

        var ordered = fill.Units
            .OrderByDescending(u => market.UnitPrice(u, false, shop.Terms, shop.Terms.Faction))
            .ThenBy(u => u.Record, StringComparer.Ordinal);
        foreach (var item in ordered) shop.Stock.Add(item);

        long value = shop.Stock.All().Where(i => !i.Unique).Sum(i => market.StackPrice(i, false, shop.Terms, shop.Terms.Faction));
        return Purse(fill, value);
    }

    /// <summary>
    /// SQUAD_TEMPLATE <c>special items</c> (ITEM references), then <c>special map items</c> (MAP_ITEM references), on a fill with specials
    /// (<c>FUN_140958740</c> from the shop refresh <c>FUN_14095b210</c>, <b>Verified</b>): both lists are stocked when both exist (the original skips
    /// both only when both are empty). Each entry's v0 items in stacks of at most the record's raw <c>stackable</c> (at least 1: a 0 loops forever in
    /// the original), also for a map item whose stack limit elsewhere is 1, each flagged unique (<c>Item::isUnique</c>), so no fill clears them.
    /// </summary>
    static List<ItemInstance> Specials(string templateId, GameDatabase db, Market market)
    {
        var specials = new List<ItemInstance>();
        if (db.Find(templateId) is not { } template) return specials;

        AddSpecialList(specials, template.GetReferences("special items"), FcsRecordType.Item, db, market);
        AddSpecialList(specials, template.GetReferences("special map items"), FcsRecordType.MapItem, db, market);
        return specials;
    }

    /// <summary>
    /// Adds each <paramref name="type"/> record of <paramref name="list"/>, its v0 units in unique stacks of at most its <c>stackable</c> (at least
    /// 1).
    /// </summary>
    static void AddSpecialList(List<ItemInstance> specials, IReadOnlyList<GameReference> list, FcsRecordType type, GameDatabase db, Market market)
    {
        foreach (var r in list)
        {
            if (db.Find(r.TargetStringId) is not { } record || record.Type != type) continue;

            int perStack = Math.Max(record.GetInt("stackable"), 1);

            for (int left = r.Values.Value0; left > 0;)
            {
                int stack = Math.Min(left, perStack);
                if (market.Items.Create(r.TargetStringId, stack) is { } item)
                {
                    item.Unique = true;
                    specials.Add(item);
                }

                left -= stack;
            }
        }
    }

    /// <summary>
    /// A weighted pick of <paramref name="entries"/> by one float draw (entries of weight 0 or less never picked; the first when no weight is
    /// above 0); a single entry is taken with no draw (<c>FUN_1409555d0</c>, <b>Verified</b> by disassembly: a table of one is read without a roll).
    /// </summary>
    internal static VendorEntry Pick(IReadOnlyList<VendorEntry> entries, ref RandomStream rng)
    {
        if (entries.Count == 1) return entries[0];

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
    /// What every list of one fill draws with: the fill's units (a unit tops up any list's stack), the trader's faction (record and index),
    /// template, the market, the data and the session's list tables.
    /// </summary>
    readonly record struct DrawContext(List<ItemInstance> Units, GameRecord? Faction, int FactionIndex, SquadTemplate Template, Market Market,
        PopulationData Data, VendorTables Tables);

    /// <summary>
    /// Draws one list's share, <paramref name="count"/> units (a share below 0: the list's <c>items count</c>), from its
    /// <paramref name="table"/> into the fill's units (<c>FUN_1409555d0</c>, <b>Verified</b> by disassembly): one pick a unit, the draw ending
    /// when no entry is left or a unit cannot be made (<see cref="DrawOne"/>).
    /// </summary>
    static void Draw(DrawContext draw, VendorTable table, int count, VendorList list, ref RandomStream rng)
    {
        if (count < 0) count = list.ItemsCount;

        for (int k = 0; k < count && table.Entries.Count > 0; k++)
        {
            if (!DrawOne(draw, table, Pick(table.Entries, ref rng), list, ref rng)) return;
        }
    }

    /// <summary>
    /// One unit of <paramref name="entry"/>; false ends the list's draw. A weapon is always a new item and ends the draw when the list has no
    /// manufacturer or it cannot be made. Anything else first rolls its grade, then tops up the first unit of its record below the record's raw
    /// <c>stackable</c> (0 when missing, so gear, RESEARCH and records without it never stack here); else it is made anew, and a blueprint or map made
    /// anew leaves the table (<see cref="VendorTable.Take"/>), so a fill stocks each at most once per list. The original reads such an item it failed
    /// to make without a test (it would fault there); <b>engine choice</b>: the draw ends, as for a weapon.
    /// </summary>
    static bool DrawOne(DrawContext draw, VendorTable table, VendorEntry entry, VendorList list, ref RandomStream rng)
    {
        if (entry.Type == FcsRecordType.Weapon)
        {
            if (Weapon(draw, entry, list, ref rng) is not { } weapon) return false;

            draw.Units.Add(weapon);
            return true;
        }

        int grade = CharacterGenerator.QualityOfGrade(GradeOf(draw, entry, ref rng));
        if (TopUp(draw, entry)) return true;
        if (Make(draw, entry, grade) is not { } item) return false;

        draw.Units.Add(item);
        if (item.Type is FcsRecordType.Blueprint or FcsRecordType.MapItem) table.Take(entry);

        return true;
    }

    /// <summary>
    /// The grade a unit other than a weapon rolls before the top-up: a crossbow's from <see cref="CrossbowGrade"/>, a limb's from
    /// <see cref="LimbGrade"/>, anything else the faction's armour grade (<see cref="Grade"/>).
    /// </summary>
    static int GradeOf(DrawContext draw, VendorEntry entry, ref RandomStream rng) => entry.Type switch
    {
        FcsRecordType.Crossbow => CrossbowGrade(draw.Template.CrossbowLevels, ref rng),
        FcsRecordType.LimbReplacement => LimbGrade(draw.Template.RoboticsLevels, ref rng),
        _ => Grade(draw.Faction, ref rng),
    };

    /// <summary>
    /// Adds one to the first unit of <paramref name="entry"/>'s record whose quantity is below the record's raw <c>stackable</c>; false when none.
    /// </summary>
    static bool TopUp(DrawContext draw, VendorEntry entry)
    {
        int stackable = draw.Data.Db.Find(entry.Id)?.GetInt("stackable") ?? 0;

        foreach (var u in draw.Units)
        {
            if (u.Record != entry.Id || u.Quantity >= stackable) continue;

            u.Quantity++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A new unit of <paramref name="entry"/> with its rolled grade (5 / 20 / 40 / 60 / 80 / 95): armour's quality, a limb's or crossbow's level;
    /// null when the record makes none.
    /// </summary>
    static ItemInstance? Make(DrawContext draw, VendorEntry entry, int grade)
    {
        float quality = entry.Type == FcsRecordType.Armour ? grade : 0;
        if (draw.Market.Items.Create(entry.Id, 1, quality) is not { } item) return null;
        if (entry.Type is FcsRecordType.LimbReplacement or FcsRecordType.Crossbow) item.Level = grade;

        return item;
    }

    /// <summary>
    /// A new weapon of <paramref name="entry"/> with a manufacturer from the list and its model (<see cref="Maker"/>); null when the list has no
    /// manufacturer or the weapon cannot be made. Its level is the model's, nothing else (<b>Verified</b>, decompile: the draw hands the weapon
    /// factory <c>FUN_140580750</c> the manufacturer and a quality of 0, and the factory sets the level from the model it picks).
    /// </summary>
    static ItemInstance? Weapon(DrawContext draw, VendorEntry entry, VendorList list, ref RandomStream rng)
    {
        if (Maker(list, draw.Data.Db, ref rng) is not { } made) return null;
        if (draw.Market.Items.Create(entry.Id) is not { } item) return null;

        item.CompanyId = made.Maker.StringId;
        item.MaterialId = made.Model ?? "";
        item.Level = made.Level;
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