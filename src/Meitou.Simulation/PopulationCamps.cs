using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Economy;
using Meitou.Data.World;

namespace Meitou.Simulation;

/// <summary>
/// The camps (docs/game/factions-squads-towns.md "Nest furniture"): what a nest, or a town with <c>spawn in town centre</c>, scatters at its place (<see cref="NestFurniture"/>).
/// A nest's stands while its zone is loaded; a town's once it is first made, for good (its fixtures stay registered as the town's do). The items go down once per camp, and only
/// in a zone the loaded save has no file of (the original lays them only in a zone loaded fresh).
/// </summary>
public sealed partial class PopulationSystem
{
    /// <summary>A camp's buildings and meshes go up (the old ones of the camp come down first), their cages and locks registered as its faction's, and its items laid when due.</summary>
    void StandCamp(World world, int camp, NestLayout layout, int owner, Vector2 centre, string record, int site)
    {
        FoldCamp(world, camp);
        world.NestBuildings.Set(camp, layout, owner, centre, record);
        foreach (var piece in layout.Pieces) world.Fixtures.RegisterNestBuilding(world, data, piece, owner, site);
        if (layout.Drops.Count > 0 && !world.SavedZones.Contains(WorldLayout.ZoneOf(centre.X, centre.Y)) && world.NestBuildings.Stock(camp))
            LayItems(world, camp, layout.Drops, owner, centre);
    }

    /// <summary>A camp's buildings and meshes come down (a nest's when it unloads; it stands them up again when it loads).</summary>
    static void FoldCamp(World world, int camp)
    {
        var pieces = world.NestBuildings.Of(camp);
        if (pieces.Count > 0) world.Fixtures.UnregisterNestBuildings(world, pieces);
        world.NestBuildings.Remove(camp);
    }

    /// <summary>A town with <c>spawn in town centre</c> scatters at its own position when made (<c>Town::setup</c> queues it), again only when its TOWN record changed since.</summary>
    void StandTownCamp(World world, TownSite site)
    {
        var town = site.Town;
        int camp = NestBuildings.TownCamp(site.Index);
        if (!town.SpawnInTownCentre || world.NestBuildings.RecordOf(camp) == town.Id) return;
        var at = new Vector2(site.Position.X, site.Position.Z);
        var layout = NestFurniture.Scatter(data.Db, town, at, world.Walkability, world.Seed, $"town{site.Index}:");
        int owner = site.Index < data.SiteFactions.Count ? data.SiteFactions[site.Index] : -1;
        StandCamp(world, camp, layout, owner, at, town.Id, site.Index);
    }

    /// <summary>
    /// The camp's items on the ground: a <c>debris</c> item as it is, a <c>loot spawn</c> entry rolled from its VENDOR_LIST as an item slot's is (<see cref="Items.ItemSlots.Roll"/>, with
    /// the camp's faction for the grade). Loose, owned by nobody, tagged as the camp's (kept past the dropped-item limit, saved with its <c>persist</c> handle).
    /// </summary>
    void LayItems(World world, int camp, List<NestDrop> drops, int owner, Vector2 centre)
    {
        ulong key = Rng.Mix(SeedSalts.NestFurniture ^ (ulong)(uint)(int)centre.X ^ (ulong)(uint)(int)centre.Y << 32 ^ 0x1007UL);
        var rng = new RandomStream(world.Seed, key, RngPurpose.Loot);
        foreach (var drop in drops)
        {
            var item = drop.List
                ? data.Db.Find(drop.Record) is { } list ? Items.ItemSlots.Roll(data, VendorList.From(data.Db, list), owner, ref rng).Item : null
                : data.Items.Create(drop.Record, 1, 0);
            if (item is not null) world.GroundItems.Place(item, drop.Position, drop.Yaw, null, -1, camp);
        }
    }
}
