using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// A small invented game database with one town, for the population and movement tests (no install needed).
/// </summary>
static class SyntheticTown
{
    const uint New = 0x10;
    public static readonly Vector3 Centre = new(1000, 300, 2000);

    internal static FcsRecord Rec(string id, FcsRecordType type, string name)
    {
        return new FcsRecord { StringId = id, Flags = New, Name = name, RecordType = type };
    }

    internal static FcsReference Ref(string id, int v0 = 0, int v1 = 0, int v2 = 0) => new(id, v0, v1, v2);

    public static readonly Vector3 Far = new(9500, 300, 2000);

    /// <summary>
    /// <paramref name="roaming"/> adds a second town (<c>31-t</c>, far away), roaming squads and bar squads at the first, and a roaming budget for
    /// the faction;
    /// <paramref name="extra"/> adds records, <paramref name="changePatrol"/> changes the resident <c>Patrol</c> template (<c>10-t</c>) and
    /// <paramref name="changeFolk"/> the
    /// <c>Faction folk</c> one (<c>12-t</c>, the roaming squads') before the database is made.
    /// </summary>
    public static GameDatabase Database(int residentSquads = 3, bool overrideFlag = false, bool roaming = false, bool anatomy = false,
        bool items = false, IReadOnlyList<FcsRecord>? extra = null, Action<FcsRecord>? changePatrol = null, Action<FcsRecord>? changeFolk = null,
        Action<FcsRecord>? changeTown = null)
    {
        var race = Rec("1-t", FcsRecordType.Race, "Testers");
        var parts = new List<FcsRecord>();
        if (anatomy) AddAnatomy(race, parts);
        race.Ints["speed min skill"] = 70;
        race.Ints["speed max skill"] = 120;
        race.Floats["walk speed"] = 15;
        var guard = Rec("2-t", FcsRecordType.Character, "Guard");
        guard.References["race"] = [Ref("1-t", 100)];
        var itemRecords = new List<FcsRecord>();
        if (items) AddRations(guard, itemRecords);
        var boss = Rec("3-t", FcsRecordType.Character, "Boss");
        boss.References["race"] = [Ref("1-t", 100)];
        var dog = Rec("4-t", FcsRecordType.AnimalCharacter, "Dog");
        dog.Floats["animal age random"] = 0.5f;

        var (patrol, gated, bossDead, factionTemplate) = SquadTemplates();
        var faction = Rec("20-t", FcsRecordType.Faction, "Testfolk");
        faction.Ints["default relation"] = 0;
        faction.References["residents"] = [Ref("12-t", 1, 0)];
        var town = Town(residentSquads, overrideFlag);
        FcsRecord? far = roaming ? AddRoaming(faction, town) : null;
        var (nameless, startoff) = StartRecords();

        var file = new FcsFile();
        file.Records.AddRange(parts);
        file.Records.AddRange(itemRecords);
        file.Records.AddRange([race, guard, boss, dog, patrol, gated, bossDead, factionTemplate, faction, town, nameless, startoff]);
        if (far is not null) file.Records.Add(far);
        changePatrol?.Invoke(patrol);
        changeFolk?.Invoke(factionTemplate);
        changeTown?.Invoke(town);
        if (extra is not null) file.Records.AddRange(extra);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return db;
    }

    /// <summary>
    /// <c>anatomy</c>: the race avoids water and gets its combat anatomy parts.
    /// </summary>
    static void AddAnatomy(FcsRecord race, List<FcsRecord> parts)
    {
        race.Floats["water avoidance"] = 3;
        // A chest (vital) and two legs: enough to be hurt, to limp and to be knocked out.
        FcsRecord Part(string id, string name, int type, int collapse, bool vital)
        {
            var p = Rec(id, FcsRecordType.LocationalDamage, name);
            p.Ints["body part type"] = type;
            p.Ints["collapse part"] = collapse;
            p.Bools["death"] = vital;
            p.Bools["collapses"] = true;
            p.Bools["severance"] = !vital;
            return p;
        }
        parts.AddRange([Part("60-t", "Chest", 0, 1, true), Part("61-t", "Left Leg", 1, 0x20, false), Part("62-t", "Right Leg", 1, 0x10, false)]);
        race.References["combat anatomy"] = [Ref("60-t", 140, 100), Ref("61-t", 80, 100), Ref("62-t", 80, 100)];
    }

    /// <summary>
    /// <c>items</c>: a ration (100 charges) and a snack (30), food both; the guard carries two rations.
    /// </summary>
    static void AddRations(FcsRecord guard, List<FcsRecord> itemRecords)
    {
        var ration = Rec("70-t", FcsRecordType.Item, "Ration");
        ration.Ints["item function"] = 3;
        ration.Floats["charges"] = 100;
        ration.Floats["weight kg"] = 1;
        ration.Ints["value"] = 10;
        var snack = Rec("71-t", FcsRecordType.Item, "Snack");
        snack.Ints["item function"] = 3;
        snack.Floats["charges"] = 30;
        snack.Floats["weight kg"] = 0.5f;
        snack.Ints["value"] = 5;
        itemRecords.AddRange([ration, snack]);
        guard.References["inventory"] = [Ref("70-t", 2)];
    }

