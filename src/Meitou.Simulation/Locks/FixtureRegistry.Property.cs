using System.Globalization;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Locks;

/// <summary>
/// Confirms the quoted price of a town house; the simulation rechecks eligibility and funds.
/// </summary>
public sealed record BuyBuilding(string Building, long Price) : SimCommand;

/// <summary>
/// The registry's town properties: the houses the player can buy, own and rebuild.
/// </summary>
public sealed partial class FixtureRegistry
{
    /// <summary>
    /// A town house a faction can own: its data, site, building, owner, whether the player bought it and its rebuild site.
    /// </summary>
    sealed class Property(PopulationData data, int site, TownBuilding building, int owner)
    {
        /// <summary>
        /// The population data the house came from.
        /// </summary>
        public PopulationData Data { get; } = data;
        /// <summary>
        /// The town site of the house.
        /// </summary>
        public int Site { get; } = site;
        /// <summary>
        /// The house's building.
        /// </summary>
        public TownBuilding Building { get; } = building;
        /// <summary>
        /// The owner faction (-1: none).
        /// </summary>
        public int Owner { get; set; } = owner;
        /// <summary>
        /// Whether the player bought it.
        /// </summary>
        public bool Purchased { get; set; }
        /// <summary>
        /// The construction site the player rebuilds it through (null: none).
        /// </summary>
        public BuildSite? Construction { get; set; }
    }

    /// <summary>
    /// The save's key of a building state's owner faction (a FACTION string id).
    /// </summary>
    const string OwnerKey = "owner faction ID";

    /// <summary>
    /// The town properties by building key.
    /// </summary>
    readonly Dictionary<string, Property> _properties = new(StringComparer.Ordinal);
    /// <summary>
    /// The cache of <see cref="OwnedProperties"/> (null: made again).
    /// </summary>
    IReadOnlySet<string>? _ownedProperties;
    /// <summary>
    /// The faction <see cref="_ownedProperties"/> was made for.
    /// </summary>
    int _ownedFaction = -1;

    /// <summary>
    /// Registers a town building as a property when it is a house (function 19, or no function and an interior) that is not a gate or linked wall.
    /// </summary>
    void RegisterProperty(World world, PopulationData data, int site, TownBuilding building, int owner, bool purchased)
    {
        var record = data.Db.Find(building.BuildingId);
        if (record is null || record.GetBool("is wall gate") || record.GetBool("links together")) return;
        int? function = TownBuildings.FunctionOf(data.Db, record, _functions)?.GetInt("function");
        if (function != 19 && !(function is null && record.GetReferences("interior").Count > 0)) return;
        _properties[building.Key] = new(data, site, building, owner) { Purchased = purchased };
        _ownedProperties = null;
        RegisterTownConstruction(world, _properties[building.Key]);
    }

    /// <summary>
    /// The owner faction of property <paramref name="building"/>; null when it is no property.
    /// </summary>
    public int? PropertyOwner(string building) => _properties.TryGetValue(building, out var p) ? p.Owner : null;

    /// <summary>
    /// The faction owning <paramref name="building"/>: its property's owner, else the owner its doors were registered with; -1 for neither.
    /// </summary>
    public int OwnerOf(string building) => PropertyOwner(building) ?? DoorOwner(building);

    /// <summary>
    /// Where property <paramref name="building"/> stands; null when it is no property.
    /// </summary>
    internal Vector3? PropertyPosition(string building) => _properties.TryGetValue(building, out var p) ? p.Building.Position : null;

