using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Save;

/// <summary>
/// The quicksave of the game host (docs/simulation.md "Saves", "The game host"): a running world is captured, written and read, and a fresh world
/// made from the file holds the same player state. What differs is what the save does not carry (the tick, paths and orders, animation, combat state,
/// the resident population).
/// </summary>
public class QuickSaveTests
{
    /// <summary>
    /// The systems of the synthetic worlds.
    /// </summary>
    const StandardParts Parts = StandardParts.Population | StandardParts.Player | StandardParts.Movement | StandardParts.Body;

    /// <summary>
    /// A synthetic world, its data and the player's squad; disposing it disposes the world and its systems.
    /// </summary>
    sealed record Game(SimWorld World, PopulationData Data, Squad Squad) : IDisposable
    {
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
    /// A synthetic town's world with the standard systems of <see cref="Parts"/>.
    /// </summary>
    static SimWorld NewWorld(PopulationData data, int threads, ulong seed = 11, int minPartition = 4)
    {
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, Parts, new PopulationSettings { CheckEveryTicks = 1 });
        return new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false, MinPartitionSize = minPartition }, walk,
                systems.Systems);
    }

    /// <summary>
    /// A new synthetic world with the player's squad started and the camera on the town.
    /// </summary>
    static Game Start(PopulationData data, int threads = 1)
    {
        var world = NewWorld(data, threads);
        var population = world.Systems.OfType<PopulationSystem>().Single();
        var squad = population.StartPlayer(world, NewGameStart.From(data.Db.Find("40-t")!));
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
        return new Game(world, data, squad);
    }

    /// <summary>
    /// The fields MEDICAL_STATE holds, part by part, equal.
    /// </summary>
    static void SameSavedMedicalState(MedicalState a, MedicalState b)
    {
        Assert.Equal(a.Parts.Count, b.Parts.Count);

        for (int k = 0; k < a.Parts.Count; k++)
        {
            Assert.Equal(a.Parts[k].Template.StringId, b.Parts[k].Template.StringId);
            Assert.Equal((a.Parts[k].Flesh, a.Parts[k].Bandage, a.Parts[k].Rig, a.Parts[k].Stun, a.Parts[k].Wear, a.Parts[k].HitMult,
                    a.Parts[k].HitWeight),

                (b.Parts[k].Flesh, b.Parts[k].Bandage, b.Parts[k].Rig, b.Parts[k].Stun, b.Parts[k].Wear, b.Parts[k].HitMult, b.Parts[k].HitWeight));
        }

        Assert.Equal((a.Blood, a.Bleeding, a.Hunger, a.Fed, a.KoTimer, a.Unconscious, a.Dead),
                (b.Blood, b.Bleeding, b.Hunger, b.Fed, b.KoTimer, b.Unconscious, b.Dead));
    }

    /// <summary>
    /// Captures the game into a quicksave folder under a fresh temporary folder and reads it back.
    /// </summary>
    static (SaveClock Clock, string Folder, SaveGame Save) Quicksave(Game g, string name)
    {
        var clock = new SaveClock(3, 7, 45);
        var save = SaveCapture.Capture(g.World, g.Data, clock, null,
                new SaveCaptureOptions { Placements = [new Meitou.Data.World.TownPlacement("inst", "30-t", SyntheticTown.Centre, null)] });
        string folder = Path.Combine(Temp(name), QuickSave.Name);
        save.Write(folder);
        return (clock, folder, SaveGame.Load(folder));
    }

    /// <summary>
    /// A walked, wounded and trained squad with a changed purse and selection, quicksaved and loaded into a fresh world.
    /// </summary>
    [Fact]
    public void A_running_world_is_quicksaved_and_a_fresh_world_loaded_from_it_holds_the_same_player_state()
    {
        var db = SyntheticTown.Database(anatomy: true);
        var data = SyntheticTown.Data(db);
        using var a = Start(data);

        // The squad walks, two are selected, one is wounded in the left leg, and the money has changed.
        var chosen = a.Squad.Members.Take(2).ToList();
        a.World.Commands.Enqueue(new SelectCommand(chosen) { Tick = 0 });
        a.World.Commands.Enqueue(new MoveOrder([], new Vector3(1200, 300, 2300)) { Tick = 1 });
        a.World.RunTicks(150);
        var wounded = a.Squad.Members[1];
        var woundedCold = a.World.Characters.Cold(wounded.Slot)!;
        woundedCold.Medical!.ApplyHit(1, new HitDamage(60, 10, 0), new MedicalContext(GameConstants.Default, BodyOptions.Default, woundedCold.Race!)
        { Toughness = woundedCold.Stats!.Toughness, Strength = woundedCold.Stats.Strength, Seed = 11, CharacterKey = Rng.Key(wounded) });
        woundedCold.Stats.Gain(StatsEnumerated.Strength, 3.5f);
        a.World.RunTicks(30);
        a.World.Player.Money = 1234;
        a.World.Player.Selection.Remove(a.Squad.Members[0]);

        var (clock, folder, save) = Quicksave(a, "quick");
        try
        {
            AssertTheFileHoldsTheCameraAndThePlatoon(a, data, save);

            using var b = new Game(NewWorld(data, 1), data, null!);
            var loaded = SaveLoader.Load(b.World, save, data);
            var squadB = b.World.Squads.Find(b.World.Player.Squad)!;

            // Relations: the table the save holds is the data's own.
            foreach (var f in loaded.Factions.Where(f => !f.IsPlayer))
                foreach (var r in f.Relations.Values)

                    Assert.Equal(data.Relations.Get(data.Factions.ToList().FindIndex(x => x.Id == f.Id), data.Relations.IndexOf(r.FactionId)),
                            r.Relation);

            Assert.Equal(clock, loaded.Clock);
            Assert.Equal(1234, b.World.Player.Money);
            Assert.Equal(a.Squad.Members.Count, squadB.Members.Count);
            Assert.Equal(a.Squad.TemplateId, squadB.TemplateId);
            Assert.Equal(a.World.Characters.Cold(a.Squad.Leader.Slot)!.Name, b.World.Characters.Cold(squadB.Leader.Slot)!.Name);

            for (int i = 0; i < a.Squad.Members.Count; i++)
                SameLoadedMember(a, b, a.Squad.Members[i], squadB.Members[i]);

            // The selection comes back as saved (CAMERA selected_character / selected_characters<k>).
            Assert.Equal(a.World.Player.Selection.Select(m => a.World.Characters.Cold(m.Slot)!.Name),
                    b.World.Player.Selection.Select(m => b.World.Characters.Cold(m.Slot)!.Name));
            Assert.Single(b.World.Player.Selection);

            // What is not carried, stated exactly: the world starts at tick 0 with no paths, orders or animation, and its state hash is not the
            // original's.
            Assert.Equal(0, b.World.Tick);
            Assert.NotEqual(a.World.StateHash(), b.World.StateHash());
            Assert.All(squadB.Members, m => Assert.Empty(b.World.Characters.Cold(m.Slot)!.OrderQueue));
            Assert.All(squadB.Members, m => Assert.Empty(b.World.Characters.Cold(m.Slot)!.Path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    /// <summary>
    /// What the file holds: the CAMERA, the player's platoon and its counter.
    /// </summary>
    static void AssertTheFileHoldsTheCameraAndThePlatoon(Game a, PopulationData data, SaveGame save)
    {
        Assert.Equal((3, 7, 45), (save.Camera.Day, save.Camera.Hour, save.Camera.Minute));
        Assert.Equal(1234, save.Camera.PlayerMoney);
        Assert.Equal((1, a.Squad.Members.Count), (save.Camera.Squads, save.Camera.Members));
        Assert.Equal(data.Factions.Count, save.Factions.Count);

        var platoon = save.Platoons.Single(p => p.FactionId == save.PlayerFaction!.Id);

        Assert.Equal("Nameless_0", platoon.Name);
        Assert.Equal(a.Squad.Members.Count, platoon.Characters.Count);
        Assert.Equal(1, save.PlayerFaction!.PlatoonCounter);
        Assert.Equal(platoon.Characters[1].Handle, save.Camera.SelectedCharacter);   // the one selected member
    }

    /// <summary>
    /// A member of the saved world and the same member loaded: name, record, role, place, the saved stats and medical state, and what the save
    /// leaves out.
    /// </summary>
    static void SameLoadedMember(Game a, Game b, CharacterId ida, CharacterId idb)
    {
        var ca = a.World.Characters.Cold(ida.Slot)!;
        var cb = b.World.Characters.Cold(idb.Slot)!;
        var ha = a.World.Characters.Previous[ida.Slot];
        var hb = b.World.Characters.Previous[idb.Slot];

        Assert.Equal(ca.Name, cb.Name);
        Assert.Equal(ca.RecordId, cb.RecordId);
        Assert.Equal(ca.Role, cb.Role);
        Assert.Equal(ha.Position, hb.Position);
        Assert.Equal(ha.Yaw, hb.Yaw, 3);

        // The body systems run on the loaded character: race, the saved stats and medical state, inventory and fighter.
        Assert.NotNull(cb.Race);
        Assert.NotNull(cb.Inventory);
        Assert.NotNull(cb.Fighter);
        Assert.True(cb.IsPlayer);
        Assert.Equal(ca.Stats!.ToArray(), cb.Stats!.ToArray());
        Assert.Equal(ca.Medical!.Blood, cb.Medical!.Blood, 3);

        SameSavedMedicalState(ca.Medical!, cb.Medical!);

        // Not in the save's keys (docs/formats/save.md): the wound list and the pain, which the medical tick works out again.
        Assert.Empty(cb.Medical.Wounds);
        Assert.Equal(ha.MaxSpeed, hb.MaxSpeed, 2);
        Assert.Equal(ca.Inventory!.Items.Count, cb.Inventory!.Items.Count);
    }

    /// <summary>
    /// A loaded world hashes the same on one thread and on four, and captures the same player squad again.
    /// </summary>
    [Fact]
    public void A_world_loaded_from_a_quicksave_runs_the_same_whatever_the_thread_count_and_captures_again()
    {
        var db = SyntheticTown.Database(anatomy: true);
        var data = SyntheticTown.Data(db);
        using var a = Start(data);
        a.World.RunTicks(40);
        var (_, folder, save) = Quicksave(a, "threads");
        try
        {
            var hashes = new List<ulong[]>();
            foreach (int threads in new[] { 1, 4 })
            {
                using var b = new Game(NewWorld(data, threads), data, null!);
                var loaded = SaveLoader.Load(b.World, save, data);
                b.World.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
                b.World.Commands.Enqueue(new SelectCommand(b.World.Squads.Find(b.World.Player.Squad)!.Members.ToList()) { Tick = 0 });
                b.World.Commands.Enqueue(new MoveOrder([], new Vector3(1300, 300, 2400)) { Tick = 2 });
                var seen = new List<ulong>();
                for (int i = 0; i < 6; i++)
                {
                    b.World.RunTicks(40);
                    seen.Add(b.World.StateHash());
                }
                hashes.Add([.. seen]);

                // Captured again from the running loaded world: the same player squad, carried on from the file it came from.
                var again = SaveCapture.Capture(b.World, data, loaded.Clock, loaded);

                Assert.Equal(save.Camera.Members, again.Camera.Members);
                Assert.Equal(save.Platoons.Select(p => p.Name), again.Platoons.Select(p => p.Name));
            }

            Assert.Equal(hashes[0], hashes[1]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    // ------------------------------------------------------------------ with the install's data

    /// <summary>
    /// The player's squads after a load: name, member count and the names of the members, in squad order.
    /// </summary>
    static List<(string Name, string[] Members)> SquadsOf(SimWorld world) =>
        [.. SquadManagement.PlayerSquads(world).Select(s => (s.Name, s.Members.Select(m => world.Characters.Cold(m.Slot)!.Name).ToArray()))];

    /// <summary>
    /// A member moved to another squad and an empty renamed squad come back from a save and from a save of the loaded world.
    /// </summary>
    [Fact]
    public void A_member_moved_to_another_squad_and_an_empty_renamed_squad_are_saved_through_two_saves()
    {
        var data = SyntheticTown.Data(SyntheticTown.Database(anatomy: true));
        using var a = Start(data);
        a.World.Commands.Enqueue(new NewSquadCommand(SyntheticTown.Centre) { Tick = 0 });
        a.World.Commands.Enqueue(new NewSquadCommand(SyntheticTown.Centre) { Tick = 0 });
        a.World.RunTicks(2);

        // The first new squad stays empty and is renamed; the second takes the moved member.
        var empty = SquadManagement.PlayerSquads(a.World)[1];
        a.World.Commands.Enqueue(new RenameSquadCommand(empty.Id, "Raiders") { Tick = a.World.Tick });
        var moved = a.Squad.Members[0];   // the leader: the platoon must not follow its first member
        string movedName = a.World.Characters.Cold(moved.Slot)!.Name;
        a.World.Commands.Enqueue(new MoveToSquadCommand(moved, SquadManagement.PlayerSquads(a.World)[2].Id) { Tick = a.World.Tick });
        a.World.RunTicks(1);
        var expected = SquadsOf(a.World);

        Assert.Equal(3, expected.Count);
        Assert.Equal("Raiders", expected[1].Name);
        Assert.Empty(expected[1].Members);
        Assert.Equal([movedName], expected[2].Members);

        var (_, folder, save) = Quicksave(a, "squad-move");
        string again = Path.Combine(Temp("squad-move-2"), QuickSave.Name);
        try
        {
            using var b = new Game(NewWorld(data, 1), data, null!);
            var loaded = SaveLoader.Load(b.World, save, data);

            Assert.Equal(expected, SquadsOf(b.World), (x, y) => x.Name == y.Name && x.Members.SequenceEqual(y.Members));

            // Saved again from the loaded world, each squad keeps its own platoon file.
            var platoons = SquadManagement.PlayerSquads(b.World).Select(s => s.SaveName).ToList();

            Assert.Equal(platoons.Count, platoons.Distinct().Count());

            SaveCapture.Capture(b.World, data, loaded.Clock, loaded,
                    new SaveCaptureOptions { Placements = [new Meitou.Data.World.TownPlacement("inst", "30-t", SyntheticTown.Centre,
                    null)] }).Write(again);
            using var c = new Game(NewWorld(data, 1), data, null!);
            SaveLoader.Load(c.World, SaveGame.Load(again), data);

            Assert.Equal(expected, SquadsOf(c.World), (x, y) => x.Name == y.Name && x.Members.SequenceEqual(y.Members));
            Assert.Equal(platoons, SquadManagement.PlayerSquads(c.World).Select(s => s.SaveName));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
            if (Directory.Exists(Path.GetDirectoryName(again)!))
                Directory.Delete(Path.GetDirectoryName(again)!, recursive: true);
        }
    }

    /// <summary>
    /// What Meitou keeps beyond the original's keys (docs/formats/save.md "NPC platoons", docs/game/ragdolls.md "Saves"): a trader's shop (stock,
    /// purse, fill count, restock) and a knocked-out character's settled ragdoll come back from a save as they were, and a second save writes their
    /// keys again.
    /// </summary>
    [Fact, Slow]
    public void A_shops_stock_and_a_settled_ragdoll_survive_a_save_load_and_second_save()
    {
        var install = GameInstall.Locate();

        Assert.SkipWhen(install is null, "Kenshi install not found");

        var db = InstallData.BaseGame!;
        var levels = InstallData.Levels!;
        var data = PopulationData.Create(db, levels.Towns(), new GeneratedAppearances(db, install!.Root));
        var assets = new Meitou.Data.Ogre.AssetLocator(install);
        var rigs = new Meitou.Data.Characters.CharacterRigs(db, p => assets.Configured.Find(Path.GetFileName(p.Replace('\\', '/'))) ?? assets.Find(p),
                n => assets.Find(n));
        var lengths = AnimationLengths.Load(install.Root, db);
        var walk = new OpenGroundWalkability((_, _) => 300);
        SimWorld Make() => MakeInstallWorld(data, walk, rigs, lengths);

        using var a = Make();
        var squad = a.Systems.OfType<PopulationSystem>().Single().StartPlayer(a,
                NewGameStart.Find(NewGameStart.LoadAll(db), NewGameStart.DefaultName)!);
        a.Player.Faction = data.PlayerFaction;
        a.RunTicks(30);
        var downed = squad.Members[0];
        a.Characters.Cold(downed.Slot)!.Medical!.KnockOut(600);
        for (int t = 0; t < 600 && Ragdolls(a).SaveState(downed) is not { Frozen: true }; t++) a.RunTicks(1);
        var ragdoll = Ragdolls(a).SaveState(downed);

        Assert.True(ragdoll is { Frozen: true }, "the knocked-out character's ragdoll settles");

        var shop = a.Squads.All.First(s => s.Shop is { Stock.Items.Count: > 1 });
        shop.Shop!.Stock.Items.RemoveAt(0);   // a fresh fill would not give this stock

        string root = Temp("meitou-keys");
        try
        {
            var save = SaveCapture.Capture(a, data, new SaveClock(1, 13, 39), null, new SaveCaptureOptions { Placements = levels.Towns().ToList() });
            string folder = QuickSave.Folder(root);
            save.Write(folder);

            using var b = Make();
            var loaded = SaveLoader.Load(b, SaveGame.Load(folder), data, new SaveLoadOptions { Appearances = data.Appearances });
            b.Player.Faction = data.PlayerFaction;

            // The new game's squad keeps its faces, stats and inventory.
            var squadB = b.Squads.Find(b.Player.Squad)!;

            Assert.Equal(squad.Members.Count, squadB.Members.Count);

            for (int i = 0; i < squad.Members.Count; i++)
                SameLook(a, b, squad.Members[i], squadB.Members[i], save, i);
            b.RunTicks(300);   // the shop's town loads and makes its saved platoons
            var again = Ragdolls(b).SaveState(b.Squads.Find(b.Player.Squad)!.Members[0]);

            SameRagdoll(ragdoll, again);

            var shopB = b.Squads.All.Single(s => s.TemplateId == shop.TemplateId && s.Town == shop.Town && s.Shop is not null);

            Assert.Equal(Stock(shop), Stock(shopB));
            Assert.Equal((shop.Money, shop.Shop.Fills), (shopB.Money, shopB.Shop!.Fills));

            // The next restock is the saved game hour.
            float refresh = save.Platoons.Single(p => p.Record.Strings.ContainsKey(ShopSave.StockKey) && p.SquadTemplate == shop.TemplateId
                    && p.Money == shop.Money)
                .Record.Floats[ShopSave.RefreshKey];

            Assert.InRange(b.GameHours + (shopB.Shop.NextRestockTick - b.Tick) * BodySystem.HoursPerTick - refresh, -0.01, 0.01);

            var second = SaveCapture.Capture(b, data, loaded.Clock, loaded, new SaveCaptureOptions { Placements = levels.Towns().ToList() });

            Assert.Contains(second.Platoons, p => p.Record.Strings.ContainsKey(ShopSave.StockKey) && p.SquadTemplate == shop.TemplateId);
            Assert.Contains(second.Characters, c => c.GameState?.Strings.ContainsKey(RagdollSave.Key) == true);

            foreach (var s in b.Systems) (s as IDisposable)?.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            foreach (var s in a.Systems) (s as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// A world of the install's data with trade, bodies, animation and ragdolls, paths found at once.
    /// </summary>
    static SimWorld MakeInstallWorld(PopulationData data, OpenGroundWalkability walk, Meitou.Data.Characters.CharacterRigs rigs,
            AnimationLengths lengths) =>
        new(new WorldSettings { Seed = 5, Threads = 2, PublishSnapshots = false }, walk,
                StandardSystems.Build(data, walk, new StandardSystemOptions
        {
            Parts = StandardParts.Population | StandardParts.Player | StandardParts.Trade | StandardParts.Movement
                | StandardParts.Body | StandardParts.Animation,
            SynchronousPaths = true, Population = new PopulationSettings { CheckEveryTicks = 1 }, Ragdolls = rigs, AnimationLengths = lengths,
        }).Systems);

    /// <summary>
    /// The world's ragdoll system.
    /// </summary>
    static Meitou.Simulation.Ragdolls.RagdollSystem Ragdolls(SimWorld w) => w.Systems.OfType<Meitou.Simulation.Ragdolls.RagdollSystem>().Single();

    /// <summary>
    /// The squad's shop stock: record, quantity and place of each item.
    /// </summary>
    static List<(string, int, int, int)> Stock(Squad s) => [.. s.Shop!.Stock.Items.Select(i => (i.Record, i.Quantity, i.X, i.Y))];

    /// <summary>
    /// A member of the saved world and the same member loaded: the same look (the roll's seed, the saved handle serial, hence the same body,
    /// head, hair and clothes), inventory, stats and medical state.
    /// </summary>
    static void SameLook(SimWorld a, SimWorld b, CharacterId ida, CharacterId idb, SaveGame save, int i)
    {
        var ca = a.Characters.Cold(ida.Slot)!;
        var cb = b.Characters.Cold(idb.Slot)!;

        Assert.Equal(ca.Name, cb.Name);
        Assert.NotNull(ca.Appearance);
        // the look's seed is the saved handle serial
        Assert.Equal(ca.Appearance!.Loadout!.Seed, save.Platoons.Single(p => p.FactionId == save.PlayerFaction!.Id).Characters[i].Handle.S);
        Assert.NotNull(cb.Appearance);

        // The same look: the roll's seed, hence the same body, head, hair and clothes; and so the same inventory.
        Assert.Equal(ca.Appearance.Loadout.Seed, cb.Appearance!.Loadout!.Seed);
        Assert.Equal(ca.Appearance.BodyMesh, cb.Appearance.BodyMesh);
        Assert.Equal(ca.Appearance.Female, cb.Appearance.Female);
        Assert.Equal(ca.Appearance.Parts.Select(p => p.Mesh), cb.Appearance.Parts.Select(p => p.Mesh));
        Assert.Equal(ca.Inventory!.Items.Select(x => x.Record), cb.Inventory!.Items.Select(x => x.Record));
        Assert.Equal(ca.Stats!.ToArray(), cb.Stats!.ToArray());

        SameSavedMedicalState(ca.Medical!, cb.Medical!);
    }

    /// <summary>
    /// The settled ragdoll after the load as it was saved: frozen, age, and each body's place and rotation.
    /// </summary>
    static void SameRagdoll(SavedRagdoll? ragdoll, SavedRagdoll? again)
    {
        Assert.NotNull(again);
        Assert.Equal((ragdoll!.Frozen, ragdoll.Age), (again!.Frozen, again.Age));
        Assert.Equal(ragdoll.Pose.Length, again.Pose.Length);

        for (int i = 0; i < ragdoll.Pose.Length; i++)   // the rotations are normalised again when read
        {
            Assert.Equal(ragdoll.Pose[i].Position, again.Pose[i].Position);
            Assert.True(MathF.Abs(Quaternion.Dot(ragdoll.Pose[i].Rotation, again.Pose[i].Rotation)) > 1 - 1e-5f, $"body {i}");
        }
    }
}
