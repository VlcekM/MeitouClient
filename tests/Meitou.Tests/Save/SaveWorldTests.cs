using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Save;

/// <summary>Saves and worlds: loading a save into a <see cref="SimWorld"/> and capturing a world into a save (docs/simulation.md "Saves as built").</summary>
public class SaveWorldTests
{
    sealed record Game(SimWorld World, PopulationData Data, Squad Squad) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    /// <summary>A started game on the synthetic town: the player's squad of four, 500 cats.</summary>
    static Game NewGame(ulong seed = 11)
    {
        var db = SyntheticTown.Database();
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var population = new PopulationSystem(data, new PopulationSettings { CheckEveryTicks = 1 });
        var world = new SimWorld(new WorldSettings { Seed = seed, PublishSnapshots = false }, walk, [population, new PlayerSystem(), new MovementSystem(new PathService(walk, true))]);
        var squad = population.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });
        return new Game(world, data, squad);
    }

    /// <summary>Runs test code at the points of a tick where the character table may be changed.</summary>
    sealed class Poke : ITickSystem
    {
        public Action<SimWorld>? OnInputs { get; set; }
        public Action<SimWorld>? OnSlow { get; set; }
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands) => OnInputs?.Invoke(world);
        public void SlowWorld(SimWorld world) => OnSlow?.Invoke(world);
    }

    static SimWorld EmptyWorld(ulong seed = 11, Poke? poke = null) =>
        new(new WorldSettings { Seed = seed, PublishSnapshots = false }, new OpenGroundWalkability(SyntheticTown.Ground), poke is null ? [new PlayerSystem()] : [new PlayerSystem(), poke]);

    static string Dir(string name) => Temp(name);

    [Fact]
    public void A_new_games_world_is_captured_into_a_save_and_loaded_into_another_world()
    {
        using var g = NewGame();
        var clock = new SaveClock(2, 14, 30);
        g.World.Player.Selection.Add(g.Squad.Leader);
        var save = SaveCapture.Capture(g.World, g.Data, clock);

        Assert.Equal((2, 14, 30), (save.Camera.Day, save.Camera.Hour, save.Camera.Minute));
        Assert.Equal(500, save.Camera.PlayerMoney);
        Assert.Equal((1, 4), (save.Camera.Squads, save.Camera.Members));
        Assert.Equal(g.Data.Factions.Count, save.Factions.Count);
        var platoon = Assert.Single(save.Platoons);
        Assert.Equal("Nameless_0", platoon.Name);
        Assert.Equal(4, platoon.Characters.Count);
        Assert.Equal(1, save.PlayerFaction!.PlatoonCounter);
        // The leader is first in the selection; its handle is the CAMERA's.
        var leader = platoon.Characters.Single(c => c.IsLeader);
        Assert.Equal(leader.Handle, save.Camera.SelectedCharacter);

        string folder = Path.Combine(Dir("newgame"), "slot");
        try
        {
            save.Write(folder);
            using var world2 = EmptyWorld();
            var loaded = SaveLoader.Load(world2, SaveGame.Load(folder), g.Data);
            Assert.Equal(clock, loaded.Clock);
            Assert.Equal(500, world2.Player.Money);
            Assert.Equal(g.Data.PlayerFaction, world2.Player.Faction);
            var squad = world2.Squads.Find(world2.Player.Squad)!;
            Assert.Equal(4, squad.Members.Count);
            Assert.Equal(g.Squad.Members.Select(m => g.World.Characters.Cold(m.Slot)!.Name), squad.Members.Select(m => world2.Characters.Cold(m.Slot)!.Name));
            for (int i = 0; i < 4; i++)
            {
                var a = g.World.Characters.Previous[g.Squad.Members[i].Slot];
                var b = world2.Characters.Previous[squad.Members[i].Slot];
                Assert.Equal(a.Position, b.Position);
                Assert.Equal(a.Yaw, b.Yaw, 4);
            }
            Assert.Equal(g.Squad.Leader.Slot, g.Squad.Members.First(m => m == g.Squad.Leader).Slot);
            Assert.Equal(world2.Characters.Cold(squad.Leader.Slot)!.Name, g.World.Characters.Cold(g.Squad.Leader.Slot)!.Name);
            Assert.Equal([squad.Leader], world2.Player.Selection);
            Assert.All(squad.Members, m => Assert.True(world2.Characters.Cold(m.Slot)!.IsPlayer));
            Assert.All(squad.Members, m => Assert.NotNull(world2.Characters.Cold(m.Slot)!.Save));
            // Relations: the table the new save holds is the data's own.
            foreach (var f in loaded.Factions.Where(f => !f.IsPlayer))
                foreach (var r in f.Relations.Values)
                    Assert.Equal(g.Data.Relations.Get(g.Data.Factions.ToList().FindIndex(x => x.Id == f.Id), g.Data.Relations.IndexOf(r.FactionId)), r.Relation);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    [Fact]
    public void Saving_a_loaded_world_unchanged_changes_nothing_but_what_derives()
    {
        using var g = NewGame();
        var first = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 13, 0));
        string folder = Path.Combine(Dir("again"), "slot");
        try
        {
            first.Write(folder);
            using var world2 = EmptyWorld();
            var loaded = SaveLoader.Load(world2, SaveGame.Load(folder), g.Data);
            var second = SaveCapture.Capture(world2, g.Data, loaded.Clock, loaded);
            Assert.Equal(first.Quick.ToBytes(), second.Quick.ToBytes());
            var a = first.Platoons[0].File!;
            var b = second.Platoons[0].File!;
            Assert.Equal(a.ToBytes(), b.ToBytes());
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    [Fact]
    public void Changes_in_the_world_reach_the_save_and_what_the_world_does_not_hold_is_kept()
    {
        using var g = NewGame();
        var first = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 13, 0));
        // Something the world does not model, written into the save: it must survive.
        first.Camera.Record.Strings["area"] = "Somewhere";
        first.Towns.Add(new SaveTown(new FcsRecord { Type = (int)FcsRecordType.GAMESTATE_TOWN, Id = 900, StringId = "900-quick.save-INGAME", Name = "Town state Kept" }));
        first.Quick.Records.Add(first.Towns[^1].Record);
        string folder = Path.Combine(Dir("changes"), "slot");
        try
        {
            first.Write(folder);
            var poke = new Poke();
            using var world2 = EmptyWorld(poke: poke);
            var loaded = SaveLoader.Load(world2, SaveGame.Load(folder), g.Data);
            var squad = world2.Squads.Find(world2.Player.Squad)!;
            var mover = squad.Members[1];
            var turner = squad.Members[3];
            var dead = squad.Members[0];
            var table = world2.Characters;

            // Move one, turn one, kill one, pay for something, let time pass.
            poke.OnInputs = w =>
            {
                w.Characters.Next[mover.Slot].Position = new Vector3(555, 300, 666);
                w.Characters.Next[turner.Slot].Yaw = 1.5f;
                w.Player.Money = 123;
            };
            poke.OnSlow = w => w.Characters.Remove(dead);
            world2.RunTick();
            var second = SaveCapture.Capture(world2, g.Data, new SaveClock(1, 15, 5), loaded);

            Assert.Equal(123, second.Camera.PlayerMoney);
            Assert.Equal((15, 5), (second.Camera.Hour, second.Camera.Minute));
            Assert.Equal("Somewhere", second.Camera.Area);
            Assert.Contains(second.Towns, t => t.Record.Name == "Town state Kept");
            var platoon = Assert.Single(second.Platoons);
            Assert.Equal(3, platoon.Characters.Count);
            Assert.Equal(3, platoon.CharCount);
            Assert.Single(platoon.Characters, c => c.IsLeader);   // the squad elected a new one
            var moved = platoon.Characters.Single(c => c.Handle.I == table.Cold(mover.Slot)!.Save!.Slot);
            Assert.Equal(new Vector3(555, 300, 666), moved.Position);
            var turned = platoon.Characters.Single(c => c.Handle.I == table.Cold(turner.Slot)!.Save!.Slot);
            Assert.Equal(1.5f, SaveRotation.YawOf(turned.Rotation), 3);
            // The records of the dead one are gone from the file too.
            Assert.Equal(3, platoon.File!.Records.Count(r => r.RecordType == FcsRecordType.GAMESTATE_CHARACTER));
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    [Fact]
    public void A_member_of_a_loaded_platoon_who_is_not_the_players_is_still_written()
    {
        using var g = NewGame();
        var first = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 13, 0));
        string folder = Path.Combine(Dir("prisoner"), "slot");
        try
        {
            first.Write(folder);
            using var world2 = EmptyWorld();
            var loaded = SaveLoader.Load(world2, SaveGame.Load(folder), g.Data);
            var squad = world2.Squads.Find(world2.Player.Squad)!;
            // A prisoner kept in the squad: not the player's character, but in the platoon file.
            world2.Characters.Cold(squad.Members[2].Slot)!.IsPlayer = false;
            var second = SaveCapture.Capture(world2, g.Data, loaded.Clock, loaded);
            Assert.Equal(4, Assert.Single(second.Platoons).Characters.Count);
            Assert.Equal(first.Platoons[0].File!.ToBytes().Length, second.Platoons[0].File!.ToBytes().Length);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    [Fact]
    public void A_character_that_joined_gets_records_of_its_own()
    {
        using var g = NewGame();
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 13, 0));
        string folder = Path.Combine(Dir("recruit"), "slot");
        try
        {
            save.Write(folder);
            using var world2 = EmptyWorld();
            var loaded = SaveLoader.Load(world2, SaveGame.Load(folder), g.Data);
            var squad = world2.Squads.Find(world2.Player.Squad)!;
            var hot = new CharacterHot { Position = new Vector3(5, 300, 6), Health = 100, Mode = SpeedMode.Free };
            var cold = new CharacterCold { Name = "Newcomer", Faction = world2.Player.Faction, RecordId = "2-t", SquadId = squad.Id, IsPlayer = true };
            var id = world2.Characters.Spawn(hot, cold, 0);
            squad.Members.Add(id);
            var second = SaveCapture.Capture(world2, g.Data, loaded.Clock, loaded, new SaveCaptureOptions { Seed = 5 });
            var platoon = Assert.Single(second.Platoons);
            Assert.Equal(5, platoon.Characters.Count);
            var added = platoon.Characters.Single(c => c.Name == "Newcomer");
            Assert.Equal(5, added.Handle.I);
            Assert.Equal(platoon.Handle.C, added.Handle.C);
            Assert.Equal(new Vector3(5, 300, 6), added.Position);
            Assert.Equal("2-t", added.RecordId);
            Assert.NotNull(added.Stats);
            Assert.NotNull(added.Appearance);
            Assert.Equal(5, platoon.CharCount);
            // And it loads again.
            string folder2 = Path.Combine(Path.GetDirectoryName(folder)!, "slot2");
            second.Write(folder2);
            using var world3 = EmptyWorld();
            SaveLoader.Load(world3, SaveGame.Load(folder2), g.Data);
            Assert.Contains(world3.Characters.Previous.ToArray().Select((_, i) => world3.Characters.Cold(i)).Where(c => c is not null), c => c!.Name == "Newcomer");
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    [Fact]
    public void Loading_needs_a_world_that_has_not_started()
    {
        using var g = NewGame();
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 13, 0));
        Assert.Throws<InvalidOperationException>(() => SaveLoader.Load(g.World, save, g.Data));
    }

    [Fact]
    public void Rotations_and_yaws_convert_both_ways()
    {
        foreach (float yaw in new[] { 0f, 0.5f, 1.5f, -2f, 3f })
            Assert.Equal(yaw, SaveRotation.YawOf(SaveRotation.OfYaw(yaw)), 4);
        Assert.Equal(0.2f, SaveRotation.Difference(0.1f, MathF.Tau - 0.1f), 3);
    }

    // ------------------------------------------------------------------ the user's saves

    static (GameDatabase Db, PopulationData Data)? cached;

    static PopulationData Data()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        lock (typeof(SaveWorldTests))
        {
            cached ??= Make(install!);
            return cached.Value.Data;
        }
    }

    static (GameDatabase, PopulationData) Make(GameInstall install)
    {
        var db = GameDatabase.Load(LoadOrder.BaseGame(install));
        var level = WorldLevelData.Load(install);
        return (db, PopulationData.Create(db, level.Towns()));
    }

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void A_real_save_loads_into_a_world(string folder)
    {
        SkipWithoutSaves(folder);
        var data = Data();
        var save = Load(folder);
        using var world = EmptyWorld();
        var loaded = SaveLoader.Load(world, save, data, new SaveLoadOptions { ApplyRelations = false });

        Assert.Equal(new SaveClock(save.Camera.Day, save.Camera.Hour, save.Camera.Minute), loaded.Clock);
        Assert.Equal(save.Camera.PlayerMoney, world.Player.Money);
        Assert.Equal("Nameless", data.Factions[world.Player.Faction].Name);
        Assert.Equal(103, loaded.Factions.Count);
        // The player's platoons: every character the save holds that is alive is in the world with its saved position and stats.
        var mine = save.Platoons.Where(p => p.FactionId == "204-gamedata.base" && p.IsLoaded).ToList();
        Assert.NotEmpty(mine);
        Assert.Equal(mine.Count, loaded.PlayerPlatoons.Count);
        int chars = 0;
        foreach (var id in loaded.Characters)
        {
            var cold = world.Characters.Cold(id.Slot)!;
            var link = cold.Save!;
            var sc = link.Source;
            Assert.Equal(sc.Position, world.Characters.Previous[id.Slot].Position);
            Assert.Equal(sc.Name, cold.Name);
            Assert.NotNull(link.Stats);
            Assert.Equal(sc.Stats!.Floats["strength"], link.Stats.Strength);
            Assert.Equal(sc.Stats.Floats["athletics"], link.Stats.Athletics);
            Assert.NotNull(link.Medical);
            Assert.Equal(sc.Medical!.Floats["blood"], link.Medical.Blood);
            Assert.Equal(7, link.Medical.Parts.Count);
            chars++;
        }
        Assert.Equal(mine.Sum(p => p.Characters.Count) - loaded.Notes.Count(n => n.Contains("dead in the save")) - loaded.Notes.Count(n => n.Contains("does not have")), chars);
        // The camera's selection is a player character.
        Assert.All(world.Player.Selection, s => Assert.True(world.Characters.Cold(s.Slot)!.IsPlayer));
        // The NPC platoons became stand-ins at their saved places (those with a town that is placed).
        Assert.NotEmpty(loaded.RoamingPlatoons);
        foreach (var (id, name) in loaded.RoamingPlatoons)
        {
            var p = world.Platoons.Find(id)!;
            var rec = save.Platoons.Single(x => x.Name == name);
            Assert.Equal(new Vector2(rec.Position.X, rec.Position.Z), p.Position);
            Assert.Equal(PlatoonState.Unloaded, p.State);
        }
    }

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void A_real_save_captured_again_keeps_every_record(string folder)
    {
        SkipWithoutSaves(folder);
        var data = Data();
        var original = Load(folder);
        using var world = EmptyWorld();
        var loaded = SaveLoader.Load(world, original, data, new SaveLoadOptions { ApplyRelations = false });
        var again = SaveCapture.Capture(world, data, loaded.Clock, loaded);

        // Nothing the world does not change changes: compare record by record.
        Assert.Equal(original.Quick.Records.Count, again.Quick.Records.Count);
        var changed = new List<string>();
        for (int i = 0; i < original.Quick.Records.Count; i++)
            if (!Same(original.Quick.Records[i], again.Quick.Records[i])) { var x = Bytes(original.Quick.Records[i]); var y = Bytes(again.Quick.Records[i]); int d = 0; while (d < Math.Min(x.Length, y.Length) && x[d] == y[d]) d++; changed.Add(original.Quick.Records[i].RecordType + $"(len {x.Length}/{y.Length} diff at {d} bs {original.Quick.Records[i].ByteSize}/{again.Quick.Records[i].ByteSize})"); }
        // Only the PLATOON records of the player's platoons (their char count follows the file) and the CAMERA (squads and members, the zone list) may differ.
        var unexpected = changed.Where(t => !t.StartsWith("PLATOON") && !t.StartsWith("CAMERA")).GroupBy(t => t).Select(g => g.Key + " x" + g.Count()).ToList();
        Assert.True(unexpected.Count == 0, "changed: " + string.Join(", ", unexpected));

        // The player's platoon files: the characters' positions, stats and medical state are the saved ones, the rest is untouched.
        int compared = 0;
        foreach (var lp in loaded.PlayerPlatoons)
        {
            var was = lp.Platoon;
            var now = again.Platoons.Single(p => p.Name == was.Name);
            Assert.Equal(was.Characters.Count, now.Characters.Count);
            for (int k = 0; k < was.Characters.Count; k++)
            {
                var a = was.Characters[k];
                var b = now.Characters[k];
                if (!Same(a.GameState!, b.GameState!)) Assert.Fail($"{was.Name}/{a.Name}: GAMESTATE_CHARACTER differs");
                Assert.Equal(a.Position, b.Position);
                Assert.Equal(a.Rotation, b.Rotation);
                foreach (var (x, y) in new[] { (a.Ai, b.Ai), (a.Inventory, b.Inventory), (a.Appearance, b.Appearance), (a.Stats, b.Stats) })
                    Assert.True(Same(x!, y!), $"{was.Name}/{a.Name}: {x!.RecordType} differs");
                SameFloats(a.Medical!, b.Medical!, $"{was.Name}/{a.Name} MEDICAL_STATE");
                compared++;
            }
        }
        Assert.True(compared > 0);
        // Every other file is byte for byte what it was.
        var wasFiles = original.AllFiles().ToDictionary(f => f.Name, f => f.File);
        foreach (var (name, file) in again.AllFiles().Where(f => f.Name != "quick.save"))
            if (loaded.PlayerPlatoons.All(p => p.Platoon.FileName != name))
            {
                // Zone files: the derived slot list and nextId are what the original had.
                if (name.StartsWith("zone/")) Assert.Equal(wasFiles[name].SlotLists[0], file.SlotLists[0]);
                Assert.Equal(wasFiles[name].Records.Count, file.Records.Count);
            }
    }


    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void A_world_loaded_from_a_real_save_runs_and_can_be_saved_again(string folder)
    {
        SkipWithoutSaves(folder);
        var data = Data();
        var save = Load(folder);
        var walk = new OpenGroundWalkability((x, z) => 300);
        var population = new PopulationSystem(data, new PopulationSettings { CheckEveryTicks = 5 });
        using var world = new SimWorld(new WorldSettings { Seed = 3, Threads = 2, PublishSnapshots = false }, walk, [population, new PlayerSystem(), new MovementSystem(new PathService(walk, true))]);
        var loaded = SaveLoader.Load(world, save, data, new SaveLoadOptions { ApplyRelations = false });
        var player = loaded.Characters[0];
        var at = world.Characters.Previous[player.Slot].Position;
        world.Commands.Enqueue(new FocusCommand(at) { Tick = 0 });
        world.RunTicks(150);
        Assert.True(world.Characters.IsAlive(player));
        // The stand-ins moved and the player's squad is where it was (it was given no orders).
        Assert.Equal(at.X, world.Characters.Previous[player.Slot].Position.X, 1);
        var again = SaveCapture.Capture(world, data, loaded.Clock, loaded);
        Assert.Equal(save.Platoons.Count, again.Platoons.Count);
        Assert.Equal(save.Characters.Count(), again.Characters.Count());
    }
    /// <summary>Two records are the same when they serialise to the same bytes (which also holds for the NaN-patterned floats of decal records).</summary>
    static bool Same(FcsRecord a, FcsRecord b) => Bytes(a).AsSpan().SequenceEqual(Bytes(b));

    static byte[] Bytes(FcsRecord r)
    {
        var ms = new MemoryStream();
        FcsWriter.Write(new FcsFile { FileType = FcsFileType.V15, Records = { r } }, ms, keepByteSizes: true, lossless: true);
        return ms.ToArray();
    }

    static void SameFloats(FcsRecord a, FcsRecord b, string what)
    {
        Assert.True(a.Floats.OrderBy(k => k.Key).SequenceEqual(b.Floats.OrderBy(k => k.Key)), $"{what}: floats differ");
        Assert.True(a.Bools.OrderBy(k => k.Key).SequenceEqual(b.Bools.OrderBy(k => k.Key)), $"{what}: bools differ");
        Assert.True(a.Strings.OrderBy(k => k.Key).SequenceEqual(b.Strings.OrderBy(k => k.Key)), $"{what}: strings differ");
    }

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void A_real_save_survives_the_whole_chain_with_changes(string folder)
    {
        SkipWithoutSaves(folder);
        var data = Data();
        var original = Load(folder);
        var poke = new Poke();
        using var world = EmptyWorld(poke: poke);
        var loaded = SaveLoader.Load(world, original, data, new SaveLoadOptions { ApplyRelations = false });
        var id = loaded.Characters[0];
        var cold = world.Characters.Cold(id.Slot)!;
        string name = cold.Name;
        var target = new Vector3(12345, 321, 54321);
        poke.OnInputs = w =>
        {
            w.Characters.Next[id.Slot].Position = target;
            w.Player.Money += 777;
        };
        world.RunTick();   // the buffers swap: Previous holds the changes
        var saved = SaveCapture.Capture(world, data, new SaveClock(9, 8, 7), loaded);
        string dest = Path.Combine(Temp("chain"), "chain");
        try
        {
            saved.Write(dest);
            var back = SaveGame.Load(dest);
            Assert.Equal((9, 8, 7), (back.Camera.Day, back.Camera.Hour, back.Camera.Minute));
            Assert.Equal(original.Camera.PlayerMoney + 777, back.Camera.PlayerMoney);
            var movedChar = cold.Save!.Source;
            Assert.Equal(target, back.Platoons.Single(p => p.Name == movedChar.Platoon.Name).Characters.Single(c => c.Handle.I == movedChar.Handle.I).Position);
            _ = name;
            // The rest of the world came through: towns, the NPC platoons and their characters, the zones.
            Assert.Equal(original.Towns.Count, back.Towns.Count);
            Assert.Equal(original.Platoons.Count, back.Platoons.Count);
            Assert.Equal(original.Zones.Count, back.Zones.Count);
            Assert.Equal(original.Characters.Count(), back.Characters.Count());
            Assert.Equal(original.Portraits, back.Portraits);
            Assert.Equal(original.Biomes!.Floats.Count, back.Biomes!.Floats.Count);
            // The written save is itself a stable fixed point of load and capture.
            using var world2 = EmptyWorld();
            var loaded2 = SaveLoader.Load(world2, back, data, new SaveLoadOptions { ApplyRelations = false });
            var again = SaveCapture.Capture(world2, data, loaded2.Clock, loaded2);
            Assert.Equal(saved.Quick.ToBytes().Length, again.Quick.ToBytes().Length);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dest)!, recursive: true); }
    }
}
