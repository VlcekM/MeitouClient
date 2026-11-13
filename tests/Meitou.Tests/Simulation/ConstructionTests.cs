using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Player construction (docs/simulation.md "Buildings", docs/game/buildings-production.md "Construction"): sites, builders, materials, dismantling.
/// </summary>
public class ConstructionTests
{
    const string Plank = "70-t", Iron = "71-t", Hut = "80-t", Shed = "81-t", Outpost = "82-t", Research = "90-t";
    const string WallRecord = "83-t", ShortRecord = "84-t", GateRecord = "85-t", ConnectorRecord = "86-t";
    const string GateMk1 = "87-t", GateMk2 = "88-t", StoreMk1 = "91-t", StoreMk2 = "92-t";
    /// <summary>
    /// Engineering 41.17647: lerp(0.3, 2, e/100) is 1, the factor the build window's estimate leaves out.
    /// </summary>
    const float Unit = 100 * 0.7f / 1.7f;

    /// <summary>
    /// The base game's build speed (6) and refund fraction (0.6), which the shared constants fixture lacks.
    /// </summary>
    static readonly GameConstants Constants = BaseGame with { BuildSpeed = 6, MinDismantleMaterialsFraction = 0.6f };

    static readonly Vector3 HutAt = new(1060, 300, 2000), ShedAt = new(1100, 300, 2040);

    static FcsRecord Rec(string id, FcsRecordType type, string name) => new() { StringId = id, Flags = 0x10, Name = name, RecordType = type };

    /// <summary>
    /// Two items, a hut (3 planks, 2 iron, build speed mult 2), a shed (4 Building Materials) and a camp hut that creates a player town (2 Building
    /// Materials), unlocked by one research.
    /// </summary>
    static GameDatabase Database()
    {
        FcsRecord Item(string id, string name)
        {
            var r = Rec(id, FcsRecordType.Item, name);
            r.Floats["weight kg"] = 1;
            return r;
        }
        var hut = Rec(Hut, FcsRecordType.Building, "Test Hut");
        hut.References["construction"] = [new FcsReference(Plank, 3, 0, 0), new FcsReference(Iron, 2, 0, 0)];
        hut.Floats["build speed mult"] = 2;
        var shed = Rec(Shed, FcsRecordType.Building, "Test Shed");
        shed.Ints["build materials"] = 4;
        var research = Rec(Research, FcsRecordType.Research, "Test Research");
        var outpost = Rec(Outpost, FcsRecordType.Building, "Test Camp Hut");
        outpost.Ints["build materials"] = 2;
        outpost.Bools["creates player town"] = true;
        outpost.References["interior"] = [new FcsReference("93-t", 0, 0, 0)];   // a house: it alone sets the outpost's centre
        FcsRecord Wall(string id, string name, int linkType, float length, int materials)
        {
            var r = Rec(id, FcsRecordType.Building, name);
            r.Bools["links together"] = true;
            r.Ints["link type"] = linkType;
            r.Floats["link length"] = length;
            r.Ints["build materials"] = materials;
            return r;
        }
        var wall = Wall(WallRecord, "Test Wall", 0, 100, 2);
        wall.References["wall subsections"] = [new FcsReference(ShortRecord, 0, 0, 0), new FcsReference(ConnectorRecord, 0, 0, 0)];
        var gate = Wall(GateRecord, "Test Gate", 3, 100, 2);
        gate.Bools["is wall gate"] = true;
        gate.References["wall subsections"] = [new FcsReference(ConnectorRecord, 0, 0, 0)];

        // A gate chain whose upgrade costs more than the 50 units an upgrade keeps.
        var mk1 = Wall(GateMk1, "Test Gate Mk1", 3, 100, 55);
        mk1.Bools["is wall gate"] = true;
        mk1.References["upgrades to"] = [new FcsReference(GateMk2, 0, 0, 0)];
        var mk2 = Wall(GateMk2, "Test Gate Mk2", 3, 100, 60);
        mk2.Bools["is wall gate"] = true;

        // A storage chain: its machine's inventory is where an upgrade takes materials from.
        FcsRecord Store(string id, string name, int materials, int size)
        {
            var r = Rec(id, FcsRecordType.Building, name);
            r.Ints["build materials"] = materials;
            r.Bools["has inventory"] = true;
            (r.Ints["storage size width"], r.Ints["storage size height"]) = (size, size);
            return r;
        }
        var store1 = Store(StoreMk1, "Test Store Mk1", 2, 4);
        store1.References["upgrades to"] = [new FcsReference(StoreMk2, 0, 0, 0)];
        var store2 = Store(StoreMk2, "Test Store Mk2", 10, 6);
        research.References["enable buildings"] = [new FcsReference(Hut, 0, 0, 0), new FcsReference(Shed, 0, 0, 0),
                new FcsReference(Outpost, 0, 0, 0),
            new FcsReference(WallRecord, 0, 0, 0), new FcsReference(GateRecord, 0, 0, 0), new FcsReference(GateMk1, 0, 0, 0),
                    new FcsReference(GateMk2, 0, 0, 0),
            new FcsReference(StoreMk1, 0, 0, 0), new FcsReference(StoreMk2, 0, 0, 0)];
        var file = new FcsFile();
        file.Records.AddRange([Item(Plank, "Plank"), Item(Iron, "Iron"), Item(BuildingBuildData.BuildingMaterialsItem, "Building Materials"), hut,
                shed, outpost, research,
            wall, gate, mk1, mk2, store1, store2, Wall(ShortRecord, "Test Wall Short", 4, 50, 1),
                    Wall(ConnectorRecord, "Test Wall Connector", 1, 20, 1)]);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return db;
    }

