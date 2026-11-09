using System.Globalization;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Gameplay.Economy;
using Meitou.Data.World;

namespace Meitou.Game.DevTools;

/// <summary>
/// The second layer of <c>--wiki-export</c> (docs/agent-playtest.md, "The wiki"): town detail (buildings, houses for sale, the shops and their
/// stock), town overrides (which world states turn a town into another), and unique and bounty characters. All of it is read from the player's own
/// game data; the same readers the game boots with (<see cref="ResidentHomes"/>, <see cref="TownBuildings"/>) decide which building houses which
/// shop, and the original's price rule gives the house prices (docs/game/ui-screens.md "Selected building").
/// </summary>
static partial class WikiExport
{
    #region Detail rows

    /// <summary>
    /// What <see cref="Run"/> keeps of each placed town for the detail pass.
    /// </summary>
    sealed record PlacedTown(TownPlacement Placement, TownData Town, Row Row);

    /// <summary>
    /// The detail layer's rows: the shops, the vendor lists they roll their stock from, the town overrides and the unique and bounty characters.
    /// </summary>
    sealed record DetailRows(List<Row> Shops, List<Row> Vendors, List<Row> Overrides, List<Row> Characters);

    /// <summary>
    /// Builds the detail layer: fills each placed town's row with its buildings, shops and houses for sale, then reads the town overrides and the
    /// characters. Ids come from <paramref name="id"/> in the order the rows are made.
    /// </summary>
    static DetailRows Detail(GameDatabase db, WorldLevelData world, List<PlacedTown> placed, Func<string, string, string> id)
    {
        // --- Town detail: buildings, houses for sale, shops.
        var towns = new TownDetailPass(db, world, id);
        foreach (var town in placed) towns.Describe(town);

        // --- Town overrides: which world states turn a town into another.
        var overrides = new TownOverridePass(db, id).Rows(placed);

        // --- Unique and bounty characters.
        var characters = Characters(db, placed, towns.Templates, id);
        return new DetailRows(towns.Shops, towns.VendorRows(), overrides, characters);
    }

    #endregion

    #region Town detail

    /// <summary>
    /// One town's tallies while <see cref="TownDetailPass"/> describes it: the building types and groups with their counts, walls, gates and the
    /// houses for sale.
    /// </summary>
    sealed class TownTally(TownData town, FactionData? faction, Dictionary<string, SquadTemplate> homeOf, bool saleAllowed, bool hostile)
    {
        /// <summary>
        /// The town being described.
        /// </summary>
        public readonly TownData Town = town;

        /// <summary>
        /// The town's owning faction, null when its record names none (or not a faction).
        /// </summary>
        public readonly FactionData? Faction = faction;

        /// <summary>
        /// Building instance key to the resident template the housing rule put in it.
        /// </summary>
        public readonly Dictionary<string, SquadTemplate> HomeOf = homeOf;

        /// <summary>
        /// The town type sells houses at all (outposts, towns, villages).
        /// </summary>
        public readonly bool SaleAllowed = saleAllowed;

        /// <summary>
        /// The owning faction is hostile to the player's faction by default (relation -30 or below), so nothing is for sale.
        /// </summary>
        public readonly bool Hostile = hostile;

        /// <summary>
        /// The building TYPES (what each housed building is used as) with counts; the exact building-to-resident pairing is only an example
        /// (<see cref="Groups"/>).
        /// </summary>
        public readonly SortedDictionary<string, (int Count, string Category, int Ruins, int Sale, List<long> Prices)> Types =
            new(StringComparer.Ordinal);

        /// <summary>
        /// The buildings, grouped by name and what lives in them (the pairing example); key "name|role|kind".
        /// </summary>
        public readonly SortedDictionary<string, (int Count, int Ruins, int Sale, List<long> Prices, string Role, string Kind)> Groups =
            new(StringComparer.Ordinal);

        /// <summary>
        /// One entry per house for sale: building name, price, whether it is a ruin.
        /// </summary>
        public readonly List<object?> ForSale = [];

        /// <summary>
        /// Wall pieces counted (not listed as buildings).
        /// </summary>
        public int Walls;

        /// <summary>
        /// Gates counted (not listed as buildings).
        /// </summary>
        public int Gates;
    }

    /// <summary>
    /// The town detail pass of <see cref="Detail"/>: per placed town its building types, the pairing example, walls and gates, houses for sale and
    /// the shops, with one row per vendor list the shops roll their stock from (shared by every shop that uses it).
    /// </summary>
    sealed class TownDetailPass
    {
        /// <summary>
        /// The game data the rows are read from.
        /// </summary>
        readonly GameDatabase _db;

        /// <summary>
        /// The export's id allocator (name, record id to a unique entry id).
        /// </summary>
        readonly Func<string, string, string> _id;

        /// <summary>
        /// Each town instance's buildings, as <see cref="TownBuildings.Build"/> places them at boot.
        /// </summary>
        readonly Dictionary<string, List<TownBuilding>> _buildings;

        /// <summary>
        /// The player faction's record id, null when the data has no player faction.
        /// </summary>
        readonly string? _playerId;

        /// <summary>
        /// The player faction, null when the data has none.
        /// </summary>
        readonly FactionData? _player;

        /// <summary>
        /// Cache for <see cref="TownBuildings.FunctionOf"/>: building record id to its function record (or null).
        /// </summary>
        readonly Dictionary<string, GameRecord?> _functions = new(StringComparer.Ordinal);

        /// <summary>
        /// Vendor list record id to its row, made on first use.
        /// </summary>
        readonly Dictionary<string, Row> _vendorRows = new(StringComparer.Ordinal);

        /// <summary>
        /// Every squad template by id (the first of a duplicated id wins).
        /// </summary>
        public readonly Dictionary<string, SquadTemplate> Templates = new(StringComparer.Ordinal);

        /// <summary>
        /// One row per trader and town, in the order the towns are described.
        /// </summary>
        public readonly List<Row> Shops = [];

