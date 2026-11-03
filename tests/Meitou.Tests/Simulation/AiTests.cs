using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Data.World;
using static Meitou.Tests.Simulation.SyntheticTown;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>The AI (docs/simulation.md "AI"): the package and goal data as docs/game/ai.md reads them, the package choice, and a town that patrols by day and sleeps at night.</summary>
public class AiTests(ITestOutputHelper output)
{
    static FcsRecord Task(string id, TaskType type, TaskPriority tier, TaskTargetType target = TaskTargetType.Specific)
    {
        var r = Rec(id, FcsRecordType.AI_TASK, type.ToString());
        r.Ints["enum"] = (int)type;
        r.Ints["classification"] = (int)tier;
        r.Ints["targeting"] = (int)target;
        r.Ints["ending"] = 0;
        return r;
    }

    /// <summary>
    /// Two packages: "Town base" (the leader keeps the shop; the others patrol 6 to 22, go to bed 22 to 6 at half weight, and have an urgent goal the engine
    /// does not act out) and "Town" inheriting it (a duplicate shop goal at weight 0.5, which the parent's beats in the leader list and which its squad list takes
    /// by the fallback, and a slave goal); a war package of a class not built (signal 20); a running patrol (signal 23: the leader patrols, the others follow);
    /// a retreat (signal 5: everyone goes home); a timed contract (signal 33: everyone sits). The resident Patrol template lists "Town" at slot 0 and the war at slot 1
    /// unless a test gives it others.
    /// </summary>
    internal static List<FcsRecord> Records()
    {
        var shop = Task("100-t", TaskType.STAND_AT_SHOPKEEPER_NODE, TaskPriority.NonUrgent);
        var patrol = Task("101-t", TaskType.PATROL_TOWN, TaskPriority.NonUrgent);
        var bed = Task("102-t", TaskType.GO_HOME_AND_GO_TO_BED, TaskPriority.NonUrgent);
        var raid = Task("103-t", TaskType.RAID_TOWN, TaskPriority.Urgent);
        var sit = Task("105-t", TaskType.SIT_AROUND, TaskPriority.Fluff);
        var parent = Rec("110-t", FcsRecordType.AI_PACKAGE, "Town base");
        parent.References["Leader AI Goals"] = [Ref("100-t", 0, 100, 0)];
        parent.References["Squad AI Goals"] = [Ref("101-t", 6, 22, 0), Ref("102-t", 22, 6, 50), Ref("103-t", 0, 100, 0)];
        var child = Rec("111-t", FcsRecordType.AI_PACKAGE, "Town");
        child.References["inherits from"] = [Ref("110-t")];
        child.References["Leader AI Goals"] = [Ref("100-t", 0, 0, 50)];
        child.References["Slave AI Goals"] = [Ref("105-t", 0, 100, 0)];
        var war = Rec("112-t", FcsRecordType.AI_PACKAGE, "War");
        war.Ints["signal func"] = 20;
        war.References["Squad AI Goals"] = [Ref("101-t")];
        var walk = Task("120-t", TaskType.PATROL, TaskPriority.NonUrgent);
        var follow = Task("121-t", TaskType.FOLLOW_SQUADLEADER, TaskPriority.NonUrgent, TaskTargetType.Leader);
        var retreat = Task("122-t", TaskType.RETREAT_HOMETOWN, TaskPriority.NonUrgent);
        var outing = Rec("113-t", FcsRecordType.AI_PACKAGE, "Go out on patrol");
        outing.Ints["signal func"] = 23;
        outing.References["Leader AI Goals"] = [Ref("120-t")];
        outing.References["Squad AI Goals"] = [Ref("121-t")];
        var refresh = Rec("114-t", FcsRecordType.AI_PACKAGE, "Retreat and refresh");
        refresh.Ints["signal func"] = 5;
        refresh.References["Leader AI Goals"] = [Ref("122-t")];
        var hired = Rec("115-t", FcsRecordType.AI_PACKAGE, "Timed contract");
        hired.Ints["signal func"] = 33;
        hired.References["Leader AI Goals"] = [Ref("105-t", 0, 100, 0)];
        return [shop, patrol, bed, raid, sit, parent, child, war, walk, follow, retreat, outing, refresh, hired];
    }

