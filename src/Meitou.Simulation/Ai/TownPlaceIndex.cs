using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// A town's public buildings with a general node by zone, its shops, its public bars, its outdoor tables and the guard posts of its
/// standing buildings, as <see cref="TownPlaceIndex.Refresh"/> last found them.
/// </summary>
sealed record TownPlaces(IReadOnlyDictionary<ZoneCoordinate, TownBuilding[]> Wander, IReadOnlyList<TownBuilding> Shops,
    IReadOnlyList<TownBuilding> Bars,
    IReadOnlyList<TownBuilding> OutdoorTables, IReadOnlyList<TownBuilding> Workplaces,
    IReadOnlyDictionary<TownBuilding, ulong> Stock, IReadOnlyList<SeatClaim> Seats, IReadOnlyDictionary<TownBuilding, int> SeatOwners,
    IReadOnlyList<TownBuilding> Public, IReadOnlyList<SeatClaim> Posts);

/// <summary>
/// The towns' places for the AI's squads, by town site (<see cref="TownPlaces"/>): rebuilt in the serial step on the package cadence, read by the
/// think on every worker. Derived, not hashed.
/// </summary>
sealed class TownPlaceIndex(PopulationData data)
{
    /// <summary>
    /// The places of a town not looked at (or of no town): none.
    /// </summary>
    public static readonly TownPlaces None = new(new Dictionary<ZoneCoordinate, TownBuilding[]>(), [], [], [], [],
        new Dictionary<TownBuilding, ulong>(), [], new Dictionary<TownBuilding, int>(), [], []);

    /// <summary>
    /// The BuildingFunction bits a worker looks for (mask <c>0x41a</c> of <c>FUN_1408cac60</c> / <c>FUN_1408cadb0</c>, read as bits,
    /// <b>Observed</b>): MINE, RESEARCH, REFINERY, CRAFTING.
    /// </summary>
    internal const int WorkFunctions = 0x41a;

    /// <summary>
    /// The places of each town looked at, by site: rebuilt in the serial step, read by the think on every worker (derived, not hashed).
    /// </summary>
    readonly Dictionary<int, TownPlaces> _places = [];

    /// <summary>
    /// The workplaces of each town 189 can really work at (<see cref="AiSystem.RealWork"/>), by site: rebuilt with the places (derived, not hashed).
    /// </summary>
    readonly Dictionary<int, IReadOnlyList<TownBuilding>> _realWork = [];

    /// <summary>
    /// The places of town <paramref name="site"/> (<see cref="None"/> when it was not looked at).
    /// </summary>
    public TownPlaces Of(int site) => site >= 0 && _places.TryGetValue(site, out var p) ? p : None;

    /// <summary>
    /// Whether the places of town <paramref name="site"/> were looked at since the last refresh.
    /// </summary>
    public bool Has(int site) => _places.ContainsKey(site);

    /// <summary>
    /// The workplaces of <paramref name="town"/> 189 can really work at (none for a town not looked at).
    /// </summary>
    public IReadOnlyList<TownBuilding> RealWorkOf(int town) => town >= 0 && _realWork.TryGetValue(town, out var real) ? real : [];

    /// <summary>
    /// The buildings wandering in <paramref name="site"/> can go to from <paramref name="at"/>: its public ones with a general node in the zone of
    /// that point.
    /// </summary>
    public IReadOnlyList<TownBuilding> WanderIn(int site, Vector3 at) =>
        Of(site).Wander.TryGetValue(WorldLayout.ZoneOf(at.X, at.Z), out var list) ? list : [];