    sealed class Rig : IDisposable
    {
        public const int PlayerFaction = 2;
        public SimWorld World { get; }
        public GameDatabase Db { get; }
        public ItemFactory Items { get; }
        public List<CharacterId> Ids { get; } = [];

        public Rig(int threads = 1, bool snapshots = false, ulong seed = 6, bool production = false,
            PopulationData? population = null)
        {
            var db = Db = Database();
            Items = new ItemFactory(db, Constants);
            var walk = new OpenGroundWalkability(SyntheticTown.Ground);
            var systems = new List<ITickSystem>
            {
                new PlayerSystem(), new MovementSystem(new PathService(walk, true)),
                new ConstructionSystem(db, Constants, BodyOptions.Default, Items, [], "", new ConstructionSettings()),
            };
            if (production)
                systems.Add(new Meitou.Simulation.Production.ProductionSystem(db, Constants, BodyOptions.Default, Items, null));
            if (population is not null) systems.Add(new PopulationSystem(population));
            World = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = snapshots, MinPartitionSize = 1 }, walk,
                    systems);
            World.Player.Faction = PlayerFaction;
            World.Research.Set(db, [Research]);
        }

        /// <summary>
        /// A player character at a spot with an engineering level and what it carries.
        /// </summary>
        public CharacterId Add(float x, float z, float engineering, params (string Item, int Count)[] carried)
        {
            var inventory = new Inventory();
            foreach (var (item, count) in carried) inventory.Add(Items.Create(item, count)!);
            var stats = new CharacterStats();
            stats[StatsEnumerated.Engineering] = engineering;
            var cold = new CharacterCold { Name = "builder" + Ids.Count, Race = Human, Stats = stats, Medical = MedicalState.Create(Human, 50),
                    Inventory = inventory, IsPlayer = true, Faction = PlayerFaction };
            var id = World.Characters.Spawn(new CharacterHot { Position = new Vector3(x, 300, z), MaxSpeed = 40, WalkSpeed = 15,
                    Task = (byte)CharacterTask.Idle }, cold, 0);
            Ids.Add(id);
            return id;
        }

        public CharacterCold Cold(CharacterId id) => World.Characters.Cold(id.Slot)!;
        public void Say(SimCommand c) => World.Commands.Enqueue(c);
        public int Carried(CharacterId id, string item) => Cold(id).Inventory!.All().Where(i => i.Record == item).Sum(i => i.Quantity);
        public void Dispose() => World.Dispose();
    }

    static string SiteId(int n, Vector3 at) => BuildingRegistry.IdOf(n, WorldLayout.ZoneOf(at.X, at.Z));

    // ------------------------------------------------------------------ determinism (the golden hash)

    /// <summary>
    /// A hut and a shed are placed; one builder carries the hut's materials, another the shed's. The first finishes the hut, the shed is paused for a
    /// while, and the hut is taken apart (its refund falls on the ground). Hashes at eight checkpoints.
    /// </summary>
    internal static List<ulong> Scripted(int threads)
    {
        using var rig = new Rig(threads, seed: 9);
        var a = rig.Add(1000, 2000, 60, (Plank, 3), (Iron, 2));
        var b = rig.Add(1010, 2000, 20, (BuildingBuildData.BuildingMaterialsItem, 4));
        string hut = SiteId(1, HutAt), shed = SiteId(2, ShedAt);
        rig.Say(new PlaceBuildings([new PlannedBuilding(Hut, HutAt, Quaternion.Identity),
                new PlannedBuilding(Shed, ShedAt, Quaternion.Identity)]) { Tick = 2 });
        rig.Say(new BuildOrder([a], hut) { Tick = 5 });
        rig.Say(new BuildOrder([b], shed) { Tick = 5 });
        rig.Say(new PauseSite(shed, true) { Tick = 1200 });
        rig.Say(new PauseSite(shed, false) { Tick = 1600 });
        rig.Say(new DismantleOrder([a], hut) { Tick = 2300 });
        var hashes = new List<ulong>();
        foreach (int target in new[] { 1, 5, 60, 600, 1500, 2000, 2400, 3200 })
        {
            rig.World.RunTicks(target - (int)rig.World.Tick);
            hashes.Add(rig.World.StateHash());
        }
        return hashes;
    }

    /// <summary>
    /// Dismantling a loaded outpost's last building removes its saved instance and states, even with no other player towns left.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_dismantled_outpost_does_not_return_after_saving(bool keepOtherTown)
    {
        const string player = "204-gamedata.base";
        var save = new SaveBuilder(7).Game;
        using (var source = new Rig())
        {
            var town = source.World.Buildings.AddTown(HutAt, Rig.PlayerFaction);
            if (keepOtherTown)
            {
                // The other outpost holds a building: a reassessment removes an outpost without one.
                var otherAt = HutAt + new Vector3(8000, 0, 0);
                var other = source.World.Buildings.AddTown(otherAt, Rig.PlayerFaction);
                source.World.Buildings.Add(new BuildSite
                {
                    Id = SiteId(2, otherAt), Record = Hut, Position = otherAt, Town = other.Number, Owner = Rig.PlayerFaction,
                    Materials = [], Complete = true, Target = 1, Progress = 1,
                });
            }

            source.World.Buildings.Add(new BuildSite
            {
                Id = SiteId(1, HutAt), Record = Hut, Position = HutAt, Town = town.Number, Owner = Rig.PlayerFaction,
                Materials = [], Complete = true, Target = 1, Progress = 1,
            });
            BuildingSave.Write(save, source.World, player);
        }
        var original = save.TownInstanceList!.Instances[0];
        var oldStates = original.States.ToArray();
        var builder = new SaveBuilder(save, 7);
        var npc = builder.AddTown("NPC", "npc", "foreign", false, null, hand: new Hand(13, 100, 100, 0, 0));
        var nest = builder.AddTown("Nest", "nest", "foreign", true, null, hand: new Hand(85, 101, 101, 0, 0));
        save.TownInstanceList.Instances.Add(new FcsInstance { Id = "npc", Target = "npc", States = { npc.Record.StringId } });
        save.TownInstanceList.Instances.Add(new FcsInstance { Id = "nest", Target = "nest", States = { nest.Record.StringId } });

        using var rig = new Rig();
        BuildingSave.Read(save, rig.World, rig.Db, player, Rig.PlayerFaction);
        var worker = rig.Add(HutAt.X, HutAt.Z + 10, Unit);
        var site = rig.World.Buildings.All.Single(s => s.Id == SiteId(1, HutAt));
        rig.Say(new DismantleOrder([worker], site.Id) { Tick = rig.World.Tick });
        for (int t = 0; t < 3000 && rig.World.Buildings.Find(site.Id) is not null; t++) rig.World.RunTick();
        Assert.Equal(keepOtherTown ? 1 : 0, rig.World.Buildings.Count);
        Assert.Equal(keepOtherTown ? 1 : 0, rig.World.Buildings.Towns.Count);

        BuildingSave.Write(save, rig.World, player);
        save.Reindex();
        using var reloaded = new Rig();
        BuildingSave.Read(save, reloaded.World, reloaded.Db, player, Rig.PlayerFaction);
        Assert.Equal(keepOtherTown ? 1 : 0, reloaded.World.Buildings.Towns.Count);
        Assert.DoesNotContain(save.TownInstanceList.Instances, instance => instance.Id == original.Id);
        Assert.DoesNotContain(save.Quick.Records, state => oldStates.Contains(state.StringId));
        Assert.Contains(npc.Record, save.Quick.Records);
        Assert.Contains(nest.Record, save.Quick.Records);
        Assert.Contains(save.TownInstanceList.Instances, instance => instance.Id == "npc");
        Assert.Contains(save.TownInstanceList.Instances, instance => instance.Id == "nest");
    }

    // ------------------------------------------------------------------ a site built, a building taken apart

    [Fact]
    public void A_builder_completes_a_site_with_the_documented_rates_and_dismantling_refunds_60_to_100_percent()
    {
        using var rig = new Rig(snapshots: true);
        var builder = rig.Add(1000, 2000, Unit, (Plank, 3), (Iron, 2));
        rig.Say(new PlaceBuildings([new PlannedBuilding(Hut, HutAt, Quaternion.Identity),
                new PlannedBuilding("999-t", HutAt, Quaternion.Identity)]) { Tick = 0 });
        rig.World.RunTick();

        // The unknown record is dropped with the message; the hut is a site of 5 units, nothing delivered, and the snapshot has it with the
        // research's building list.
        var site = Assert.Single(rig.World.Buildings.All);

        Assert.Equal(SiteId(1, HutAt), site.Id);
        Assert.Equal(5, site.Target);
        Assert.Contains(rig.World.TakeMessages(), m => m == PlacementRules.Message(PlacementFailure.NotAvailable));

        var published = Assert.Single(rig.World.Snapshot.Buildings);

        Assert.False(published.Complete);
        Assert.Equal(3, published.Materials[0].Required);
        Assert.True(rig.World.Snapshot.BuildingsRevision > 0);
        Assert.Contains(Hut, rig.World.Snapshot.UnlockedBuildings);

        // One builder, engineering factor 1: three 5 s deliveries (planks and iron one unit per action) and 5 units of work at
        // build speed mult 2 x build speed 6 / (1200/11) = 0.11 units per second, i.e. 1364 ticks.
        rig.Say(new BuildOrder([builder], site.Id) { Tick = rig.World.Tick });
        long started = rig.World.Tick;
        for (int t = 0; t < 4000 && !site.Complete; t++) rig.World.RunTick();
        long ticks = rig.World.Tick - started;

        Assert.True(site.Complete);
        Assert.InRange(ticks, 3 * 150 + 1364 - 12, 3 * 150 + 1364 + 12);
        Assert.Equal(5, site.Progress);
        Assert.All(site.Materials, m => Assert.Equal(m.Required, m.Delivered));
        Assert.Equal(0, rig.Carried(builder, Plank) + rig.Carried(builder, Iron));
        Assert.Contains(rig.World.TakeMessages(), m => m == "Building 'Test Hut' construction complete.");
        Assert.True(rig.World.Snapshot.Buildings[0].Complete);
        Assert.True(rig.Cold(builder).Stats![StatsEnumerated.Engineering] > Unit);   // building trains engineering

        // Taking it apart: from target - 1 down at dt x lerp(0.05, 4, e) x 4 x rate = 0.123 per second (976 ticks), then the site is gone and the
        // refund lies around it, one ground item per unit.
        rig.Say(new DismantleOrder([builder], site.Id) { Tick = rig.World.Tick });
        started = rig.World.Tick;
        for (int t = 0; t < 3000 && rig.World.Buildings.Count > 0; t++) rig.World.RunTick();

        Assert.Empty(rig.World.Buildings.All);
        Assert.InRange(rig.World.Tick - started, 960, 1000);

        foreach (var (item, delivered) in new[] { (Plank, 3), (Iron, 2) })
        {
            var back = rig.World.GroundItems.All.Where(g => g.Item.Record == item).ToList();

            Assert.InRange(back.Count, Math.Max(1, (int)(0.6f * delivered)), delivered);
            Assert.All(back, g => Assert.Equal(1, g.Item.Quantity));
            Assert.All(back, g => Assert.InRange(MathF.Max(MathF.Abs(g.Position.X - HutAt.X), MathF.Abs(g.Position.Z - HutAt.Z)), 0, 20));
        }

        Assert.Equal(0, rig.Carried(builder, Plank) + rig.Carried(builder, Iron));
        Assert.DoesNotContain(rig.World.Snapshot.Buildings, b => b.Id == site.Id);
    }

    // ------------------------------------------------------------------ outposts

    /// <summary>
    /// A camp hut built in a town house joins that town and makes no outpost, though a far town lists a building of the same instance id first:
    /// instance ids are unique only within a zone (feedback #399, where a mod's <c>19-rebirth-INGAME</c> is in dozens of zones).
    /// </summary>
    [Fact]
    public void A_building_in_a_town_house_makes_no_outpost_when_a_far_zone_reuses_its_instance_id()
    {
        const string instance = "19-rebirth-INGAME";
        var farAt = HutAt + new Vector3(20000, 0, 0);
        TownSite Town(int index, Vector3 at, int zone) =>
            new(index, new TownData { Id = "town" + index, Name = "Town " + index }, at, new ZoneCoordinate(zone, 0))
            {
                Buildings = [new TownBuilding(instance, Hut, "House", at, 0, false, false, []) { Zone = new ZoneCoordinate(zone, 0) }],
            };
        var data = new PopulationData
        {
            Db = Database(), Sites = [Town(0, farAt, 9), Town(1, HutAt, 5)], Factions = [],
        };
        using var rig = new Rig(population: data);
        rig.Say(new PlaceBuildings([new PlannedBuilding(Outpost, HutAt + new Vector3(10, 0, 10), Quaternion.Identity, Parent: instance)]) { Tick = 0 });
        rig.World.RunTicks(2);

        Assert.Empty(rig.World.Buildings.Towns);
        Assert.Equal(0, Assert.Single(rig.World.Buildings.All).Town);
    }

    /// <summary>
    /// A shed out in the open has no town; a camp hut 3000 away makes "Your Outpost" at its position and takes the shed in (nearest town within
    /// 8000). The save writes the outpost as a town-list entry with its GAMESTATE_TOWN and the buildings' <c>town</c> handle; reading it back gives
    /// the same outpost, and saving again keeps its entry and handle.
    /// </summary>
    [Fact]
    public void A_creates_player_town_building_makes_an_outpost_that_round_trips_through_the_save()
    {
        const string playerFactionId = "204-gamedata.base";
        var campAt = ShedAt + new Vector3(3000, 0, 0);
        using var rig = new Rig();
        rig.Say(new PlaceBuildings([new PlannedBuilding(Shed, ShedAt, Quaternion.Identity)]) { Tick = 0 });
        rig.Say(new PlaceBuildings([new PlannedBuilding(Outpost, campAt, Quaternion.Identity)]) { Tick = 1 });
        rig.World.RunTicks(2);
        var town = Assert.Single(rig.World.Buildings.Towns);

        // Made at the hut, then reassessed: centred on its house (the shed does not move the centre), the radius 200 beyond the farthest building.
        Assert.Equal((1, PlayerTown.DefaultName, campAt, Rig.PlayerFaction), (town.Number, town.Name, town.Position, town.Owner));
        Assert.Equal(3200, town.Radius);
        Assert.All(rig.World.Buildings.All, s => Assert.Equal(1, s.Town));

        var builder = new SaveBuilder(1);
        builder.AddFaction(playerFactionId, "Nameless", true, 1000, []);
        builder.AddEmptyWorldState();
        BuildingSave.Write(builder.Game, rig.World, playerFactionId, 5);
        string dir = Path.Combine(Path.GetTempPath(), "meitou-outpost-" + Guid.NewGuid().ToString("N"));
        try
        {
            builder.Game.Write(dir);
            var save = SaveGame.Load(dir);
            var entry = Assert.Single(save.TownInstanceList!.Instances);

            Assert.Equal(PlayerTown.Template, entry.Target);

            var state = Assert.Single(save.Towns).Record;

            Assert.Equal((entry.Id + "-S5e", "Town state Your Outpost", entry.Id, playerFactionId),
                    (state.StringId, state.Name, state.Strings["instance"], state.Strings["faction"]));

            using var back = new Rig();

            Assert.Equal(2, BuildingSave.Read(save, back.World, back.Db, playerFactionId, Rig.PlayerFaction));

            var read = Assert.Single(back.World.Buildings.Towns);

            Assert.Equal((town.Name, town.Position, entry.Id, Hand.Read(state, "hand")),
                    (read.Name, read.Position, read.SaveInstance, read.SaveHandle!.Value));
            Assert.All(back.World.Buildings.All, s => Assert.Equal(read.Number, s.Town));

            var again = new SaveBuilder(save.Clone(), 1);
            BuildingSave.Write(again.Game, back.World, playerFactionId, 7);

            Assert.Equal(entry.Id, Assert.Single(again.Game.TownInstanceList!.Instances).Id);
            Assert.Equal(Hand.Read(state, "hand"), Hand.Read(Assert.Single(again.Game.Towns).Record, "hand"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// A regular placement joins a town of any owner whose actual radius contains it; the 5000-unit candidate search does not extend town ownership.
    /// </summary>
    [Theory]
    [InlineData(1249, true, false)]
    [InlineData(1249, true, true)]
    [InlineData(1250, true, false)]
    [InlineData(1250, true, true)]
    [InlineData(1251, false, false)]
    [InlineData(1251, false, true)]
    [InlineData(5001, false, false)]
    [InlineData(5001, false, true)]
    public void A_placed_building_joins_only_a_town_that_contains_it(float distance, bool contained, bool levelTown)
    {
        var population = levelTown ? new PopulationData
        {
            Db = Database(), Factions = [],
            Sites = [new TownSite(0, new TownData { Id = "npc", Name = "NPC outpost", Type = TownType.Outpost },
                new Vector3(0, 300, 0), new ZoneCoordinate(32, 32))],
        } : null;
        using var rig = new Rig(population: population);
        var town = rig.World.Buildings.AddTown(new Vector3(0, 300, 0), Rig.PlayerFaction);
        town.Override = levelTown ? 1 : 1250;
        if (levelTown) town.Position = new Vector3(-3000, 300, 0);
        rig.World.Buildings.Add(new BuildSite
        {
            Id = rig.World.Buildings.NewId(new ZoneCoordinate(32, 32)), Record = Hut, Position = new Vector3(0, 300, 0), Town = town.Number,
            Owner = Rig.PlayerFaction, Materials = [], Complete = true, Target = 1, Progress = 1,
        });

        rig.Say(new PlaceBuildings([new PlannedBuilding(Shed, new Vector3(distance, 300, 0), Quaternion.Identity)]) { Tick = 0 });
        rig.World.RunTicks(2);

        var placed = rig.World.Buildings.All.Single(site => site.Record == Shed);
        Assert.Equal(contained && !levelTown ? town.Number : 0, placed.Town);
        if (contained && levelTown) Assert.Equal(-1, TownLookup.KeyOf(rig.World, placed));
        Assert.Equal(!contained, placed.InNoTown);
        Assert.Equal(!contained, placed.ForeignTown is not null);
    }

    /// <summary>
    /// A new outpost adopts nearby existing player buildings in NO_TOWN and clears that association; a later refit alone does not adopt one.
    /// </summary>
    [Fact]
    public void Founding_adopts_no_town_buildings_but_refitting_does_not()
    {
        using var rig = new Rig();
        rig.Say(new PlaceBuildings([new PlannedBuilding(Shed, ShedAt, Quaternion.Identity)]) { Tick = 0 });
        rig.World.RunTicks(2);
        var orphan = rig.World.Buildings.All.Single();
        Assert.True(orphan.InNoTown);
        var foreignTown = new BuildSite
        {
            Id = rig.World.Buildings.NewId(new ZoneCoordinate(32, 32)), Record = Shed, Position = ShedAt + new Vector3(1000, 0, 0),
            Owner = Rig.PlayerFaction, Materials = [], Complete = true, Target = 1, Progress = 1,
            ForeignTown = new Hand(13, 511, -77, 0, 0),
        };
        rig.World.Buildings.Add(foreignTown);

        rig.Say(new PlaceBuildings([new PlannedBuilding(Outpost, ShedAt + new Vector3(3000, 0, 0), Quaternion.Identity)]) { Tick = 2 });
        rig.World.RunTicks(2);
        var town = Assert.Single(rig.World.Buildings.Towns);
        Assert.Equal(town.Number, orphan.Town);
        Assert.Null(orphan.ForeignTown);
        Assert.False(orphan.InNoTown);
        Assert.Equal(0, foreignTown.Town);
        Assert.Equal(new Hand(13, 511, -77, 0, 0), foreignTown.ForeignTown);

        var outsideAt = town.Position + new Vector3(10000, 0, 0);
        rig.Say(new PlaceBuildings([new PlannedBuilding(Shed, outsideAt, Quaternion.Identity)]) { Tick = rig.World.Tick });
        rig.World.RunTicks(2);
        var outside = rig.World.Buildings.All.Single(site => site.Position == outsideAt);
        Assert.True(outside.InNoTown);

        var fartherMemberAt = town.Position + new Vector3(15000, 0, 0);
        rig.World.Buildings.Add(new BuildSite
        {
            Id = rig.World.Buildings.NewId(new ZoneCoordinate(32, 32)), Record = Shed, Position = fartherMemberAt, Town = town.Number,
            Owner = Rig.PlayerFaction, Materials = [], Complete = true, Target = 1, Progress = 1,
        });
        ConstructionSystem.ReassessTown(rig.World, town);
        var fromCentre = new Vector2(outside.Position.X - town.Position.X, outside.Position.Z - town.Position.Z);
        Assert.True(fromCentre.Length() <= town.Radius);
        Assert.Equal(0, outside.Town);
        Assert.Equal(Hand.NoTown, outside.ForeignTown);
        Assert.True(outside.InNoTown);
    }

    /// <summary>
    /// A child of a placed townless building inherits its NO_TOWN association from the parent.
    /// </summary>
    [Fact]
    public void A_placed_child_inherits_its_townless_parent_association()
    {
        using var rig = new Rig();
        rig.Say(new PlaceBuildings([new PlannedBuilding(Shed, ShedAt + new Vector3(10000, 0, 0), Quaternion.Identity)]) { Tick = 0 });
        rig.World.RunTicks(2);
        var parent = rig.World.Buildings.All.Single();

        rig.Say(new PlaceBuildings([new PlannedBuilding(Shed, parent.Position + new Vector3(10, 0, 0), Quaternion.Identity,
            Parent: parent.Id)]) { Tick = 2 });
        rig.World.RunTicks(2);

        var child = rig.World.Buildings.All.Single(site => site.Id != parent.Id);
        Assert.Equal(0, child.Town);
        Assert.Equal(Hand.NoTown, child.ForeignTown);
        Assert.True(child.InNoTown);
    }

    /// <summary>
    /// A batch placement reassesses once, with the last placed building's town (<c>FUN_1404d72a0</c>, docs/game/buildings-production.md
    /// "Outposts"): a shed beside town A and one beside far town C, with A overlapping B, merge nothing, as only C's merge scan runs; a batch whose
    /// last shed is beside A merges A and B.
    /// </summary>
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    public void A_batch_runs_the_merge_scan_once_for_the_last_buildings_town(bool lastBesideA, int towns)
    {
        using var rig = new Rig();
        foreach (var x in new[] { 0f, 2000f, 30000f })
        {
            var town = rig.World.Buildings.AddTown(new Vector3(x, 300, 0), Rig.PlayerFaction);
            town.Override = 1500;
            var member = new BuildSite
            {
                Id = rig.World.Buildings.NewId(new ZoneCoordinate(32, 32)), Record = Hut, Position = new Vector3(x, 300, 0), Town = town.Number,
                Owner = Rig.PlayerFaction, Materials = [], Complete = true, Target = 1, Progress = 1,
            };
            rig.World.Buildings.Add(member);
        }

        float first = lastBesideA ? 30050 : 50, last = lastBesideA ? 50 : 30050;
        rig.Say(new PlaceBuildings([new PlannedBuilding(Shed, new Vector3(first, 300, 40), Quaternion.Identity),
            new PlannedBuilding(Shed, new Vector3(last, 300, 40), Quaternion.Identity)]) { Tick = 0 });
        rig.World.RunTicks(2);

        Assert.Equal(towns, rig.World.Buildings.Towns.Count);
    }

    // ------------------------------------------------------------------ walls

    /// <summary>
    /// A short piece laid at a wall's end attaches to it (<c>FUN_14029ac50</c>): a build order on the short builds the wall, and the wall's
    /// completion completes the short with nothing delivered to it. The link is not saved; reading the save attaches it again. A connector at a
    /// gate's start stays on its own (a gate is not a WallBuilding).
    /// </summary>
    [Fact]
    public void A_short_at_a_wall_end_is_built_by_its_wall_and_reattaches_after_a_save_while_a_gate_takes_no_connector()
    {
        const string playerFactionId = "204-gamedata.base";
        var wallAt = new Vector3(2000, 300, 3000);
        var gateAt = new Vector3(2600, 300, 3000);
        using var rig = new Rig();
        var builder = rig.Add(2000, 2980, Unit, (BuildingBuildData.BuildingMaterialsItem, 2));
        rig.Say(new PlaceBuildings([
            new PlannedBuilding(WallRecord, wallAt, Quaternion.Identity),
                    new PlannedBuilding(ShortRecord, wallAt + new Vector3(0, 0, 100), Quaternion.Identity),
            new PlannedBuilding(GateRecord, gateAt, Quaternion.Identity),
                    new PlannedBuilding(ConnectorRecord, gateAt, Quaternion.Identity)]) { Tick = 0 });
        rig.World.RunTick();
        var sites = rig.World.Buildings.All;
        var wall = sites.Single(s => s.Record == WallRecord);
        var piece = sites.Single(s => s.Record == ShortRecord);

        Assert.Equal(wall.Id, piece.WallParent);
        Assert.Null(sites.Single(s => s.Record == ConnectorRecord).WallParent);

        rig.Say(new BuildOrder([builder], piece.Id) { Tick = rig.World.Tick });
        for (int t = 0; t < 20000 && !wall.Complete; t++) rig.World.RunTick();

        Assert.True(wall.Complete);
        Assert.True(piece.Complete);
        Assert.Equal(piece.Target, piece.Progress);
        Assert.All(piece.Materials, m => Assert.Equal(0, m.Delivered));
        Assert.Equal(0, rig.Carried(builder, BuildingBuildData.BuildingMaterialsItem));

        var save = new SaveBuilder(1);
        save.AddFaction(playerFactionId, "Nameless", true, 1000, []);
        save.AddEmptyWorldState();
        BuildingSave.Write(save.Game, rig.World, playerFactionId, 5);
        string dir = Path.Combine(Path.GetTempPath(), "meitou-wall-" + Guid.NewGuid().ToString("N"));
        try
        {
            save.Game.Write(dir);
            using var back = new Rig();

            Assert.Equal(4, BuildingSave.Read(SaveGame.Load(dir), back.World, back.Db, playerFactionId, Rig.PlayerFaction));

            var read = back.World.Buildings.Find(piece.Id)!;

            Assert.Equal(wall.Id, read.WallParent);
            Assert.True(read.Complete);
            Assert.Null(back.World.Buildings.All.Single(s => s.Record == ConnectorRecord).WallParent);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------ upgrades

    /// <summary>
    /// A finished gate upgraded in place (docs/game/buildings-production.md "Upgrades"): same id, the next record's name and materials with the 55
    /// delivered units carried over, unfinished with progress capped at 50. Downgrading the unfinished upgrade finishes the lower gate at once, its
    /// materials being all delivered. A storage building's upgrade takes what it still owes from its own inventory (5 of the 8), and its machine is
    /// made anew for the next record keeping the rest of the stock.
    /// </summary>
    [Fact]
    public void A_building_upgrades_in_place_taking_owed_materials_from_its_stock_and_a_downgrade_of_the_unfinished_upgrade_finishes_at_once()
    {
        using var rig = new Rig(production: true);
        rig.Say(new PlaceBuildings([new PlannedBuilding(GateMk1, HutAt, Quaternion.Identity)]) { Tick = 0 });
        rig.World.RunTick();
        var gate = Assert.Single(rig.World.Buildings.All);
        foreach (var m in gate.Materials) m.Delivered = m.Required;
        (gate.Progress, gate.Complete) = (gate.Target, true);

        rig.Say(new UpgradeSite(gate.Id) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.Same(gate, Assert.Single(rig.World.Buildings.All));
        Assert.Equal((GateMk2, "Test Gate Mk2", 60f, false, ConstructionSystem.UpgradeProgressCap),
                (gate.Record, gate.Name, gate.Target, gate.Complete, gate.Progress));
        Assert.Equal(55, Assert.Single(gate.Materials).Delivered);
        Assert.True(gate.Upgrading);

        rig.Say(new DowngradeSite(gate.Id) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.Equal((GateMk1, 55f, true, 55f, false), (gate.Record, gate.Target, gate.Complete, gate.Progress, gate.Upgrading));

        const string materials = BuildingBuildData.BuildingMaterialsItem;
        rig.Say(new PlaceBuildings([new PlannedBuilding(StoreMk1, ShedAt, Quaternion.Identity)]) { Tick = rig.World.Tick });
        rig.World.RunTick();
        var store = rig.World.Buildings.All.Single(s => s.Record == StoreMk1);
        foreach (var m in store.Materials) m.Delivered = m.Required;
        (store.Progress, store.Complete) = (store.Target, true);
        rig.World.RunTick();
        var stock = rig.World.Machines.Find(store.Id)!.Storage!;
        for (int i = 0; i < 5; i++) Assert.True(stock.Add(rig.Items.Create(materials, 1)!));

        Assert.True(stock.Add(rig.Items.Create(Plank, 1)!));

        rig.Say(new UpgradeSite(store.Id) { Tick = rig.World.Tick });
        rig.World.RunTicks(2);

        Assert.Equal((StoreMk2, 7f), (store.Record, Assert.Single(store.Materials).Delivered));

        var machine = rig.World.Machines.Find(store.Id)!;

        Assert.Equal(StoreMk2, machine.Data.Id);
        Assert.Equal(Plank, Assert.Single(machine.Storage!.Items).Record);
        Assert.Equal(1, rig.World.Machines.Count);
    }
}

/// <summary>
/// The build data over the installed game's records: the build window's estimated hours and the new game's research. Skipped without the game.
/// </summary>
[Slow]
public class ConstructionInstallTests(ITestOutputHelper output)
{
    [Fact]
    public void Base_game_buildings_give_the_documented_build_hours_and_a_new_game_unlocks_the_default_start_buildings()
    {
        Assert.SkipWhen(InstallData.BaseGame is null, "Kenshi install not found");

        var db = InstallData.BaseGame!;
        var constants = GameConstants.FromDatabase(db);

        Assert.Equal(6f, constants.BuildSpeed);   // CONSTANTS build speed, Verified in the base game

        // hours = materials / (build speed mult x 6 x option 1), the materials the sum of the `construction` first values, else `build materials`.
        var all = BuildingBuildData.LoadAll(db);

        Assert.True(all.Count > 500);

        foreach (var b in all)
        {
            var record = db.Find(b.Id)!;
            var list = record.GetReferences("construction");
            float materials = list.Count > 0 ? list.Sum(r => r.Values.Value0) : record.GetInt("build materials");
            float mult = record.GetFloat("build speed mult", 1);
            if (materials <= 0 || mult <= 0) continue;

            Assert.Equal(materials / (mult * 6), b.EstimatedHours(constants.BuildSpeed), 4);
        }

        // A few known ones, worked out by hand from their records.
        var byName = all.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var (name, hours) in new (string, float)[] { ("Harpoon Turret", 13 / 6f), ("Small Generator", 8 / 9f), ("Hemp Farm L", 30 / 120f),
                ("Well", 5 / 12f) })
        {
            output.WriteLine($"{name}: {byName[name].EstimatedHours(constants.BuildSpeed):F4} h");

            Assert.Equal(hours, byName[name].EstimatedHours(constants.BuildSpeed), 3);
        }

        // The Wanderer start has no research of its own; the default start research unlocks the first buildings (Small Shack, Campfire, Well...).
        var wanderer = NewGameStart.Find(NewGameStart.LoadAll(db), NewGameStart.DefaultName)!;
        var start = ResearchGate.StartResearch(db, wanderer);

        Assert.Equal([ResearchGate.DefaultStartId], start);

        var unlocked = ResearchGate.Unlocked(db, start);
        output.WriteLine("unlocked at the start: " + string.Join(", ", unlocked.Select(id => db.Find(id)!.Name).Order()));

        Assert.Equal(11, unlocked.Count);
        Assert.Contains(byName["Campfire"].Id, unlocked);
    }
}
