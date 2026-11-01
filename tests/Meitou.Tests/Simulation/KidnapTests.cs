using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Items;
using Meitou.Simulation.Kidnap;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Saving;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Raiders kidnapping the downed (docs/simulation.md "Kidnapping"; the finder in docs/game/carry-loot.md 3.2).</summary>
public class KidnapTests
{
    static readonly Vector3 Centre = new(2000, 150, 2000);
    static readonly RaceData Person = new() { StringId = "17-c", Name = "Greenlander", StatMultipliers = Human.StatMultipliers, Anatomy = Human.Anatomy, Carriable = true };

    /// <summary>A package whose every role list holds goal 254 (find, kidnap and put in a cage), always in its hours.</summary>
    static AiPackage Kidnapping()
    {
        var goal = new AiGoal(new AiTask("t", "kidnap", TaskType.FINAL_KIDNAPPER_CAGE_JOB, TaskPriority.Urgent, TaskTargetType.Specific, TaskEndEvent.Nothing), 0, 100, 1);
        return new AiPackage { Id = "kidnap", Name = "kidnap", Roles = [[goal], [goal], [goal], [], []] };
    }

    static CharacterId Add(SimWorld world, CharacterCold cold, Vector2 at, Squad squad)
    {
        cold.Race = Person;
        cold.SquadId = squad.Id;
        var id = world.Characters.Spawn(new CharacterHot { Position = Centre + new Vector3(at.X, 0, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15 }, cold, 0);
        squad.Members.Add(id);
        if (squad.Leader.IsNone) squad.Leader = id;
        return id;
    }

    /// <summary>The jail town of the arrest tests with a cannibal-like squad whose package has goal 254 beside a knocked-out player character: lift, carry, cage; pinned in <see cref="GoldenHashTests"/>.</summary>
    internal static List<ulong> Scripted(int threads)
    {
        var data = ArrestTests.Town();
        var carry = new CarrySystem(BaseGame, BodyOptions.Default, 1) { Relations = data.Relations };
        using var population = new PopulationSystem(data);
        var kidnap = new KidnapSystem(data, population, carry, new KidnapSettings { RecordLog = true });
        var walk = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 31, Threads = threads, PublishSnapshots = false, MinPartitionSize = 1 }, walk,
            [new PlayerSystem(), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), carry, kidnap]);
        world.Fixtures.RegisterSite(world, data, 0);
        int city = data.FactionIndex("50-a");

        var board = new Blackboard([new(0, Kidnapping())]);
        board.Update();
        var raiders = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = city, Town = 0, Ai = board, HomeCentre = new Vector2(Centre.X, Centre.Z), HomeRadius = 500 };
        world.Squads.Add(raiders);
        var raider = new Combatant { Name = "raider" }.MakeCold();
        raider.Faction = city;
        Add(world, raider, new Vector2(0, 0), raiders);

        var players = new Squad { Id = world.Squads.NextId(), TemplateId = "p", Faction = data.Relations.PlayerFaction };
        world.Squads.Add(players);
        var hero = new Combatant { Name = "hero" }.MakeCold();
        hero.Faction = data.Relations.PlayerFaction;
        hero.IsPlayer = true;
        var victim = Add(world, hero, new Vector2(60, 0), players);
        hero.Medical!.KnockOut(10000);

        var hashes = new List<ulong>();
        foreach (int target in new[] { 1, 60, 300, 900, 1800 })
        {
            world.RunTicks(target - (int)world.Tick);
            hashes.Add(world.StateHash());
        }
        var cold = world.Characters.Cold(victim.Slot)!;
        var seen = (Caged: cold.IsImprisoned && cold.InWhat is "0.0:cageA" or "0.0:cageB", cold.Kidnapped,
            Logged: kidnap.Log.Any(e => e.Kind == KidnapEventKind.Lifted && e.Victim == victim) && kidnap.Log.Any(e => e.Kind == KidnapEventKind.Caged && e.Victim == victim));
        // It covers what it is for: the downed player character was lifted, carried to a cage of the raiders' town and held there as kidnapped.
        Assert.True(seen.Caged && seen.Kidnapped && seen.Logged, $"{seen}");
        return hashes;
    }

    [Fact]
    public void A_hungry_bandit_loots_only_food_from_a_downed_player_character_after_8_seconds_and_never_loots_it_again()
    {
        var data = ArrestTests.Town();
        var carry = new CarrySystem(BaseGame, BodyOptions.Default, 1) { Relations = data.Relations };
        using var population = new PopulationSystem(data);
        var walk = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 7, PublishSnapshots = false }, walk,
            [new PlayerSystem(), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), carry, population, new Meitou.Simulation.Dialogue.DialogueSystem(data)]);
        int city = data.FactionIndex("50-a");
        // Goal 177 in every role list, always in its hours.
        var goal = new AiGoal(new AiTask("t", "loot", TaskType.LOOT_THE_DEAD, TaskPriority.Urgent, TaskTargetType.Specific, TaskEndEvent.Nothing), 0, 100, 1);
        var board = new Blackboard([new(0, new AiPackage { Id = "loot", Name = "loot", Roles = [[goal], [goal], [goal], [], []] })]);
        board.Update();
        var bandits = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = city, Ai = board };
        world.Squads.Add(bandits);
        var bandit = new Combatant { Name = "bandit" }.MakeCold();
        var pack = new Inventory();
        (bandit.Faction, bandit.NpcClass, bandit.Inventory, bandit.Ai) = (city, FundamentalType.Bandit, pack, new AiMind { Squad = bandits });
        pack.Items.Add(new ItemInstance { Record = "sabre", Type = FcsRecordType.WEAPON, Section = "hip" });
        var looter = Add(world, bandit, Vector2.Zero, bandits);

        var players = new Squad { Id = world.Squads.NextId(), TemplateId = "p", Faction = data.Relations.PlayerFaction };
        world.Squads.Add(players);
        var hero = new Combatant { Name = "hero" }.MakeCold();
        var pockets = new Inventory();
        (hero.Faction, hero.IsPlayer, hero.Inventory) = (data.Relations.PlayerFaction, true, pockets);
        var bread = new ItemInstance { Record = "bread", Type = FcsRecordType.ITEM, ItemFunction = 3 };
        var wrench = new ItemInstance { Record = "wrench", Type = FcsRecordType.ITEM };
        var katana = new ItemInstance { Record = "katana", Type = FcsRecordType.WEAPON, Section = "hip" };
        Assert.True(pockets.Add(bread) && pockets.Add(wrench));
        pockets.Items.Add(katana);
        var victim = Add(world, hero, new Vector2(30, 0), players);
        hero.Medical!.KnockOut(10000);

        // The walk and 8 s at the body: nothing taken while it kneels.
        world.RunTicks(30 * 7);
        Assert.Equal(CharacterTask.Loot, (CharacterTask)world.Characters.Next[looter.Slot].Task);
        Assert.Contains(bread, pockets.Items);
        world.RunTicks(30 * 6);
        Assert.Equal([bread], pack.Items.Where(i => i.Record == "bread"));
        Assert.Contains(wrench, pockets.Items);
        Assert.Contains(katana, pockets.Items);   // the bandit has its own weapon on
        Assert.True(world.SystemOf<Meitou.Simulation.Dialogue.DialogueSystem>()!.Memory.HasShort(world, bandits.Id, victim, Meitou.Simulation.Dialogue.MemoryTag.Looted));
        // Tagged: it is not looted again.
        world.RunTicks(30 * 20);
        Assert.Single(carry.Loots, e => e.Victim == victim);
        Assert.NotEqual(CharacterTask.Loot, (CharacterTask)world.Characters.Next[looter.Slot].Task);
    }

    [Fact]
    public void A_kidnapped_prisoner_serves_no_sentence_gets_no_bail_and_commits_no_escape_and_the_finder_skips_the_wrong_targets()
    {
        var data = ArrestTests.Town();
        var carry = new CarrySystem(BaseGame, BodyOptions.Default, 1);
        using var population = new PopulationSystem(data);
        var kidnap = new KidnapSystem(data, population, carry);
        var crime = new CrimeSystem(data.Relations);
        var arrest = new ArrestSystem(crime, BaseGame);
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 150));
        world.Fixtures.RegisterSite(world, data, 0);
        int city = data.FactionIndex("50-a"), player = data.Relations.PlayerFaction;
        var squad = new Squad { Id = 1, TemplateId = "t", Faction = player };
        world.Squads.Add(squad);
        CharacterId Down(int faction, Action<CharacterCold>? tweak = null)
        {
            var cold = new Combatant().MakeCold();
            cold.Faction = faction;
            tweak?.Invoke(cold);
            var id = Add(world, cold, new Vector2(world.Characters.HighWater * 10, 0), squad);
            cold.Medical!.KnockOut(10000);
            return id;
        }

        // The finder: a down stranger is a victim; the raiders' own, an animal, one already caged and (once their faction is allied to it) the stranger's faction are not.
        var stranger = Down(player);
        var own = Down(city);
        var animal = Down(player, c => c.Fighter = new Fighter(null) { IsAnimal = true });
        var caged = Down(player);
        Assert.True(world.Fixtures.Imprison(world, caged, world.Fixtures.Find("0.0:cageA")!));
        Assert.True(kidnap.IsVictim(world, stranger.Slot, city));
        Assert.False(kidnap.IsVictim(world, own.Slot, city));
        Assert.False(kidnap.IsVictim(world, animal.Slot, city));
        Assert.False(kidnap.IsVictim(world, caged.Slot, city));
        data.Relations.Set(city, player, FactionRelations.AllyThreshold);
        Assert.False(kidnap.IsVictim(world, stranger.Slot, city));
        data.Relations.Set(city, player, 0);
        Assert.False(kidnap.IsVictim(world, caged.Slot, city));

        // A kidnapped prisoner: its sentence is never served, no bail is offered, getting out is no escape. The same prisoner with an unserved sentence does escape.
        var cold = world.Characters.Cold(caged.Slot)!;
        var cage = world.Fixtures.Find("0.0:cageA")!;
        cold.SentenceStart = world.GameHours;
        cold.SentenceLength = 0;
        Assert.True(FixtureRegistry.SentenceServed(cold, world.GameHours));
        cold.Kidnapped = true;
        Assert.False(FixtureRegistry.SentenceServed(cold, world.GameHours + 1e6));
        Assert.Null(arrest.BailFor(world, cold));
        cage.Lock!.Locked = false;
        Assert.True(world.Fixtures.GetOut(world, caged, crime));
        Assert.False(cold.Escaped);
        Assert.False(cold.Kidnapped);   // free again

        var other = Down(player);
        var oc = world.Characters.Cold(other.Slot)!;
        Assert.True(world.Fixtures.Imprison(world, other, cage));
        Assert.NotNull(arrest.BailFor(world, oc));
        oc.SentenceStart = world.GameHours;
        oc.SentenceLength = 100;
        cage.Lock!.Locked = false;
        Assert.True(world.Fixtures.GetOut(world, other, crime));
        Assert.True(oc.Escaped);
    }

    [Fact]
    public void A_slave_serves_no_sentence_escapes_as_an_escaped_slave_is_reenslaved_by_its_owners_cage_stays_one_through_a_release_and_the_save_keeps_it()
    {
        var data = ArrestTests.Town();
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 150));
        world.Fixtures.RegisterSite(world, data, 0);
        int city = data.FactionIndex("50-a");
        var squad = new Squad { Id = 1, TemplateId = "t", Faction = data.Relations.PlayerFaction };
        world.Squads.Add(squad);
        var cold = new Combatant().MakeCold();
        cold.Faction = data.Relations.PlayerFaction;
        var slave = Add(world, cold, Vector2.Zero, squad);
        var cage = world.Fixtures.Find("0.0:cageA")!;

        FixtureRegistry.Enslave(world, cold, city);
        Assert.Equal((1, city), (cold.SlaveState, cold.SlaveOwner));
        Assert.True(world.Fixtures.Imprison(world, slave, cage));
        Assert.False(FixtureRegistry.SentenceServed(cold, world.GameHours + 1e6));

        // Getting out: an escaped slave (state 2), recognised at once.
        cage.Lock!.Locked = false;
        Assert.True(world.Fixtures.GetOut(world, slave));
        Assert.Equal((2, city), (cold.SlaveState, cold.SlaveOwner));
        Assert.Equal(CrimeSystem.RecentlyWantedSeconds, cold.RecentlyWanted);

        // The save keeps state and owner.
        var record = SyntheticTown.Rec("1-s", FcsRecordType.CHARACTER, "state");
        SlaveSave.Write(record, cold, data);
        var loaded = new Combatant().MakeCold();
        SlaveSave.Read(record, loaded, data);
        Assert.Equal((2, city), (loaded.SlaveState, loaded.SlaveOwner));

        // Caged again by an owner: a slave again; a release leaves it one (FUN_1403570a0 never sets the slave state), bail's clear frees it (FUN_1406ae170).
        Assert.True(world.Fixtures.Imprison(world, slave, cage));
        Assert.Equal((1, city), (cold.SlaveState, cold.SlaveOwner));
        Assert.True(world.Fixtures.Release(world, slave));
        Assert.Equal((1, city), (cold.SlaveState, cold.SlaveOwner));
        FixtureRegistry.ClearSlave(world, cold);
        Assert.Equal((0, -1), (cold.SlaveState, cold.SlaveOwner));
        SlaveSave.Write(record, cold, data);
        Assert.False(record.Strings.ContainsKey(SlaveSave.OwnerKey));
    }
}

