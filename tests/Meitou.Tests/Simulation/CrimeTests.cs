using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Crimes, witnesses and bounties (docs/simulation.md "Crimes and bounties", docs/game/economy.md section 11).
/// </summary>
public class CrimeTests
{
    // Factions: 0 the city (offers bounties; its police book crimes), 1 bandits (lawless), 2 the player (Nameless), 3 farmers (allied with the city
    // both ways, book nothing), 4 wildlife (not real), 5 rivals (hostile to the city both ways).
    const int City = 0, Bandits = 1, Player = 2, Farmers = 3, Wildlife = 4, Rivals = 5;

    static readonly Vector2 O = new(2000, 2000);

    static FactionRelations Relations()
    {
        FactionData F(string id, int def = 0, (string, int)[]? rel = null, bool offers = false, FundamentalType type = FundamentalType.Civilian,
                bool notReal = false) => new()
        {
            Id = id, Name = id, DefaultRelation = def, OffersBounties = offers, FundamentalType = type, NotReal = notReal,
            Relations = (rel ?? []).ToDictionary(r => r.Item1, r => r.Item2),
        };
        var table = FactionRelations.Build([
            F("city", 0, [("farmers", 60), ("rivals", -80)], offers: true),
            F("bandits", 0, type: FundamentalType.Bandit),
            F("Nameless", 10),
            F("farmers", 0, [("city", 60)]),
            F("wildlife", -100, notReal: true),
            F("rivals", 0, [("city", -80)]),
        ]);
        table.PlayerFaction = Player;
        return table;
    }

    // ------------------------------------------------------------------ the tables

    [Theory]
    [InlineData(CrimeCode.Murder, 5000)]
    [InlineData((CrimeCode)17, 500)]
    public void The_crime_table_pays_the_games_amount_and_an_unknown_code_the_default(CrimeCode code, int amount) => Assert.Equal(amount,
            CrimeTable.Amount(code));

    // ------------------------------------------------------------------ worlds

    sealed class Town : IDisposable
    {
        public SimWorld World { get; }
        public CombatSystem Combat { get; }
        public AggroSystem Aggro { get; }
        public CarrySystem Carry { get; }
        public CrimeSystem Crime { get; }
        public ArrestSystem Arrest { get; }
        public FactionRelations Relations { get; }

