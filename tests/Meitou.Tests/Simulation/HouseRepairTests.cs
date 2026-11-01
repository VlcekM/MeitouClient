using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;
using Meitou.Simulation.Saving;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Simulation;

public class HouseRepairTests
{
    internal sealed class Rig : IDisposable
    {
        public PopulationData Data { get; }
        public TownBuilding House { get; }
        public Meitou.Simulation.World World { get; }
        public ConstructionSystem Construction { get; }
        public ItemFactory Items { get; }
        public CharacterId Builder { get; }
        public CharacterCold Cold => World.Characters.Cold(Builder.Slot)!;
        public BuildSite Site => World.Buildings.Find(House.Key)!;
        public Rig(FcsRecord? saved = null, ISightSource? sight = null)
        {
            (Data, House) = HousePurchaseTests.Setup(destroyed: true);
            Data.Sight = sight;
            var file = new FcsFile();
            file.Records.Add(new FcsRecord { StringId = BuildingBuildData.BuildingMaterialsItem, RecordType = FcsRecordType.ITEM, Name = "Building Materials", Flags = 0x10 });
            Data.Db.Apply(file, "test.mod");
            var constants = BaseGame with { BuildSpeed = 6 };
            Items = new ItemFactory(Data.Db, constants);
            Construction = new ConstructionSystem(Data.Db, constants, BodyOptions.Default, Items, [], "204-gamedata.base");
            var production = new ProductionSystem(Data.Db, constants, BodyOptions.Default, Items, null) { Construction = Construction };
            World = new Meitou.Simulation.World(new WorldSettings { Seed = 1, TickSeconds = 1 }, new OpenGroundWalkability((_, _) => 0),
                [new PlayerSystem(), Construction, production, new LockSystem(constants, BodyOptions.Default)]);
            World.Player.Faction = Data.PlayerFaction; World.Player.Money = 10000;
            if (saved is not null) World.Fixtures.Saved = new Dictionary<string, FcsRecord> { [House.Key] = saved };
            World.Fixtures.RegisterSite(World, Data, 0);
            var stats = new CharacterStats(); stats[StatsEnumerated.Engineering] = 100;
            Builder = World.Characters.Spawn(new CharacterHot { Position = House.Position, Health = 100 },
                new CharacterCold { Name = "Builder", Race = Human, Medical = MedicalState.Create(Human, 50), Stats = stats,
                    IsPlayer = true, Faction = Data.PlayerFaction, Inventory = new Inventory() }, 0);
            World.Player.Selection.Add(Builder);
            World.RunTick();
        }
        public void Buy() { Say(new BuyBuilding(House.Key, 4800) { Tick = World.Tick }); World.RunTick(); }
        public void Say(SimCommand command) => World.Commands.Enqueue(command with { Tick = World.Tick });
        public void Repair() { Say(new BuildOrder([Builder], House.Key) { Tick = World.Tick }); World.RunTick(); }
        public void Supply(int count) => Cold.Inventory!.Add(Items.Create(BuildingBuildData.BuildingMaterialsItem, count)!);
        public int Carried => Cold.Inventory!.All().Where(i => i.Record == BuildingBuildData.BuildingMaterialsItem).Sum(i => i.Quantity);
        public FcsRecord Save()
        {
            var state = new FcsRecord { StringId = "saved", RecordType = FcsRecordType.GAMESTATE_BUILDING };
            Assert.True(World.Fixtures.WriteBuilding(House.Key, state));
            var file = new SaveFile(); file.Records.Add(state);
            return Assert.Single(SaveFile.Read(file.ToBytes()).Records);
        }
        public void Dispose() => World.Dispose();
    }

    sealed class HouseSight : ISightSource
    {
        public InteriorVolume? Room(TownBuilding b) => new(b.Key,
            [new(90, 90), new(110, 90), new(110, 110), new(90, 110)], -1, 20);
        public IReadOnlyList<WorldLight> Lights(TownBuilding b) => [];
    }

