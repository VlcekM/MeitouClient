using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Kidnap;
using Meitou.Simulation.Locks;
using Meitou.Tests.Combat;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>A squad's slave list (docs/simulation.md "Gather", "Shout"): its slave members and the slaves of other squads that obey it.</summary>
public class SlaveListTests
{
    static AiGoal Goal(TaskType type) =>
        new(new AiTask("t", type.ToString(), type, TaskPriority.NonUrgent, TaskTargetType.Specific, TaskEndEvent.Nothing), 0, 24, 1);

    /// <summary>One package with the five role lists in <see cref="AiRole"/> order (the leader's is the third).</summary>
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

    /// <summary>A character of <paramref name="squad"/> at <paramref name="at"/>, run by the AI; the squad's leader when it has none.</summary>
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

    /// <summary>A slave of <paramref name="faction"/> in a squad of its own, obeying <paramref name="master"/>.</summary>
    static CharacterId ObeyingSlave(SimWorld world, int faction, Squad master, Vector3 at) =>
        Member(world, NewSquad(world, faction, Board([], [], [], [], [])), at, c => (c.SlaveState, c.SlaveOwner, c.SlaveMaster) = (1, faction, master.Id));

    static (SimWorld World, CaptiveSystem Captive, int City, Fixture Cage) Town()
    {
        var data = ArrestTests.Town();
        var captive = new CaptiveSystem(data, null, new CaptiveSettings { RecordLog = true });
        var world = new SimWorld(new WorldSettings { PublishSnapshots = false, StartHours = 24 + 12 }, new OpenGroundWalkability((_, _) => 150), [captive]);
        world.Fixtures.RegisterSite(world, data, 0);
        return (world, captive, data.FactionIndex("50-a"), world.Fixtures.Find("0.0:cageA")!);
    }

    /// <summary>
    /// 188 Working the slaves shouts at a slave on the worker's squad list (docs/game/ai-tasks.md, 188's finder over the platoon's slave list): here one of
    /// a slave squad of its own that obeys the worker's squad, as the Rebirth slaves obey their workmasters. Catches the list holding only the squad's own
    /// slave members, which left every workmaster of a town whose slaves are squads of their own with nobody to work.
    /// </summary>
    [Fact]
    public void A_worker_shouts_at_a_slave_of_another_squad_that_obeys_its_squad()
    {
        var (world, captive, city, cage) = Town();
        using var _ = world;
        var master = NewSquad(world, city, Board([], [], [Goal(TaskType.WORK_THE_SLAVES)], [], []));
        var worker = Member(world, master, cage.Position + new Vector3(60, 0, 0));
        var slave = ObeyingSlave(world, city, master, cage.Position + new Vector3(80, 0, 0));
        world.RunTicks(61);

        Assert.Contains(captive.Log, e => e is { Kind: CaptiveEventKind.Started, Job: CaptiveJobKind.Shout } && e.Worker == worker && e.Captive == slave);
    }

    /// <summary>
    /// 173 Gathering slaves from cages waits while more than 3 slaves of the squad's list are up and free (its desire, docs/game/ai-tasks.md slot 63), the slaves
    /// that obey the squad from squads of their own counted. With 4 it gathers nobody; with 2 left it opens the occupied cage. Catches the gate reading only the
    /// squad's own slave members, so a workmaster with many obeying slaves kept gathering.
    /// </summary>
    [Fact]
    public void Gathering_waits_while_more_than_three_slaves_that_obey_the_squad_are_up_and_free()
    {
        var (world, captive, city, cage) = Town();
        using var _ = world;
        var master = NewSquad(world, city, Board([], [], [Goal(TaskType.GATHER_SLAVES_FROM_CAGES)], [], []));
        var worker = Member(world, master, cage.Position + new Vector3(60, 0, 0));
        var prisoner = Member(world, NewSquad(world, city, Board([], [], [], [], [])), cage.Position);
        Assert.True(world.Fixtures.Imprison(world, prisoner, cage));
        var slaves = Enumerable.Range(0, 4).Select(k => ObeyingSlave(world, city, master, cage.Position + new Vector3(80 + 10 * k, 0, 0))).ToList();

        world.RunTicks(61);
        bool Gathers() => captive.Log.Any(e => e is { Kind: CaptiveEventKind.Started, Job: CaptiveJobKind.Gather } && e.Worker == worker);
        Assert.False(Gathers());

        // Two of them freed: 2 left up and free, so it gathers.
        foreach (var s in slaves.Take(2)) (world.Characters.Cold(s.Slot)!.SlaveState, world.Characters.Cold(s.Slot)!.SlaveMaster) = (0, -1);
        world.RunTicks(61);
        Assert.True(Gathers());
    }
}
