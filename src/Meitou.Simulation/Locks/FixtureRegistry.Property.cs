using System.Numerics;
using Meitou.Simulation.Construction;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;

namespace Meitou.Simulation.Locks;

/// <summary>Confirms the quoted price of a town house; the simulation rechecks eligibility and funds.</summary>
public sealed record BuyBuilding(string Building, long Price) : SimCommand;

public sealed partial class FixtureRegistry
{
    sealed class Property(PopulationData data, int site, TownBuilding building, int owner)
    {
        public PopulationData Data { get; } = data;
        public int Site { get; } = site;
        public TownBuilding Building { get; } = building;
        public int Owner { get; set; } = owner;
        public bool Purchased { get; set; }
        public BuildSite? Construction { get; set; }
    }
    readonly Dictionary<string, Property> properties = new(StringComparer.Ordinal);
    IReadOnlySet<string>? ownedProperties;
    int ownedFaction = -1;

    void RegisterProperty(World world, PopulationData data, int site, TownBuilding building, int owner, bool purchased)
    {
        var record = data.Db.Find(building.BuildingId);
        if (record is null || record.GetBool("is wall gate") || record.GetBool("links together")) return;
        int? function = TownBuildings.FunctionOf(data.Db, record, functions)?.GetInt("function");
        if (function != 19 && !(function is null && record.GetReferences("interior").Count > 0)) return;
        properties[building.Key] = new(data, site, building, owner) { Purchased = purchased };
        ownedProperties = null;
        RegisterTownConstruction(world, properties[building.Key]);
    }

    public int? PropertyOwner(string building) => properties.TryGetValue(building, out var p) ? p.Owner : null;

    /// <summary>Whether <paramref name="faction"/> owns a house in the town site <paramref name="site"/> (a registered site's properties only).</summary>
    public bool OwnsPropertyIn(int site, int faction)
    {
        if (site < 0 || faction < 0) return false;
        foreach (var p in properties.Values)
            if (p.Site == site && p.Owner == faction) return true;
        return false;
    }

    public bool OwnsBuilding(string building, int faction) => faction >= 0 &&
        (properties.TryGetValue(building, out var p) ? p.Owner == faction : OfBuilding(building).Any(f => f.Owner == faction));

    public IReadOnlySet<string> OwnedProperties(int faction)
    {
        if (ownedProperties is null || ownedFaction != faction)
        {
            ownedFaction = faction;
            ownedProperties = properties.Where(p => faction >= 0 && p.Value.Owner == faction).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        }
        return ownedProperties;
    }

    public long? SalePrice(string building, int player)
    {
        if (player < 0 || !properties.TryGetValue(building, out var p) || p.Owner < 0 || p.Owner == player || p.Purchased) return null;
        var data = p.Data;
        if (data.Sites[p.Site].Town.Type is not (TownType.Outpost or TownType.Town or TownType.Village)
            || data.SiteFactions[p.Site] == player || data.Relations.IsHostile(p.Owner, player)) return null;
        var saved = Saved?.GetValueOrDefault(building);
        var state = p.Building.LevelState;
        bool sale = saved?.Bools.TryGetValue("sell home", out bool savedSale) == true ? savedSale
            : state?.Bools.TryGetValue("sell home", out bool levelSale) == true ? levelSale
            : data.Db.Find(p.Building.ResidentsTemplate)?.GetBool("sell home") ?? (saved is null && state is null);
        bool inside = saved is not null ? saved.Bools.GetValueOrDefault("is inside building") : state?.GetBool("is inside building") ?? false;
        if (!sale || inside || data.Db.Find(p.Building.BuildingId) is not { } record) return null;
        bool destroyed = saved?.Bools.GetValueOrDefault("destroyed") ?? p.Building.Destroyed;
        float multiplier = data.Db.Find(data.Factions[p.Owner].Id)?.GetFloat("building cost mult", 1) ?? 1;
        float price = (destroyed ? 0.375f : 1) * BuildingBuildData.From(record).Target * multiplier * 400;
        return float.IsFinite(price) && price >= 0 && price <= int.MaxValue ? (long)price : null;
    }

