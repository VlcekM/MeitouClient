using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Production (docs/simulation.md "Production", docs/game/buildings-production.md "Production", "Farming", "Research"): wells, farms, research
/// benches.
/// </summary>
public class ProductionTests
{
    const string Water = "50-t", Crop = "51-t", Book = "52-t", Gadget = "53-t", Well = "60-t", Farm = "61-t", Bench = "62-t", Workshop = "63-t",
        Study = "70-t", Fuel = "54-t", Generator = "64-t", Battery = "65-t", Lamp = "66-t", BigLamp = "67-t", PoweredWorkshop = "68-t",
        Tank = "69-t";
    /// <summary>
    /// The stat that gives skill factor 1: lerp(0.5, 1.4, x/100) = 1.
    /// </summary>
    const float Unit = 100 / 1.8f;
    const int PlayerFaction = 2;
    static readonly GameConstants Constants = BaseGame with { ProductionSpeed = 2, ResearchRate = 0.3f };
    static readonly Vector3 At = new(1000, 300, 2000);

    /// <summary>
    /// The base game's ARMOUR, CONTAINER and CROSSBOW records (one <c>.TECH.1</c> research each).
    /// </summary>
    const int GearCount = 165;

    #region Test setup

    static FcsRecord Rec(string id, FcsRecordType type, string name) => new() { StringId = id, Flags = 0x10, Name = name, RecordType = type };

    /// <summary>
    /// A well (0.1 water per second at skill 1), an indoor farm (4 plants, 1 h to grow, 2 crops each at farming 33.3, 1 water per game hour) and a
    /// level 1 research bench with a 4 x 4 grid, one research of 0.3 h costing 2 books, and a workshop crafting a gadget of 1 crop in 1 h (its
    /// section consumes water until a craft rebinds it; the output holds 3); a generator (20 power, 0.01 x P fuel a second at full load), a battery
    /// (rate 10, store 30 s), lamps drawing 4 and 8 and the workshop drawing 5; a 2 x 2 tank storing only water, its stacks x 10.
    /// </summary>
    static GameDatabase Database()
    {
        var file = new FcsFile();
        file.Records.AddRange([Item(Water, "Water"), Item(Crop, "Crop"), Item(Book, "Book"), WellFunction(), FarmFunction(), BenchFunction(),
            FarmData(), Building(Well, "Test Well", "80-t"), FarmBuilding(), BenchBuilding(), StudyResearch(), GadgetItem(), WorkshopFunction(),
            Building(Workshop, "Test Workshop", "84-t"), Item(Fuel, "Fuel"), GeneratorFunction(), Function("86-t", MachineData.BatteryFunction, 0),
            Function("87-t", 15, 0), Powered(Building(Generator, "Test Generator", "85-t"), 20),
            Powered(Building(Battery, "Test Battery", "86-t"), 10, 30), Powered(Building(Lamp, "Lamp", "87-t"), -4),
            Powered(Building(BigLamp, "Big Lamp", "87-t"), -8), Powered(Building(PoweredWorkshop, "Powered Workshop", "84-t"), -5),
            Function("88-t", MachineData.ResourceStorageFunction, 0), TankBuilding()]);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return db;
    }

    /// <summary>
    /// A stackable item of 1 kg.
    /// </summary>
    static FcsRecord Item(string id, string name)
    {
        var r = Rec(id, FcsRecordType.Item, name);
        r.Floats["weight kg"] = 1;
        r.Ints["stackable"] = 100;
        return r;
    }

    /// <summary>
    /// A building functionality record of the given function, run with <paramref name="stat"/>.
    /// </summary>
    static FcsRecord Function(string id, int function, int stat)
    {
        var r = Rec(id, FcsRecordType.BuildingFunctionality, id);
        r.Ints["function"] = function;
        r.Ints["stat used"] = stat;
        return r;
    }

    /// <summary>
    /// A building with the functionality <paramref name="function"/>.
    /// </summary>
    static FcsRecord Building(string id, string name, string function)
    {
        var r = Rec(id, FcsRecordType.Building, name);
        r.References["functionality"] = [new FcsReference(function, 0, 0, 0)];
        return r;
    }

    /// <summary>
    /// <paramref name="building"/> with a power output (negative: a draw) and a battery capacity.
    /// </summary>
    static FcsRecord Powered(FcsRecord building, int output, int capacity = 0)
    {
        building.Ints["power output"] = output;
        building.Ints["power capacity"] = capacity;
        return building;
    }

    /// <summary>
    /// The well's functionality: 0.1 water per second at skill 1, from labouring.
    /// </summary>
    static FcsRecord WellFunction()
    {
        var wellFn = Function("80-t", MachineData.MineFunction, (int)StatsEnumerated.Labouring);
        wellFn.Floats["production mult"] = 0.05f;
        wellFn.References["produces"] = [new FcsReference(Water, 10, 0, 0)];
        return wellFn;
    }

    /// <summary>
    /// The farm's functionality: two farmers on the ground resource, water in, crops out.
    /// </summary>
    static FcsRecord FarmFunction()
    {
        var farmFn = Function("81-t", MachineData.MineFunction, (int)StatsEnumerated.Farming);
        farmFn.Ints["world resource mining"] = MachineData.GroundResource;
        farmFn.Ints["max operators"] = 2;
        farmFn.References["consumes"] = [new FcsReference(Water, 10, 0, 0)];
        farmFn.References["produces"] = [new FcsReference(Crop, 20, 0, 0)];
        return farmFn;
    }

    /// <summary>
    /// The research bench's functionality: tech level 1, from science.
    /// </summary>
    static FcsRecord BenchFunction()
    {
        var benchFn = Function("82-t", MachineData.ResearchFunction, (int)StatsEnumerated.Science);
        benchFn.Ints["tech level"] = 1;
        return benchFn;
    }

    /// <summary>
    /// The farm's crop: 4 plants, 1 h to grow, 2 crops each.
    /// </summary>
    static FcsRecord FarmData()
    {
        var farmData = Rec("83-t", FcsRecordType.FarmData, "Test Crop");
        farmData.Ints["amount"] = 4;

        // An indoor crop (fertility effect 0) whose minimum fertility the bare test ground (0) clears, as the shipped indoor farms' -1.
        farmData.Floats["minimum fertility"] = -1;
        foreach (var (key, value) in new[] { ("growth time", 1f), ("harvest time", 48f), ("death time", 8f), ("consumption rate", 24f),
                ("harvest rate", 256f), ("clear rate", 500f), ("output per plant", 2f), ("green", 1f) })
            farmData.Floats[key] = value;
        return farmData;
    }

    /// <summary>
    /// The indoor farm, growing <see cref="FarmData"/>.
    /// </summary>
    static FcsRecord FarmBuilding()
    {
        var farm = Building(Farm, "Test Farm", "81-t");
        farm.References["farm data"] = [new FcsReference("83-t", 0, 0, 0)];
        return farm;
    }

    /// <summary>
    /// The research bench, with a 4 x 4 inventory grid.
    /// </summary>
    static FcsRecord BenchBuilding()
    {
        var bench = Building(Bench, "Test Bench", "82-t");
        bench.Bools["has inventory"] = true;
        bench.Ints["storage size width"] = 4;
        bench.Ints["storage size height"] = 4;
        return bench;
    }

    /// <summary>
    /// The level 1 research of 0.3 h, costing 2 books.
    /// </summary>
    static FcsRecord StudyResearch()
    {
        var study = Rec(Study, FcsRecordType.Research, "Test Study");
        study.Ints["level"] = 1;
        study.Ints["time"] = 1;
        study.References["cost"] = [new FcsReference(Book, 2, 0, 0)];
        return study;
    }

