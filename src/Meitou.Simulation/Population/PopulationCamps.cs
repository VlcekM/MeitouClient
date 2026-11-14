using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Economy;
using Meitou.Data.World;

namespace Meitou.Simulation.Population;

/// <summary>
/// The camps (docs/game/factions-squads-towns.md "Nest furniture"): what a nest, or a town with <c>spawn in town centre</c>, scatters at its place
/// (<see cref="NestFurniture"/>). A nest's stands while its zone is loaded; a town's once it is first made, for good (its fixtures stay registered as
/// the town's do). The items go down once per camp, and only in a zone the loaded save has no file of (the original lays them only in a zone loaded
/// fresh).
/// </summary>
public sealed partial class PopulationSystem
{
    /// <summary>
    /// A camp's buildings and meshes go up (the old ones of the camp come down first), their cages and locks registered as its faction's, and its
    /// items laid when due.
    /// </summary>
    void StandCamp(World world, int camp, NestLayout layout, int owner, Vector2 centre, string record, int site)
    {
        FoldCamp(world, camp);
        world.NestBuildings.Set(camp, layout, owner, centre, record);
        foreach (var piece in layout.Pieces) world.Fixtures.RegisterNestBuilding(world, _data, piece, owner, site);
        if (layout.Drops.Count > 0 && !world.SavedZones.Contains(WorldLayout.ZoneOf(centre.X, centre.Y)) && world.NestBuildings.Stock(camp))
            LayItems(world, camp, layout.Drops, owner, centre);
    }

    /// <summary>
    /// A camp's buildings and meshes come down (a nest's when it unloads; it stands them up again when it loads).
    /// </summary>
    static void FoldCamp(World world, int camp)
    {
        var pieces = world.NestBuildings.Of(camp);
        if (pieces.Count > 0) world.Fixtures.UnregisterNestBuildings(world, pieces);
        world.NestBuildings.Remove(camp);
    }

    /// <summary>
    /// A town with <c>spawn in town centre</c> scatters at its own position when made (<c>Town::setup</c> queues it), again only when its TOWN record
    /// changed since. Any town also lays the items of its residents' nests (<see cref="ResidentNestDrops"/>) once, in a fresh zone.
    /// </summary>
    void StandTownCamp(World world, TownSite site)
    {
        var town = site.Town;
        int camp = NestBuildings.TownCamp(site.Index);
        var at = new Vector2(site.Position.X, site.Position.Z);
        int owner = site.Index < _data.SiteFactions.Count ? _data.SiteFactions[site.Index] : -1;
        if (!town.SpawnInTownCentre)
        {
            LayResidentNests(world, site, camp, owner, at);
            return;
        }

        if (world.NestBuildings.RecordOf(camp) == town.Id) return;

        var layout = NestFurniture.Scatter(_data.Db, town, at, world.Walkability, world.Seed, $"town{site.Index}:");
        layout.Drops.AddRange(ResidentNestDrops(world, site));
        StandCamp(world, camp, layout, owner, at, town.Id, site.Index);
    }

    /// <summary>
    /// The residents' nest items of a town that stands no camp of its own, as the town's camp's items (kept past the dropped-item limit, saved with
    /// the town's handle).
    /// </summary>
    void LayResidentNests(World world, TownSite site, int camp, int owner, Vector2 at)
    {
        var drops = ResidentNestDrops(world, site);
        if (drops.Count > 0 && world.NestBuildings.Stock(camp)) LayItems(world, camp, drops, owner, at);
    }

    /// <summary>
    /// The items (<c>loot spawn</c>, and <c>debris</c> with physics) of the TOWN each home's resident template names as its <c>nest</c>, rolled
    /// round that home, building by building (docs/formats/zones.md "Nest debris and loot": <b>Observed</b> in original saves, whose items lie
    /// within the nest's <c>size radius</c> of a home of the template and not round the town's centre; ruins such as the Old Empire Watchtowers'
    /// have no lists of their own). A home in a zone the save has a file of lays nothing. The nest's buildings and meshes are not stood up
    /// (<b>Unknown</b> whether the original does).
    /// </summary>
    List<NestDrop> ResidentNestDrops(World world, TownSite site)
    {
        var drops = new List<NestDrop>();
        foreach (var home in site.Buildings)
        {
            if (world.SavedZones.Contains(WorldLayout.ZoneOf(home.Position.X, home.Position.Z))) continue;
            if (_data.Db.Find(world.Fixtures.ResidentsTemplate(home)) is not { } template) continue;

            foreach (var link in template.GetReferences("nest"))
                drops.AddRange(HomeNestDrops(world, site, home, link.TargetStringId));
        }

        return drops;
    }

    /// <summary>
    /// What the nest <paramref name="nestId"/> of <paramref name="home"/>'s residents scatters round the home's origin; none when it is no TOWN.
    /// </summary>
    List<NestDrop> HomeNestDrops(World world, TownSite site, TownBuilding home, string nestId)
    {
        if (_data.Db.Find(nestId) is not { Type: Meitou.Data.Fcs.FcsRecordType.Town } nest) return [];

        ulong seed = world.Seed ^ Rng.StableHash(nestId);
        var at = new Vector2(home.Position.X, home.Position.Z);
        return NestFurniture.Scatter(_data.Db, TownData.From(nest), at, world.Walkability, seed, $"town{site.Index}:{home.Key}:{nestId}:").Drops;
    }

    /// <summary>
    /// The camp's items on the ground: a <c>debris</c> item as it is, a <c>loot spawn</c> entry rolled from its VENDOR_LIST as an item slot's is
    /// (<see cref="Items.ItemSlots.Roll"/>, with the camp's faction for the grade). Loose, owned by nobody, tagged as the camp's (kept past the
    /// dropped-item limit, saved with its <c>persist</c> handle).
    /// </summary>
    void LayItems(World world, int camp, List<NestDrop> drops, int owner, Vector2 centre)
    {
        ulong key = Rng.Mix(SeedSalts.NestFurniture ^ (ulong)(uint)(int)centre.X ^ (ulong)(uint)(int)centre.Y << 32 ^ 0x1007UL);
        var rng = new RandomStream(world.Seed, key, RngPurpose.Loot);

        foreach (var drop in drops)
        {
            var item = drop.List
                ? _data.Db.Find(drop.Record) is { } list
                    ? Items.ItemSlots.Roll(_data, VendorList.From(_data.Db, list), owner, ref rng).Item
                    : null
                : _data.Items.Create(drop.Record, 1, 0);
            if (item is not null) world.GroundItems.Place(item, drop.Position, drop.Yaw, null, -1, camp);
        }
    }
}
