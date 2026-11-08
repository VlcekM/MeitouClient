using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The jail beyond the arrest itself, on the scripted town of <see cref="ArrestTests"/> (two cages, city that books bounties): the release round, the lift out of a cage, the bounty claim and
/// bail, and the bounty hunters (docs/simulation.md "Cages, beds and locks", "Arrest and jail"; docs/game/economy.md 11.4, 11.5, 11.8; docs/game/locks.md).
/// </summary>
public class JailTests
{
    static readonly Vector3 Centre = new(2000, 150, 2000);
    static readonly RaceData Person = new() { StringId = "17-c", Name = "Greenlander", StatMultipliers = Human.StatMultipliers, Anatomy = Human.Anatomy, Carriable = true };

    /// <summary>The town's cages are at +200 (A) and -200 (B) on X. <paramref name="locks"/>: the lock system's rounds run (they free a served prisoner).</summary>
    sealed class Jail : IDisposable
    {
        public SimWorld World { get; }
        public ArrestSystem Arrest { get; }
        public LockSystem? Locks { get; }
        public Fixture CageA { get; }
        public Fixture CageB { get; }
        public int City { get; }
        public int Hunters { get; }
        readonly Squad _playerSquad;
        readonly FactionRelations _relations;

        public Jail(bool locks = false)
        {
            var hunters = SyntheticTown.Rec("56-a", FcsRecordType.Faction, "hunters");
            hunters.Ints["fundamental type"] = (int)FundamentalType.Adventurer;
            hunters.References["legal system"] = [SyntheticTown.Ref("50-a")];
            var data = ArrestTests.Town([hunters]);
            _relations = data.Relations;
            City = data.FactionIndex("50-a");
            Hunters = data.FactionIndex("56-a");
            var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths, new CombatOptions { SelfApproach = false, TickMedical = false }) { Relations = _relations };
            var carry = new CarrySystem(BaseGame, BodyOptions.Default, 1) { Relations = _relations };
            var crime = new CrimeSystem(_relations);
            combat.Crimes = crime;
            carry.Crimes = crime;
            Arrest = new ArrestSystem(crime, BaseGame, new ArrestSettings { RecordLog = true }) { Carry = carry };
            var walk = new OpenGroundWalkability((_, _) => 150);
            var systems = new List<ITickSystem>
            {
                new PlayerSystem(), new PursuitSystem(combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), carry, combat,
                new RetaliationSystem(combat), new AggroSystem(_relations), crime, Arrest,
            };
            if (locks) systems.Add(Locks = new LockSystem(BaseGame, BodyOptions.Default) { Crimes = crime });
            World = new SimWorld(new WorldSettings { Seed = 31, PublishSnapshots = false, MinPartitionSize = 1 }, walk, systems);
            World.Fixtures.RegisterSite(World, data, 0);
            _playerSquad = new Squad { Id = 1000, TemplateId = "p", Faction = _relations.PlayerFaction };
            World.Squads.Add(_playerSquad);
            CageA = World.Fixtures.Find("0.0:cageA")!;
            CageB = World.Fixtures.Find("0.0:cageB")!;
        }

        public void Dispose() => World.Dispose();

        public CharacterCold Cold(CharacterId id) => World.Characters.Cold(id.Slot)!;
        public Vector3 Where(CharacterId id) => World.Characters.Previous[id.Slot].Position;

