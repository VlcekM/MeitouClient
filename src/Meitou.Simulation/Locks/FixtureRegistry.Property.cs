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

    void RegisterProperty(World world, PopulationData data, int site, TownBuilding building, int owner)
    {
        var record = data.Db.Find(building.BuildingId);
        if (record is null || record.GetBool("is wall gate") || record.GetBool("links together")) return;
        int? function = TownBuildings.FunctionOf(data.Db, record, functions)?.GetInt("function");
        if (function != 19 && !(function is null && record.GetReferences("interior").Count > 0)) return;
        properties[building.Key] = new(data, site, building, owner);
        ownedProperties = null;
        RegisterTownConstruction(world, properties[building.Key]);
    }

    public int? PropertyOwner(string building) => properties.TryGetValue(building, out var p) ? p.Owner : null;

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
            || data.SiteFactions[p.Site] == player || data.Relations.IsAlly(p.Owner, player)) return null;
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
        ownedProperties = null;
        foreach (var f in OfBuilding(order.Building)) f.Owner = p.Owner;
        containerSpots = null; doorPicks = null; bedSpots = null; cageList = null;
        world.ItemSlots.TransferBuilding(world, p.Building, p.Owner);
        SetPublic(order.Building, false);
        RegisterTownConstruction(world, p);
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
