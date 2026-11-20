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
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Kidnap;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Raiders kidnapping the downed (docs/simulation.md "Kidnapping"; the finder in docs/game/carry-loot.md 3.2).
/// </summary>
public class KidnapTests
{
    static readonly Vector3 Centre = new(2000, 150, 2000);
    static readonly RaceData Person = new() { StringId = "17-c", Name = "Greenlander", StatMultipliers = Human.StatMultipliers,
            Anatomy = Human.Anatomy, Carriable = true };

    /// <summary>
    /// A package whose every role list holds goal 254 (find and kidnap) and 112 (find and put in a cage: the carrier's own goal cages the body once
    /// 254 is done at the lift), always in their hours.
    /// </summary>
    static AiPackage Kidnapping()
    {
        var kidnap = new AiGoal(new AiTask("t", "kidnap", TaskType.FinalKidnapperCageJob, TaskPriority.Urgent, TaskTargetType.Specific,
                TaskEndEvent.Nothing), 0, 100, 1);
        var cage = new AiGoal(new AiTask("c", "cage", TaskType.FindCageAndPutIn, TaskPriority.Urgent, TaskTargetType.Specific,
                TaskEndEvent.Nothing), 0, 100, 1);
        return new AiPackage { Id = "kidnap", Name = "kidnap", Roles = [[kidnap, cage], [kidnap, cage], [kidnap, cage], [], []] };
    }

    static CharacterId Add(SimWorld world, CharacterCold cold, Vector2 at, Squad squad)
    {
        cold.Race = Person;
        cold.SquadId = squad.Id;
        var id = world.Characters.Spawn(new CharacterHot { Position = Centre + new Vector3(at.X, 0, at.Y), Health = 100, MaxSpeed = 60,
                WalkSpeed = 15 }, cold, 0);
        squad.Members.Add(id);
        if (squad.Leader.IsNone) squad.Leader = id;
        return id;
    }