        /// <summary>A policeman of the city (<paramref name="mind"/>: the AI holds him, so a lock round may take him) or any NPC of <paramref name="faction"/> and class.</summary>
        public CharacterId Npc(Vector2 at, int faction, FundamentalType type, Combatant stats, bool mind = false, int bounty = 0, Vector2? face = null)
        {
            var cold = stats.MakeCold();
            cold.Race = Person;
            cold.Faction = faction;
            cold.NpcClass = type;
            var squad = new Squad { Id = World.Squads.NextId(), TemplateId = "t", Faction = faction, Town = 0, HomeCentre = new Vector2(Centre.X, Centre.Z), HomeRadius = 500 };
            World.Squads.Add(squad);
            cold.SquadId = squad.Id;
            if (mind) cold.Ai = new AiMind { Squad = squad };
            if (bounty > 0) cold.Bounties = new BountyLedger();
            if (bounty > 0) cold.Bounties!.Get(City, "city").Amount = bounty;
            float yaw = face is { } f ? MathF.Atan2(f.X - at.X, f.Y - at.Y) : 0;
            var id = World.Characters.Spawn(new CharacterHot { Position = Centre + new Vector3(at.X, 0, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Yaw = yaw, Task = (byte)CharacterTask.Idle }, cold, 0);
            squad.Members.Add(id);
            squad.Leader = id;
            return id;
        }

        /// <summary>A player character, wanted by the city for <paramref name="bounty"/> when that is above 0.</summary>
        public CharacterId Hero(Vector2 at, string name, Combatant stats, int bounty = 0)
        {
            var cold = stats.MakeCold(name);
            cold.Race = Person;
            cold.Faction = _relations.PlayerFaction;
            cold.IsPlayer = true;
            cold.NpcClass = FundamentalType.Adventurer;
            cold.SquadId = _playerSquad.Id;
            if (bounty > 0) (cold.Bounties = new BountyLedger()).Get(City, "city").Amount = bounty;
            var id = World.Characters.Spawn(new CharacterHot { Position = Centre + new Vector3(at.X, 0, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Yaw = MathF.PI }, cold, 0);
            _playerSquad.Members.Add(id);
            return id;
        }

        public void Say(SimCommand c) => World.Commands.Enqueue(c);

        /// <summary>Runs up to <paramref name="max"/> ticks until <paramref name="done"/>; the tick it became true (the check is after each tick), or fails.</summary>
        public long RunUntil(Func<bool> done, int max, string what)
        {
            for (int t = 0; t < max; t++)
            {
                World.RunTick();
                if (done()) return World.Tick;
            }
            throw new Xunit.Sdk.XunitException($"{what} did not happen within {max} ticks");
        }
    }

    static Combatant Strong => new() { Attack = 70, Strength = 70 };
    static Combatant Weak => new() { Attack = 20, Defence = 40, Dodge = 40, Toughness = 50 };

    // ------------------------------------------------------------------ the release round

    [Fact]
    public void A_served_prisoner_is_freed_by_a_policeman_who_walks_to_the_cage_and_unlocks_it_not_by_the_fallback()
    {
        using var j = new Jail(locks: true);
        var police = j.Npc(new Vector2(-100, 0), j.City, FundamentalType.LawEnforcement, Strong, mind: true);
        // Only a holder of goal 211 frees prisoners (FUN_1408cf3c0): in every role list, always in its hours.
        var free = new AiGoal(new AiTask("t", "free", TaskType.PoliceFreePrisonersWhenDone, TaskPriority.Urgent, TaskTargetType.Specific, TaskEndEvent.Nothing), 0, 100, 1);
        var board = new Blackboard([new(0, new AiPackage { Id = "police", Name = "police", Roles = [[free], [free], [free], [], []] })]);
        board.Update();
        j.World.Squads.Find(j.Cold(police).SquadId)!.Ai = board;
        var hero = j.Hero(new Vector2(200, 30), "hero", Weak, bounty: 5000);
        Assert.True(j.World.Fixtures.Imprison(j.World, hero, j.CageA));
        Assert.True(j.CageA.Lock!.Locked);
        // The sentence (100 hours for 5000) is served: 1 hour from game hour 0.
        j.Cold(hero).SentenceStart = 0;
        j.Cold(hero).SentenceLength = 1;
        var start = j.Where(police);

        bool round = false;
        var at = Vector3.Zero;
        long freed = j.RunUntil(() =>
        {
            round |= j.Locks!.ActiveRounds.Any(r => r.Kind == RoundKind.Release && r.Worker == police);
            at = j.Where(police);
            return !j.Cold(hero).IsImprisoned;
        }, 1500, "the release");

        // A round of the policeman, after a walk of about 250 units (the system's fallback frees at the first sweep, tick 0, or only after 120 s).
        Assert.True(round);
        Assert.InRange(freed, 300, (long)(LockSystem.ReleaseTimeout / j.World.TickSeconds) - 1);
        Assert.True(Vector2.Distance(new Vector2(start.X, start.Z), new Vector2(at.X, at.Z)) > 150);
        Assert.True(Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(j.CageA.Position.X, j.CageA.Position.Z)) <= j.CageA.UseRange + LockSystem.Reach + 1);
        Assert.False(j.CageA.Lock.Locked);
        Assert.True(j.CageA.Occupant.IsNone);
        Assert.Null(j.Cold(hero).Bounties?.Of(j.City));
    }

    // ------------------------------------------------------------------ lifting out of a cage

    [Fact]
    public void Lifting_a_prisoner_out_of_another_factions_cage_is_refused_while_it_is_locked_and_then_books_the_lift_and_the_escape()
    {
        using var j = new Jail();
        var prisoner = j.Hero(new Vector2(200, 0), "prisoner", Weak);
        j.Cold(prisoner).Medical!.KnockOut(100000);   // an awake body is set down again at once (CarrySystem), so the lifted one is out cold
        var lifter = j.Hero(new Vector2(200, 30), "lifter", Weak);
        Assert.True(j.World.Fixtures.Imprison(j.World, prisoner, j.CageA));
        j.CageA.Lock!.Level = 50;   // locked (by the caging) and a level that holds it
        Assert.True(j.CageA.Lock.EffectivelyLocked);

        j.Say(new PickUpOrder([lifter], prisoner) { Tick = j.World.Tick });
        j.World.RunTicks(90);
        Assert.True(j.Cold(lifter).Carrying.IsNone);
        Assert.True(j.Cold(prisoner).IsImprisoned);
        Assert.Null(j.Cold(lifter).Pending);

        j.World.Fixtures.SetLocked(j.CageA, false);
        j.Say(new PickUpOrder([lifter], prisoner) { Tick = j.World.Tick });
        j.RunUntil(() => j.Cold(lifter).Carrying == prisoner, 300, "the lift");
        // The lifter is booked for code 7 (against the cage owner); the freed one gets no crime (its code 0xc is set while still held, which the setter refuses) but is an escaped prisoner.
        Assert.Equal(CrimeCode.Terrorism, j.Cold(lifter).Pending?.Code);
        Assert.Equal(7, (int)j.Cold(lifter).Pending!.Code);
        Assert.Equal(j.City, j.Cold(lifter).Pending!.VictimFaction);
        Assert.Null(j.Cold(prisoner).Pending);
        Assert.True(j.Cold(prisoner).Escaped);
        Assert.False(j.Cold(prisoner).IsImprisoned);
        Assert.True(j.CageA.Occupant.IsNone);
    }