        public Town(ulong seed = 7, int threads = 1, int minPartition = 1)
        {
            Relations = CrimeTests.Relations();
            Combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths,
                    new CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = true })
            {
                Relations = Relations,
            };
            Aggro = new AggroSystem(Relations, new AggroSettings { RecordLog = true });
            Carry = new CarrySystem(BaseGame, BodyOptions.Default, 1) { Relations = Relations };
            Crime = new CrimeSystem(Relations, new CrimeSettings { RecordLog = true });
            Combat.Crimes = Crime;
            Carry.Crimes = Crime;
            Arrest = new ArrestSystem(Crime, BaseGame, new ArrestSettings { RecordLog = true }) { Carry = Carry };
            var walk = new OpenGroundWalkability((_, _) => 150);
            World = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false, MinPartitionSize = minPartition },
                    walk,
                [new PlayerSystem(), new PursuitSystem(Combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), Carry,
                        Combat,
                 new RetaliationSystem(Combat), Aggro, Crime, Arrest]);
        }

        public void Dispose() => World.Dispose();

        /// <summary>
        /// An idle NPC alone in its squad, facing <paramref name="face"/> (null: along +Z).
        /// </summary>
        public CharacterId Npc(int faction, Vector2 at, Vector2? face = null, FundamentalType type = FundamentalType.Civilian, string name = "npc")
        {
            var cold = new Combatant { Name = name }.MakeCold();
            cold.Faction = faction;
            cold.NpcClass = type;
            cold.Inventory = new Inventory();
            var squad = new Squad { Id = World.Squads.NextId(), TemplateId = "t", Faction = faction, HomeCentre = at, HomeRadius = 20 };
            World.Squads.Add(squad);
            cold.SquadId = squad.Id;
            float yaw = face is { } f ? MathF.Atan2(f.X - at.X, f.Y - at.Y) : 0;
            var id = World.Characters.Spawn(new CharacterHot { Position = new Vector3(at.X, 150, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15,
                    Yaw = yaw, Task = (byte)CharacterTask.Idle }, cold, 0);
            squad.Members.Add(id);
            squad.Leader = id;
            return id;
        }

        public CharacterId PlayerCharacter(Vector2 at, string name = "hero")
        {
            var cold = new Combatant { Name = name }.MakeCold();
            cold.Faction = Player;
            cold.IsPlayer = true;
            cold.NpcClass = FundamentalType.Adventurer;
            cold.Inventory = new Inventory();
            var squad = World.Squads.Find(1000) ?? AddSquad();
            cold.SquadId = squad.Id;
            var id = World.Characters.Spawn(new CharacterHot { Position = new Vector3(at.X, 150, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15 },
                    cold, 0);
            squad.Members.Add(id);
            if (squad.Leader.IsNone) squad.Leader = id;
            return id;

            Squad AddSquad()
            {
                var s = new Squad { Id = 1000, TemplateId = "p", Faction = Player };
                World.Squads.Add(s);
                return s;
            }
        }

        public CharacterCold Cold(CharacterId id) => World.Characters.Cold(id.Slot)!;
        public int Bounty(CharacterId id, int faction) => Cold(id).Bounties?.AmountFor(faction) ?? 0;
        public void Say(SimCommand c) => World.Commands.Enqueue(c);
        public IEnumerable<CrimeEvent> Events(CrimeEventKind kind) => Crime.Log.Where(e => e.Kind == kind);
    }

    /// <summary>
    /// Only Attack Unprovoked (row 61) books an Assault and marks the attacker ordered (<c>Task_FocusedMeleeAttack</c>'s flag, docs/game/combat.md);
    /// Attack Target (row 5) does neither. Catches every picked attack booking.
    /// </summary>
    [Fact]
    public void Attack_unprovoked_books_an_assault_and_marks_the_attacker_ordered_but_attack_target_does_not()
    {
        foreach (bool unprovoked in new[] { false, true })
        {
            using var t = new Town();
            var me = t.PlayerCharacter(O);
            var victim = t.Npc(City, O + new Vector2(0, 20), name: "victim");
            t.Say(new AttackOrder([me], victim) { Tick = t.World.Tick, Picked = true, Unprovoked = unprovoked });
            t.World.RunTicks(10);

            Assert.Equal(unprovoked, t.Cold(me).Pending?.Code == CrimeCode.Assault);
            Assert.Equal(unprovoked, t.Cold(me).Ordered);
        }
    }

    // ------------------------------------------------------------------ witnesses

    [Fact]
    public void A_crime_seen_by_the_police_of_a_faction_that_offers_bounties_is_booked_and_one_they_did_not_see_is_forgotten()
    {
        using var t = new Town();
        var me = t.PlayerCharacter(O);
        var victim = t.Npc(City, O + new Vector2(0, 20), name: "victim");
        t.Npc(City, O + new Vector2(0, 250), face: O, type: FundamentalType.LawEnforcement, name: "officer");
        t.Say(new AttackOrder([me], victim) { Tick = t.World.Tick, Picked = true, Unprovoked = true });
        t.World.RunTicks(40);

        Assert.Equal(500, t.Bounty(me, City));

        var entry = t.Cold(me).Bounties!.Of(City)!;

        Assert.Equal(1 << (int)CrimeCode.Assault, entry.Crimes);
        Assert.Equal("city", entry.FactionName);
        Assert.Contains(t.World.TakeMessages(), m => m.Contains("wanted by city for Assault: bounty c.500"));

        // Booked once: more sweeps while the same crime is pending add nothing.
        t.World.RunTicks(60);

        Assert.Single(t.Events(CrimeEventKind.Booked));

        // Police looking the other way, out of sight: the loot of a knocked-out townsman is pending and then forgotten, with no bounty.
        using var u = new Town();
        var me2 = u.PlayerCharacter(O);
        var down2 = u.Npc(City, O + new Vector2(0, 20));
        u.Cold(down2).Inventory!.Add(new ItemInstance { Record = "bread", Name = "bread" });
        u.Cold(down2).Medical!.KnockOut(100000);
        u.Npc(City, O + new Vector2(0, 250), face: O + new Vector2(0, 600), type: FundamentalType.LawEnforcement);
        u.World.RunTicks(2);
        u.Say(new LootOrder([me2], down2) { Tick = u.World.Tick });
        u.World.RunTicks(30);

        Assert.Equal(CrimeCode.Looting, u.Cold(me2).Pending?.Code);

        u.World.RunTicks(150);

        Assert.Null(u.Cold(me2).Pending);
        Assert.Null(u.Cold(me2).Bounties);
        Assert.Empty(u.Events(CrimeEventKind.Witnessed));
    }

    /// <summary>
    /// The witness function (<c>FUN_140854d10</c>) ends in the booking call with no player test: a police witness books an NPC culprit's crime too.
    /// </summary>
    [Fact]
    public void A_crime_of_an_npc_culprit_seen_by_the_police_is_booked()
    {
        using var t = new Town();
        var thug = t.Npc(Rivals, O, name: "thug");
        var victim = t.Npc(City, O + new Vector2(0, 20), name: "victim");
        t.Npc(City, O + new Vector2(0, 250), face: O, type: FundamentalType.LawEnforcement, name: "officer");

        Assert.True(t.Crime.SetPending(t.World, thug.Slot, CrimeCode.Assault, City, t.World.Characters.IdOf(victim.Slot), CrimeSystem.MinTimer));

        t.World.RunTicks(40);

        Assert.Single(t.Events(CrimeEventKind.Booked));
    }

    // ------------------------------------------------------------------ determinism

    /// <summary>
    /// A market: city police and townsfolk, farmers, a rival squad, and two player characters. At tick 30 the first assaults a townsman in view of
    /// the police (booked), the police go after it; at tick 300 the second loots a knocked-out farmer next to a policeman; hashes at six checkpoints,
    /// pinned in <see cref="GoldenHashTests"/>.
    /// </summary>
    internal static List<ulong> Scripted(int threads)
    {
        using var t = new Town(13, threads);
        var me = t.PlayerCharacter(O);
        var other = t.PlayerCharacter(O + new Vector2(2500, 0), "second");
        var townsman = t.Npc(City, O + new Vector2(0, 25));
        t.Npc(City, O + new Vector2(30, 200), face: O, type: FundamentalType.LawEnforcement);
        t.Npc(City, O + new Vector2(-30, 220), face: O, type: FundamentalType.LawEnforcement);
        t.Npc(City, O + new Vector2(-200, 0), face: O);
        var farmer = t.Npc(Farmers, O + new Vector2(2500, 20));
        t.Cold(farmer).Inventory!.Add(new ItemInstance { Record = "bread", Name = "bread", Quantity = 2 });
        t.Cold(farmer).Medical!.KnockOut(1000000);
        t.Npc(City, O + new Vector2(2520, 220), face: O + new Vector2(2500, 0), type: FundamentalType.LawEnforcement);
        t.Npc(Rivals, O + new Vector2(-1200, 1200), face: O + new Vector2(-1200, 0));
        var hashes = new List<ulong>();
        foreach (int target in new[] { 1, 30, 120, 300, 400, 900 })
        {
            t.World.RunTicks(target - (int)t.World.Tick);
            if (target == 30)
                t.Say(new AttackOrder([me], townsman) { Tick = t.World.Tick, Picked = true, Unprovoked = true });
            if (target == 300) t.Say(new LootOrder([other], farmer) { Tick = t.World.Tick });
            hashes.Add(t.World.StateHash());
        }
        var seen = (Booked: t.Events(CrimeEventKind.Booked).Count(), Witnessed: t.Events(CrimeEventKind.Witnessed).Count(),
            Fights: t.Aggro.Log.Count + t.Arrest.Log.Count(e => e.Kind is ArrestEventKind.Attack or ArrestEventKind.Arrest));

        // It covers what it is for: crimes booked (the assault and the loot), witnessed, and the police starting fights over a bounty.
        Assert.True(seen.Booked >= 2 && seen.Witnessed >= 2 && seen.Fights > 0, $"{seen}");
        Assert.NotEqual(hashes[^1], hashes[^2]);

        return hashes;
    }
}