/// <summary>The Skin Bandits' small raid against an outpost of the installed game's data. Skipped without the game.</summary>
[Slow]
public class KidnapInstallTests(ITestOutputHelper output)
{
    [Fact]
    public void A_cannibal_raid_lifts_downed_player_characters_and_cages_them_and_a_save_keeps_them_kidnapped()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var install = InstallData.Install!;
        var db = InstallData.FullLoadOrder!;
        var levels = WorldLevelData.LoadBaseGame(install, includeInteriors: true);
        using var map = TerrainHeightmap.Open(install);
        var buildings = TownBuildings.Build(db, levels, new BuildingLayouts(db, levels.Interiors), (x, z) => map.HeightAt((float)x, (float)z));
        var data = PopulationData.Create(db, levels.Towns(), new GeneratedAppearances(db, install.Root), buildings: buildings);
        var walk = new OpenGroundWalkability((_, _) => 300);
        SimWorld NewWorld(PopulationSettings settings, out StandardSystemSet set)
        {
            set = StandardSystems.Build(data, walk, new StandardSystemOptions
            {
                Parts = StandardParts.Population | StandardParts.Player | StandardParts.Pursuit | StandardParts.Movement | StandardParts.Body | StandardParts.Combat | StandardParts.Retaliation
                    | StandardParts.FirstAid | StandardParts.Aggro | StandardParts.Flee | StandardParts.Carry | StandardParts.Crime,
                SynchronousPaths = true,
                Population = settings,
                Kidnap = new KidnapSettings { RecordLog = true },
            });
            return new SimWorld(new WorldSettings { Seed = 11, Threads = 2, PublishSnapshots = false }, walk, set.Systems);
        }