    public void Buy(World world, BuyBuilding order)
    {
        if (SalePrice(order.Building, world.Player.Faction) is not { } price || price != order.Price) return;
        if (world.Player.Money < price) { world.Post(Meitou.Localisation.Loc.Tr("You can't afford that.")); return; }
        var p = properties[order.Building];
        world.Player.Money -= price;
        p.Owner = world.Player.Faction;
        p.Purchased = true;
        resetBuildings.Remove(order.Building);
        ownedProperties = null;
        foreach (var f in OfBuilding(order.Building)) f.Owner = p.Owner;
        containerSpots = null; doorPicks = null; bedSpots = null; cageList = null; dummySpots = null;
        world.ItemSlots.TransferBuilding(world, p.Building, p.Owner);
        SetPublic(order.Building, false);
        RegisterTownConstruction(world, p);
    }

    /// <summary>
    /// A town replaced (docs/game/factions-squads-towns.md "Town overrides"): its buildings that are not the player's go back to their level owner under the town's new faction.
    /// Those in the town's own zone also lose their saved state, as the original drops it from the zone file (<c>FUN_1409fe340</c>): they are registered again as the level has
    /// them (locks, doors, contents), and a held character is bound to its cage or bed again. A town not registered yet takes all of that when it is.
    /// </summary>
    public void TownReplaced(World world, PopulationData data, int site)
    {
        var town = data.Sites[site];
        int townFaction = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        bool Players(TownBuilding b) => properties.TryGetValue(b.Key, out var p) ? p.Purchased || p.Owner == player
            : sites.Contains(site) ? OfBuilding(b.Key).Any(f => f.Owner == player)
            : Saved?.GetValueOrDefault(b.Key)?.Strings.GetValueOrDefault("owner faction ID") is { Length: > 0 } id && data.FactionIndex(id) == player;
        int LevelOwner(TownBuilding b) => b.OwnerFactionId is { } o && data.FactionIndex(o) is var fi and >= 0 ? fi : townFaction;
        var reset = town.Buildings.Where(b => b.Zone == town.Zone && !Players(b)).Select(b => b.Key).ToHashSet(StringComparer.Ordinal);
        if (Saved is { } saved && reset.Any(saved.ContainsKey))
            Saved = saved.Where(s => !reset.Contains(s.Key)).ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        foreach (var b in town.Buildings)
            if (reset.Contains(b.Key)) resetBuildings[b.Key] = (b, data, LevelOwner(b));
        if (!sites.Contains(site)) return;
        foreach (var b in town.Buildings)
        {
            if (reset.Contains(b.Key))
            {
                Unregister(b.Key);
                properties.Remove(b.Key);
                RegisterBuilding(world, data, site, b, townFaction);
            }
            else if (Players(b)) continue;
            int owner = LevelOwner(b);
            if (properties.TryGetValue(b.Key, out var p)) p.Owner = owner;
            foreach (var f in OfBuilding(b.Key)) f.Owner = owner;
            world.ItemSlots.TransferBuilding(world, b, owner);
        }
        Bind(world);
        ownedProperties = null;
        containerSpots = null; doorPicks = null; bedSpots = null; cageList = null; dummySpots = null;
    }

    // The buildings a town override reset to the level, with their level owner then: their saved record (patched, not rewritten, by a save) must not bring the old state back.
    // A building the player buys afterwards leaves it.
    readonly Dictionary<string, (TownBuilding Building, PopulationData Data, int Owner)> resetBuildings = new(StringComparer.Ordinal);

    /// <summary>A reset building's keys the live writers do not touch: its owner (its fixtures', else the one it was reset to) and its level <c>destroyed</c>.</summary>
    void WriteReset(string building, FcsRecord state)
    {
        if (!resetBuildings.TryGetValue(building, out var reset)) return;
        int owner = OfBuilding(building).FirstOrDefault()?.Owner ?? reset.Owner;
        if ((uint)owner < (uint)reset.Data.Factions.Count) state.Strings["owner faction ID"] = reset.Data.Factions[owner].Id;
        else state.Strings.Remove("owner faction ID");
        state.Bools["destroyed"] = reset.Building.Destroyed;
    }

