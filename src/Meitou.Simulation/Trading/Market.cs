using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Economy;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Trading;

/// <summary>
/// Who the player trades with, as the price code sees it: the trader squad's town, faction and template fields (docs/game/economy.md section 6).
/// </summary>
/// <param name="Site">The trader's town, a position in <see cref="PopulationData.Sites"/> (-1: none, no culture or market factor of a town).</param>
/// <param name="Faction">The trader's faction, a position in <see cref="PopulationData.Factions"/> (-1: none).</param>
/// <param name="BuyMult">SQUAD_TEMPLATE <c>buy mult</c>.</param>
public readonly record struct Trader(int Site, int Faction, float BuyMult = 1, bool BuysStolen = false, bool BuysIllegal = false)
{
    /// <summary>
    /// No trade open: every factor 1.
    /// </summary>
    public static Trader None { get; } = new(-1, -1);
}

/// <summary>
/// Item prices in play (docs/game/economy.md sections 5 to 7), over the world's data and seed. Pure: nothing here changes the world, so the UI and
/// the trade system read the same prices.
/// <list type="bullet">
/// <item><b>Base value</b> (<see cref="BaseValue"/>): <see cref="ItemValue"/> with the category rows, at the item's quality (a weapon's or crossbow's
/// quality is its model level, the <c>Gear +0x224</c> int of combat.md; armour's its level) and, for a weapon, its manufacturer. The game stores it
/// in weapons and armour when they are made; the constants never change in a run, so computing it again gives the same number.</item>
/// <item><b>Unit price</b> (<see cref="UnitPrice"/>, the item's slot 81) and <b>stack price</b> (<see cref="StackPrice"/>, slot 82), with the
/// <c>sell</c> flag set when the player sells.</item>
/// <item><b>Trader factor</b> (<see cref="TraderFactor"/>): <c>buy mult</c> x the culture price, halved for an item stolen from the player's faction
/// (as read, section 6).</item>
/// <item><b>Market factor</b> (<see cref="MarketFactor"/>): the random per-town factor of the <c>all trade goods</c> items, rolled for every town
/// from the world seed (section 7.2).</item>
/// </list>
/// </summary>
public sealed class Market
{
    /// <summary>
    /// The ITEM the game counts as money (a <c>MoneyItem</c>, priced at its record <c>value</c>). Which records make a <c>MoneyItem</c> is
    /// <b>Unknown</b>; this name is the engine's test.
    /// </summary>
    public const string MoneyItemName = "Cats";
    /// <summary>
    /// The VENDOR_LIST whose items get a market factor in every town (found by name and type, as <c>Zones_TownSetup</c> does).
    /// </summary>
    public const string AllTradeGoods = "all trade goods";
    /// <summary>
    /// The price factor of armour that is a faction's uniform, in both directions (<c>Armour::vfunc_81</c> <c>FUN_1408a9c20</c>, <b>Verified</b>: the
    /// constant 0.3 the loader writes itself, not a CONSTANTS field).
    /// </summary>
    public const float UniformPriceMult = 0.3f;

    /// <summary>
    /// The world's data: items, towns, factions and the game database.
    /// </summary>
    readonly PopulationData _data;
    /// <summary>
    /// The base value of each record, quality and manufacturer asked for so far.
    /// </summary>
    readonly ConcurrentDictionary<(string Record, float Quality, string Company), int> _baseValues = new();
    /// <summary>
    /// The trade culture of each placed town and of each faction (null: none).
    /// </summary>
    readonly TradeCulture?[] _siteCultures, _factionCultures;
    /// <summary>
    /// The towns' market factors, rolled on first use.
    /// </summary>
    readonly Lazy<MarketFactors> _factors;

    /// <summary>
    /// The CONSTANTS the prices read.
    /// </summary>
    public PriceConstants Constants { get; }
    /// <summary>
    /// The item factory of the world's data.
    /// </summary>
    public ItemFactory Items => _data.Items;

    /// <summary>
    /// Prices for <paramref name="data"/>'s items, towns and factions; the market factors are rolled from <paramref name="seed"/> when first read.
    /// </summary>
    public Market(PopulationData data, ulong seed)
    {
        this._data = data;
        Constants = PriceConstants.FromDatabase(data.Db);
        _siteCultures = [.. data.Sites.Select(s => TradeCulture.From(data.Db, s.Town.TradeCulture))];
        _factionCultures = [.. data.Factions.Select(f => TradeCulture.From(data.Db, f.TradeCulture))];
        _factors = new(() => MarketFactors.Roll(data, Constants.TradeProfitMargins, seed));
    }