    [Fact]
    public void Ruins_require_purchase_and_an_explicit_order_then_consume_only_delivered_materials()
    {
        using var r = new Rig(sight: new HouseSight());
        r.Repair(); Assert.Empty(r.World.Buildings.All);
        r.Buy(); r.World.RunTicks(5);
        Assert.True(r.Site.Destroyed); Assert.True(r.Site.Complete);
        Assert.Null(r.Construction.EngineerSite(r.World, r.Builder.Slot));
        r.World.Sight.Update(r.World); Assert.True(r.World.Sight.Outdoors(r.Builder.Slot));
        r.Repair();
        Assert.False(r.Site.Destroyed); Assert.False(r.Site.Complete);
        r.World.Sight.Update(r.World); Assert.False(r.World.Sight.Outdoors(r.Builder.Slot));
        float retained = Assert.Single(r.Site.Materials).Delivered;
        Assert.InRange(retained, 10, 16);
        Assert.InRange(r.Site.Progress, 12.8f, 13);
        Assert.Contains(r.World.Fixtures.OfBuilding(r.House.Key), f => f.Kind == FixtureKind.Door);
        r.World.RunTicks(200);
        Assert.False(r.Site.Complete); Assert.Equal(retained, r.Site.Materials[0].Delivered);
        r.Supply(32); r.Repair();
        for (int i = 0; i < 2000 && !r.Site.Complete; i++) r.World.RunTick();
        Assert.True(r.Site.Complete); Assert.Equal(32, r.Site.Progress);
        Assert.Equal(32, r.Site.Materials[0].Delivered);
        Assert.Equal((int)retained, r.Carried);
        Assert.Equal(5200, r.World.Player.Money);
    }

    [Fact]
    public void Engineer_job_survives_a_mid_repair_save_fetches_materials_and_the_finished_house_reloads_in_place()
    {
        using var initial = new Rig(); initial.Buy(); initial.Repair();
        initial.Say(new AddJob([initial.Builder], TaskType.REPAIR, initial.House.Key) { Tick = initial.World.Tick }); initial.World.RunTicks(250);
        Assert.False(initial.Site.Complete);
        Assert.Equal(TaskType.JOB_BUILDER, Assert.Single(initial.Cold.Jobs).Type);
        float progress = initial.Site.Progress, delivered = initial.Site.Materials[0].Delivered;
        var ai = new FcsRecord(); var state = initial.Save();
        PermaJobSave.Write(ai, initial.Cold, initial.World.Buildings, new Dictionary<string, FcsRecord> { [initial.House.Key] = state });
        using var r = new Rig(state, new HouseSight());
        Assert.Equal(progress, r.Site.Progress); Assert.Equal(delivered, r.Site.Materials[0].Delivered);
        Assert.False(r.Site.Destroyed);
        r.World.Sight.Update(r.World); Assert.False(r.World.Sight.Outdoors(r.Builder.Slot));
        // Save loading restores jobs before the town's construction registry is populated.
        PermaJobSave.Read(ai, null, r.Cold, new BuildingRegistry(), new SaveBuilder().Game.BuildHandles(),
            new Dictionary<FcsRecord, string> { [state] = r.House.Key }, new HashSet<string> { r.House.Key });
        Assert.Equal(r.House.Key, Assert.Single(r.Cold.Jobs).Site);
        Assert.True((r.Cold.Orders & CharacterOrders.Jobs) != 0);
        // Loose resources exercise the fetch -> deliver -> build transitions without a navigation test.
        r.World.GroundItems.Drop(r.Items.Create(BuildingBuildData.BuildingMaterialsItem, 32)!, r.House.Position, 0);
        bool fetched = false, deliveredAny = false;
        for (int i = 0; i < 2500 && !r.Site.Complete; i++)
        {
            r.World.RunTick();
            var task = (CharacterTask)r.World.Characters.Previous[r.Builder.Slot].Task;
            fetched |= task == CharacterTask.FetchMaterials;
            deliveredAny |= task == CharacterTask.DeliverMaterials;
        }
        Assert.True(fetched); Assert.True(deliveredAny); Assert.True(r.Site.Complete);
        Assert.Equal(TaskType.JOB_BUILDER, Assert.Single(r.Cold.Jobs).Type);
        using var finished = new Rig(r.Save());
        Assert.True(finished.Site.Complete); Assert.False(finished.Site.Destroyed); Assert.False(finished.Site.NeedsWork);
        Assert.NotNull(finished.Site.LevelPlacement);
        Assert.Contains(finished.World.Fixtures.OfBuilding(finished.House.Key), f => f.Kind == FixtureKind.Door);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("inventory")]
    [InlineData("ground")]
    public void Repeated_manual_repair_barks_only_when_stalled_without_available_materials(string supply)
    {
        using var r = new Rig();
        r.Buy(); r.Repair(); r.World.RunTicks(250);
        Assert.True(r.Site.Stalled);
        Assert.Empty(r.World.Snapshot.Speech);
        if (supply == "inventory") r.Supply(32);
        if (supply == "ground")
            r.World.GroundItems.Drop(r.Items.Create(BuildingBuildData.BuildingMaterialsItem, 32)!, r.House.Position, 0);
        r.Repair();
        if (supply == "none")
        {
            var bubble = Assert.Single(r.World.Snapshot.Speech);
            Assert.Equal(r.Builder, bubble.Speaker);
            Assert.Equal("I don't have any building materials.", bubble.Text);
            r.World.RunTicks(20);
            Assert.Empty(r.World.Snapshot.Speech); // No repeated bark from the simulation's build ticks.
            r.Repair();
            Assert.Equal(bubble.Text, Assert.Single(r.World.Snapshot.Speech).Text);
        }
        else
        {
            Assert.Empty(r.World.Snapshot.Speech);
            for (int i = 0; i < 2500 && !r.Site.Complete; i++) r.World.RunTick();
            Assert.True(r.Site.Complete);
            Assert.Empty(r.World.Snapshot.Speech);
        }
    }