        /// <summary>
        /// Places the towns' buildings (ruins applied) and reads the player faction and the squad templates.
        /// </summary>
        public TownDetailPass(GameDatabase db, WorldLevelData world, Func<string, string, string> id)
        {
            _db = db;
            _id = id;
            var layouts = new BuildingLayouts(db, world.Interiors);
            ResidentRuins.Apply(db, world, layouts);
            _buildings = TownBuildings.Build(db, world, layouts, (_, _) => 0f);
            _playerId = db.Find(FactionData.PlayerId) is not null ? FactionData.PlayerId : null;
            _player = _playerId is null ? null : FactionData.From(db.Find(_playerId)!);
            foreach (var t in SquadTemplate.LoadAll(db)) Templates.TryAdd(t.Id, t);
        }

        /// <summary>
        /// The vendor list rows made so far, by name then id.
        /// </summary>
        public List<Row> VendorRows() =>
            [.. _vendorRows.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal)];

        /// <summary>
        /// Fills one town's row: building types, the pairing example, walls, gates, shops and houses for sale; adds its shop rows.
        /// </summary>
        public void Describe(PlacedTown placed)
        {
            var (placement, town, row) = placed;
            var faction = town.Faction is { } fid && _db.Find(fid) is { Type: FcsRecordType.Faction } fr ? FactionData.From(fr) : null;
            var list = _buildings.GetValueOrDefault(placement.InstanceId) ?? [];
            var housed = ResidentHomes.Assign(_db, town, faction, list, _playerId);
            var homeOf = new Dictionary<string, SquadTemplate>(StringComparer.Ordinal);
            foreach (var (b, t) in housed) homeOf[b.Key] = t;
            bool saleAllowedTown = town.Type is TownType.Outpost or TownType.Town or TownType.Village;
            var tally = new TownTally(town, faction, homeOf, saleAllowedTown, Hostile(faction));

            foreach (var b in list) Count(tally, b);
            var buildingRows = BuildingRows(tally);
            var shopNames = AddShops(tally, row);
            WriteTown(tally, row, buildingRows, shopNames);
        }

        /// <summary>
        /// Whether <paramref name="faction"/> is hostile to the player's faction by default (its explicit relation, else the lower default).
        /// </summary>
        bool Hostile(FactionData? faction)
        {
            bool hostile = false;
            if (faction is not null && _player is not null)
            {
                int rel = faction.Relations.TryGetValue(_playerId!, out int r) ? r : Math.Min(faction.DefaultRelation, _player.DefaultRelation);
                hostile = rel <= -30;
            }
            return hostile;
        }

        /// <summary>
        /// Counts one building into the town's tallies: a wall or gate by kind, a housed, shop or bar building by type and group with its sale price.
        /// </summary>
        void Count(TownTally tally, TownBuilding b)
        {
            if (b.IsWall)
            {
                tally.Walls++;
                return;
            }

            if (b.IsGate)
            {
                tally.Gates++;
                return;
            }

            var town = tally.Town;
            tally.HomeOf.TryGetValue(b.Key, out var home);
            // a housed building is what its residents make of it; the exterior layout's flags only speak for an empty one
            bool shop = home is null ? b.IsShop : home.HasShop;
            bool bar = !shop && (home is null ? b.IsBar : town.BarSquads.Any(l => l.Id == home.Id));
            if (!b.CanHouse && !shop && !bar) return;
            string role = home is null ? "" : ResidentRuins.DisplayName(home) ?? home.Name;
            string kind = shop ? "shop" : bar ? "bar" : home is null ? "house" : "building";
            string name = _db.Find(b.BuildingId)?.Name ?? b.Name;
            long? price = SaleOf(_db, b, home, tally.Faction, town, tally.SaleAllowed && !tally.Hostile, _functions);
            string type = role.Length > 0 ? role : "Unassigned building (no resident listed)";
            string category = home is null ? "unassigned" : town.BarSquads.Any(l => l.Id == home.Id)
                || role.Equals("Bar", StringComparison.OrdinalIgnoreCase) ? "bar" : home.HasShop ? "shop" : SlaveShop(home) ? "slave shop"
                : "other";
            CountType(tally, type, category, b.Destroyed, price);
            CountGroup(tally, name, role, kind, b.Destroyed, price);
        }

        /// <summary>
        /// Adds one building to its type's count, ruins and sale prices.
        /// </summary>
        static void CountType(TownTally tally, string type, string category, bool destroyed, long? price)
        {
            var ty = tally.Types.GetValueOrDefault(type, (0, category, 0, 0, []));
            ty.Count++;
            if (destroyed) ty.Ruins++;
            if (price is { } tp)
            {
                ty.Sale++;
                ty.Prices.Add(tp);
            }

            tally.Types[type] = ty;
        }

        /// <summary>
        /// Adds one building to its name-role-kind group, and to the houses for sale when it has a price.
        /// </summary>
        static void CountGroup(TownTally tally, string name, string role, string kind, bool destroyed, long? price)
        {
            string key = $"{name}|{role}|{kind}";
            var g = tally.Groups.GetValueOrDefault(key, (0, 0, 0, [], role, kind));
            g.Count++;
            if (destroyed) g.Ruins++;
            if (price is { } p)
            {
                g.Sale++;
                g.Prices.Add(p);
                tally.ForSale.Add(new SortedDictionary<string, object?> { ["building"] = name, ["price"] = (int)p, ["ruin"] = destroyed });
            }
            tally.Groups[key] = g;
        }

        /// <summary>
        /// The pairing example's rows: one per building group with its count, use, ruins and price range.
        /// </summary>
        static List<object?> BuildingRows(TownTally tally)
        {
            var buildingRows = new List<object?>();
            foreach (var (key, g) in tally.Groups)
            {
                var d = new SortedDictionary<string, object?> { ["name"] = key.Split('|')[0], ["count"] = g.Count, ["kind"] = g.Kind };
                if (g.Role.Length > 0) d["usedAs"] = g.Role;
                if (g.Ruins > 0) d["ruins"] = g.Ruins;
                if (g.Sale > 0)
                {
                    d["forSale"] = g.Sale;
                    d["priceMin"] = (int)g.Prices.Min();
                    d["priceMax"] = (int)g.Prices.Max();
                }
                buildingRows.Add(d);
            }
            return buildingRows;
        }