    /// <summary>
    /// The jail town of the arrest tests with a cannibal-like squad whose package has goal 254 beside a knocked-out player character: lift, carry,
    /// cage; pinned in <see cref="GoldenHashTests"/>.
    /// </summary>
    internal static List<ulong> Scripted(int threads)
    {
        var data = ArrestTests.Town();
        var carry = new CarrySystem(BaseGame, BodyOptions.Default, 1) { Relations = data.Relations };
        using var population = new PopulationSystem(data);
        var kidnap = new KidnapSystem(data, population, carry, new KidnapSettings { RecordLog = true });
        var walk = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 31, Threads = threads, PublishSnapshots = false, MinPartitionSize = 1 }, walk,
            [new PlayerSystem(), new AiSystem(data), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), carry, kidnap,
                    new TownDutySystem(data, population, carry)]);
        world.Fixtures.RegisterSite(world, data, 0);
        int city = data.FactionIndex("50-a");

        var board = new Blackboard([new(0, Kidnapping())]);
        board.Update();
        var raiders = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = city, Town = 0, Ai = board,
                HomeCentre = new Vector2(Centre.X, Centre.Z), HomeRadius = 500 };
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
            Logged: kidnap.Log.Any(e => e.Kind == KidnapEventKind.Lifted && e.Victim == victim)
                    && kidnap.Log.Any(e => e.Kind == KidnapEventKind.Caged && e.Victim == victim));

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
            [new PlayerSystem(), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), carry, population,
                    new Meitou.Simulation.Dialogue.DialogueSystem(data), new AiSystem(data)]);
        int city = data.FactionIndex("50-a");

        // Goal 177 in every role list, always in its hours.
        var goal = new AiGoal(new AiTask("t", "loot", TaskType.LootTheDead, TaskPriority.Urgent, TaskTargetType.Specific, TaskEndEvent.Nothing), 0,
                100, 1);
        var board = new Blackboard([new(0, new AiPackage { Id = "loot", Name = "loot", Roles = [[goal], [goal], [goal], [], []] })]);
        board.Update();
        var bandits = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = city, Ai = board };
        world.Squads.Add(bandits);
        var bandit = new Combatant { Name = "bandit" }.MakeCold();
        var pack = new Inventory();
        (bandit.Faction, bandit.NpcClass, bandit.Inventory, bandit.Ai) = (city, FundamentalType.Bandit, pack, new AiMind { Squad = bandits });
        pack.Items.Add(new ItemInstance { Record = "sabre", Type = FcsRecordType.Weapon, Section = "hip" });
        var looter = Add(world, bandit, Vector2.Zero, bandits);

        var players = new Squad { Id = world.Squads.NextId(), TemplateId = "p", Faction = data.Relations.PlayerFaction };
        world.Squads.Add(players);
        var hero = new Combatant { Name = "hero" }.MakeCold();
        var pockets = new Inventory();
        (hero.Faction, hero.IsPlayer, hero.Inventory) = (data.Relations.PlayerFaction, true, pockets);
        var bread = new ItemInstance { Record = "bread", Type = FcsRecordType.Item, ItemFunction = 3 };
        var wrench = new ItemInstance { Record = "wrench", Type = FcsRecordType.Item };
        var katana = new ItemInstance { Record = "katana", Type = FcsRecordType.Weapon, Section = "hip" };

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
        Assert.True(world.SystemOf<Meitou.Simulation.Dialogue.DialogueSystem>()!.Memory.HasShort(world, bandits.Id, victim,
                Meitou.Simulation.Dialogue.MemoryTag.Looted));

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

        // The finder: a down stranger is a victim; the raiders' own, an animal, one already caged and (once their faction is allied to it) the
        // stranger's faction are not.
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

        // A kidnapped prisoner: its sentence is never served, no bail is offered, getting out is no escape. The same prisoner with an unserved
        // sentence does escape.
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
        var record = SyntheticTown.Rec("1-s", FcsRecordType.Character, "state");
        SlaveSave.Write(record, cold, data);
        var loaded = new Combatant().MakeCold();
        SlaveSave.Read(record, loaded, data);

        Assert.Equal((2, city), (loaded.SlaveState, loaded.SlaveOwner));

        // Caged again by an owner: a slave again; a release leaves it one (FUN_1403570a0 never sets the slave state), bail's clear frees it
        // (FUN_1406ae170).
        Assert.True(world.Fixtures.Imprison(world, slave, cage));
        Assert.Equal((1, city), (cold.SlaveState, cold.SlaveOwner));
        Assert.True(world.Fixtures.Release(world, slave));
        Assert.Equal((1, city), (cold.SlaveState, cold.SlaveOwner));

        FixtureRegistry.ClearSlave(world, cold);

        Assert.Equal((0, -1), (cold.SlaveState, cold.SlaveOwner));

        SlaveSave.Write(record, cold, data);

        Assert.False(record.Strings.ContainsKey(SlaveSave.OwnerKey));
    }

    static (SimWorld World, CharacterCold Cold, int City, PopulationData Data) SlaveRig()
    {
        var data = ArrestTests.Town();
        var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 150));
        var cold = new Combatant().MakeCold();
        cold.Faction = data.Relations.PlayerFaction;
        var squad = new Squad { Id = 1, TemplateId = "t", Faction = data.Relations.PlayerFaction };
        world.Squads.Add(squad);
        Add(world, cold, Vector2.Zero, squad);
        return (world, cold, data.FactionIndex("50-a"), data);
    }

    [Fact]
    public void An_escaped_slave_lies_low_to_ex_slave_after_12_hours_then_free_after_4_days_and_a_future_stamp_frees_at_once()
    {
        var (world, cold, city, _) = SlaveRig();
        using var owned = world;
        world.GameHours = 100;
        FixtureRegistry.Enslave(world, cold, city);
        cold.SlaveSince = 200;   // a slave (1) never lies low, whatever its stamp
        FixtureRegistry.LayLow(world, cold);

        Assert.Equal((1, city), (cold.SlaveState, cold.SlaveOwner));

        FixtureRegistry.TagEscaped(world, cold);
        world.GameHours = 112;
        FixtureRegistry.LayLow(world, cold);

        Assert.Equal(2, cold.SlaveState);   // not more than 12 hours

        world.GameHours = 112.5;
        FixtureRegistry.LayLow(world, cold);

        Assert.Equal((3, city, 112.5), (cold.SlaveState, cold.SlaveOwner, cold.SlaveSince));

        world.GameHours = 112.5 + 96;
        FixtureRegistry.LayLow(world, cold);

        Assert.Equal(3, cold.SlaveState);   // not more than 4 days

        world.GameHours = 112.5 + 96.5;
        FixtureRegistry.LayLow(world, cold);

        Assert.Equal((0, -1), (cold.SlaveState, cold.SlaveOwner));

        // A stamp later than the clock (a save's ssct ahead of the clock it loads with) frees at once.
        FixtureRegistry.Enslave(world, cold, city);
        FixtureRegistry.TagEscaped(world, cold);
        world.GameHours -= 0.001;
        FixtureRegistry.LayLow(world, cold);

        Assert.Equal((0, -1), (cold.SlaveState, cold.SlaveOwner));
    }

    [Fact]
    public void The_save_keeps_the_ex_slave_state_and_the_ssct_stamp()
    {
        var (world, cold, city, data) = SlaveRig();
        using var owned = world;
        (cold.SlaveState, cold.SlaveOwner, cold.SlaveSince) = (3, city, 34.00063);
        var record = SyntheticTown.Rec("1-s", FcsRecordType.Character, "state");
        SlaveSave.Write(record, cold, data);

        Assert.Equal(34.00063f, record.Floats[SlaveSave.SinceKey]);

        var loaded = new Combatant().MakeCold();
        SlaveSave.Read(record, loaded, data);

        Assert.Equal((3, city, (double)34.00063f), (loaded.SlaveState, loaded.SlaveOwner, loaded.SlaveSince));

        record.Ints[SlaveSave.StateKey] = 4;   // out of range: free
        SlaveSave.Read(record, loaded, data);

        Assert.Equal((0, -1), (loaded.SlaveState, loaded.SlaveOwner));
    }
}