    /// <summary>
    /// Whether <paramref name="faction"/> owns a house in the town site <paramref name="site"/> (a registered site's properties only).
    /// </summary>
    public bool OwnsPropertyIn(int site, int faction)
    {
        if (site < 0 || faction < 0) return false;
        foreach (var p in _properties.Values)
            if (p.Site == site && p.Owner == faction) return true;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="faction"/> owns the building: as its property's owner, else as owner of one of its fixtures.
    /// </summary>
    public bool OwnsBuilding(string building, int faction) => faction >= 0 &&
        (_properties.TryGetValue(building, out var p) ? p.Owner == faction : OfBuilding(building).Any(f => f.Owner == faction));

    /// <summary>
    /// The building keys of the properties <paramref name="faction"/> owns, in registration order (deterministic, unlike
    /// <see cref="OwnedProperties"/>'s set).
    /// </summary>
    internal IEnumerable<string> OwnedPropertyKeys(int faction)
    {
        if (faction < 0) yield break;

        foreach (var (key, p) in _properties)
            if (p.Owner == faction) yield return key;
    }

    /// <summary>
    /// The building keys of the properties <paramref name="faction"/> owns (cached).
    /// </summary>
    public IReadOnlySet<string> OwnedProperties(int faction)
    {
        if (_ownedProperties is null || _ownedFaction != faction)
        {
            _ownedFaction = faction;
            _ownedProperties = _properties.Where(p => faction >= 0 && p.Value.Owner == faction).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        }

        return _ownedProperties;
    }

    /// <summary>
    /// The first town house <paramref name="faction"/> owns that can be the player's home (<see cref="Meitou.Simulation.Player.PlayerHome"/>:
    /// it has an interior; a property is never a gate or a wall), by key, or null.
    /// </summary>
    public string? FirstHome(int faction)
    {
        string? first = null;
        foreach (var (key, p) in _properties)
        {
            if (faction < 0 || p.Owner != faction || !p.Building.HasInterior) continue;
            if (first is null || string.CompareOrdinal(key, first) < 0) first = key;
        }

        return first;
    }

    /// <summary>
    /// The templates of the squads that took a building as their home since the boot or the load, by building key (<see cref="StampResidents"/>).
    /// </summary>
    readonly Dictionary<string, string> _residentStamps = new(StringComparer.Ordinal);

    /// <summary>
    /// The squad template <paramref name="b"/> carries now (the original's <c>Building+0xf0</c>; "" none), saved as its <c>residents template</c>
    /// and <c>squad template</c>: the last squad's that took it as home (<see cref="StampResidents"/>), else a town override's housing, else a
    /// loaded save's, else the boot's housing (<see cref="TownBuilding.ResidentsTemplate"/>).
    /// </summary>
    public string ResidentsTemplate(TownBuilding b) =>
        _residentStamps.TryGetValue(b.Key, out string? stamped) ? stamped
        : b.Naming is { } naming ? naming.SquadTemplate
        : Saved?.GetValueOrDefault(b.Key)?.Strings.GetValueOrDefault(ResidentRuins.ResidentsTemplateKey) ?? b.ResidentsTemplate;

    /// <summary>
    /// A squad of <paramref name="templateId"/> made from its template takes <paramref name="home"/> as its home: the building carries its template
    /// from now on, whoever's it carried (<c>FUN_140553210</c>, Verified: the squad factory calls it each time it makes a squad's members, so a
    /// housemate squad made after the home's own leaves the building with the housemate's).
    /// </summary>
    public void StampResidents(TownBuilding home, string templateId) => _residentStamps[home.Key] = templateId;

    /// <summary>
    /// Writes the template each town building carries into its saved record, as <c>residents template</c> and <c>squad template</c> (the original
    /// writes the same id under both, Observed: all 18518 building states of the save METARU). A building whose template is not its record's loses
    /// the record's <c>residents</c> handle, which named the squad of the old one; the platoon writer names the new squad
    /// (<c>SaveCapture</c>).
    /// </summary>
    public void WriteResidents(PopulationData data, IReadOnlyDictionary<string, FcsRecord> states)
    {
        foreach (var b in data.Sites.SelectMany(s => s.Buildings))
        {
            if (!states.TryGetValue(b.Key, out var state)) continue;

            string template = ResidentsTemplate(b);
            if (state.Strings.GetValueOrDefault(ResidentRuins.ResidentsTemplateKey, "") != template || Saved?.ContainsKey(b.Key) != true)
                Hand.Null.Write(state, "residents");

            state.Strings[ResidentRuins.ResidentsTemplateKey] = template;
            state.Strings[ResidentRuins.SquadTemplateKey] = template;
        }
    }

    /// <summary>
    /// The owner a loaded save names for a town building, when it is to be believed: null for no saved owner, a faction that is not loaded, or one
    /// that is only the level data's string and not the player's. The original saves the live owner, which the housing has set
    /// (<see cref="PopulationData.LevelOwnerOf"/>; Observed: all 3244 town buildings of the save METARU hold that rule's owner), while Meitou's
    /// saves before <see cref="WriteOwners"/> kept the level's string, which names the wrong faction for a home of another faction's residents.
    /// </summary>
    public int? SavedOwner(PopulationData data, TownBuilding b)
    {
        if (Saved?.GetValueOrDefault(b.Key)?.Strings.GetValueOrDefault(OwnerKey) is not { Length: > 0 } id) return null;
        if (data.FactionIndex(id) is not (>= 0 and var faction)) return null;

        return faction != data.PlayerFaction && id == b.OwnerFactionId ? null : faction;
    }

    /// <summary>
    /// The faction that owns a town building now: its property's owner, else its fixtures', else a loaded save's (<see cref="SavedOwner"/>), else
    /// the housing's (<see cref="PopulationData.LevelOwnerOf"/>).
    /// </summary>
    public int OwnerNow(PopulationData data, TownBuilding b, int townFaction) =>
        PropertyOwner(b.Key)
        ?? OfBuilding(b.Key).FirstOrDefault()?.Owner
        ?? SavedOwner(data, b)
        ?? data.LevelOwnerOf(b, townFaction);

    /// <summary>
    /// Writes each town building's owner into its saved record as <c>owner faction ID</c>: the original saves the live owner, not the level's
    /// string (Observed: the save METARU has Sho-Battai's Slave Shop as the Slave Traders', where its level state names the Tech Hunters). The
    /// player's buildings keep what <see cref="WriteProperty"/> and the level gave them.
    /// </summary>
    public void WriteOwners(PopulationData data, IReadOnlyDictionary<string, FcsRecord> states)
    {
        foreach (var site in data.Sites)
        {
            int townFaction = site.Index < data.SiteFactions.Count ? data.SiteFactions[site.Index] : -1;
            foreach (var b in site.Buildings)
            {
                if (!states.TryGetValue(b.Key, out var state)) continue;

                int owner = OwnerNow(data, b, townFaction);
                if ((uint)owner < (uint)data.Factions.Count && owner != data.PlayerFaction) state.Strings[OwnerKey] = data.Factions[owner].Id;
            }
        }
    }

    /// <summary>
    /// The price of a property for the player; null when it is not for sale to the player (unowned, the player's, bought, in a town of the player's
    /// or hostile owner, or of a type that sells none).
    /// </summary>
    public long? SalePrice(string building, int player)
    {
        if (player < 0 || !_properties.TryGetValue(building, out var p) || p.Owner < 0 || p.Owner == player || p.Purchased)
            return null;

        var data = p.Data;
        if (data.Sites[p.Site].Town.Type is not (TownType.Outpost or TownType.Town or TownType.Village)
            || data.SiteFactions[p.Site] == player || data.Relations.IsHostile(p.Owner, player)) return null;
        var saved = Saved?.GetValueOrDefault(building);
        var state = p.Building.LevelState;

        // FUN_14029c500 (Verified): a building that carries no template is for sale, else its template's `sell home` decides.
        bool sale = data.Db.Find(ResidentsTemplate(p.Building))?.GetBool("sell home") ?? true;
        bool inside = saved is not null ? saved.Bools.GetValueOrDefault("is inside building") : state?.GetBool("is inside building") ?? false;
        if (!sale || inside || data.Db.Find(p.Building.BuildingId) is not { } record) return null;
        bool destroyed = IsDestroyed(p.Building);
        float multiplier = data.Db.Find(data.Factions[p.Owner].Id)?.GetFloat("building cost mult", 1) ?? 1;
        float price = (destroyed ? 0.375f : 1) * BuildingBuildData.From(record).Target * multiplier * 400;
        return float.IsFinite(price) && price >= 0 && price <= int.MaxValue ? (long)price : null;
    }

    /// <summary>
    /// Buys a town house at its quoted price (docs/simulation.md "Town house purchases"): it becomes the player's and private, its furniture and
    /// loose items go (<see cref="EmptyInterior"/>, <see cref="Meitou.Simulation.Items.ItemSlots.EmptyBuilding"/>), so does its residents' camp
    /// (<see cref="ClearCamp"/>), and the squads living in it move out (<see cref="Meitou.Simulation.Ai.AiSystem.Evict"/>).
    /// </summary>
    public void Buy(World world, BuyBuilding order)
    {
        if (SalePrice(order.Building, world.Player.Faction) is not { } price || price != order.Price) return;
        if (world.Player.Money < price)
        {
            world.Post(Loc.Tr("You can't afford that."));
            return;
        }

        var p = _properties[order.Building];
        bool ruin = IsDestroyed(p.Building);
        world.Player.Money -= price;
        TakeOver(world, p);
        world.ItemSlots.EmptyBuilding(world, p.Building);
        EmptyInterior(world, order.Building, spill: false);
        ClearCamp(world, p, ruin);
        world.SystemOf<Ai.AiSystem>()?.Evict(world, order.Building);
    }

    /// <summary>
    /// The bought house's share of its town's camp goes, as the original's purchase (<c>FUN_1407ad6c0</c>, <c>FUN_140928ee0</c>,
    /// <c>FUN_1408146f0</c>; docs/formats/zones.md "Nest debris and loot"): when its residents' nest stands, the pieces standing on the house
    /// (its interior objects there), sleepers let out first, and the camp's debris meshes within the radius of the house's bounding box, as it
    /// was when bought (<paramref name="ruin"/>), of its position. The pieces on the ground beside it stay.
    /// </summary>
    void ClearCamp(World world, Property p, bool ruin)
    {
        var b = p.Building;
        float radius = p.Data.Sight?.BoundsRadius(b, ruin) ?? 0;
        var gone = world.NestBuildings.ClearHome(b.Key, new Vector2(b.Position.X, b.Position.Z), radius);
        if (gone.Count > 0) UnregisterNestBuildings(world, gone);
    }

    /// <summary>
    /// The bought house's owner, fixtures and public flag become the player's; a ruin becomes the player's construction site.
    /// </summary>
    void TakeOver(World world, Property p)
    {
        string key = p.Building.Key;
        p.Owner = world.Player.Faction;
        p.Purchased = true;
        _resetBuildings.Remove(key);
        _rebuilt.Remove(key);
        bool ruin = IsDestroyed(p.Building);
        _ownedProperties = null;
        foreach (var f in OfBuilding(key)) f.Owner = p.Owner;

        // Every door is opened (FUN_1407ad6c0 calls FUN_140297040, from closed; Verified); the sleepers leave their beds with the interior's emptying
        // below.
        foreach (var f in OfBuilding(key).ToList()) OpenDoor(f);
        _containerSpots = null;
        _doorPicks = null;
        _bedSpots = null;
        _bedList = null;
        _cageList = null;
        _dummySpots = null;
        SetPublic(key, false);
        RegisterTownConstruction(world, p, ruin);
        DropRuin(key);   // its site shows it now
    }

    /// <summary>
    /// A town replaced (docs/game/factions-squads-towns.md "Town overrides"): its buildings that are not the player's go back to their level owner
    /// under the town's new faction. Those in the town's own zone also lose their saved state, as the original drops it from the zone file
    /// (<c>FUN_1409fe340</c>): they are registered again as the level has them (locks, doors, contents), and a held character is bound to its cage or
    /// bed again. The replacement's homes in every zone are furnished anew (<see cref="IsRebuilt"/>). A town not registered yet takes all of that
    /// when it is.
    /// </summary>
    public void TownReplaced(World world, PopulationData data, int site)
    {
        var town = data.Sites[site];
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        var reset = town.Buildings
            .Where(b => b.Zone == town.Zone && !IsPlayers(data, site, b, player))
            .Select(b => b.Key)
            .ToHashSet(StringComparer.Ordinal);
        var rebuilt = HousedHomes(world, data, site);
        foreach (string key in rebuilt) MarkRebuilt(key);
        var relaid = reset
            .Union(rebuilt)
            .ToHashSet(StringComparer.Ordinal);
        RelayTown(world, data, site, relaid);
        ApplyTownRuins(world, data, site, reset, housed: true);
        ResetState(data, site, reset);
        if (!_sites.Contains(site)) return;

        RegisterAgain(world, data, site, reset, rebuilt);
        Bind(world);
        _ownedProperties = null;
        _containerSpots = null;
        _doorPicks = null;
        _bedSpots = null;
        _bedList = null;
        _cageList = null;
        _dummySpots = null;
    }

    /// <summary>
    /// The keys of a replaced town's homes that are not the player's, in all its zones: the original's housing pass houses the replacement's
    /// residents in the town's buildings wherever they stand (<c>FUN_1409370d0</c>), each home through <c>FUN_14092e4b0</c>, which clears its
    /// interior and lays it out from the template's names.
    /// </summary>
    HashSet<string> HousedHomes(World world, PopulationData data, int site)
    {
        var town = data.Sites[site];
        int factionIndex = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        var faction = factionIndex >= 0 ? data.Factions[factionIndex] : null;
        string? playerId = data.PlayerFaction >= 0 ? data.Factions[data.PlayerFaction].Id : null;
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        var players = PlayerOwnedIn(data, site, player);
        var homes = ResidentRuins.HomeTemplates(data.Db, town.Town, faction, town.Buildings, playerId, players);
        return homes.Keys
            .Where(key => !players.Contains(key))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The own zone's buildings <paramref name="reset"/> lose their saved state and their residents' stamps, and are listed as reset with their level
    /// owner.
    /// </summary>
    void ResetState(PopulationData data, int site, HashSet<string> reset)
    {
        var town = data.Sites[site];
        int townFaction = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        if (Saved is { } saved && reset.Any(saved.ContainsKey))
            Saved = saved.Where(s => !reset.Contains(s.Key)).ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        foreach (string key in reset) _residentStamps.Remove(key);

        foreach (var b in town.Buildings)
            if (reset.Contains(b.Key)) _resetBuildings[b.Key] = (b, data, data.LevelOwnerOf(b, townFaction));
    }

    /// <summary>
    /// A registered town's buildings after its override: the reset ones registered again as the level has them, the homes furnished anew in other
    /// zones registered again with their saved state but new objects (<see cref="Rebuild"/>), and every one that is not the player's handed to its
    /// level owner with its fixtures and item slots.
    /// </summary>
    void RegisterAgain(World world, PopulationData data, int site, HashSet<string> reset, HashSet<string> rebuilt)
    {
        var town = data.Sites[site];
        int townFaction = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        foreach (var b in town.Buildings)
        {
            if (reset.Contains(b.Key))
            {
                Unregister(b.Key);
                _properties.Remove(b.Key);
                RegisterBuilding(world, data, site, b, townFaction);
                world.ItemSlots.PlaceBuilding(world, data, site, b);   // a house stood up again has its slots back
            }
            else if (IsPlayers(data, site, b, player)) continue;
            else if (rebuilt.Contains(b.Key)) Rebuild(world, data, site, b, townFaction);

            int owner = data.LevelOwnerOf(b, townFaction);
            if (_properties.TryGetValue(b.Key, out var p)) p.Owner = owner;
            foreach (var f in OfBuilding(b.Key)) f.Owner = owner;
            world.ItemSlots.TransferBuilding(world, b, owner);
        }
    }

    /// <summary>
    /// A home outside the replaced town's zone furnished anew: registered again from its saved state, which keeps its doors and own lock while its
    /// layout's objects start new (<see cref="Restore"/>), its doors as they stand now; its item slots go with their items and its layout's come
    /// (<c>FUN_14054f4d0</c> destroys the interior's item groups).
    /// </summary>
    void Rebuild(World world, PopulationData data, int site, TownBuilding b, int townFaction)
    {
        var old = OfBuilding(b.Key).ToList();
        Unregister(b.Key);
        RegisterBuilding(world, data, site, b, townFaction);
        CarryDoors(b.Key, old);

        world.ItemSlots.EmptyBuilding(world, b);
        world.ItemSlots.PlaceBuilding(world, data, site, b);
    }

    /// <summary>
    /// The homes a town override furnished anew since the load (<see cref="TownReplaced"/>), the player's left out; a building the player buys
    /// afterwards leaves it.
    /// </summary>
    readonly HashSet<string> _rebuilt = new(StringComparer.Ordinal);
    /// <summary>
    /// Every home furnished anew, in order, once per override (<see cref="RebuiltHouses"/>).
    /// </summary>
    readonly List<string> _rebuiltLog = [];

    /// <summary>
    /// The homes town overrides furnished anew since the load, in order, a home once per override: production drops their objects' machines.
    /// </summary>
    public IReadOnlyList<string> RebuiltHouses => _rebuiltLog;

    /// <summary>
    /// Whether a town override furnished the home anew since the load and the player has not bought it since: its layout's objects are new, so no
    /// saved state of the old ones applies to them, and a save drops the old ones' entries (docs/formats/save.md "Zone files").
    /// </summary>
    public bool IsRebuilt(string building) => _rebuilt.Contains(building);

    /// <summary>
    /// Lists a home as furnished anew.
    /// </summary>
    void MarkRebuilt(string key)
    {
        _rebuilt.Add(key);
        _rebuiltLog.Add(key);
    }

    /// <summary>
    /// Takes the saved state of a rebuilt home's old layout objects (Meitou's container and lock keys) off its record, which a save patches: the
    /// live objects write their own. A bought house's go too: its layout objects are gone (<see cref="EmptyInterior"/>), so a key left behind
    /// would put its items back at every load, where <see cref="EmptyPurchased"/> drops them on the ground again.
    /// </summary>
    void DropObjectState(string building, FcsRecord state)
    {
        if (!_rebuilt.Contains(building) && !IsEmptied(building)) return;

        var contents = state.Strings.Keys
            .Where(k => k.StartsWith(ContainerSave.KeyPrefix, StringComparison.Ordinal))
            .ToList();
        foreach (string key in contents) state.Strings.Remove(key);

        var locks = state.Ints.Keys
            .Where(k => k.StartsWith(InteriorLockPrefix, StringComparison.Ordinal))
            .ToList();
        foreach (string key in locks) state.Ints.Remove(key);
    }

    /// <summary>
    /// The buildings a town override reset to the level, with their level owner then: their saved record (patched, not rewritten, by a save) must not
    /// bring the old state back. A building the player buys afterwards leaves it.
    /// </summary>
    readonly Dictionary<string, (TownBuilding Building, PopulationData Data, int Owner)> _resetBuildings = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a town override reset the building to the level (<see cref="TownReplaced"/>) and the player has not bought it since.
    /// </summary>
    public bool IsReset(string building) => _resetBuildings.ContainsKey(building);

    /// <summary>
    /// A reset building's keys the live writers do not touch: its owner (its fixtures', else the one it was reset to) and its level <c>destroyed</c>.
    /// </summary>
    void WriteReset(string building, FcsRecord state)
    {
        if (!_resetBuildings.TryGetValue(building, out var reset)) return;
        int owner = OfBuilding(building).FirstOrDefault()?.Owner ?? reset.Owner;
        if ((uint)owner < (uint)reset.Data.Factions.Count)
            state.Strings["owner faction ID"] = reset.Data.Factions[owner].Id;
        else state.Strings.Remove("owner faction ID");
        state.Bools["destroyed"] = IsDestroyed(reset.Building);
    }

    /// <summary>
    /// Writes <see cref="WriteReset"/>'s keys into the saved records of every reset building, by instance id: also those with no fixtures or whose
    /// town was not registered, which <see cref="WriteBuilding"/> never sees.
    /// </summary>
    public void WriteResets(IReadOnlyDictionary<string, FcsRecord> states)
    {
        foreach (var building in _resetBuildings.Keys)
            if (states.TryGetValue(building, out var state)) WriteReset(building, state);

        // A rebuilt home of a town not registered has no live objects to write theirs (WriteBuilding drops the old keys of the others).
        foreach (var building in _rebuilt)
            if (!_byBuilding.ContainsKey(building) && states.TryGetValue(building, out var state)) DropObjectState(building, state);
        WriteRuins(states);
    }

    /// <summary>
    /// Whether a town building is a ruin: its saved <c>destroyed</c>, but a level ruin stays one unless the player's repair saved it (<c>meitou town
    /// construction</c>, or the original's repair: saved standing as the player's): a save from before the residents' ruins were laid out
    /// (docs/game/buildings-production.md "Ruined by residents") wrote those buildings intact. A saved level ruin its residents stand up is not one
    /// (<see cref="SavedLevelRuin"/>).
    /// </summary>
    public bool SavedDestroyed(TownBuilding b)
    {
        if (Saved?.GetValueOrDefault(b.Key) is not { } saved) return b.Destroyed;
        if (b.Destroyed && !saved.Bools.GetValueOrDefault("meitou town construction") && !RepairedByPlayer(saved)) return true;

        return saved.Bools.GetValueOrDefault("destroyed", b.Destroyed) && !SavedLevelRuin(b, saved);
    }

    /// <summary>
    /// Whether a saved state is the original's repair of a level ruin: standing and the player's, with no Meitou construction keys (the original
    /// loads a building's saved <c>destroyed</c> as it is, docs/game/buildings-production.md "Ruined by residents").
    /// </summary>
    bool RepairedByPlayer(FcsRecord saved) =>
        SavedPlayer is { Length: > 0 } player && !saved.Bools.GetValueOrDefault("destroyed", true)
        && saved.Strings.GetValueOrDefault("owner faction ID") == player;

    /// <summary>
    /// Whether a saved <c>destroyed</c> is only the level file's, written by a save from before housing stood such homes up (docs/game/
    /// buildings-production.md "Ruined by residents"): the building stands by its residents, the save has the same residents in it and no rebuild
    /// site of the player's.
    /// </summary>
    static bool SavedLevelRuin(TownBuilding b, FcsRecord saved) =>
        b.RestoredByResidents && !saved.Bools.GetValueOrDefault("meitou town construction")
        && saved.Strings.GetValueOrDefault(ResidentRuins.ResidentsTemplateKey) == b.ResidentsTemplate;

    /// <summary>
    /// Writes a property's Meitou keys into its building state: its rebuild site's state when it has one; a bought house's owner, its doors'
    /// owners (<c>owner faction ID&lt;i&gt;</c>) and its empty <c>residents</c> link (docs/formats/save.md "Zone files").
    /// </summary>
    void WriteProperty(string building, FcsRecord state)
    {
        if (!_properties.TryGetValue(building, out var p)) return;
        if (p.Construction is { } site)
        {
            state.Bools["meitou town construction"] = true;
            state.Bools["destroyed"] = site.Destroyed;
            state.Bools["is complete"] = site.Complete;
            state.Bools["pause"] = site.Paused;
            state.Floats["construction progress"] = site.Progress;
            for (int i = 0; i < site.Materials.Length; i++) state.Floats[$"{i}construction mats"] = site.Materials[i].Delivered;
        }

        if (!p.Purchased && p.Construction is null) return;
        state.Strings["owner faction ID"] = p.Data.Factions[p.Owner].Id;
        state.Bools["sell home"] = false;
        state.Bools["is public night"] = false;

        // Nobody lives in a bought house: the original's save of one has no residents link (Observed, ShopCounterSold; no player-owned building
        // of three original saves has one), where the record kept the evicted squad's.
        if (p.Purchased) Hand.Null.Write(state, "residents");

        // Each door's owner is the house's (Observed: all 466 doors of five original saves, ShopCounterSold's bought Longhouse among them).
        foreach (var door in OfBuilding(building).Where(f => p.Purchased && f.Kind == FixtureKind.Door))
            state.Strings["owner faction ID" + door.DoorIndex.ToString(CultureInfo.InvariantCulture)] = p.Data.Factions[p.Owner].Id;
    }

    /// <summary>
    /// Registers the player's property as a town construction site, its state from the save, else the level's.
    /// </summary>
    void RegisterTownConstruction(World world, Property p, bool? ruin = null)
    {
        if (p.Owner != world.Player.Faction || p.Owner < 0 || p.Construction is not null) return;
        var b = p.Building;
        var saved = Saved?.GetValueOrDefault(b.Key);
        bool Bool(string key, bool fallback) => saved?.Bools.GetValueOrDefault(key, fallback) ?? b.LevelState?.GetBool(key, fallback) ?? fallback;
        float Float(string key, float fallback) => saved?.Floats.GetValueOrDefault(key, fallback) ?? b.LevelState?.GetFloat(key, fallback)
                ?? fallback;
        var data = BuildingBuildData.From(p.Data.Db.Find(b.BuildingId)!);
        bool destroyed = ruin ?? IsDestroyed(b), complete = Bool("is complete", true);
        float progress = Math.Clamp(Float("construction progress", data.Target), 0, data.Target);

        // Keep a repaired level ruin in the overlay even after completion, to replace its original ruin mesh.
        if (!destroyed && complete && progress >= data.Target && !b.Destroyed
                && !(saved?.Bools.GetValueOrDefault("meitou town construction") ?? false))
            return;

        var placement = b.Resolved?.Placement ?? new BuildingPlacement(b.Zone, b.InstanceId, b.BuildingId, b.Position,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, b.Yaw), b.LevelState?.StringId, b.Position.Y);
        placement = placement with { WithoutInterior = p.Purchased };   // a bought house's furniture is gone (EmptyInterior)
        var site = new BuildSite
        {
            Id = b.Key,
            Record = b.BuildingId,
            Name = b.Name,
            Position = b.Position,
            Rotation = placement.Rotation,
            LevelPlacement = placement,
            LevelTownName = p.Data.Sites[p.Site].Town.Name,
            Owner = p.Owner,
            Target = data.Target,
            Rate = data.Rate,
            Complete = complete,
            Destroyed = destroyed,
            Progress = progress,
            Paused = Bool("pause", false),
            Public = _publicFlags.GetValueOrDefault(b.Key, Bool("is public day", true)),
            Materials = data.Materials.Select((m, i) => new MaterialSlot(m.Item, m.Required)
            { Delivered = Math.Clamp(Float($"{i}construction mats", complete ? m.Required : 0), 0, m.Required) }).ToArray(),
        };
        p.Construction = site;
        world.Buildings.Add(site);
    }

    /// <summary>
    /// Restore doors and indoor sight when a purchased ruin enters construction; keep its interior fixtures and ownership.
    /// </summary>
    public void RestoreTownShell(World world, BuildSite site)
    {
        if (!_properties.TryGetValue(site.Id, out var p)) return;
        if (p.Data.Sight is { } sight) world.Sight.Add(sight, SightBuilding(p.Building, false));
        if (!_byBuilding.TryGetValue(site.Id, out var list)
            || list.Any(f => f.Kind == FixtureKind.Door) || p.Data.Db.Find(site.Record) is not { } record) return;
        var doors = new List<Fixture>();
        AddDoors(world, p.Data.Db, record, site.Id, p.Site, p.Owner, p.Data.Factions[p.Owner], site.Position, p.Building.Yaw,
            p.Building.Resolved?.Placement.Rotation, doors);
        foreach (var door in doors)
        {
            list.Add(door);
            _all.Add(door);
            _byId[door.Id] = door;
        }

        // A stale door list keeps the new doors out of the snapshot: the renderer then draws them shut while walkers pass (#522).
        _doorPicks = null;
        _doorsByBuilding = null;
        _doorList = null;
        SyncDoors(site.Id);
    }

    /// <summary>
    /// The building with its destroyed flag set for the host's sight.
    /// </summary>
    static TownBuilding SightBuilding(TownBuilding b, bool destroyed) => b with
    {
        Destroyed = destroyed,
        Resolved = b.Resolved is { } r ? r with { Destroyed = destroyed } : null,
    };

    /// <summary>
    /// Adds the player's and bought properties' owners to the world's state hash, in key order.
    /// </summary>
    void HashProperties(ref StateHasher h)
    {
        foreach (var (key, p) in _properties.Where(p => p.Value.Purchased || p.Value.Owner == p.Value.Data.PlayerFaction).OrderBy(p => p.Key,
                StringComparer.Ordinal))
        {
            h.Add(Rng.StableHash(key));
            h.Add(p.Owner);
        }
    }
}