        /// <summary>
        /// Shops: every resident or bar template that trades, with the building that houses it when the assignment gave it one. Adds a shop row
        /// per trader with stock and returns the traders' template names.
        /// </summary>
        List<string> AddShops(TownTally tally, Row row)
        {
            var traders = Traders(tally);
            var shopNames = new List<string>();
            foreach (var (tpl, source) in traders) AddShop(tally, row, tpl, source, shopNames);
            return shopNames;
        }

        /// <summary>
        /// The town's trading resident and bar templates (shops and slave shops), each once, with where the town lists it.
        /// </summary>
        List<(SquadTemplate Template, string Source)> Traders(TownTally tally)
        {
            var town = tally.Town;
            var traders = new List<(SquadTemplate Template, string Source)>();
            foreach (var l in ResidentHomes.Entries(town, tally.Faction).Select(e => (e, "resident")).Concat(town.BarSquads.Select(e => (e, "bar"))))
            {
                if (Templates.TryGetValue(l.e.Id, out var tpl) && (tpl.HasShop || SlaveShop(tpl)) && traders.All(x => x.Template.Id != tpl.Id))
                    traders.Add((tpl, l.Item2));
            }
            return traders;
        }

        /// <summary>
        /// Adds the shop row of one trader (none when it has neither vendor lists nor slaves), and counts a trader nobody could house once in the
        /// town's types.
        /// </summary>
        void AddShop(TownTally tally, Row row, SquadTemplate tpl, string source, List<string> shopNames)
        {
            var town = tally.Town;
            var listNames = new List<string>();
            var vendors = ShopVendors(tpl, listNames);
            bool slaves = SlaveShop(tpl);
            if (vendors.Count == 0 && !slaves) return;
            string shopType = ResidentRuins.DisplayName(tpl) ?? tpl.Name;
            shopNames.Add(tpl.Name);
            if (!tally.Types.Any(t => t.Key == shopType))   // a trader nobody could house still counts once
                tally.Types[shopType] = (1, slaves ? "slave shop"
                    : town.BarSquads.Any(l => l.Id == tpl.Id) || shopType.Equals("Bar", StringComparison.OrdinalIgnoreCase) ? "bar"
                    : "shop", 0, 0, []);
            var data = ShopData(town, row, tpl, shopType, slaves, source, vendors);
            if (slaves) data["slaves"] = SlavesOf(_db, tpl, Templates);
            string stock = slaves ? "sells slaves (" + string.Join("; ", ((IEnumerable<object?>)data["slaves"]!)
                .OfType<SortedDictionary<string, object?>>()
                .Select(x => $"{x["template"]}: {x["count"]}")) + ")"
                + (listNames.Count > 0 ? "; also stock from " + string.Join(", ", listNames) : "") : "stock rolled from "
                + string.Join(", ", listNames);
            Shops.Add(new Row(_id($"{tpl.Name} {town.Name}", tpl.Id), $"{tpl.Name} ({town.Name})",
                "trader", FactionNameOf(_db, tpl.Faction) is { Length: > 0 } tf ? tf : null, row.Zone, row.X, row.Z, "Inferred",
                $"{(slaves ? "Slave shop" : "Trader")} ({shopType}) in {town.Name}; {stock}. Position is the town's.", tpl.Id, data));
        }

        /// <summary>
        /// A trader's vendor lists with their weights and the approximate item count each gives of the fill amount; the list names go into
        /// <paramref name="listNames"/>.
        /// </summary>
        List<object?> ShopVendors(SquadTemplate tpl, List<string> listNames)
        {
            var vendors = new List<object?>();
            long weightSum = tpl.Vendors.Where(v => _db.Find(v.Id) is { Type: FcsRecordType.VendorList }).Sum(v => (long)(v.V0 == 0 ? 100 : v.V0));
            foreach (var v in tpl.Vendors)
            {
                if (_db.Find(v.Id) is not { Type: FcsRecordType.VendorList } vr) continue;
                var vrow = VendorRow(v.Id);
                listNames.Add(vr.Name);
                vendors.Add(new SortedDictionary<string, object?> { ["id"] = vrow.Id, ["list"] = vr.Name, ["weight"] = v.V0 == 0 ? 100 : v.V0,
                    ["approxItems"] = (int)Math.Round((double)(v.V0 == 0 ? 100 : v.V0) / Math.Max(1, weightSum) * tpl.VendorsFillTotalAmount) });
            }
            return vendors;
        }

        /// <summary>
        /// A shop row's data: town, squad, type, vendor lists and the template's trading numbers.
        /// </summary>
        static SortedDictionary<string, object?> ShopData(TownData town, Row row, SquadTemplate tpl, string shopType, bool slaves, string source,
            List<object?> vendors) => new()
        {
            ["town"] = town.Name,
            ["townId"] = row.Id,
            ["squad"] = tpl.Name,
            ["type"] = shopType,
            ["slaveShop"] = slaves,
            ["via"] = source,
            ["vendors"] = vendors,
            ["fillItems"] = tpl.VendorsFillTotalAmount,
            ["buyMult"] = tpl.BuyMult,
            ["buysStolen"] = tpl.BuysStolen,
            ["buysIllegal"] = tpl.BuysIllegal,
            ["refreshHours"] = tpl.VendorsRefreshTime,
            ["record"] = tpl.Id,
        };

        /// <summary>
        /// The row of a vendor list (made once, then shared): its possible items with their pick chance, the money item and the manufacturers.
        /// </summary>
        Row VendorRow(string recordId)
        {
            if (_vendorRows.TryGetValue(recordId, out var known)) return known;
            var record = _db.Find(recordId)!;
            var list = VendorList.From(_db, record);
            double total = list.Entries.Sum(e => (double)e.Weight);
            var rows = new List<object?>();
            foreach (var e in list.Entries.Take(400))
            {
                if (_db.Find(e.Id) is not { } item || item.Name.Length == 0) continue;
                rows.Add(new SortedDictionary<string, object?>
                {
                    ["item"] = item.Name,
                    ["type"] = e.Type == FcsRecordType.Research ? "blueprint" : e.Type.ToString().ToLowerInvariant(),
                    ["pct"] = total > 0 ? (float)Math.Round(e.Weight / total * 100, 1) : 0f,
                });
            }
            var makers = Names(_db, list.Manufacturers.Select(m => m.Id));
            var data = new SortedDictionary<string, object?>
            {
                ["itemsCount"] = list.ItemsCount,
                ["items"] = rows,
                ["itemTotal"] = list.Entries.Count,
                ["moneyChance"] = list.MoneyItemProb,
                ["moneyMin"] = list.MoneyItemMin,
                ["moneyMax"] = list.MoneyItemMax,
                ["manufacturers"] = makers,
                ["record"] = recordId,
            };
            var row = new Row(_id(record.Name, recordId), record.Name, "vendor-list", null, null, null, null, "Observed",
                $"Vendor list: {list.Entries.Count} possible items (pct is the relative chance of one pick; a shop picks around its template's "
                + $"fill amount, split between its lists by "
                + $"weight){(makers.Count > 0 ? ", weapons made by " + string.Join(", ", makers) : "")}.", recordId, data);
            _vendorRows[recordId] = row;
            return row;
        }

