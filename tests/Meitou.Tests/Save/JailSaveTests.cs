using Meitou.Content;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Save;

/// <summary>
/// Jail state in a save (docs/simulation.md "Saves", docs/formats/save.md "Locks, cages and sentences"): the keys are Meitou's own, written only
/// while the state is set.
/// </summary>
public class JailSaveTests
{
    /// <summary>
    /// The systems of the jail worlds.
    /// </summary>
    const StandardParts Parts = StandardParts.Population | StandardParts.Player | StandardParts.Movement | StandardParts.Body | StandardParts.Crime;

    /// <summary>
    /// Where the house stands.
    /// </summary>
    static readonly Vector3 HouseAt = SyntheticTown.Centre + new Vector3(0, 0, 100);

    /// <summary>
    /// The house stands in its own zone and is known at run time by its key there (BuildingKey).
    /// </summary>
    static readonly ZoneCoordinate HouseZone = WorldLayout.ZoneOf(HouseAt.X, HouseAt.Z);

    /// <summary>
    /// The house's key in its zone.
    /// </summary>
    static readonly string HouseKey = BuildingKey.Of(HouseZone, "house");

    /// <summary>
    /// The character keys only a jailed, escaped or recognised character writes.
    /// </summary>
    static readonly string[] CharacterKeys = [CrimeSave.OldEscapedKey, CrimeSave.OldRecentlyWantedKey, CrimeSave.RecognitionKey, HeldSave.MeitouState,
            HeldSave.MeitouWhat];

    /// <summary>
    /// The capture options: the synthetic town's placement.
    /// </summary>
    static readonly SaveCaptureOptions Options = new() { Placements = [new TownPlacement("inst", "30-t", SyntheticTown.Centre, null)] };

    /// <summary>
    /// Where the bed of <see cref="JailData"/> stands.
    /// </summary>
    static readonly Vector3 BedAt = HouseAt + new Vector3(-10, 0, 0);

    /// <summary>
    /// The handle the original gives the generated interior cage (any handle the zone's states resolve).
    /// </summary>
    static readonly Hand GeneratedCage = new((int)FcsRecordType.Building, 1238, 11111, 1485, 99);

    /// <summary>
    /// The synthetic town with a house of one door that has a cage in its interior (<c>house/0</c>), and with <paramref name="bed"/> a bed
    /// (<c>house@bed0</c>).
    /// </summary>
    static PopulationData JailData(bool bed = false)
    {
        static FcsRecord Rec(string id, FcsRecordType type, string name) => SyntheticTown.Rec(id, type, name);
        var door = Rec("51-t", FcsRecordType.Building, "Door");
        var house = Rec("52-t", FcsRecordType.Building, "House");
        house.Floats["hardness"] = 40;
        house.References["doors"] = [SyntheticTown.Ref("51-t")];
        var function = Rec("53-t", FcsRecordType.BuildingFunctionality, "Cage");
        function.Ints["function"] = TownBuildings.CageFunction;
        function.Floats["use range"] = 25;
        var cage = Rec("54-t", FcsRecordType.Building, "Prisoner Cage");
        cage.References["functionality"] = [SyntheticTown.Ref("53-t")];
        var sleep = Rec("55-t", FcsRecordType.BuildingFunctionality, "Bed");
        sleep.Ints["function"] = TownBuildings.BedFunction;
        var cot = Rec("56-t", FcsRecordType.Building, "Bed");
        cot.References["functionality"] = [SyntheticTown.Ref("55-t")];
        var db = SyntheticTown.Database(anatomy: true, extra: [door, house, function, cage, sleep, cot]);
        HomeSpot[] spots = bed ? [new HomeSpot(HomeSpotKind.Bed, BedAt, 0) { Record = "56-t" }] : [];
        var home = new TownBuilding("house", "52-t", "house", HouseAt, 0, false, false, spots)
        {
            Zone = HouseZone,
            Fixtures = [new TownFixture($"{HouseKey}/0", "54-t", true, HouseAt + new Vector3(10, 0, 0), 0)],
        };
        return PopulationData.Create(db, [new TownPlacement("inst", "30-t", SyntheticTown.Centre, null)], buildings: new Dictionary<string,
                List<TownBuilding>> { ["inst"] = [home] });
    }