    /// <summary>
    /// The culture of a town (null: none, or no such town).
    /// </summary>
    public TradeCulture? SiteCulture(int site) => (uint)site < (uint)_siteCultures.Length ? _siteCultures[site] : null;
    /// <summary>
    /// The culture of a faction (null: none).
    /// </summary>
    public TradeCulture? FactionCulture(int faction) => (uint)faction < (uint)_factionCultures.Length ? _factionCultures[faction] : null;

    /// <summary>
    /// The quality <c>ItemValue</c> gets: <see cref="ItemInstance.GearLevel"/>, never the saved `quality` (armour at level 0 is worth its record's
    /// own value).
    /// </summary>
    static float QualityOf(ItemInstance item) => item.GearLevel;

    /// <summary>
    /// <c>ItemValue(record, quality, 1, manufacturer)</c> of an item (0 for a record the data does not have).
    /// </summary>
    public int BaseValue(ItemInstance item) => _baseValues.GetOrAdd((item.Record, QualityOf(item), item.CompanyId), static (key, m) =>
    {
        var db = m._data.Db;
        if (db.Find(key.Record) is not { } record) return 0;

        var maker = key.Company.Length > 0 ? db.Find(key.Company) : null;
        return ItemValue.Of(db, record, key.Quality, true, m.Constants, maker is { Type: FcsRecordType.WeaponManufacturer } ? maker : null);
    }, this);

    /// <summary>
    /// The Value and Sell value an inventory tooltip prints for one piece of gear with no trader (<b>Observed</b>: the Samurai Legplates at level 95
    /// print c.16,262 and c.4,065, the numbers the wiki lists for a Masterwork one): the base value at the item's level (a weapon's, crossbow's and
    /// armour's grade quality), and that x <c>loot price mult GEAR</c> truncated. Null for anything but armour, a weapon or a crossbow (their prices
    /// need the charges and the category rows, not done).
    /// </summary>
    public static (int Value, int Sell)? GearPrices(GameDatabase db, PriceConstants k, string record, float level, string company)
    {
        if (db.Find(record) is not { Type: FcsRecordType.Armour or FcsRecordType.Weapon or FcsRecordType.Crossbow } r)
            return null;

        var maker = company.Length > 0 ? db.Find(company) : null;
        int value = ItemValue.Of(db, r, level, true, k, maker is { Type: FcsRecordType.WeaponManufacturer } ? maker : null);
        return (value, (int)(value * k.LootPriceMultGear));
    }

    /// <summary>
    /// The <c>Avg. Price</c> an ITEM's tooltip prints when it is a <c>trade item</c> (<c>FUN_1407ae130</c>, <b>Observed</b> in the disassembly):
    /// <c>ItemValue</c> at the item's level (the item's slot 87, 0 for a plain item, so quality plays no part) with the category rows and no
    /// manufacturer, i.e. the base value with no town or trader factor. Null otherwise.
    /// </summary>
    public int? AveragePrice(ItemInstance item) =>
        item.Type == FcsRecordType.Item && Items.Info(item.Record) is { TradeItem: true } ? BaseValue(item) : null;

    /// <summary>
    /// The culture price of an item record for a trader (section 6, <c>FUN_14092bec0</c>, <b>Observed</b>): the trader's town's culture if it has one
    /// and names the item, else the culture of the town's faction (the town's <c>getFaction</c>, not the trader's: a Shinobi thief in Sho-Battai
    /// prices by United Cities), else 1; 1 with no town.
    /// </summary>
    public float CulturePrice(string record, Trader trader)
    {
        if ((uint)trader.Site >= (uint)_data.Sites.Count) return 1;
        if (SiteCulture(trader.Site) is { } town && town.TryPrice(record, out float f)) return f;

        return FactionCulture(_data.SiteFactions[trader.Site]) is { } owner && owner.TryPrice(record, out f) ? f : 1;
    }

