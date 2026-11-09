using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using static Meitou.Tests.Simulation.SyntheticTown;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The AI (docs/simulation.md "AI"): the package and goal data as docs/game/ai.md reads them, the package choice, and a town that patrols by day and
/// sleeps at night.
/// </summary>
public class AiTests(ITestOutputHelper output)
{
    #region Test setup

    static FcsRecord Task(string id, TaskType type, TaskPriority tier, TaskTargetType target = TaskTargetType.Specific)
    {
        var r = Rec(id, FcsRecordType.AiTask, type.ToString());
        r.Ints["enum"] = (int)type;
        r.Ints["classification"] = (int)tier;
        r.Ints["targeting"] = (int)target;
        r.Ints["ending"] = 0;
        return r;
    }

    /// <summary>
    /// Two packages: "Town base" (the leader keeps the shop; the others patrol 6 to 22, go to bed 22 to 6 at half weight, and have an urgent goal the
    /// engine does not act out) and "Town" inheriting it (a duplicate shop goal at weight 0.5, which the parent's beats in the leader list and which
    /// its squad list takes by the fallback, and a slave goal); a war package of a class not built (signal 20); a running patrol (signal 23: the
    /// leader patrols, the others follow); a retreat (signal 5: everyone goes home); a timed contract (signal 33: everyone sits). The resident Patrol
    /// template lists "Town" at slot 0 and the war at slot 1 unless a test gives it others.
    /// </summary>
    internal static List<FcsRecord> Records()
    {
        var shop = Task("100-t", TaskType.StandAtShopkeeperNode, TaskPriority.NonUrgent);
        var patrol = Task("101-t", TaskType.PatrolTown, TaskPriority.NonUrgent);
        var bed = Task("102-t", TaskType.GoHomeAndGoToBed, TaskPriority.NonUrgent);
        var raid = Task("103-t", TaskType.RaidTown, TaskPriority.Urgent);
        var sit = Task("105-t", TaskType.SitAround, TaskPriority.Fluff);
        var parent = Rec("110-t", FcsRecordType.AiPackage, "Town base");
        parent.References["Leader AI Goals"] = [Ref("100-t", 0, 100, 0)];
        parent.References["Squad AI Goals"] = [Ref("101-t", 6, 22, 0), Ref("102-t", 22, 6, 50), Ref("103-t", 0, 100, 0)];
        var child = Rec("111-t", FcsRecordType.AiPackage, "Town");
        child.References["inherits from"] = [Ref("110-t")];
        child.References["Leader AI Goals"] = [Ref("100-t", 0, 0, 50)];
        child.References["Slave AI Goals"] = [Ref("105-t", 0, 100, 0)];
        var war = Rec("112-t", FcsRecordType.AiPackage, "War");
        war.Ints["signal func"] = 20;
        war.References["Squad AI Goals"] = [Ref("101-t")];
        var walk = Task("120-t", TaskType.Patrol, TaskPriority.NonUrgent);
        var follow = Task("121-t", TaskType.FollowSquadleader, TaskPriority.NonUrgent, TaskTargetType.Leader);
        var retreat = Task("122-t", TaskType.RetreatHometown, TaskPriority.NonUrgent);
        var outing = Rec("113-t", FcsRecordType.AiPackage, "Go out on patrol");
        outing.Ints["signal func"] = 23;
        outing.References["Leader AI Goals"] = [Ref("120-t")];
        outing.References["Squad AI Goals"] = [Ref("121-t")];
        var refresh = Rec("114-t", FcsRecordType.AiPackage, "Retreat and refresh");
        refresh.Ints["signal func"] = 5;
        refresh.References["Leader AI Goals"] = [Ref("122-t")];
        var hired = Rec("115-t", FcsRecordType.AiPackage, "Timed contract");
        hired.Ints["signal func"] = 33;
        hired.References["Leader AI Goals"] = [Ref("105-t", 0, 100, 0)];
        return [shop, patrol, bed, raid, sit, parent, child, war, walk, follow, retreat, outing, refresh, hired];
    }

    internal static GameDatabase Database(int residentSquads = 3, params (string Id, int Slot)[] packages) =>
        SyntheticTown.Database(residentSquads, extra: Records(), changePatrol: p => p.References["AI packages"] =
            packages.Length == 0 ? [Ref("111-t", 0), Ref("112-t", 1)] : [.. packages.Select(x => Ref(x.Id, x.Slot))]);

    #endregion

    #region Weapons, packages and base game records

