using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Combat;
using Meitou.Tests.Save;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Cages, locks, arrests and jail (docs/simulation.md "Cages, beds and locks", "Arrest and jail"; docs/game/locks.md, docs/game/economy.md
/// 11.5-11.8).
/// </summary>
public class ArrestTests
{
    static readonly Vector3 Centre = new(2000, 150, 2000);
    static readonly RaceData Person = new() { StringId = "17-c", Name = "Greenlander", StatMultipliers = Human.StatMultipliers,
            Anatomy = Human.Anatomy, Carriable = true };

    /// <summary>
    /// A town of a faction that books bounties, with a house of two doors and two cages.
    /// </summary>
    internal static PopulationData Town(IEnumerable<FcsRecord>? extra = null)
    {
        static FcsRecord Rec(string id, FcsRecordType type, string name) => SyntheticTown.Rec(id, type, name);
        var city = Rec("50-a", FcsRecordType.Faction, "city");
        city.Bools["offers bounties"] = true;
        var nameless = Rec("204-gamedata.base", FcsRecordType.Faction, "Nameless");
        var door = Rec("51-a", FcsRecordType.Building, "Door");
        var house = Rec("52-a", FcsRecordType.Building, "House");
        house.Floats["hardness"] = 40;
        house.References["doors"] = [SyntheticTown.Ref("51-a"), SyntheticTown.Ref("51-a")];
        var function = Rec("53-a", FcsRecordType.BuildingFunctionality, "Cage");
        function.Ints["function"] = TownBuildings.CageFunction;
        function.Floats["use range"] = 25;
        var cage = Rec("54-a", FcsRecordType.Building, "Prisoner Cage");
        cage.Floats["hardness"] = 50;
        cage.References["functionality"] = [SyntheticTown.Ref("53-a")];
        var town = Rec("55-a", FcsRecordType.Town, "Jailtown");
        town.Ints["type"] = 2;
        town.Floats["size radius"] = 600;
        town.References["faction"] = [SyntheticTown.Ref("50-a")];
        var file = new FcsFile();
        file.Records.AddRange([city, nameless, door, house, function, cage, town]);
        if (extra is not null) file.Records.AddRange(extra);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        TownBuilding B(string id, string record, Vector2 at) => new(id, record, record, Centre + new Vector3(at.X, 0, at.Y), 0, false, false, []);
        var buildings = new Dictionary<string, List<TownBuilding>>
        {
            ["jailtown"] = [B("house", "52-a", new(0, 400)), B("cageA", "54-a", new(200, 0)), B("cageB", "54-a", new(-200, 0))],
        };
        return PopulationData.Create(db, [new TownPlacement("jailtown", "55-a", Centre, null)], buildings: buildings);
    }

    /// <summary>
    /// The town above: two city policemen and two player characters. One, wanted for 5000 and just booked, stands by the first policeman, who arrests
    /// him (fight, lift, carry, cage A); the other starts in cage B, picks its lock and gets out at tick 900 (an escape). Hashes at five checkpoints,
    /// pinned in <see cref="GoldenHashTests"/>.
    /// </summary>
    internal static List<ulong> Scripted(int threads)
    {
        var data = Town();
        var relations = data.Relations;
        using var world = ScriptedWorld(data, relations, threads, out var arrest);
        int city = data.FactionIndex("50-a");
        var playerSquad = new Squad { Id = 1000, TemplateId = "p", Faction = relations.PlayerFaction };
        world.Squads.Add(playerSquad);

        Police(world, city, new Vector2(0, 60));
        Police(world, city, new Vector2(-200, 200));
        var hero = Hero(world, relations, city, playerSquad, new Vector2(0, 0), "hero", 5000,
                new Combatant { Attack = 20, Defence = 40, Dodge = 40, Toughness = 50 });
        world.Characters.Cold(hero.Slot)!.RecentlyWanted = CrimeSystem.RecentlyWantedSeconds;
        var picker = Hero(world, relations, city, playerSquad, new Vector2(-200, 0), "picker", 2500, new Combatant());
        world.Characters.Cold(picker.Slot)!.Stats![StatsEnumerated.Lockpicking] = 60;
        var cageB = world.Fixtures.Find("0.0:cageB")!;

        Assert.True(world.Fixtures.Imprison(world, picker, cageB));

        var (hashes, picked) = RunTheScript(world, picker, cageB);
        var heroCold = world.Characters.Cold(hero.Slot)!;
        var seen = (Jailed: heroCold.IsImprisoned && heroCold.InWhat == "0.0:cageA" && arrest.Log.Any(e => e.Kind == ArrestEventKind.Jailed
                && e.Target == hero),
            Picked: picked, Escaped: world.Characters.Cold(picker.Slot)!.Escaped || arrest.Log.Any(e => e.Target == picker));

        // It covers what it is for: the arrest reached the cage, the prisoner's pick opened the other cage, and getting out was an escape.
        Assert.True(seen.Jailed && seen.Picked && seen.Escaped, $"{seen}");

        return hashes;
    }

    /// <summary>
    /// The world of <see cref="Scripted"/>: player, pursuit, movement, bodies, carrying, combat, retaliation, aggro, crime, arrest and locks, the
    /// town's site registered.
    /// </summary>
    static SimWorld ScriptedWorld(PopulationData data, FactionRelations relations, int threads, out ArrestSystem arrest)
    {
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths,
                new CombatOptions { SelfApproach = false, TickMedical = false }) { Relations = relations };
        var carry = new CarrySystem(BaseGame, BodyOptions.Default, 1) { Relations = relations };
        var crime = new CrimeSystem(relations);
        combat.Crimes = crime;
        carry.Crimes = crime;
        arrest = new ArrestSystem(crime, BaseGame, new ArrestSettings { RecordLog = true }) { Carry = carry };
        var locks = new LockSystem(BaseGame, BodyOptions.Default) { Crimes = crime };
        var walk = new OpenGroundWalkability((_, _) => 150);
        var world = new SimWorld(new WorldSettings { Seed = 31, Threads = threads, PublishSnapshots = false, MinPartitionSize = 1 }, walk,
            [new PlayerSystem(), new PursuitSystem(combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), carry, combat,
             new RetaliationSystem(combat), new AggroSystem(relations), crime, arrest, locks]);
        world.Fixtures.RegisterSite(world, data, 0);
        return world;
    }

    /// <summary>
    /// A city policeman in a squad of his own homed on the town, standing at <paramref name="at"/> from the centre and facing it.
    /// </summary>
    static CharacterId Police(SimWorld world, int city, Vector2 at)
    {
        var cold = new Combatant { Name = "officer", Attack = 70, Strength = 70 }.MakeCold();
        cold.Faction = city;
        cold.NpcClass = FundamentalType.LawEnforcement;
        var squad = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = city, Town = 0,
                HomeCentre = new Vector2(Centre.X, Centre.Z), HomeRadius = 500 };
        world.Squads.Add(squad);
        cold.SquadId = squad.Id;
        var id = world.Characters.Spawn(new CharacterHot { Position = Centre + new Vector3(at.X, 0, at.Y), Health = 100, MaxSpeed = 60,
                WalkSpeed = 15, Yaw = MathF.Atan2(-at.X, -at.Y), Task = (byte)CharacterTask.Idle }, cold, 0);
        squad.Members.Add(id);
        squad.Leader = id;
        return id;
    }

    /// <summary>
    /// A player character of the player's squad, wanted by the city for <paramref name="bounty"/>, standing at <paramref name="at"/> from the
    /// centre.
    /// </summary>
    static CharacterId Hero(SimWorld world, FactionRelations relations, int city, Squad playerSquad, Vector2 at, string name, int bounty,
            Combatant stats)
    {
        var cold = stats.MakeCold(name);
        cold.Race = Person;
        cold.Faction = relations.PlayerFaction;
        cold.IsPlayer = true;
        cold.NpcClass = FundamentalType.Adventurer;
        cold.SquadId = playerSquad.Id;
        var entry = (cold.Bounties = new BountyLedger()).Get(city, "city");
        entry.Amount = bounty;
        var id = world.Characters.Spawn(new CharacterHot { Position = Centre + new Vector3(at.X, 0, at.Y), Health = 100, MaxSpeed = 60,
                WalkSpeed = 15, Yaw = MathF.PI }, cold, 0);
        playerSquad.Members.Add(id);
        return id;
    }

    /// <summary>
    /// Runs to the five checkpoints: the prisoner starts picking at the first and gets out at tick 900; returns the hashes at the checkpoints and
    /// whether the cage was picked open by then.
    /// </summary>
    static (List<ulong> Hashes, bool Picked) RunTheScript(SimWorld world, CharacterId picker, Fixture cageB)
    {
        var hashes = new List<ulong>();
        bool picked = false;
        foreach (int target in new[] { 1, 300, 900, 1800, 3000 })
        {
            world.RunTicks(target - (int)world.Tick);
            if (target == 1) world.Commands.Enqueue(new PickLockOrder([picker], picker) { Tick = world.Tick });
            if (target == 900)
            {
                picked = cageB.Lock is { EffectivelyLocked: false };
                world.Commands.Enqueue(new GetOutOrder([picker]) { Tick = world.Tick });
            }
            hashes.Add(world.StateHash());
        }
        return (hashes, picked);
    }

    [Fact]
    public void The_sentence_and_the_lockpick_rules_hold_at_their_edges()
    {
        // trunc(bounty x 0.02 x prison time) hours; above 25000 for life (economy.md 11.5).
        Assert.Equal(0, FixtureRegistry.SentenceHours(49, 1));
        Assert.Equal(1, FixtureRegistry.SentenceHours(50, 1));
        Assert.Equal(100, FixtureRegistry.SentenceHours(2500, 2));
        Assert.Equal(500, FixtureRegistry.SentenceHours(25000, 1));
        Assert.Equal(FixtureRegistry.LifeSentence, FixtureRegistry.SentenceHours(25001, 1));

        // 0.9 at or above the level, halved every 10 below (locks.md "Lockpicking").
        Assert.Equal(0.9f, Lockpicking.Chance(40, 40));
        Assert.Equal(0.9f, Lockpicking.Chance(40, 90));
        Assert.Equal(0.45f, Lockpicking.Chance(40, 30), 5);

        // Allowed while the chance is above 0.05 (strict): 41 below the level is, 42 is not; a caged picker rolls at skill - 10 but is allowed by its
        // full skill.
        Assert.Equal(0.05f, BaseGame.MinimumLockpickChance, 5);

        using var world = new SimWorld(new WorldSettings(), new OpenGroundWalkability((_, _) => 0));
        var picks = new Lockpicking(BaseGame, BodyOptions.Default);
        var picker = new Combatant().MakeCold();
        picker.Stats![StatsEnumerated.Lockpicking] = 20;

        Assert.True(picks.Allowed(world, picker, 61));
        Assert.False(picks.Allowed(world, picker, 62));

        picker.InSomething = HeldState.Imprisoned;

        Assert.True(picks.Allowed(world, picker, 61));
        Assert.Equal(Lockpicking.Chance(61, 10), Lockpicking.RollChance(picker, 61));

        picker.InSomething = HeldState.None;
        picker.Medical!.KnockOut(1000);

        Assert.False(picks.Allowed(world, picker, 20));
    }

    [Fact]
    public void A_bounty_is_perceived_while_the_target_is_recently_wanted_or_is_down_and_an_enemy()
    {
        // FUN_140853fd0 behind goal 204's finder (FUN_14099bfc0): the entry of the guard's booking faction, visible with the recently-wanted marker,
        // or on a downed enemy.
        var data = Town();
        var relations = data.Relations;
        int city = data.FactionIndex("50-a");
        var arrest = new ArrestSystem(new CrimeSystem(relations), BaseGame);
        var guard = new Combatant().MakeCold();
        guard.Faction = city;
        var target = new Combatant().MakeCold();
        target.Faction = relations.PlayerFaction;

        Assert.False(arrest.PerceivedBounty(guard, target));

        (target.Bounties = new BountyLedger()).Get(city, "city").Amount = 500;
        target.RecentlyWanted = CrimeSystem.RecentlyWantedSeconds;

        Assert.True(arrest.PerceivedBounty(guard, target));
    }
}
