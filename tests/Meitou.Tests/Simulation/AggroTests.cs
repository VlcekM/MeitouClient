using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Faction relations driving fights: the relation table's rules, the auto-aggro, the chase give-up, the player's self-defence and deeds.</summary>
public class AggroTests
{
    // Factions: 0 raiders, 1 villagers (raiders hate them), 2 nomads (raiders hate them but coexist), 3 friends (allied to the raiders),
    // 4 hermits (the raiders' own list says hostile, only the hermits list the raiders as coexisting: attacked), 5 the player (Nameless, no table of its own),
    // 6 wildlife (not real), 7 townsfolk (neutral to everyone, the player's victims).
    const int Raiders = 0, Villagers = 1, Nomads = 2, Friends = 3, Hermits = 4, Player = 5, Wildlife = 6, Townsfolk = 7;

    static FactionRelations Relations()
    {
        FactionData F(string id, int def = 0, (string, int)[]? rel = null, string[]? coexist = null, bool notReal = false) => new()
        {
            Id = id, Name = id, DefaultRelation = def, NotReal = notReal,
            Relations = (rel ?? []).ToDictionary(r => r.Item1, r => r.Item2),
            Coexistence = coexist ?? [],
        };
        var table = FactionRelations.Build([
            F("raiders", 0, [("villagers", -80), ("nomads", -80), ("friends", 70), ("hermits", -80), ("Nameless", -60)], ["nomads"]),
            F("villagers"),
            F("nomads"),
            F("friends", 0, [("raiders", 70)]),
            F("hermits", 0, null, ["raiders"]),
            F("Nameless", 10, [("raiders", 40)]),
            F("wildlife", -100, notReal: true),
            F("townsfolk", 10),
        ]);
        table.PlayerFaction = Player;
        return table;
    }

    // ------------------------------------------------------------------ worlds

    sealed class Arena(SimWorld world, CombatSystem combat, AggroSystem aggro, FactionRelations relations) : IDisposable
    {
        public SimWorld World { get; } = world;
        public CombatSystem Combat { get; } = combat;
        public AggroSystem Aggro { get; } = aggro;
        public FactionRelations Relations { get; } = relations;
        public void Dispose() => World.Dispose();

        public Squad Squad(int faction, Vector2 home, params Vector2[] at) => FacingSquad(faction, home, null, at);

        /// <summary>A squad whose members face <paramref name="face"/> (null: yaw 0, along +Z); the original's NPCs see only ahead (the view cone) and hear only movers.</summary>
        public Squad FacingSquad(int faction, Vector2 home, Vector2? face, params Vector2[] at) =>
            SquadOf(faction, home, false, [.. at.Select(p => (p, face is { } f ? MathF.Atan2(f.X - p.X, f.Y - p.Y) : 0f))]);

        /// <summary>A squad with each member's position and yaw; <paramref name="idle"/> makes them stand (no wandering, so they make no noise).</summary>
        public Squad SquadOf(int faction, Vector2 home, bool idle, params (Vector2 At, float Yaw)[] members) => SquadOf(faction, home, idle, -1, members);

        /// <summary>
        /// The active half of a roaming platoon that runs <paramref name="package"/> (the population's stand-in; the AI does not run its squad), its members standing and facing along +Z.
        /// </summary>
        public Squad Roamers(int faction, string package, Vector2 home, params Vector2[] at)
        {
            var platoon = new Platoon { Id = World.Platoons.NextId(), Origin = 0, TemplateId = "t", Key = 1, Faction = faction, Package = package, State = PlatoonState.Loaded };
            World.Platoons.Add(platoon);
            var squad = SquadOf(faction, home, true, platoon.Id, [.. at.Select(p => (p, 0f))]);
            platoon.SquadId = squad.Id;
            return squad;
        }

        /// <summary>A town's own squad that holds <paramref name="own"/> as its home, with the board the AI would give it (the AI is not run here).</summary>
        public Squad TownSquadOf(int faction, Vector2 home, Meitou.Data.World.TownBuilding own, params (Vector2 At, float Yaw)[] members)
        {
            var squad = SquadOf(faction, home, true, -1, members, town: 0);
            squad.Ai = new Meitou.Simulation.Ai.Blackboard([]) { OwnHome = own };
            return squad;
        }

        Squad SquadOf(int faction, Vector2 home, bool idle, int platoon, (Vector2 At, float Yaw)[] members, int town = -1)
        {
            var squad = new Squad { Id = World.Squads.NextId(), TemplateId = "t", Faction = faction, HomeCentre = home, HomeRadius = 30, PlatoonId = platoon, Town = town };
            World.Squads.Add(squad);
            foreach (var (p, yaw) in members)
            {
                var cold = new Combatant { Name = $"f{faction}-{squad.Members.Count}" }.MakeCold();
                cold.Faction = faction;
                cold.SquadId = squad.Id;
                cold.FactionImportance = 1;   // the most common CHARACTER value: blows on them move their faction's relations
                bool leader = squad.Members.Count == 0;
                var id = World.Characters.Spawn(new CharacterHot
                {
                    Position = new Vector3(p.X, 150, p.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Yaw = yaw,
                    Task = (byte)(idle ? CharacterTask.Idle : leader ? CharacterTask.Wander : CharacterTask.Follow), TaskTime = 1,
                }, cold, 0);
                squad.Members.Add(id);
                if (leader) squad.Leader = id;
            }
            return squad;
        }

        public CharacterId PlayerCharacter(Vector2 at)
        {
            var cold = new Combatant { Name = "player" }.MakeCold();
            cold.Faction = Player;
            cold.IsPlayer = true;
            return World.Characters.Spawn(new CharacterHot { Position = new Vector3(at.X, 150, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15 }, cold, 0);
        }

        public bool Fighting(CharacterId id) => World.Characters.Cold(id.Slot)!.InCombat;
    }

    /// <summary>Catches the dialogue events the systems fire (there is no dialogue system here).</summary>
    sealed class EventLog : ITickSystem
    {
        public readonly List<Meitou.Simulation.Dialogue.DialogueEvent> Events = [];
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands) => Events.AddRange(commands.OfType<Meitou.Simulation.Dialogue.DialogueEvent>());
        public void Hash(ref StateHasher hasher) { }
    }

