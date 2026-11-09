using System.Numerics;
using Meitou.Data.Gameplay.Ai;
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
                [captive, .. more]);
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
    /// At its first slow world, starts a walk of row 43 Staying home (a goal's provenance) for <paramref name="who"/> to <paramref name="to"/> and
    /// lets the captive sweep have it.
    /// </summary>
    sealed class HomeWalker(Func<CharacterId> who, Vector2 to) : ITickSystem
    {
        bool _done;

        public void SlowWorld(SimWorld world)
        {
            if (_done || !world.Characters.TryResolveNext(who(), out int slot)) return;
            _done = true;
            TaskStart.Walk(world, slot, to, SpeedMode.Walk, TaskType.StayInHome, TaskSource.Goal);
            world.Characters.Cold(slot)!.Engaged = false;
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

        // Held off the sweep until it walks home (HomeWalker), a walk the AI holds (43's act-out).
        worker = Member(world, master, cage.Position + new Vector3(150, 0, 0), c => (c.Ai!.Behaviour, c.Engaged) = (AiBehaviour.Post, true));
        var prisoner = Member(world, NewSquad(world, city, Board([], [], [], [], [])), cage.Position);

        Assert.True(world.Fixtures.Imprison(world, prisoner, cage));

        bool Gathered() => captive.Log.Any(e => e is { Kind: CaptiveEventKind.Gathered } && e.Worker == worker && e.Captive == prisoner);
        for (int t = 0; t < 1200 && !Gathered(); t++) world.RunTicks(1);

        Assert.True(Gathered());
    }

    /// <summary>
    /// With its line on (a dialogue package with an event-38 line, ambient events on), the worker's Shout job ends once it says the line, and the
    /// next sweep starts another on its slaves. Catches a job that never ended, or ended without a line.
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
                [captive, talk]);
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
    /// event 35 and a processing that leaves the head as it was, so the slave it made stayed a target.
    /// </summary>
    [Fact]
    public void Processing_a_slave_shaves_its_head_and_says_37_to_one_shaven_already()
    {
        foreach (bool shaven in new[] { false, true })
        {
            var log = new EventLog();
            var (world, captive, city, cage) = Town(log);
            using var _ = world;
            var watcher = Member(world, NewSquad(world, city, Board([], [], [Goal(TaskType.SlaveWatching)], [], [])),
                    cage.Position + new Vector3(60, 0, 0));
            var slave = LooseSlave(world, city, cage.Position + new Vector3(70, 0, 0), shaven, shackled: false);
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
    /// At its first slow world, seats <paramref name="who"/> as 43 Staying home does on a chair (task Rest, a goal's provenance; its AI behaviour Sit
    /// is the test's) and lets the captive sweep see it; once <see cref="Up"/> is set, stands it up (inside the tick, where a task holds).
    /// </summary>
    sealed class HomeSitter(Func<CharacterId> who) : ITickSystem
    {
        bool _seated, _stood;

        public bool Up { get; set; }

        public void SlowWorld(SimWorld world)
        {
            if (!world.Characters.TryResolveNext(who(), out int slot)) return;
            if (!_seated)
            {
                _seated = true;
                TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Rest, TaskType.StayInHome, TaskSource.Goal));
                world.Characters.Cold(slot)!.Engaged = false;
            }
            else if (Up && !_stood)
            {
                _stood = true;
                TaskStart.Stand(world.Characters, slot);
            }
        }
    }

    /// <summary>
    /// A seated member's sweep turn only finds out which captive goals would start (docs/simulation.md "Captives", weighed by the AI): an eater on
    /// its chair with a captive in the cage keeps 139 as an offer for its think, and remembers no cage, sheathes nothing, has no "marked for death"
    /// line said and starts no job. Once it stands, the sweep starts the job, which does all of these. Catches a probe with the job start's side
    /// effects, which would pick and announce a victim for every seated eater, every sweep.
    /// </summary>
    [Fact]
    public void A_seated_eater_is_offered_its_goal_and_nothing_is_drawn_said_or_remembered_until_it_stands()
    {
        var eater = CharacterId.None;
        var sitter = new HomeSitter(() => eater);
        var (world, captive, city, cage) = Town(sitter);
        using var _ = world;
        var squad = NewSquad(world, city, Board([], [], [Goal(TaskType.EatARandomCageOccupant)], [], []));
        var prisoner = Member(world, NewSquad(world, city, Board([], [], [], [], [])), cage.Position);

        Assert.True(world.Fixtures.Imprison(world, prisoner, cage));

        // Held off the sweep until it sits (HomeSitter).
        eater = Member(world, squad, cage.Position + new Vector3(60, 0, 0),
            c => (c.Ai!.Behaviour, c.DrawnWeapon, c.Engaged) = (AiBehaviour.Sit, Data.Gameplay.WeaponKinds.Katana, true));
        var cold = world.Characters.Cold(eater.Slot)!;
        for (int t = 0; t < 61; t++)
        {
            world.RunTicks(1);

            Assert.Equal(0, world.DeferredCount);
        }

        Assert.Equal(CharacterTask.Rest, (CharacterTask)world.Characters.Next[eater.Slot].Task);
        Assert.Equal([(TaskType.EatARandomCageOccupant, 1f)], cold.Ai!.CaptiveOffers);
        Assert.Equal((null, Data.Gameplay.WeaponKinds.Katana), (cold.Ai.FateCage, cold.DrawnWeapon));
        Assert.DoesNotContain(captive.Log, e => e.Worker == eater);

        sitter.Up = true;
        world.RunTicks(31);

        Assert.Contains(captive.Log,

            e => e is { Kind: CaptiveEventKind.Started, Job: CaptiveJobKind.Eat } && e.Worker == eater && e.Captive == prisoner);

        Assert.Equal((cage.Id, Data.Gameplay.WeaponKinds.None), (cold.Ai.FateCage, cold.DrawnWeapon));
        Assert.Empty(cold.Ai.CaptiveOffers);
    }
}
