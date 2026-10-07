using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>A small invented game database with one town, for the population and movement tests (no install needed).</summary>
static class SyntheticTown
{
    const uint New = 0x10;
    public static readonly Vector3 Centre = new(1000, 300, 2000);

    static FcsRecord Rec(string id, FcsRecordType type, string name)
    {
        return new FcsRecord { StringId = id, Flags = New, Name = name, RecordType = type };
    }

    static FcsReference Ref(string id, int v0 = 0, int v1 = 0, int v2 = 0) => new(id, v0, v1, v2);

    public static readonly Vector3 Far = new(9500, 300, 2000);

    /// <summary><paramref name="roaming"/> adds a second town (<c>31-t</c>, far away), roaming squads and bar squads at the first, and a roaming budget for the faction.</summary>
    public static GameDatabase Database(int residentSquads = 3, bool overrideFlag = false, bool roaming = false)
    {
        var race = Rec("1-t", FcsRecordType.RACE, "Testers");
        race.Ints["speed min skill"] = 70;
        race.Ints["speed max skill"] = 120;
        race.Floats["walk speed"] = 15;
        var guard = Rec("2-t", FcsRecordType.CHARACTER, "Guard");
        guard.References["race"] = [Ref("1-t", 100)];
        var boss = Rec("3-t", FcsRecordType.CHARACTER, "Boss");
        boss.References["race"] = [Ref("1-t", 100)];
        var dog = Rec("4-t", FcsRecordType.ANIMAL_CHARACTER, "Dog");
        dog.Floats["animal age random"] = 0.5f;

        var patrol = Rec("10-t", FcsRecordType.SQUAD_TEMPLATE, "Patrol");
        patrol.References["leader"] = [Ref("3-t", 1)];
        patrol.References["squad"] = [Ref("2-t", 2, 4)];
        patrol.References["squad2"] = [Ref("2-t", 1, 100)];
        patrol.References["animals"] = [Ref("4-t", 1, 0, 50)];
        var gated = Rec("11-t", FcsRecordType.SQUAD_TEMPLATE, "Gated");
        gated.References["squad"] = [Ref("2-t", 5)];
        gated.References["world state"] = [Ref("90-t")];
        var factionTemplate = Rec("12-t", FcsRecordType.SQUAD_TEMPLATE, "Faction folk");
        factionTemplate.References["squad"] = [Ref("2-t", 3)];

        var faction = Rec("20-t", FcsRecordType.FACTION, "Testfolk");
        faction.Ints["default relation"] = 0;
        faction.References["residents"] = [Ref("12-t", 1, 0)];
        var town = Rec("30-t", FcsRecordType.TOWN, "Testville");
        town.Ints["type"] = 2;
        town.Floats["size radius"] = 300;
        town.Floats["town radius mult"] = 1;
        town.Bools["residents override"] = overrideFlag;
        town.References["faction"] = [Ref("20-t")];
        town.References["residents"] = [Ref("10-t", residentSquads, 0), Ref("11-t", 1, 0), Ref("13-t", 0, 0)];

        FcsRecord? far = null;
        if (roaming)
        {
            faction.Ints["roaming population"] = 10;
            town.References["roaming squads"] = [Ref("12-t", 2)];
            town.References["bar squads"] = [Ref("12-t", 2, 100), Ref("10-t", 1, 100)];
            far = Rec("31-t", FcsRecordType.TOWN, "Faraway");
            far.Ints["type"] = 2;
            far.Floats["size radius"] = 300;
            far.Floats["town radius mult"] = 1;
            far.References["faction"] = [Ref("20-t")];
        }

        var nameless = Rec("204-gamedata.base", FcsRecordType.FACTION, "Nameless");
        nameless.Ints["default relation"] = 0;
        var startoff = Rec("40-t", FcsRecordType.NEW_GAME_STARTOFF, "Test start");
        startoff.Ints["money"] = 500;
        startoff.Ints["start pos X"] = 4000;
        startoff.Ints["start pos Z"] = 4000;
        startoff.References["squad"] = [Ref("3-t"), Ref("12-t")];
        startoff.References["town"] = [Ref("30-t")];

        var file = new FcsFile();
        file.Records.AddRange([race, guard, boss, dog, patrol, gated, factionTemplate, faction, town, nameless, startoff]);
        if (far is not null) file.Records.Add(far);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return db;
    }

    public static PopulationData Data(GameDatabase db) =>
        PopulationData.Create(db, db.Find("31-t") is null
            ? [new TownPlacement("inst", "30-t", Centre, null)]
            : [new TownPlacement("inst", "30-t", Centre, null), new TownPlacement("inst2", "31-t", Far, null)]);

    public static float Ground(float x, float z) => 300;

    public static SimWorld World(ulong seed, int threads, int residentSquads = 3, PopulationSettings? settings = null, bool synchronousPaths = true,
        GameDatabase? db = null)
    {
        db ??= Database(residentSquads);
        var walk = new OpenGroundWalkability(Ground);
        var population = new PopulationSystem(Data(db), settings);
        var movement = new MovementSystem(new PathService(walk, synchronousPaths));
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false }, walk, [population, movement]);
        world.Commands.Enqueue(new FocusCommand(Centre) { Tick = 0 });
        return world;
    }
}