    // ------------------------------------------------------------------ the claim and the bail

    [Fact]
    public void A_wanted_captive_put_in_a_lawful_cage_pays_its_bounty_once_and_its_bail_is_twice_the_bounty_and_frees_it_without_a_crime()
    {
        using var j = new Jail();
        var hero = j.Hero(new Vector2(150, 0), "hero", Weak);
        var captive = j.Npc(new Vector2(150, 30), j.City, FundamentalType.Civilian, new Combatant(), bounty: 1500);
        j.Cold(captive).Medical!.KnockOut(100000);
        j.World.Player.Money = 0;

        j.Say(new PickUpOrder([hero], captive) { Tick = j.World.Tick });
        j.RunUntil(() => j.Cold(hero).Carrying == captive, 300, "the lift");
        j.Say(new PutInCageOrder([hero]) { Tick = j.World.Tick });
        j.RunUntil(() => j.Arrest.Log.Any(e => e.Kind == ArrestEventKind.Claimed), 600, "the claim");

        Assert.Equal(captive, j.CageA.Occupant);
        Assert.Equal(1500, j.World.Player.Money);
        Assert.True(j.Cold(captive).Bounties!.Of(j.City)!.Claimed);
        Assert.Equal(0, j.Arrest.Claim(j.World, hero.Slot, captive, j.CageA));   // the second claim pays nothing
        Assert.Equal(1500, j.World.Player.Money);

        // Bail: twice the bounty; for an NPC of at least 2000 (a 700 bounty in the other cage).
        Assert.Equal(3000, j.Arrest.BailFor(j.World, j.Cold(captive)));
        var small = j.Npc(new Vector2(-200, 30), j.City, FundamentalType.Civilian, new Combatant(), bounty: 700);
        Assert.True(j.World.Fixtures.Imprison(j.World, small, j.CageB));
        Assert.Equal(ArrestSystem.MinNpcBail, j.Arrest.BailFor(j.World, j.Cold(small)));

        j.World.Player.Money = 5000;
        j.Say(new PayBailOrder(captive) { Tick = j.World.Tick });
        j.World.RunTicks(2);
        Assert.Equal(2000, j.World.Player.Money);
        Assert.Null(j.Cold(captive).Bounties);
        Assert.False(j.Cold(captive).IsImprisoned);
        Assert.False(j.Cold(captive).Escaped);
        Assert.Null(j.Cold(captive).Pending);
        Assert.True(j.CageA.Occupant.IsNone);
        Assert.Contains(j.Arrest.Log, e => e.Kind == ArrestEventKind.Bailed && e.Amount == 3000);
    }

    // ------------------------------------------------------------------ bounty hunters

    [Fact]
    public void A_bounty_hunter_who_spots_a_character_with_a_big_bounty_attacks_until_it_is_down_then_leaves_it_alone_and_cages_nobody()
    {
        using var j = new Jail();
        var hero = j.Hero(new Vector2(0, 0), "hero", Weak, bounty: 5000);
        j.Cold(hero).RecentlyWanted = CrimeSystem.RecentlyWantedSeconds;   // just booked: recognised at once
        var hunter = j.Npc(new Vector2(0, 60), j.Hunters, FundamentalType.Adventurer, Strong, face: Vector2.Zero);

        j.RunUntil(() => j.Arrest.Log.Any(e => e.Kind == ArrestEventKind.Hunt), 120, "the hunt");
        Assert.Equal(5000, j.Arrest.Log.First(e => e.Kind == ArrestEventKind.Hunt).Amount);
        j.RunUntil(() => j.Arrest.Log.Any(e => e.Kind == ArrestEventKind.HuntOver), 3000, "the end of the hunt");

        Assert.True(j.Cold(hero).Medical!.Incapacitated && !j.Cold(hero).Medical!.Dead);
        Assert.False(j.Cold(hero).IsImprisoned);
        Assert.DoesNotContain(j.Arrest.Log, e => e.Kind is ArrestEventKind.Arrest or ArrestEventKind.Lifted or ArrestEventKind.Jailed);
        Assert.Empty(j.Arrest.Arrests);
        Assert.Empty(j.Arrest.Cagings);
        Assert.False(j.Cold(hunter).Engaged);
        var hunt = Assert.Single(j.Arrest.Hunts);
        Assert.Equal((hunter, hero), (hunt.Hunter, hunt.Target));
        Assert.InRange(hunt.Rest, 1, new ArrestSettings().HuntRestSeconds);
    }
}
