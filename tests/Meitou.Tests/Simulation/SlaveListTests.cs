using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Kidnap;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Tests.Combat;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// A squad's slave list (docs/simulation.md "Gather", "Shout"): its slave members and the slaves of other squads that obey it.
/// </summary>
public class SlaveListTests
{
    static AiGoal Goal(TaskType type) =>
        new(new AiTask("t", type.ToString(), type, TaskPriority.NonUrgent, TaskTargetType.Specific, TaskEndEvent.Nothing), 0, 24, 1);

    /// <summary>
    /// One package with the five role lists in <see cref="AiRole"/> order (the leader's is the third).
    /// </summary>
    static Blackboard Board(params List<AiGoal>[] roles)
    {
        var board = new Blackboard([new(0, new AiPackage { Id = "p", Name = "p", Roles = roles })]);
        board.Update();
        return board;
    }

    static Squad NewSquad(SimWorld world, int faction, Blackboard board)
    {
        var squad = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = faction, Ai = board };
        world.Squads.Add(squad);
        return squad;
    }

    /// <summary>
    /// A character of <paramref name="squad"/> at <paramref name="at"/>, run by the AI; the squad's leader when it has none.
    /// </summary>
    static CharacterId Member(SimWorld world, Squad squad, Vector3 at, Action<CharacterCold>? set = null)
    {
        var cold = new Combatant { Name = "member" }.MakeCold();
        cold.Ai = new AiMind { Squad = squad };
        cold.SquadId = squad.Id;
        cold.Faction = squad.Faction;
        set?.Invoke(cold);
        var id = world.Characters.Spawn(new CharacterHot { Position = at, Health = 100, MaxSpeed = 60, WalkSpeed = 15 }, cold, world.Tick);
        squad.Members.Add(id);
        if (squad.Leader.IsNone) squad.Leader = id;
        return id;
    }

    /// <summary>
    /// A slave of <paramref name="faction"/> in a squad of its own, obeying <paramref name="master"/>.
    /// </summary>
    static CharacterId ObeyingSlave(SimWorld world, int faction, Squad master, Vector3 at) =>
        Member(world, NewSquad(world, faction, Board([], [], [], [], [])), at,
                c => (c.SlaveState, c.SlaveOwner, c.SlaveMaster) = (1, faction, master.Id));

    static (SimWorld World, CaptiveSystem Captive, int City, Fixture Cage) Town(params ITickSystem[] more)
    {
        var data = ArrestTests.Town();
        var captive = new CaptiveSystem(data, null, new CaptiveSettings { RecordLog = true });
        var world = new SimWorld(new WorldSettings { PublishSnapshots = false, StartHours = 24 + 12 }, new OpenGroundWalkability((_, _) => 150),
                [new AiSystem(data), captive, .. more]);
        world.Fixtures.RegisterSite(world, data, 0);
        return (world, captive, data.FactionIndex("50-a"), world.Fixtures.Find("0.0:cageA")!);
    }

    /// <summary>
    /// 188 Working the slaves shouts at a slave on the worker's squad list (docs/game/ai-tasks.md, 188's finder over the platoon's slave list): here
    /// one of a slave squad of its own that obeys the worker's squad, as the Rebirth slaves obey their workmasters. Catches the list holding only the
    /// squad's own slave members, which left every workmaster of a town whose slaves are squads of their own with nobody to work.
    /// </summary>
    [Fact]
    public void A_worker_shouts_at_a_slave_of_another_squad_that_obeys_its_squad()
    {
        var (world, captive, city, cage) = Town();
        using var _ = world;
        var master = NewSquad(world, city, Board([], [], [Goal(TaskType.WorkTheSlaves)], [], []));
        var worker = Member(world, master, cage.Position + new Vector3(60, 0, 0));
        var slave = ObeyingSlave(world, city, master, cage.Position + new Vector3(80, 0, 0));
        world.RunTicks(61);

        Assert.Contains(captive.Log,
                e => e is { Kind: CaptiveEventKind.Started, Job: CaptiveJobKind.Shout } && e.Worker == worker && e.Captive == slave);
    }

    /// <summary>
    /// At its first slow world, starts a walk of row 43 Staying home (a goal's provenance) for <paramref name="who"/> to <paramref name="to"/> as
    /// 43's act-out does: under the AI's behaviour Post, so the AI holds the walk and its chooser may take it over.
    /// </summary>
    sealed class HomeWalker(Func<CharacterId> who, Vector2 to) : ITickSystem
    {
        bool _done;

        public void SlowWorld(SimWorld world)
        {
            if (_done || !world.Characters.TryResolveNext(who(), out int slot)) return;
            _done = true;
            TaskStart.Walk(world, slot, to, SpeedMode.Walk, TaskType.StayInHome, TaskSource.Goal);
            world.Characters.Cold(slot)!.Ai!.Behaviour = AiBehaviour.Post;
        }
    }

    /// <summary>
    /// A job's walk is its own (docs/simulation.md "Captives"): a gatherer that takes its 173 job while it walks away for another goal (a walk of row
    /// 43 Staying home, from 150 off the cage to 450 farther) turns to the cage at once and lifts the captive. Catches the cage walk that took any
    /// walk for its own, so Rebirth's workmasters, walking home when their gather jobs started, reached the cages only after most of the hour.
    /// </summary>
    [Fact]
    public void A_gatherer_walking_for_another_goal_turns_to_the_cage_at_once()
    {
        var worker = CharacterId.None;
        var walkability = new OpenGroundWalkability((_, _) => 150);
        var (world, captive, city, cage) = Town(new MovementSystem(new PathService(walkability, true)),
                new HomeWalker(() => worker, new Vector2(2200 + 600, 2000)));
        using var _ = world;

        Assert.Equal((2200f, 2000f), (cage.Position.X, cage.Position.Z));

        var master = NewSquad(world, city, Board([], [], [Goal(TaskType.GatherSlavesFromCages)], [], []));

        // It walks home at tick 2 (HomeWalker), before the look's first pick of 173.
        worker = Member(world, master, cage.Position + new Vector3(150, 0, 0));
        var prisoner = Member(world, NewSquad(world, city, Board([], [], [], [], [])), cage.Position);

        Assert.True(world.Fixtures.Imprison(world, prisoner, cage));

        bool Gathered() => captive.Log.Any(e => e is { Kind: CaptiveEventKind.Gathered } && e.Worker == worker && e.Captive == prisoner);
        for (int t = 0; t < 1200 && !Gathered(); t++) world.RunTicks(1);

        Assert.True(Gathered());
    }

    /// <summary>
    /// With its line on (a dialogue package with an event-38 line, ambient events on), the worker's Shout job ends once it says the line, and the
    /// chooser starts another on its slaves. Catches a job that never ended, or ended without a line.
    /// </summary>
    [Fact]
    public void A_worker_ends_its_shout_job_on_its_line_and_starts_another()
    {
        var shouter = SyntheticTown.Rec("shouter-c", Data.Fcs.FcsRecordType.Character, "Workmaster");
        shouter.References["dialogue package"] = [SyntheticTown.Ref("shout-p")];
        var package = SyntheticTown.Rec("shout-p", Data.Fcs.FcsRecordType.DialoguePackage, "Shouts");
        package.References["dialogs"] = [SyntheticTown.Ref("shout-d", CaptiveSystem.ShoutAtSlaveWorker)];
        var dialog = SyntheticTown.Rec("shout-d", Data.Fcs.FcsRecordType.Dialogue, "Shout");
        dialog.Bools["monologue"] = true;
        dialog.References["lines"] = [SyntheticTown.Ref("shout-l")];
        var line = SyntheticTown.Rec("shout-l", Data.Fcs.FcsRecordType.DialogueLine, "Shout line");
        line.Strings["text0"] = "Back to work!";
        var data = ArrestTests.Town([shouter, package, dialog, line]);
        var captive = new CaptiveSystem(data, null, new CaptiveSettings { RecordLog = true });
        var talk = new Meitou.Simulation.Dialogue.DialogueSystem(data, new Meitou.Simulation.Dialogue.DialogueSettings { AmbientEvents = true });
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false, StartHours = 24 + 12 }, new OpenGroundWalkability((_, _) => 150),
                [new AiSystem(data), captive, talk]);
        world.Fixtures.RegisterSite(world, data, 0);
        int city = data.FactionIndex("50-a");
        var cage = world.Fixtures.Find("0.0:cageA")!;
        var master = NewSquad(world, city, Board([], [], [Goal(TaskType.WorkTheSlaves)], [], []));
        var worker = Member(world, master, cage.Position + new Vector3(60, 0, 0), c => c.RecordId = "shouter-c");
        ObeyingSlave(world, city, master, cage.Position + new Vector3(80, 0, 0));

        world.RunTicks((int)(30 / world.TickSeconds));
        bool Logged(CaptiveEventKind kind) => captive.Log.Any(e => e.Job == CaptiveJobKind.Shout && e.Worker == worker && e.Kind == kind);

        Assert.True(Logged(CaptiveEventKind.Shouted));
        Assert.True(captive.Log.Count(e => e is { Kind: CaptiveEventKind.Started, Job: CaptiveJobKind.Shout } && e.Worker == worker) >= 2);
        Assert.False(Logged(CaptiveEventKind.GaveUp));
    }

    /// <summary>
    /// Catches the dialogue events the systems fire (there is no dialogue system here).
    /// </summary>
    sealed class EventLog : ITickSystem
    {
        public readonly List<Meitou.Simulation.Dialogue.DialogueEvent> Events = [];
        public void Inputs(SimWorld world,
                IReadOnlyList<SimCommand> commands) => Events.AddRange(commands.OfType<Meitou.Simulation.Dialogue.DialogueEvent>());
        public void Hash(ref StateHasher hasher) { }
    }

    /// <summary>
    /// A slave of <paramref name="owner"/> in a squad of its own, its head <paramref name="shaven"/>, wearing locked shackles when
    /// <paramref name="shackled"/>.
    /// </summary>
    static CharacterId LooseSlave(SimWorld world, int owner, Vector3 at, bool shaven, bool shackled) =>
        Member(world, NewSquad(world, owner, Board([], [], [], [], [])), at, c =>
        {
            (c.SlaveState, c.SlaveOwner, c.Shaved, c.Inventory) = (1, owner, shaven, new Meitou.Simulation.Items.Inventory());
            if (!shackled) return;
            var shackles = new Meitou.Simulation.Items.ItemInstance { Record = "shackles", Name = "shackles",
                    Lock = new LockState { Locked = true, Level = 10, Hardness = 5 } };
            c.Inventory.Add(shackles);
            shackles.Section = "boots";
        });

    /// <summary>
    /// 183 Watching slaves leaves a slave of its own side alone when its head is shaven and it looks like one (docs/game/ai-tasks.md "Looks like a
    /// slave": FUN_1408ca3d0 with CharacterHuman::isHeadShaven): a shaven slave in locked shackles draws no job, and once unshaven it does. Catches
    /// the needs-processing test that took every slave of an allied owner, which set Rebirth's guards, shopkeepers and workmasters on its 32 shaven
    /// slaves all hour.
    /// </summary>
    [Fact]
    public void A_watcher_leaves_a_shaven_slave_in_shackles_alone_and_processes_it_unshaven()
    {
        var (world, captive, city, cage) = Town();
        using var _ = world;
        var watcher = Member(world, NewSquad(world, city, Board([], [], [Goal(TaskType.SlaveWatching)], [], [])),
                cage.Position + new Vector3(60, 0, 0));
        var slave = LooseSlave(world, city, cage.Position + new Vector3(80, 0, 0), shaven: true, shackled: true);
        bool Watched() => captive.Log.Any(e => e is { Kind: CaptiveEventKind.Started,
                Job: CaptiveJobKind.Process } && e.Worker == watcher && e.Captive == slave);
        world.RunTicks(61);

        Assert.False(Watched());

        world.Characters.Cold(slave.Slot)!.Shaved = false;
        world.RunTicks(61);

        Assert.True(Watched());
    }

    /// <summary>
    /// Processing a slave shaves its head (docs/game/ai-tasks.md "Processing a slave shaves its head", FUN_14035aa20): a watcher processes an
    /// unshaven slave of its own side, which hears event 35 and ends shaven; one shaven already but without shackles hears 37. Catches the constant
    /// event 35 and a processing that leaves the head as it was, so the slave it made stayed a target. The slave is caged in the watcher's town: a
    /// loose one standing unshackled plans nothing (182 needs 134, whose provider 166 needs 7 isTargetDown; docs/simulation.md "Process").
    /// </summary>
    [Fact]
    public void Processing_a_slave_shaves_its_head_and_says_37_to_one_shaven_already()
    {
        foreach (bool shaven in new[] { false, true })
        {
            var log = new EventLog();
            var walkability = new OpenGroundWalkability((_, _) => 150);
            var (world, captive, city, cage) = Town(log, new MovementSystem(new PathService(walkability, true)));
            using var _ = world;
            var board = Board([], [], [Goal(TaskType.SlaveWatching)], [], []);
            board.Town = 0;
            var watcher = Member(world, NewSquad(world, city, board), cage.Position + new Vector3(60, 0, 0));
            var slave = LooseSlave(world, city, cage.Position, shaven, shackled: false);

            Assert.True(world.Fixtures.Imprison(world, slave, cage));

            bool Enslaved() => captive.Log.Any(e => e is { Kind: CaptiveEventKind.Enslaved } && e.Worker == watcher && e.Captive == slave);
            for (int t = 0; t < 1200 && !Enslaved(); t++) world.RunTicks(1);

            Assert.True(Enslaved(), $"shaven {shaven}: not processed");

            world.RunTicks(2);

            Assert.True(world.Characters.Cold(slave.Slot)!.Shaved);
            Assert.Contains(log.Events,
                    e => e.Npc == watcher && e.Target == slave
                    && e.Event == (shaven ? CaptiveSystem.RecapturedSlave : CaptiveSystem.IntroducingNewSlave));
        }
    }

    /// <summary>
    /// A member seated and committed under 43 Staying home weighs no captive goal of 43's tier (docs/simulation.md "Captives", the look; committed
    /// mode, FUN_140510ef0): an eater its chooser seated on its home's chair, a captive put in the cage, remembers no cage, has no "marked for
    /// death" line said and starts no job. Once its commitment runs out, its chooser takes 139 up and the job starts, which does both. Catches a
    /// look whose finder picks and announces a victim for every seated eater.
    /// </summary>
    [Fact]
    public void A_seated_eater_is_offered_its_goal_and_nothing_is_drawn_said_or_remembered_until_it_stands()
    {
        var walkability = new OpenGroundWalkability((_, _) => 150);
        var (world, captive, city, cage) = Town(new MovementSystem(new PathService(walkability, true)));
        using var _ = world;
        var seat = cage.Position + new Vector3(60, 0, 0);
        var board = Board([], [], [Goal(TaskType.StayInHome), Goal(TaskType.EatARandomCageOccupant)], [], []);
        board.OwnHome = board.Home = new TownBuilding("home", "b-home", "Home", seat, 0, false, false, [new HomeSpot(HomeSpotKind.Chair, seat, 0)]);
        var squad = NewSquad(world, city, board);
        var prisoner = Member(world, NewSquad(world, city, Board([], [], [], [], [])), cage.Position);
        var eater = Member(world, squad, seat);
        var cold = world.Characters.Cold(eater.Slot)!;
        var mind = cold.Ai!;
        for (int t = 0; t < 600 && world.Characters.Next[eater.Slot].Task != (byte)CharacterTask.Rest; t++) world.RunTicks(1);

        Assert.Equal((CharacterTask.Rest, TaskType.StayInHome), ((CharacterTask)world.Characters.Next[eater.Slot].Task, mind.Goal?.Task.Type));

        // Seated by its chooser under 43, committed an hour more, with a captive in the cage.
        Assert.True(world.Fixtures.Imprison(world, prisoner, cage));

        mind.CommitUntil = world.GameHours + 1;
        for (int t = 0; t < 61; t++)
        {
            world.RunTicks(1);

            Assert.Equal(0, world.DeferredCount);
        }

        Assert.Equal(CharacterTask.Rest, (CharacterTask)world.Characters.Next[eater.Slot].Task);
        Assert.Null(mind.FateCage);
        Assert.DoesNotContain(captive.Log, e => e.Worker == eater);

        mind.CommitUntil = world.GameHours;
        world.RunTicks(31);

        Assert.Contains(captive.Log,

            e => e is { Kind: CaptiveEventKind.Started, Job: CaptiveJobKind.Eat } && e.Worker == eater && e.Captive == prisoner);

        Assert.Equal((cage.Id, Data.Gameplay.WeaponKinds.None), (mind.FateCage, cold.DrawnWeapon));
    }

    /// <summary>
    /// The jail town with a second faction, <c>60-a</c> (neither allied to the city nor its enemy), and the dialogue system
    /// <paramref name="makeTalk"/> makes: the captive system, the AI and the town's two cages, A listed first.
    /// </summary>
    static (SimWorld World, CaptiveSystem Captive, int City, int Others, Fixture CageA, Fixture CageB) TwoFactionTown(
            IEnumerable<Data.Fcs.FcsRecord>? extra = null,
            Func<Meitou.Simulation.Population.PopulationData, Meitou.Simulation.Dialogue.DialogueSystem>? makeTalk = null)
    {
        var data = ArrestTests.Town([SyntheticTown.Rec("60-a", Data.Fcs.FcsRecordType.Faction, "others"), .. extra ?? []]);
        var captive = new CaptiveSystem(data, null, new CaptiveSettings { RecordLog = true });
        var systems = new List<ITickSystem> { new AiSystem(data), captive };
        if (makeTalk?.Invoke(data) is { } dialogue) systems.Add(dialogue);
        var world = new SimWorld(new WorldSettings { PublishSnapshots = false, StartHours = 24 + 12 }, new OpenGroundWalkability((_, _) => 150),
                systems);
        world.Fixtures.RegisterSite(world, data, 0);
        var cages = world.Fixtures.All.Where(f => f.Kind == FixtureKind.Cage).ToList();

        Assert.Equal(["0.0:cageA", "0.0:cageB"], cages.Select(f => f.Id));

        return (world, captive, data.FactionIndex("50-a"), data.FactionIndex("60-a"), cages[0], cages[1]);
    }

    /// <summary>
    /// A city NPC of a squad of its own, imprisoned in <paramref name="cage"/>.
    /// </summary>
    static CharacterId Caged(SimWorld world, int city, Fixture cage)
    {
        var prisoner = Member(world, NewSquad(world, city, Board([], [], [], [], [])), cage.Position);

        Assert.True(world.Fixtures.Imprison(world, prisoner, cage));

        return prisoner;
    }

    /// <summary>
    /// 276's finder takes the first listed qualifying cage, with no distance test (docs/game/ai-tasks.md goal 276, <c>FUN_1408cf140</c> over
    /// <c>FUN_1409fc450</c>, 7eea7e88e): a liberator standing by cage B, both cages of the city holding a city prisoner, is given cage A, the first
    /// in the registry. Catches the nearest-cage pick the finder had before.
    /// </summary>
    [Fact]
    public void A_liberator_takes_the_first_listed_cage_not_the_nearest()
    {
        var (world, captive, city, others, cageA, cageB) = TwoFactionTown();
        using var _ = world;
        Caged(world, city, cageA);
        Caged(world, city, cageB);
        var liberator = Member(world, NewSquad(world, others, Board([], [], [], [], [])), cageB.Position + new Vector3(20, 0, 0));
        var cold = world.Characters.Cold(liberator.Slot)!;

        Assert.Equal(cageA.Id, captive.LiberateTarget(world, liberator.Slot, cold)?.Id);
    }

    /// <summary>
    /// 276's slot-0 test (<c>FUN_1408db880</c>, f2bb0011a): a cage of no faction fails the allied test, so its captive is freed, while a captive the
    /// liberator's squad hates (long-term tag CAPTOR, <c>FUN_1405995d0</c> &amp; 0x2b0) is passed over though its cage is listed first. The liberator
    /// runs 276 from its leader list and starts its job on cage B's captive. Catches the finder that skipped ownerless cages and the one with no
    /// hate test.
    /// </summary>
    [Fact]
    public void A_liberator_frees_a_captive_from_a_cage_of_no_faction_and_skips_one_its_squad_hates()
    {
        var (world, captive, city, others, cageA, cageB) = TwoFactionTown(makeTalk: data => new Meitou.Simulation.Dialogue.DialogueSystem(data));
        using var _ = world;
        var hated = Caged(world, city, cageA);
        var freed = Caged(world, city, cageB);
        cageB.Owner = -1;
        var squad = NewSquad(world, others, Board([], [], [Goal(TaskType.LiberateAllThePrisoners)], [], []));
        var liberator = Member(world, squad, cageA.Position + new Vector3(60, 0, 0));
        world.SystemOf<Meitou.Simulation.Dialogue.DialogueSystem>()!.Memory.Remember(world, squad.Id, hated, Meitou.Simulation.Dialogue.MemoryTag.Captor);
        bool Started(CharacterId who) => captive.Log.Any(e => e is { Kind: CaptiveEventKind.Started, Job: CaptiveJobKind.Liberate }
                && e.Worker == liberator && e.Captive == who);
        for (int t = 0; t < 600 && !Started(freed); t++) world.RunTicks(1);

        Assert.True(Started(freed));
        Assert.False(Started(hated));
    }

    /// <summary>
    /// A dialogue package whose event-39 SLAVE_DELIVERY monologue says a line with DA 24 GIVE_TARGET_MY_SLAVES, as the base data's
    /// <c>slave delivery</c> lines do (docs/simulation.md "Sell"), on the character record <c>slaver-c</c>.
    /// </summary>
    static Data.Fcs.FcsRecord[] DeliveryLines()
    {
        var slaver = SyntheticTown.Rec("slaver-c", Data.Fcs.FcsRecordType.Character, "Slaver");
        slaver.References["dialogue package"] = [SyntheticTown.Ref("deliver-p")];
        var package = SyntheticTown.Rec("deliver-p", Data.Fcs.FcsRecordType.DialoguePackage, "Delivers");
        package.References["dialogs"] = [SyntheticTown.Ref("deliver-d", CaptiveSystem.SlaveDelivery)];
        var dialog = SyntheticTown.Rec("deliver-d", Data.Fcs.FcsRecordType.Dialogue, "Delivery");
        dialog.Bools["monologue"] = true;
        dialog.References["lines"] = [SyntheticTown.Ref("deliver-l")];
        var line = SyntheticTown.Rec("deliver-l", Data.Fcs.FcsRecordType.DialogueLine, "Delivery line");
        line.Strings["text0"] = "Fresh stock for the pens.";
        line.References["effects"] = [SyntheticTown.Ref("deliver-a")];
        var give = SyntheticTown.Rec("deliver-a", Data.Fcs.FcsRecordType.DialogAction, "Give slaves");
        give.Ints["action name"] = Data.Gameplay.Dialogue.DialogueActions.GiveTargetMySlaves;
        return [slaver, package, dialog, line, give];
    }

    /// <summary>
    /// Event 39 to a pen keeper (f8f592ba0; DA 24 <c>FUN_140680560</c>, docs/simulation.md "Sell"): the slaver's line hands both slaves on its
    /// squad's list to the keeper: their owner is the keeper's faction and their master its squad, each keeps its own squad, and the slaver's list is
    /// empty. Catches the engine handover that moved the slaves into squads of their own and cages.
    /// </summary>
    [Fact]
    public void Event_39_to_a_pen_keeper_gives_it_the_slavers_slaves_and_leaves_their_squads()
    {
        var (world, _, city, others, cageA, _) = TwoFactionTown(DeliveryLines(),
                makeTalk: data => new Meitou.Simulation.Dialogue.DialogueSystem(data,
                        new Meitou.Simulation.Dialogue.DialogueSettings { AmbientEvents = true }));
        using var __ = world;
        var slavers = NewSquad(world, others, Board([], [], [], [], []));
        var slaver = Member(world, slavers, cageA.Position + new Vector3(60, 0, 0), c => c.RecordId = "slaver-c");
        var keepers = NewSquad(world, city, Board([], [], [], [], []));
        var keeper = Member(world, keepers, cageA.Position + new Vector3(80, 0, 0));
        var slaves = new[]
        {
            ObeyingSlave(world, others, slavers, cageA.Position + new Vector3(70, 0, 10)),
            ObeyingSlave(world, others, slavers, cageA.Position + new Vector3(70, 0, -10)),
        };
        var squads = slaves.Select(s => world.Characters.Cold(s.Slot)!.SquadId).ToList();

        Assert.Equal(2, CaptiveSystem.SlavesOf(world, slavers).Count());

        world.Commands.Enqueue(new Meitou.Simulation.Dialogue.DialogueEvent(slaver, keeper, CaptiveSystem.SlaveDelivery) { Tick = world.Tick });
        world.RunTicks(30);

        Assert.All(slaves, s => Assert.Equal((city, keepers.Id),
                (world.Characters.Cold(s.Slot)!.SlaveOwner, world.Characters.Cold(s.Slot)!.SlaveMaster)));
        Assert.Equal(squads, slaves.Select(s => world.Characters.Cold(s.Slot)!.SquadId));
        Assert.Empty(CaptiveSystem.SlavesOf(world, slavers));
    }
}