        /// <summary>
        /// Writes the town's tallies into its row: building types, the pairing example, walls, gates, shops, houses for sale and the sale note.
        /// </summary>
        static void WriteTown(TownTally tally, Row row, List<object?> buildingRows, List<string> shopNames)
        {
            var typeRows = TypeRows(tally);
            row.Data["buildingTypes"] = typeRows;
            row.Data["building_assignment_example"] = new SortedDictionary<string, object?>
            {
                ["note"] = "One possible pairing of building models with residents, as this exporter's fixed housing rule gives it. "
                    + "The original rerolls which building holds which shop or resident at every new game, and mods change the lists, "
                    + "so do not treat it as the town's layout; only the TYPES in buildingTypes are reliable.",
                ["buildings"] = buildingRows,
            };
            row.Data["walls"] = tally.Walls;
            row.Data["gates"] = tally.Gates;
            row.Data["shops"] = shopNames;
            row.Data["forSale"] = tally.ForSale;
            if (tally.Hostile && tally.SaleAllowed)
                row.Data["saleNote"] = "the owning faction is hostile to your faction by default, so the game offers no houses here";
        }

        /// <summary>
        /// The building type rows: one per type with its count, category, ruins and price range.
        /// </summary>
        static List<object?> TypeRows(TownTally tally)
        {
            var typeRows = new List<object?>();
            foreach (var (type, g) in tally.Types)
            {
                var d = new SortedDictionary<string, object?> { ["type"] = type, ["count"] = g.Count, ["category"] = g.Category };
                if (g.Ruins > 0) d["ruins"] = g.Ruins;
                if (g.Sale > 0)
                {
                    d["forSale"] = g.Sale;
                    d["priceMin"] = (int)g.Prices.Min();
                    d["priceMax"] = (int)g.Prices.Max();
                }
                typeRows.Add(d);
            }
            return typeRows;
        }
    }

    #endregion

    #region Town overrides

    /// <summary>
    /// One town's override variants while <see cref="TownOverridePass"/> reads them.
    /// </summary>
    sealed class OverrideTally
    {
        /// <summary>
        /// One entry per variant town: what it becomes, its faction, conditions and record.
        /// </summary>
        public readonly List<object?> Variants = [];

        /// <summary>
        /// The characters any variant's conditions name.
        /// </summary>
        public readonly SortedSet<string> AllChars = new(StringComparer.Ordinal);

        /// <summary>
        /// The factions any variant's conditions name.
        /// </summary>
        public readonly SortedSet<string> AllFacs = new(StringComparer.Ordinal);

        /// <summary>
        /// One line per variant: what the town becomes and when.
        /// </summary>
        public readonly List<string> Summary = [];
    }

    /// <summary>
    /// The town override pass of <see cref="Detail"/>: per town with override variants, what it becomes under which world states.
    /// </summary>
    sealed class TownOverridePass(GameDatabase db, Func<string, string, string> id)
    {
        /// <summary>
        /// World state record id to its condition text, the negated text and the characters and factions it names (made on first use).
        /// </summary>
        readonly Dictionary<string, (string Text, string Negated, List<string> Characters, List<string> Factions)> _stateText =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Character name to the towns a variant whose conditions name them changes (and into what).
        /// </summary>
        readonly Dictionary<string, List<(string Town, string Variant)>> _overridesOf = new(StringComparer.Ordinal);

        /// <summary>
        /// One override row per town with at least one variant record; the town rows get their override summary.
        /// </summary>
        public List<Row> Rows(List<PlacedTown> placed)
        {
            var overrides = new List<Row>();
            foreach (var (placement, town, row) in placed)
            {
                if (TownRow(placement, town, row) is { } overrideRow) overrides.Add(overrideRow);
            }
            return overrides;
        }

        /// <summary>
        /// A town's override row (null when it lists no override or none of them is a town record).
        /// </summary>
        Row? TownRow(TownPlacement placement, TownData town, Row row)
        {
            if (town.OverrideTown.Count == 0) return null;
            var tally = new OverrideTally();
            foreach (var link in town.OverrideTown) AddVariant(tally, town, link);
            if (tally.Variants.Count == 0) return null;
            var data = new SortedDictionary<string, object?>
            {
                ["town"] = town.Name,
                ["townId"] = row.Id,
                ["variants"] = tally.Variants,
                ["characters"] = tally.AllChars.ToList(),
                ["factions"] = tally.AllFacs.ToList(),
                ["rule"] = "Checked when the town's zone loads; the variant with the most conditions that all hold wins (ties: first listed); "
                    + "skipped for towns of your faction.",
                ["record"] = placement.TownId,
            };
            row.Data["overrides"] = tally.Summary;
            return new Row(id($"{town.Name} overrides", placement.TownId), $"{town.Name} overrides",
                "override", FactionNameOf(db, town.Faction) is { Length: > 0 } fn ? fn : null, row.Zone, row.X, row.Z, "Observed",
                $"{tally.Variants.Count} variant(s) of {town.Name}: " + string.Join(" | ", tally.Summary), placement.TownId, data);
        }

        /// <summary>
        /// Adds one override variant of <paramref name="town"/>: the town it becomes, the world states that must (not) hold, and who they name.
        /// </summary>
        void AddVariant(OverrideTally tally, TownData town, RecordLink link)
        {
            if (db.Find(link.Id) is not { Type: FcsRecordType.Town } cr) return;
            var cand = TownData.From(cr);
            var conds = new List<object?>();
            var used = cand.WorldState.Where(w => db.Find(w.Id) is { Type: FcsRecordType.WorldEventState }).DistinctBy(w => w.Id).ToList();
            var chars = new SortedSet<string>(StringComparer.Ordinal);
            var facs = new SortedSet<string>(StringComparer.Ordinal);
            var texts = new List<string>();
            foreach (var w in used)
            {
                var (text, neg, cs, fs) = State(w.Id);
                string t = w.V0 == 1 ? text : neg;
                texts.Add(t);
                conds.Add(new SortedDictionary<string, object?> { ["state"] = db.Find(w.Id)!.Name, ["mustHold"] = w.V0 == 1, ["text"] = t });
                foreach (var c in cs) chars.Add(c);
                foreach (var f in fs) facs.Add(f);
            }
            foreach (var c in chars) tally.AllChars.Add(c);
            foreach (var f in facs) tally.AllFacs.Add(f);
            string newFaction = cand.Faction is { } cf && cf != town.Faction ? FactionNameOf(db, cf) : "";
            tally.Variants.Add(new SortedDictionary<string, object?>
            {
                ["becomes"] = cand.Name,
                ["type"] = cand.Type.ToString(),
                ["faction"] = newFaction.Length > 0 ? newFaction : FactionNameOf(db, town.Faction),
                ["factionChanges"] = newFaction.Length > 0,
                ["weight"] = used.Count,
                ["when"] = texts,
                ["conditions"] = conds,
                ["record"] = cr.StringId,
                ["furtherOverrides"] = cand.OverrideTown.Count,
            });
            tally.Summary.Add($"{cand.Name} ({cand.Type}{(newFaction.Length > 0 ? ", " + newFaction : "")}) "
                + $"when {(texts.Count == 0 ? "no condition" : string.Join("; ", texts))}");
            foreach (var c in chars)
            {
                if (!_overridesOf.TryGetValue(c, out var l)) _overridesOf[c] = l = [];
                l.Add((town.Name, cand.Name));
            }
        }

        /// <summary>
        /// A world state's condition as text, its negation, and the characters and factions it names (cached per state).
        /// </summary>
        (string Text, string Negated, List<string> Characters, List<string> Factions) State(string stateId)
        {
            if (_stateText.TryGetValue(stateId, out var known)) return known;
            var chars = new List<string>();
            var facs = new List<string>();
            var s = WorldEventStateData.From(db, db.Find(stateId)!);
            var parts = new List<string>();
            var negated = new List<string>();
            foreach (var (c, v) in s.NpcIs)
            {
                string n = db.Find(c)?.Name ?? c;
                chars.Add(n);
                parts.Add($"{n} is {Unique(v)}");
                negated.Add($"{n} is not {Unique(v)}");
            }

            foreach (var (c, v) in s.NpcIsNot)
            {
                string n = db.Find(c)?.Name ?? c;
                chars.Add(n);
                parts.Add($"{n} is not {Unique(v)}");
                negated.Add($"{n} is {Unique(v)}");
            }

            foreach (var (f, v) in s.PlayerAlly)
            {
                string n = db.Find(f)?.Name ?? f;
                facs.Add(n);
                parts.Add($"{n} {(v ? "is" : "is not")} allied with you");
                negated.Add($"{n} {(v ? "is not" : "is")} allied with you");
            }

            foreach (var (f, v) in s.PlayerEnemy)
            {
                string n = db.Find(f)?.Name ?? f;
                facs.Add(n);
                parts.Add($"{n} {(v ? "is" : "is not")} hostile to you");
                negated.Add($"{n} {(v ? "is not" : "is")} hostile to you");
            }

            string involved = s.PlayerInvolvement ? " (you took part)" : "";
            string text = parts.Count == 0 ? "(no condition)" : string.Join(" and ", parts) + involved;
            string neg = parts.Count == 0 ? "(never)" : parts.Count == 1 && involved.Length == 0 ? negated[0] : $"not ({text})";
            return _stateText[stateId] = (text, neg, chars, facs);
        }

        /// <summary>
        /// A unique character's world-state value as a word: 0 dead, 1 alive, 2 imprisoned.
        /// </summary>
        static string Unique(int v) => v switch { 0 => "dead", 1 => "alive", 2 => "imprisoned", _ => "state " + v };
    }

    #endregion

    #region Slaves and factions

    /// <summary>
    /// A resident template that runs a slave shop: it trades and its home is stocked with slaves (a `slaves` squad) rather than, or besides, vendor
    /// lists.
    /// </summary>
    static bool SlaveShop(SquadTemplate t) => t.IsTrader && t.Slaves.Count > 0;

    /// <summary>
    /// The squads a slave shop holds for sale: each `slaves` template with the count range its random picks give, and who they are drawn from.
    /// </summary>
    static List<object?> SlavesOf(GameDatabase db, SquadTemplate tpl, Dictionary<string, SquadTemplate> templates)
    {
        var rows = new List<object?>();
        foreach (var l in tpl.Slaves)
        {
            if (!templates.TryGetValue(l.Id, out var s)) continue;
            int fixedCount = s.Squad.Concat(s.Squad2).Sum(x => Math.Max(0, x.V0));
            int min = fixedCount + s.NumRandomChars, max = fixedCount + Math.Max(s.NumRandomChars, s.NumRandomCharsMax);
            rows.Add(new SortedDictionary<string, object?>
            {
                ["template"] = s.Name,
                ["count"] = min == max ? min.ToString(CultureInfo.InvariantCulture) : $"{min}-{max}",
                ["drawnFrom"] = Names(db, s.Squad.Concat(s.Squad2).Concat(s.ChooseFrom).Select(x => x.Id)),
            });
        }
        return rows;
    }

    /// <summary>
    /// The name of faction record <paramref name="id"/>, "" when the id is null or names no record.
    /// </summary>
    static string FactionNameOf(GameDatabase db, string? id) => id is not null && db.Find(id) is { } f ? f.Name : "";

    /// <summary>
    /// The price the game quotes for a town house (docs/game/ui-screens.md "Selected building"), or null when it is not for sale.
    /// </summary>
    static long? SaleOf(GameDatabase db, TownBuilding b, SquadTemplate? home, FactionData? owner, TownData town, bool allowed,
        Dictionary<string, GameRecord?> functions)
    {
        if (!allowed || owner is null || b.IsGate || b.IsWall || db.Find(b.BuildingId) is not { } record || record.GetBool("is wall gate")
                || record.GetBool("links together"))
            return null;
        int? function = TownBuildings.FunctionOf(db, record, functions)?.GetInt("function");
        if (function != 19 && !(function is null && record.GetReferences("interior").Count > 0)) return null;
        var state = b.LevelState;
        bool sale = state?.Bools.TryGetValue("sell home", out bool levelSale) == true ? levelSale
            : db.Find(b.ResidentsTemplate)?.GetBool("sell home") ?? state is null;
        if (!sale || (state?.GetBool("is inside building") ?? false)) return null;
        float multiplier = db.Find(owner.Id)?.GetFloat("building cost mult", 1) ?? 1;
        float price = (b.Destroyed ? 0.375f : 1) * BuildingBuildData.From(record).Target * multiplier * 400;
        return float.IsFinite(price) && price >= 0 && price <= int.MaxValue ? (long)price : null;
    }

    #endregion

    #region Characters

    /// <summary>
    /// Unique and bounty characters (humans and animals): faction and where it comes from, home towns, bounty, whether they can join, the
    /// campaigns they lead.
    /// </summary>
    static List<Row> Characters(GameDatabase db, List<PlacedTown> placed, Dictionary<string, SquadTemplate> templates,
        Func<string, string, string> id) =>
        new CharacterPass(db, placed, templates, id).Rows();

    /// <summary>
    /// One character's facts while <see cref="CharacterPass"/> gathers them.
    /// </summary>
    sealed class CharacterEntry(GameRecord record, List<(SquadTemplate Template, string Role)> uses)
    {
        /// <summary>
        /// The character (or animal character) record.
        /// </summary>
        public readonly GameRecord Record = record;

        /// <summary>
        /// The squad templates that name the character, with the role in each (leader, member, prisoner).
        /// </summary>
        public readonly List<(SquadTemplate Template, string Role)> Uses = uses;

        /// <summary>
        /// The character's faction name, "" while none is found.
        /// </summary>
        public string Faction = "";

        /// <summary>
        /// Where <see cref="Faction"/> was read: the record, a squad, or a home town.
        /// </summary>
        public string FactionSource = "";

        /// <summary>
        /// One entry per town that lists one of the character's squads (town, position, squad, role, via).
        /// </summary>
        public readonly List<object?> Homes = [];

        /// <summary>
        /// The first home town's position, null when no town lists their squad.
        /// </summary>
        public (float X, float Z)? First;

        /// <summary>
        /// The first home town's name, "" when none.
        /// </summary>
        public string FirstTown = "";
    }

    /// <summary>
    /// The character pass of <see cref="Detail"/>: indexes which squads, towns and campaigns name each character, then makes one row per unique or
    /// bounty character.
    /// </summary>
    sealed class CharacterPass
    {
        /// <summary>
        /// The game data the rows are read from.
        /// </summary>
        readonly GameDatabase _db;

        /// <summary>
        /// The export's id allocator (name, record id to a unique entry id).
        /// </summary>
        readonly Func<string, string, string> _id;

        /// <summary>
        /// Which squad templates name a character, and in which role.
        /// </summary>
        readonly Dictionary<string, List<(SquadTemplate Template, string Role)>> _usedBy = new(StringComparer.Ordinal);

        /// <summary>
        /// Towns that list a template (the town's own lists, and the lists of its override variants), with the town's position.
        /// </summary>
        readonly Dictionary<string, List<(string Town, float X, float Z, string Via)>> _townsOf = new(StringComparer.Ordinal);

        /// <summary>
        /// Campaigns that name a character as special leader.
        /// </summary>
        readonly Dictionary<string, List<string>> _campaignsOf = new(StringComparer.Ordinal);

        /// <summary>
        /// Dialogue record id to whether a line in its tree makes the speaker join (cached; false while being walked).
        /// </summary>
        readonly Dictionary<string, bool> _joinByDialogue = new(StringComparer.Ordinal);

        /// <summary>
        /// Town name to its owning faction's name (the first placed town of a name wins).
        /// </summary>
        readonly Dictionary<string, string> _townFaction = new(StringComparer.Ordinal);

        /// <summary>
        /// The placed towns.
        /// </summary>
        readonly List<PlacedTown> _placed;

        /// <summary>
        /// Builds the squad, town and campaign indexes the rows read.
        /// </summary>
        public CharacterPass(GameDatabase db, List<PlacedTown> placed, Dictionary<string, SquadTemplate> templates, Func<string, string, string> id)
        {
            _db = db;
            _placed = placed;
            _id = id;
            IndexSquads(templates);
            IndexTowns();
            IndexCampaigns();
            foreach (var pt in placed) if (pt.Row.Faction is { Length: > 0 } pf) _townFaction.TryAdd(pt.Town.Name, pf);
        }

        /// <summary>
        /// One row per named unique or bounty character, by name then record id.
        /// </summary>
        public List<Row> Rows()
        {
            var rows = new List<Row>();
            foreach (var record in _db.OfType(FcsRecordType.Character).Concat(_db.OfType(FcsRecordType.AnimalCharacter))
                    .OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.StringId, StringComparer.Ordinal))
            {
                if (CharacterRow(record) is { } row) rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// Indexes every character a squad template names (leader, members, the leaders and squads of its prisoner templates).
        /// </summary>
        void IndexSquads(Dictionary<string, SquadTemplate> templates)
        {
            foreach (var t in templates.Values)
            {
                if (t.Leader is { } leader) Use(leader.Id, t, "leader");
                foreach (var m in t.Squad) Use(m.Id, t, "member");
                foreach (var m in t.Squad2) Use(m.Id, t, "member");
                foreach (var m in t.ChooseFrom) Use(m.Id, t, "member");
                foreach (var m in t.Prisoners)
                {
                    if (templates.TryGetValue(m.Id, out var pt))
                    {
                        if (pt.Leader is { } pl) Use(pl.Id, t, "prisoner");
                        foreach (var x in pt.Squad) Use(x.Id, t, "prisoner");
                    }
                }
            }
        }

        /// <summary>
        /// Records that template <paramref name="t"/> names <paramref name="character"/> in <paramref name="role"/> (each pair once).
        /// </summary>
        void Use(string character, SquadTemplate t, string role)
        {
            if (!_usedBy.TryGetValue(character, out var l)) _usedBy[character] = l = [];
            if (!l.Any(x => x.Template.Id == t.Id && x.Role == role)) l.Add((t, role));
        }

        /// <summary>
        /// Indexes the towns that list each template: their resident and bar lists, and those of their override variants.
        /// </summary>
        void IndexTowns()
        {
            foreach (var pt in _placed)
            {
                foreach (var l in pt.Town.Residents.Concat(pt.Town.BarSquads)) Home(l.Id, pt, "");
                foreach (var o in pt.Town.OverrideTown)
                {
                    if (_db.Find(o.Id) is { Type: FcsRecordType.Town } ovr)
                    {
                        var ot = TownData.From(ovr);
                        foreach (var l in ot.Residents.Concat(ot.BarSquads)) Home(l.Id, pt, $"as {ot.Name}");
                    }
                }
            }
        }

        /// <summary>
        /// Records that town <paramref name="pt"/> lists <paramref name="template"/> (each town and via once).
        /// </summary>
        void Home(string template, PlacedTown pt, string via)
        {
            if (!_townsOf.TryGetValue(template, out var l)) _townsOf[template] = l = [];
            if (!l.Any(x => x.Town == pt.Town.Name && x.Via == via))
                l.Add((pt.Town.Name, pt.Placement.Position.X, pt.Placement.Position.Z, via));
        }

        /// <summary>
        /// Indexes the faction campaigns by the characters they name as special leader.
        /// </summary>
        void IndexCampaigns()
        {
            foreach (var c in _db.OfType(FcsRecordType.FactionCampaign))
            {
                foreach (var r in c.GetReferences("special leader"))
                {
                    if (!_campaignsOf.TryGetValue(r.TargetStringId, out var l)) _campaignsOf[r.TargetStringId] = l = [];
                    if (c.Name.Length > 0) l.Add(c.Name);
                }
            }
        }

        /// <summary>
        /// A character's row, null when it is unnamed or neither unique nor carries a spawn bounty.
        /// </summary>
        Row? CharacterRow(GameRecord record)
        {
            if (record.Name.Length == 0) return null;
            bool unique = record.GetBool("unique");
            int chance = record.GetInt("bounty chance"), amount = record.GetInt("bounty amount"),
                fuzz = Math.Max(0, record.GetInt("bounty amount fuzz"));
            bool bounty = chance > 0 && amount > 0;
            if (!unique && !bounty) return null;
            var entry = new CharacterEntry(record, _usedBy.GetValueOrDefault(record.StringId) ?? []);
            FactionFromRecordOrSquads(entry);
            FindHomes(entry);
            FactionFromHomes(entry);
            var bountyData = new SortedDictionary<string, object?>();
            string bountyNote = bounty ? Bounty(record, chance, amount, fuzz, bountyData) : "";
            var uses = entry.Uses;
            bool join = record.Type == FcsRecordType.Character && CanJoin(record, uses.Where(u => u.Role == "leader").Select(u => u.Template));
            var camps = _campaignsOf.GetValueOrDefault(record.StringId) ?? [];
            var data = new SortedDictionary<string, object?>
            {
                ["factionSource"] = entry.FactionSource,
                ["unique"] = unique,
                ["recruitable"] = join,
                ["squads"] = uses.Select(u => $"{u.Template.Name} ({u.Role})").Distinct().Take(12).ToList(),
                ["homes"] = entry.Homes.Take(12).ToList(),
                ["campaigns"] = camps.Distinct().Take(8).ToList(),
                ["replacement"] = Names(_db, record.GetReferences("unique replacement spawn").Select(r => r.TargetStringId)),
                ["record"] = record.StringId,
            };
            if (bounty) data["bounty"] = bountyData;
            return RowOf(entry, unique, bountyNote, join, camps, data);
        }

        /// <summary>
        /// Faction: the character record's own, else the squad they lead or belong to (a prisoner's squad belongs to the captor, so it does not
        /// count).
        /// </summary>
        void FactionFromRecordOrSquads(CharacterEntry entry)
        {
            string faction = entry.Record.GetReferences("faction") is { Count: > 0 } fl ? FactionNameOf(_db, fl[0].TargetStringId) : "";
            string factionSource = faction.Length > 0 ? "character record" : "";
            if (faction.Length == 0)
            {
                foreach (var u in entry.Uses.Where(u => u.Role != "prisoner").OrderBy(u => u.Role == "leader" ? 0 : 1))
                {
                    if (FactionNameOf(_db, u.Template.Faction) is { Length: > 0 } sf)
                    {
                        faction = sf;
                        factionSource = $"squad {u.Template.Name} ({u.Role})";
                        break;
                    }
                }
            }
            (entry.Faction, entry.FactionSource) = (faction, factionSource);
        }

        /// <summary>
        /// The towns that list the character's squads, each town, kind and role once, best first: living in a town beats being held there; the
        /// town's own list beats an override variant's.
        /// </summary>
        void FindHomes(CharacterEntry entry)
        {
            var seenHome = new HashSet<string>(StringComparer.Ordinal);
            var found = new List<(int Rank, string Town, float X, float Z, string Via, string Squad, string Role)>();
            foreach (var (t, role) in entry.Uses)
            {
                foreach (var h in _townsOf.GetValueOrDefault(t.Id) ?? [])
                    found.Add(((role == "prisoner" ? 2 : 0) + (h.Via.Length > 0 ? 1 : 0), h.Town, h.X, h.Z, h.Via, t.Name, role));
            }
            foreach (var (_, town, x, z, via, squad, role) in found.OrderBy(f => f.Rank).ThenBy(f => f.Town, StringComparer.Ordinal))
            {
                if (!seenHome.Add(town + "|" + (via.Length > 0) + "|" + role)) continue;
                entry.First ??= (x, z);
                if (entry.FirstTown.Length == 0) entry.FirstTown = town;
                entry.Homes.Add(new SortedDictionary<string, object?> { ["town"] = town, ["x"] = (int)Math.Round(x), ["z"] = (int)Math.Round(z),
                    ["squad"] = squad, ["role"] = role, ["via"] = via.Length == 0 ? "town lists the squad" : "town override " + via });
            }
        }

        /// <summary>
        /// Faction, failing the record and the squads: the faction of the first home town they are not held in.
        /// </summary>
        void FactionFromHomes(CharacterEntry entry)
        {
            if (entry.Faction.Length != 0) return;
            foreach (var h in entry.Homes.OfType<SortedDictionary<string, object?>>().Where(h => (string)h["role"]! != "prisoner"))
            {
                if (_townFaction.GetValueOrDefault((string)h["town"]!) is { Length: > 0 } tf)
                {
                    entry.Faction = tf;
                    entry.FactionSource = $"home town {h["town"]}";
                    break;
                }
            }
        }

        /// <summary>
        /// Fills <paramref name="bountyData"/> with the spawn bounty (chance, amount range, the factions that post it) and returns its note.
        /// </summary>
        string Bounty(GameRecord record, int chance, int amount, int fuzz, SortedDictionary<string, object?> bountyData)
        {
            var refs = record.GetReferences("bounty factions");
            var facs = refs.Count > 0 ? [.. refs.Select(r => FactionNameOf(_db, r.TargetStringId)).Where(n => n.Length > 0)]
                : new List<string> { FactionNameOf(_db, "defaultEmpireFactionSID") is { Length: > 0 } de ? de + " (default)"
                    : "the default empire faction" };
            bountyData["chancePct"] = chance;
            bountyData["amount"] = amount;
            bountyData["amountMax"] = amount + fuzz;
            bountyData["factions"] = facs;
            return $" Bounty {(fuzz > 0 ? $"{amount} to {amount + fuzz}" : amount.ToString(CultureInfo.InvariantCulture))} "
                + $"with {string.Join(", ", facs)} ({(chance >= 100 ? "always" : chance + " % of spawns")}).";
        }

        /// <summary>
        /// The character's row: positioned at the first home town (if any), with a note on faction, bounty, homes, recruiting and campaigns.
        /// </summary>
        Row RowOf(CharacterEntry entry, bool unique, string bountyNote, bool join, List<string> camps, SortedDictionary<string, object?> data)
        {
            var record = entry.Record;
            var homes = entry.Homes;
            var first = entry.First;
            string faction = entry.Faction;
            bool animal = record.Type == FcsRecordType.AnimalCharacter;
            string where = first is null ? " No town lists their squad in the data (roaming, campaign, or placed another way)."
                : homes.Count == 1 ? $" Home: {entry.FirstTown} (position is the town's, not live)."
                : $" Homes: {string.Join(", ", homes.OfType<SortedDictionary<string, object?>>().Select(h => (string)h["town"]!).Distinct().Take(6))} "
                    + $"(position is the first town's, not live).";
            return new Row(_id(record.Name, record.StringId),
                record.Name, animal ? "animal-character" : "character", faction.Length == 0 ? null : faction,
                first is { } f0 ? $"zone-{WorldLayout.ZoneOf(f0.X, f0.Z).X}-{WorldLayout.ZoneOf(f0.X, f0.Z).Y}" : null,
                first?.X, first?.Z, first is null ? "Observed" : "Inferred",
                (unique ? "Unique character" : "Character with a spawn bounty")
                    + (faction.Length > 0 ? $" of {faction} (faction from {entry.FactionSource})" : "") + "."
                    + bountyNote + where + (join ? " Has a dialogue line that makes them join your squad (recruit or hire)." : "")
                    + (camps.Count > 0 ? $" Special leader of campaign {camps[0]}." : ""),
                record.StringId, data);
        }

        /// <summary>
        /// Whether one of the character's dialogues (their own, their dialogue packages', or the leader dialogue of squads they lead) can make
        /// them join.
        /// </summary>
        bool CanJoin(GameRecord character, IEnumerable<SquadTemplate> asLeader)
        {
            var dialogues = new List<string>();
            dialogues.AddRange(character.GetReferences("dialogue").Select(r => r.TargetStringId));
            foreach (var package in character.GetReferences("dialogue package").Select(r => _db.Find(r.TargetStringId)))
            {
                if (package is not null)
                    dialogues.AddRange(package.GetReferences("dialogs").Select(r => r.TargetStringId));
            }
            foreach (var t in asLeader)
            {
                if (_db.Find(t.Id) is { } tr)
                {
                    foreach (var package in tr.GetReferences("dialog leader").Select(r => _db.Find(r.TargetStringId)))
                    {
                        if (package is not null)
                            dialogues.AddRange(package.GetReferences("dialogs").Select(r => r.TargetStringId));
                    }
                }
            }
            return dialogues.Distinct().Any(Joins);
        }

        /// <summary>
        /// Whether dialogue <paramref name="dialogueId"/>'s tree (lines and interrupts, 30 deep) has an effect with action 3 or 18 (join); cached.
        /// </summary>
        bool Joins(string dialogueId)
        {
            if (_joinByDialogue.TryGetValue(dialogueId, out bool known)) return known;
            _joinByDialogue[dialogueId] = false;
            bool found = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (_db.Find(dialogueId) is { Type: FcsRecordType.Dialogue } d) Walk(d, 0, seen, ref found);
            return _joinByDialogue[dialogueId] = found;
        }

        /// <summary>
        /// One step of <see cref="Joins"/>: checks a dialogue node's effects, then walks its lines and interrupts.
        /// </summary>
        void Walk(GameRecord node, int depth, HashSet<string> seen, ref bool found)
        {
            if (found || depth > 30 || !seen.Add(node.StringId)) return;
            foreach (var e in node.GetReferences("effects"))
            {
                if (_db.Find(e.TargetStringId)?.GetInt("action name") is 3 or 18)
                {
                    found = true;
                    return;
                }
            }
            foreach (string list in new[] { "lines", "interrupt" })
            {
                foreach (var r in node.GetReferences(list))
                    if (_db.Find(r.TargetStringId) is { } next) Walk(next, depth + 1, seen, ref found);
            }
        }
    }

    #endregion

}