    /// <summary>
    /// The gadget: 1 crop, crafted in 1 h.
    /// </summary>
    static FcsRecord GadgetItem()
    {
        var gadget = Item(Gadget, "Gadget");
        gadget.Ints["production time"] = 1;
        gadget.References["ingredients"] = [new FcsReference(Crop, 100, 0, 0)];
        return gadget;
    }

    /// <summary>
    /// The workshop's functionality: crafts the gadget (the output holds 3), its section consuming water until a craft rebinds it.
    /// </summary>
    static FcsRecord WorkshopFunction()
    {
        var workshopFn = Function("84-t", CraftBench.CraftingFunction, (int)StatsEnumerated.Engineering);
        workshopFn.References["item crafts"] = [new FcsReference(Gadget, 3, 0, 0)];
        workshopFn.References["consumes"] = [new FcsReference(Water, 5, 100, 0)];
        return workshopFn;
    }

    /// <summary>
    /// The generator's functionality: 0.01 x P fuel a second at full load.
    /// </summary>
    static FcsRecord GeneratorFunction()
    {
        var generatorFn = Function("85-t", MachineData.GeneratorFunction, 0);
        generatorFn.Floats["production mult"] = 0.01f;
        generatorFn.References["consumes"] = [new FcsReference(Fuel, 10, 100, 0)];
        return generatorFn;
    }

    /// <summary>
    /// The 2 x 2 tank storing only water, its stacks x 10.
    /// </summary>
    static FcsRecord TankBuilding()
    {
        var tank = Building(Tank, "Test Tank", "88-t");
        tank.Bools["has inventory"] = true;
        tank.Ints["storage size width"] = 2;
        tank.Ints["storage size height"] = 2;
        tank.Floats["stackable bonus mult"] = 10;
        tank.References["limit inventory"] = [new FcsReference(Water, 0, 0, 0)];
        return tank;
    }

    sealed class Rig : IDisposable
    {
        public SimWorld World { get; }
        public ItemFactory Items { get; }
        public ProductionSystem Production { get; }
        public List<string> Sites { get; } = [];

        public Rig(GameDatabase db, GameConstants constants, string[] records, int threads = 1, bool snapshots = false, int town = 1)
        {
            Items = new ItemFactory(db, constants);
            var walk = new OpenGroundWalkability(SyntheticTown.Ground);
            Production = new ProductionSystem(db, constants, BodyOptions.Default, Items, null);
            World = new SimWorld(new WorldSettings { Seed = 5, Threads = threads, PublishSnapshots = snapshots, MinPartitionSize = 1 }, walk,
                [new PlayerSystem(), new MovementSystem(new PathService(walk, true)), Production]);
            World.Player.Faction = PlayerFaction;
            World.Research.Set(db, []);
            for (int i = 0; i < records.Length; i++)
            {
                // 30 apart: a character at one building is within the trading range (40) of the next.
                var p = At + new Vector3(i * 30, 0, 0);
                string id = World.Buildings.NewId(WorldLayout.ZoneOf(p.X, p.Z));
                World.Buildings.Add(new BuildSite { Id = id, Record = records[i], Position = p, Owner = PlayerFaction, Town = town, Materials = [], Complete = true });
                Sites.Add(id);
            }
        }

        /// <summary>
        /// A player character beside site <paramref name="site"/> with one stat and what it carries (one stack each).
        /// </summary>
        public CharacterId Add(int site, StatsEnumerated stat, float level, params (string Item, int Count)[] carried) => Add(site, Vector3.Zero,
                stat, level, carried);

        /// <summary>
        /// A worker <paramref name="offset"/> further from the building than the usual 10.
        /// </summary>
        public CharacterId Add(int site, Vector3 offset, StatsEnumerated stat, float level, params (string Item, int Count)[] carried)
        {
            var inventory = new Inventory();
            foreach (var (item, count) in carried) inventory.Add(Items.Create(item, count)!);
            var stats = new CharacterStats();
            stats[stat] = level;
            var cold = new CharacterCold { Name = "worker" + site, Race = Human, Stats = stats, Medical = MedicalState.Create(Human, 50),
                    Inventory = inventory, IsPlayer = true, Faction = PlayerFaction };
            var p = World.Buildings.Find(Sites[site])!.Position + new Vector3(0, 0, 10) + offset;
            return World.Characters.Spawn(new CharacterHot { Position = p, MaxSpeed = 40, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold, 0);
        }

        public Inventory Carried(CharacterId id) => World.Characters.Cold(id.Slot)!.Inventory!;
        public int Count(CharacterId id, string item) => Carried(id).All().Where(i => i.Record == item).Sum(i => i.Quantity);

        /// <summary>
        /// Puts the character's first stack of <paramref name="item"/> into slot <paramref name="slot"/> of a site.
        /// </summary>
        public void Put(CharacterId who, string item, int site, int slot, int x = 0, int y = 0)
        {
            var all = Carried(who).All().ToList();
            int index = all.FindIndex(i => i.Record == item);
            var it = all[index];
            World.Commands.Enqueue(new PutInMachine(who, new InventoryItemToken(index, it.Record, it.Section, it.X, it.Y, it.Quantity, it.Quality),
                    Sites[site], slot, x, y) { Tick = World.Tick });
        }

        public Machine Machine(int site) => World.Machines.Find(Sites[site])!;
        public void Dispose() => World.Dispose();
    }

    // ------------------------------------------------------------------ determinism

    /// <summary>
    /// A well, a farm and a powered workshop are worked, water goes into the farm, books into the bench and crops into the workshop, the study and
    /// three gadgets are queued, a fuelled generator powers the workshop and charges a battery, the labourer hauls the well's water to a tank; hashes
    /// at six checkpoints, pinned in <see cref="GoldenHashTests"/>.
    /// </summary>
    internal static List<ulong> Scripted(int threads)
    {
        using var rig = new Rig(Database(), Constants, [Well, Farm, Bench, PoweredWorkshop, Generator, Battery, Tank], threads);
        var labourer = rig.Add(0, StatsEnumerated.Labouring, Unit);
        var farmer = rig.Add(1, StatsEnumerated.Farming, 100 / 3f, (Water, 5));
        var scientist = rig.Add(2, StatsEnumerated.Science, Unit, (Book, 3));
        var crafter = rig.Add(3, StatsEnumerated.Engineering, Unit, (Crop, 5));
        rig.World.RunTick();
        rig.Production.Ensure(rig.World, rig.World.Buildings.Find(rig.Sites[4])!)!.Inputs[0].Count = 10;
        rig.World.Commands.Enqueue(new OperateOrder([labourer], rig.Sites[0]) { Tick = 2 });
        rig.World.Commands.Enqueue(new OperateOrder([farmer], rig.Sites[1]) { Tick = 2 });
        rig.World.Commands.Enqueue(new OperateOrder([scientist], rig.Sites[2]) { Tick = 2 });
        rig.World.Commands.Enqueue(new QueueCraft(rig.Sites[3], Gadget, 3) { Tick = rig.World.Tick });
        rig.World.Commands.Enqueue(new OperateOrder([crafter], rig.Sites[3]) { Tick = 2 });
        rig.Put(farmer, Water, 1, 0);
        rig.Put(scientist, Book, 2, -1);
        rig.Put(crafter, Crop, 3, 0);
        rig.World.RunTick();
        rig.World.Commands.Enqueue(new QueueResearch(Study) { Tick = rig.World.Tick });
        var hashes = new List<ulong>();
        foreach (int target in new[] { 5, 60, 600, 1500, 3000, 5000 })
        {
            rig.World.RunTicks(target - (int)rig.World.Tick);
            hashes.Add(rig.World.StateHash());
        }
        var seen = (Water: rig.Machine(0).Output!.Count, Crops: rig.Machine(1).Output!.Count, Studied: rig.World.Research.IsDone(Study),
            Gadgets: rig.Machine(3).Craft!.Done.Sum(i => i.Quantity), rig.Machine(5).Charge,
                    Stored: rig.Machine(6).Storage!.Items.Sum(i => i.Quantity));

        Assert.True(seen.Water > 0 && seen.Crops > 0 && seen.Studied && seen.Gadgets == 3 && seen.Charge > 0 && seen.Stored > 0, $"{seen}");

        return hashes;
    }

