using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Save;

/// <summary>A town override's reset of its zone's buildings (docs/game/factions-squads-towns.md "Town overrides"), live and through a save.</summary>
public class TownOverrideSaveTests
{
    static readonly ZoneCoordinate Zone = WorldLayout.ZoneOf(SyntheticTown.Centre.X, SyntheticTown.Centre.Z);
    static readonly Vector3 HouseAt = SyntheticTown.Centre + new Vector3(0, 0, 50), ShedAt = SyntheticTown.Centre + new Vector3(50, 0, 0);
    static readonly string HouseKey = BuildingKey.Of(Zone, "house"), ShedKey = BuildingKey.Of(Zone, "shed");
    static readonly TownPlacement[] Placements = [new TownPlacement("inst", "30-t", SyntheticTown.Centre, null)];
    static readonly SaveCaptureOptions Options = new() { Placements = Placements };

    /// <summary>The override town with a house of one door and a shed with no fixtures, both in the town's zone; B takes over when 91 and 92 hold.</summary>
    static PopulationData Data(IWorldStates states)
    {
        var door = SyntheticTown.Rec("60-t", FcsRecordType.Building, "Door");
        var house = SyntheticTown.Rec("61-t", FcsRecordType.Building, "House");
        house.References["doors"] = [SyntheticTown.Ref("60-t")];
        var shed = SyntheticTown.Rec("62-t", FcsRecordType.Building, "Shed");
        var db = PopulationTests.OverrideDb(extra: [door, house, shed]);
        List<TownBuilding> buildings =
        [
            new TownBuilding("house", "61-t", "house", HouseAt, 0, false, false, []) { Zone = Zone },
            new TownBuilding("shed", "62-t", "shed", ShedAt, 0, false, false, []) { Zone = Zone },
        ];
        var made = PopulationData.Create(db, Placements, buildings: new Dictionary<string, List<TownBuilding>> { ["inst"] = buildings });
        return PopulationTests.WithStates(made, states);
    }

