using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Kidnap;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Combat;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// A loaded slave's owner and master from the save's <c>slaver</c> handle (docs/formats/save.md "Slave state").
/// </summary>
public class SlaveMasterLoadTests
{
    static readonly Vector3 Centre = new(2000, 150, 2000);

    /// <summary>
    /// An owner platoon and a slave platoon of one save, one character each; the slave's state record names the owner when
    /// <paramref name="named"/>.
    /// </summary>
    static (SaveCharacter Owner, SaveCharacter Slave) Pair(SaveBuilder builder, string tag, bool named = true)
    {
        var owners = builder.AddPlatoon($"Owners_{tag}", "city", "50-a", "t", Centre);
        var slaves = builder.AddPlatoon($"Slaves_{tag}", "city", "50-a", "t", Centre);
        var owner = builder.AddCharacter(owners, "1-c", "owner", "50-a", Centre, Quaternion.Identity, isLeader: true);
        var slave = builder.AddCharacter(slaves, "1-c", "slave", "50-a", Centre, Quaternion.Identity);
        slave.GameState!.Ints[SlaveSave.StateKey] = 1;
        (named ? owner.Handle : Hand.Null).Write(slave.GameState, SlaveSave.SlaverKey);
        return (owner, slave);
    }

    /// <summary>
    /// The character as loaded from <paramref name="source"/>: its save link and, read from its state record, its slave state.
    /// </summary>
    static CharacterCold Loaded(SaveCharacter source, PopulationData data)
    {
        var cold = new Combatant { Name = source.Name }.MakeCold();
        cold.Save = new SavedCharacterLink { Source = source, PlatoonName = source.Platoon.Record.StringId, Slot = source.Handle.I };
        SlaveSave.Read(source.GameState, cold, data);
        return cold;
    }