    [Fact, Slow]
    public void Original_town_ruin_saves_in_place_and_restores_its_doors()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var db = InstallData.BaseGame!;
        var levels = InstallData.Levels!;
        var data = PopulationData.Create(db, levels.Towns(), buildings: TownBuildings.Build(db, levels, null, (_, _) => 0));
        var construction = new ConstructionSystem(data);
        using var world = new Meitou.Simulation.World(new WorldSettings { Seed = 1 }, new OpenGroundWalkability((_, _) => 0),
            [new PlayerSystem(), construction, new LockSystem(data.Bodies.Constants, data.BodyOptions)]);
        world.Player.Faction = data.PlayerFaction; world.Player.Money = 10000;
        int hub = data.Sites.ToList().FindIndex(s => s.Town.Name == "The Hub");
        world.Fixtures.RegisterSite(world, data, hub);
        var house = data.Sites[hub].Buildings.First(b => b.Name == "Storm House" && b.Destroyed && world.Fixtures.SalePrice(b.Key, data.PlayerFaction) == 4800);
        var builder = world.Characters.Spawn(new CharacterHot { Position = house.Position, Health = 100 },
            new CharacterCold { IsPlayer = true, Faction = data.PlayerFaction, Inventory = new Inventory() }, 0);
        world.Commands.Enqueue(new BuyBuilding(house.Key, 4800) { Tick = world.Tick }); world.RunTick();
        world.Commands.Enqueue(new BuildOrder([builder], house.Key) { Tick = world.Tick }); world.RunTick();
        var site = world.Buildings.Find(house.Key)!;
        Assert.False(site.Destroyed); Assert.False(site.Complete);
        world.VisitedZones.Add(house.Zone);
        var save = SaveCapture.Capture(world, data, new SaveClock(1, 13, 0), options: new SaveCaptureOptions { Levels = levels, Placements = levels.Towns().ToArray() });
        var states = HeldSave.BuildingStates(save);
        Assert.Equal(site.Progress, states[house.Key].Floats["construction progress"]);
        Assert.Equal(site.Materials[0].Delivered, states[house.Key].Floats["0construction mats"]);
        Assert.False(states[house.Key].Bools["destroyed"]);
        Assert.Single(save.Zones[house.Zone].BuildingList!.Instances, i => i.Id == house.InstanceId);
        Assert.DoesNotContain(save.Zones[house.Zone].BuildingList!.Instances, i => i.Id == site.Id);
        using var loaded = new Meitou.Simulation.World(new WorldSettings(), new OpenGroundWalkability((_, _) => 0), []);
        loaded.Player.Faction = data.PlayerFaction;
        Assert.Equal(0, BuildingSave.Read(save, loaded, db, data.Factions[data.PlayerFaction].Id, data.PlayerFaction));
        loaded.Fixtures.Saved = states; loaded.Fixtures.RegisterSite(loaded, data, hub);
        var restored = Assert.Single(loaded.Buildings.All);
        Assert.Equal(house.Key, restored.Id); Assert.Equal(site.Progress, restored.Progress);
        Assert.Contains(loaded.Fixtures.OfBuilding(house.Key), f => f.Kind == FixtureKind.Door);
        Assert.Equal(house.InstanceId, restored.LevelPlacement!.InstanceId);
    }
}