    static SimWorld World(PopulationData data)
    {
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        return new SimWorld(new WorldSettings { Seed = 5, PublishSnapshots = false }, walk,
            SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Movement, new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 1 }).Systems);
    }

    static void Focus(SimWorld world, Vector3 at, int ticks)
    {
        world.Commands.Enqueue(new FocusCommand(at) { Tick = world.Tick });
        world.RunTicks(ticks);
    }

    /// <summary>A state as an old save holds it: the house's door locked at 77, both buildings the old faction's, the shed destroyed.</summary>
    static FcsRecord Stale(string stringId, bool house)
    {
        var state = new FcsRecord { Type = (int)FcsRecordType.GamestateBuilding, Name = "0", StringId = stringId };
        state.Strings["owner faction ID"] = "20-t";
        if (house) (state.Ints["lock lvl0"], state.Bools["locked0"]) = (77, true);
        else state.Bools["destroyed"] = true;
        return state;
    }

    [Fact]
    public void A_replaced_town_resets_its_zone_buildings_to_the_level_and_a_save_keeps_them_so()
    {
        var states = new PopulationTests.FixedStates();
        var data = Data(states);
        int others = data.FactionIndex("21-t");
        using var a = World(data);
        a.Fixtures.Saved = new Dictionary<string, FcsRecord> { [HouseKey] = Stale("house-S23", house: true), [ShedKey] = Stale("shed-S23", house: false) };
        // First visit: no override qualifies; the house takes its saved state.
        Focus(a, SyntheticTown.Centre, 2);
        var door = a.Fixtures.Find(HouseKey + "#0")!;
        Assert.Equal((77, true, data.FactionIndex("20-t")), (door.Lock!.Level, door.Lock.Locked, door.Owner));

        // B takes over at the next load: the house is the level's again (a fresh roll, unlocked), the new faction's.
        Focus(a, new Vector3(-50000, 0, -50000), 180);   // the block's 4 s, then the 1 s grace
        states.Holding.UnionWith(["91-t", "92-t"]);
        Focus(a, SyntheticTown.Centre, 2);
        Assert.Equal("51-t", data.Sites[0].Replacement?.Id);
        door = a.Fixtures.Find(HouseKey + "#0")!;
        Assert.Equal((false, others), (door.Lock!.Locked, door.Owner));
        Assert.NotEqual(77, door.Lock.Level);

        // A save made over the old records writes the reset into them, the shed's too, which has no fixture to write.
        var builder = SaveCapture.PrepareBase(data, null, Options);
        if (!builder.Game.Zones.TryGetValue(Zone, out var saveZone)) builder.Game.Zones[Zone] = saveZone = new SaveZone(Zone, new SaveFile());
        var file = saveZone.File;
        int next = Math.Max(file.Data.NextId, file.Records.Count == 0 ? 0 : file.Records.Max(r => r.Id)) + 1;
        var list = new FcsRecord { Type = (int)FcsRecordType.InstanceCollection, Id = next, Name = "0", StringId = WorldLevelData.BuildingListId };
        foreach (var (id, target, at) in new[] { ("house", "61-t", HouseAt), ("shed", "62-t", ShedAt) })
        {
            var instance = new FcsInstance { Id = id, Target = target, Position = at };
            instance.States.Add(id + "-S23");
            list.Instances.Add(instance);
            var state = Stale(id + "-S23", id == "house");
            state.Id = ++next;
            file.Records.Add(state);
        }
        file.Records.Add(list);
        var save = SaveCapture.Capture(a, data, new SaveClock(3, 7, 45), null, Options, builder);
        var written = HeldSave.BuildingStates(save);
        Assert.Equal(("21-t", false, false), (written[HouseKey].Strings["owner faction ID"], written[HouseKey].Bools["locked0"], written[HouseKey].Ints["lock lvl0"] == 77));
        Assert.Equal(("21-t", false), (written[ShedKey].Strings["owner faction ID"], written[ShedKey].Bools["destroyed"]));

        // Loaded: B is back in place and the house is the new faction's.
        string folder = Path.Combine(Temp("override"), QuickSave.Name);
        try
        {
            save.Write(folder);
            data.ClearReplacements();
            using var b = World(data);
            SaveLoader.Load(b, SaveGame.Load(folder), data);
            Assert.Equal("51-t", data.Sites[0].Replacement?.Id);
            b.Fixtures.RegisterSite(b, data, 0);
            Assert.Equal(others, b.Fixtures.Find(HouseKey + "#0")!.Owner);
            Assert.Equal("21-t", b.Fixtures.Saved![ShedKey].Strings["owner faction ID"]);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    /// <summary>
    /// Five houses with a door and a destroyed mesh, the last two in the next zone; B's default resident is a ruined template (docs/game/buildings-production.md
    /// "Ruined by residents"), its one resident entry takes a house.
    /// </summary>
    static (PopulationData Data, List<TownBuilding> Houses) RuinData(IWorldStates states)
    {
        var door = SyntheticTown.Rec("60-t", FcsRecordType.Building, "Door");
        var house = SyntheticTown.Rec("61-t", FcsRecordType.Building, "House");
        house.References["doors"] = [SyntheticTown.Ref("60-t")];
        house.Filenames["destroyed mesh"] = "ruin.mesh";
        var ruined = SyntheticTown.Rec("70-t", FcsRecordType.SquadTemplate, "Ruined");
        ruined.Bools["building ruined"] = true;
        var b = new FcsRecord { StringId = "51-t", Flags = 0x11, Name = "B (override)", RecordType = FcsRecordType.Town };
        b.References["default resident"] = [SyntheticTown.Ref("70-t")];
        var db = PopulationTests.OverrideDb(extra: [door, house, ruined, b]);
        var next = new ZoneCoordinate(Zone.X + 1, Zone.Y);
        var houses = Enumerable.Range(0, 5).Select(i => new TownBuilding($"h{i}", "61-t", $"h{i}", SyntheticTown.Centre + new Vector3(40 * i, 0, 50), 0, false, false, [])
            { Zone = i >= 3 ? next : Zone, CanHouse = true }).ToList();
        var made = PopulationData.Create(db, Placements, buildings: new Dictionary<string, List<TownBuilding>> { ["inst"] = houses });
        return (PopulationTests.WithStates(made, states), houses);
    }

    [Fact]
    public void A_replaced_towns_ruined_residents_ruin_their_homes_in_and_out_of_its_zone_and_a_save_keeps_them()
    {
        var states = new PopulationTests.FixedStates();
        var (data, houses) = RuinData(states);
        using var a = World(data);
        Focus(a, SyntheticTown.Centre, 2);
        Assert.Empty(a.Fixtures.Ruins);

        // B takes over: every house but the one its resident entry takes is a ruin, without its door, in the next zone too.
        Focus(a, new Vector3(-50000, 0, -50000), 180);
        states.Holding.UnionWith(["91-t", "92-t"]);
        Focus(a, SyntheticTown.Centre, 2);
        Assert.Equal(4, a.Fixtures.Ruins.Count);
        Assert.Contains(a.Fixtures.Ruins, r => r.Building.Zone != Zone);
        foreach (var h in houses) Assert.Equal(!a.Fixtures.IsDestroyed(h), a.Fixtures.Find(h.Key + "#0") is not null);

        // Saved and loaded: the same ruins.
        var options = new SaveCaptureOptions { Placements = Placements };
        var save = SaveCapture.Capture(a, data, new SaveClock(3, 7, 45), null, options, SaveCapture.PrepareBase(data, null, options));
        string folder = Path.Combine(Temp("override-ruins"), QuickSave.Name);
        try
        {
            save.Write(folder);
            data.ClearReplacements();
            using var b = World(data);
            SaveLoader.Load(b, SaveGame.Load(folder), data);
            Assert.Equal(a.Fixtures.Ruins.Select(r => r.Building.Key).Order(), b.Fixtures.Ruins.Select(r => r.Building.Key).Order());
            b.Fixtures.RegisterSite(b, data, 0);
            foreach (var h in houses) Assert.Equal(!b.Fixtures.IsDestroyed(h), b.Fixtures.Find(h.Key + "#0") is not null);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }
}