    #endregion

    #region Machines and crafting

    /// <summary>
    /// A town building an NPC worker of goal 189 stands at is a machine of the town's faction, made when it arrives
    /// (docs/game/buildings-production.md "NPC town machines"): with row 87 first in its plan it operates it and the output grows; with row 92 first
    /// it empties the output into its inventory (<c>Task_EmptyMachine</c>) and a full output (status 1) goes back to 2.
    /// </summary>
    [Fact]
    public void An_npc_worker_operates_its_town_buildings_machine_and_then_empties_its_output()
    {
        using var rig = new Rig(Database(), Constants, []);
        const int town = 7;
        var building = new TownBuilding("well", Well, "Well", At, 0, false, false, []);
        var stats = new CharacterStats();
        stats[StatsEnumerated.Labouring] = Unit;
        var squad = new Squad { Id = 1, TemplateId = "t", Faction = town };
        var cold = new CharacterCold { Name = "miner", Race = Human, Stats = stats, Medical = MedicalState.Create(Human, 50),
                Inventory = new Inventory(), Faction = town };
        cold.Ai = new Meitou.Simulation.Ai.AiMind { Squad = squad, Behaviour = Meitou.Simulation.Ai.AiBehaviour.Work, WorkOperate = building,
                WorkOwner = town };
        var worker = rig.World.Characters.Spawn(new CharacterHot { Position = At + new Vector3(0, 0, 10), MaxSpeed = 40, WalkSpeed = 15,
                Task = (byte)CharacterTask.Idle }, cold, 0);
        rig.World.RunTicks(600);
        var machine = rig.World.Machines.Find(ProductionSystem.TownWorkId(building));

        Assert.NotNull(machine);
        Assert.True(machine.Output!.Count > 0);

        int made = machine.Output.Count;

        machine.Status = 1;
        cold.Ai.WorkOperate = null;
        cold.Ai.CollectFrom = building;
        rig.World.RunTicks(2);

        Assert.Equal(0, machine.Output.Count);
        Assert.Equal(2, machine.Status);
        Assert.Equal(made, cold.Inventory!.All().Where(i => i.Record == Water).Sum(i => i.Quantity));
    }

    // ------------------------------------------------------------------ items and research costs