/// <summary>
/// The Skin Bandits' small raid against an outpost of the installed game's data. Skipped without the game.
/// </summary>
[Slow]
public class KidnapInstallTests(ITestOutputHelper output)
{
    /// <summary>
    /// How far round a cage the test's ground is the cage's floor: past its still reach (use range 15, doubled).
    /// </summary>
    const float CageFloorRadius = 40;

    /// <summary>
    /// How far east of the raiders' town the outpost stands: the raid is drafted within 700 of its town, so it starts outside the outpost's arrival
    /// circle (0.8 of a player town's radius 2500) and marches there under its travel package.
    /// </summary>
    const float OutpostDistance = 4200;

    /// <summary>
    /// How far short of the outpost's centre, towards the raiders' town, the heroes stand: just inside the arrival circle, where the raid stops and
    /// its assault package starts. An outpost with no building gives that package nothing else to go to (216 no building to ransack, 270 no gate or
    /// wall), so the raiders reach only characters they see from there.
    /// </summary>
    const float HeroesInset = 1850;

    /// <summary>
    /// How far east of the raiders' town the house test's outpost stands: inside its arrival circle (2000) for a raid drafted within 700 of its
    /// town, so phase 1 starts where the raid is drafted.
    /// </summary>
    const float NearOutpostDistance = 1500;

    /// <summary>
    /// How far east of the house the first hero stands (the others 30 apart from there, 150 to 240 from the house), so a raider at the house is not one
    /// fighting a hero.
    /// </summary>
    const float HeroesBesideHouse = 150;

    /// <summary>
    /// How near the house's origin a raider counts as at its way in: the Small Shack's door fixture stands at the origin, and the raiders come
    /// within 3 to 8 of it.
    /// </summary>
    const float HouseReach = 20;