/// <summary>
/// The crime rules over the installed game's data: who books bounties, who is police in the towns, and the police going after a wanted player
/// character. Skipped without the game.
/// </summary>
[Slow]
public class CrimeInstallTests(ITestOutputHelper output)
{
    [Fact]
    public void Town_police_of_the_factions_that_book_bounties_go_after_a_wanted_character()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        var db = InstallData.BaseGame!;
        var data = PopulationData.Create(db, InstallData.Levels!.Towns(), new GeneratedAppearances(db, InstallData.Install!.Root));

        // The factions that book (the loader's +0x40: law enforcement, or offers bounties).
        var books = data.Factions.Where(f => f.BooksBounties).Select(f => f.Name).OrderBy(n => n).ToList();
        output.WriteLine("book bounties: " + string.Join(", ", books));

        Assert.Equal(["Mercenary Police", "Police", "Republic of Tertius", "Shek Kingdom", "The Holy Nation", "United Cities"], books);

        var start = NewGameStart.Find(NewGameStart.LoadAll(data.Db), NewGameStart.DefaultName)!;
        string record = "";
        foreach (var link in start.Squad)
        {
            if (data.Db.Find(link.Id) is not { } r) continue;
            if (!SquadFactory.IsTemplate(r))
            {
                record = link.Id;
                break;
            }
            if (SquadFactory.Plan(data.Db, SquadTemplate.From(r), 1, 0, 0, IWorldStates.None).Members.FirstOrDefault(m => !m.IsAnimal) is { } member)
            {
                record = member.RecordId;
                break;
            }
        }
        int found = 0;
        foreach (string town in new[] { "Admag", "Stack", "Heng", "Blister Hill", "Bad Teeth" })
        {
            if (data.Sites.FirstOrDefault(s => s.Town.Name == town) is not { } site) continue;
            var walk = new OpenGroundWalkability((_, _) => 300);
            var systems = StandardSystems.Build(data, walk, new StandardSystemOptions
            {
                Parts = StandardParts.Population | StandardParts.Player | StandardParts.Movement | StandardParts.Pursuit
                    | StandardParts.Body | StandardParts.Combat
                    | StandardParts.Retaliation | StandardParts.Aggro | StandardParts.Crime,
                SynchronousPaths = true,
                Population = new PopulationSettings { CheckEveryTicks = 1 },
                Combat = new CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = true },
                Aggro = new AggroSettings { RecordLog = true },
                Arrest = new ArrestSettings { RecordLog = true },
            });
            using var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, walk, systems.Systems);
            world.Commands.Enqueue(new FocusCommand(site.Position) { Tick = 0 });
            world.RunTicks(60);
            var table = world.Characters;
            var police = Enumerable.Range(0, table.HighWater)
                .Where(s => table.Previous[s].Alive && systems.Crime!.IsPoliceLike(world, table.Cold(s)!))
                .ToList();
            var wantedBy = police.Select(s => table.Cold(s)!.Faction).Where(f => data.Factions[f].BooksBounties).Distinct().ToList();
            output.WriteLine($"{town}: {police.Count} police-like "
                + $"({string.Join(", ", police.Select(s => $"{table.Cold(s)!.Name} {table.Cold(s)!.NpcClass}").Distinct().Take(6))})");
            if (wantedBy.Count == 0) continue;
            found++;

            // A wanted player character right beside the first such policeman, in front of it.
            int officer = police.First(s => table.Cold(s)!.Faction == wantedBy[0]);
            var at = table.Previous[officer].Position;
            float yaw = table.Previous[officer].Yaw;
            var spot = new Vector2(at.X + MathF.Sin(yaw) * 60, at.Z + MathF.Cos(yaw) * 60);
            var me = systems.Population!.SpawnSandbox(world, record, spot, player: true);
            var entry = (table.Cold(me.Slot)!.Bounties = new BountyLedger()).Get(wantedBy[0], data.Factions[wantedBy[0]].Name);
            entry.Amount = 5000;
            entry.RaisedTick = world.Tick;
            table.Cold(me.Slot)!.RecentlyWanted = CrimeSystem.RecentlyWantedSeconds;   // just booked: recognised at once
            world.RunTicks(60);
            var attackers = systems.Arrest!.Log.Where(e => e.Target == me && e.Kind is ArrestEventKind.Attack or ArrestEventKind.Arrest).ToList();
            output.WriteLine($"  wanted by {data.Factions[wantedBy[0]].Name}: gone after by {attackers.Count} "
                + $"({string.Join(", ", attackers.Select(e => $"{table.Cold(e.Guard.Slot)?.Name} {e.Kind}"))})");

            Assert.NotEmpty(attackers);
            Assert.Contains(attackers, e => e.Faction == wantedBy[0]);
        }

        Assert.True(found > 0, "some town has police of a faction that books bounties");
    }
}
