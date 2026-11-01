using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Locks;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Housed squads of the installed game's towns: prisoners caged at spawn and building shops (docs/simulation.md "Cages, beds and locks", "Trade"). Skipped without the game.</summary>
[Slow]
public class HousedSquadTests(ITestOutputHelper output)
{
    static PopulationData? cached;
    static readonly Lock gate = new();

    static PopulationData Load()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        lock (gate)
        {
            if (cached is not null) return cached;
            var install = InstallData.Install!;
            var db = InstallData.FullLoadOrder!;
            var levels = WorldLevelData.LoadBaseGame(install, includeInteriors: true);
            using var map = TerrainHeightmap.Open(install);
            var buildings = TownBuildings.Build(db, levels, new BuildingLayouts(db, levels.Interiors), (x, z) => map.HeightAt((float)x, (float)z));
            return cached = PopulationData.Create(db, levels.Towns(), buildings: buildings);
        }
    }

    static SimWorld Town(PopulationData data, TownSite site, StandardParts parts)
    {
        var walk = new OpenGroundWalkability((_, _) => 300);
        var set = StandardSystems.Build(data, walk, new StandardSystemOptions { Parts = StandardParts.Population | StandardParts.Movement | parts, Population = new PopulationSettings { CheckEveryTicks = 1 } });
        var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, walk, set.Systems);
        world.Player.Faction = data.PlayerFaction;
        world.Commands.Enqueue(new FocusCommand(site.Position) { Tick = 0 });
        return world;
    }

    [Fact]
    public void A_town_whose_home_squad_makes_prisoners_has_them_caged_in_that_home_without_a_sentence()
    {
        var data = Load();
        var db = data.Db;
        // The first town with a level-data home whose template makes prisoners and that has cages.
        var (site, home) = data.Sites.SelectMany(s => s.Buildings.Select(b => (Site: s, Building: b)))
            .First(x => x.Building.ResidentsTemplate.Length > 0 && db.Find(x.Building.ResidentsTemplate) is { } t && t.GetReferences("prisoners").Count > 0 && x.Building.Fixtures.Any(f => f.IsCage));
        using var world = Town(data, site, 0);
        world.RunTicks(3);
        var table = world.Characters;
        var caged = world.Fixtures.OfBuilding(home.Key).Where(f => f.Kind == FixtureKind.Cage && !f.Occupant.IsNone).ToList();
        output.WriteLine($"{site.Town.Name}, {home.Name} ({home.ResidentsTemplate}): {caged.Count} of {home.Fixtures.Count(f => f.IsCage)} cages hold " +
            string.Join(", ", caged.Select(f => table.Cold(f.Occupant.Slot)!.Name)));
        Assert.NotEmpty(caged);
        Assert.All(caged, f =>
        {
            var c = table.Cold(f.Occupant.Slot)!;
            Assert.True(c.IsImprisoned && c.Kidnapped);
            Assert.Equal(f.Id, c.InWhat);
            Assert.Equal(0, c.SentenceLength);
            Assert.True(f.Lock!.Locked);
        });
    }

    [Fact]
    public void Squins_housed_traders_sell_from_their_homes_stock_containers_with_their_vendor_money()
    {
        var data = Load();
        var squin = data.Sites.First(s => s.Town.Name == "Squin");
        using var world = Town(data, squin, StandardParts.Trade);
        world.RunTicks(3);
        var shops = world.Squads.All.Where(s => s.Shop?.Building is not null).ToList();
        foreach (var s in shops) output.WriteLine($"{s.Name} in {s.HomeBuilding!.Name}: {s.Shop!.Stock.Width} x {s.Shop.Stock.Height}, {s.Shop.Stock.All().Sum(i => i.Quantity)} units, {s.Money} cats");
        Assert.NotEmpty(shops);
        foreach (var s in shops)
        {
            var template = SquadTemplate.From(data.Db.Find(s.TemplateId)!);
            Assert.Equal(s.HomeBuilding!.Key, s.Shop!.Building);
            Assert.Empty(template.Prisoners);
            int cells = s.HomeBuilding.Fixtures.Where(f => f.HoldsShopStock).Sum(f => f.StorageWidth * f.StorageHeight);
            Assert.Equal(Math.Max(18, s.HomeBuilding.Fixtures.Where(f => f.HoldsShopStock).Max(f => f.StorageWidth)), s.Shop.Stock.Width);
            int packed = (cells + s.Shop.Stock.Width - 1) / s.Shop.Stock.Width;   // the original's merged section; each container's own rectangle takes a few rows more
            Assert.InRange(s.Shop.Stock.Height, packed, packed * 11 / 10 + 3);
            Assert.NotEmpty(s.Shop.Stock.Items);
            if (template.VendorMoney > 0) Assert.True(s.Money >= template.VendorMoney - (int)(template.VendorMoney * 0.4), $"{s.Name}: {s.Money}");
        }
    }
}