        using var world = NewWorld(new PopulationSettings { RaidFrequencyMult = 1000 }, out var set);
        var population = set.Population!;
        world.Player.Faction = data.PlayerFaction;
        int faction = Enumerable.Range(0, data.Factions.Count).First(f => data.Factions[f].Name == "Skin Bandits");
        Assert.Contains(data.Factions[faction].Campaigns, l => population.CampaignOf(l.Id)?.Name == "skin bandit standard raid small");
        // A town of theirs with cages, and an outpost 1500 east of it with four player characters.
        TownSite home = null!;
        foreach (var t in data.Sites.Where(s => data.SiteFactions[s.Index] == faction && s.Town.Type is not (TownType.NestMarker or TownType.Null)))
        {
            world.Fixtures.RegisterSite(world, data, t.Index);
            if (world.Fixtures.All.Any(x => x.Kind == FixtureKind.Cage && x.Site == t.Index)) { home = t; break; }
        }
        Assert.NotNull(home);
        var outpost = world.Buildings.AddTown(home.Position + new Vector3(1500, 0, 0), data.PlayerFaction);
        world.RunTicks(15);
        var war = population.Wars[faction];
        foreach (var m in population.Wars) m.UpdateTime = m == war ? 0 : 1e9;
        string human = db.OfType(FcsRecordType.CHARACTER).First(r => r.GetReferences("race").Any(x => x.Values.Value0 > 0 && db.Find(x.TargetStringId)?.Name == "Greenlander") && r.GetReferences("stats").Count > 0).StringId;
        var at = new Vector2(outpost.Position.X, outpost.Position.Z);
        var heroes = new List<CharacterId> { population.SpawnSandbox(world, human, at, player: true) };
        for (int k = 1; k < 4; k++) heroes.Add(population.SpawnSandbox(world, human, at + new Vector2(30 * k, 20), player: true, variant: k, faction: data.PlayerFaction));
        var table = world.Characters;
        foreach (var h in heroes) table.Cold(h.Slot)!.IsPlayer = true;

