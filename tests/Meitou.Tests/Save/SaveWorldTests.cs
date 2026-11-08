using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Save;

/// <summary>Saves and worlds: loading a save into a <see cref="SimWorld"/> and capturing a world into a save (docs/simulation.md "Saves").</summary>
public class SaveWorldTests
{
    sealed record Game(SimWorld World, PopulationData Data, Squad Squad) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    /// <summary>A started game on the synthetic town: the player's squad of four, 500 cats.</summary>
    static Game NewGame(ulong seed = 11, bool items = false, GameDatabase? db = null, GameDatabase? level = null)
    {
        db ??= SyntheticTown.Database(items: items);
        var data = SyntheticTown.Data(db, level: level);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Player | StandardParts.Movement, new PopulationSettings { CheckEveryTicks = 1 });
        var population = systems.Population!;
        var world = new SimWorld(new WorldSettings { Seed = seed, PublishSnapshots = false }, walk, systems.Systems);
        var squad = population.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });
        return new Game(world, data, squad);
    }

    static SimWorld EmptyWorld(ulong seed = 11) =>
        new(new WorldSettings { Seed = seed, PublishSnapshots = false }, new OpenGroundWalkability(SyntheticTown.Ground), [new PlayerSystem()]);

    static string Dir(string name) => Temp(name);

    [Fact]
    public void A_dead_player_character_leaves_its_squad_for_the_hidden_dead_squad()
    {
        using var g = NewGame();
        g.World.RunTicks(1);
        var dead = g.Squad.Leader;
        var next = g.Squad.Members[1];
        g.World.Characters.Cold(dead.Slot)!.DiedTick = g.World.Tick;
        g.World.RunTicks(1);

        Assert.DoesNotContain(dead, g.Squad.Members);
        Assert.Equal(next, g.Squad.Leader);
        Assert.Equal((int)SquadRole.Leader, g.World.Characters.Cold(next.Slot)!.Role);
        var graveyard = Assert.Single(g.World.Squads.All, s => s.Dead);
        Assert.Equal([dead], graveyard.Members);
        Assert.Equal(graveyard.Id, g.World.Characters.Cold(dead.Slot)!.SquadId);
        Assert.DoesNotContain(graveyard, SquadManagement.PlayerSquads(g.World));
    }

    [Fact]
    public void An_inventory_changed_in_play_is_saved_and_loaded()
    {
        using var g = NewGame(items: true);
        var cold = g.World.Characters.Cold(g.Squad.Leader.Slot)!;
        var ration = g.Data.Items.Create("70-t", 3)!;
        (ration.X, ration.Y, ration.Charges) = (2, 1, 40);
        cold.Inventory = new Inventory();
        cold.Inventory.Items.Add(ration);
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 12, 0));
        string folder = Path.Combine(Dir("inventory"), "slot");
        try
        {
            save.Write(folder);
            using var world2 = EmptyWorld();
            SaveLoader.Load(world2, SaveGame.Load(folder), g.Data);
            var squad = world2.Squads.Find(world2.Player.Squad)!;
            var item = Assert.Single(world2.Characters.Cold(squad.Leader.Slot)!.Inventory!.Items);
            Assert.Equal(("70-t", "main", 2, 1, 3, 40f), (item.Record, item.Section, item.X, item.Y, item.Quantity, item.Charges));
            Assert.NotNull(item.Saved);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    [Fact]
    public void The_factions_met_are_saved_as_the_known_list_and_an_import_without_relations_leaves_them_out()
    {
        // A mod lists Testfolk in the player faction's relations (known from the setup) and adds the Outsiders, met in play.
        var db = SyntheticTown.Database();
        var nameless = new FcsRecord { StringId = "204-gamedata.base", Flags = 0x11, Name = "Nameless", RecordType = FcsRecordType.FACTION };
        nameless.References["relations"] = [new("20-t", 0, 0, 0)];
        var mod = new FcsFile();
        mod.Records.AddRange([nameless, SyntheticTown.Rec("21-t", FcsRecordType.FACTION, "Outsiders")]);
        db.Apply(mod, "known.mod");
        using var g = NewGame(db: db);
        int folk = g.Data.FactionIndex("20-t"), outsiders = g.Data.FactionIndex("21-t");
        Assert.Equal([folk], g.World.Player.KnownFactions);
        g.World.Player.Meet(outsiders);
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 12, 0));
        Assert.Equal(["20-t", "21-t"], save.PlayerFaction!.Known);
        // A load replaces the set with the list: Testfolk, left out of it, is not known after it. A faction the game data lacks (a mod no longer loaded) stays in the list.
        save.PlayerFaction.Known = ["21-t", "99-gone"];
        string folder = Path.Combine(Dir("known"), "slot");
        try
        {
            save.Write(folder);
            using var world2 = EmptyWorld();
            var loaded = SaveLoader.Load(world2, SaveGame.Load(folder), g.Data);
            Assert.Equal([outsiders], world2.Player.KnownFactions);
            Assert.Equal(["99-gone", "21-t"], SaveCapture.Capture(world2, g.Data, new SaveClock(1, 12, 0), loaded).PlayerFaction!.Known);

            // Without Relations the import starts from the setup's factions, and its next save keeps none of the save's list.
            using var world3 = EmptyWorld();
            var imported = SaveLoader.Load(world3, SaveImport.Filter(SaveGame.Load(folder), ImportOptions.Default & ~ImportOptions.Relations, g.Data), g.Data, new SaveLoadOptions { ResetRelations = true });
            Assert.Equal([folk], world3.Player.KnownFactions);
            Assert.Equal(["20-t"], SaveCapture.Capture(world3, g.Data, new SaveClock(1, 12, 0), imported).PlayerFaction!.Known);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    [Fact]
    public void A_new_game_starts_from_the_level_data_player_faction_state_instead_of_the_setup_and_adds_the_start_off_factions()
    {
        // A mod lists Testfolk in the player faction's relations (the setup's) and the Neighbours in the start-off's faction relations.
        var db = SyntheticTown.Database();
        var nameless = new FcsRecord { StringId = "204-gamedata.base", Flags = 0x11, Name = "Nameless", RecordType = FcsRecordType.FACTION };
        nameless.References["relations"] = [new("20-t", 0, 0, 0)];
        var start = new FcsRecord { StringId = "40-t", Flags = 0x11, Name = "Test start", RecordType = FcsRecordType.NEW_GAME_STARTOFF };
        start.References["faction relations"] = [new("22-t", 50, 0, 0)];
        var mod = new FcsFile();
        mod.Records.AddRange([nameless, start, SyntheticTown.Rec("21-t", FcsRecordType.FACTION, "Outsiders"), SyntheticTown.Rec("22-t", FcsRecordType.FACTION, "Neighbours")]);
        db.Apply(mod, "known.mod");
        using var plain = NewGame(db: db);
        int folk = plain.Data.FactionIndex("20-t"), outsiders = plain.Data.FactionIndex("21-t"), neighbours = plain.Data.FactionIndex("22-t");
        Assert.Equal(new[] { folk, neighbours }.Order(), plain.World.Player.KnownFactions.Order());

        // Its level data holds the player faction's state, which knows the Outsiders, and Testfolk's.
        var player = SyntheticTown.Rec("1-level", FcsRecordType.GAMESTATE_FACTION, "Nameless");
        player.Strings["gamedata stringID"] = "204-gamedata.base";
        player.References["known"] = [new("21-t", 0, 0, 0)];
        var other = SyntheticTown.Rec("2-level", FcsRecordType.GAMESTATE_FACTION, "Testfolk");
        other.Strings["gamedata stringID"] = "20-t";
        other.References["known"] = [new("20-t", 0, 0, 0)];
        var levelFile = new FcsFile();
        levelFile.Records.AddRange([player, other]);
        var level = new GameDatabase();
        level.Apply(levelFile, "leveldata.level");
        using var modded = NewGame(db: db, level: level);
        Assert.Equal(new[] { outsiders, neighbours }.Order(), modded.World.Player.KnownFactions.Order());
    }

    [Fact]
    public void A_player_character_meets_the_factions_in_sight_range_and_an_inspected_one_at_any_distance()
    {
        // A mod adds the Outsiders and the Neighbours, neither known at the start.
        var db = SyntheticTown.Database();
        var mod = new FcsFile();
        mod.Records.AddRange([SyntheticTown.Rec("21-t", FcsRecordType.FACTION, "Outsiders"), SyntheticTown.Rec("22-t", FcsRecordType.FACTION, "Neighbours")]);
        db.Apply(mod, "known.mod");
        using var g = NewGame(db: db);
        int outsiders = g.Data.FactionIndex("21-t"), neighbours = g.Data.FactionIndex("22-t");
        g.World.Commands.Enqueue(new Meitou.Simulation.Editing.FinishCreation { Tick = g.World.Tick });
        g.World.RunTicks(1);
        Assert.DoesNotContain(outsiders, g.World.Player.KnownFactions);
        Assert.DoesNotContain(neighbours, g.World.Player.KnownFactions);

        var population = g.World.Systems.OfType<PopulationSystem>().Single();
        var at = g.World.Characters.Previous[g.Squad.Leader.Slot].Position;
        population.SpawnSandbox(g.World, "2-t", new Vector2(at.X + 300, at.Z), player: false, faction: outsiders);
        var far = population.SpawnSandbox(g.World, "2-t", new Vector2(at.X + 40000, at.Z), player: false, faction: neighbours);
        g.World.RunTicks(2);
        Assert.Contains(outsiders, g.World.Player.KnownFactions);
        Assert.DoesNotContain(neighbours, g.World.Player.KnownFactions);

        g.World.Commands.Enqueue(new InspectCommand(far) { Tick = g.World.Tick });
        g.World.RunTicks(1);
        Assert.Contains(neighbours, g.World.Player.KnownFactions);
    }

    [Fact]
    public void A_save_with_an_empty_known_list_loads_the_setup_factions_and_the_owners_of_the_towns_explored()
    {
        using var g = NewGame();
        g.World.RunTicks(1);
        int home = g.Data.SiteFactions[0];
        Assert.True(home >= 0);
        g.World.Player.Know(0, TownKnowledge.Discovered | TownKnowledge.Explored);
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 12, 0), null, new SaveCaptureOptions { Placements = [new TownPlacement("inst", "30-t", SyntheticTown.Centre, null)] });
        save.PlayerFaction!.Known = [];
        string folder = Path.Combine(Dir("known-empty"), "slot");
        try
        {
            save.Write(folder);
            using var world2 = EmptyWorld();
            SaveLoader.Load(world2, SaveGame.Load(folder), g.Data);
            var expected = g.Data.KnownAtSetup();
            expected.Add(home);
            Assert.Equal(expected.Order(), world2.Player.KnownFactions.Order());
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }
}
