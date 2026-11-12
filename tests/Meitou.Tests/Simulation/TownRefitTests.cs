using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The refit of a player town after a placement or dismantle (docs/game/buildings-production.md "Outposts"; <c>FUN_1405488f0</c>).
/// </summary>
public class TownRefitTests
{
    const string House = "80-t", Gate = "81-t", Wall = "82-t", Storage = "83-t", Oven = "84-t", OvenFunction = "85-t";
    const int Player = 2;

    static FcsRecord Rec(string id, FcsRecordType type, string name) => new() { StringId = id, Flags = 0x10, Name = name, RecordType = type };

    /// <summary>
    /// A house (an interior), a wall gate, a wall piece, a plain storage and an oven (functionality 4, REFINERY).
    /// </summary>
    static GameDatabase Database()
    {
        var house = Rec(House, FcsRecordType.Building, "House");
        house.References["interior"] = [new FcsReference("90-t", 0, 0, 0)];
        var gate = Rec(Gate, FcsRecordType.Building, "Gate");
        gate.Bools["links together"] = true;
        gate.Bools["is wall gate"] = true;
        var wall = Rec(Wall, FcsRecordType.Building, "Wall");
        wall.Bools["links together"] = true;
        var oven = Rec(Oven, FcsRecordType.Building, "Oven");
        oven.References["functionality"] = [new FcsReference(OvenFunction, 0, 0, 0)];
        var function = Rec(OvenFunction, FcsRecordType.BuildingFunctionality, "Oven function");
        function.Ints["function"] = 4;
        var file = new FcsFile();
        file.Records.AddRange([house, gate, wall, Rec(Storage, FcsRecordType.Building, "Storage"), oven, function]);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return db;
    }