    /// <summary>
    /// The towns' places for the AI's squads (serial, on the package cadence). A bar is a building whose residents' template has <c>building
    /// designation</c> BAR (the original's hang-around-bar package picks one of those in the home town, docs/game/ai-tasks.md "Town goals",
    /// Observed); a shop one whose residents trade (<see cref="SquadTemplate.HasShop"/>; the original's shopping looks for BF_SHOP furniture inside,
    /// which Meitou does not read: engine choice), and public, as the original's shop finders require. Residents are the squads living there, else
    /// the level data's <c>residents template</c>; the layout flags <c>is bar</c> / <c>is shop</c> count too. A building is public as its residents'
    /// template's <c>public day</c> (the original writes it over the home's flag), else as its level state's <c>is public day</c>, else public
    /// (engine choice: the original's default is Unknown); a town building the player owns as its Open to public toggle once set (the stats panel,
    /// <see cref="Locks.FixtureRegistry.PublicOf"/>).
    /// </summary>
    public void Refresh(World world, AiSystem ai)
    {
        _places.Clear();
        _realWork.Clear();
        var shops = new HashSet<TownBuilding>(ReferenceEqualityComparer.Instance);
        var bars = new HashSet<TownBuilding>(ReferenceEqualityComparer.Instance);
        var publicFlag = new Dictionary<TownBuilding, bool>(ReferenceEqualityComparer.Instance);

        // The building's own record (designation, last template, public day), else (engine choice, a town not made yet) the template of its level
        // data's residents.
        void Note(TownBuilding b, SquadTemplate? t)
        {
            if (world.Fixtures.RecordOf(b.Key) is { } record)
            {
                if (record.Template is { } id && ai.TemplateOf(id).Template is { HasShop: true }) shops.Add(b);
                if (record.Designation == AiSystem.DesignationBar) bars.Add(b);
                if (record.PublicDay is { } open) publicFlag[b] = open;

                return;
            }

            if (t is null) return;
            if (t.HasShop) shops.Add(b);
            if (t.BuildingDesignation == AiSystem.DesignationBar) bars.Add(b);

            publicFlag.TryAdd(b, t.PublicDay);
        }

        var stocks = new Dictionary<TownBuilding, List<Trading.Shop>>(ReferenceEqualityComparer.Instance);

        foreach (var squad in world.Squads.All)
        {
            if ((squad.Ai?.OwnHome ?? squad.HomeBuilding) is { } home)
            {
                Note(home, ai.TemplateOf(squad.TemplateId).Template);

                // Only a building shop sells to shoppers: its stock is in containers, which is all FUN_14054add0 looks at (a trader's carried stock
                // is not).
                if (squad.Shop is { Building: not null } shop) (stocks.TryGetValue(home, out var held) ? held : stocks[home] = []).Add(shop);
            }
        }

        foreach (var squad in world.Squads.All)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                // The squad's town, and the one it is in (a bar package away from home takes that town's bar).
                int site = pass == 0 ? squad.Ai?.Town ?? -1 : squad.Ai?.InTown.Site ?? -1;
                if (site >= 0 && !_places.ContainsKey(site)) AddPlaces(site);
            }
        }

        void AddPlaces(int site)
        {
            var buildings = data.Sites[site].Buildings;
            foreach (var b in buildings)
                if (b.ResidentsTemplate.Length > 0) Note(b, ai.TemplateOf(b.ResidentsTemplate).Template);

            bool IsPublic(TownBuilding b) => world.Fixtures.PublicOf(b.Key)
                ?? (publicFlag.TryGetValue(b, out bool open) ? open : b.LevelState?.GetBool("is public day", true) ?? true);
            var wander = buildings.Where(b => IsPublic(b)).GroupBy(b => b.Zone).ToDictionary(g => g.Key, g => g.ToArray());
            var shopList = buildings.Where(b => (b.IsShop || shops.Contains(b)) && IsPublic(b)).ToArray();
            var stock = new Dictionary<TownBuilding, ulong>(ReferenceEqualityComparer.Instance);
            foreach (var b in shopList) stock[b] = StockOf(world, b, stocks.GetValueOrDefault(b));

            // The town's furniture to sit around at (chairs, thrones, dummies) and the faction owning each building: its property owner, else its
            // level owner, else the town's.
            int townFaction = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
            var seats = new List<SeatClaim>();
            var seatOwners = new Dictionary<TownBuilding, int>(ReferenceEqualityComparer.Instance);

            foreach (var b in buildings)
            {
                if (world.Fixtures.IsEmptied(b.Key)) continue;   // a bought house's furniture is gone

                for (int i = 0; i < b.SeatCount; i++)
                {
                    if (TownPlaceClaims.Sittable(b.SeatAt(i).Kind, dummies: true))
                    {
                        seats.Add(new SeatClaim(b, i));
                        if (!seatOwners.ContainsKey(b)) seatOwners[b] = ai.BuildingOwner(world, b, townFaction);
                    }
                }
            }

            _places[site] = new(wander, shopList, [.. buildings.Where(b => (b.IsBar || bars.Contains(b)) && IsPublic(b))],
                [.. buildings.Where(b => b.IsOutdoorTable)],
                [.. buildings.Where(b => !world.Fixtures.IsDestroyed(b) && b.Function is >= 0 and var fn && (WorkFunctions >> fn & 1) != 0)],
                stock, seats, seatOwners,
                [.. buildings.Where(b => b.HasInterior && !b.IsWall && !world.Fixtures.IsDestroyed(b) && IsPublic(b))],
                [.. buildings.Where(b => !world.Fixtures.IsDestroyed(b)).SelectMany(TownPlaceClaims.GuardPosts)]);
            _realWork[site] = [.. _places[site].Workplaces.Where(b => AiSystem.RealWork(world, b))];
        }
    }

    /// <summary>
    /// The item functions a shop has on sale, as a bit mask (goal 118 looks for a shop selling what it wants, <c>FUN_14054add0</c>): what its stock
    /// containers hold and what the stock of the squads living in it holds (a building shop's containers are merged into the squad's stock; a
    /// trader's carried stock is not looked at).
    /// </summary>
    ulong StockOf(World world, TownBuilding building, List<Trading.Shop>? held)
    {
        ulong mask = 0;

        void Add(IEnumerable<Items.ItemInstance> items)
        {
            foreach (var item in items)
                if (data.Items.Info(item.Record) is { } info) mask |= ShoppingList.Bit(info.Function);
        }

        foreach (var f in building.Fixtures)
            if (f.HoldsShopStock && world.Fixtures.Find(f.Id)?.Contents is { } box) Add(box.Items);

        if (held is not null)
            foreach (var shop in held) Add(shop.Stock.Items);

        return mask;
    }
}