        int Caged(SimWorld w, IEnumerable<CharacterId> ids) => ids.Count(h => w.Characters.TryResolveNext(h, out int s) && w.Characters.Cold(s)!.IsImprisoned);
        // The assault is won once nobody fights 0.2 h in, which the lifts themselves can bring about, so not every hero need be downed; the leader's captive goes to the campaign's
        // home, which has no free cage. The carriers of the rest walk home and cage them.
        for (int i = 0; i < 60 && Caged(world, heroes) < 2; i++) world.RunTicks(150);
        output.WriteLine($"tick {world.Tick}: caged {Caged(world, heroes)}, lifted {set.Kidnap!.Log.Count(e => e.Kind == KidnapEventKind.Lifted)}");
        Assert.True(set.Kidnap!.Log.Count(e => e.Kind == KidnapEventKind.Lifted && heroes.Contains(e.Victim)) >= 3);
        Assert.True(Caged(world, heroes) >= 2);
        Assert.All(heroes.Where(h => table.Cold(h.Slot)!.IsImprisoned), h => Assert.True(table.Cold(h.Slot)!.Kidnapped));

        // A save and a load keep the cage state and the flag, and a second save writes the same.
        var options = new SaveCaptureOptions { Placements = [.. levels.Towns()], Seed = 3 };
        var game = SaveCapture.Capture(world, data, new SaveClock(1, 13, 0), options: options);
        using var back = NewWorld(new PopulationSettings(), out _);
        SaveLoader.Load(back, game, data, new SaveLoadOptions { ApplyRelations = false });
        back.RunTicks(30);
        var loaded = Enumerable.Range(0, back.Characters.HighWater).Where(s => back.Characters.Next[s].Alive && back.Characters.Cold(s) is { IsPlayer: true, IsImprisoned: true }).ToList();
        Assert.Equal(Caged(world, heroes), loaded.Count);
        Assert.All(loaded, s => Assert.True(back.Characters.Cold(s)!.Kidnapped));
        var again = SaveCapture.Capture(back, data, new SaveClock(1, 13, 0), options: options);
        int Flags(SaveGame g) => g.Platoons.SelectMany(p => p.Characters).Count(c => c.GameState?.Bools.ContainsKey(CrimeSave.KidnappedKey) == true);
        Assert.Equal(Flags(game), Flags(again));
        Assert.Equal(loaded.Count, Flags(game));
    }

    [Fact]
    public void A_player_character_puts_the_games_shackles_on_a_downed_character_locked_and_its_worn_item_in_that_slot_goes_to_its_inventory()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var db = InstallData.BaseGame!;
        var data = PopulationData.Create(db, []);
        var walk = new OpenGroundWalkability((_, _) => 300);
        var set = StandardSystems.Build(data, walk, new StandardSystemOptions
        {
            Parts = StandardParts.Player | StandardParts.Movement | StandardParts.Body | StandardParts.Carry | StandardParts.Crime, SynchronousPaths = true,
        });
        using var world = new SimWorld(new WorldSettings { Seed = 5, PublishSnapshots = false }, walk, set.Systems);
        var population = new PopulationSystem(data, new PopulationSettings());
        string human = db.OfType(FcsRecordType.CHARACTER).First(r => r.GetReferences("race").Any(x => x.Values.Value0 > 0 && db.Find(x.TargetStringId)?.Name == "Greenlander") && r.GetReferences("stats").Count > 0).StringId;
        var worker = population.SpawnSandbox(world, human, new Vector2(5000, 5000), player: true);
        var wearer = population.SpawnSandbox(world, human, new Vector2(5100, 5000), player: false, variant: 1);
        var table = world.Characters;
        var wc = table.Cold(wearer.Slot)!;
        wc.Medical!.KnockOut(100000);

        var record = db.OfType(FcsRecordType.ARMOUR).First(r => r.GetBool("is locked"));
        var shackles = data.Items.Create(record.StringId)!;
        shackles.Section = "main";
        Assert.True(table.Cold(worker.Slot)!.Inventory!.Add(shackles));
        // Something already worn in that slot.
        var other = db.OfType(FcsRecordType.ARMOUR).Select(r => data.Items.Create(r.StringId)!).First(i => !i.IsLocked && i.EquipmentSection == shackles.EquipmentSection);
        wc.Inventory!.Items.RemoveAll(i => i.Section == shackles.EquipmentSection);
        Assert.True(Meitou.Simulation.Items.InventoryCommands.TryEquip(wc.Inventory, other));
        Assert.True(LockSystem.CanBeShackled(wc, true));

        world.Commands.Enqueue(new PutShacklesOrder([worker], wearer) { Tick = world.Tick });
        for (int i = 0; i < 60 && LockSystem.WornShackles(wc) is null; i++) world.RunTicks(30);
        output.WriteLine($"{record.Name} ({shackles.EquipmentSection}) on {wc.Name} at tick {world.Tick}; {other.Name} now in {other.Section}");
        var worn = LockSystem.WornShackles(wc);
        Assert.NotNull(worn);
        Assert.Equal(record.StringId, worn.Record);
        Assert.True(worn.Lock!.EffectivelyLocked);
        Assert.Equal("main", other.Section);
        Assert.Contains(other, wc.Inventory.Items);
        Assert.Null(LockSystem.CarriedShackles(table.Cold(worker.Slot)));
        Assert.False(LockSystem.CanBeShackled(wc, true));   // one pair at a time
    }
}