    /// <summary>
    /// The trader factor T (<c>FUN_14079e6d0</c>, <b>Observed</b>): <c>buy mult</c> x 0.5 when the item is stolen and its from-owner is the player's
    /// faction (the condition as read; the predicate is only partly decoded) x the culture price. <paramref name="holderFaction"/> is the faction of
    /// whoever holds the item now. 1 with no trader.
    /// </summary>
    public float TraderFactor(ItemInstance item, Trader trader, int holderFaction)
    {
        if (trader == Trader.None) return 1;

        float t = trader.BuyMult;
        if (item.IsStolenFor(holderFaction, _data) && item.StolenFrom == _data.PlayerFaction) t *= 0.5f;

        return t * CulturePrice(item.Record, trader);
    }

    /// <summary>
    /// The market factor of an item record in a town (section 7.2): 1 for an item outside <c>all trade goods</c> or a town without factors.
    /// </summary>
    public float MarketFactor(int site, string record) => _factors.Value.Of(site, record);

    /// <summary>
    /// The price of one unit (the item classes' slot 81, section 5), <paramref name="sell"/> true when the player sells it to
    /// <paramref name="trader"/>:
    /// <list type="bullet">
    /// <item>Weapons and crossbows: trunc(T x base), selling trunc(T x base x <c>loot price mult GEAR</c>). (A crossbow taking the weapon's slot is
    /// an engine reading: <b>Unknown</b>.)</item>
    /// <item>Armour: the same with the factor applied to the base first (<c>loot price mult player armour</c> instead of GEAR when crafted by a
    /// smith, as a weapon's player row), then x 0.3 when it is a faction's uniform (<see cref="ItemInstance.Uniform"/>), buying or selling, then
    /// T.</item>
    /// <item>The money item: its record <c>value</c>, nothing else.</item>
    /// <item>Maps and blueprints: trunc(T x <c>value</c>), selling x <c>loot price mult</c> / <c>loot price mult GEAR</c>. A blueprint's value is its
    /// research's <c>money</c> (<b>Verified</b>, the <c>BlueprintItem</c> constructor); a map's stored price comes from places not traced, so the
    /// record's <c>value</c> is an engine stand-in.</item>
    /// <item>Everything else (<c>FUN_1407a8f90</c>, <b>Verified</b>): the base value, x the town's market factor, x <c>loot price mult</c> when sold
    /// unless it is a <c>trade item</c> that is not stolen, x charges / max charges for a single unit, then trunc(T x that), each step
    /// truncated.</item>
    /// </list>
    /// </summary>
    public int UnitPrice(ItemInstance item, bool sell, Trader trader, int holderFaction)
    {
        float t = TraderFactor(item, trader, holderFaction);
        var k = Constants;

        switch (item.Type)
        {
            case FcsRecordType.Weapon or FcsRecordType.Crossbow:
                {
                    int stored = BaseValue(item);
                    return sell
                        ? (int)(t * stored * (item.Crafter.Length > 0 ? k.LootPriceMultPlayerWeapons : k.LootPriceMultGear)) : (int)(t * stored);
                }
            case FcsRecordType.Armour:
                {
                    float v = BaseValue(item);
                    if (sell) v *= item.Crafter.Length > 0 ? k.LootPriceMultPlayerArmour : k.LootPriceMultGear;
                    if (item.Uniform.Length > 0 && Items.Db.Find(item.Uniform) is { Type: FcsRecordType.Faction })
                        v *= UniformPriceMult;

                    return (int)(t * v);
                }
            case FcsRecordType.MapItem or FcsRecordType.Blueprint:
                {
                    int value = item.Type == FcsRecordType.Blueprint
                        ? Items.ResearchOf(item)?.GetInt("money") ?? 0 : Items.Info(item.Record)?.Value ?? 0;
                    if (!sell) return (int)(t * value);

                    return (int)(t * value * (item.Type == FcsRecordType.MapItem ? k.LootPriceMult : k.LootPriceMultGear));
                }
        }

        var info = Items.Info(item.Record);
        if (item.Type == FcsRecordType.Item && info?.BaseName == MoneyItemName) return info.Value;

        int u = BaseValue(item);
        if (trader.Site >= 0) u = (int)(u * MarketFactor(trader.Site, item.Record));
        if (sell && !(info is { TradeItem: true } && !item.IsStolenFor(holderFaction, _data)))
            u = (int)(u * k.LootPriceMult);
        if (item.Quantity == 1 && item.MaxCharges > 0) u = (int)(u * item.Charges / item.MaxCharges);

        return (int)(t * u);
    }