    internal static GameDatabase Database(int residentSquads = 3, params (string Id, int Slot)[] packages) =>
        SyntheticTown.Database(residentSquads, extra: Records(), changePatrol: p => p.References["AI packages"] =
            packages.Length == 0 ? [Ref("111-t", 0), Ref("112-t", 1)] : [.. packages.Select(x => Ref(x.Id, x.Slot))]);

    [Fact]
    public void Packages_load_the_parent_first_and_the_parent_wins()
    {
        var ai = new AiDatabase(Database());
        var town = ai.Package("111-t")!;
        // The leader's shop goal comes from the parent: weight 1 (val2 0), window (0, 100); the child's duplicate is dropped.
        var lead = Assert.Single(town.Goals(AiRole.Leader));
        Assert.Equal((TaskType.STAND_AT_SHOPKEEPER_NODE, 0, 100, 1f), (lead.Task.Type, lead.Start, lead.End, lead.Weight));
        // Squad: the parent's own list, then the child's, which has no squad goals and falls back to its leader goals.
        Assert.Equal([TaskType.PATROL_TOWN, TaskType.GO_HOME_AND_GO_TO_BED, TaskType.RAID_TOWN, TaskType.STAND_AT_SHOPKEEPER_NODE], town.Goals(AiRole.Squad).Select(g => g.Task.Type));
        Assert.Equal(0.5f, town.Goals(AiRole.Squad)[1].Weight);
        Assert.Equal((0, 0, 0.5f), (town.Goals(AiRole.Squad)[3].Start, town.Goals(AiRole.Squad)[3].End, town.Goals(AiRole.Squad)[3].Weight));
        Assert.Equal(town.Goals(AiRole.Squad).Select(g => g.Task.Type), town.Goals(AiRole.Squad2).Select(g => g.Task.Type));
        Assert.Equal([TaskType.SIT_AROUND], town.Goals(AiRole.Slave).Select(g => g.Task.Type));
        Assert.Empty(town.Goals(AiRole.Unused));
        Assert.True(town.IsPlain);
        Assert.False(ai.Package("112-t")!.IsPlain);
        Assert.Equal(0, ai.Problems);
    }

    static AiPackage Package(string id, int signal = 0, string? leadsTo = null) =>
        new() { Id = id, Name = id, Signal = signal, LeadsTo = leadsTo, Roles = [[], [], [], [], []] };

    [Fact]
    public void The_highest_slot_that_can_start_wins()
    {
        var town = Package("town");
        var patrol = Package("patrol", signal: 6);   // GoOutOnPatrol: not built, so it never starts by itself
        var bar = Package("bar", signal: 0);
        var board = new Blackboard([new(0, town), new(1, patrol)]);
        board.Update();
        Assert.Same(town, board.Current);
        board = new Blackboard([new(0, town), new(2, bar), new(1, patrol)]);
        board.Update();
        Assert.Same(bar, board.Current);
        Assert.Equal(2, board.CurrentSlot);
        int version = board.Version;
        board.Update();
        Assert.Equal(version, board.Version);   // the current package stays
        // A lone slot-0 package starts even when it cannot; two such packages in slot 0 do not.
        board = new Blackboard([new(0, patrol)]);
        board.Update();
        Assert.Same(patrol, board.Current);
        board = new Blackboard([new(0, patrol), new(0, Package("other", signal: 6))]);
        board.Update();
        Assert.Null(board.Current);
    }

    static SimWorld Town(int threads, out PopulationData data) => Town(threads, out data, null, null);

    static SimWorld Town(int threads, out PopulationData data, PopulationData? given, AiSettings? settings, double startHours = 24 + 13)
    {
        data = given ?? SyntheticTown.Data(Database());
        var walk = new OpenGroundWalkability(Ground);
        var systems = StandardSystems.Build(data, walk, new StandardSystemOptions
        {
            Parts = StandardParts.Population | StandardParts.Movement | StandardParts.Ai, SynchronousPaths = true, Ai = settings,
        }).Systems;
        var world = new SimWorld(new WorldSettings { Seed = 5, Threads = threads, StartHours = startHours, MinPartitionSize = 4 }, walk, systems);
        world.Commands.Enqueue(new FocusCommand(Centre) { Tick = 0 });
        return world;
    }