    /// <summary>
    /// The ticks the house test gives the raid: a raider reaches the house at about 2300 and a hero is down at about 2500.
    /// </summary>
    const int RaidTicks = 4500;

    /// <summary>
    /// The Skin Bandits' small raid on an outpost of four player characters: the raiders lift the downed and cage them in their town, and a save
    /// and a load keep them caged and kidnapped.
    /// </summary>
    [Fact]
    public void A_cannibal_raid_lifts_downed_player_characters_and_cages_them_and_a_save_keeps_them_kidnapped()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        var db = InstallData.FullLoadOrder!;
        using var map = TerrainHeightmap.Open(InstallData.Install!);
        var data = InstallPopulation(map, out var levels);
        var cageFloors = new List<Fixture>();
        var walk = CageFloorGround(map, cageFloors);

        using var world = NewWorld(data, walk, new PopulationSettings { RaidFrequencyMult = 1000 }, out var set);
        var population = set.Population!;
        world.Player.Faction = data.PlayerFaction;
        int faction = Enumerable.Range(0, data.Factions.Count).First(f => data.Factions[f].Name == "Skin Bandits");

        Assert.Contains(data.Factions[faction].Campaigns, l => population.CampaignOf(l.Id)?.Name == "skin bandit standard raid small");

        // A town of theirs with cages, and an outpost east of it with four player characters at its edge.
        var home = TownWithCages(world, data, faction);

        Assert.NotNull(home);

        cageFloors.AddRange(world.Fixtures.All.Where(x => x.Kind == FixtureKind.Cage && x.Site == home.Index));
        var outpost = world.Buildings.AddTown(home.Position + new Vector3(OutpostDistance, 0, 0), data.PlayerFaction);
        world.RunTicks(15);
        var war = population.Wars[faction];
        foreach (var m in population.Wars) m.UpdateTime = m == war ? 0 : 1e9;
        var heroes = SpawnHeroes(world, population, db, data, new Vector2(outpost.Position.X - HeroesInset, outpost.Position.Z));
        var table = world.Characters;
        foreach (var h in heroes) table.Cold(h.Slot)!.IsPlayer = true;

        // The assault is won once nobody fights 0.2 h in, which the lifts themselves can bring about, so not every hero need be downed. The
        // carriers walk their captives to their platoon's home town under the retreat package and cage them there.
        for (int i = 0; i < 60 && Caged(world, heroes) < 2; i++) world.RunTicks(150);
        output.WriteLine($"tick {world.Tick}: caged {Caged(world, heroes)}, lifted {set.Kidnap!.Log.Count(e => e.Kind == KidnapEventKind.Lifted)}");

        Assert.True(set.Kidnap!.Log.Count(e => e.Kind == KidnapEventKind.Lifted && heroes.Contains(e.Victim)) >= 3);
        Assert.True(Caged(world, heroes) >= 2);
        Assert.All(heroes.Where(h => table.Cold(h.Slot)!.IsImprisoned), h => Assert.True(table.Cold(h.Slot)!.Kidnapped));

