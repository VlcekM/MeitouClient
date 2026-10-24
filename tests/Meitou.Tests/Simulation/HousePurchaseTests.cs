using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Saving;
using static Meitou.Tests.Gameplay.EconomyTests;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class HousePurchaseTests
{
    internal static (PopulationData Data, TownBuilding House) Setup(bool destroyed = false, bool sale = true, int townType = 2, bool inside = false)
    {
        var db = Db(
            Rec("player", FcsRecordType.FACTION, r => r.Name = "Nameless"),
            Rec("owner", FcsRecordType.FACTION, r => r.Floats["building cost mult"] = 1),
            Rec("town", FcsRecordType.TOWN, r => { r.Ints["type"] = townType; r.References["faction"] = [new("owner", 0, 0, 0)]; }),
            Rec("shell", FcsRecordType.BUILDING_FUNCTIONALITY, r => r.Ints["function"] = 19),
            Rec("house", FcsRecordType.BUILDING, r => { r.Ints["build materials"] = 32; r.References["functionality"] = [new("shell", 0, 0, 0)]; r.References["doors"] = [new("door", 0, 0, 0)]; }),
            Rec("door", FcsRecordType.BUILDING),
            Rec("state", FcsRecordType.GAMESTATE_BUILDING, r => { r.Bools["sell home"] = sale; r.Bools["is inside building"] = inside; r.Bools["destroyed"] = destroyed; }));
        var house = new TownBuilding("home", "house", "House", new(100, 0, 100), 0, false, false, [])
        { LevelState = db.Find("state"), Destroyed = destroyed };
        var data = PopulationData.Create(db, [new TownPlacement("town-instance", "town", house.Position, null)],
            buildings: new Dictionary<string, List<TownBuilding>> { ["town-instance"] = [house] });
        return (data, house);
    }

    static SimWorld World(PopulationData data) => new(new WorldSettings { Seed = 1 }, new OpenGroundWalkability((_, _) => 0),
        [new PlayerSystem(), new LockSystem(GameConstants.FromDatabase(data.Db), BodyOptions.Default)]);

    [Theory]
    [InlineData(false, 12800)]
    [InlineData(true, 4800)]
    public void Buying_changes_ownership_once_and_survives_saved_building_state(bool destroyed, long price)
    {
        var (data, house) = Setup(destroyed);
        using var world = World(data);
        world.Player.Faction = data.PlayerFaction; world.Player.Money = 20000;
        world.Fixtures.RegisterSite(world, data, 0);
        world.Player.InspectedBuilding = house.Key; world.RunTick();
        Assert.Equal(price, world.Snapshot.InspectedBuilding!.SalePrice);
        Assert.False(world.Snapshot.InspectedBuilding.Owned);
        world.Commands.Enqueue(new BuyBuilding(house.Key, price) { Tick = world.Tick }); world.RunTick();
        Assert.Equal(20000 - price, world.Player.Money);
        Assert.True(world.Snapshot.InspectedBuilding!.Owned);
        Assert.Null(world.Snapshot.InspectedBuilding.SalePrice);
        Assert.Contains(house.Key, world.Snapshot.OwnedTownBuildings);
        Assert.All(world.Fixtures.OfBuilding(house.Key), f => Assert.Equal(data.PlayerFaction, f.Owner));
        world.Commands.Enqueue(new BuyBuilding(house.Key, price) { Tick = world.Tick }); world.RunTick();
        Assert.Equal(20000 - price, world.Player.Money);
        var state = Rec("saved", FcsRecordType.GAMESTATE_BUILDING);
        Assert.True(world.Fixtures.WriteBuilding(house.Key, state));
        Assert.Equal("player", state.Strings["owner faction ID"]); Assert.False(state.Bools["sell home"]);
        using var loaded = World(data);
        loaded.Player.Faction = data.PlayerFaction;
        loaded.Fixtures.Saved = new Dictionary<string, FcsRecord> { [house.Key] = state };
        loaded.Fixtures.RegisterSite(loaded, data, 0);
        loaded.Player.InspectedBuilding = house.Key; loaded.RunTick();
        Assert.True(loaded.Snapshot.InspectedBuilding!.Owned);
        Assert.Null(loaded.Snapshot.InspectedBuilding.SalePrice);
        loaded.Commands.Enqueue(new Meitou.Simulation.Construction.SetBuildingPublic(house.Key, true) { Tick = loaded.Tick }); loaded.RunTick();
        Assert.True(loaded.Snapshot.InspectedBuilding!.Public);
    }

    [Theory]
    [InlineData("money")]
    [InlineData("price")]
    [InlineData("sale")]
    [InlineData("town")]
    [InlineData("inside")]
    [InlineData("ally")]
    public void Invalid_purchases_do_not_take_money_or_change_ownership(string reason)
    {
        var (data, house) = Setup(sale: reason != "sale", townType: reason == "town" ? 4 : 2, inside: reason == "inside");
        using var world = World(data);
        world.Player.Faction = data.PlayerFaction; world.Player.Money = reason == "money" ? 12799 : 20000;
        if (reason == "ally") data.Relations.Set(data.FactionIndex("owner"), data.PlayerFaction, 100);
        world.Fixtures.RegisterSite(world, data, 0);
        long money = world.Player.Money;
        world.Commands.Enqueue(new BuyBuilding(house.Key, reason == "price" ? 1 : 12800) { Tick = 0 }); world.RunTick();
        Assert.Equal(money, world.Player.Money);
        Assert.False(world.Fixtures.OwnsBuilding(house.Key, data.PlayerFaction));
        if (reason is not ("money" or "price")) Assert.Null(world.Fixtures.SalePrice(house.Key, data.PlayerFaction));
    }

    [Fact, Slow]
    public void The_Hub_has_a_ruined_Storm_House_for_4800_in_the_original_data()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var db = InstallData.BaseGame!;
        var levels = InstallData.Levels!;
        var data = PopulationData.Create(db, levels.Towns(), buildings: TownBuildings.Build(db, levels, null, (_, _) => 0));
        using var world = World(data);
        world.Player.Faction = data.PlayerFaction;
        int hub = data.Sites.ToList().FindIndex(s => s.Town.Name == "The Hub");
        Assert.True(hub >= 0);
        world.Fixtures.RegisterSite(world, data, hub);
        var house = data.Sites[hub].Buildings.First(b => b.Name == "Storm House" && b.Destroyed && world.Fixtures.SalePrice(b.Key, data.PlayerFaction) == 4800);
        world.Player.Money = 10000;
        world.Commands.Enqueue(new BuyBuilding(house.Key, 4800) { Tick = world.Tick }); world.RunTick();
        world.VisitedZones.Add(house.Zone);
        var save = SaveCapture.Capture(world, data, new SaveClock(1, 13, 0), options: new SaveCaptureOptions { Levels = levels, Placements = levels.Towns().ToArray() });
        var states = HeldSave.BuildingStates(save);
        Assert.Equal(data.Factions[data.PlayerFaction].Id, states[house.Key].Strings["owner faction ID"]);
        using var loaded = World(data);
        loaded.Player.Faction = data.PlayerFaction;
        loaded.Fixtures.Saved = states;
        loaded.Fixtures.RegisterSite(loaded, data, hub);
        Assert.True(loaded.Fixtures.OwnsBuilding(house.Key, data.PlayerFaction));
        Assert.Null(loaded.Fixtures.SalePrice(house.Key, data.PlayerFaction));
    }
}