    /// <summary>
    /// A world with the construction system over <see cref="Database"/>, on ground that is walkable where <paramref name="ground"/> is above water.
    /// </summary>
    static SimWorld World(Func<float, float, float>? ground = null)
    {
        var db = Database();
        var walk = new OpenGroundWalkability(ground ?? SyntheticTown.Ground);
        var construction = new ConstructionSystem(db, BaseGame, BodyOptions.Default, new ItemFactory(db, BaseGame), [], "", new ConstructionSettings());
        return new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, walk, [construction]) { Player = { Faction = Player } };
    }

    static BuildSite Add(SimWorld world, string record, float x, float z, int town, string? parent = null)
    {
        var site = new BuildSite
        {
            Id = world.Buildings.NewId(new Meitou.Data.World.ZoneCoordinate(32, 32)), Record = record, Position = new Vector3(x, 300, z), Town = town,
            Owner = Player, Parent = parent, Materials = [], Complete = true, Target = 1, Progress = 1,
        };
        world.Buildings.Add(site);
        return site;
    }

    /// <summary>
    /// The centre is the mean of the houses and wall gates (weight 4 each) and the top-level refinery-function buildings (weight 1); the radius is 200
    /// beyond the farthest building that is not a wall piece (a gate and a storage count, a wall piece does not).
    /// </summary>
    [Fact]
    public void The_centre_weighs_houses_and_gates_and_the_radius_ignores_wall_pieces()
    {
        using var world = World();
        var town = world.Buildings.AddTown(new Vector3(0, 300, 0), Player);
        foreach (var (record, x) in new[] { (House, 0f), (House, 1000f), (Gate, 2000f), (Storage, -3000f), (Wall, 9000f) }) Add(world, record, x, 0, town.Number);

        ConstructionSystem.ReassessTown(world, town);

        Assert.Equal(1000, town.Position.X, 0.5f);
        Assert.Equal(4200, town.Radius, 0.5f);
    }

    /// <summary>
    /// A top-level refinery-function building weighs 1 against a house's 4: (4 x 0 + 1000) / 5.
    /// </summary>
    [Fact]
    public void A_top_level_refinery_building_weighs_one_against_a_houses_four()
    {
        using var world = World();
        var town = world.Buildings.AddTown(new Vector3(0, 300, 0), Player);
        Add(world, House, 0, 0, town.Number);
        Add(world, Oven, 1000, 0, town.Number);

        ConstructionSystem.ReassessTown(world, town);

        Assert.Equal(200, town.Position.X, 0.5f);
    }

    /// <summary>
    /// A centre over unwalkable ground moves to the closest walkable point within 500 (a patch 300 away).
    /// </summary>
    [Fact]
    public void A_centre_off_the_ground_moves_to_the_closest_walkable_point_within_500()
    {
        using var world = World((x, _) => x >= 300 ? 300 : 0);
        var town = world.Buildings.AddTown(new Vector3(0, 300, 0), Player);
        Add(world, House, 0, 0, town.Number);

        ConstructionSystem.ReassessTown(world, town);

        Assert.InRange(town.Position.X, 300, 312);
        Assert.InRange(town.Position.Z, -12, 12);
    }

    /// <summary>
    /// A refit puts the town's height on the terrain at its new position (slot 32 <c>FUN_140933b90</c>), not on the buildings' mean height.
    /// </summary>
    [Fact]
    public void A_refitted_centre_takes_the_terrain_height()
    {
        using var world = World((x, _) => 100 + x / 10);
        var town = world.Buildings.AddTown(new Vector3(0, 300, 0), Player);
        Add(world, House, 1000, 0, town.Number);

        ConstructionSystem.ReassessTown(world, town);

        Assert.Equal(1000, town.Position.X, 0.5f);
        Assert.Equal(200, town.Position.Y, 0.5f);
    }

    /// <summary>
    /// With nothing walkable within 500 the town keeps its old position, and the radius is still refitted.
    /// </summary>
    [Fact]
    public void A_town_with_no_walkable_point_near_its_centre_keeps_its_old_position()
    {
        using var world = World((x, _) => x >= 900 ? 300 : 0);
        var town = world.Buildings.AddTown(new Vector3(50, 300, 50), Player);
        town.Override = 3000;
        Add(world, House, 0, 0, town.Number);

        ConstructionSystem.ReassessTown(world, town);

        Assert.Equal(new Vector3(50, 300, 50), town.Position);
        Assert.Equal(1250, town.Radius);
    }

    /// <summary>
    /// A touched town with no outdoor building (only a piece indoors) changes nothing itself but the reassessment goes on: the merge scan joins the
    /// overlapping neighbour (the larger radius, here the touched town's, survives).
    /// </summary>
    [Fact]
    public void A_touched_town_without_centre_buildings_still_merges_an_overlapping_neighbour()
    {
        using var world = World();
        var neighbour = world.Buildings.AddTown(new Vector3(0, 300, 0), Player);
        var house = Add(world, House, 0, 0, neighbour.Number);
        var touched = world.Buildings.AddTown(new Vector3(2000, 300, 0), Player);
        Add(world, Storage, 2000, 0, touched.Number, parent: "a-house");

        ConstructionSystem.ReassessTown(world, touched);

        Assert.Same(touched, Assert.Single(world.Buildings.Towns));
        Assert.Equal(touched.Number, house.Town);
    }

    /// <summary>
    /// A building whose saved handle names a live town that is no outpost (a level town, or NO_TOWN) is kept there by the original: the nearest outpost
    /// neither counts it in its radius nor adopts it (original Wind Generator II 1099 at Goonrock: the refit radius was the farthest other building
    /// plus 200, 2545.56), while a building with no handle at all still takes the nearest town.
    /// </summary>
    [Fact]
    public void A_building_in_a_live_foreign_town_is_not_the_nearest_towns()
    {
        using var world = World();
        var town = world.Buildings.AddTown(new Vector3(0, 300, 0), Player);
        Add(world, House, 0, 0, town.Number);
        Add(world, Storage, 1500, 0, town.Number);
        var kept = new BuildSite
        {
            Id = world.Buildings.NewId(new Meitou.Data.World.ZoneCoordinate(32, 32)), Record = Storage, Position = new Vector3(2500, 300, 0), Owner = Player,
            Materials = [], Complete = true, Target = 1, Progress = 1, ForeignTown = new Meitou.Data.Save.Hand((int)FcsRecordType.Town, 511, -77, 0, 0), InNoTown = true,
        };
        world.Buildings.Add(kept);

        ConstructionSystem.ReassessTown(world, town);

        Assert.Equal(TownLookup.LevelOwned, TownLookup.KeyOf(world, kept));
        Assert.Null(TownLookup.PlayerTownOf(world, kept));
        Assert.Equal(1700, town.Radius, 0.5f);

        world.Buildings.Remove(kept);
        var orphan = Add(world, Storage, 2500, 0, 0);
        ConstructionSystem.ReassessTown(world, town);

        Assert.Same(town, TownLookup.PlayerTownOf(world, orphan));
        Assert.Equal(2700, town.Radius, 0.5f);
    }

    /// <summary>
    /// A building with no town handle belongs to the nearest town: the refit counts it (centre, radius), the town is not removed for holding only
    /// such buildings, and a dismantle of one reassesses that town.
    /// </summary>
    [Fact]
    public void A_building_without_a_town_belongs_to_the_nearest_town()
    {
        using var world = World();
        var town = world.Buildings.AddTown(new Vector3(0, 300, 0), Player);
        Add(world, House, 0, 0, town.Number);
        var orphan = Add(world, House, 3000, 0, 0);
        var dangling = Add(world, House, 3000, 0, 77);

        ConstructionSystem.ReassessTown(world, town);

        Assert.Same(town, TownLookup.PlayerTownOf(world, orphan));
        Assert.Same(town, TownLookup.PlayerTownOf(world, dangling));
        Assert.Equal(2000, town.Position.X, 0.5f);
        Assert.Equal(2200, town.Radius, 0.5f);

        world.Buildings.Remove(orphan);
        world.Buildings.Remove(dangling);
        var only = Add(world, House, 5000, 0, 0);
        world.Buildings.Remove(world.Buildings.All.First(s => s.Town == town.Number));
        ConstructionSystem.ReassessTown(world, town);

        Assert.Same(town, Assert.Single(world.Buildings.Towns));
        Assert.Equal(5000, town.Position.X, 0.5f);
        Assert.Same(town, TownLookup.PlayerTownOf(world, only));
    }
}
