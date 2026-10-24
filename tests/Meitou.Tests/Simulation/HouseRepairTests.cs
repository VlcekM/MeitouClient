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
            Construction = new ConstructionSystem(Data.Db, constants, BodyOptions.Default, Items, [], "player");
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

    [Fact]
    public void Ruins_require_purchase_and_an_explicit_order_then_consume_only_delivered_materials()
    {
        using var r = new Rig();
        r.Repair(); Assert.Empty(r.World.Buildings.All);
        r.Buy(); r.World.RunTicks(5);
        Assert.True(r.Site.Destroyed); Assert.True(r.Site.Complete);
        Assert.Null(r.Construction.EngineerSite(r.World, r.Builder.Slot));
        r.Repair();
        Assert.False(r.Site.Destroyed); Assert.False(r.Site.Complete);
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
    public void Save_mid_repair_preserves_materials_progress_and_completed_house_overlay()
    {
        using var r = new Rig(); r.Buy(); r.Supply(3); r.Repair(); r.World.RunTicks(300);
        float progress = r.Site.Progress, delivered = r.Site.Materials[0].Delivered;
        using var loaded = new Rig(r.Save());
        Assert.Equal(progress, loaded.Site.Progress); Assert.Equal(delivered, loaded.Site.Materials[0].Delivered);
        Assert.False(loaded.Site.Destroyed); Assert.False(loaded.Site.Complete);
        loaded.World.RunTicks(20);
        Assert.Equal(progress, loaded.Site.Progress); Assert.Equal(delivered, loaded.Site.Materials[0].Delivered);
        loaded.Supply(32); loaded.Repair();
        for (int i = 0; i < 2000 && !loaded.Site.Complete; i++) loaded.World.RunTick();
        Assert.True(loaded.Site.Complete);
        using var finished = new Rig(loaded.Save());
        Assert.True(finished.Site.Complete); Assert.False(finished.Site.Destroyed);
        Assert.NotNull(finished.Site.LevelPlacement);
        Assert.False(finished.Site.NeedsWork);
        Assert.Contains(finished.World.Fixtures.OfBuilding(finished.House.Key), f => f.Kind == FixtureKind.Door);
    }

    [Fact]
    public void Engineer_job_waits_for_materials_fetches_them_and_retains_its_queue_entry()
    {
        using var initial = new Rig(); initial.Buy(); initial.Repair();
        initial.Say(new AddJob([initial.Builder], TaskType.REPAIR, initial.House.Key) { Tick = initial.World.Tick }); initial.World.RunTicks(250);
        Assert.False(initial.Site.Complete);
        Assert.Equal(TaskType.JOB_BUILDER, Assert.Single(initial.Cold.Jobs).Type);
        var ai = new FcsRecord(); var state = initial.Save();
        PermaJobSave.Write(ai, initial.Cold, initial.World.Buildings, new Dictionary<string, FcsRecord> { [initial.House.Key] = state });
        using var r = new Rig(state);
        // Save loading restores jobs before the town's construction registry is populated.
        PermaJobSave.Read(ai, null, r.Cold, new BuildingRegistry(), new SaveBuilder().Game.BuildHandles(),
            new Dictionary<FcsRecord, string> { [state] = r.House.Key });
        Assert.Equal(r.House.Key, Assert.Single(r.Cold.Jobs).Site);
        Assert.True((r.Cold.Orders & CharacterOrders.Jobs) != 0);
        // Loose resources exercise the fetch -> deliver -> build transitions without a navigation test.
        r.World.GroundItems.Drop(r.Items.Create(BuildingBuildData.BuildingMaterialsItem, 32)!, r.House.Position, 0);
        bool fetched = false, delivered = false;
        for (int i = 0; i < 2500 && !r.Site.Complete; i++)
        {
            r.World.RunTick();
            var task = (CharacterTask)r.World.Characters.Previous[r.Builder.Slot].Task;
            fetched |= task == CharacterTask.FetchMaterials;
            delivered |= task == CharacterTask.DeliverMaterials;
        }
        Assert.True(fetched); Assert.True(delivered); Assert.True(r.Site.Complete);
        Assert.Equal(TaskType.JOB_BUILDER, Assert.Single(r.Cold.Jobs).Type);
    }

    [Fact]
    public void Damaged_complete_house_repairs_without_consuming_materials()
    {
        using var original = new Rig(); original.Buy();
        var state = original.Save();
        state.Bools["destroyed"] = false;
        state.Bools["is complete"] = true;
        state.Floats["construction progress"] = 10;
        using var r = new Rig(state); r.Supply(5); r.Repair();
        for (int i = 0; i < 1000 && r.Site.NeedsWork; i++) r.World.RunTick();
        Assert.False(r.Site.NeedsWork); Assert.True(r.Site.Complete);
        Assert.Equal(5, r.Carried); Assert.Equal(32, r.Site.Materials[0].Delivered);
    }

    [Fact, Slow]
    public void Original_town_ruin_saves_in_place_and_restores_its_normal_mesh_and_doors()
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
        var before = house.Resolved!.Value;
        var placement = restored.LevelPlacement! with { DestroyedOverride = false };
        var after = BuildingPlacements.Resolve(placement, db, levels, (_, _) => 0)!.Value;
        Assert.False(after.Destroyed); Assert.Equal(before.Placement.InstanceId, after.Placement.InstanceId);
        var ruin = WorldObjectLayout.Building(db, before.Record, house.InstanceId, before.Position, before.Placement.Rotation,
            new BuildingState(true) { WithInterior = false, Identity = house.Key });
        var repaired = WorldObjectLayout.Building(db, after.Record, house.InstanceId, after.Position, placement.Rotation,
            new BuildingState(false) { WithInterior = true, Identity = house.Key });
        Assert.NotEmpty(repaired);
        Assert.False(ruin.Select(m => m.MeshPath).SequenceEqual(repaired.Select(m => m.MeshPath)));
    }

    sealed class HouseSight : ISightSource
    {
        public InteriorVolume? Room(TownBuilding b) => new(b.Key,
            [new(90, 90), new(110, 90), new(110, 110), new(90, 110)], -1, 20);
        public IReadOnlyList<WorldLight> Lights(TownBuilding b) => [];
    }

    [Fact]
    public void Starting_repair_restores_the_indoor_volume_and_reload_keeps_it()
    {
        using var r = new Rig(sight: new HouseSight()); r.Buy();
        r.World.Sight.Update(r.World); Assert.True(r.World.Sight.Outdoors(r.Builder.Slot));
        r.Repair(); r.World.Sight.Update(r.World);
        Assert.False(r.World.Sight.Outdoors(r.Builder.Slot));
        using var loaded = new Rig(r.Save(), new HouseSight());
        loaded.World.Sight.Update(loaded.World);
        Assert.False(loaded.World.Sight.Outdoors(loaded.Builder.Slot));
    }

    [Fact]
    public void Placement_override_replaces_only_the_matching_world_key()
    {
        var a = new BuildingPlacement(new(1, 1), "same", "house", new(1, 2, 3), Quaternion.Identity, "state", 2);
        var b = a with { Zone = new(2, 1) };
        var replacement = a with { DestroyedOverride = false };
        var merged = BuildingPlacements.Overlay([a, b], [replacement]).ToArray();
        Assert.Equal(2, merged.Length); Assert.Same(replacement, merged[0]); Assert.Same(b, merged[1]);
    }
}