    /// <summary>
    /// docs/game/ai-tasks.md (Task_EquipBestWeapon, FUN_140346c30): an NPC of a non-player faction with no weapon on its back or hip draws the first
    /// weapon of its inventory of the task's kind; a player-side character does not.
    /// </summary>
    [Fact]
    public void An_npc_without_a_back_or_hip_weapon_draws_the_first_matching_weapon_of_its_inventory()
    {
        var ai = new AiSystem(new PopulationData { Db = new GameDatabase(), Sites = [], Factions = [],
                Relations = Data.Gameplay.FactionRelations.Build([]) });
        using var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 150), []);
        world.Player.Faction = 1;
        ItemInstance Weapon(string record, FcsRecordType type) => new() { Record = record, Type = type, Section = "main", Width = 1, Height = 2 };
        CharacterCold Make(int faction, params ItemInstance[] items)
        {
            var cold = new Combat.Combatant { Name = "c" }.MakeCold();
            cold.Faction = faction;
            cold.Inventory = new Inventory();
            foreach (var item in items) cold.Inventory.Items.Add(item);
            world.Characters.Spawn(new CharacterHot { Position = Vector3.Zero, Health = 100 }, cold, 0);
            return cold;
        }
        var sabre = Weapon("sabre", FcsRecordType.Weapon);
        var bow = Weapon("bow", FcsRecordType.Crossbow);
        var raider = Make(2, Weapon("bread", FcsRecordType.Item), bow, sabre);

        Assert.True(ai.DrawFromInventory(world, raider, ranged: false));
        Assert.Equal("back", sabre.Section);   // the first weapon section it fits; the crossbow waits for task 264
        Assert.Equal("main", bow.Section);
        Assert.True(ai.DrawFromInventory(world, raider, ranged: true));
        Assert.NotEqual("main", bow.Section);

        var recruit = Make(1, Weapon("katana", FcsRecordType.Weapon));

        Assert.False(ai.DrawFromInventory(world, recruit, ranged: false));
    }

    [Fact]
    public void Packages_load_the_parent_first_and_the_parent_wins()
    {
        var ai = new AiDatabase(Database());
        var town = ai.Package("111-t")!;

        // The leader's shop goal comes from the parent: weight 1 (val2 0), window (0, 100); the child's duplicate is dropped.
        var lead = Assert.Single(town.Goals(AiRole.Leader));

        Assert.Equal((TaskType.StandAtShopkeeperNode, 0, 100, 1f), (lead.Task.Type, lead.Start, lead.End, lead.Weight));

        // Squad: the parent's own list, then the child's, which has no squad goals and falls back to its leader goals.
        Assert.Equal([TaskType.PatrolTown, TaskType.GoHomeAndGoToBed, TaskType.RaidTown, TaskType.StandAtShopkeeperNode],
                town.Goals(AiRole.Squad).Select(g => g.Task.Type));
        Assert.Equal(0.5f, town.Goals(AiRole.Squad)[1].Weight);
        Assert.Equal((0, 0, 0.5f), (town.Goals(AiRole.Squad)[3].Start, town.Goals(AiRole.Squad)[3].End, town.Goals(AiRole.Squad)[3].Weight));
        Assert.Equal(town.Goals(AiRole.Squad).Select(g => g.Task.Type), town.Goals(AiRole.Squad2).Select(g => g.Task.Type));
        Assert.Equal([TaskType.SitAround], town.Goals(AiRole.Slave).Select(g => g.Task.Type));
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

    static SimWorld Town(int threads, out PopulationData data, PopulationData? given, AiSettings? settings, double startHours = 24 + 13,
            ITickSystem? extra = null,
        StandardParts more = StandardParts.None)
    {
        data = given ?? SyntheticTown.Data(Database());
        var walk = new OpenGroundWalkability(Ground);
        var systems = StandardSystems.Build(data, walk, new StandardSystemOptions
        {
            Parts = StandardParts.Population | StandardParts.Movement | StandardParts.Ai | more, SynchronousPaths = true, Ai = settings,
        }).Systems;
        var world = new SimWorld(new WorldSettings { Seed = 5, Threads = threads, StartHours = startHours, MinPartitionSize = 4 }, walk,
                extra is null ? systems : [.. systems, extra]);
        world.Commands.Enqueue(new FocusCommand(Centre) { Tick = 0 });
        return world;
    }

    /// <summary>
    /// The town's AI by day, then from 23:00 on; pinned in <see cref="GoldenHashTests"/>.
    /// </summary>
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

    /// <summary>
    /// The counts docs/game/ai.md gives for the merged base game (gamedata.base, Newwworld, Dialogue, rebirth), read through
    /// <see cref="AiDatabase"/>.
    /// </summary>
    [Fact, Slow]
    public void The_base_game_AI_records_load_as_documented()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        var db = InstallData.BaseGame!;
        var packages = db.OfType(FcsRecordType.AiPackage).ToList();
        var tasks = db.OfType(FcsRecordType.AiTask).ToList();

        Assert.Equal(219, packages.Count);
        Assert.Equal(124, tasks.Count);

        int Entries(string list) => packages.Sum(p => p.GetReferences(list).Count);

        Assert.Equal((804, 561, 58, 64),
                (Entries("Leader AI Goals"), Entries("Squad AI Goals"), Entries("Squad2 AI Goals"), Entries("Slave AI Goals")));

        var ai = new AiDatabase(db);

        Assert.All(tasks, t => Assert.InRange((int)ai.Task(t.StringId)!.Type, 1, 290));
        Assert.All(tasks, t => Assert.InRange((int)ai.Task(t.StringId)!.Priority, 1, 3));

        foreach (var p in packages) Assert.NotNull(ai.Package(p.StringId));

        Assert.Equal(0, ai.Problems);

        var templates = Data.Gameplay.SquadTemplate.LoadAll(db);
        int withPackages = templates.Count(t => ai.Packages(t).Count > 0);
        var used = tasks.Select(t => ai.Task(t.StringId)!.Type).Distinct().ToList();
        var built = used.Where(t => TaskBehaviours.Of(t) != AiBehaviour.None).ToList();
        output.WriteLine($"{withPackages} templates with packages; {built.Count} of {used.Count} task types used acted out: {string.Join(", ", built)}");

        Assert.Equal(0, ai.Problems);
    }

    #endregion

    // ------------------------------------------------------------------ homes, package classes, stances, platoons

    #region Homes, sleeping and shops

    /// <summary>
    /// The synthetic town with two buildings: a shop (a shopkeeper node, two beds) east of the centre and a house (three beds,
    /// <paramref name="bedGap"/> apart) west of it.
    /// </summary>
    static PopulationData WithHomes(GameDatabase db, out TownBuilding shop, out TownBuilding house, float bedGap = 10, bool chair = false)
    {
        Vector3 At(float dx, float dz) => new(Centre.X + dx, 300, Centre.Z + dz);
        shop = new TownBuilding("shop", "b-shop", "Shop", At(80, 0), 0, true, false,
            [new HomeSpot(HomeSpotKind.Shopkeeper, At(80, 12), 0), new HomeSpot(HomeSpotKind.Bed, At(90, 0), 0),
                    new HomeSpot(HomeSpotKind.Bed, At(90, -10), 0)]);
        List<HomeSpot> spots =
            [
                new HomeSpot(HomeSpotKind.Bed, At(-90, 0), 0), new HomeSpot(HomeSpotKind.Bed, At(-90, bedGap), 0),
                new HomeSpot(HomeSpotKind.Bed, At(-90, 2 * bedGap), 0),
            ];
        if (chair) spots.Add(new HomeSpot(HomeSpotKind.Chair, At(-70, -10), 0));   // a piece 43 Staying home sits on
        house = new TownBuilding("house", "b-house", "House", At(-80, 0), 0, false, false, spots);
        return PopulationData.Create(db, [new TownPlacement("inst", "30-t", Centre, null)], buildings: new Dictionary<string,
                List<TownBuilding>> { ["inst"] = [shop, house] });
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
            Assert.Equal(Math.Min(beds.Count, sleepers.Count), sleepers.Count(m => world.Characters.Cold(m.Slot)!.InSomething == HeldState.Bed));
        }
    }

    /// <summary>
    /// An AI walk that another system stopped (a captive job's or lock round's stand) is neither arrival nor a failed walk (docs/game/ai-tasks.md
    /// "Planner", #314): the character walks to its spot again. Catches such a stop counting as arrival, which left a slaver standing where a job had
    /// left it all day.
    /// </summary>
    [Fact]
    public void A_walk_another_system_stopped_is_neither_arrival_nor_a_failed_walk()
    {
        var data = WithHomes(Database(2), out var shop, out _);
        var outside = new Outside();
        using var world = Town(1, out _, data, null, extra: outside);
        var table = world.Characters;
        world.RunTicks(400);   // the shop's leader stands at its shopkeeper node
        var leader = world.Squads.All.Single(s => s.TemplateId == "10-t" && s.Ai!.Home == shop).Leader;
        var mind = table.Cold(leader.Slot)!.Ai!;

        outside.Next = w => w.Characters.Next[leader.Slot].Position += new Vector3(0, 0, 40);   // shoved off its node: it walks back
        for (int t = 0; t < 60 && !mind.Walking; t++) world.RunTick();

        Assert.True(mind.Walking);

        outside.Next = w => TaskStart.Stand(w.Characters, leader.Slot);   // another system stands it still mid-walk
        world.RunTick();

        Assert.Equal(CharacterTask.Idle, (CharacterTask)table.Previous[leader.Slot].Task);

        for (int t = 0; t < 200 && (CharacterTask)table.Previous[leader.Slot].Task != CharacterTask.GoTo; t++) world.RunTick();

        Assert.Equal(0, mind.FailedWalks);
        Assert.True(mind.Walking);
        Assert.Equal(CharacterTask.GoTo, (CharacterTask)table.Previous[leader.Slot].Task);
    }

    /// <summary>
    /// A stand-in for another system: runs one action in the next tick's inputs.
    /// </summary>
    sealed class Outside : ITickSystem
    {
        public Action<SimWorld>? Next { get; set; }

        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands)
        {
            var act = Next;
            Next = null;
            act?.Invoke(world);
        }
    }

    /// <summary>
    /// A shopkeeper with goal 86 (Opening up) outside its own home, whose doors are all locked from the level data (no lock-up round set the house
    /// flag), has the door unlocked in its hours and stands at its node inside (docs/game/locks.md "Lock-up", #293). Catches the opener waiting for a
    /// round-locked flag that level data never sets.
    /// </summary>
    [Fact]
    public void A_shopkeeper_outside_its_home_unlocks_a_door_locked_from_level_data_and_stands_inside()
    {
        var open = Task("140-t", TaskType.OpenUpShopDoors, TaskPriority.NonUrgent);
        var package = Rec("141-t", FcsRecordType.AiPackage, "Shop");
        package.References["Leader AI Goals"] = [Ref("100-t", 0, 100, 0), Ref("140-t", 6, 22, 0)];
        var door = Rec("151-t", FcsRecordType.Building, "Door");
        var building = Rec("152-t", FcsRecordType.Building, "Shed");
        building.References["doors"] = [Ref("151-t")];
        var db = SyntheticTown.Database(1, extra: [.. Records(), open, package, door, building],
                changePatrol: p => p.References["AI packages"] = [Ref("141-t", 0)]);
        var at = Centre + new Vector3(80, 0, 0);
        var node = at + new Vector3(0, 0, 12);
        var shop = new TownBuilding("shop", "152-t", "Shop", at, 0, true, false,
                [new HomeSpot(HomeSpotKind.Shopkeeper, node, 0)]) { Zone = WorldLayout.ZoneOf(at.X, at.Z) };
        var data = PopulationData.Create(db, [new TownPlacement("inst", "30-t", Centre, null)], buildings: new Dictionary<string,
                List<TownBuilding>> { ["inst"] = [shop] });
        var outside = new Outside();
        using var world = Town(1, out _, data, null, extra: outside, more: StandardParts.Crime);
        world.RunTicks(2);
        world.Fixtures.RegisterSite(world, data, 0);

        Assert.True(world.Fixtures.SetDoorsLocked(shop.Key, true));
        Assert.True(world.Fixtures.AllDoorsLocked(shop.Key));

        var leader = world.Squads.All.Single(s => s.TemplateId == "10-t").Leader;
        outside.Next = w => w.Characters.Next[leader.Slot].Position = at + new Vector3(0, 0, 150);   // outside, away from the door
        world.RunTicks(1200);

        Assert.False(world.Fixtures.AllDoorsLocked(shop.Key));

        var shopDoor = Assert.Single(world.Fixtures.OfBuilding(shop.Key), f => f.Kind == FixtureKind.Door);

        Assert.False(shopDoor.Lock!.Locked);
        Assert.Equal(DoorMotion.Open, shopDoor.Motion);

        var p = world.Characters.Previous[leader.Slot].Position;

        Assert.InRange(Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(node.X, node.Z)), 0, 13);
    }

    #endregion

    #region Work and workplaces

    /// <summary>
    /// The synthetic town for the work goals: one resident squad whose package gives every member 189 all day, a house, a quarry west of it and a
    /// refinery east of it, which no machine runs. <paramref name="quarryRecord"/> names the quarry's BUILDING record (null: none, so no operator
    /// limit).
    /// </summary>
    static PopulationData WithWork(out TownBuilding quarry, out TownBuilding refinery, string? quarryRecord = null,
            IEnumerable<FcsRecord>? extra = null)
    {
        var work = Task("140-t", TaskType.AutoLabouringMines, TaskPriority.NonUrgent);
        var package = Rec("119-t", FcsRecordType.AiPackage, "Workers");
        package.References["Leader AI Goals"] = [Ref("140-t", 0, 24, 0)];
        package.References["Squad AI Goals"] = [Ref("140-t", 0, 24, 0)];
        var db = SyntheticTown.Database(1, extra: [.. Records(), work, package, .. extra ?? []],
                changePatrol: p => p.References["AI packages"] = [Ref("119-t", 0)]);
        Vector3 At(float dx, float dz) => new(Centre.X + dx, 300, Centre.Z + dz);
        var house = new TownBuilding("house", "b-house", "House", At(0, -80), 0, false, false, [new HomeSpot(HomeSpotKind.Bed, At(0, -90), 0)]);
        quarry = new TownBuilding("quarry", quarryRecord ?? "b-quarry", "Quarry", At(-90, 40), 0, false, false, []) { Function = 1 };
        refinery = new TownBuilding("refinery", "b-refinery", "Refinery", At(90, 40), 0, false, false,
                []) { Function = TownBuildings.RefineryFunction };
        return PopulationData.Create(db, [new TownPlacement("inst", "30-t", Centre, null)],
            buildings: new Dictionary<string, List<TownBuilding>> { ["inst"] = [house, quarry, refinery] });
    }

    /// <summary>
    /// The members of the resident squad whose goal is 189 Worker, with their minds.
    /// </summary>
    static List<(CharacterId Id, AiMind Mind)> Workers(SimWorld world)
    {
        var workers = new List<(CharacterId, AiMind)>();
        foreach (var squad in world.Squads.All.Where(s => s.TemplateId == "10-t"))
            foreach (var m in squad.Members)
                if (world.Characters.Cold(m.Slot)?.Ai is { Goal.Task.Type: TaskType.AutoLabouringMines } mind)
                    workers.Add((m, mind));
        return workers;
    }

    /// <summary>
    /// 189 Worker only takes a workplace it can run (docs/game/ai-tasks.md "Worker": 87's B list needs !62, and an NPC town's machines run nothing):
    /// a squad on 189 in a town with a quarry and an unfed refinery holds only the quarry and works there. Catches the pick over every workplace,
    /// which set Rebirth's slaves at its four unfed Manual Stone Processors where the original's work only the quarries.
    /// </summary>
    [Fact]
    public void Workers_take_only_the_workplace_they_can_run()
    {
        using var world = Town(1, out _, WithWork(out var quarry, out _), null);
        world.RunTicks(600);
        var workers = Workers(world);

        Assert.True(workers.Count >= 3, $"{workers.Count} members on 189");
        Assert.All(workers, w => Assert.Same(quarry, w.Mind.WorkPost));

        // Within the reach of a building without a functionality, doubled once stopped (AiSystem.AtWork): the last to come stop behind the first.
        Assert.All(workers, w => Assert.InRange(Off(world, w.Id, quarry.Position), 0, 2 * AiSystem.WorkReach));
    }

    /// <summary>
    /// A bar package needs a bar (docs/game/ai-tasks.md "Town goals", the hang-around-bar package): the town's squad on its normal kind (signal 17),
    /// whose package gives 214 Relaxing at full weight and 118 Shopping at half, in its town with no bar but an outdoor table and a public house,
    /// gets no table, so nobody relaxes and its members shop. Catches the fallback onto the outdoor table, which set a Holy Prisoner Caravan relaxing
    /// at Rebirth's stone processors where the original's shops.
    /// </summary>
    [Fact]
    public void A_bar_package_in_a_town_without_a_bar_takes_no_table_and_its_members_shop()
    {
        var relax = Task("131-t", TaskType.RelaxInTownPackage, TaskPriority.NonUrgent);
        var shop = Task("132-t", TaskType.Shopping, TaskPriority.NonUrgent);
        var bar = Rec("118-t", FcsRecordType.AiPackage, "Hang out in a bar");
        bar.Ints["signal func"] = 17;
        bar.References["Leader AI Goals"] = [Ref("131-t", 0, 24, 0), Ref("132-t", 0, 24, 50)];
        bar.References["Squad AI Goals"] = [Ref("131-t", 0, 24, 0), Ref("132-t", 0, 24, 50)];
        var db = SyntheticTown.Database(1, extra: [.. Records(), relax, shop, bar],
                changePatrol: p => p.References["AI packages"] = [Ref("118-t", 0)]);
        Vector3 At(float dx, float dz) => new(Centre.X + dx, 300, Centre.Z + dz);
        var house = new TownBuilding("house", "b-house", "House", At(-80, 0), 0, false, false, [new HomeSpot(HomeSpotKind.Bed, At(-90, 0), 0)])
        {
            HasInterior = true,
        };
        var table = new TownBuilding("table", "b-table", "Table", At(60, 40), 0, false, false, []) { Function = TownBuildings.TableFunction };
        var data = PopulationData.Create(db, [new TownPlacement("inst", "30-t", Centre, null)],
            buildings: new Dictionary<string, List<TownBuilding>> { ["inst"] = [house, table] });

        using var world = Town(1, out _, data, null);
        world.RunTicks(300);
        var squad = Assert.Single(world.Squads.All, s => s.TemplateId == "10-t");

        Assert.Equal(-1, squad.Ai!.BarTable);
        Assert.NotSame(table, squad.Ai.Home);

        var goals = squad.Members.Select(m => world.Characters.Cold(m.Slot)?.Ai?.Goal?.Task.Type).ToList();

        Assert.DoesNotContain(TaskType.RelaxInTownPackage, goals.Cast<TaskType?>());
        Assert.Contains(TaskType.Shopping, goals.Cast<TaskType?>());
    }

    #endregion

    #region Guards and posts

    /// <summary>
    /// The synthetic town for 157 Static town guard: one resident squad whose package gives every member 157 from 6 to 22, a house with beds, and two
    /// towers north of it with <paramref name="west"/> and <paramref name="east"/> guard posts, 30 apart.
    /// </summary>
    static PopulationData WithPosts(int west, int east, out TownBuilding[] towers)
    {
        var guard = Task("130-t", TaskType.StandAtGuardNodeHometownOutside, TaskPriority.NonUrgent);
        var package = Rec("116-t", FcsRecordType.AiPackage, "Town guards");
        package.References["Leader AI Goals"] = [Ref("130-t", 6, 22, 0)];
        package.References["Squad AI Goals"] = [Ref("130-t", 6, 22, 0)];
        var db = SyntheticTown.Database(1, extra: [.. Records(), guard, package], changePatrol: p => p.References["AI packages"] = [Ref("116-t", 0)]);
        Vector3 At(float dx, float dz) => new(Centre.X + dx, 300, Centre.Z + dz);
        TownBuilding Tower(string id, float dx, int posts) => new(id, "b-" + id, "Tower", At(dx, 60), 0, false, false,
            [.. Enumerable.Range(0, posts).Select(i => new HomeSpot(HomeSpotKind.Guard, At(dx + 30 * i, 70), 0) { NodeType = 2 })]);
        towers = [Tower("tower-w", -150, west), Tower("tower-e", 30, east)];
        var house = new TownBuilding("house", "b-house", "House", At(0, -80), 0, false, false,
            [new HomeSpot(HomeSpotKind.Bed, At(-10, -90), 0), new HomeSpot(HomeSpotKind.Bed, At(0, -90), 0),
                    new HomeSpot(HomeSpotKind.Bed, At(10, -90), 0)]);
        return PopulationData.Create(db, [new TownPlacement("inst", "30-t", Centre, null)],
            buildings: new Dictionary<string, List<TownBuilding>> { ["inst"] = [house, .. towers] });
    }

    /// <summary>
    /// The members of the resident squad whose goal is 157 Static town guard, with their minds.
    /// </summary>
    static List<(CharacterId Id, AiMind Mind)> TownGuards(SimWorld world)
    {
        var guards = new List<(CharacterId, AiMind)>();
        foreach (var squad in world.Squads.All.Where(s => s.TemplateId == "10-t"))
            foreach (var m in squad.Members)
                if (world.Characters.Cold(m.Slot)?.Ai is { Goal.Task.Type: TaskType.StandAtGuardNodeHometownOutside } mind)
                    guards.Add((m, mind));
        return guards;
    }

    /// <summary>
    /// 157 Static town guard holds a guard post of its town (docs/game/ai-tasks.md "Town goals"): of a squad of four or more on 157, three draw the
    /// town's three posts, one each, and stand at them; the others hold none. Catches the goal aimed at the home building's spots (the members stayed
    /// at their own posts, as a gate's stayed at its gate) and two members drawn onto one post.
    /// </summary>
    [Fact]
    public void Town_guards_take_different_free_posts_of_their_town()
    {
        using var world = Town(1, out _, WithPosts(2, 1, out var towers), null);
        world.RunTicks(900);
        var guards = TownGuards(world);

        Assert.True(guards.Count >= 4, $"{guards.Count} members on 157");

        var holders = guards.Where(g => g.Mind.TownPost is not null).ToList();
        var posts = holders.Select(g => g.Mind.TownPost!.Value).ToList();

        Assert.Equal(3, posts.Distinct().Count());
        Assert.Equal(3, holders.Count);
        Assert.All(posts, p => Assert.Contains(p.Building, towers));
        Assert.All(holders, g => Assert.InRange(Off(world, g.Id, g.Mind.TownPost!.Value.Spot.Position), 0, 13));
    }

    /// <summary>
    /// A town guard keeps its post while its goal stays 157 and lets it go when the goal ends: the posts drawn stay the same over 20 more package
    /// cadences, and at 23:00, outside the goal's hours, no member holds one. Catches a draw on every cadence (the guards walking from post to post)
    /// and a post still held after the goal.
    /// </summary>
    [Fact]
    public void A_town_guard_keeps_its_post_until_its_goal_ends()
    {
        using var world = Town(1, out _, WithPosts(4, 4, out _), null);
        world.RunTicks(300);
        var held = TownGuards(world).ToDictionary(g => g.Id, g => g.Mind.TownPost);

        Assert.True(held.Count >= 3, $"{held.Count} members on 157");
        Assert.All(held.Values, p => Assert.NotNull(p));

        world.RunTicks(600);

        Assert.All(held, h => Assert.Equal(h.Value, world.Characters.Cold(h.Key.Slot)!.Ai!.TownPost));

        world.GameHours = 24 + 23;
        world.RunTicks(300);

        Assert.All(held.Keys, id => Assert.Null(world.Characters.Cold(id.Slot)!.Ai!.TownPost));
    }

    #endregion

    #region Beds and workmasters

    /// <summary>
    /// A committed goal ends with its hours (docs/game/ai-tasks.md "Precedence", goal end): sleepers on a 153 of 22 to 0 with their commitment
    /// running have no goal past midnight, when nothing else of theirs is open, and lie on in their beds. Catches the committed mode keeping a goal
    /// out of its window, which kept Rebirth's garrison on 153 past its hours, and the default task getting a sleeper up, which the original's do
    /// not.
    /// </summary>
    [Fact]
    public void A_committed_bed_goal_ends_with_its_hours_and_its_sleepers_lie_on_with_no_goal()
    {
        var package = Rec("132-t", FcsRecordType.AiPackage, "Night shift");
        package.References["Leader AI Goals"] = package.References["Squad AI Goals"] = [Ref("102-t", 22, 0, 50), Ref("101-t", 6, 22, 0)];
        var db = SyntheticTown.Database(1, extra: [.. Records(), package], changePatrol: p => p.References["AI packages"] = [Ref("132-t", 0)]);
        using var world = Town(1, out _, WithHomes(db, out _, out _), null);
        world.RunTicks(400);
        world.GameHours = 24 + 22.2;
        world.RunTicks(1200);
        var sleepers = world.Squads.All.Where(s => s.TemplateId == "10-t").SelectMany(s => s.Members).Select(m => world.Characters.Cold(m.Slot)!)
            .Where(c => c.InSomething == HeldState.Bed && c.Ai?.Goal?.Task.Type == TaskType.GoHomeAndGoToBed).ToList();

        Assert.NotEmpty(sleepers);
        Assert.True(world.GameHours < 48, $"{world.GameHours} h");

        // Past midnight, the commitment (at least 2 hours from going to bed) still runs.
        world.GameHours = 48.1;

        Assert.All(sleepers, c => Assert.True(c.Ai!.CommitUntil > world.GameHours, $"commitment {c.Ai.CommitUntil}"));

        world.RunTicks(300);

        Assert.All(sleepers, c => Assert.Equal((HeldState.Bed, (TaskType?)null), (c.InSomething, c.Ai!.Goal?.Task.Type)));
    }

    /// <summary>
    /// A seated character weighs its captive goals (docs/simulation.md "Captives", weighed by the AI): a workmaster on its home's chair under 43
    /// Staying home, with a slave that obeys its squad up, stays seated while 43's commitment runs, then gets up and starts its 188 shout job, as the
    /// original's chooser weighs 188 against the seated goal. Catches the AI that did not see the captive goals and renewed 43, so Rebirth's
    /// workmasters the AI seated at home sat there all hour.
    /// </summary>
    [Fact]
    public void A_workmaster_seated_at_home_gets_up_to_work_its_slaves_once_staying_home_runs_out()
    {
        var stay = Task("150-t", TaskType.StayInHome, TaskPriority.NonUrgent);
        var work = Task("151-t", TaskType.WorkTheSlaves, TaskPriority.NonUrgent);
        var package = Rec("152-t", FcsRecordType.AiPackage, "Workmasters");
        package.References["Leader AI Goals"] = [Ref("150-t", 0, 100, 0), Ref("151-t", 0, 100, 0)];

        // The others patrol the town (an empty list would take the leader's).
        package.References["Squad AI Goals"] = package.References["Squad2 AI Goals"] = [Ref("101-t", 0, 100, 0)];
        var db = SyntheticTown.Database(2, extra: [.. Records(), stay, work, package],
            changePatrol: p => p.References["AI packages"] = [Ref("152-t", 0)]);
        var data = WithHomes(db, out _, out var house, chair: true);
        using var world = Town(1, out _, data, null);
        var captives = world.SystemOf<Meitou.Simulation.Kidnap.CaptiveSystem>()!;
        world.RunTicks(31);
        var squad = world.Squads.All.Single(s => s.TemplateId == "10-t" && s.Ai!.Home == house);
        var mind = world.Characters.Cold(squad.Leader.Slot)!.Ai!;
        for (int t = 0; t < 3000 && world.Characters.Next[squad.Leader.Slot].Task != (byte)CharacterTask.Rest; t++) world.RunTicks(1);

        Assert.Equal((CharacterTask.Rest, TaskType.StayInHome, AiBehaviour.Sit),

            ((CharacterTask)world.Characters.Next[squad.Leader.Slot].Task, mind.Goal?.Task.Type, mind.Behaviour));

        // A slave that obeys the workmaster's squad comes; 43's commitment runs an hour more.
        ObeyingSlave(world, squad, house.Position + new Vector3(0, 0, 30));
        mind.CommitUntil = world.GameHours + 1;
        bool Shouts() => captives.Jobs.Any(j => j.Kind == Meitou.Simulation.Kidnap.CaptiveJobKind.Shout && j.Worker == squad.Leader);
        world.RunTicks(61);

        Assert.False(Shouts());
        Assert.Equal(CharacterTask.Rest, (CharacterTask)world.Characters.Next[squad.Leader.Slot].Task);

        mind.CommitUntil = world.GameHours;
        world.RunTicks(61);

        Assert.True(Shouts());
        Assert.Equal(TaskType.WorkTheSlaves, mind.Goal?.Task.Type);
    }

    /// <summary>
    /// A captive job's goal is its worker's (docs/simulation.md "Captives", the job's goal is the mind's): a workmaster walking home under 43
    /// Staying home, committed for an hour, that the sweep sets shouting at a slave of its squad holds 188 from then on. When that slave dies
    /// as two more come, the think after the job weighs 188 again and shouts at one of them, never going back to 43. Catches the job that left 43
    /// and its commitment in place, and the think after a job that saw no captive goal: either way Rebirth's workmasters went home to sit after
    /// their first job.
    /// </summary>
    [Fact]
    public void A_workmaster_whose_slave_dies_shouts_at_the_next_rather_than_going_home_to_sit()
    {
        var stay = Task("150-t", TaskType.StayInHome, TaskPriority.NonUrgent);
        var work = Task("151-t", TaskType.WorkTheSlaves, TaskPriority.NonUrgent);
        var package = Rec("152-t", FcsRecordType.AiPackage, "Workmasters");
        package.References["Leader AI Goals"] = [Ref("150-t", 0, 100, 0), Ref("151-t", 0, 100, 0)];
        package.References["Squad AI Goals"] = package.References["Squad2 AI Goals"] = [Ref("101-t", 0, 100, 0)];
        var db = SyntheticTown.Database(2, extra: [.. Records(), stay, work, package],
            changePatrol: p => p.References["AI packages"] = [Ref("152-t", 0)]);
        var data = WithHomes(db, out _, out var house, chair: true);
        using var world = Town(1, out _, data, null);
        var captives = world.SystemOf<Meitou.Simulation.Kidnap.CaptiveSystem>()!;
        world.RunTicks(31);
        var squad = world.Squads.All.Single(s => s.TemplateId == "10-t" && s.Ai!.Home == house);
        int leader = squad.Leader.Slot;
        var mind = world.Characters.Cold(leader)!.Ai!;

        Assert.Equal(TaskType.StayInHome, mind.Goal?.Task.Type);
        Assert.NotEqual(CharacterTask.Rest, (CharacterTask)world.Characters.Next[leader].Task);

        // On its walk home, committed an hour more, a slave of its squad comes: the sweep sets it shouting, and 188 is its goal.
        mind.CommitUntil = world.GameHours + 1;
        var first = ObeyingSlave(world, squad, house.Position + new Vector3(0, 0, 30));
        Meitou.Simulation.Kidnap.CaptiveJob? Shout() =>
            captives.Jobs.FirstOrDefault(j => j.Kind == Meitou.Simulation.Kidnap.CaptiveJobKind.Shout && j.Worker == squad.Leader);
        for (int t = 0; t < 300 && Shout() is null; t++) world.RunTicks(1);

        Assert.Equal(first, Shout()?.Captive);
        Assert.Equal(TaskType.WorkTheSlaves, mind.Goal?.Task.Type);

        // Right after that sweep turn, the slave dies as two more come: the job ends, and the think comes before the next turn.
        var next = new[]
        {
            ObeyingSlave(world, squad, house.Position + new Vector3(10, 0, 30)), ObeyingSlave(world, squad, house.Position + new Vector3(-10, 0, 30)),
        };
        world.Characters.Remove(first);
        bool ShoutsAtNext() => Shout() is { } job && next.Contains(job.Captive);
        for (int t = 0; t < 29 && !ShoutsAtNext(); t++)
        {
            world.RunTicks(1);

            Assert.NotEqual(TaskType.StayInHome, mind.Goal?.Task.Type);
        }

        Assert.True(ShoutsAtNext());
        Assert.NotEqual(CharacterTask.Rest, (CharacterTask)world.Characters.Next[leader].Task);
    }

    /// <summary>
    /// A slave of a squad of its own (idle: no goals) that obeys <paramref name="master"/>, as a town's slave squads obey their workmasters.
    /// </summary>
    static CharacterId ObeyingSlave(SimWorld world, Squad master, Vector3 at)
    {
        var board = new Blackboard([new(0, new AiPackage { Id = "p", Name = "p", Roles = [[], [], [], [], []] })]);
        board.Update();
        var own = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = master.Faction, Ai = board };
        world.Squads.Add(own);
        var slave = new Combat.Combatant { Name = "slave" }.MakeCold();
        (slave.Ai, slave.SquadId, slave.Faction) = (new AiMind { Squad = own }, own.Id, master.Faction);
        (slave.SlaveState, slave.SlaveOwner, slave.SlaveMaster) = (1, master.Faction, master.Id);
        own.Leader = world.Characters.Spawn(new CharacterHot { Position = at, Health = 100, MaxSpeed = 60, WalkSpeed = 15 }, slave, world.Tick);
        own.Members.Add(own.Leader);
        return own.Leader;
    }

    /// <summary>
    /// A character's world for the planner: in bed or not, at the node or not; nothing drawn, carried or attacking; the rest held true.
    /// </summary>
    sealed class PlanStub(bool inBed, bool atNode, int category = 1) : IPlanWorld
    {
        public int Category => category;
        public bool Squadless => false;
        public float Desire(TaskDataRow row, in PlanTarget target) => 1;
        public PlanTarget Find(TaskDataRow row, in PlanTarget from) => new(1, new Vector3(10, 0, 10));
        public bool Holds(TaskCondition c, in PlanTarget target) => c.Holds == c.State switch
        {
            StateType.IsInBed => inBed,
            StateType.IsAtLocation or StateType.AtNode => atNode,
            StateType.HasWeaponEquipped or StateType.HasWeaponEquipped156 or StateType.HasWeaponEquipped157 or StateType.IsCarryingSomething
                or StateType.IAmBeingMeleeAttackedImmediate or StateType.IsCrouching or StateType.IsCrouchingOrLying or StateType.IAmProne => false,
            _ => true,
        };
    }

    #endregion

    #region Goal plans and schedules

    [Fact]
    public void A_goal_plans_through_the_TaskData_rows_and_one_without_a_plan_gives_way_to_the_next()
    {
        // In bed and away from its node, a general-node goal chains getting up, the walk and standing at the node (docs/game/ai-tasks.md "Planner").
        var plan = new List<PlanStep>();
        var node = TaskDataTable.Of(TaskType.StandAtGeneralNode)!;

        Assert.True(TaskPlanner.Local.Plan(node, 1, default, new PlanStub(inBed: true, atNode: false), [], plan) > 0);
        Assert.Equal([TaskType.GetOutOfBed, TaskType.MoveOnFreeWill, TaskType.StandAtNode, TaskType.StandAtGeneralNode], plan.Select(s => s.Type));
        Assert.True(TaskPlanner.Local.Plan(node, 1, default, new PlanStub(inBed: false, atNode: true), [], plan) > 0);
        Assert.Equal([TaskType.StandAtNode, TaskType.StandAtGeneralNode], plan.Select(s => s.Type));

        // Sleeping on the floor is urgent but needs to be away from the home town, which nothing provides: at home going to bed wins.
        var floor = Task("130-t", TaskType.SleepOnFloor, TaskPriority.Urgent);
        var package = Rec("131-t", FcsRecordType.AiPackage, "Floor or bed");
        package.References["Leader AI Goals"] = package.References["Squad AI Goals"] = [Ref("130-t", 0, 100), Ref("102-t", 0, 100)];
        var db = SyntheticTown.Database(1, extra: [.. Records(), floor, package], changePatrol: p => p.References["AI packages"] = [Ref("131-t", 0)]);
        using var world = Town(1, out _, WithHomes(db, out _, out _), null);
        world.RunTicks(120);
        var minds = world.Squads.All.Where(s => s.TemplateId == "10-t").SelectMany(s => s.Members).Select(m => world.Characters.Cold(m.Slot)!.Ai!)
                .ToList();

        Assert.NotEmpty(minds);
        Assert.All(minds, m => Assert.Equal(TaskType.GoHomeAndGoToBed, m.Goal!.Task.Type));
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
                .SelectMany(s => s.Members.Where(m => m != s.Leader).Select(m => (m,
                        Beds: s.Ai!.Home!.Of(HomeSpotKind.Bed).Select(b => b.Position).ToList()))).ToList();

            Assert.NotEmpty(sleepers);

            bool InBed((CharacterId M,
                    List<Vector3> Beds) x) => (CharacterTask)world.Characters.Previous[x.M.Slot].Task is CharacterTask.Sleep or CharacterTask.Rest
                    && x.Beds.Min(b => Off(world, x.M, b)) <= 13;

            // With the switch they are in bed within a second of the town coming back; without it some are still on their way from where the town
            // unloaded.
            if (schedule) Assert.All(sleepers, x => Assert.True(InBed(x)));
            else Assert.Contains(sleepers, x => !InBed(x));
        }
    }

    #endregion

    #region Saves and roaming platoons

    [Fact]
    public void A_dialogue_contract_change_AI_and_a_roaming_platoons_package_are_saved_and_loaded()
    {
        // The town of the AI tests with roaming folk whose packages are "Patrol near home" and "Stay home".
        var near = Rec("116-t", FcsRecordType.AiPackage, "Patrol near home");
        near.Ints["unloaded func"] = 2;
        var stay = Rec("117-t", FcsRecordType.AiPackage, "Stay home");
        stay.Ints["unloaded func"] = 4;
        var data = SyntheticTown.Data(SyntheticTown.Database(roaming: true, extra: [.. Records(), near, stay],
            changePatrol: p => p.References["AI packages"] = [Ref("111-t", 0), Ref("112-t", 1)],
                    changeFolk: t => t.References["AI packages"] = [Ref("116-t", 0), Ref("117-t", 0)]));

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
                Meitou.Simulation.Saving.SaveLoader.Load(b, Data.Save.SaveGame.Load(folder), data);
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
            using var world = new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, new OpenGroundWalkability(Ground),
                    [new PlayerSystem()]);
            var loaded = Meitou.Simulation.Saving.SaveLoader.Load(world, Data.Save.SaveGame.Load(platoonFolder), data);
            var p = Assert.Single(world.Platoons.All);

            Assert.Equal(("117-t", PlatoonJob.GoHome), (p.Package, p.Job));

            // Captured again: the package it runs now is written back.
            p.Package = "116-t";
            var again = Meitou.Simulation.Saving.SaveCapture.Capture(world, data, new Meitou.Simulation.Saving.SaveClock(1, 12, 0), loaded);

            Assert.Equal("116-t", again.Platoons.Single(x => x.Name == "Testfolk_7").Record.Strings["currentPackage"]);
        }
        finally { if (Directory.Exists(platoonFolder)) Directory.Delete(platoonFolder, recursive: true); }
    }

    /// <summary>
    /// Every loaded platoon runs its blackboard in the original (<c>FUN_1402732b0</c>), a roaming one too; no town building is claimed, and the
    /// population still moves a package that does not travel.
    /// </summary>
    [Fact]
    public void A_loaded_roaming_platoons_squad_runs_a_blackboard_without_claiming_a_home_and_keeps_its_engine_routine()
    {
        var near = Rec("116-t", FcsRecordType.AiPackage, "Patrol near home");
        near.Ints["unloaded func"] = 2;
        var data = WithHomes(SyntheticTown.Database(roaming: true, extra: [.. Records(), near],
            changePatrol: p => p.References["AI packages"] = [Ref("111-t", 0), Ref("112-t", 1)],
                    changeFolk: t => t.References["AI packages"] = [Ref("116-t", 0)]), out _, out _);
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

    #endregion
}