        ASaveKeepsThemKidnapped(world, data, walk, levels, heroes);
    }

    /// <summary>
    /// The Skin Bandits' small raid on an outpost 1500 from their town, so drafted inside its arrival circle: phase 1 starts with nobody in sight,
    /// and only 216's draw of the outpost's house (<c>FUN_1408cb510</c> takes the squad's town, a player outpost too) takes the raiders in. They
    /// reach the house's door and down one of the heroes standing 150 out, at about 2300 and 2500 ticks (the same with the heroes 40 to 250 out).
    /// Catches 216 losing player towns again: the raiders then stand where phase 1 finds them, no closer than 682, and nobody is hurt.
    /// </summary>
    [Fact]
    public void A_raid_drafted_near_a_player_outpost_enters_its_house_and_downs_a_hero()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        using var map = TerrainHeightmap.Open(InstallData.Install!);
        var data = InstallPopulation(map, out _);
        var cageFloors = new List<Fixture>();
        using var world = NewWorld(data, CageFloorGround(map, cageFloors), new PopulationSettings { RaidFrequencyMult = 1000 }, out var set,
                StandardParts.All);
        world.Player.Faction = data.PlayerFaction;
        int faction = Enumerable.Range(0, data.Factions.Count).First(f => data.Factions[f].Name == "Skin Bandits");
        var home = TownWithCages(world, data, faction);

        Assert.NotNull(home);

        cageFloors.AddRange(world.Fixtures.All.Where(x => x.Kind == FixtureKind.Cage && x.Site == home.Index));
        var outpost = world.Buildings.AddTown(home.Position + new Vector3(NearOutpostDistance, 0, 0), data.PlayerFaction);
        var house = PlaceShack(world, data, map, outpost);
        world.Commands.Enqueue(new FocusCommand(house) { Tick = world.Tick + 1 });
        world.RunTicks(15);
        var population = set.Population!;
        foreach (var m in population.Wars) m.UpdateTime = m == population.Wars[faction] ? 0 : 1e9;
        var heroes = SpawnHeroes(world, population, data.Db, data, new Vector2(house.X + HeroesBesideHouse, house.Z));
        foreach (var h in heroes) world.Characters.Cold(h.Slot)!.IsPlayer = true;
        var (reached, downed) = RunRaid(world, faction, house, heroes);
        output.WriteLine($"a raider within {HouseReach} of the house at tick {reached}, a hero down at tick {downed}");

        Assert.True(reached >= 0, $"no raider came within {HouseReach} of the house in {RaidTicks} ticks");
        Assert.True(downed >= 0, $"no hero was downed in {RaidTicks} ticks");
    }

    /// <summary>
    /// The install's base-game population with its towns' buildings on the terrain of <paramref name="map"/>, and the level data it was made of.
    /// </summary>
    static PopulationData InstallPopulation(TerrainHeightmap map, out WorldLevelData levels)
    {
        var install = InstallData.Install!;
        var db = InstallData.FullLoadOrder!;
        levels = WorldLevelData.LoadBaseGame(install, includeInteriors: true);
        var buildings = TownBuildings.Build(db, levels, new BuildingLayouts(db, levels.Interiors), (x, z) => map.HeightAt((float)x, (float)z));
        return PopulationData.Create(db, levels.Towns(), new GeneratedAppearances(db, install.Root), buildings: buildings);
    }

    /// <summary>
    /// The terrain, with the floor the navmesh would give round each cage in <paramref name="cageFloors"/> (filled in later): Skinhouse HQ's cages
    /// stand on upper floors, 87 to 95 above the ground, and a cage is reached only within 15 of its height (FixtureRegistry.AtCage). The
    /// heightmap's file reader is not thread-safe.
    /// </summary>
    static OpenGroundWalkability CageFloorGround(TerrainHeightmap map, List<Fixture> cageFloors)
    {
        var heightGate = new object();
        float Ground(float x, float z)
        {
            foreach (var c in cageFloors)
                if (Vector2.Distance(new(x, z), new(c.Position.X, c.Position.Z)) < CageFloorRadius) return c.Position.Y;
            lock (heightGate) return map.HeightAt(x, z);
        }

        return new OpenGroundWalkability(Ground);
    }

    /// <summary>
    /// A finished player "Small Shack" (one unlocked door; function key 19, a house) at the centre of <paramref name="outpost"/>, in that town and
    /// on the ground, with its doors registered; returns where it stands.
    /// </summary>
    static Vector3 PlaceShack(SimWorld world, PopulationData data, TerrainHeightmap map, Meitou.Simulation.Construction.PlayerTown outpost)
    {
        var at = new Vector3(outpost.Position.X, map.HeightAt(outpost.Position.X, outpost.Position.Z), outpost.Position.Z);
        string shack = data.Db.OfType(FcsRecordType.Building).First(r => r.Name == "Small Shack").StringId;
        var site = new Meitou.Simulation.Construction.BuildSite
        {
            Id = world.Buildings.NewId(WorldLayout.ZoneOf(at.X, at.Z)), Record = shack, Position = at, Owner = data.PlayerFaction, Materials = [],
            Complete = true, Town = outpost.Number,
        };
        world.Buildings.Add(site);
        world.Fixtures.RegisterPlayerBuilding(world, data, site);
        return at;
    }

    /// <summary>
    /// Runs the raid up to <see cref="RaidTicks"/> ticks, until a character of <paramref name="faction"/> has come within <see cref="HouseReach"/>
    /// of <paramref name="house"/> and one of the heroes is down; the ticks (from the start of the run) of the first of each, -1 for none.
    /// </summary>
    static (long Reached, long Downed) RunRaid(SimWorld world, int faction, Vector3 house, List<CharacterId> heroes)
    {
        var table = world.Characters;
        long start = world.Tick;
        long reached = -1, downed = -1;
        while (world.Tick - start < RaidTicks && (reached < 0 || downed < 0))
        {
            world.RunTicks(1);
            if (reached < 0 && RaiderAt(table, faction, house)) reached = world.Tick - start;
            if (downed < 0 && heroes.Any(h => table.Cold(h.Slot)!.Medical!.Incapacitated)) downed = world.Tick - start;
        }

        return (reached, downed);
    }

    /// <summary>
    /// Whether a live character of <paramref name="faction"/> stands within <see cref="HouseReach"/> of <paramref name="house"/> (on the ground).
    /// </summary>
    static bool RaiderAt(CharacterTable table, int faction, Vector3 house)
    {
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s)?.Faction != faction) continue;

            var p = table.Next[s].Position;
            if (Vector2.Distance(new(p.X, p.Z), new(house.X, house.Z)) <= HouseReach) return true;
        }

        return false;
    }

    /// <summary>
    /// A world of the install's data with population, the player, fighting, first aid, aggro, flight, carrying, crime and AI, paths found at once
    /// and the kidnap log recorded.
    /// </summary>
    static SimWorld NewWorld(PopulationData data, OpenGroundWalkability walk, PopulationSettings settings, out StandardSystemSet set,
            StandardParts parts = StandardParts.Population | StandardParts.Player | StandardParts.Pursuit | StandardParts.Movement | StandardParts.Body
                | StandardParts.Combat | StandardParts.Retaliation
                | StandardParts.FirstAid | StandardParts.Aggro | StandardParts.Flee | StandardParts.Carry | StandardParts.Crime | StandardParts.Ai)
    {
        set = StandardSystems.Build(data, walk, new StandardSystemOptions
        {
            Parts = parts,
            SynchronousPaths = true,
            Population = settings,
            Kidnap = new KidnapSettings { RecordLog = true },
        });
        return new SimWorld(new WorldSettings { Seed = 11, Threads = 2, PublishSnapshots = false }, walk, set.Systems);
    }

    /// <summary>
    /// The faction's first town that has cages, its sites registered up to it.
    /// </summary>
    static TownSite TownWithCages(SimWorld world, PopulationData data, int faction)
    {
        TownSite home = null!;
        foreach (var t in data.Sites.Where(s => data.SiteFactions[s.Index] == faction && s.Town.Type is not (TownType.NestMarker or TownType.Null)))
        {
            world.Fixtures.RegisterSite(world, data, t.Index);
            if (world.Fixtures.All.Any(x => x.Kind == FixtureKind.Cage && x.Site == t.Index))
            {
                home = t;
                break;
            }
        }
        return home;
    }

    /// <summary>
    /// Four Greenlander player characters of the outpost at <paramref name="at"/>, side by side.
    /// </summary>
    static List<CharacterId> SpawnHeroes(SimWorld world, PopulationSystem population, GameDatabase db, PopulationData data, Vector2 at)
    {
        string human = db.OfType(FcsRecordType.Character).First(r => r.GetReferences("race").Any(x => x.Values.Value0 > 0
                && db.Find(x.TargetStringId)?.Name == "Greenlander") && r.GetReferences("stats").Count > 0).StringId;
        var heroes = new List<CharacterId> { population.SpawnSandbox(world, human, at, player: true) };
        for (int k = 1; k < 4; k++)
        {
            heroes.Add(population.SpawnSandbox(world, human, at + new Vector2(30 * k, 20), player: true, variant: k,
                    faction: data.PlayerFaction));
        }
        return heroes;
    }

    /// <summary>
    /// How many of the characters are alive and imprisoned.
    /// </summary>
    static int Caged(SimWorld w, IEnumerable<CharacterId> ids) =>
        ids.Count(h => w.Characters.TryResolveNext(h, out int s) && w.Characters.Cold(s)!.IsImprisoned);

    /// <summary>
    /// How many saved characters carry the kidnapped flag.
    /// </summary>
    static int Flags(SaveGame g) => g.Platoons.SelectMany(p => p.Characters).Count(c => c.GameState?.Bools.ContainsKey(CrimeSave.KidnappedKey) == true);

    /// <summary>
    /// A save and a load keep the cage state and the flag, and a second save writes the same.
    /// </summary>
    static void ASaveKeepsThemKidnapped(SimWorld world, PopulationData data, OpenGroundWalkability walk, WorldLevelData levels,
            List<CharacterId> heroes)
    {
        var options = new SaveCaptureOptions { Placements = [.. levels.Towns()], Seed = 3 };
        var game = SaveCapture.Capture(world, data, new SaveClock(1, 13, 0), options: options);
        using var back = NewWorld(data, walk, new PopulationSettings(), out _);
        SaveLoader.Load(back, game, data, new SaveLoadOptions { ApplyRelations = false });
        back.RunTicks(30);
        var loaded = Enumerable.Range(0, back.Characters.HighWater)
            .Where(s => back.Characters.Next[s].Alive && back.Characters.Cold(s) is { IsPlayer: true, IsImprisoned: true })
            .ToList();

        Assert.Equal(Caged(world, heroes), loaded.Count);
        Assert.All(loaded, s => Assert.True(back.Characters.Cold(s)!.Kidnapped));

        var again = SaveCapture.Capture(back, data, new SaveClock(1, 13, 0), options: options);

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
            Parts = StandardParts.Player | StandardParts.Movement | StandardParts.Body | StandardParts.Carry | StandardParts.Crime,
                    SynchronousPaths = true,
        });
        using var world = new SimWorld(new WorldSettings { Seed = 5, PublishSnapshots = false }, walk, set.Systems);
        var population = new PopulationSystem(data, new PopulationSettings());
        string human = db.OfType(FcsRecordType.Character).First(r => r.GetReferences("race").Any(x => x.Values.Value0 > 0
                && db.Find(x.TargetStringId)?.Name == "Greenlander") && r.GetReferences("stats").Count > 0).StringId;
        var worker = population.SpawnSandbox(world, human, new Vector2(5000, 5000), player: true);
        var wearer = population.SpawnSandbox(world, human, new Vector2(5100, 5000), player: false, variant: 1);
        var table = world.Characters;
        var wc = table.Cold(wearer.Slot)!;
        wc.Medical!.KnockOut(100000);

        var record = db.OfType(FcsRecordType.Armour).First(r => r.GetBool("is locked"));
        var shackles = data.Items.Create(record.StringId)!;
        shackles.Section = "main";

        Assert.True(table.Cold(worker.Slot)!.Inventory!.Add(shackles));

        // Something already worn in that slot.
        var other = db.OfType(FcsRecordType.Armour).Select(r => data.Items.Create(r.StringId)!).First(i => !i.IsLocked
                && i.EquipmentSection == shackles.EquipmentSection);
        wc.Inventory!.Items.RemoveAll(i => i.Section == shackles.EquipmentSection);

        Assert.True(InventoryCommands.TryEquip(wc.Inventory, other));
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