    /// <summary>The town's AI by day, then from 23:00 on; pinned in <see cref="GoldenHashTests"/>.</summary>
    internal static List<ulong> Scripted(int threads)
    {
        using var world = Town(threads, out _);
        world.RunTicks(120);
        var hashes = new List<ulong> { world.StateHash() };
        world.GameHours = 24 + 23;
        world.RunTicks(240);
        hashes.Add(world.StateHash());
        return hashes;
    }

    /// <summary>The counts docs/game/ai.md gives for the merged base game (gamedata.base, Newwworld, Dialogue, rebirth), read through <see cref="AiDatabase"/>.</summary>
    [Fact, Slow]
    public void The_base_game_AI_records_load_as_documented()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var db = InstallData.BaseGame!;
        var packages = db.OfType(FcsRecordType.AI_PACKAGE).ToList();
        var tasks = db.OfType(FcsRecordType.AI_TASK).ToList();
        Assert.Equal(219, packages.Count);
        Assert.Equal(124, tasks.Count);
        int Entries(string list) => packages.Sum(p => p.GetReferences(list).Count);
        Assert.Equal((804, 561, 58, 64), (Entries("Leader AI Goals"), Entries("Squad AI Goals"), Entries("Squad2 AI Goals"), Entries("Slave AI Goals")));
        var ai = new AiDatabase(db);
        Assert.All(tasks, t => Assert.InRange((int)ai.Task(t.StringId)!.Type, 1, 290));
        Assert.All(tasks, t => Assert.InRange((int)ai.Task(t.StringId)!.Priority, 1, 3));
        foreach (var p in packages) Assert.NotNull(ai.Package(p.StringId));
        Assert.Equal(0, ai.Problems);
        var templates = Meitou.Data.Gameplay.SquadTemplate.LoadAll(db);
        int withPackages = templates.Count(t => ai.Packages(t).Count > 0);
        var used = tasks.Select(t => ai.Task(t.StringId)!.Type).Distinct().ToList();
        var built = used.Where(t => TaskBehaviours.Of(t) != AiBehaviour.None).ToList();
        output.WriteLine($"{withPackages} templates with packages; {built.Count} of {used.Count} task types used acted out: {string.Join(", ", built)}");
        Assert.Equal(0, ai.Problems);
    }

    // ------------------------------------------------------------------ homes, package classes, stances, platoons

    /// <summary>
    /// The synthetic town with two buildings: a shop (a shopkeeper node, two beds) east of the centre and a house (three beds,
    /// <paramref name="bedGap"/> apart) west of it.
    /// </summary>
    static PopulationData WithHomes(GameDatabase db, out TownBuilding shop, out TownBuilding house, float bedGap = 10)
    {
        Vector3 At(float dx, float dz) => new(Centre.X + dx, 300, Centre.Z + dz);
        shop = new TownBuilding("shop", "b-shop", "Shop", At(80, 0), 0, true, false,
            [new HomeSpot(HomeSpotKind.Shopkeeper, At(80, 12), 0), new HomeSpot(HomeSpotKind.Bed, At(90, 0), 0), new HomeSpot(HomeSpotKind.Bed, At(90, -10), 0)]);
        house = new TownBuilding("house", "b-house", "House", At(-80, 0), 0, false, false,
            [
                new HomeSpot(HomeSpotKind.Bed, At(-90, 0), 0), new HomeSpot(HomeSpotKind.Bed, At(-90, bedGap), 0),
                new HomeSpot(HomeSpotKind.Bed, At(-90, 2 * bedGap), 0),
            ]);
        return PopulationData.Create(db, [new TownPlacement("inst", "30-t", Centre, null)], buildings: new Dictionary<string, List<TownBuilding>> { ["inst"] = [shop, house] });
    }

    static float Off(SimWorld world, CharacterId id, Vector3 spot)
    {
        var p = world.Characters.Previous[id.Slot].Position;
        return Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(spot.X, spot.Z));
    }

    [Fact]
    public void Squads_get_homes_keep_shop_at_its_node_and_sleep_in_their_beds()
    {
        var data = WithHomes(Database(2), out var shop, out var house);
        using var world = Town(1, out _, data, null);
        world.RunTicks(400);
        var squads = world.Squads.All.Where(s => s.TemplateId == "10-t").ToList();
        Assert.Equal(2, squads.Count);
        // One home each, none shared.
        Assert.Equal(2, squads.Select(s => s.Ai!.Home).Distinct().Count());
        Assert.All(squads, s => Assert.Contains(s.Ai!.Home, new[] { shop, house }));
        // The shop's leader stands at the shopkeeper node; the house has no node, so its leader keeps its own post.
        var shopSquad = squads.Single(s => s.Ai!.Home == shop);
        Assert.InRange(Off(world, shopSquad.Leader, shop.Spots[0].Position), 0, 13);
        Assert.Equal(CharacterTask.Idle, (CharacterTask)world.Characters.Previous[shopSquad.Leader.Slot].Task);

        world.GameHours = 24 + 22.5;
        world.RunTicks(1200);
        foreach (var squad in squads)
        {
            var beds = squad.Ai!.Home!.Of(HomeSpotKind.Bed).Select(b => b.Position).ToList();
            var sleepers = squad.Members.Where(m => m != squad.Leader).ToList();
            foreach (var m in sleepers)
            {
                Assert.Contains((CharacterTask)world.Characters.Previous[m.Slot].Task, new[] { CharacterTask.Sleep, CharacterTask.Rest });
                Assert.InRange(beds.Min(b => Off(world, m, b)), 0, 13);
            }
            // Every bed is taken (lain in, task Sleep); the sleepers left over rest beside them.
            Assert.Equal(Math.Min(beds.Count, sleepers.Count), sleepers.Count(m => world.Characters.Cold(m.Slot)!.InSomething == Meitou.Simulation.Locks.HeldState.Bed));
        }
    }

    /// <summary>
    /// A character already lying in a bed keeps it when it goes to bed (docs/game/locks.md "Bed search"): sleepers moved into each other's beds of
    /// their home (30 apart, beyond the post slack), with no goal yet as after a load, stay in those beds, plan 98 Sleeping first and sleep as the
    /// goal's own task. Catches the goal aiming at the bed its member index picks, whose plan got the saved sleepers of a loaded town out of their
    /// beds, and a sleep left with the load's or the walk's provenance, which has no order.
    /// </summary>
    [Fact]
    public void Sleepers_lying_in_other_beds_of_their_home_keep_them_and_plan_sleeping_first()
    {
        var data = WithHomes(Database(2), out _, out var house, bedGap: 30);
        using var world = Town(1, out _, data, null);
        world.RunTicks(400);
        world.GameHours = 24 + 22.5;
        world.RunTicks(1200);
        var squad = world.Squads.All.Single(s => s.TemplateId == "10-t" && s.Ai!.Home == house);
        var lying = squad.Members.Where(m => world.Characters.Cold(m.Slot)!.InSomething == Meitou.Simulation.Locks.HeldState.Bed).Take(2).ToList();
        Assert.Equal(2, lying.Count);

        // Each into the other's bed, as a save may hold them; no goal or plan yet, as on load.
        var beds = lying.Select(m => world.Fixtures.Find(world.Characters.Cold(m.Slot)!.InWhat!)!).ToList();
        foreach (var m in lying) world.Fixtures.LeaveBed(world, m);
        for (int k = 0; k < 2; k++) Assert.True(world.Fixtures.EnterBed(world, lying[k], beds[1 - k]));
        foreach (var m in lying)
        {
            var mind = world.Characters.Cold(m.Slot)!.Ai!;
            mind.Goal = null;
            mind.Plan.Clear();
        }
        world.RunTicks(300);
        for (int k = 0; k < 2; k++)
        {
            var cold = world.Characters.Cold(lying[k].Slot)!;
            Assert.Equal((Meitou.Simulation.Locks.HeldState.Bed, beds[1 - k].Id), (cold.InSomething, cold.InWhat));
            Assert.Equal((TaskType.GO_HOME_AND_GO_TO_BED, TaskType.USE_BED), (cold.Ai!.Goal!.Task.Type, cold.Ai.Step));
            // The sleep is the goal's own, so its plan step is its order.
            var run = cold.Running!;
            Assert.Equal((TaskSource.Goal, TaskType.GO_HOME_AND_GO_TO_BED, CharacterTask.Sleep), (run.Source, run.Row, run.Task));
        }
    }

    /// <summary>
    /// A goal it committed to goes on past a step of its own once that step is done (docs/simulation.md "Planner"): sleepers with a weapon drawn
    /// sheathe it, then walk to their beds. Catches the kept plan repeating its done first step, which held a character where it stood for the whole
    /// commitment.
    /// </summary>
    [Fact]
    public void A_committed_goal_goes_on_past_its_done_first_step_so_armed_sleepers_sheathe_and_go_to_bed()
    {
        var data = WithHomes(Database(2), out _, out var house);
        using var world = Town(1, out _, data, null);
        world.RunTicks(400);
        var squad = world.Squads.All.Single(s => s.TemplateId == "10-t" && s.Ai!.Home == house);
        var sleepers = squad.Members.Where(m => m != squad.Leader).ToList();
        Assert.NotEmpty(sleepers);
        foreach (var m in sleepers) world.Characters.Cold(m.Slot)!.DrawnWeapon = Meitou.Data.Gameplay.WeaponKinds.Katana;

        world.GameHours = 24 + 22.5;
        world.RunTicks(1200);
        var beds = house.Of(HomeSpotKind.Bed).Select(b => b.Position).ToList();
        foreach (var m in sleepers)
        {
            Assert.Equal(Meitou.Data.Gameplay.WeaponKinds.None, world.Characters.Cold(m.Slot)!.DrawnWeapon);
            Assert.InRange(beds.Min(b => Off(world, m, b)), 0, 13);
        }
    }

    /// <summary>A character's world for the planner: in bed or not, at the node or not; nothing drawn, carried or attacking; the rest held true.</summary>
    sealed class PlanStub(bool inBed, bool atNode, int category = 1) : IPlanWorld
    {
        public int Category => category;
        public bool Squadless => false;
        public float Desire(TaskDataRow row, in PlanTarget target) => 1;
        public PlanTarget Find(TaskDataRow row, in PlanTarget from) => new(1, new Vector3(10, 0, 10));
        public bool Holds(TaskCondition c, in PlanTarget target) => c.Holds == c.State switch
        {
            StateType.isInBed => inBed,
            StateType.isAtLocation or StateType.atNode => atNode,
            StateType.hasWeaponEquipped or StateType.hasWeaponEquipped_156 or StateType.hasWeaponEquipped_157 or StateType.IS_CARRYING_SOMETHING
                or StateType.iAmBeingMeleeAttacked_immediate or StateType.isCrouching or StateType.IS_CROUCHING_OR_LYING or StateType.iAmProne => false,
            _ => true,
        };
    }

    [Fact]
    public void A_goal_plans_through_the_TaskData_rows_and_one_without_a_plan_gives_way_to_the_next()
    {
        // In bed and away from its node, a general-node goal chains getting up, the walk and standing at the node (docs/game/ai-tasks.md "Planner").
        var plan = new List<PlanStep>();
        var node = TaskDataTable.Of(TaskType.STAND_AT_GENERAL_NODE)!;
        Assert.True(TaskPlanner.Local.Plan(node, 1, default, new PlanStub(inBed: true, atNode: false), [], plan) > 0);
        Assert.Equal([TaskType.GET_OUT_OF_BED, TaskType.MOVE_ON_FREE_WILL, TaskType.STAND_AT_NODE, TaskType.STAND_AT_GENERAL_NODE], plan.Select(s => s.Type));
        Assert.True(TaskPlanner.Local.Plan(node, 1, default, new PlanStub(inBed: false, atNode: true), [], plan) > 0);
        Assert.Equal([TaskType.STAND_AT_NODE, TaskType.STAND_AT_GENERAL_NODE], plan.Select(s => s.Type));

        // Sleeping on the floor is urgent but needs to be away from the home town, which nothing provides: at home going to bed wins.
        var floor = Task("130-t", TaskType.SLEEP_ON_FLOOR, TaskPriority.Urgent);
        var package = Rec("131-t", FcsRecordType.AI_PACKAGE, "Floor or bed");
        package.References["Leader AI Goals"] = package.References["Squad AI Goals"] = [Ref("130-t", 0, 100), Ref("102-t", 0, 100)];
        var db = SyntheticTown.Database(1, extra: [.. Records(), floor, package], changePatrol: p => p.References["AI packages"] = [Ref("131-t", 0)]);
        using var world = Town(1, out _, WithHomes(db, out _, out _), null);
        world.RunTicks(120);
        var minds = world.Squads.All.Where(s => s.TemplateId == "10-t").SelectMany(s => s.Members).Select(m => world.Characters.Cold(m.Slot)!.Ai!).ToList();
        Assert.NotEmpty(minds);
        Assert.All(minds, m => Assert.Equal(TaskType.GO_HOME_AND_GO_TO_BED, m.Goal!.Task.Type));
    }

    [Fact]
    public void A_crippled_character_plans_with_the_crippled_repertoire_which_has_no_stand_up_row()
    {
        // FUN_14060e840: the crippled repertoire (0x40) wins over human; its rows 28 / 65 (standing, getting up) are 0x25, so nothing provides standing and a plan needing it fails.
        Assert.Equal(Repertoire.Crippled, Repertoire.Crippled & TaskDataTable.Of(TaskType.RUN_AWAY)!.Category);
        var plan = new List<PlanStep>();
        var run = TaskDataTable.Of(TaskType.RUN_AWAY)!;
        Assert.True(TaskPlanner.Local.Plan(run, 1, default, new PlanStub(false, false, Repertoire.Crippled), [], plan) > 0);
        Assert.Equal(TaskType.RUN_AWAY, plan[^1].Type);
        Assert.DoesNotContain(plan, s => s.Type is TaskType.STAND_UP or TaskType.GET_UP);
        Assert.All([TaskType.STAND_UP, TaskType.GET_UP], t => Assert.Equal(0, TaskDataTable.Of(t)!.Category & Repertoire.Crippled));
    }

    [Fact]
    public void Residents_back_after_hours_away_are_where_their_schedule_puts_them_only_with_the_switch()
    {
        foreach (bool schedule in new[] { true, false })
        {
            var data = WithHomes(Database(2), out _, out _);
            using var world = Town(1, out _, data, null);
            world.Systems.OfType<PopulationSystem>().Single().ResidentSchedule = schedule;
            world.RunTicks(400);   // noon: at their nodes and posts
            world.Commands.Enqueue(new FocusCommand(new Vector3(-50000, 0, -50000)) { Tick = world.Tick });
            world.RunTicks(3000);
            Assert.Equal(0, world.Squads.Count);
            world.GameHours = 24 + 23;   // ten hours on: bed time
            world.Commands.Enqueue(new FocusCommand(Centre) { Tick = world.Tick });
            world.RunTicks(30);
            var sleepers = world.Squads.All.Where(s => s.TemplateId == "10-t")
                .SelectMany(s => s.Members.Where(m => m != s.Leader).Select(m => (m, Beds: s.Ai!.Home!.Of(HomeSpotKind.Bed).Select(b => b.Position).ToList()))).ToList();
            Assert.NotEmpty(sleepers);
            bool InBed((CharacterId M, List<Vector3> Beds) x) => (CharacterTask)world.Characters.Previous[x.M.Slot].Task is CharacterTask.Sleep or CharacterTask.Rest && x.Beds.Min(b => Off(world, x.M, b)) <= 13;
            // With the switch they are in bed within a second of the town coming back; without it some are still on their way from where the town unloaded.
            if (schedule) Assert.All(sleepers, x => Assert.True(InBed(x)));
            else Assert.Contains(sleepers, x => !InBed(x));
        }
    }

    [Fact]
    public void A_dialogue_contract_change_AI_and_a_roaming_platoons_package_are_saved_and_loaded()
    {
        // The town of the AI tests with roaming folk whose packages are "Patrol near home" and "Stay home".
        var near = Rec("116-t", FcsRecordType.AI_PACKAGE, "Patrol near home");
        near.Ints["unloaded func"] = 2;
        var stay = Rec("117-t", FcsRecordType.AI_PACKAGE, "Stay home");
        stay.Ints["unloaded func"] = 4;
        var data = SyntheticTown.Data(SyntheticTown.Database(roaming: true, extra: [.. Records(), near, stay],
            changePatrol: p => p.References["AI packages"] = [Ref("111-t", 0), Ref("112-t", 1)], changeFolk: t => t.References["AI packages"] = [Ref("116-t", 0), Ref("117-t", 0)]));

        using (var world = Town(1, out _, data, null))
        {
            world.RunTicks(50);
            var squad = world.Squads.All.First(s => s.Ai is not null);
            world.Commands.Enqueue(new ChangeAi(squad.Id, "113-t") { Tick = world.Tick });
            world.Commands.Enqueue(new StartContract(squad.Id, "115-t", 8) { Tick = world.Tick });
            world.RunTicks(2);
            double end = squad.Ai!.ContractEnd;
            var save = Meitou.Simulation.Saving.SaveCapture.Capture(world, data, new Meitou.Simulation.Saving.SaveClock(1, 13, 0), null);
            var record = save.Platoons.Single(p => p.Record.Strings.GetValueOrDefault("contractjob") is { Length: > 0 }).Record;
            Assert.Equal(("115-t", "113-t"), (record.Strings["contractjob"], record.Strings["replacementAI"]));
            Assert.Equal((float)end, record.Floats["jobhr"]);

            string folder = Path.Combine(Path.GetTempPath(), "meitou-ai-" + Guid.NewGuid().ToString("N"));
            try
            {
                save.Write(folder);
                using var b = Town(1, out _, data, null);
                Meitou.Simulation.Saving.SaveLoader.Load(b, Meitou.Data.Save.SaveGame.Load(folder), data);
                b.RunTicks(170);   // past the unloaded platoon's 5 s activation dwell
                var board = Assert.Single(b.Squads.All, s => s.Ai?.Contract is not null).Ai!;
                Assert.Equal(("115-t", "113-t"), (board.Contract!.Id, board.Replacement?.Id));
                Assert.Equal((float)end, (float)board.ContractEnd);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        }

        // A roaming platoon's package: the saved one is run, and the one it runs now is written back.
        var builder = Meitou.Simulation.Saving.SaveCapture.PrepareBase(data);
        var saved = builder.AddPlatoon("Testfolk_7", "Testfolk", "20-t", "12-t", new Vector3(Centre.X + 500, 300, Centre.Z));
        saved.Record.Strings["basetown"] = "30-t";
        saved.Record.Strings["currentPackage"] = "117-t";   // not the one the slot rule picks (116-t, the first)
        builder.Game.UpdateDerived();
        string platoonFolder = Path.Combine(Path.GetTempPath(), "meitou-ai-" + Guid.NewGuid().ToString("N"));
        try
        {
            builder.Game.Write(platoonFolder);
            using var world = new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, new OpenGroundWalkability(Ground), [new PlayerSystem()]);
            var loaded = Meitou.Simulation.Saving.SaveLoader.Load(world, Meitou.Data.Save.SaveGame.Load(platoonFolder), data);
            var p = Assert.Single(world.Platoons.All);
            Assert.Equal(("117-t", PlatoonJob.GoHome), (p.Package, p.Job));
            // Captured again: the package it runs now is written back.
            p.Package = "116-t";
            var again = Meitou.Simulation.Saving.SaveCapture.Capture(world, data, new Meitou.Simulation.Saving.SaveClock(1, 12, 0), loaded);
            Assert.Equal("116-t", again.Platoons.Single(x => x.Name == "Testfolk_7").Record.Strings["currentPackage"]);
        }
        finally { if (Directory.Exists(platoonFolder)) Directory.Delete(platoonFolder, recursive: true); }
    }

    /// <summary>Every loaded platoon runs its blackboard in the original (<c>FUN_1402732b0</c>), a roaming one too; no town building is claimed, and the population still moves a package that does not travel.</summary>
    [Fact]
    public void A_loaded_roaming_platoons_squad_runs_a_blackboard_without_claiming_a_home_and_keeps_its_engine_routine()
    {
        var near = Rec("116-t", FcsRecordType.AI_PACKAGE, "Patrol near home");
        near.Ints["unloaded func"] = 2;
        var data = WithHomes(SyntheticTown.Database(roaming: true, extra: [.. Records(), near],
            changePatrol: p => p.References["AI packages"] = [Ref("111-t", 0), Ref("112-t", 1)], changeFolk: t => t.References["AI packages"] = [Ref("116-t", 0)]), out _, out _);
        using var world = Town(1, out _, data, null);
        world.RunTicks(700);
        var roamers = world.Platoons.All.Where(p => !p.Pinned && p.State == PlatoonState.Loaded).ToList();
        Assert.NotEmpty(roamers);
        foreach (var platoon in roamers)
        {
            var squad = world.Squads.Find(platoon.SquadId)!;
            Assert.Equal("116-t", squad.Ai?.Current?.Id);
            Assert.Null(squad.Ai!.OwnHome);
            Assert.False(AiSystem.BoardMoves(squad.Ai));
            Assert.All(squad.Members, m => Assert.True(world.Characters.Cold(world.Characters.TryResolveNext(m, out int s) ? s : -1)!.Ai!.Engine));
        }
    }
}