    static Squad NewSquad(SimWorld world, int faction)
    {
        var squad = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = faction };
        world.Squads.Add(squad);
        return squad;
    }

    static void Spawn(SimWorld world, CharacterCold cold, Squad squad)
    {
        cold.SquadId = squad.Id;
        cold.Faction = squad.Faction;
        var id = world.Characters.Spawn(new CharacterHot { Position = Centre, Health = 100, MaxSpeed = 60, WalkSpeed = 15 }, cold, world.Tick);
        squad.Members.Add(id);
        if (squad.Leader.IsNone) squad.Leader = id;
    }

    [Fact]
    public void A_loaded_slave_takes_the_faction_and_squad_of_its_owner_once_the_owner_is_in_the_world()
    {
        var data = ArrestTests.Town();
        int city = data.FactionIndex("50-a");
        int player = data.Relations.PlayerFaction;
        var builder = new SaveBuilder(1);
        var walk = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, walk, [new CaptiveSystem(data, null)]);
        var slaveSquad = NewSquad(world, city);

        // The slave is loaded first: it waits for its owner, then takes the owner's faction and squad.
        var (owner, slave) = Pair(builder, "a");
        var slaveCold = Loaded(slave, data);
        Spawn(world, slaveCold, slaveSquad);
        world.RunTicks(41);

        Assert.Equal((-1, -1), (slaveCold.SlaveOwner, slaveCold.SlaveMaster));
        Assert.False(slaveCold.Save!.Slaver.IsNull);

        var ownerSquad = NewSquad(world, city);
        Spawn(world, Loaded(owner, data), ownerSquad);
        world.RunTicks(21);

        Assert.Equal((city, ownerSquad.Id), (slaveCold.SlaveOwner, slaveCold.SlaveMaster));
        Assert.True(slaveCold.Save.Slaver.IsNull);

        // Meitou's own owner key wins over the owner character's faction; the master is still the owner's squad.
        var (keyedOwner, keyed) = Pair(builder, "b");
        keyed.GameState!.Strings[SlaveSave.OwnerKey] = data.Factions[player].Id;
        var keyedCold = Loaded(keyed, data);
        Spawn(world, keyedCold, slaveSquad);
        var keyedSquad = NewSquad(world, city);
        Spawn(world, Loaded(keyedOwner, data), keyedSquad);
        world.RunTicks(21);

        Assert.Equal((player, keyedSquad.Id), (keyedCold.SlaveOwner, keyedCold.SlaveMaster));

        // A slave freed before its owner is found drops the handle.
        var (_, freed) = Pair(builder, "c");
        var freedCold = Loaded(freed, data);
        Spawn(world, freedCold, slaveSquad);
        freedCold.SlaveState = 0;
        world.RunTicks(21);

        Assert.True(freedCold.Save!.Slaver.IsNull);
        Assert.Equal(-1, freedCold.SlaveMaster);
    }

    static AiGoal CageGoal(int start, int end)
    {
        var task = new AiTask("t", "cage", TaskType.SlaveGetInMyMastersCage, TaskPriority.NonUrgent, TaskTargetType.Specific, TaskEndEvent.Nothing);
        return new AiGoal(task, start, end, 1);
    }

    static Blackboard Board(List<AiGoal>[] roles)
    {
        var board = new Blackboard([new(0, new AiPackage { Id = "p", Name = "p", Roles = roles })]);
        board.Update();
        return board;
    }

    /// <summary>
    /// A slave of <paramref name="faction"/> by <paramref name="cage"/>, its own squad running <paramref name="own"/>, obeying
    /// <paramref name="master"/> (-1: none).
    /// </summary>
    static CharacterId Slave(SimWorld world, int faction, Blackboard own, Fixture cage, int master)
    {
        var squad = NewSquad(world, faction);
        squad.Ai = own;
        var cold = new Combatant { Name = "slave" }.MakeCold();
        (cold.SlaveState, cold.SlaveOwner, cold.SlaveMaster) = (1, faction, master);
        cold.Ai = new AiMind { Squad = squad };
        cold.SquadId = squad.Id;
        cold.Faction = faction;
        var at = new CharacterHot { Position = cage.Position + new Vector3(30, 0, 0), Health = 100, MaxSpeed = 60, WalkSpeed = 15 };
        var id = world.Characters.Spawn(at, cold, world.Tick);
        squad.Members.Add(id);
        squad.Leader = id;
        return id;
    }

    /// <summary>
    /// An obeying slave takes its captive jobs from its master's Slave AI Goals, not its own lists (job 187, docs/simulation.md
    /// "Obedient Slave"): at noon the one whose master gets it into its cage only at night stays out, the one with no master goes by
    /// its own package's all-day goal. Catches the captive sweep reading the own lists of an obeying slave, which sent saved slaves to
    /// their cages by day once they had owners.
    /// </summary>
    [Fact]
    public void An_obeying_slave_takes_its_captive_jobs_from_its_masters_slave_goals_not_its_own()
    {
        var data = ArrestTests.Town();
        int city = data.FactionIndex("50-a");
        var captive = new CaptiveSystem(data, null, new CaptiveSettings { RecordLog = true });
        var walk = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false, StartHours = 24 + 12 }, walk, [captive]);
        world.Fixtures.RegisterSite(world, data, 0);
        var cage = world.Fixtures.Find("0.0:cageA")!;

        var master = NewSquad(world, city);
        master.Ai = Board([[], [], [], [], [CageGoal(22, 5)]]);
        List<AiGoal>[] Own() => [[CageGoal(0, 24)], [CageGoal(0, 24)], [CageGoal(0, 24)], [], []];
        var obeying = Slave(world, city, Board(Own()), cage, master.Id);
        var masterless = Slave(world, city, Board(Own()), cage, -1);
        world.RunTicks(61);

        Assert.DoesNotContain(captive.Log, e => e.Worker == obeying);
        Assert.Contains(captive.Log, e => e.Worker == masterless && e.Kind == CaptiveEventKind.Started && e.Job == CaptiveJobKind.CageSelf);
    }
}