    /// <summary>
    /// The arena; <paramref name="ai"/> adds the AI (first, as the standard order has it), which runs the squads given goal lists (<see cref="Lists"/>); <paramref name="flee"/> adds the flee step
    /// after the looks and gives the AI the threats (Self preservation).
    /// </summary>
    static Arena Make(ulong seed, int threads, FactionRelations? relations = null, AggroSettings? settings = null, ITickSystem? extra = null, bool ai = false, bool flee = false, bool? squadBreak = null,
        GameDatabase? db = null, AiSettings? think = null)
    {
        relations ??= Relations();
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths, new CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = true })
        {
            Relations = relations,
            Deeds = new FactionDeeds(relations),
        };
        var aggro = new AggroSystem(relations, settings ?? new AggroSettings { RecordLog = true });
        var walk = new OpenGroundWalkability((_, _) => 150);
        ITickSystem[] mind = ai ? [new AiSystem(new PopulationData { Db = db ?? new GameDatabase(), Sites = [], Factions = [], Relations = relations }, think) { ThreatSource = flee ? combat : null }] : [];
        ITickSystem[] run = flee ? [squadBreak is { } on ? new FleeSystem(relations, combat) { SquadBreak = on } : new FleeSystem(relations, combat)] : [];
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false, MinPartitionSize = 1 }, walk,
            [.. mind, new PursuitSystem(combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), combat, new RetaliationSystem(combat), aggro, .. run, .. extra is null ? Array.Empty<ITickSystem>() : [extra]]);
        return new Arena(world, combat, aggro, relations);
    }

    /// <summary>
    /// Raiders that attack on sight (goal 16) between villagers (hostile), nomads (coexisting), hermits (coexisting from their side) and friends (allied), each a squad 150 units off, whose
    /// minds hold no combat goal (no AI packages). Returns the squads in that order.
    /// </summary>
    static (Arena Arena, Squad Raiders, Squad Villagers, Squad Nomads, Squad Hermits, Squad Friends) Crossroads(ulong seed, int threads)
    {
        var a = Make(seed, threads, ai: true);
        var o = new Vector2(2000, 2000);
        var raiders = a.Squad(Raiders, o, o, o + new Vector2(0, 12));
        Lists(raiders, [Goal(TaskType.ATTACK_ENEMIES)], [Goal(TaskType.ATTACK_ENEMIES)]);
        var villagers = a.Squad(Villagers, o + new Vector2(150, 0), o + new Vector2(150, 0), o + new Vector2(160, 10));
        var nomads = a.Squad(Nomads, o + new Vector2(-150, 0), o + new Vector2(-150, 0), o + new Vector2(-160, 10));
        var hermits = a.Squad(Hermits, o + new Vector2(0, 150), o + new Vector2(0, 150));
        var friends = a.Squad(Friends, o + new Vector2(0, -150), o + new Vector2(0, -150));
        return (a, raiders, villagers, nomads, hermits, friends);
    }

    [Fact]
    public void Hostile_squads_in_sight_fight_and_nobody_else_does()
    {
        var (a, raiders, villagers, nomads, hermits, friends) = Crossroads(3, 1);
        using var _ = a;
        a.World.RunTicks(60);
        // The raiders started it: every logged fight is a raider attacking a villager or a hermit (the hermits list the raiders as coexisting, but attack on sight reads only the
        // looker's own list, FUN_1406b23c0), and both raiders took part (each by its own 16).
        Assert.NotEmpty(a.Aggro.Log);
        Assert.All(a.Aggro.Log, e => { Assert.Equal(Raiders, e.AttackerFaction); Assert.Contains(e.TargetFaction, new[] { Villagers, Hermits }); });
        Assert.All(raiders.Members, m => Assert.True(a.Fighting(m), "both raiders fight"));
        // The attacked answer (retaliation), and nobody touches the nomads or friends.
        a.World.RunTicks(60);
        Assert.Contains(villagers.Members.Concat(hermits.Members), a.Fighting);
        foreach (var m in nomads.Members.Concat(friends.Members)) Assert.False(a.Fighting(m));
        foreach (var e in a.Combat.Log)
        {
            int fa = a.World.Characters.Cold(e.Attacker)!.Faction, fd = a.World.Characters.Cold(e.Defender)!.Faction;
            Assert.True((fa, fd) is (Raiders, Villagers) or (Villagers, Raiders) or (Raiders, Hermits) or (Hermits, Raiders), $"a blow between factions {fa} and {fd}");
        }
    }

    [Fact]
    public void A_war_on_the_player_turns_a_faction_hostile_with_the_games_messages_and_its_people_then_attack()
    {
        using var a = Make(7, 1, ai: true);
        var o = new Vector2(2000, 2000);
        var town = a.Squad(Townsfolk, o, o, o + new Vector2(30, 0));
        var watch = a.Squad(Townsfolk, o + new Vector2(200, 0), o + new Vector2(200, 0));
        Lists(watch, [Goal(TaskType.ATTACK_ENEMIES)]);
        var me = a.PlayerCharacter(o + new Vector2(60, 60));
        a.World.RunTicks(20);
        Assert.Empty(a.Aggro.Log);   // neutral: 10
        // The order itself changes no relation (the game's relation events come from the blows that land).
        a.World.Commands.Enqueue(new AttackOrder([me], town.Members[0]) { Tick = a.World.Tick });
        a.World.RunTick();
        Assert.Equal(10, a.Relations.Get(Townsfolk, Player));
        Assert.Empty(a.World.TakeMessages());
        // The townsfolk declare war on the player: -35, hostile, and the game's messages.
        PlayerRelations.DeclareWar(a.World, a.Relations, Townsfolk, Player);
        Assert.Equal(-35, a.Relations.Get(Townsfolk, Player));
        Assert.Equal(["War breaks out between townsfolk and Nameless", "townsfolk are now hostile towards you"], a.World.TakeMessages());
        // Now another squad of theirs, which nobody attacked, picks the fight with the player by itself (its 16).
        a.World.RunTicks(30);
        Assert.Contains(a.Aggro.Log, e => e.Attacker == watch.Members[0] && e.Target == me);
    }

    /// <summary>
    /// The original's chase give-up (<c>ChaseMode</c>, docs/game/combat.md "Leaving a fight"): a raider chasing a runner that gets out of its sight and hearing stops once the
    /// gate and 24 s without a sense of it have passed, saying event 60 GIVE_UP_CHASE to it; one whose runner stays in view keeps on.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_chaser_gives_up_a_target_it_no_longer_senses_and_keeps_one_it_sees(bool escapes)
    {
        var log = new EventLog();
        using var a = Make(5, 1, extra: log);
        a.Aggro.Combat = a.Combat;   // as the standard systems wire it: the chase reads the fight target
        var o = new Vector2(2000, 2000);
        var raider = a.SquadOf(Raiders, o, true, (o, 0f)).Members[0];
        var runner = a.PlayerCharacter(o + new Vector2(0, 200));
        a.World.Characters.Cold(runner.Slot)!.Orders |= CharacterOrders.Passive;   // it never turns to fight back
        a.World.Commands.Enqueue(new AttackOrder([raider], runner) { Tick = a.World.Tick });
        a.World.Commands.Enqueue(new MoveOrder([runner], new Vector3(o.X, 150, o.Y + 20000)) { Tick = a.World.Tick });
        a.World.RunTicks(60);
        Assert.True(a.World.Characters.Cold(raider.Slot)!.CombatRunning, "the raider runs after it");
        // Escaping: the runner is put far ahead, beyond any sight or noise range, and is ordered onto the raider: as its attacker it stays a candidate of the raider's target choice
        // (docs/game/combat.md "Target selection"; one that only ran would be dropped at the next choice), so only the give-up lets it go.
        if (escapes)
        {
            a.World.Characters.Place(runner.Slot, new Vector3(o.X, 150, o.Y + 9000), 0);
            a.World.Commands.Enqueue(new AttackOrder([runner], raider) { Tick = a.World.Tick, Picked = true });
        }
        long from = a.World.Tick;
        // Up to the first give-up (the runner, still attacking, is taken on again later), and the two ticks its stop takes.
        for (int k = 0; k < (int)(60 / a.World.TickSeconds) && !log.Events.Any(e => e.Event == 60); k++) a.World.RunTick();
        a.World.RunTicks(2);
        var gaveUp = log.Events.Where(e => e.Event == 60).ToList();
        if (escapes)
        {
            var e = Assert.Single(gaveUp);
            Assert.Equal((raider, runner), (e.Npc, e.Target));
            // 24 s after the last look that sensed it, which came up to one look interval before it vanished.
            float after = (e.Tick - from) * a.World.TickSeconds, look = new AggroSettings().ScanEveryTicks * a.World.TickSeconds;
            Assert.InRange(after, AggroSystem.GiveUpSenseSeconds - look, AggroSystem.GiveUpSenseSeconds + look);
            Assert.NotEqual((byte)CharacterTask.Attack, a.World.Characters.Next[raider.Slot].Task);
        }
        else
        {
            Assert.Empty(gaveUp);
            Assert.Equal((byte)CharacterTask.Attack, a.World.Characters.Next[raider.Slot].Task);
        }
    }

    /// <summary>
    /// The chase lead with solid bodies (docs/game/combat.md "Combat stance", Meitou): a raider faster than its runner passes it beside its track and lands a blow
    /// within a few seconds of catching up, instead of pushing it along or stopping beside it out of reach of its led aim point.
    /// </summary>
    [Fact]
    public void A_faster_chaser_passes_its_runner_and_lands_a_blow()
    {
        using var a = Make(5, 1);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var raider = a.SquadOf(Raiders, o, true, (o, 0f)).Members[0];
        var runner = a.PlayerCharacter(o + new Vector2(0, 100));   // dead ahead: the pass must not need a sideways start
        var table = a.World.Characters;
        table.Cold(runner.Slot)!.Orders |= CharacterOrders.Passive;
        table.Cold(runner.Slot)!.SpeedOverride = 55;   // slower than the raider's run
        a.World.Commands.Enqueue(new AttackOrder([raider], runner) { Tick = a.World.Tick });
        a.World.Commands.Enqueue(new MoveOrder([runner], new Vector3(o.X, 150, o.Y + 20000)) { Tick = a.World.Tick });
        long caught = -1;
        for (int k = 0; k < (int)(60 / a.World.TickSeconds); k++)
        {
            a.World.RunTicks(1);
            var d = table.Next[runner.Slot].Position - table.Next[raider.Slot].Position;
            if (caught < 0 && new Vector2(d.X, d.Z).Length() < 20) caught = a.World.Tick;
            if (a.Combat.StateOf(raider) is { AttackSeq: > 0, LastOutcome: BlowOutcome.Hit }) break;
        }
        Assert.True(caught >= 0, "the raider catches up");
        Assert.True(a.Combat.StateOf(raider) is { AttackSeq: > 0, LastOutcome: BlowOutcome.Hit }, "a blow lands");
        Assert.InRange((a.World.Tick - caught) * a.World.TickSeconds, 0, 5);
    }

    /// <summary>Raiders (0) and the player's Nameless (1), enemies (the player's relations are in the other side's table).</summary>
    static FactionRelations OneSided()
    {
        var table = FactionRelations.Build([
            new FactionData { Id = "raiders", Name = "raiders", Relations = new Dictionary<string, int> { ["Nameless"] = -80 } },
            new FactionData { Id = "Nameless", Name = "Nameless", Relations = new Dictionary<string, int>() },
        ]);
        table.PlayerFaction = 1;
        return table;
    }

    /// <summary>Makes a raider a raiders' slave: it looks for no fight, yet stays a target for the raiders' enemies.</summary>
    static void Docile(Arena a, CharacterId raider) => (a.World.Characters.Cold(raider.Slot)!.SlaveState, a.World.Characters.Cold(raider.Slot)!.SlaveOwner) = (1, 0);

    /// <summary>
    /// Hold (docs/game/combat.md "Fight lifecycle and orders", perception.md "The attack-on-sight tests"): an idle holder attacks on sight only targets its post test takes, so a
    /// raider standing 50 away is left alone while one walking past within 80 of the hold point is attacked.
    /// </summary>
    [Fact]
    public void A_holder_takes_on_a_raider_passing_its_post_but_not_one_standing_off()
    {
        using var a = Make(4, 1, OneSided());
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var me = a.World.Characters.Spawn(new CharacterHot { Position = new Vector3(o.X, 150, o.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15 },
            new Combatant { Name = "holder" }.MakeCold(), 0);
        var cold = a.World.Characters.Cold(me.Slot)!;
        (cold.Faction, cold.IsPlayer, cold.Orders, cold.HoldPoint) = (1, true, CharacterOrders.Hold, new Vector3(o.X, 150, o.Y));
        var stander = a.SquadOf(0, o, true, (o + new Vector2(0, 50), MathF.PI)).Members[0];
        Docile(a, stander);
        a.World.RunTicks((int)(3 / a.World.TickSeconds));
        Assert.False(a.Fighting(me), "a raider standing 50 away fails the post test (standing targets within 40 only)");
        // A raider walking across in front, 60 from the hold point at its nearest.
        var walker = a.SquadOf(0, o, true, (o + new Vector2(-300, 60), 0f)).Members[0];
        Docile(a, walker);
        a.World.Commands.Enqueue(new MoveOrder([walker], new Vector3(o.X + 300, 150, o.Y + 60)) { Tick = a.World.Tick });
        for (int k = 0; k < (int)(10 / a.World.TickSeconds) && !a.Fighting(me); k++) a.World.RunTicks(1);
        Assert.True(a.Fighting(me), "the walker passing within 80 of the hold point is attacked");
        Assert.Equal(walker.Slot, a.Combat.StateOf(me).TargetSlot);
        Assert.NotEqual(stander.Slot, a.Combat.StateOf(me).TargetSlot);
    }

    /// <summary>Starts a job's standing task (row 87, a job's provenance) for one character at its first slow world.</summary>
    sealed class JobStarter(Func<CharacterId> who) : ITickSystem
    {
        bool done;

        public void SlowWorld(SimWorld world)
        {
            if (done || !world.Characters.TryResolveNext(who(), out int slot)) return;
            done = true;
            TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Idle, Meitou.Data.Gameplay.Ai.TaskType.OPERATE_MACHINERY, TaskSource.Job) { Job = 0 });
        }
    }

    /// <summary>
    /// Hold's attack on sight is a priority-3 goal, tried before the jobs (docs/game/ai-tasks.md "Precedence"): a holder on a job's task attacks the raider passing its post, as that goal.
    /// Catches a job's task that keeps a holder from attacking on sight.
    /// </summary>
    [Fact]
    public void A_holder_on_a_jobs_task_attacks_on_sight()
    {
        var holder = CharacterId.None;
        using var a = Make(4, 1, OneSided(), extra: new JobStarter(() => holder));
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var me = holder = a.World.Characters.Spawn(new CharacterHot { Position = new Vector3(o.X, 150, o.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15 },
            new Combatant { Name = "holder" }.MakeCold(), 0);
        var cold = a.World.Characters.Cold(me.Slot)!;
        (cold.Faction, cold.IsPlayer, cold.Orders, cold.HoldPoint) = (1, true, CharacterOrders.Hold, new Vector3(o.X, 150, o.Y));
        a.World.RunTicks(2);
        Assert.True(cold.Running is { Source: TaskSource.Job } job && job.Runs(CharacterTask.Idle), "on a job's task");
        var walker = a.SquadOf(0, o, true, (o + new Vector2(-300, 60), 0f)).Members[0];
        Docile(a, walker);
        a.World.Commands.Enqueue(new MoveOrder([walker], new Vector3(o.X + 300, 150, o.Y + 60)) { Tick = a.World.Tick });
        for (int k = 0; k < (int)(10 / a.World.TickSeconds) && !a.Fighting(me); k++) a.World.RunTicks(1);
        Assert.True(a.Fighting(me), "the holder attacks the walker passing within 80 of its hold point");
        Assert.Equal(walker.Slot, a.Combat.StateOf(me).TargetSlot);
        Assert.Equal(Meitou.Data.Gameplay.Ai.TaskType.ATTACK_ENEMIES, cold.Running!.Row);
        Assert.Equal(TaskSource.Goal, cold.Running.Source);
    }

    /// <summary>
    /// A villager whose raider goes down ends that fight with its task target (docs/game/combat.md "Leaving a fight") and at once joins its squad mate's defence against the other raider by its
    /// goal 143 (the fight's end asks it to think); once that one is down too and nobody is left, its fight ends.
    /// </summary>
    [Fact]
    public void A_fighter_whose_target_drops_turns_on_a_mates_attacker_and_stops_when_none_is_left()
    {
        var table = FactionRelations.Build([
            new FactionData { Id = "raiders", Name = "raiders", Relations = new Dictionary<string, int>() },
            new FactionData { Id = "villagers", Name = "villagers", Relations = new Dictionary<string, int>() },
        ]);
        using var a = Make(6, 1, table, ai: true);
        var o = new Vector2(2000, 2000);
        var villagers = a.SquadOf(1, o, true, (o, 0f), (o + new Vector2(40, 0), 0f));
        Lists(villagers, [Goal(TaskType.PROTECT_OWN_SQUAD)], [Goal(TaskType.PROTECT_OWN_SQUAD)]);
        var raiders = a.SquadOf(0, o, true, (o + new Vector2(0, 20), MathF.PI), (o + new Vector2(40, 20), MathF.PI));
        CharacterId fighter = villagers.Members[0], mate = villagers.Members[1], first = raiders.Members[0], second = raiders.Members[1];
        a.World.Commands.Enqueue(new AttackOrder([fighter], first) { Tick = a.World.Tick });
        a.World.Commands.Enqueue(new AttackOrder([first], fighter) { Tick = a.World.Tick });
        a.World.Commands.Enqueue(new AttackOrder([second], mate) { Tick = a.World.Tick });
        a.World.RunTicks(30);
        Assert.Equal(first.Slot, a.Combat.StateOf(fighter).TargetSlot);
        a.World.Characters.Cold(first.Slot)!.Medical!.KnockOut(600);
        a.World.RunTicks(5);
        Assert.Equal(second.Slot, a.Combat.StateOf(fighter).TargetSlot);
        a.World.Characters.Cold(second.Slot)!.Medical!.KnockOut(600);
        a.World.RunTicks(5);
        Assert.False(a.Fighting(fighter), "nobody left: the fight ends");
        Assert.True(a.Combat.StateOf(fighter).TargetSlot < 0);
    }

    /// <summary>
    /// A sense entry's decay (<c>SensoryData::decay</c> FUN_140859010, docs/game/perception.md "Senses"): an entry its looker no longer senses is dropped 30 sense units (60 s) after the look
    /// that last sensed it, 60 units (120 s) while its character is in combat mode.
    /// </summary>
    [Theory, InlineData(false, 60f), InlineData(true, 120f)]
    public void An_entry_no_longer_sensed_is_dropped_after_its_life(bool fighting, float life)
    {
        var aggro = new AggroSystem(Relations(), new AggroSettings { AttackOnSight = false });
        using var world = new SimWorld(new WorldSettings { Seed = 3, Threads = 1, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 150), [aggro]);
        CharacterId Spawn(float z)
        {
            var cold = new Combatant().MakeCold();
            cold.Faction = Villagers;
            return world.Characters.Spawn(new CharacterHot { Position = new Vector3(2000, 150, z), Health = 100, MaxSpeed = 60, WalkSpeed = 15 }, cold, 0);
        }
        CharacterId looker = Spawn(2000), target = Spawn(2200);   // 200 straight ahead of the looker (yaw 0 faces +Z)
        world.RunTicks(30);
        var senses = aggro.SensesOf(looker.Slot)!;
        Assert.True(senses.Knows(target, now: true), "seen");
        // Out of sight and hearing; in combat mode for the long life (no combat system here, so nothing ends it).
        world.Characters.Cold(target.Slot)!.InCombat = fighting;
        world.Characters.Place(target.Slot, new Vector3(2000, 150, 9000), 0);
        world.RunTicks((int)((life - 5) / world.TickSeconds));
        Assert.True(senses.Knows(target), "kept while its life lasts");
        Assert.False(senses.Knows(target, now: true));
        world.RunTicks((int)(10 / world.TickSeconds));
        Assert.False(senses.Knows(target), "dropped once its life is over");
    }

    /// <summary>
    /// Two of the player's squad fight a group, a third stands apart and one of the group attacks it: the lone one keeps fighting its own attacker through every re-pick
    /// (docs/game/combat.md "Target selection": the chooser's lists are its own attackers and threats, and a bodyguard's protectee's attackers, never a squad mate's).
    /// </summary>
    [Fact]
    public void A_lone_fighter_keeps_its_own_attacker_while_its_mates_fight_a_group()
    {
        using var a = Make(7, 1);
        var o = new Vector2(2000, 2000);
        var mine = a.SquadOf(Player, o, true, (o, 0f), (o + new Vector2(0, 25), 0f), (o + new Vector2(0, 150), 0f));
        var raiders = a.SquadOf(Raiders, o, true, (o + new Vector2(25, 0), 0f), (o + new Vector2(25, 25), 0f), (o + new Vector2(30, -10), 0f), (o + new Vector2(30, 35), 0f),
            (o + new Vector2(40, 150), 0f));
        var table = a.World.Characters;
        foreach (var m in mine.Members) table.Cold(m.Slot)!.IsPlayer = true;
        CharacterId pa = mine.Members[0], pb = mine.Members[1], lone = mine.Members[2], foe = raiders.Members[4];
        long t = a.World.Tick;
        a.World.Commands.Enqueue(new AttackOrder([raiders.Members[0], raiders.Members[2]], pa) { Tick = t });
        a.World.Commands.Enqueue(new AttackOrder([raiders.Members[1], raiders.Members[3]], pb) { Tick = t });
        a.World.Commands.Enqueue(new AttackOrder([foe], lone) { Tick = t });
        a.World.Commands.Enqueue(new AttackOrder([pa], raiders.Members[0]) { Tick = t, Defensive = true });
        a.World.Commands.Enqueue(new AttackOrder([pb], raiders.Members[1]) { Tick = t, Defensive = true });
        a.World.Commands.Enqueue(new AttackOrder([lone], foe) { Tick = t, Defensive = true });
        int checkedTicks = 0;
        for (int k = 0; k < 300; k++)
        {
            a.World.RunTicks(1);
            if (table.Cold(foe.Slot)!.Medical!.Incapacitated || table.Cold(lone.Slot)!.Medical!.Incapacitated) break;
            Assert.Equal(foe.Slot, a.Combat.StateOf(lone).TargetSlot);
            checkedTicks++;
        }
        Assert.True(checkedTicks * a.World.TickSeconds > 3 * CombatSystem.PickSeconds, $"only {checkedTicks} ticks checked");
    }

    // The shop scenarios (docs/game/ai-tasks.md "Combat goals"): 0 the town (the shop's; it hates the bandits), 1 bandits (they hate nobody, so they start no fight), 2 drifters.
    const int Town = 0, Bandits = 1, Drifters = 2;

    static FactionRelations TownRelations() => FactionRelations.Build([
        new FactionData { Id = "town", Name = "town", Relations = new Dictionary<string, int> { ["bandits"] = -80 } },
        new FactionData { Id = "bandits", Name = "bandits", Relations = new Dictionary<string, int>() },
        new FactionData { Id = "drifters", Name = "drifters", Relations = new Dictionary<string, int>() },
    ]);

    /// <summary>An urgent (priority 3) goal of every hour unless given a window, as an AI package lists it.</summary>
    static AiGoal Goal(TaskType type, float weight = 1, int start = 0, int end = 100, TaskTargetType target = TaskTargetType.Specific) =>
        new(new AiTask("t", type.ToString(), type, TaskPriority.Urgent, target, TaskEndEvent.Nothing), start, end, weight);

    /// <summary>
    /// Gives a squad one AI package with these role lists (the leader takes the first, the others the second, a member made Squad2 the third), so the AI (<see cref="Make"/> with ai) runs
    /// its members by them.
    /// </summary>
    static void Lists(Squad squad, AiGoal[] leader, AiGoal[]? squadList = null, AiGoal[]? squad2 = null)
    {
        var board = new Blackboard([new(0, new AiPackage { Id = "p", Name = "p", Roles = [squadList ?? [], squad2 ?? [], leader, [], []] })]);
        board.Update();
        squad.Ai = board;
    }

    /// <summary>Gives the squads given lists their minds (the AI's first serial step), before anyone else is put in the world.</summary>
    static void Think(Arena a) => a.World.RunTick();

    /// <summary>
    /// An NPC is never fully rested (state 72, docs/game/ai-tasks.md "StateType ids"): with only Sleeping on ground (175) in its lists and no town, no enemy and no weapon
    /// drawn, it sleeps. Catches the planner's done test leaving the NPC's sleep row out as already achieved (it idled then).
    /// </summary>
    [Fact]
    public void An_npc_sleeps_on_the_ground_by_its_goal_since_it_is_never_fully_rested()
    {
        using var a = Make(5, 1, TownRelations(), ai: true);
        var o = new Vector2(2000, 2000);
        var pack = a.Squad(Drifters, o, o, o + new Vector2(10, 0));
        Lists(pack, [Goal(TaskType.SLEEP_ON_FLOOR)], [Goal(TaskType.SLEEP_ON_FLOOR)]);
        a.World.RunTicks(30);
        Assert.All(pack.Members, m => Assert.Equal(TaskType.SLEEP_ON_FLOOR, a.World.Characters.Cold(m.Slot)!.Ai!.Goal!.Task.Type));
    }

    /// <summary>
    /// A healthy character of the player's faction is fully rested (state 72): the same squad, of the player's faction, leaves 175 out as done and idles. Catches the player's
    /// side losing its rested test along with the NPCs'.
    /// </summary>
    [Fact]
    public void A_healthy_character_of_the_players_faction_is_fully_rested_and_does_not_sleep_by_that_goal()
    {
        using var a = Make(5, 1, TownRelations(), ai: true);
        a.World.Player.Faction = Drifters;
        var o = new Vector2(2000, 2000);
        var pack = a.Squad(Drifters, o, o, o + new Vector2(10, 0));
        Lists(pack, [Goal(TaskType.SLEEP_ON_FLOOR)], [Goal(TaskType.SLEEP_ON_FLOOR)]);
        a.World.RunTicks(30);
        Assert.All(pack.Members, m => Assert.Equal(TaskType.IDLE, a.World.Characters.Cold(m.Slot)!.Ai!.Goal!.Task.Type));
    }

    /// <summary>
    /// A law enforcer (34) defends an innocent who is no ally (docs/game/ai-tasks.md, <c>FUN_14099b820</c>): the victim of another, peaceful faction, with no pending crime and no bounty, is one;
    /// a victim with a pending crime or a bandit is not. Catches 34 defending only allies.
    /// </summary>
    [Fact]
    public void A_law_enforcer_counts_a_peaceful_stranger_as_an_innocent_victim_but_not_a_criminal()
    {
        using var a = Make(13, 1, TownRelations(), ai: true);
        var o = new Vector2(2000, 2000);
        CharacterId Lone(int faction, float x) => a.SquadOf(faction, o, true, (o + new Vector2(x, 0), 0f)).Members[0];
        var guard = Lone(Town, 0); var attacker = Lone(Bandits, 10); var victim = Lone(Drifters, 20);
        var table = a.World.Characters;
        Assert.True(a.Aggro.Innocent(table.Cold(guard.Slot)!, table.Cold(attacker.Slot)!, table.Cold(victim.Slot)!));
        table.Cold(victim.Slot)!.Pending = new Meitou.Simulation.Crime.PendingCrime { Code = Meitou.Simulation.Crime.CrimeCode.Assault, VictimFaction = Town };
        Assert.False(a.Aggro.Innocent(table.Cold(guard.Slot)!, table.Cold(attacker.Slot)!, table.Cold(victim.Slot)!));
    }

    /// <summary>
    /// Goal 15's desire is the finder's first sensed ally above 0, not the best (<c>FUN_140599b90</c> asks <c>FUN_14099ca10</c> to stop at the first): a gate guard sees two mates, one fought by
    /// one bandit (threat 0.05) and one by three (0.55); the scan's first value is the first of them in its senses, whichever is larger. Catches a constant desire of 1.
    /// </summary>
    [Fact]
    public void Goal_15_desire_is_the_first_fighting_allys_threat_not_the_best()
    {
        using var a = Make(14, 1, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var town = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f), (o + new Vector2(-10, 0), 0f));
        Lists(town, [Goal(TaskType.PROTECT_ALLIES)]);
        Think(a);
        CharacterId guard = town.Members[0], one = town.Members[1], three = town.Members[2];
        var gang = a.SquadOf(Bandits, o, true, (o + new Vector2(0, 30), MathF.PI), (o + new Vector2(8, 30), MathF.PI), (o + new Vector2(-8, 30), MathF.PI), (o + new Vector2(16, 30), MathF.PI));
        a.World.Commands.Enqueue(new AttackOrder([gang.Members[0]], one) { Tick = a.World.Tick });
        foreach (var b in gang.Members.Skip(1)) a.World.Commands.Enqueue(new AttackOrder([b], three) { Tick = a.World.Tick });
        var ai = a.World.SystemOf<AiSystem>()!;
        float Threat(CharacterId id) => a.Combat.FightThreat(a.World, id.Slot, a.Aggro.SensesOf(id.Slot));
        bool Seen(SenseEntry e) => e.Now && (e.Target == one.Slot || e.Target == three.Slot) && Threat(new CharacterId(e.Target, e.Generation)) > 0;
        for (int k = 0; k < 600 && !(Threat(one) > 0 && Threat(three) > 0 && a.Aggro.SensesOf(guard.Slot)!.Entries.Any(Seen)); k++) a.World.RunTicks(1);
        Assert.True(Threat(one) > 0 && Threat(three) > 0 && Threat(one) != Threat(three), $"both mates fought: {Threat(one)} and {Threat(three)}");
        var first = a.Aggro.SensesOf(guard.Slot)!.Entries.First(Seen);
        var scan = ai.ProtectScan(a.World, guard.Slot, TaskType.PROTECT_ALLIES, a.World.Characters.Cold(guard.Slot)!.Ai!);
        Assert.Equal(Threat(new CharacterId(first.Target, first.Generation)), scan.First);
        Assert.Equal(MathF.Max(Threat(one), Threat(three)), scan.Value);
    }

    /// <summary>
    /// A squad shares a sighting, not the attack (docs/simulation.md "Hostility and relations"): a guard that attacks on sight (16) goes for a bandit, and its mate with no combat goal,
    /// which sees the bandit too, never joins. Catches the squad's free mates being dragged into a member's fight whatever their own goals.
    /// </summary>
    [Fact]
    public void A_passive_npc_never_joins_its_mates_attack()
    {
        using var a = Make(5, 1, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var shop = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        CharacterId guard = shop.Members[0], passive = shop.Members[1];
        Lists(shop, [Goal(TaskType.ATTACK_ENEMIES)]);
        Think(a);
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(5, 100), MathF.PI)).Members[0];
        for (int k = 0; k < (int)(4 / a.World.TickSeconds); k++)
        {
            a.World.RunTicks(1);
            Assert.False(a.Fighting(passive), $"the Passive mate joined at tick {a.World.Tick}");
        }
        Assert.True(a.Fighting(guard), "the guard attacks the bandit on sight");
        Assert.Equal(bandit.Slot, a.Combat.StateOf(guard).TargetSlot);
    }

    /// <summary>
    /// A shop keeps out of a fight between strangers (docs/game/ai-tasks.md "Combat goals"): a bandit, whom the town hates, fights a drifter 100 away in view of a vanilla shop's squad, the
    /// shopkeeper (143), a shop guard (143 and 34 Law enforcer, in no town here) and a gate guard (15); nobody joins, since the drifter is nobody's ally and no mate fights. Catches the
    /// defending goals attacking any enemy that fights anyone.
    /// </summary>
    [Fact]
    public void A_shop_keeps_out_of_a_fight_between_strangers_100_away()
    {
        using var a = Make(8, 1, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var shop = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(8, 0), 0f), (o + new Vector2(-8, 0), 0f));
        a.World.Characters.Cold(shop.Members[2].Slot)!.Role = (int)SquadRole.Squad2;
        Lists(shop, [Goal(TaskType.PROTECT_OWN_SQUAD)], [Goal(TaskType.PROTECT_OWN_SQUAD), Goal(TaskType.ATTACK_TROUBLE_MAKERS)], [Goal(TaskType.PROTECT_ALLIES)]);
        Think(a);
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(0, 100), MathF.PI)).Members[0];
        var drifter = a.SquadOf(Drifters, o, true, (o + new Vector2(0, 110), MathF.PI)).Members[0];
        a.World.Commands.Enqueue(new AttackOrder([bandit], drifter) { Tick = a.World.Tick });
        a.World.Commands.Enqueue(new AttackOrder([drifter], bandit) { Tick = a.World.Tick });
        for (int k = 0; k < (int)(4 / a.World.TickSeconds); k++)
        {
            a.World.RunTicks(1);
            foreach (var m in shop.Members) Assert.False(a.Fighting(m), $"shop member {m.Slot} joined at tick {a.World.Tick}");
        }
        Assert.True(a.Fighting(bandit) && a.Fighting(drifter), "the strangers fight on");
    }

    /// <summary>
    /// A shopkeeper defends a squad mate as 143 does (docs/game/ai-tasks.md "Combat goals": a mate holding a target, achieved by 13 against that mate's foe within 150 of it): its guard runs
    /// at a bandit 400 away, and the shopkeeper, who sees it go, joins only once guard and bandit are within 150 of each other, against that bandit. Catches a protector taking a mate's
    /// target anywhere.
    /// </summary>
    [Fact]
    public void A_shopkeeper_defends_its_mate_against_a_foe_within_150_of_it()
    {
        using var a = Make(9, 1, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var shop = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        CharacterId keeper = shop.Members[0], guard = shop.Members[1];
        Lists(shop, [Goal(TaskType.PROTECT_OWN_SQUAD)], [Goal(TaskType.PROTECT_OWN_SQUAD)]);
        Think(a);
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(5, 400), MathF.PI)).Members[0];
        a.World.Commands.Enqueue(new AttackOrder([guard], bandit) { Tick = a.World.Tick });
        var table = a.World.Characters;
        float Gap() => Vector2.Distance(new Vector2(table.Previous[guard.Slot].Position.X, table.Previous[guard.Slot].Position.Z),
            new Vector2(table.Previous[bandit.Slot].Position.X, table.Previous[bandit.Slot].Position.Z));
        float gapBefore = Gap();
        for (int k = 0; k < (int)(15 / a.World.TickSeconds) && !a.Fighting(keeper); k++)
        {
            gapBefore = Gap();
            a.World.RunTicks(1);
        }
        Assert.True(a.Fighting(keeper), "the shopkeeper comes to its guard's fight");
        Assert.Equal(bandit.Slot, a.Combat.StateOf(keeper).TargetSlot);
        Assert.True(gapBefore <= RetaliationSystem.ProtectReach + 10, $"it joined with the bandit {gapBefore:0} from the guard");
    }

    static float Gap(SimWorld world, CharacterId x, CharacterId y)
    {
        var p = world.Characters.Previous;
        return Vector2.Distance(new Vector2(p[x.Slot].Position.X, p[x.Slot].Position.Z), new Vector2(p[y.Slot].Position.X, p[y.Slot].Position.Z));
    }

    /// <summary>
    /// A squad shares its sightings, not its attacks (docs/game/ai-tasks.md "Combat goals"): a guard that attacks on sight (16) runs at a bandit 400 away, which its shopkeeper (143 only)
    /// sees too; the shopkeeper does not go with it, and joins by its own goal only once the guard holds the bandit within 150 of it. Catches a mate's sighting pulling a 143-only member
    /// into the attack (the old squad join).
    /// </summary>
    [Fact]
    public void A_protect_squad_mate_is_not_pulled_into_a_mates_attack_on_sight()
    {
        using var a = Make(10, 1, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var shop = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        CharacterId keeper = shop.Members[0], guard = shop.Members[1];
        Lists(shop, [Goal(TaskType.PROTECT_OWN_SQUAD)], [Goal(TaskType.ATTACK_ENEMIES)]);
        Think(a);
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(5, 400), MathF.PI)).Members[0];
        float gapBefore = Gap(a.World, guard, bandit);
        bool guardFirst = false;
        for (int k = 0; k < (int)(15 / a.World.TickSeconds) && !a.Fighting(keeper); k++)
        {
            gapBefore = Gap(a.World, guard, bandit);
            a.World.RunTicks(1);
            guardFirst |= a.Fighting(guard) && !a.Fighting(keeper);
        }
        Assert.True(guardFirst, "the guard attacks on sight first, alone");
        Assert.True(a.Fighting(keeper), "the shopkeeper comes to its guard's fight");
        Assert.Equal(bandit.Slot, a.Combat.StateOf(keeper).TargetSlot);
        Assert.True(gapBefore <= RetaliationSystem.ProtectReach + 10, $"it joined with the bandit {gapBefore:0} from the guard");
    }

    /// <summary>
    /// Protect allies (15, finder <c>FUN_14099ca10</c> with row 13, docs/game/ai-tasks.md "Combat goals"): a resident of the town (another squad) runs at a bandit 400 away; a gate guard of the
    /// town that senses the resident joins only once the bandit is within 150 of the resident, against that bandit, and a shop beside it (a shopkeeper with 143, a shop guard with 143 and 34)
    /// stays out, since none of its members fights. Catches a defender joining a fight far from the ally, and a shop joining a battle of its faction that involves none of its members.
    /// </summary>
    [Fact]
    public void A_gate_guard_defends_a_sensed_ally_within_150_of_its_foe_and_a_shop_beside_it_stays_out()
    {
        using var a = Make(12, 1, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var shop = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(8, 0), 0f));
        Lists(shop, [Goal(TaskType.PROTECT_OWN_SQUAD)], [Goal(TaskType.PROTECT_OWN_SQUAD), Goal(TaskType.ATTACK_TROUBLE_MAKERS)]);
        var gate = a.SquadOf(Town, o, true, (o + new Vector2(-30, 0), 0f));
        Lists(gate, [Goal(TaskType.PROTECT_ALLIES)]);
        Think(a);
        CharacterId guard = gate.Members[0];
        var resident = a.SquadOf(Town, o, true, (o + new Vector2(0, 40), 0f)).Members[0];
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(0, 440), MathF.PI)).Members[0];
        a.World.Commands.Enqueue(new AttackOrder([resident], bandit) { Tick = a.World.Tick });
        float gapBefore = Gap(a.World, resident, bandit);
        for (int k = 0; k < (int)(15 / a.World.TickSeconds) && !a.Fighting(guard); k++)
        {
            gapBefore = Gap(a.World, resident, bandit);
            a.World.RunTicks(1);
        }
        Assert.True(a.Fighting(guard), "the gate guard comes to the resident's fight");
        Assert.Equal(bandit.Slot, a.Combat.StateOf(guard).TargetSlot);
        Assert.True(gapBefore <= RetaliationSystem.ProtectReach + 10, $"it joined with the bandit {gapBefore:0} from the resident");
        for (int k = 0; k < (int)(5 / a.World.TickSeconds); k++)
        {
            a.World.RunTicks(1);
            foreach (var m in shop.Members) Assert.False(a.Fighting(m), $"shop member {m.Slot} joined at tick {a.World.Tick}");
        }
    }

    /// <summary>
    /// An attack goal is one goal of the chooser (docs/game/ai-tasks.md "Precedence", "Desire and commitment"): a member whose list holds following its leader (priority 3 here, weight 1)
    /// and 16 Attack enemies sees a bandit; it attacks only when 16 outscores the follow (its weight above 1.25, the follow's continuity bonus once it runs) and only in 16's hours. Catches
    /// an attack on sight that ignores the goals' weights and hours.
    /// </summary>
    [Theory]
    [InlineData(2f, true, true)]
    [InlineData(0.5f, true, false)]
    [InlineData(2f, false, false)]
    public void An_attack_goal_wins_only_when_it_outscores_the_members_other_goals(float weight, bool inHours, bool attacks)
    {
        using var a = Make(14, 1, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var squad = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        CharacterId member = squad.Members[1];
        int hour = a.World.Hour;
        var (start, end) = inHours ? (0, 100) : ((hour + 6) % 24, (hour + 8) % 24);
        Lists(squad, [], [Goal(TaskType.FOLLOW_SQUADLEADER, target: TaskTargetType.Leader), Goal(TaskType.ATTACK_ENEMIES, weight, start, end)]);
        Think(a);
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(5, 200), MathF.PI)).Members[0];
        a.World.RunTicks((int)(4 / a.World.TickSeconds));
        Assert.Equal(attacks, a.Fighting(member));
        if (attacks) Assert.Equal(bandit.Slot, a.Combat.StateOf(member).TargetSlot);
        else Assert.Equal(TaskType.FOLLOW_SQUADLEADER, a.World.Characters.Cold(member.Slot)!.Ai!.Goal!.Task.Type);
        Assert.False(a.Fighting(squad.Members[0]), "the leader has no combat goal");
    }

    /// <summary>
    /// A fight is a plan step of its combat goal (docs/game/ai-tasks.md "Combat goals"): a member that attacks a bandit on sight (16) keeps the one fight across its thinks while it runs at it
    /// (one start, the fight's start tick unchanged), and once its lists make following its leader outscore the attack (16 at 0.2, x1.25 as the current goal, against the follow's 0.5), the
    /// chooser takes it out of the fight to follow. Catches a fight started again at every think, and a fighter that never thinks.
    /// </summary>
    [Fact]
    public void A_fight_goal_runs_on_across_thinks_and_a_higher_goal_takes_the_fighter_out_of_it()
    {
        using var a = Make(17, 1, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var squad = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        CharacterId member = squad.Members[1];
        var follow = Goal(TaskType.FOLLOW_SQUADLEADER, 0.5f, target: TaskTargetType.Leader);
        Lists(squad, [], [follow, Goal(TaskType.ATTACK_ENEMIES)]);
        Think(a);
        // A bandit that cannot strike back (no fighter), far enough that the run at it outlasts several thinks.
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(5, 400), MathF.PI)).Members[0];
        var table = a.World.Characters;
        table.Cold(bandit.Slot)!.Fighter = null;
        for (int k = 0; k < (int)(4 / a.World.TickSeconds) && !a.Fighting(member); k++) a.World.RunTicks(1);
        Assert.True(a.Fighting(member), "it attacks the bandit on sight");
        long from = table.Cold(member.Slot)!.Running!.Tick;
        for (int k = 0; k < 4 * 15; k++)
        {
            a.World.RunTicks(1);
            Assert.True(a.Fighting(member), $"its fight ended at tick {a.World.Tick}");
            Assert.Equal(from, table.Cold(member.Slot)!.Running!.Tick);
        }
        Assert.Single(a.Aggro.Log, e => e.Attacker == member);
        Lists(squad, [], [follow, Goal(TaskType.ATTACK_ENEMIES, 0.2f)]);
        a.World.RunTicks(2 * 15);
        Assert.False(a.Fighting(member), "the follow outscores the attack");
        Assert.Equal(TaskType.FOLLOW_SQUADLEADER, table.Cold(member.Slot)!.Ai!.Goal!.Task.Type);
    }

    /// <summary>
    /// Self preservation is a goal of the same chooser (docs/game/ai-tasks.md "Combat goals", 32 and 35): a member fighting a bandit by its attack goal, whom the bandit fights back, loses
    /// both arms; the chooser takes 32, whose plan is 35 for a character with no working arm, and the flee step runs it from its threat. Catches the run left to a special case outside the
    /// chooser. (A leg going down at posture 0 knocks out first, and the bandit drops the downed target.)
    /// </summary>
    [Fact]
    public void A_fighter_with_no_working_arm_runs_away_by_self_preservation_in_the_chooser()
    {
        using var a = Make(18, 1, TownRelations(), ai: true, flee: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var squad = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        CharacterId member = squad.Members[1];
        Lists(squad, [], [Goal(TaskType.ATTACK_ENEMIES)]);
        Think(a);
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(5, 100), MathF.PI)).Members[0];
        var table = a.World.Characters;
        for (int k = 0; k < (int)(6 / a.World.TickSeconds) && !(a.Fighting(member) && a.Combat.StateOf(bandit).TargetSlot == member.Slot); k++) a.World.RunTicks(1);
        Assert.True(a.Fighting(member) && a.Combat.StateOf(bandit).TargetSlot == member.Slot, "the member attacks and the bandit fights back");
        a.World.RunTicks(15);   // a look that lists the bandit among its threats
        var cold = table.Cold(member.Slot)!;
        cold.Medical!.Parts[RArm].Flesh = cold.Medical.Parts[LArm].Flesh = -80;
        for (int k = 0; k < 2 * 15 && !cold.Fleeing; k++) a.World.RunTicks(1);
        Assert.False(cold.Medical.ArmWorks(left: true) || cold.Medical.ArmWorks(left: false), "no arm works");
        Assert.True(cold.Fleeing && cold.FleesAlone, "it crawls away from its threat");
        Assert.Equal(TaskType.SELF_PRESERVATION, cold.Ai!.Goal!.Task.Type);
    }

    /// <summary>
    /// A squad of two whose leader is knocked out is not intact (docs/game/perception.md "Fleeing", <c>FUN_14079acf0</c>): the member standing runs from the bandit it fights, and the HUD's Goal
    /// line says "Running away" (<c>Task_Runaway</c>'s name) rather than "Moving", its walk's. Catches a run that reads as an aimless walk to the player.
    /// </summary>
    [Fact]
    public void A_squad_member_running_after_its_leader_fell_is_shown_as_running_away()
    {
        using var a = Make(31, 1, TownRelations(), ai: true, flee: true, squadBreak: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var squad = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        CharacterId leader = squad.Leader, member = squad.Members.First(m => m != squad.Leader);
        Lists(squad, [], [Goal(TaskType.ATTACK_ENEMIES)]);
        Think(a);
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(5, 100), MathF.PI)).Members[0];
        var table = a.World.Characters;
        for (int k = 0; k < (int)(6 / a.World.TickSeconds) && !a.Fighting(member); k++) a.World.RunTicks(1);
        Assert.True(a.Fighting(member), "the member attacks the bandit");
        table.Cold(leader.Slot)!.Medical!.KnockOut(1000);
        var cold = table.Cold(member.Slot)!;
        for (int k = 0; k < 10 / a.World.TickSeconds && !cold.Fleeing; k++) a.World.RunTicks(1);
        Assert.True(cold.Fleeing && !cold.FleesAlone, "the broken squad's member runs");
        a.World.RunTicks(3);
        Assert.Equal("Running away", Meitou.Simulation.Ai.AiSystem.ActivityText(table.Next[member.Slot], cold));
    }

    /// <summary>
    /// The original runs no squad for being broken (docs/game/perception.md "Squad not intact": only a package with the retreat signal acts on it, and no squad template lists one): by default
    /// the member of a squad of two whose leader is knocked out keeps fighting. Catches every broken squad running.
    /// </summary>
    [Fact]
    public void A_broken_squad_does_not_run_unless_the_option_is_on()
    {
        using var a = Make(31, 1, TownRelations(), ai: true, flee: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var squad = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        CharacterId leader = squad.Leader, member = squad.Members.First(m => m != squad.Leader);
        Lists(squad, [], [Goal(TaskType.ATTACK_ENEMIES)]);
        Think(a);
        a.SquadOf(Bandits, o, true, (o + new Vector2(5, 100), MathF.PI));
        var table = a.World.Characters;
        for (int k = 0; k < (int)(6 / a.World.TickSeconds) && !a.Fighting(member); k++) a.World.RunTicks(1);
        Assert.True(a.Fighting(member), "the member attacks the bandit");
        table.Cold(leader.Slot)!.Medical!.KnockOut(1000);
        a.World.RunTicks(4 * 15);
        Assert.False(table.Cold(member.Slot)!.Fleeing, "no squad runs for being broken");
        Assert.Equal(0, a.World.Systems.OfType<FleeSystem>().Single().Broken);
    }

    /// <summary>
    /// A small town squad with its own building holds while anyone is able (the persistent-with-home rule of <c>FUN_14079acf0</c>, the same test as the AI's intact flag): with the leader knocked
    /// out, its member does not run, where the same squad without a home does. Catches the flee trigger passing no home.
    /// </summary>
    [Fact]
    public void A_town_squad_with_its_own_home_holds_when_its_leader_falls()
    {
        foreach (bool home in new[] { false, true })
        {
            using var a = Make(41, 1, TownRelations(), flee: true, squadBreak: true);
            a.Aggro.Combat = a.Combat;
            var o = new Vector2(2000, 2000);
            var building = new Meitou.Data.World.TownBuilding("b", "b", "House", new Vector3(o.X, 150, o.Y), 0, false, false, []);
            var squad = home ? a.TownSquadOf(Town, o, building, (o, 0f), (o + new Vector2(10, 0), 0f)) : a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
            CharacterId leader = squad.Leader, member = squad.Members.First(m => m != squad.Leader);
            var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(5, 100), MathF.PI)).Members[0];
            var table = a.World.Characters;
            a.World.Commands.Enqueue(new AttackOrder([member], bandit) { Tick = a.World.Tick });
            a.World.RunTicks(30);
            Assert.True(a.Fighting(member), "the member fights the bandit");
            table.Cold(leader.Slot)!.Medical!.KnockOut(1000);
            a.World.RunTicks(4 * 15);
            Assert.Equal(!home, table.Cold(member.Slot)!.Fleeing);
        }
    }

    /// <summary>
    /// An NPC defends itself by 32 Self preservation in the chooser (docs/game/ai-tasks.md "Combat goals": desire 1 within 40, plan 13 on its own attacker): a member with no combat goal that
    /// already senses a bandit is attacked by it; the alert puts the bandit on its threats list at once, so its next think takes 32 and the serial step starts the fight two ticks after the alert,
    /// a fight the AI holds across its thinks. Catches the old engine answer outside the chooser, and a threats list stale until the next look (32 at 0, the fight never started or dropped).
    /// </summary>
    [Fact]
    public void An_attacked_npc_fights_back_by_self_preservation_in_the_chooser()
    {
        using var a = Make(23, 1, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var squad = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        CharacterId member = squad.Members[1];
        Lists(squad, [], [Goal(TaskType.FOLLOW_SQUADLEADER, 0.5f, target: TaskTargetType.Leader)]);
        Think(a);
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(5, 30), MathF.PI)).Members[0];
        a.World.RunTicks(2 * 15 + 5);   // its looks have sensed the bandit (an entry before the attack); its next look is 10 ticks off
        var table = a.World.Characters;
        Assert.True(a.Aggro.SensesOf(member.Slot)!.IndexOf(bandit.Slot, bandit.Generation) >= 0, "the bandit is sensed before the attack");
        Assert.False(a.Fighting(member));
        a.World.Commands.Enqueue(new AttackOrder([bandit], member) { Tick = a.World.Tick });
        a.World.RunTicks(3);   // the order and the alert, the think, the fight's start
        var cold = table.Cold(member.Slot)!;
        Assert.True(a.Fighting(member), "it fights back two ticks after the alert");
        Assert.Equal(bandit.Slot, a.Combat.StateOf(member).TargetSlot);
        Assert.Equal(TaskType.SELF_PRESERVATION, cold.Ai!.Goal!.Task.Type);
        Assert.Equal(TaskType.ATTACK_ATTACKERS_OF, cold.Ai.Step);
        long from = cold.Running!.Tick;
        Assert.Equal(from, cold.Ai.FightFrom);
        for (int k = 0; k < 3 * 15 && !table.Cold(bandit.Slot)!.Medical!.Incapacitated; k++)
        {
            a.World.RunTicks(1);
            Assert.True(a.Fighting(member), $"its fight ended at tick {a.World.Tick}");
            Assert.Equal(from, cold.Running!.Tick);
        }
    }

    /// <summary>
    /// Self preservation competes with the other priority-3 goals by its desire (<c>FUN_140599d20</c>: 0.7 with an attacker within 181.7, 1 within 40): a member following its leader at weight
    /// 0.75 (x1.25 as the current goal) keeps following while the bandit that attacks it runs in from 120, and fights it once the bandit is within 40. Catches a self-defence at any distance
    /// whatever the other goals, and one that never outranks a goal above 0.7.
    /// </summary>
    [Fact]
    public void A_higher_goal_yields_to_self_preservation_only_once_the_attacker_is_within_40()
    {
        using var a = Make(24, 1, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var squad = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        CharacterId member = squad.Members[1];
        Lists(squad, [], [Goal(TaskType.FOLLOW_SQUADLEADER, 0.75f, target: TaskTargetType.Leader)]);
        Think(a);
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(10, 120), MathF.PI)).Members[0];
        a.World.RunTicks(15);
        var table = a.World.Characters;
        a.World.Commands.Enqueue(new AttackOrder([bandit], member) { Tick = a.World.Tick });
        float Gap() => Vector2.Distance(new(table.Previous[member.Slot].Position.X, table.Previous[member.Slot].Position.Z), new(table.Previous[bandit.Slot].Position.X, table.Previous[bandit.Slot].Position.Z));
        long near = -1;
        for (int k = 0; k < (int)(8 / a.World.TickSeconds) && !a.Fighting(member); k++)
        {
            a.World.RunTicks(1);
            if (near < 0 && Gap() <= RetaliationSystem.ImmediateReach) near = a.World.Tick;
            if (Gap() > RetaliationSystem.ImmediateReach + 5) Assert.False(a.Fighting(member), $"it fought back from {Gap():0} at tick {a.World.Tick}");
        }
        Assert.True(near >= 0, "the bandit came within 40");
        Assert.True(a.Fighting(member), "it fights back within 40");
        Assert.InRange(a.World.Tick - near, 0, 15 + 3);
        Assert.Equal(TaskType.SELF_PRESERVATION, table.Cold(member.Slot)!.Ai!.Goal!.Task.Type);
    }

    /// <summary>
    /// A landed blow on an NPC whose fight is off asks its AI to think (the hit path of <c>AI::underMeleeAttack</c>, docs/game/combat.md "Being targeted"): the member following its leader at
    /// 0.75 is attacked from 120, so the alert finds Self preservation at 0.7; with the periodic think far off (every 600 ticks), it fights back two ticks after the first blow that lands.
    /// Catches a hit that leaves the threat to the periodic think (up to 15 ticks of being beaten unanswered).
    /// </summary>
    [Fact]
    public void A_landed_blow_on_an_npc_with_its_fight_off_has_it_think_at_once()
    {
        using var a = Make(24, 1, TownRelations(), ai: true, think: new AiSettings { ThinkEveryTicks = 600 });
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var squad = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        CharacterId member = squad.Members[1];
        Lists(squad, [], [Goal(TaskType.FOLLOW_SQUADLEADER, 0.75f, target: TaskTargetType.Leader)]);
        Think(a);
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(10, 120), MathF.PI)).Members[0];
        a.World.RunTicks(15);
        a.World.Commands.Enqueue(new AttackOrder([bandit], member) { Tick = a.World.Tick });
        long hit = -1;
        for (int k = 0; k < (int)(8 / a.World.TickSeconds) && !a.Fighting(member); k++)
        {
            a.World.RunTicks(1);
            if (hit < 0 && a.Combat.StateOf(member).Taken > 0) hit = a.World.Tick;
        }
        Assert.True(hit >= 0, "a blow landed before it fought back");
        Assert.True(a.Fighting(member), "it fights back");
        Assert.InRange(a.World.Tick - hit, 0, 2);
        Assert.Equal(TaskType.SELF_PRESERVATION, a.World.Characters.Cold(member.Slot)!.Ai!.Goal!.Task.Type);
    }

    /// <summary>
    /// A crossbowman shooting from 400, beyond Self preservation's 181.7 (desire 0, <c>FUN_140599d20</c>), is not fought back by an NPC whose lists hold no combat goal: nothing in its chooser
    /// covers it (the original's too, docs/game/combat.md "Self-defence"). With 143 Protect close allies the NPC counts itself (its own fight threat, <c>FUN_14099d510</c>) and row 13 takes its own
    /// attacker at any distance, so it goes for the shooter. Catches the old engine answer at any distance, and a 143 that never covers its own holder.
    /// </summary>
    [Theory, InlineData(false), InlineData(true)]
    public void A_shooter_beyond_self_preservations_reach_is_fought_back_only_through_another_goal(bool protects)
    {
        using var a = Make(25, 1, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var squad = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        CharacterId member = squad.Members[1];
        Lists(squad, [], protects ? [Goal(TaskType.PROTECT_OWN_SQUAD)] : []);
        Think(a);
        var table = a.World.Characters;
        var shooter = table.Spawn(new CharacterHot { Position = new Vector3(o.X + 10, 150, o.Y + 400), Health = 100, Yaw = MathF.PI }, RangedTests.Shooter(100, 100, 30, Bandits), 0);
        a.World.Commands.Enqueue(new AttackOrder([shooter], member) { Tick = a.World.Tick });
        bool shot = false;
        for (int k = 0; k < (int)(6 / a.World.TickSeconds) && !a.Fighting(member); k++)
        {
            a.World.RunTicks(1);
            shot |= a.Combat.ProjectilesFired > 0;
        }
        Assert.True(a.Combat.StateOf(shooter).Ranged, "it shoots");
        Assert.Equal(protects, a.Fighting(member));
        if (protects) Assert.Equal(shooter.Slot, a.Combat.StateOf(member).TargetSlot);
        else
        {
            Assert.True(shot, "it was shot at");
            Assert.NotEqual(TaskType.SELF_PRESERVATION, table.Cold(member.Slot)!.Ai!.Goal?.Task.Type);
        }
    }

    /// <summary>The self-defence scenarios (melee attacks on AI-run members, a shooter on another) hash the same at 1, 4 and 16 threads.</summary>
    [Fact]
    public void Self_defence_in_the_chooser_hashes_the_same_at_1_4_and_16_threads()
    {
        static List<ulong> Run(int threads)
        {
            using var a = Make(26, threads, TownRelations(), ai: true);
            a.Aggro.Combat = a.Combat;
            var o = new Vector2(2000, 2000);
            var squad = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f), (o + new Vector2(-10, 0), 0f));
            Lists(squad, [], [Goal(TaskType.FOLLOW_SQUADLEADER, 0.5f, target: TaskTargetType.Leader)]);
            Think(a);
            var bandits = a.SquadOf(Bandits, o, true, (o + new Vector2(10, 60), MathF.PI), (o + new Vector2(-10, 90), MathF.PI));
            var shooter = a.World.Characters.Spawn(new CharacterHot { Position = new Vector3(o.X, 150, o.Y + 150), Health = 100, Yaw = MathF.PI }, RangedTests.Shooter(100, 100, 30, Bandits), 0);
            a.World.Commands.Enqueue(new AttackOrder([bandits.Members[0]], squad.Members[1]) { Tick = a.World.Tick });
            a.World.Commands.Enqueue(new AttackOrder([bandits.Members[1]], squad.Members[0]) { Tick = a.World.Tick });
            a.World.Commands.Enqueue(new AttackOrder([shooter], squad.Members[2]) { Tick = a.World.Tick });
            var hashes = new List<ulong>();
            foreach (int ticks in new[] { 10, 50, 180 })
            {
                a.World.RunTicks(ticks);
                hashes.Add(a.World.StateHash());
            }
            Assert.All(squad.Members, m => Assert.True(a.Fighting(m) || a.World.Characters.Cold(m.Slot)!.Medical!.Incapacitated, "each member defends itself"));
            return hashes;
        }
        var one = Run(1);
        Assert.Equal(one, Run(4));
        Assert.Equal(one, Run(16));
    }

    /// <summary>
    /// A combat goal pre-empts an engine duty that a fight may interrupt (docs/game/ai-tasks.md "Precedence": the duties are goals and jobs below a priority-3 goal): a member carrying a downed
    /// intruder out of town (a town errand's walk) attacks a bandit on sight with the body on its shoulder; on a kidnapper's cage walk, which its system walks again over a fight, it does not.
    /// Catches a carrier the AI never lets fight, and a fight started over a duty that cannot be interrupted.
    /// </summary>
    [Theory, InlineData(true), InlineData(false)]
    public void A_carrier_on_a_town_errand_attacks_on_sight_with_the_body_on_its_shoulder(bool errand)
    {
        CharacterId carrier = CharacterId.None, body = CharacterId.None;
        var o = new Vector2(2000, 2000);
        var row = errand ? TaskType.TAKE_INTRUDER_OUTSIDE_TOWN : TaskType.FIND_CAGE_AND_PUT_IN;
        using var a = Make(19, 1, TownRelations(), ai: true, extra: new DutyWalker(() => (carrier, body), o + new Vector2(10, 800), row));
        a.Aggro.Combat = a.Combat;
        var squad = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        Lists(squad, [], [Goal(TaskType.ATTACK_ENEMIES)]);
        Think(a);
        var table = a.World.Characters;
        body = a.SquadOf(Drifters, o, true, (o + new Vector2(10, 0), 0f)).Members[0];
        table.Cold(body.Slot)!.Medical!.KnockOut(1000);
        carrier = squad.Members[1];
        a.World.RunTick();
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(5, 300), MathF.PI)).Members[0];
        a.World.RunTicks((int)(4 / a.World.TickSeconds));
        Assert.Equal(errand, a.Fighting(carrier));
        Assert.Equal(body, table.Cold(carrier.Slot)!.Carrying);
        if (errand) Assert.Equal(bandit.Slot, a.Combat.StateOf(carrier).TargetSlot);
    }

    /// <summary>At its first slow world with a carrier named, puts the body on its shoulder and starts an engine duty's walk (row <paramref name="row"/>, a goal's provenance) to <paramref name="to"/>.</summary>
    sealed class DutyWalker(Func<(CharacterId Carrier, CharacterId Body)> who, Vector2 to, TaskType row) : ITickSystem
    {
        bool done;

        public void SlowWorld(SimWorld world)
        {
            var (carrier, body) = who();
            if (done || !world.Characters.TryResolveNext(carrier, out int slot)) return;
            done = true;
            (world.Characters.Cold(slot)!.Carrying, world.Characters.Cold(body.Slot)!.CarriedBy) = (body, carrier);
            TaskStart.Walk(world, slot, to, SpeedMode.Walk, row, TaskSource.Goal);
        }
    }

    /// <summary>
    /// Mates choosing from one sighting in the same tick spread over the foes as the original's one-after-another choosers do (a fight's start puts the fighter on its target's engaged list, which
    /// the next chooser's target score counts, docs/game/ai-tasks.md "Combat goals"): two members that attack on sight see two bandits, one 20 farther than the other, and start their fights in
    /// the same tick on different bandits. Catches every mate of a sighting piling on one target.
    /// </summary>
    [Fact]
    public void Mates_attacking_from_one_sighting_in_the_same_tick_take_different_bandits()
    {
        using var a = Make(20, 1, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var squad = a.SquadOf(Town, o, true, (o + new Vector2(-4, 0), 0f), (o + new Vector2(4, 0), 0f));
        Lists(squad, [Goal(TaskType.ATTACK_ENEMIES)], [Goal(TaskType.ATTACK_ENEMIES)]);
        Think(a);
        a.SquadOf(Bandits, o, true, (o + new Vector2(0, 200), MathF.PI), (o + new Vector2(0, 220), MathF.PI));
        for (int k = 0; k < (int)(4 / a.World.TickSeconds) && !squad.Members.All(a.Fighting); k++) a.World.RunTicks(1);
        Assert.All(squad.Members, m => Assert.True(a.Fighting(m)));
        var log = a.Aggro.Log.Where(e => squad.Members.Contains(e.Attacker)).ToList();
        Assert.Equal(2, log.Count);
        Assert.Equal(log[0].Tick, log[1].Tick);
        Assert.NotEqual(a.Combat.StateOf(squad.Members[0]).TargetSlot, a.Combat.StateOf(squad.Members[1]).TargetSlot);
    }

    /// <summary>Two AI packages, "210-t" with 16 Attacking enemies and "211-t" with 143 Protect close allies, for every role, all hours, weight 1.</summary>
    static GameDatabase RoamingPackages()
    {
        Meitou.Data.Fcs.FcsRecord Task(string id, TaskType type)
        {
            var r = SyntheticTown.Rec(id, Meitou.Data.Fcs.FcsRecordType.AI_TASK, type.ToString());
            (r.Ints["enum"], r.Ints["classification"], r.Ints["targeting"], r.Ints["ending"]) = ((int)type, (int)TaskPriority.Urgent, 0, 0);
            return r;
        }
        Meitou.Data.Fcs.FcsRecord Package(string id, string task)
        {
            var r = SyntheticTown.Rec(id, Meitou.Data.Fcs.FcsRecordType.AI_PACKAGE, id);
            r.References["Leader AI Goals"] = [SyntheticTown.Ref(task, 0, 100, 0)];
            r.References["Squad AI Goals"] = [SyntheticTown.Ref(task, 0, 100, 0)];
            return r;
        }
        var file = new Meitou.Data.Fcs.FcsFile();
        file.Records.AddRange([Task("200-t", TaskType.ATTACK_ENEMIES), Task("201-t", TaskType.PROTECT_OWN_SQUAD), Package("210-t", "200-t"), Package("211-t", "201-t")]);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return db;
    }

    /// <summary>
    /// A squad the AI does not run takes its combat goals from its package all the same (docs/simulation.md "AI": every character has goals, <c>setupAI</c>): of two roaming squads of the
    /// town that see a bandit, the one whose package lists 16 attacks it, the one whose package lists only 143 does not, and a member whose mind was dropped (as trade or recruitment do)
    /// gets one again. Catches the old stance-blind rule (attack on sight by the relations alone) for squads without a mind of the AI's.
    /// </summary>
    [Fact]
    public void A_roaming_squad_attacks_on_sight_only_by_its_packages_attack_goal()
    {
        using var a = Make(21, 1, TownRelations(), ai: true, db: RoamingPackages());
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var hunters = a.Roamers(Town, "210-t", o, o + new Vector2(-40, 0), o + new Vector2(-30, 0));
        var traders = a.Roamers(Town, "211-t", o, o + new Vector2(30, 0), o + new Vector2(40, 0));
        Think(a);
        var table = a.World.Characters;
        Assert.All(hunters.Members.Concat(traders.Members), m => Assert.True(table.Cold(m.Slot)!.Ai is { Engine: true }, "an engine-run mind"));
        table.Cold(hunters.Members[1].Slot)!.Ai = null;
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(0, 150), MathF.PI)).Members[0];
        for (int k = 0; k < (int)(4 / a.World.TickSeconds) && !hunters.Members.All(a.Fighting); k++)
        {
            a.World.RunTicks(1);
            Assert.All(traders.Members, m => Assert.False(a.Fighting(m), $"a 143-only roamer attacked at tick {a.World.Tick}"));
        }
        Assert.All(hunters.Members, m => Assert.Equal(bandit.Slot, a.Combat.StateOf(m).TargetSlot));
        Assert.True(table.Cold(hunters.Members[1].Slot)!.Ai is { Engine: true }, "the dropped mind is made again");
    }

    /// <summary>
    /// A squad whose template lists no AI packages has only the goals every character has (<c>setupAI</c>: 14, 32 and its race's, none of which attacks here): it starts no fight with an enemy in
    /// sight, the attacked member fights back (32), and its mate, with no 143, keeps out. Catches the old rule for squads without packages (attack on sight and the squad's join by relations).
    /// </summary>
    [Fact]
    public void A_squad_without_packages_starts_no_fight_and_only_the_attacked_member_answers()
    {
        using var a = Make(22, 1, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var folk = a.SquadOf(Town, o, true, (o, 0f), (o + new Vector2(10, 0), 0f));
        Think(a);
        var bandit = a.SquadOf(Bandits, o, true, (o + new Vector2(5, 60), MathF.PI)).Members[0];
        a.World.RunTicks((int)(2 / a.World.TickSeconds));
        Assert.True(a.World.Characters.Cold(folk.Members[0].Slot)!.Ai is { Engine: true, EngineGoals.Count: 0 });
        Assert.All(folk.Members, m => Assert.False(a.Fighting(m), "no attack on sight"));
        a.World.Commands.Enqueue(new AttackOrder([bandit], folk.Members[0]) { Tick = a.World.Tick });
        a.World.RunTicks((int)(3 / a.World.TickSeconds));
        Assert.True(a.Fighting(folk.Members[0]), "the attacked member fights back");
        Assert.Equal(bandit.Slot, a.Combat.StateOf(folk.Members[0]).TargetSlot);
        Assert.False(a.Fighting(folk.Members[1]), "the mate keeps out");
    }

    /// <summary>
    /// NPC combat goals in a running fight: a town squad that attacks on sight (16) and protects its own (143), a gate guard squad that protects allies (15) beside it, and a bandit squad

    /// without an AI that only answers; pinned in <see cref="GoldenHashTests"/> (with the 1, 4 and 16 thread agreement).
    /// </summary>
    internal static List<ulong> GoalFightHashes(int threads)
    {
        using var a = Make(16, threads, TownRelations(), ai: true);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var town = a.FacingSquad(Town, o, o + new Vector2(0, 300), o, o + new Vector2(12, 0), o + new Vector2(-12, 0));
        Lists(town, [Goal(TaskType.ATTACK_ENEMIES), Goal(TaskType.PROTECT_OWN_SQUAD)], [Goal(TaskType.PROTECT_OWN_SQUAD), Goal(TaskType.TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME)]);
        var gate = a.FacingSquad(Town, o, o + new Vector2(0, 300), o + new Vector2(-60, 0), o + new Vector2(-70, 10));
        Lists(gate, [Goal(TaskType.PROTECT_ALLIES)], [Goal(TaskType.PROTECT_ALLIES)]);
        a.World.RunTick();
        var bandits = a.FacingSquad(Bandits, o + new Vector2(0, 250), o, o + new Vector2(0, 250), o + new Vector2(15, 260), o + new Vector2(-15, 260));
        a.FacingSquad(Drifters, o + new Vector2(300, 0), o, o + new Vector2(300, 0));
        var hashes = new List<ulong>();
        foreach (int target in new[] { 30, 120, 400, 900 })
        {
            a.World.RunTicks(target - (int)a.World.Tick);
            hashes.Add(a.World.StateHash());
        }
        // The scenario covers what it is for: an attack on sight, a mate's protection and an ally's, all by the goals.
        Assert.Contains(a.Aggro.Log, e => e.Attacker == town.Members[0]);
        Assert.Contains(a.Aggro.Log, e => e.Attacker != town.Members[0] && town.Members.Contains(e.Attacker));
        Assert.Contains(a.Aggro.Log, e => gate.Members.Contains(e.Attacker));
        return hashes;
    }

    /// <summary>The crossroads with a player character among the raiders who then assaults a villager; pinned in <see cref="GoldenHashTests"/>.</summary>
    internal static List<ulong> Hashes(int threads)
    {
        var (a, _, _, _, _, _) = Crossroads(11, threads);
        using var _a = a;
        // A player character close to the raiders, who hate the player: it is attacked and defends itself; at tick 120 it is ordered onto a villager, an assault on the villagers.
        var me = a.PlayerCharacter(new Vector2(2000, 2080));
        var hashes = new List<ulong>();
        foreach (int target in new[] { 1, 30, 120, 400, 900 })
        {
            a.World.RunTicks(target - (int)a.World.Tick);
            if (target == 120) a.World.Commands.Enqueue(new AttackOrder([me], a.World.Squads.All.First(s => s.Faction == Villagers).Members[0]) { Tick = a.World.Tick });
            hashes.Add(a.World.StateHash());
        }
        var seen = (RelationChanges: a.Relations.Changes, a.Aggro.Started, Blows: a.Combat.Log.Count);
        // The scenario covers what it is for: fights started by hostility, blows, and relations changed by the blows (so the table is hashed).
        Assert.True(seen.RelationChanges > 0 && seen.Started > 0 && seen.Blows > 0, $"{seen}");
        Assert.NotEqual(hashes[^1], hashes[^2]);
        return hashes;
    }
}