    /// <summary>
    /// A jail world and its data; disposing it disposes the world and its systems.
    /// </summary>
    sealed class Game(PopulationData data, StandardParts parts = Parts) : IDisposable
    {
        /// <summary>
        /// The data the world was made from.
        /// </summary>
        public PopulationData Data { get; } = data;

        /// <summary>
        /// The world.
        /// </summary>
        public SimWorld World { get; } = Make(data, parts);

        /// <summary>
        /// A synthetic town's world with the parts' standard systems.
        /// </summary>
        static SimWorld Make(PopulationData data, StandardParts parts)
        {
            var walk = new OpenGroundWalkability(SyntheticTown.Ground);
            var systems = SyntheticTown.Standard(data, walk, parts, new PopulationSettings { CheckEveryTicks = 1 });
            return new SimWorld(new WorldSettings { Seed = 11, PublishSnapshots = false }, walk, systems.Systems);
        }

        /// <summary>
        /// Disposes the world and then each disposable system.
        /// </summary>
        public void Dispose()
        {
            World.Dispose();
            foreach (var s in World.Systems) (s as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The house's state record with the bools set.
    /// </summary>
    static FcsRecord HouseState(params (string Key, bool Value)[] bools)
    {
        var state = new FcsRecord { Type = (int)FcsRecordType.GamestateBuilding, Id = 0, Name = "0", StringId = "house-S23" };
        foreach (var (key, value) in bools) state.Bools[key] = value;
        return state;
    }

    /// <summary>
    /// A new save's zone has no building list, so no lock state would be written: this adds the house's instance and an empty state record to its
    /// zone, as a real save has them.
    /// </summary>
    static SaveGame Capture(SimWorld world, PopulationData data)
    {
        var builder = SaveCapture.PrepareBase(data, null, Options);
        var zone = WorldLayout.ZoneOf(HouseAt.X, HouseAt.Z);
        if (!builder.Game.Zones.TryGetValue(zone, out var saveZone))
            builder.Game.Zones[zone] = saveZone = new SaveZone(zone, new SaveFile());
        var file = saveZone.File;
        int next = Math.Max(file.Data.NextId, file.Records.Count == 0 ? 0 : file.Records.Max(r => r.Id)) + 1;
        var list = new FcsRecord { Type = (int)FcsRecordType.InstanceCollection, Id = next, Name = "0", StringId = WorldLevelData.BuildingListId };
        var instance = new FcsInstance { Id = "house", Target = "52-t", Position = HouseAt };
        instance.States.Add("house-S23");
        list.Instances.Add(instance);
        var state = HouseState();
        state.Id = next + 1;
        file.Records.Add(list);
        file.Records.Add(state);
        return SaveCapture.Capture(world, data, new SaveClock(3, 7, 45), null, Options, builder);
    }

    /// <summary>
    /// A save as the original writes a member caged in a generated interior cage: the member's state has only the original's keys, <c>in what</c>
    /// naming a zone building-list entry of <paramref name="record"/> at <paramref name="at"/> (an <c>&lt;n&gt;-zoneX.Y-INGAME</c> entry with its own
    /// state), which Meitou's fixture ids do not map.
    /// </summary>
    static SaveGame OriginalCageSave(PopulationData data, string folder, string record, Vector3 at)
    {
        using (var a = new Game(data))
        {
            var squad = a.World.Systems.OfType<PopulationSystem>().Single().StartPlayer(a.World, NewGameStart.From(data.Db.Find("40-t")!));
            a.World.Fixtures.RegisterSite(a.World, data, 0);
            a.World.RunTicks(2);

            Assert.True(a.World.Fixtures.Imprison(a.World, squad.Members[0], a.World.Fixtures.Find($"{HouseKey}/0")!));

            Capture(a.World, data).Write(folder);
        }
        var save = SaveGame.Load(folder);
        AddGeneratedEntry(save, 7, record, at, GeneratedCage);
        OriginalHeld(save, HeldState.Imprisoned, GeneratedCage);
        return save;
    }

    /// <summary>
    /// The house zone's building-list entry <c>&lt;n&gt;-zoneX.Y-INGAME</c> of <paramref name="record"/> at <paramref name="at"/>, with a state of
    /// its own named by <paramref name="handle"/>.
    /// </summary>
    static void AddGeneratedEntry(SaveGame save, int n, string record, Vector3 at, Hand handle)
    {
        var file = save.Zones[HouseZone].File;
        string id = $"{n}-zone{HouseZone.X}.{HouseZone.Y}-INGAME";
        var entry = new FcsInstance { Id = id, Target = record, Position = at };
        entry.States.Add(id + "-S23");
        save.Zones[HouseZone].BuildingList!.Instances.Add(entry);
        var state = new FcsRecord { Type = (int)FcsRecordType.GamestateBuilding, Id = file.Records.Max(r => r.Id) + 1, Name = "0",
                StringId = id + "-S23" };
        handle.Write(state, "handle");
        file.Records.Add(state);
    }

    /// <summary>
    /// The player's first member held as the original writes it: only the original's keys, <c>in what</c> naming <paramref name="handle"/>.
    /// </summary>
    static void OriginalHeld(SaveGame save, HeldState held, Hand handle)
    {
        var state = save.Platoons.Single(p => p.FactionId == save.PlayerFaction!.Id).Characters[0].GameState!;
        state.Ints.Remove(HeldSave.MeitouState);
        state.Strings.Remove(HeldSave.MeitouWhat);
        state.Ints["in something"] = (int)held;
        handle.Write(state, "in what");
    }

    [Fact]
    public void A_prisoner_the_original_saved_in_a_generated_interior_cage_is_seated_in_that_interior_cage_and_keeps_the_original_keys()
    {
        var data = JailData();
        string folder = Path.Combine(Temp("jail-original"), QuickSave.Name);
        try
        {
            var save = OriginalCageSave(data, folder, "54-t", HouseAt + new Vector3(10, 0, 0));
            using var b = new Game(data);
            var loaded = SaveLoader.Load(b.World, save, data);
            b.World.Fixtures.RegisterSite(b.World, data, 0);
            var prisoner = b.World.Squads.Find(b.World.Player.Squad)!.Members[0];
            var cold = b.World.Characters.Cold(prisoner.Slot)!;

            Assert.True(cold.IsImprisoned);
            Assert.Equal($"{HouseKey}/0", cold.InWhat);
            Assert.Equal(prisoner, b.World.Fixtures.Find($"{HouseKey}/0")!.Occupant);

            // Unchanged since the load: written back as the original wrote it, with none of Meitou's keys.
            var second = SaveCapture.Capture(b.World, data, loaded.Clock, loaded, Options);
            var state = second.Platoons.Single(p => p.FactionId == second.PlayerFaction!.Id).Characters[0].GameState!;

            Assert.Equal((int)HeldState.Imprisoned, state.Ints["in something"]);
            Assert.Equal(GeneratedCage, Hand.Read(state, "in what"));
            Assert.DoesNotContain(Keys(state), e => e.Key is HeldSave.MeitouState or HeldSave.MeitouWhat);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    /// <summary>
    /// Every key of the record, its ints, floats, bools and strings, in ordinal key order.
    /// </summary>
    static IEnumerable<KeyValuePair<string, object>> Keys(FcsRecord r) =>
        r.Ints.Select(k => new KeyValuePair<string, object>(k.Key, k.Value))
            .Concat(r.Floats.Select(k => new KeyValuePair<string, object>(k.Key, k.Value)))
            .Concat(r.Bools.Select(k => new KeyValuePair<string, object>(k.Key, k.Value)))
            .Concat(r.Strings.Select(k => new KeyValuePair<string, object>(k.Key, k.Value)))
            .OrderBy(k => k.Key, StringComparer.Ordinal);

    /// <summary>
    /// A member caged with a bounty, another escaped and a locked home: captured, loaded and captured again they keep their keys; a world without
    /// them writes none.
    /// </summary>
    [Fact]
    public void A_caged_character_its_markers_and_a_locked_home_survive_capture_load_capture_and_a_world_without_them_writes_no_such_keys()
    {
        var data = JailData();
        int owner = data.FactionIndex("20-t");
        using var a = new Game(data);
        var population = a.World.Systems.OfType<PopulationSystem>().Single();
        var squadA = population.StartPlayer(a.World, NewGameStart.From(data.Db.Find("40-t")!));

        // The home is locked up (the lock round's flag read from a loaded state, as the round's own is private), its door locked, one member
        // caged in the interior cage with a bounty, another free and escaped.
        a.World.Fixtures.Saved = new Dictionary<string, FcsRecord> { [HouseKey] = HouseState((LockSystem.HouseLockedKey, true)) };
        a.World.Fixtures.RegisterSite(a.World, data, 0);
        a.World.Fixtures.SetDoorsLocked(HouseKey, true);
        a.World.RunTicks(2);
        var cage = a.World.Fixtures.Find($"{HouseKey}/0")!;
        cage.Lock!.Level = 40;
        var caged = squadA.Members[0];
        var cagedCold = a.World.Characters.Cold(caged.Slot)!;
        cagedCold.Bounties = new BountyLedger();
        cagedCold.Bounties.Get(owner, "Testfolk").Amount = 3000;

        Assert.True(a.World.Fixtures.Imprison(a.World, caged, cage));

        var runaway = a.World.Characters.Cold(squadA.Members[1].Slot)!;
        (runaway.Escaped, runaway.RecentlyWanted, runaway.Recognition) = (true, 100, 0.25f);
        var locksA = a.World.Systems.OfType<LockSystem>().Single();

        Assert.True(locksA.IsHouseLocked(a.World, HouseKey));

        var first = Capture(a.World, data);
        string folder = Path.Combine(Temp("jail"), QuickSave.Name);
        try
        {
            first.Write(folder);
            var save = SaveGame.Load(folder);
            var (states, house) = AssertTheSaveHoldsTheJail(save);

            using var b = new Game(data);
            var loaded = SaveLoader.Load(b.World, save, data);
            b.World.Fixtures.RegisterSite(b.World, data, 0);
            AssertTheJailIsLoaded(b.World, cagedCold, owner);
            AssertCapturesOfTheLoadedWorldKeepTheKeys(b.World, data, loaded, states, house);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }

        AssertAWorldWithoutAJailWritesNoSuchKeys(data);
    }

    /// <summary>
    /// The save holds the cage, the escape and the locks (a world without the state holds none); returns the members' and the house's states.
    /// </summary>
    static (List<FcsRecord> States, FcsRecord House) AssertTheSaveHoldsTheJail(SaveGame save)
    {
        var states = save.Platoons.Single(p => p.FactionId == save.PlayerFaction!.Id).Characters.Select(c => c.GameState!).ToList();

        Assert.Equal(2, states[0].Ints[HeldSave.MeitouState]);
        Assert.Equal($"{HouseKey}/0", states[0].Strings[HeldSave.MeitouWhat]);
        Assert.True(states[1].Bools[CrimeSave.EscapedKey]);
        Assert.Equal(100, states[1].Floats[CrimeSave.RecentlyWantedKey]);
        Assert.Equal(0.25f, states[1].Floats[CrimeSave.RecognitionKey]);

        var house = HeldSave.BuildingStates(save)[HouseKey];

        Assert.True(house.Bools[LockSystem.HouseLockedKey]);
        Assert.True(house.Bools["locked0"]);
        Assert.Equal(40 << 2 | 1, house.Ints["meitou lock 0"]);

        return (states, house);
    }

    /// <summary>
    /// The loaded world: the member caged in the locked cage with the bounty and the sentence, the other escaped, the home and its door locked.
    /// </summary>
    static void AssertTheJailIsLoaded(SimWorld world, CharacterCold cagedCold, int owner)
    {
        var squadB = world.Squads.Find(world.Player.Squad)!;
        var cagedB = world.Characters.Cold(squadB.Members[0].Slot)!;
        var cageB = world.Fixtures.Find($"{HouseKey}/0")!;

        Assert.True(cagedB.IsImprisoned);
        Assert.Equal($"{HouseKey}/0", cagedB.InWhat);
        Assert.Equal(squadB.Members[0], cageB.Occupant);
        Assert.True(cageB.Lock!.EffectivelyLocked);
        Assert.Equal(40, cageB.Lock.Level);
        Assert.Equal(3000, cagedB.Bounties!.AmountFor(owner));
        Assert.Equal((Math.Truncate(cagedCold.SentenceStart), cagedCold.SentenceLength), (cagedB.SentenceStart, cagedB.SentenceLength));

        var runawayB = world.Characters.Cold(squadB.Members[1].Slot)!;

        Assert.Equal((true, 100f, 0.25f), (runawayB.Escaped, runawayB.RecentlyWanted, runawayB.Recognition));
        Assert.True(world.Fixtures.AllDoorsLocked(HouseKey));
        Assert.True(world.Systems.OfType<LockSystem>().Single().IsHouseLocked(world, HouseKey));
    }

    /// <summary>
    /// Captured again from the loaded world: the same keys, character by character and for the house; a later capture keeps the bounty's raise
    /// hour.
    /// </summary>
    static void AssertCapturesOfTheLoadedWorldKeepTheKeys(SimWorld world, PopulationData data, LoadedSave loaded, List<FcsRecord> states,
            FcsRecord house)
    {
        var second = SaveCapture.Capture(world, data, loaded.Clock, loaded, Options);
        var again = second.Platoons.Single(p => p.FactionId == second.PlayerFaction!.Id).Characters.Select(c => c.GameState!).ToList();
        for (int k = 0; k < 2; k++)
        {
            Assert.Equal(Keys(states[k]).Where(e => e.Key.StartsWith("meitou ") || e.Key is "sentence" or "psts" or "bountyfac0" or "amount0"),

                Keys(again[k]).Where(e => e.Key.StartsWith("meitou ") || e.Key is "sentence" or "psts" or "bountyfac0" or "amount0"));
        }

        Assert.Equal(Keys(house), Keys(HeldSave.BuildingStates(second)[HouseKey]));

        // A later capture, its clock floored to the minute as the host's is, keeps the bounty's raise hour (each load used to cost an hour).
        world.RunTicks(1000);
        double hours = BountySave.HoursOf(loaded.Clock) + world.Tick * world.TickSeconds / SimWorld.SecondsPerGameHour;
        var later = SaveCapture.Capture(world, data, new SaveClock((int)(hours / 24), (int)(hours % 24), (int)(hours % 1 * 60)), loaded,
                Options);

        Assert.Equal(states[0].Ints["bountyexp0"],
                later.Platoons.Single(p => p.FactionId == later.PlayerFaction!.Id).Characters[0].GameState!.Ints["bountyexp0"]);
    }

    /// <summary>
    /// A world with nothing of the kind writes none of the new keys, though its lock state is written.
    /// </summary>
    static void AssertAWorldWithoutAJailWritesNoSuchKeys(PopulationData data)
    {
        using var plain = new Game(data);
        var squadP = plain.World.Systems.OfType<PopulationSystem>().Single().StartPlayer(plain.World, NewGameStart.From(data.Db.Find("40-t")!));
        plain.World.Fixtures.RegisterSite(plain.World, data, 0);
        plain.World.RunTicks(2);
        var empty = Capture(plain.World, data);
        var plainPlatoon = empty.Platoons.Single(p => p.FactionId == empty.PlayerFaction!.Id);   // the town's own squads are written too

        Assert.Equal(squadP.Members.Count, plainPlatoon.Characters.Count);

        foreach (var c in plainPlatoon.Characters)
        {
            Assert.DoesNotContain(Keys(c.GameState!), e => CharacterKeys.Contains(e.Key));
        }

        var plainHouse = HeldSave.BuildingStates(empty)[HouseKey];

        Assert.False(plainHouse.Bools["locked0"]);
        Assert.DoesNotContain(Keys(plainHouse), e => e.Key is "meitou lock 0" or LockSystem.HouseLockedKey);
    }

    [Fact]
    public void A_character_outside_the_players_faction_opening_a_door_clears_its_homes_locked_flag()
    {
        // Task_OpenDoor (FUN_1403370f0) clears the owner squad's house flag unless the opener is of the player's faction.
        var data = JailData();
        using var a = new Game(data);
        a.World.Fixtures.Saved = new Dictionary<string, FcsRecord> { [HouseKey] = HouseState((LockSystem.HouseLockedKey, true)) };
        a.World.Fixtures.RegisterSite(a.World, data, 0);
        a.World.RunTicks(2);
        var locks = a.World.Systems.OfType<LockSystem>().Single();
        var door = a.World.Fixtures.OfBuilding(HouseKey).First(f => f.Kind == FixtureKind.Door);

        Assert.True(locks.IsHouseLocked(a.World, HouseKey));

        locks.NoteDoorOpened(a.World, door, a.World.Player.Faction);

        Assert.True(locks.IsHouseLocked(a.World, HouseKey));

        locks.NoteDoorOpened(a.World, door, a.World.Player.Faction + 1);

        Assert.False(locks.IsHouseLocked(a.World, HouseKey));
    }
}
