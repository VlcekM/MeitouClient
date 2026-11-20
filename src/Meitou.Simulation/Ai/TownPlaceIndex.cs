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
    /// The workplaces of each outpost a squad has as its town (<see cref="Blackboard.Outpost"/>), by the player town's number, and those 189 can
    /// really work at: rebuilt with the places (derived, not hashed).
    /// </summary>
    readonly Dictionary<int, (IReadOnlyList<TownBuilding> All, IReadOnlyList<TownBuilding> Real)> _outposts = [];

    /// <summary>
    /// The shops of each outpost that has one, by the player town's number (<see cref="OutpostShops"/>): rebuilt with the places (derived, not
    /// hashed).
    /// </summary>
    readonly Dictionary<int, TownPlaces> _outpostShops = [];

    /// <summary>
    /// The places of each outpost a squad has as its town (<see cref="Blackboard.Outpost"/>), by the player town's number: where its squads
    /// wander and sit (<see cref="AddOutpostPlaces"/>). Rebuilt with the places (derived, not hashed); none without such a squad.
    /// </summary>
    readonly Dictionary<int, TownPlaces> _outpostPlaces = [];

    /// <summary>
    /// The wall gates of each outpost a squad has as its town, by the player town's number (<see cref="OutpostGates"/>): rebuilt with the places
    /// (derived, not hashed); none without such a squad.
    /// </summary>
    readonly Dictionary<int, IReadOnlyList<TownBuilding>> _outpostGates = [];

    /// <summary>
    /// The stand-in of each player-built workplace, by site id: kept across refreshes, as a worker holds its workplace by reference
    /// (<see cref="AiMind.WorkPost"/>); a site whose record changed gets a new one (derived, not hashed).
    /// </summary>
    readonly Dictionary<string, TownBuilding> _standIns = new(StringComparer.Ordinal);

    /// <summary>
    /// The seat pieces of the outposts squads have as their town, and their stand-ins (<see cref="OutpostSeats"/>).
    /// </summary>
    readonly OutpostSeats _outpostSeats = new(data);

    /// <summary>
    /// The places of town <paramref name="site"/> (<see cref="None"/> when it was not looked at).
    /// </summary>
    public TownPlaces Of(int site) => site >= 0 && _places.TryGetValue(site, out var p) ? p : None;

    /// <summary>
    /// The places of <paramref name="town"/>, a placed town's (<see cref="Of(int)"/>) or a player town's (an outpost some squad has as its town:
    /// where to wander and sit, <see cref="AddOutpostPlaces"/>); <see cref="None"/> for no town and for any other outpost.
    /// </summary>
    public TownPlaces Of(CampaignTown town)
    {
        if (town.Site >= 0) return Of(town.Site);

        return town.PlayerTown > 0 && _outpostPlaces.TryGetValue(town.PlayerTown, out var p) ? p : None;
    }

    /// <summary>
    /// Whether the places of town <paramref name="site"/> were looked at since the last refresh.
    /// </summary>
    public bool Has(int site) => _places.ContainsKey(site);

    /// <summary>
    /// The workplaces of <paramref name="town"/> 189 can really work at (none for a town not looked at).
    /// </summary>
    public IReadOnlyList<TownBuilding> RealWorkOf(int town) => town >= 0 && _realWork.TryGetValue(town, out var real) ? real : [];

    /// <summary>
    /// The workplaces of outpost <paramref name="number"/> (a player town's number): its complete, standing buildings with a function in
    /// <see cref="WorkFunctions"/> that the finder's <c>couldIOperate</c> takes (<see cref="NeedsWork"/>), each a stand-in known by its site id (<see cref="TownBuilding.RuntimeKey"/>, the id of its machine); 189
    /// takes only those it can really work at (<paramref name="real"/>, <see cref="AiSystem.RealWork"/>). None for an outpost not looked at.
    /// </summary>
    public IReadOnlyList<TownBuilding> OutpostWorkplaces(int number, bool real)
    {
        if (!_outposts.TryGetValue(number, out var work)) return [];

        return real ? work.Real : work.All;
    }

    /// <summary>
    /// The places of outpost <paramref name="number"/> (a player town's number) for its shoppers: its shops and what they stock, nothing else (a town
    /// of the player's faction gets no random shopping, so its other public buildings are never picked). <see cref="None"/> for an outpost without
    /// a shop.
    /// </summary>
    public TownPlaces OutpostShops(int number) => _outpostShops.TryGetValue(number, out var p) ? p : None;

    /// <summary>
    /// The wall gates of outpost <paramref name="number"/> (a player town's number) a squad has as its town (<see cref="AddOutpostGates"/>); none
    /// for an outpost not looked at.
    /// </summary>
    public IReadOnlyList<TownBuilding> OutpostGates(int number) => _outpostGates.TryGetValue(number, out var gates) ? gates : [];

    /// <summary>
    /// The outposts' workplaces as the last refresh found them, by outpost number in order.
    /// </summary>
    public IEnumerable<TownBuilding> OutpostStandIns() => _outposts
        .OrderBy(o => o.Key)
        .SelectMany(o => o.Value.All);

    /// <summary>
    /// The buildings wandering in <paramref name="town"/> can go to from <paramref name="at"/>: its public ones in the zone of that point.
    /// </summary>
    public IReadOnlyList<TownBuilding> WanderIn(CampaignTown town, Vector3 at) =>
        Of(town).Wander.TryGetValue(WorldLayout.ZoneOf(at.X, at.Z), out var list) ? list : [];

    /// <summary>
    /// The towns' places for the AI's squads (serial, on the package cadence). A bar is a building whose residents' template has <c>building
    /// designation</c> BAR (the original's hang-around-bar package picks one of those in the home town, docs/game/ai-tasks.md "Town goals",
    /// Observed); a shop one whose residents trade (<see cref="SquadTemplate.HasShop"/>, standing for the original's shop designation), and public,
    /// as the original's shop finders require. Residents are the squads living there, else
    /// the level data's <c>residents template</c>; the layout flags <c>is bar</c> / <c>is shop</c> count too. A building is public as its residents'
    /// template's <c>public day</c> (the original writes it over the home's flag), else as its level state's <c>is public day</c>, else public
    /// (engine choice: the original's default is Unknown); a town building the player owns as its Open to public toggle once set (the stats panel,
    /// <see cref="Locks.FixtureRegistry.PublicOf"/>). A public town building the player owns with an interior and a shop counter the player built in
    /// it is a shop too (<see cref="Trading.PlayerShops"/>), and so is a public building the player built in an outpost with one
    /// (<see cref="OutpostShops"/>).
    /// </summary>
    public void Refresh(World world, AiSystem ai)
    {
        _places.Clear();
        _realWork.Clear();
        _outposts.Clear();
        _outpostShops.Clear();
        _outpostPlaces.Clear();
        _outpostGates.Clear();
        var refresh = new Refresher(this, world, ai, data);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        refresh.NoteHomes();
        refresh.AddTowns();
        AddOutposts(world, ai, seen);
        AddOutpostShops(world, ai, seen);

        foreach (string gone in _standIns.Keys.Where(id => !seen.Contains(id)).ToList()) _standIns.Remove(gone);
        _outpostSeats.Prune(seen);
    }

    /// <summary>
    /// The workplaces of each outpost a squad has as its town (<see cref="OutpostWorkplaces"/>), their sites noted in <paramref name="seen"/>.
    /// </summary>
    void AddOutposts(World world, AiSystem ai, HashSet<string> seen)
    {
        foreach (var squad in world.Squads.All)
        {
            if (squad.Ai is not { Outpost: > 0 and var number } || _outposts.ContainsKey(number)) continue;

            var all = new List<TownBuilding>();
            foreach (var site in world.Buildings.All)
            {
                if (!site.Complete || site.Destroyed || ai.FunctionOf(site.Record) is not (>= 0 and var fn)) continue;
                if ((WorkFunctions >> fn & 1) == 0 || !NeedsWork(world, site, fn)) continue;
                if (Construction.TownLookup.PlayerTownOf(world, site)?.Number != number) continue;

                all.Add(StandIn(site, fn));
                seen.Add(site.Id);
            }

            _outposts[number] = (all, [.. all.Where(b => AiSystem.RealWork(world, b))]);
            AddOutpostPlaces(world, ai, number, seen);
            AddOutpostGates(world, ai, number, seen);
        }
    }

    /// <summary>
    /// The places of outpost <paramref name="number"/>, their sites noted in <paramref name="seen"/>: where its squads sit (its seat pieces,
    /// <see cref="OutpostSeats"/>) and where they wander (37 Wandering town, <see cref="WanderIn"/>): its complete, standing, public buildings
    /// of the outpost's owner (<see cref="InOutpost"/>) whose doors are not all locked (the AI-run squad keeps to the player's locks,
    /// <see cref="SquadAutonomy.KeepsPlayerLocks"/>), that stand on their own (no furniture or machine placed in another) and have an interior,
    /// no wall or gate, by zone in the
    /// order of the player's buildings, each a stand-in known by its site id (<see cref="TownBuilding.RuntimeKey"/>). <b>Engine choice</b> (Meitou
    /// only: the original plans no goals for the player's squads): a placed town's wanderer goes to a general node of a public building, and a
    /// player-built building has no nodes, so the building itself is the target (<see cref="AiSystem"/>'s <c>TargetFor</c>), as an outpost's
    /// shop is.
    /// </summary>
    void AddOutpostPlaces(World world, AiSystem ai, int number, HashSet<string> seen)
    {
        var wander = new List<TownBuilding>();
        foreach (var site in world.Buildings.All)
        {
            bool standing = site.Complete && !site.Destroyed && site.Public && site.Parent is null;
            if (!standing || !InOutpost(world, site, number)) continue;
            if (world.Fixtures.AllDoorsLocked(site.Id) || data.Db.Find(site.Record) is not { } record) continue;
            if (record.GetReferences("interior").Count == 0 || IsWallOrGate(record)) continue;

            wander.Add(StandIn(site, ai.FunctionOf(site.Record)));
            seen.Add(site.Id);
        }

        var byZone = wander.GroupBy(b => b.Zone).ToDictionary(g => g.Key, g => g.ToArray());
        var (seats, owners) = _outpostSeats.Of(world, number, seen);
        _outpostPlaces[number] = None with { Wander = byZone, Seats = seats, SeatOwners = owners };
    }

    /// <summary>
    /// The wall gates of outpost <paramref name="number"/> (<see cref="OutpostGates"/>), their sites noted in <paramref name="seen"/>: its
    /// complete, standing buildings of the outpost's owner (<see cref="InOutpost"/>) with <c>is wall gate</c> (the flag a placed town's gate is
    /// known by, <see cref="TownBuilding.IsGate"/>), in the order of the player's buildings, each a stand-in known by its site id, whose doors the
    /// player's door fixtures name (<see cref="Locks.FixtureRegistry.RegisterPlayerBuilding"/>). Their guards stand on the outpost's side
    /// (<see cref="GatePost.Inside"/>).
    /// </summary>
    void AddOutpostGates(World world, AiSystem ai, int number, HashSet<string> seen)
    {
        var gates = new List<TownBuilding>();
        foreach (var site in world.Buildings.All)
        {
            if (!site.Complete || site.Destroyed || !InOutpost(world, site, number)) continue;
            if (data.Db.Find(site.Record)?.GetBool("is wall gate") != true) continue;

            gates.Add(StandIn(site, ai.FunctionOf(site.Record)));
            seen.Add(site.Id);
        }

        _outpostGates[number] = gates;
    }

    /// <summary>
    /// Whether a building the player built stands in outpost <paramref name="number"/> (<see cref="Construction.TownLookup.PlayerTownOf"/>) and is
    /// owned by the outpost's owner (not another faction's building inside it).
    /// </summary>
    internal static bool InOutpost(World world, Construction.BuildSite site, int number) =>
        Construction.TownLookup.PlayerTownOf(world, site) is { } town && town.Number == number && site.Owner == town.Owner;

    /// <summary>
    /// A wall (<c>links together</c>) or a wall gate (<c>is wall gate</c>): not a building to wander into.
    /// </summary>
    static bool IsWallOrGate(Data.GameRecord record) => record.GetBool("links together") || record.GetBool("is wall gate");

    /// <summary>
    /// The shops of each outpost (<see cref="OutpostShops"/>), their sites noted in <paramref name="seen"/>: the original's shop test passes a
    /// building of the player's faction with an interior, a function 9 piece in it and public, no residents needed (<c>FUN_1409986e0</c>,
    /// <b>Observed</b>, disassembly), and its list finder takes the shops of the shopper's status town, an outpost when it stands in one
    /// (<c>FUN_1409997f0</c>, <b>Verified</b>, disassembly). So a complete, standing building the player built in an outpost, set public, with a shop counter the player built
    /// inside (which gives it the interior), is a shop of that outpost; it sells what its counters hold.
    /// </summary>
    void AddOutpostShops(World world, AiSystem ai, HashSet<string> seen)
    {
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        var shops = new Dictionary<int, (List<TownBuilding> Shops, Dictionary<TownBuilding, ulong> Stock)>();

        foreach (var (id, counters) in Trading.PlayerShops.InBuiltBuildings(world, player))
        {
            if (world.Buildings.Find(id) is not { Complete: true, Destroyed: false, Public: true } site || site.Owner != player)
                continue;
            if (Construction.TownLookup.PlayerTownOf(world, site) is not { } town) continue;

            var shop = StandIn(site, ai.FunctionOf(site.Record));
            seen.Add(site.Id);
            if (!shops.TryGetValue(town.Number, out var of))
                shops[town.Number] = of = ([], new Dictionary<TownBuilding, ulong>(ReferenceEqualityComparer.Instance));
            of.Shops.Add(shop);
            of.Stock[shop] = StockOf(world, shop, null, counters);
        }

        foreach (var (number, of) in shops) _outpostShops[number] = None with { Shops = of.Shops, Stock = of.Stock };
    }

    /// <summary>
    /// <c>dontNeedWorkRightNow</c> (slot <c>0x4e8</c>) is false, which the finder's <c>couldIOperate</c> (slot <c>0x500</c>, <c>FUN_1400f7c90</c>)
    /// asks: a constant 0 but for a research bench (<c>FUN_1402995e0</c>: true for the player's bench while no research is current).
    /// </summary>
    static bool NeedsWork(World world, Construction.BuildSite site, int function) =>
        function != TownBuildings.ResearchFunction || site.Owner != world.Player.Faction || world.Research.Queue.Count > 0;

    /// <summary>
    /// The stand-in of a player-built workplace (<see cref="_standIns"/>): its place, facing and function, known by its site id.
    /// </summary>
    TownBuilding StandIn(Construction.BuildSite site, int function)
    {
        if (_standIns.TryGetValue(site.Id, out var known) && known.BuildingId == site.Record) return known;

        return _standIns[site.Id] = new TownBuilding(site.Id, site.Record, site.Name, site.Position, TownBuildings.YawOf(site.Rotation), false,
            false, [])
        {
            RuntimeKey = site.Id,
            Zone = site.Zone,
            Function = function,
            IsGate = data.Db.Find(site.Record)?.GetBool("is wall gate") == true,
            IsGateway = data.Db.Find(site.Record)?.GetBool("is gateway") == true,
        };
    }

    /// <summary>
    /// One <see cref="Refresh"/>: the shops, bars and public flags noted from the buildings' records and residents, then the places of the squads'
    /// towns.
    /// </summary>
    sealed class Refresher(TownPlaceIndex index, World world, AiSystem ai, PopulationData data)
    {
        /// <summary>
        /// The buildings whose residents trade.
        /// </summary>
        readonly HashSet<TownBuilding> _shops = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The player's faction.
        /// </summary>
        readonly int _player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;

        /// <summary>
        /// The shop counters the player built, by the key of the building each stands in (<see cref="Trading.PlayerShops.ByBuilding"/>).
        /// </summary>
        readonly Dictionary<string, List<Items.Inventory>> _counters = Trading.PlayerShops.ByBuilding(world,
            world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction);

        /// <summary>
        /// The buildings designated bars.
        /// </summary>
        readonly HashSet<TownBuilding> _bars = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Each building's public flag as its record or residents have it.
        /// </summary>
        readonly Dictionary<TownBuilding, bool> _publicFlag = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The building shops of the squads living in each building.
        /// </summary>
        readonly Dictionary<TownBuilding, List<Trading.Shop>> _stocks = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Notes the homes of every squad, and the shops selling from them.
        /// </summary>
        public void NoteHomes()
        {
            foreach (var squad in world.Squads.All)
            {
                if ((squad.Ai?.OwnHome ?? squad.HomeBuilding) is { } home)
                {
                    Note(home, ai.TemplateOf(squad.TemplateId).Template);

                    // Only a building shop sells to shoppers: its stock is in containers, which is all FUN_14054add0 looks at (a trader's carried
                    // stock is not).
                    if (squad.Shop is { Building: not null } shop) (_stocks.TryGetValue(home, out var held) ? held : _stocks[home] = []).Add(shop);
                }
            }
        }

        /// <summary>
        /// Adds the places of each squad's town and of the town it is in.
        /// </summary>
        public void AddTowns()
        {
            foreach (var squad in world.Squads.All)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    // The squad's town, and the one it is in (a bar package away from home takes that town's bar).
                    int site = pass == 0 ? squad.Ai?.Town ?? -1 : squad.Ai?.InTown.Site ?? -1;
                    if (site >= 0 && !index._places.ContainsKey(site)) AddPlaces(site);
                }
            }
        }

        /// <summary>
        /// The building's own record (designation, last template, public day), else (engine choice, a town not made yet) the template of its level
        /// data's residents.
        /// </summary>
        void Note(TownBuilding b, SquadTemplate? t)
        {
            if (world.Fixtures.RecordOf(b.Key) is { } record)
            {
                if (record.Template is { } id && ai.TemplateOf(id).Template is { HasShop: true }) _shops.Add(b);
                if (record.Designation == AiSystem.DesignationBar) _bars.Add(b);
                if (record.PublicDay is { } open) _publicFlag[b] = open;

                return;
            }

            if (t is null) return;
            if (t.HasShop) _shops.Add(b);
            if (t.BuildingDesignation == AiSystem.DesignationBar) _bars.Add(b);

            _publicFlag.TryAdd(b, t.PublicDay);
        }

        /// <summary>
        /// The places of one town: where to wander, shop, drink, sit, work, go inside and stand guard.
        /// </summary>
        void AddPlaces(int site)
        {
            var buildings = data.Sites[site].Buildings;
            foreach (var b in buildings)
                if (b.ResidentsTemplate.Length > 0) Note(b, ai.TemplateOf(b.ResidentsTemplate).Template);

            var wander = buildings.Where(b => IsPublic(b)).GroupBy(b => b.Zone).ToDictionary(g => g.Key, g => g.ToArray());
            var owned = _counters.Count == 0 ? [] : world.Fixtures.PlayerOwnedIn(data, site, _player);
            var shopList = buildings.Where(b => (IsTownShop(b) || (owned.Contains(b.Key) && PlayerShop(b))) && IsPublic(b)).ToArray();
            var stock = new Dictionary<TownBuilding, ulong>(ReferenceEqualityComparer.Instance);
            foreach (var b in shopList) stock[b] = index.StockOf(world, b, _stocks.GetValueOrDefault(b), _counters.GetValueOrDefault(b.Key));

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

            index._places[site] = new(wander, shopList, [.. buildings.Where(b => (b.IsBar || _bars.Contains(b)) && IsPublic(b))],
                [.. buildings.Where(b => b.IsOutdoorTable)],
                [.. buildings.Where(b => !world.Fixtures.IsDestroyed(b) && b.Function is >= 0 and var fn && (WorkFunctions >> fn & 1) != 0)],
                stock, seats, seatOwners,
                [.. buildings.Where(b => b.HasInterior && !b.IsWall && !world.Fixtures.IsDestroyed(b) && IsPublic(b))],
                [.. buildings.Where(b => !world.Fixtures.IsDestroyed(b)).SelectMany(TownPlaceClaims.GuardPosts)]);
            index._realWork[site] = [.. index._places[site].Workplaces.Where(b => AiSystem.RealWork(world, b))];
        }

        /// <summary>
        /// Whether a building is a shop by its layout flag or trading residents.
        /// </summary>
        bool IsTownShop(TownBuilding b) => b.IsShop || _shops.Contains(b);

        /// <summary>
        /// Whether a town building the player owns is a shop (the original's shop test for a building of the player's faction): it has an interior,
        /// stands, and holds a shop counter the player built.
        /// </summary>
        bool PlayerShop(TownBuilding b) => b.HasInterior && !world.Fixtures.IsDestroyed(b) && _counters.ContainsKey(b.Key);

        /// <summary>
        /// Whether a building is open to the public: the player's toggle, else its noted flag, else its level state's.
        /// </summary>
        bool IsPublic(TownBuilding b) => world.Fixtures.PublicOf(b.Key)
            ?? (_publicFlag.TryGetValue(b, out bool open) ? open : b.LevelState?.GetBool("is public day", true) ?? true);
    }

    /// <summary>
    /// The item functions a shop has on sale, as a bit mask (goal 118 looks for a shop selling what it wants, <c>FUN_14054add0</c>): what its stock
    /// containers hold, what the stock of the squads living in it holds (a building shop's containers are merged into the squad's stock; a
    /// trader's carried stock is not looked at) and what the shop counters the player built in it hold.
    /// </summary>
    ulong StockOf(World world, TownBuilding building, List<Trading.Shop>? held, List<Items.Inventory>? counters)
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

        if (counters is not null)
            foreach (var counter in counters) Add(counter.Items);

        return mask;
    }
}