    /// <summary>
    /// An input takes only up to its capacity, the output comes back into the character's inventory, the study's books are paid from the bench once
    /// (cancelling and queueing again costs nothing) and it completes after hours x G/24 seconds at rate 1; the save keys hold it.
    /// </summary>
    [Fact]
    public void Inputs_clamp_to_capacity_outputs_are_taken_and_a_research_is_paid_once()
    {
        var db = Database();
        using var rig = new Rig(db, Constants, [Well, Farm, Bench]);
        var worker = rig.Add(1, StatsEnumerated.Science, Unit, (Water, 15), (Book, 4));
        rig.World.RunTick();
        rig.Put(worker, Water, 1, 0);
        rig.World.RunTick();

        Assert.Equal(10, rig.Machine(1).Inputs[0].Count + (int)MathF.Ceiling(rig.Machine(1).Inputs[0].Level));
        Assert.Equal(5, rig.Count(worker, Water));

        rig.Machine(0).Output!.Count = 3;
        rig.World.Commands.Enqueue(new TakeFromMachine(worker, rig.Sites[0], -2) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.Equal(0, rig.Machine(0).Output!.Count);
        Assert.Equal(8, rig.Count(worker, Water));

        rig.Put(worker, Book, 2, -1);
        rig.World.RunTick();
        rig.World.Commands.Enqueue(new QueueResearch(Study) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.Equal(2, rig.Machine(2).Storage!.Items.Sum(i => i.Quantity));

        rig.World.Commands.Enqueue(new CancelResearch(Study) { Tick = rig.World.Tick });
        rig.World.RunTick();
        rig.World.Commands.Enqueue(new QueueResearch(Study) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.Equal(2, rig.Machine(2).Storage!.Items.Sum(i => i.Quantity));
        Assert.Contains(Study, rig.World.Research.Paid);

        // The worker, science at skill factor 1, operates the level 1 bench (output rate 1): 0.3 h = 0.3 x G/24 s.
        rig.World.Commands.Enqueue(new OperateOrder([worker], rig.Sites[2]) { Tick = rig.World.Tick });
        long started = rig.World.Tick;
        for (int t = 0; t < 4000 && !rig.World.Research.IsDone(Study); t++) rig.World.RunTick();
        float seconds = (rig.World.Tick - started) * rig.World.TickSeconds;

        Assert.True(rig.World.Research.IsDone(Study));
        Assert.InRange(seconds, 0.3f * MachineData.DaySeconds / 24, 0.3f * MachineData.DaySeconds / 24 * 1.15f);
        Assert.Contains(rig.World.TakeMessages(), m => m == "Research complete: Test Study");
    }

    /// <summary>
    /// A rock (an <c>is foliage</c> iron mine) belongs to no faction until a player character starts working it, then to the player's faction
    /// (Task_OperateMachine slot 1, docs/game/ui-screens.md "Selected building", Rocks).
    /// </summary>
    [Fact]
    public void A_rock_becomes_the_players_once_a_player_character_works_it()
    {
        var db = Database();
        var rockFn = Rec("89-t", FcsRecordType.BuildingFunctionality, "Rock Mine");
        rockFn.Ints["function"] = MachineData.MineFunction;
        rockFn.Ints["world resource mining"] = ResourceField.IronResource;
        rockFn.References["produces"] = [new FcsReference(Crop, 10, 0, 0)];
        var rockRecord = Rec("90-t", FcsRecordType.Building, "Test Rock");
        rockRecord.Bools["is foliage"] = true;
        rockRecord.References["functionality"] = [new FcsReference("89-t", 0, 0, 0)];
        var file = new FcsFile();
        file.Records.AddRange([rockFn, rockRecord]);
        db.Apply(file, "rock.mod");
        using var rig = new Rig(db, Constants, [Well]);
        string rock = ProductionSystem.NaturalId("90-t", At + new Vector3(0, 0, 20));

        Assert.Equal(-1, rig.Production.RichnessOf(rig.World, rock)!.Value.Owner);

        var worker = rig.Add(0, StatsEnumerated.Labouring, Unit);
        rig.World.Commands.Enqueue(new OperateOrder([worker], rock) { Tick = rig.World.Tick });
        for (int t = 0; t < 300 && rig.World.Characters.Cold(worker.Slot)!.Operating == false; t++) rig.World.RunTick();

        Assert.Equal(PlayerFaction, rig.Production.RichnessOf(rig.World, rock)!.Value.Owner);
    }

    // ------------------------------------------------------------------ crafting

    /// <summary>
    /// Queueing a gadget rebinds the workshop's water section to crops (the water falls out); two gadgets draw 2 crops and wait in the output until
    /// taken; a half-made third and the rebound section survive a save round-trip (docs/game/buildings-production.md "Crafting").
    /// </summary>
    [Fact]
    public void A_craft_rebinds_its_section_draws_ingredients_waits_to_be_taken_and_saves()
    {
        const string playerFactionId = "204-gamedata.base";
        var db = Database();
        using var rig = new Rig(db, Constants, [Workshop]);
        var worker = rig.Add(0, StatsEnumerated.Engineering, Unit, (Water, 2), (Crop, 5));
        rig.World.RunTick();
        rig.Put(worker, Water, 0, 0);
        rig.World.RunTick();

        Assert.Equal((Water, 2), (rig.Machine(0).Inputs[0].Item, rig.Machine(0).Inputs[0].Count));

        rig.World.Commands.Enqueue(new QueueCraft(rig.Sites[0], Gadget, 2) { Tick = rig.World.Tick });
        rig.World.RunTick();
        var section = rig.Machine(0).Inputs[0];

        Assert.Equal((Crop, 0, 1f), (section.Item, section.Count, section.Rate));
        Assert.Equal(2, rig.World.GroundItems.All.Where(g => g.Item.Record == Water).Sum(g => g.Item.Quantity));

        rig.Put(worker, Crop, 0, 0);
        rig.World.Commands.Enqueue(new OperateOrder([worker], rig.Sites[0]) { Tick = rig.World.Tick });
        var craft = rig.Machine(0).Craft!;
        for (int t = 0; t < 4000 && craft.Queue.Count > 0; t++) rig.World.RunTick();

        Assert.Empty(craft.Queue);
        Assert.Equal(2, craft.Done.Sum(i => i.Quantity));
        Assert.Equal(3, section.Count + section.Level, 0.01f);

        rig.World.Commands.Enqueue(new TakeFromMachine(worker, rig.Sites[0], -2) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.Empty(craft.Done);
        Assert.Equal(2, rig.Count(worker, Gadget));

        rig.World.Commands.Enqueue(new QueueCraft(rig.Sites[0], Gadget) { Tick = rig.World.Tick });
        for (int t = 0; t < 4000 && (craft.Queue.Count == 0 || craft.Queue[0].Progress < 0.3f); t++) rig.World.RunTick();
        var job = Assert.Single(craft.Queue);

        var builder = new Meitou.Data.Save.SaveBuilder(1);
        builder.AddFaction(playerFactionId, "Nameless", true, 1000, []);
        builder.AddEmptyWorldState();
        Meitou.Simulation.Saving.BuildingSave.Write(builder.Game, rig.World, playerFactionId, 5);
        Meitou.Simulation.Saving.MachineSave.Write(builder.Game, rig.World);
        string dir = Path.Combine(Path.GetTempPath(), "meitou-craft-" + Guid.NewGuid().ToString("N"));
        try
        {
            builder.Game.Write(dir);
            var save = Data.Save.SaveGame.Load(dir);
            using var back = new Rig(db, Constants, []);
            Meitou.Simulation.Saving.BuildingSave.Read(save, back.World, db, playerFactionId, PlayerFaction);
            Meitou.Simulation.Saving.MachineSave.Read(save, back.World, back.Production, back.Items);
            var read = back.World.Machines.Find(rig.Sites[0])!;
            var again = Assert.Single(read.Craft!.Queue);

            Assert.Equal((job.Option.Key, job.Progress, job.Critical), (again.Option.Key, again.Progress, again.Critical));
            Assert.Equal((section.Item, section.Count, section.Level, section.Rate),
                    (read.Inputs[0].Item, read.Inputs[0].Count, read.Inputs[0].Level, read.Inputs[0].Rate));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    #endregion

    // ------------------------------------------------------------------ power

    #region Power and mines

    /// <summary>
    /// A 20 generator powers lamps of 4 and 8 and charges the battery (rate 10) from the 8 left until its 30 s store is full, then runs at load
    /// 12/20; out of fuel, the battery takes over the tick after and its 10 is shared smallest demand first (4, then 6 of 8), draining 1 s of store a
    /// second (docs/game/buildings-production.md "Power").
    /// </summary>
    [Fact]
    public void A_town_shares_generator_power_charges_its_battery_and_falls_back_on_it()
    {
        using var rig = new Rig(Database(), Constants, [Generator, Lamp, BigLamp, Battery]);
        rig.World.RunTick();
        var generator = rig.Production.Ensure(rig.World, rig.World.Buildings.Find(rig.Sites[0])!)!;
        generator.Inputs[0].Count = 10;
        rig.World.RunTicks(2);
        var (lamp, big, battery) = (rig.Machine(1), rig.Machine(2), rig.Machine(3));

        Assert.Equal((4f, 8f, 8f, 1f, 1f), (lamp.Granted, big.Granted, battery.Granted, lamp.Power, big.Power));

        for (int t = 0; t < 4000 && battery.Charge < 30; t++) rig.World.RunTick();
        rig.World.RunTick();

        Assert.Equal(30, battery.Charge);
        Assert.Equal(0.6f, generator.Usage, 0.001f);

        generator.Inputs[0].Count = 0;
        generator.Inputs[0].Level = 0;
        rig.World.RunTicks(2);

        Assert.Equal((4f, 6f, -10f, 1f, 0.75f), (lamp.Granted, big.Granted, battery.Granted, lamp.Power, big.Power));

        float charge = battery.Charge;
        int ticks = (int)(10 / rig.World.TickSeconds);
        rig.World.RunTicks(ticks);

        Assert.Equal(charge - ticks * rig.World.TickSeconds, battery.Charge, 0.01f);
    }

    /// <summary>A complete machine whose saved town is unresolved does not create Meitou's town power grid.</summary>
    [Fact]
    public void A_no_town_machine_does_not_join_a_power_grid()
    {
        using var rig = new Rig(Database(), Constants, [Generator, Lamp, Battery], town: 0);
        rig.World.RunTick();
        var generator = rig.Production.Ensure(rig.World, rig.World.Buildings.Find(rig.Sites[0])!)!;
        generator.Inputs[0].Count = 10;
        rig.World.RunTicks(2);
        Assert.Equal(0, rig.Machine(1).Granted);
        Assert.False(rig.Machine(2).OnGrid);
    }

    /// <summary>A complete research bench with an unresolved town does not contribute its level.</summary>
    [Fact]
    public void A_no_town_research_bench_does_not_raise_the_global_bench_level()
    {
        using var rig = new Rig(Database(), Constants, [Bench], town: 0);
        rig.World.RunTick();
        Assert.Equal(0, rig.World.Research.BenchLevel);
    }

    // ------------------------------------------------------------------ ore

    sealed class Rocks(params ResourceNode[] nodes) : IResourceNodes
    {
        public IReadOnlyList<ResourceNode> Near(int resource, float x, float z, float range) =>
            [.. nodes.Where(n => n.Resource == resource && Vector2.Distance(new(n.Position.X, n.Position.Z), new(x, z)) <= range)];
    }

    /// <summary>
    /// The same wind speed everywhere, no other weather.
    /// </summary>
    sealed class Wind(float speed) : IWeatherSource
    {
        public WeatherHere? At(float x, float z) => new WeatherHere(WeatherAffecting.None, 0, 0, 0, speed);
        public IReadOnlyList<WeatherAffectZone> Zones => [];
    }

    /// <summary>
    /// An ore mine yields the nearest rock of its resource x (1 - (d² - 625) / (1500² - 625)) (docs/game/buildings-production.md "Resources"): the
    /// rock's own yield is the resources record's iron mult (0.6), 0.1 for copper which the record lacks; nothing beyond 1500, for a resource with no
    /// rock, or with no rock source at all.
    /// </summary>
    [Fact]
    public void An_ore_mine_yields_its_nearest_rock_with_distance_falloff()
    {
        var resources = Rec("91-t", FcsRecordType.EnvironmentResources, "Ore ground");
        resources.Floats["iron mult"] = 0.6f;
        var group = Rec("90-t", FcsRecordType.BiomeGroup, "Ore lands");
        group.References["resources"] = [new FcsReference("91-t", 0, 0, 0)];
        var file = new FcsFile();
        file.Records.AddRange([resources, group]);
        var db = new GameDatabase();
        db.Apply(file, "ore.mod");
        var cells = new int[WeatherAreas.Cells * WeatherAreas.Cells];
        var map = new BiomeMap(BiomeGroupData.LoadAll(db), cells);
        var rock = new Vector3(1000, 300, 2000);
        var field = new ResourceField(db, map, new Rocks(
            new ResourceNode(rock, ResourceField.IronResource, 25),
            new ResourceNode(rock + new Vector3(1000, 0, 0), ResourceField.IronResource, 25),
            new ResourceNode(rock + new Vector3(0, 0, 500), ResourceField.CopperResource, 25)));
        float Falloff(float d) => 1 - (d * d - 625) / (1500f * 1500f - 625);

        Assert.Equal(0.6f, field.Yield(ResourceField.IronResource, rock), 0.0001f);
        Assert.Equal(0.6f * Falloff(400), field.Yield(ResourceField.IronResource, rock + new Vector3(-400, 0, 0)), 0.0001f);
        Assert.Equal(0.6f * Falloff(100), field.Yield(ResourceField.IronResource, rock + new Vector3(900, 0, 0)), 0.0001f);
        Assert.Equal(0, field.Yield(ResourceField.IronResource, rock + new Vector3(-1600, 0, 0)));
        Assert.Equal(0.1f, field.Yield(ResourceField.CopperResource, rock + new Vector3(0, 0, 500)), 0.0001f);
        Assert.Equal(0, field.Yield(ResourceField.CarbonResource, rock));
        Assert.Equal(0, new ResourceField(db, map).Yield(ResourceField.IronResource, rock));
    }

    #endregion

    // ------------------------------------------------------------------ the installed game

    /// <summary>
    /// The installed Weapon Smith (2033) and Leather Armour Crafting Bench (2262) offer the documented crafts (docs/game/buildings-production.md
    /// "Crafting"): every PLAYER_WEAPONS weapon type in each listed model, a katana at level 30 taking (0.3² x 100 x value/300 x 2/P) h and its
    /// material cost in iron plates; a Tricorn Hat taking coverage/100 x lerp(0.4, 1, 5) x 0.6 h and drawing coverage/100 leather and that x fabrics
    /// amount of fabrics. With no research the weapons are locked; with all of it some are not.
    /// </summary>
    // ------------------------------------------------------------------ hauling

    #region Haulage and job lists

    /// <summary>
    /// Taking an output into an empty inventory moves it (once); the labourer carries the well's full output to the tank, the farmer fetches it for
    /// the farm's empty input with no water made or lost on the way; the tank refuses crops, by hand and as the farm's output (docs/simulation.md
    /// "Production", Hauling).
    /// </summary>
    [Fact]
    public void Operators_haul_a_full_output_to_a_storage_that_takes_it_and_fetch_an_empty_input()
    {
        using var rig = new Rig(Database(), Constants, [Well, Farm, Tank]);
        var taker = rig.Add(0, StatsEnumerated.Labouring, Unit);
        var labourer = rig.Add(0, StatsEnumerated.Labouring, Unit);
        var farmer = rig.Add(1, StatsEnumerated.Farming, Unit, (Crop, 1));
        rig.World.RunTick();
        var (well, farm, tank) = (rig.Machine(0), rig.Machine(1), rig.Machine(2).Storage!);
        well.Output!.Count = 3;
        rig.World.Commands.Enqueue(new TakeFromMachine(taker, rig.Sites[0], -2) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.Equal((0, 3), (well.Output.Count, rig.Count(taker, Water)));

        rig.Put(farmer, Crop, 2, -1);
        rig.World.RunTick();

        Assert.Equal((1, 0), (rig.Count(farmer, Crop), tank.Items.Count));
        Assert.Contains(rig.World.TakeMessages(), m => m == "Test Tank does not take Crop.");

        // The well makes nothing more, so the 10 water and the taker's 3 only move.
        well.Output.Count = 10;
        well.Yield = 0;
        rig.World.Commands.Enqueue(new OperateOrder([labourer], rig.Sites[0]) { Tick = rig.World.Tick });
        rig.World.Commands.Enqueue(new OperateOrder([farmer], rig.Sites[1]) { Tick = rig.World.Tick });
        int TotalWater() => well.Output.Count + farm.Inputs[0].Count + tank.Items.Where(i => i.Record == Water)
                .Sum(i => i.Quantity) + rig.Count(labourer, Water) + rig.Count(farmer, Water) + rig.Count(taker, Water);
        bool stored = false;
        for (int t = 0; t < 30000 && farm.Inputs[0].Count == 0; t++)
        {
            rig.World.RunTick();
            stored |= tank.Items.Any(i => i.Record == Water);

            Assert.Equal(13, TotalWater());
        }

        Assert.True(stored && farm.Inputs[0].Count > 0, $"tank {tank.Items.Count}, farm {farm.Inputs[0].Count}");
        Assert.Equal((1, 0), (rig.Count(farmer, Crop), tank.Items.Count(i => i.Record == Crop)));
    }

    // ------------------------------------------------------------------ job lists

    /// <summary>
    /// The index of the list job the character is on (<see cref="PermaJobs.ListJob"/>), -1 when none.
    /// </summary>
    static int JobOf(SimWorld world, CharacterId id) =>
        PermaJobs.ListJob(world.Characters.Cold(id.Slot)!,
                (CharacterTask)world.Characters.Next[id.Slot].Task) is { Implicit: false } job ? job.Job : -1;

    [Fact]
    public void A_job_list_runs_the_first_job_that_can_run_and_goes_back_to_a_higher_one()
    {
        using var rig = new Rig(Database(), Constants, [Well, Well]);
        var w = rig.Add(0, StatsEnumerated.Labouring, Unit);
        rig.World.RunTick();
        rig.World.Commands.Enqueue(new AddJob([w], Data.Gameplay.Ai.TaskType.OperateMachinery, rig.Sites[0]) { Tick = rig.World.Tick });
        rig.World.Commands.Enqueue(new AddJob([w], Data.Gameplay.Ai.TaskType.OperateMachinery, rig.Sites[1]) { Tick = rig.World.Tick });
        var cold = rig.World.Characters.Cold(w.Slot)!;
        void Look() => rig.World.RunTicks(2 * ProductionSystem.JobCheckTicks);
        Look();

        Assert.Equal((rig.Sites[0], 0), (cold.OperateSite, JobOf(rig.World, w)));

        // A full output with no storage blocks A: on to B; emptied, A takes over again.
        rig.Machine(0).Output!.Count = 10;
        Look();

        Assert.Equal((rig.Sites[1], 1), (cold.OperateSite, JobOf(rig.World, w)));

        rig.Machine(0).Output!.Count = 0;
        Look();

        Assert.Equal((rig.Sites[0], 0), (cold.OperateSite, JobOf(rig.World, w)));

        // An order takes it off its job; idle again, the list takes over.
        rig.World.Commands.Enqueue(new MoveOrder([w], At + new Vector3(0, 0, 150)) { Tick = rig.World.Tick });
        rig.World.RunTicks(2);

        Assert.Equal(-1, JobOf(rig.World, w));

        for (int t = 0; t < 3000 && rig.World.Characters.Next[w.Slot].Task != (byte)CharacterTask.Idle; t++) rig.World.RunTick();
        Look();

        Assert.Equal((rig.Sites[0], 0), (cold.OperateSite, JobOf(rig.World, w)));

        // Removing the job it is on: the next one runs.
        rig.World.Commands.Enqueue(new RemoveJob(w, 0) { Tick = rig.World.Tick });
        Look();

        Assert.Equal((rig.Sites[1], 0, 1), (cold.OperateSite, JobOf(rig.World, w), cold.Jobs.Count));
    }

    /// <summary>
    /// A character takes a machine job just inside the original's job range (about 6517, docs/game/ai-tasks.md "Perma-jobs") and skips one just
    /// outside it.
    /// </summary>
    [Fact]
    public void A_machine_job_beyond_the_job_range_is_skipped()
    {
        using var rig = new Rig(Database(), Constants, [Well, Well]);
        var (far, near) = (rig.Add(0, new Vector3(0, 0, 6600), StatsEnumerated.Labouring, Unit),
                rig.Add(1, new Vector3(0, 0, 6400), StatsEnumerated.Labouring, Unit));
        rig.World.RunTick();
        rig.World.Commands.Enqueue(new AddJob([far], Data.Gameplay.Ai.TaskType.OperateMachinery, rig.Sites[0]) { Tick = rig.World.Tick });
        rig.World.Commands.Enqueue(new AddJob([near], Data.Gameplay.Ai.TaskType.OperateMachinery, rig.Sites[1]) { Tick = rig.World.Tick });
        rig.World.RunTicks(2 * ProductionSystem.JobCheckTicks);

        Assert.Equal((-1, 0), (JobOf(rig.World, far), JobOf(rig.World, near)));
    }

    /// <summary>
    /// Jobs farm then well, the farm dry and no water anywhere: the farmer pumps the well full, carries its water into the farm and then tends the
    /// growing crop, counted as operating (docs/simulation.md "Production", Hauling and 87).
    /// </summary>
    [Fact]
    public void A_farmer_fills_the_well_carries_its_water_to_the_farm_and_tends_the_growing_crop()
    {
        using var rig = new Rig(Database(), Constants, [Farm, Well]);
        var farmer = rig.Add(0, StatsEnumerated.Farming, Unit);
        rig.World.RunTick();
        var (farm, well) = (rig.Machine(0), rig.Machine(1).Output!);
        rig.World.Commands.Enqueue(new AddJob([farmer], Data.Gameplay.Ai.TaskType.OperateMachinery, rig.Sites[0]) { Tick = rig.World.Tick });
        rig.World.Commands.Enqueue(new AddJob([farmer], Data.Gameplay.Ai.TaskType.OperateMachinery, rig.Sites[1]) { Tick = rig.World.Tick });
        var cold = rig.World.Characters.Cold(farmer.Slot)!;
        bool pumped = false;
        for (int t = 0; t < 30000 && farm.Inputs[0].Count == 0; t++)
        {
            rig.World.RunTick();
            pumped |= cold.OperateSite == rig.Sites[1] && well.Full;
        }

        Assert.True(pumped && farm.Inputs[0].Count == 10, $"well {well.Count}, farm {farm.Inputs[0].Count}, carried {rig.Count(farmer, Water)}");

        for (int t = 0; t < 600 && !cold.Operating; t++) rig.World.RunTick();

        Assert.True(cold.Operating && cold.OperateSite == rig.Sites[0] && farm.Farm!.Growth < 1,
                $"operating {cold.Operating} at {cold.OperateSite}, growth {farm.Farm!.Growth}");
    }

    [Fact]
    public void Adding_a_job_follows_the_originals_rows_and_a_job_list_saves_and_reads_back()
    {
        using var rig = new Rig(Database(), Constants, [Well, Tank]);
        var cold = new CharacterCold();
        var (a, b) = (rig.Sites[0], rig.Sites[1]);

        // 25 (first aid) becomes the medic job 58; 152 brings its companion 124 on the same target; the switch turns on.
        Assert.True(PermaJobs.Add(cold, Data.Gameplay.Ai.TaskType.FirstAidOrder,
                new PermaJob(Data.Gameplay.Ai.TaskType.FirstAidOrder, null, Data.Save.Hand.Null)));
        Assert.True(PermaJobs.Add(cold, Data.Gameplay.Ai.TaskType.OperateAutomaticMachinery,
                new PermaJob(Data.Gameplay.Ai.TaskType.OperateAutomaticMachinery, a)));
        Assert.Equal([(58, (string?)null), (152, a), (124, a)], cold.Jobs.Select(j => ((int)j.Type, j.Site)));
        Assert.True((cold.Orders & CharacterOrders.Jobs) != 0);

        // The same target twice, and a single-target type twice, are refused; 98 (sleeping) is no job.
        Assert.False(PermaJobs.Add(cold, Data.Gameplay.Ai.TaskType.OperateAutomaticMachinery,
                new PermaJob(Data.Gameplay.Ai.TaskType.OperateAutomaticMachinery, a)));
        Assert.False(PermaJobs.Add(cold, Data.Gameplay.Ai.TaskType.FirstAidOrder, new PermaJob(Data.Gameplay.Ai.TaskType.FirstAidOrder, b)));
        Assert.True(PermaJobs.Add(cold, Data.Gameplay.Ai.TaskType.OperateAutomaticMachinery,
                new PermaJob(Data.Gameplay.Ai.TaskType.OperateAutomaticMachinery, b)));
        Assert.Null(PermaJobs.JobOf(Data.Gameplay.Ai.TaskType.UseBed));

        // A job list saves its switch off and reads back without doubling companions.
        var saved = new CharacterCold();
        PermaJobs.Add(saved, Data.Gameplay.Ai.TaskType.OperateAutomaticMachinery,
                new PermaJob(Data.Gameplay.Ai.TaskType.OperateAutomaticMachinery, a));
        saved.Orders &= ~CharacterOrders.Jobs;
        var ai = new FcsRecord { Type = (int)FcsRecordType.GamestateAi, Name = "ai" };
        Meitou.Simulation.Saving.PermaJobSave.Write(ai, saved, rig.World.Buildings, new Dictionary<string, FcsRecord>());

        Assert.Equal((152, 124, false), (ai.Ints["pjob0"], ai.Ints["pjob1"], ai.Bools["jobs"]));

        var quick = new Meitou.Data.Save.SaveFile();
        quick.Data.Records.Add(new FcsRecord { Type = (int)FcsRecordType.Camera, StringId = "1051", Name = "camera" });
        var back = new CharacterCold { Orders = Orders.PlayerDefaults };
        Meitou.Simulation.Saving.PermaJobSave.Read(ai, null, back, rig.World.Buildings, new Meitou.Data.Save.SaveGame(quick).BuildHandles(),
                new Dictionary<FcsRecord, string>(), new HashSet<string>());

        // Re-adding 152 brings 124 again and the saved 124 is then refused as a duplicate; the switch is read last.
        Assert.Equal(saved.Jobs.Select(j => (j.Type, j.Site)), back.Jobs.Select(j => (j.Type, j.Site)));
        Assert.True((back.Orders & CharacterOrders.Jobs) == 0);
    }

    #endregion

    #region Benches, wells and blueprints

    [Fact, Slow]
    public void The_installed_weapon_and_armour_benches_offer_the_documented_crafts()
    {
        var db = InstallData.BaseGame;

        Assert.SkipWhen(db is null, "Kenshi install not found");

        var constants = GameConstants.FromDatabase(db!);
        using var rig = new Rig(db!, constants, ["2033-gamedata.base", "2262-gamedata.base"]);
        rig.World.RunTick();
        var smith = rig.Machine(0).Data;
        var maker = db!.Find(CraftBench.PlayerWeapons)!;
        var models = db.Find(db.Find("2033-gamedata.base")!.GetReferences("functionality")[0].TargetStringId)!.GetReferences("weapon crafts")
                .Select(r => r.TargetStringId).ToHashSet();
        int types = maker.GetReferences("weapon types").Count(t => db.Find(t.TargetStringId) is { Type: FcsRecordType.Weapon });
        int listed = maker.GetReferences("weapon models").Count(m => models.Contains(m.TargetStringId));

        Assert.Equal(types * listed, smith.Craft!.Options.Count);

        var katana = db.OfType(FcsRecordType.Weapon).First(r => r.Name == "Katana");
        var recipe = smith.Craft.Recipe(db, constants, new CraftOption(CraftKind.Weapon, katana.StringId, null, 30, 1), smith.Inputs);

        Assert.Equal(0.09f * 100 * katana.GetInt("value") / 300f * 2 / constants.ProductionSpeed, recipe.Hours, 0.001f);
        Assert.Equal((smith.Inputs[0].Item, (float)katana.GetInt("material cost")), (recipe.Inputs[0].Item, recipe.Inputs[0].PerItem));

        var bench = rig.Machine(1).Data;
        var hat = db.OfType(FcsRecordType.Armour).First(r => r.Name == "Tricorn Hat");
        float coverage = hat.GetReferences("part coverage")
                .Sum(p => p.TargetStringId == "101-gamedata.quack" ? (int)(p.Values.Value0 * 1.5) : p.Values.Value0) / 100f;
        var hatRecipe = bench.Craft!.Recipe(db, constants, bench.Craft.Options.First(o => o.Item == hat.StringId), bench.Inputs);

        Assert.Equal(MathF.Max(1, coverage * 2.6f) * 0.6f, hatRecipe.Hours, 0.001f);

        var leather = hatRecipe.Inputs.Single(i => i.Item != CraftBench.Fabrics);

        Assert.Equal(coverage, leather.PerItem, 0.001f);
        Assert.Equal(coverage * hat.GetFloat("fabrics amount", 0.2f), hatRecipe.Inputs.Single(i => i.Item == CraftBench.Fabrics).PerItem, 0.001f);

        Assert.DoesNotContain(rig.Production.CraftOptions(rig.World, rig.Sites[0]), o => o.Unlocked);

        rig.World.Research.Set(db, [.. db.OfType(FcsRecordType.Research).Select(r => r.StringId)]);

        Assert.Contains(rig.Production.CraftOptions(rig.World, rig.Sites[0]), o => o.Unlocked);
    }

    /// <summary>
    /// The installed Well (1921) at skill factor 1 yields nothing on ground with no water value (the 0.5 fallback is not the player's), and at yield
    /// 0.5 makes 36 x 0.5 water per game day (docs/game/buildings-production.md "Production"); the Small Research Bench (1875) finishes a free level
    /// 1 research in its hours x G/24 seconds at its output rate 0.85.
    /// </summary>
    [Fact, Slow]
    public void The_installed_well_and_research_bench_work_at_the_documented_rates()
    {
        var db = InstallData.BaseGame;

        Assert.SkipWhen(db is null, "Kenshi install not found");

        var constants = GameConstants.FromDatabase(db!);
        using var rig = new Rig(db!, constants, ["1921-gamedata.base", "1875-gamedata.base"]);
        var labourer = rig.Add(0, StatsEnumerated.Labouring, Unit);
        var scientist = rig.Add(1, StatsEnumerated.Science, Unit);
        rig.World.RunTick();

        Assert.Equal(0f, rig.Machine(0).Yield);

        rig.Machine(0).Yield = 0.5f;
        rig.World.Commands.Enqueue(new OperateOrder([labourer], rig.Sites[0]) { Tick = rig.World.Tick });
        rig.World.Commands.Enqueue(new OperateOrder([scientist], rig.Sites[1]) { Tick = rig.World.Tick });
        rig.World.RunTicks(30);
        var research = ResearchData.LoadAll(db!).First(r => rig.Production.Available(rig.World, r) && r.Cost.Count == 0 && r.Level <= 1);
        rig.World.Commands.Enqueue(new QueueResearch(research.Id) { Tick = rig.World.Tick });

        // Stats rise while operating, so the first quarter day at s ~ 1 is compared (the well's output holds 10).
        float quarter = MachineData.DaySeconds / 4;
        long started = rig.World.Tick;
        float expectedSeconds = research.Hours(constants) * MachineData.DaySeconds / 24 / 0.85f;
        long done = -1;
        while ((rig.World.Tick - started) * rig.World.TickSeconds < quarter)
        {
            rig.World.RunTick();
            if (done < 0 && rig.World.Research.IsDone(research.Id)) done = rig.World.Tick;
        }
        var well = rig.Machine(0).Output!;

        Assert.InRange(well.Count + well.Progress, 36 * 0.5f / 4 * 0.95f, 36 * 0.5f / 4 * 1.1f);
        Assert.True(done > 0, research.Name);
        Assert.InRange((done - started) * rig.World.TickSeconds, expectedSeconds * 0.9f, expectedSeconds * 1.02f);
    }

    /// <summary>
    /// Blueprints (docs/game/buildings-production.md "Blueprints"): every base-game armour, backpack and crossbow gets a priced <c>.TECH.1</c>
    /// research, armour enabling its own craft; a right click on a blueprint whose requirements are unmet keeps it, one with no requirements
    /// completes its research and is used up.
    /// </summary>
    [Fact, Slow]
    public void Gear_blueprints_are_made_at_load_and_a_right_click_learns_one()
    {
        var db = InstallData.BaseGame;

        Assert.SkipWhen(db is null, "Kenshi install not found");

        var tech = db!.Records.Values.Where(r => r.StringId.EndsWith(".TECH.1", StringComparison.Ordinal)).ToList();

        Assert.Equal(GearCount, tech.Count);

        // Priced from the item: only the shackles, worth nothing, give a free blueprint.
        Assert.All(tech,
                r => Assert.True(r.Type == FcsRecordType.Research && r.GetBool("blueprint only") && (r.GetInt("money") > 0
                || db.Find(r.StringId[..^7])!.GetBool("is locked")), r.StringId));

        var armour = db.OfType(FcsRecordType.Armour).First();

        Assert.Contains(db.Find(armour.StringId + ".TECH.1")!.GetReferences("enable armour"), e => e.TargetStringId == armour.StringId);

        var locked = db.OfType(FcsRecordType.Research).First(r => r.GetBool("blueprint only")
                && r.GetReferences("requirements").Any(q => db.Find(q.TargetStringId)?.Type == FcsRecordType.Research));
        var gear = db.Find(armour.StringId + ".TECH.1")!;
        using var rig = new Rig(db, GameConstants.FromDatabase(db), []);
        var who = rig.World.Characters.Spawn(new CharacterHot { Position = At, Task = (byte)CharacterTask.Idle },
            new CharacterCold { Name = "reader", Race = Human, Medical = MedicalState.Create(Human, 50), Inventory = new Inventory(), IsPlayer = true,
                    Faction = PlayerFaction }, 0);
        var inventory = rig.Carried(who);
        inventory.Add(rig.Items.Create(locked.StringId)!);
        inventory.Add(rig.Items.Create(gear.StringId)!);
        void Read(GameRecord research)
        {
            var all = inventory.All().ToList();
            int index = all.FindIndex(i => i.CompanyId == research.StringId);
            var it = all[index];
            rig.World.Commands.Enqueue(new QuickMoveItem(who,
                    new InventoryItemToken(index, it.Record, it.Section, it.X, it.Y, it.Quantity, it.Quality), false,
                    false) { Tick = rig.World.Tick });
            rig.World.RunTick();
        }
        Read(locked);

        Assert.False(rig.World.Research.IsDone(locked.StringId));
        Assert.Contains(inventory.All(), i => i.CompanyId == locked.StringId);

        Read(gear);

        Assert.True(rig.World.Research.IsDone(gear.StringId));
        Assert.DoesNotContain(inventory.All(), i => i.CompanyId == gear.StringId);
        Assert.Contains(rig.World.TakeMessages(), m => m == "Research complete: " + gear.Name);
    }

    #endregion

    #region Installed world and farms

    /// <summary>
    /// The installed Small Generator (1976, 40) with Upgraded Small Generators gives 55; the Battery Bank (1855, rate 40, store 60) with Battery
    /// Power, Advanced Battery Power, Battery Capacity and Advanced Battery Capacity charges at 60 into 180; the Wind Generator (1999, 50, no fuel)
    /// gives its output x its blades' wind share (wind 10 to 50: half at 30) (docs/game/buildings-production.md "Power"). The resource storages take
    /// only their resource.
    /// </summary>
    [Fact, Slow]
    public void The_installed_generators_and_battery_take_their_research_improvements_and_storages_their_resource()
    {
        var db = InstallData.BaseGame;

        Assert.SkipWhen(db is null, "Kenshi install not found");

        using var rig = new Rig(db!, GameConstants.FromDatabase(db!), ["1976-gamedata.base", "1855-gamedata.base", "1999-gamedata.base"]);
        string[] names = ["Upgraded Small Generators", "Battery Power", "Advanced Battery Power", "Battery Capacity", "Advanced Battery Capacity"];
        rig.World.Research.Set(db!, [.. names.Select(n => db!.OfType(FcsRecordType.Research).First(r => r.Name == n).StringId)]);
        rig.World.Weather = new Wind(30);
        rig.World.RunTick();
        rig.Production.Ensure(rig.World, rig.World.Buildings.Find(rig.Sites[0])!)!.Inputs[0].Count = 10;
        rig.World.RunTicks(2);
        var (generator, battery, wind) = (rig.Machine(0), rig.Machine(1), rig.Machine(2));

        Assert.True(wind.Data.IsWind && !generator.Data.IsWind && battery.Data.IsBattery);
        Assert.Equal((55f, 25f, 60f, 180f), (generator.Demand, wind.Demand, battery.Granted, battery.Store));

        // The Water Tower (1950) takes only Water and Storage: Ore only Raw Iron (1866), both resource storages with stacks x 100
        // (docs/game/buildings-production.md "Storage").
        var tower = MachineData.From(db!, db!.Find("1950-gamedata.base")!)!;
        var ore = MachineData.From(db!, db!.OfType(FcsRecordType.Building).First(r => r.Name == "Storage: Ore"))!;
        string water = db!.OfType(FcsRecordType.Item).First(r => r.Name == "Water").StringId, iron = "1866-gamedata.base";

        Assert.True(tower.IsStorage && ore.IsStorage);
        Assert.Equal((true, false, false, true),
                (tower.Accepts(water, FcsRecordType.Item), tower.Accepts(iron, FcsRecordType.Item), ore.Accepts(water, FcsRecordType.Item),
                ore.Accepts(iron, FcsRecordType.Item)));
        Assert.Equal((100f, 100f), (tower.StackBonusMult, ore.StackBonusMult));
    }

    /// <summary>
    /// The foliage of the middle zone (32, 32) holds Iron Resource rocks; an ore mine standing on one yields the rock's own yield, which is between
    /// 0.1 and 1 (docs/game/buildings-production.md "Resources").
    /// </summary>
    [Fact, Slow]
    public void The_installed_world_has_iron_rocks_an_ore_mine_finds()
    {
        var db = InstallData.BaseGame;

        Assert.SkipWhen(db is null || InstallData.Levels is null, "Kenshi install not found");

        var install = InstallData.Install!;
        using var nodes = new FoliageResourceNodes(new FoliageWorld(install, db!, InstallData.Levels!), db!);
        var (x, z) = WorldLayout.ZoneOrigin(new ZoneCoordinate(32, 32));
        var rocks = nodes.Near(ResourceField.IronResource, (float)x + WorldLayout.ZoneSize / 2f, (float)z + WorldLayout.ZoneSize / 2f,
                WorldLayout.ZoneSize / 2f);

        Assert.NotEmpty(rocks);

        var field = new ResourceField(db!, BiomeMap.Create(db!, WeatherAreas.Load(install)), nodes);
        float own = field.NodeYield(ResourceField.IronResource, rocks[0].Position);

        Assert.InRange(own, 0.1f, 1f);
        Assert.Equal(own, field.Yield(ResourceField.IronResource, rocks[0].Position));
    }

    /// <summary>
    /// The farm catch-up (docs/game/buildings-production.md "Farm catch-up"): watered, the crop moves on h hours and w x rate water is used; dry,
    /// only the watered hours (water / (rate x h), the per-second rate with the game hours, as the game mixes them) count in full and the rest grows
    /// at the drought multiplier while the death rises.
    /// </summary>
    [Fact]
    public void A_farm_catches_up_its_unloaded_hours_watered_or_in_drought()
    {
        using var rig = new Rig(Database(), Constants, [Farm]);
        rig.World.RunTick();
        var m = rig.Machine(0);
        Machine Copy(float level, int count)
        {
            var copy = new Machine
            {
                Site = m.Site, Position = m.Position,
                        Data = m.Data with { Farm = m.Data.Farm! with { DroughtMultiplier = 0.5f, DroughtDeathTime = 100 } },
                Inputs = m.Inputs, Farm = new FarmState(4), WaterRate = 1,
            };
            (m.Inputs[0].Level, m.Inputs[0].Count) = (level, count);
            return copy;
        }

        // Watered: 10 water gives w = 10 / (1 x 2) = 5, more than the 2 h away; the 1 h growth time and 1 h of the 48 h harvest time pass; 5 water
        // drawn, the level full.
        var wet = Copy(0, 10);
        rig.Production.TimeSkip(rig.World, wet, 2, false);

        Assert.Equal(1 + 1 / 48f, wet.Farm!.Growth, 4);
        Assert.Equal(0, wet.Farm.Death);
        Assert.Equal(5, wet.Inputs[0].Count);
        Assert.Equal(1, wet.Inputs[0].Level);

        // Dry: 0.2 water over 4 h counts 0.2 / (1 x 4) = 0.05 h; the other 3.95 h pass unstretched (FUN_1400df6e0: the drought only stretches the
        // growth phase, here 1 / 0.5 = 2 h) and reach 4 h of the cycle: ripening, 1 + (4 - 2) / 48 of the harvest time; the death 3.95 / 100. The
        // water left is less than it needs: the level empties.
        var dry = Copy(0.2f, 0);
        rig.Production.TimeSkip(rig.World, dry, 4, false);

        Assert.Equal(1 + 2f / 48, dry.Farm!.Growth, 4);
        Assert.Equal(0.0395f, dry.Farm.Death, 4);
        Assert.Equal(0, dry.Inputs[0].Level);
    }

    #endregion
}