    /// <summary>
    /// The price of the whole stack (slot 82, <c>FUN_1407915b0</c>, <b>Verified</b>): the unit price for a single unit, else every unit at the full
    /// unit price with the last scaled by its charge fraction: p x n - trunc(p - p x charges / max charges).
    /// </summary>
    public long StackPrice(ItemInstance item, bool sell, Trader trader, int holderFaction)
    {
        int p = UnitPrice(item, sell, trader, holderFaction);
        if (item.Quantity <= 1) return p;

        float fraction = item.MaxCharges > 0 ? item.Charges / item.MaxCharges : 1;
        return (long)p * item.Quantity - (int)(p - p * fraction);
    }
}

/// <summary>
/// The market factors of every town (docs/game/economy.md 7.2, <b>Observed</b>): for each item of <c>all trade goods</c> a factor uniform in [1 - m,
/// 1 + m), m = <c>trade profit margins</c>, then capped to p + w and raised to at least p - w for every town set up before (p its factor, w = d /
/// 1,000,000 x m x 10, d the x-z distance). Engine choices where the original is <b>Unknown</b>: towns are set up in the order of the placed sites
/// and a town meets only the ones before it (the original's global list and its order are not decoded); the towns of type Nest, NestMarker,
/// PointOfInterest and Null have no factors (which TOWN records make a <c>Town</c> object is not decoded); the rolls come from the world seed, not
/// the C <c>rand()</c>, so they are the same for every load of a world (the original saves them as the town key <c>trade goods</c>, not done here).
/// </summary>
sealed class MarketFactors
{
    /// <summary>
    /// The position of each <c>all trade goods</c> item in a town's factor list, by record id.
    /// </summary>
    readonly Dictionary<string, int> _itemIndex;
    /// <summary>
    /// Each placed town's factors (null: no market).
    /// </summary>
    readonly float[]?[] _bySite;

    /// <summary>
    /// The factors of the items in <paramref name="itemIndex"/> per town in <paramref name="bySite"/>.
    /// </summary>
    MarketFactors(Dictionary<string, int> itemIndex, float[]?[] bySite)
    {
        this._itemIndex = itemIndex;
        this._bySite = bySite;
    }

    /// <summary>
    /// The market factor of <paramref name="record"/> in town <paramref name="site"/>; 1 when it has none.
    /// </summary>
    public float Of(int site, string record) =>
        (uint)site < (uint)_bySite.Length && _bySite[site] is { } f && _itemIndex.TryGetValue(record, out int i) ? f[i] : 1;

    /// <summary>
    /// Whether a town of <paramref name="type"/> has market factors (not a nest, nest marker, point of interest or null town).
    /// </summary>
    public static bool HasMarket(TownType type) => type is not (TownType.Nest or TownType.NestMarker or TownType.PointOfInterest or TownType.Null);

    /// <summary>
    /// Rolls every market town's factors in placed-site order (see the class summary).
    /// </summary>
    public static MarketFactors Roll(PopulationData data, float margin, ulong seed)
    {
        var list = data.Db.OfType(FcsRecordType.VendorList).FirstOrDefault(r => r.Name == Market.AllTradeGoods);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        if (list is not null)
            foreach (var r in list.GetReferences("items")) index.TryAdd(r.TargetStringId, index.Count);

        var bySite = new float[]?[data.Sites.Count];
        var done = new List<int>();

        foreach (var site in data.Sites)
        {
            if (index.Count == 0 || !HasMarket(site.Town.Type)) continue;

            var f = new float[index.Count];
            var stream = new RandomStream(seed, Rng.StableHash(site.Town.Id) ^ (ulong)site.Index, RngPurpose.Trade);

            for (int i = 0; i < f.Length; i++)
            {
                float v = 1 - margin + 2 * margin * stream.NextFloat();

                foreach (int j in done)
                {
                    var other = data.Sites[j];
                    float dx = other.Position.X - site.Position.X, dz = other.Position.Z - site.Position.Z;
                    float w = MathF.Sqrt(dx * dx + dz * dz) / 1_000_000f * margin * 10;
                    float p = bySite[j]![i];
                    v = MathF.Max(MathF.Min(v, p + w), p - w);
                }

                f[i] = v;
            }

            bySite[site.Index] = f;
            done.Add(site.Index);
        }

        return new MarketFactors(index, bySite);
    }
}