    /// <summary>
    /// Writes <see cref="WriteReset"/>'s keys into the saved records of every reset building, by instance id: also those with no fixtures or whose town was not registered, which
    /// <see cref="WriteBuilding"/> never sees.
    /// </summary>
    public void WriteResets(IReadOnlyDictionary<string, FcsRecord> states)
    {
        foreach (var building in resetBuildings.Keys)
            if (states.TryGetValue(building, out var state)) WriteReset(building, state);
    }

    void WriteProperty(string building, FcsRecord state)
    {
        if (!properties.TryGetValue(building, out var p)) return;
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
    }

    void RegisterTownConstruction(World world, Property p)
    {
        if (p.Owner != world.Player.Faction || p.Owner < 0 || p.Construction is not null) return;
        var b = p.Building;
        var saved = Saved?.GetValueOrDefault(b.Key);
        bool Bool(string key, bool fallback) => saved?.Bools.GetValueOrDefault(key, fallback) ?? b.LevelState?.GetBool(key, fallback) ?? fallback;
        float Float(string key, float fallback) => saved?.Floats.GetValueOrDefault(key, fallback) ?? b.LevelState?.GetFloat(key, fallback) ?? fallback;
        var data = BuildingBuildData.From(p.Data.Db.Find(b.BuildingId)!);
        bool destroyed = Bool("destroyed", b.Destroyed), complete = Bool("is complete", true);
        float progress = Math.Clamp(Float("construction progress", data.Target), 0, data.Target);
        // Keep a repaired level ruin in the overlay even after completion, to replace its original ruin mesh.
        if (!destroyed && complete && progress >= data.Target && !b.Destroyed && !(saved?.Bools.GetValueOrDefault("meitou town construction") ?? false)) return;
        var placement = b.Resolved?.Placement ?? new BuildingPlacement(b.Zone, b.InstanceId, b.BuildingId, b.Position,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, b.Yaw), b.LevelState?.StringId, b.Position.Y);
        var site = new BuildSite
        {
            Id = b.Key, Record = b.BuildingId, Name = b.Name, Position = b.Position, Rotation = placement.Rotation,
            LevelPlacement = placement, LevelTownName = p.Data.Sites[p.Site].Town.Name, Owner = p.Owner,
            Target = data.Target, Rate = data.Rate, Complete = complete, Destroyed = destroyed, Progress = progress,
            Paused = Bool("pause", false), Public = publicFlags.GetValueOrDefault(b.Key, Bool("is public day", true)),
            Materials = data.Materials.Select((m, i) => new MaterialSlot(m.Item, m.Required)
                { Delivered = Math.Clamp(Float($"{i}construction mats", complete ? m.Required : 0), 0, m.Required) }).ToArray(),
        };
        p.Construction = site;
        world.Buildings.Add(site);
    }

    /// <summary>Restore doors and indoor sight when a purchased ruin enters construction; keep its interior fixtures and ownership.</summary>
    public void RestoreTownShell(World world, BuildSite site)
    {
        if (!properties.TryGetValue(site.Id, out var p)) return;
        if (p.Data.Sight is { } sight) world.Sight.Add(sight, SightBuilding(p.Building, false));
        if (!byBuilding.TryGetValue(site.Id, out var list)
            || list.Any(f => f.Kind == FixtureKind.Door) || p.Data.Db.Find(site.Record) is not { } record) return;
        var doors = new List<Fixture>();
        AddDoors(world, p.Data.Db, record, site.Id, p.Site, p.Owner, p.Data.Factions[p.Owner], site.Position, p.Building.Yaw, doors);
        foreach (var door in doors) { list.Add(door); all.Add(door); byId[door.Id] = door; }
        doorPicks = null;
        SyncDoors(site.Id);
    }

    static TownBuilding SightBuilding(TownBuilding b, bool destroyed) => b with
    {
        Destroyed = destroyed,
        Resolved = b.Resolved is { } r ? r with { Destroyed = destroyed } : null,
    };

    void HashProperties(ref StateHasher h)
    {
        foreach (var (key, p) in properties.Where(p => p.Value.Purchased || p.Value.Owner == p.Value.Data.PlayerFaction).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            h.Add(Rng.StableHash(key)); h.Add(p.Owner);
        }
    }
}
