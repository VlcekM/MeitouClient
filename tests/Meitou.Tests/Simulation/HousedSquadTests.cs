using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Trading;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Housed squads of the installed game's towns: prisoners caged at spawn and building shops (docs/simulation.md "Cages, beds and locks", "Trade").
/// Skipped without the game.
/// </summary>
[Slow]
public class HousedSquadTests(ITestOutputHelper output)
{
    static PopulationData? s_cached;
    static readonly Lock Gate = new();

    static PopulationData Load()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        lock (Gate)
        {
            if (s_cached is not null) return s_cached;
            var install = InstallData.Install!;
            var db = InstallData.FullLoadOrder!;
            var levels = WorldLevelData.LoadBaseGame(install, includeInteriors: true);
            using var map = TerrainHeightmap.Open(install);
            var buildings = TownBuildings.Build(db, levels, new BuildingLayouts(db, levels.Interiors), (x, z) => map.HeightAt((float)x, (float)z));
            return s_cached = PopulationData.Create(db, levels.Towns(), buildings: buildings);
        }
    }

    static SimWorld Town(PopulationData data, TownSite site, StandardParts parts)
    {
        var walk = new OpenGroundWalkability((_, _) => 300);
        var set = StandardSystems.Build(data, walk,
                new StandardSystemOptions { Parts = StandardParts.Population | StandardParts.Movement | parts,
                Population = new PopulationSettings { CheckEveryTicks = 1 } });
        var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, walk, set.Systems);
        world.Player.Faction = data.PlayerFaction;
        world.Commands.Enqueue(new FocusCommand(site.Position) { Tick = 0 });
        return world;
    }

    [Fact]
    public void A_town_whose_home_squad_makes_prisoners_has_them_caged_in_that_home_for_life()
    {
        var data = Load();
        var db = data.Db;

        // The first town with a level-data home whose template makes prisoners and that has cages.
        var (site, home) = data.Sites.SelectMany(s => s.Buildings.Select(b => (Site: s, Building: b)))
            .First(x => x.Building.ResidentsTemplate.Length > 0 && db.Find(x.Building.ResidentsTemplate) is { } t
                    && t.GetReferences("prisoners").Count > 0 && x.Building.Fixtures.Any(f => f.IsCage));
        using var world = Town(data, site, 0);
        world.RunTicks(3);
        var table = world.Characters;
        var caged = world.Fixtures.OfBuilding(home.Key).Where(f => f.Kind == FixtureKind.Cage && !f.Occupant.IsNone).ToList();
        output.WriteLine($"{site.Town.Name}, {home.Name} ({home.ResidentsTemplate}): {caged.Count} of {home.Fixtures.Count(f => f.IsCage)} "
            + "cages hold " + string.Join(", ", caged.Select(f => table.Cold(f.Occupant.Slot)!.Name)));

        Assert.NotEmpty(caged);
        Assert.All(caged, f =>

        {
            var c = table.Cold(f.Occupant.Slot)!;

            Assert.True(c.IsImprisoned && !c.Kidnapped);
            Assert.Equal(f.Id, c.InWhat);
            Assert.Equal(PopulationSystem.SpawnSentence, c.SentenceLength);
            Assert.True(f.Lock!.Locked);
        });
    }

    /// <summary>
    /// Going to bed (153, 239) weighs row 98's sleeping desire (AI slot 30, <c>FUN_140319a60</c> registers it for 98, 153, 175, 239 and 261) times
    /// its bed finder's 1 (<c>FUN_1408cfe00</c>; docs/game/ai-tasks.md "Following"): a guard of Stack with a bed goal and a bed of its home, awake,
    /// weighs it at 1 with its last bed 5 hours ago and at 0.3 having used a bed 10 minutes ago. Catches the goal weighed at a flat 1.
    /// </summary>
    [Fact]
    public void A_town_guard_that_used_a_bed_within_20_minutes_weighs_going_to_bed_at_0_3()
    {
        var data = Load();
        var site = data.Sites.First(s => s.Town.Name == "Stack");
        using var world = Town(data, site, StandardParts.Ai);
        world.RunTicks(60);
        var ai = world.SystemOf<Meitou.Simulation.Ai.AiSystem>()!;
        var sleepers = world.Squads.All
            .Where(s => s.Ai?.Current is { } p && p.Roles.Any(r => r.Any(g => IsBedGoal(g.Task.Type))))
            .SelectMany(s => s.Members.Select(m => (Squad: s, Id: m)))
            .Where(x => world.Characters.Cold(x.Id.Slot) is { Ai: not null, InSomething: HeldState.None })
            .ToList();
        float Desire(Squad squad, CharacterId id, double usedHoursAgo)
        {
            var cold = world.Characters.Cold(id.Slot)!;
            cold.BedUsedHours = world.GameHours - usedHoursAgo;
            var goal = squad.Ai!.Current!.Roles.SelectMany(r => r).First(g => IsBedGoal(g.Task.Type));
            var type = goal.Task.Type;
            return ai.Desire(world, Meitou.Simulation.Ai.TaskBehaviours.Of(type), type, Meitou.Simulation.Ai.TaskProfiles.Of(type), squad,
                    squad.Ai, cold.Ai!, id, goal.Task.Target);
        }

        bool Guard(Squad s) => s.Name.Contains("guard", StringComparison.OrdinalIgnoreCase);
        var (squad, guard) = sleepers.First(x => Guard(x.Squad) && Desire(x.Squad, x.Id, 5) == 1);
        output.WriteLine($"{squad.Name}: {world.Characters.Cold(guard.Slot)!.Name}");

        Assert.Equal(0.3f, Desire(squad, guard, 10.0 / 60));
    }

    /// <summary>
    /// Whether the goal is one of the going-to-bed rows (153, 239).
    /// </summary>
    static bool IsBedGoal(Data.Gameplay.Ai.TaskType type) =>
        type is Data.Gameplay.Ai.TaskType.GoHomeAndGoToBed or Data.Gameplay.Ai.TaskType.GoHomeAndGoToBedSecure;

    [Fact]
    public void A_building_shops_trade_window_shows_its_containers_goods_in_one_grid_and_a_purchase_leaves_its_container()
    {
        var data = Load();
        var squin = data.Sites.First(s => s.Town.Name == "Squin");
        using var world = Town(data, squin, StandardParts.Trade);
        world.RunTicks(TickPhases.TradeCheck + 1);
        var squad = world.Squads.All.First(s => s.Shop?.Building is not null);
        var shop = squad.Shop!;
        var grids = shop.Containers.Select(id => world.Fixtures.Find(id)!.Contents!).ToList();
        int Units() => grids.Sum(g => g.Items.Where(i => i.ItemFunction != PlayerSystem.MoneyFunction).Sum(i => i.Quantity));
        int units = Units();

        var hero = PlayerNextTo(world, data, world.Characters.Next[squad.Leader.Slot].Position);
        world.Commands.Enqueue(new OpenTrade(hero, squad.Leader) { Tick = world.Tick });
        world.RunTicks(5);
        var trade = world.Systems.OfType<TradeSystem>().Single();
        var view = trade.PublishTrade()!;

        // The original's merged section: the widest container's columns, ceil(cells / columns) rows (more only when the copies do not fit).
        var (columns, rows) = shop.ViewSize;
        Assert.Equal(columns, view.StockWidth);
        Assert.InRange(view.StockHeight, rows, rows * 2);
        Assert.Equal(units, view.Stock.Sum(i => i.Quantity));

        var bought = view.Stock.OrderBy(i => i.Width * i.Height).First();
        world.Commands.Enqueue(new BuyItem(bought.Token, 1) { Tick = world.Tick });
        world.RunTicks(1);
        Assert.Equal(units - 1, Units());
        Assert.Equal(bought.Record, Assert.Single(world.Characters.Cold(hero.Slot)!.Inventory!.Items).Record);
    }

    /// <summary>
    /// A rich player character standing at <paramref name="at"/>.
    /// </summary>
    static CharacterId PlayerNextTo(SimWorld world, PopulationData data, Vector3 at)
    {
        var cold = new Combat.Combatant { Name = "hero" }.MakeCold();
        cold.Faction = data.PlayerFaction;
        cold.IsPlayer = true;
        cold.Inventory = new Inventory();
        world.Player.Money = 1_000_000;
        return world.Characters.Spawn(new CharacterHot { Position = at + new Vector3(10, 0, 0), Health = 100, MaxSpeed = 60, WalkSpeed = 15,
                Task = (byte)CharacterTask.Idle }, cold, 0);
    }

    [Fact]
    public void Squins_housed_traders_sell_from_their_homes_stock_containers_with_their_vendor_money()
    {
        var data = Load();
        var squin = data.Sites.First(s => s.Town.Name == "Squin");
        using var world = Town(data, squin, StandardParts.Trade);
        world.RunTicks(TickPhases.TradeCheck + 1);
        var shops = world.Squads.All.Where(s => s.Shop?.Building is not null).ToList();
        foreach (var s in shops)
        {
            output.WriteLine($"{s.Name} in {s.HomeBuilding!.Name}: {s.Shop!.Stock.Width} x {s.Shop.Stock.Height}, {s.Money} cats");
        }

        Assert.NotEmpty(shops);

        foreach (var s in shops)
        {
            var template = SquadTemplate.From(data.Db.Find(s.TemplateId)!);

            Assert.Equal(s.HomeBuilding!.Key, s.Shop!.Building);
            Assert.Empty(template.Prisoners);

            var boxes = s.HomeBuilding.Fixtures.Where(f => f.HoldsShopStock).ToList();
            int cells = boxes.Sum(f => f.StorageWidth * f.StorageHeight);
            int width = s.Shop.Stock.Width;

            Assert.Equal(Math.Max(18, boxes.Max(f => f.StorageWidth)), width);
            Assert.Equal((cells + width - 1) / width, s.Shop.Stock.Height);   // the original's merged section
            Assert.Contains(boxes, f => world.Fixtures.Find(f.Id)?.Contents is { Items.Count: > 0 });

            if (template.VendorMoney > 0)

                Assert.True(s.Money >= template.VendorMoney - (int)(template.VendorMoney * 0.4), $"{s.Name}: {s.Money}");
        }
    }

    [Fact]
    public void A_home_whose_template_makes_no_squad_but_names_vendors_is_stocked_when_the_town_is_made()
    {
        var data = Load();
        var db = data.Db;

        // A level-data home with an empty template that holds vendor lists or special items (a ruin's loot, FUN_140583a10's empty path) and stock
        // containers.
        var (site, home) = data.Sites.SelectMany(s => s.Buildings.Select(b => (Site: s, Building: b)))
            .First(x => x.Building.ResidentsTemplate.Length > 0 && x.Building.Fixtures.Any(f => f.HoldsShopStock)
                    && db.Find(x.Building.ResidentsTemplate) is { } r
                && r.GetReferences("squad").Count + r.GetReferences("squad2").Count + r.GetReferences("animals").Count
                    + r.GetReferences("animals2").Count + r.GetReferences("leader").Count + r.GetReferences("choosefrom list").Count == 0
                && r.GetReferences("vendors").Count > 0 && Site0(x.Site));
        using var world = Town(data, site, StandardParts.Trade);
        world.RunTicks(3);
        var held = world.Fixtures.OfBuilding(home.Key).Where(f => f.Contents is not null).Sum(f => f.Contents!.Items.Count);
        output.WriteLine($"{site.Town.Name}, {home.Name} ({home.ResidentsTemplate}): {held} stacks in its containers");

        Assert.True(held > 0);
    }

    static bool Site0(TownSite site) => site.Town.Faction is not null;
}