    /// <summary>
    /// The squad templates: <c>Patrol</c> (the boss leading guards and a dog), <c>Gated</c> (behind the boss's death, which does not hold), that
    /// world state, and <c>Faction folk</c>.
    /// </summary>
    static (FcsRecord Patrol, FcsRecord Gated, FcsRecord BossDead, FcsRecord FactionTemplate) SquadTemplates()
    {
        var patrol = Rec("10-t", FcsRecordType.SquadTemplate, "Patrol");
        patrol.References["leader"] = [Ref("3-t", 1)];
        patrol.References["squad"] = [Ref("2-t", 2, 4)];
        patrol.References["squad2"] = [Ref("2-t", 1, 100)];
        patrol.References["animals"] = [Ref("4-t", 1, 0, 50)];
        var gated = Rec("11-t", FcsRecordType.SquadTemplate, "Gated");
        gated.References["squad"] = [Ref("2-t", 5)];
        gated.References["world state"] = [Ref("90-t", 1)];   // needs a state that does not hold: the boss dead
        var bossDead = Rec("90-t", FcsRecordType.WorldEventState, "Boss dead");
        bossDead.References["NPC is"] = [Ref("3-t", 0)];
        var factionTemplate = Rec("12-t", FcsRecordType.SquadTemplate, "Faction folk");
        factionTemplate.References["squad"] = [Ref("2-t", 3)];
        return (patrol, gated, bossDead, factionTemplate);
    }

    /// <summary>
    /// Testville, the faction's town: <paramref name="residentSquads"/> patrols, a gated squad and a missing template as residents.
    /// </summary>
    static FcsRecord Town(int residentSquads, bool overrideFlag)
    {
        var town = Rec("30-t", FcsRecordType.Town, "Testville");
        town.Ints["type"] = 2;
        town.Floats["size radius"] = 300;
        town.Floats["town radius mult"] = 1;
        town.Bools["residents override"] = overrideFlag;
        town.References["faction"] = [Ref("20-t")];
        town.References["residents"] = [Ref("10-t", residentSquads, 0), Ref("11-t", 1, 0), Ref("13-t", 0, 0)];
        return town;
    }

    /// <summary>
    /// <c>roaming</c>: the faction's roaming budget, the town's roaming and bar squads, and the second town far away (answered).
    /// </summary>
    static FcsRecord AddRoaming(FcsRecord faction, FcsRecord town)
    {
        faction.Ints["roaming population"] = 10;
        town.References["roaming squads"] = [Ref("12-t", 2)];
        town.References["bar squads"] = [Ref("12-t", 2, 100), Ref("10-t", 1, 100)];
        var far = Rec("31-t", FcsRecordType.Town, "Faraway");
        far.Ints["type"] = 2;
        far.Floats["size radius"] = 300;
        far.Floats["town radius mult"] = 1;
        far.References["faction"] = [Ref("20-t")];
        return far;
    }

    /// <summary>
    /// The Nameless faction and the new game start (500 money, the boss and the faction folk, in Testville).
    /// </summary>
    static (FcsRecord Nameless, FcsRecord Startoff) StartRecords()
    {
        var nameless = Rec("204-gamedata.base", FcsRecordType.Faction, "Nameless");
        nameless.Ints["default relation"] = 0;
        var startoff = Rec("40-t", FcsRecordType.NewGameStartoff, "Test start");
        startoff.Ints["money"] = 500;
        startoff.Ints["start pos X"] = 4000;
        startoff.Ints["start pos Z"] = 4000;
        startoff.References["squad"] = [Ref("3-t"), Ref("12-t")];
        startoff.References["town"] = [Ref("30-t")];
        return (nameless, startoff);
    }

    public static PopulationData Data(GameDatabase db, Meitou.Simulation.Bodies.BodyOptions? options = null, GameDatabase? level = null) =>
        PopulationData.Create(db, db.Find("31-t") is null
            ? [new TownPlacement("inst", "30-t", Centre, null)]
            : [new TownPlacement("inst", "30-t", Centre, null), new TownPlacement("inst2", "31-t", Far, null)], bodyOptions: options, level: level);

    public static float Ground(float x, float z) => 300;

    /// <summary>
    /// The game's system order (<see cref="StandardSystems"/>) for the parts a test needs; path requests are answered inside the tick unless asked
    /// otherwise.
    /// </summary>
    public static StandardSystemSet Standard(PopulationData data, IWalkability walk, StandardParts parts, PopulationSettings? population = null,
            bool synchronousPaths = true, float bodyTimeScale = 1, Meitou.Simulation.Items.FeedSettings? feed = null) =>
        StandardSystems.Build(data, walk,
                new StandardSystemOptions { Parts = parts, Population = population, SynchronousPaths = synchronousPaths,
                        BodyTimeScale = bodyTimeScale, Feed = feed });

    public static SimWorld World(ulong seed, int threads, int residentSquads = 3, PopulationSettings? settings = null, bool synchronousPaths = true,
        GameDatabase? db = null, bool bodies = false, float bodyTimeScale = 1, Meitou.Simulation.Items.FeedSettings? feed = null,
        Meitou.Simulation.Bodies.BodyOptions? bodyOptions = null, int minPartition = 16, params ITickSystem[] extra)
    {
        db ??= Database(residentSquads);
        var walk = new OpenGroundWalkability(Ground);
        var data = Data(db, bodyOptions);
        var parts = StandardParts.Population | StandardParts.Movement | (bodies ? StandardParts.Body : 0) | (feed is not null ? StandardParts.Feed
                : 0);
        var systems = Standard(data, walk, parts, settings, synchronousPaths, bodyTimeScale, feed).Systems.Concat(extra);
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false, MinPartitionSize = minPartition },
                walk, systems);
        world.Commands.Enqueue(new FocusCommand(Centre) { Tick = 0 });
        return world;
    }
}
