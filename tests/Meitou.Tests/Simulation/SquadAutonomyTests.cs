using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Save;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// A player's squad on AI packages (docs/simulation.md "Autonomous player squads"): the AI tests' town and packages, the player's start squad
/// made autonomous with the house as its home and "Town base" (its members patrol the town by day) as its package.
/// </summary>
public class SquadAutonomyTests
{
    #region Test setup

    const StandardParts Parts = StandardParts.Population | StandardParts.Player | StandardParts.Movement | StandardParts.Ai;
    const string Package = "110-t";

    sealed record Game(SimWorld World, PopulationData Data, Squad Squad, TownBuilding House) : IDisposable
    {
        public void Dispose() => World.Dispose();

        public void Say(SimCommand command) => World.Commands.Enqueue(command);

        public CharacterCold Cold(CharacterId id) => World.Characters.Cold(id.Slot)!;

        /// <summary>
        /// Whether the AI runs the character's task: one its think started.
        /// </summary>
        public bool AiRuns(CharacterId id) => Cold(id) is { Ai: { } mind, Running: { } run } && mind.Owns(run);

        public bool Until(Func<bool> reached, int ticks)
        {
            for (int k = 0; k < ticks && !reached(); k++) World.RunTicks(1);

            return reached();
        }

        /// <summary>
        /// The squad made autonomous, at home in the house, on the package.
        /// </summary>
        public void MakeAutonomous()
        {
            Say(new SetSquadAutonomy(Squad.Id, true) { Tick = World.Tick });
            Say(new SetSquadHome(Squad.Id, House.Key) { Tick = World.Tick });
            Say(new ChangeAi(Squad.Id, Package) { Tick = World.Tick });
        }
    }

    static Game NewGame(bool start = true, ITickSystem? extra = null)
    {
        var db = AiTests.Database();
        var data = AiTests.WithHomes(db, out _, out var house);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, Parts, new PopulationSettings { CheckEveryTicks = 1 });
        var world = new SimWorld(new WorldSettings { Seed = 11, PublishSnapshots = false, StartHours = 24 + 13 }, walk,
                extra is null ? systems.Systems : [.. systems.Systems, extra]);
        var squad = start ? systems.Population!.StartPlayer(world, NewGameStart.From(db.Find("40-t")!)) : null!;
        return new Game(world, data, squad, house);
    }

    /// <summary>
    /// Starts a job's standing task (row 87, a job's provenance) for one character at its first slow world.
    /// </summary>
    sealed class JobStarter(Func<CharacterId> who) : ITickSystem
    {
        bool _done;

        public void SlowWorld(SimWorld world)
        {
            if (_done || !world.Characters.TryResolveNext(who(), out int slot)) return;

            _done = true;
            TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Idle, TaskType.OperateMachinery, TaskSource.Job) { Job = 0 });
        }
    }

    static FcsRecord PlayerPlatoon(SaveGame save) => save.Platoons.Single(p => p.FactionId == save.PlayerFaction!.Id).Record;

    #endregion

    /// <summary>
    /// The precedence: a package is ignored until the squad is autonomous; then a member with nothing to do acts the package out, a move order
    /// takes it from the AI at once and the AI takes it back when the order is done, and a member on a job's task is left on it throughout.
    /// Catches an AI that runs a player's squad unasked, that overrides an order or a job, or that never resumes after an order.
    /// </summary>
    [Fact]
    public void An_autonomous_squads_members_act_the_package_out_below_the_players_orders_and_jobs()
    {
        var worker = CharacterId.None;
        using var g = NewGame(extra: new JobStarter(() => worker));
        var walker = g.Squad.Members[1];
        worker = g.Squad.Members[2];

        g.Say(new ChangeAi(g.Squad.Id, Package) { Tick = g.World.Tick });
        g.World.RunTicks(3);

        Assert.Null(g.Squad.Ai);
        Assert.Equal(TaskSource.Job, g.Cold(worker).Running?.Source);

        g.MakeAutonomous();

        Assert.True(g.Until(() => g.AiRuns(walker), 300), "a member with nothing to do acts the package out");
        Assert.Equal(TaskType.PatrolTown, g.Cold(walker).Ai!.Goal!.Task.Type);

        var here = g.World.Characters.Next[walker.Slot].Position;
        g.Say(new MoveOrder([walker], here + new Vector3(60, 0, 0)) { Tick = g.World.Tick });
        g.World.RunTicks(2);

        Assert.Equal((TaskSource.Order, TaskType.MoveCusOrdered), (g.Cold(walker).Running!.Source, g.Cold(walker).Running!.Row));
        Assert.True(g.Until(() => g.Cold(walker).Running is not { Source: TaskSource.Order }, 600), "the order ends");
        Assert.True(g.Until(() => g.AiRuns(walker), 300), "the AI resumes after the order");
        Assert.Equal(TaskSource.Job, g.Cold(worker).Running?.Source);
        Assert.False(g.AiRuns(worker));
    }

    /// <summary>
    /// The switch, the package and the home are written on the player's PLATOON record (docs/formats/save.md "Player platoons"), a loaded game
    /// runs the squad on them again, and a squad switched off is written without them. Catches keys lost or misspelt by the save, a loaded squad
    /// the AI does not attach, and keys left behind after the switch goes off.
    /// </summary>
    [Fact]
    public void An_autonomous_squads_switch_package_and_home_survive_a_save_and_a_load()
    {
        using var a = NewGame();
        a.MakeAutonomous();
        a.World.RunTicks(3);

        var first = SaveCapture.Capture(a.World, a.Data, new SaveClock(1, 13, 0));
        string folder = Path.Combine(SaveSamples.Temp("autonomy"), QuickSave.Name);
        try
        {
            first.Write(folder);
            var save = SaveGame.Load(folder);
            var record = PlayerPlatoon(save);

            Assert.True(record.Bools[AutonomySave.AutonomousKey]);
            Assert.Equal((a.House.Key, Package), (record.Strings[AutonomySave.HomeKey], record.Strings["replacementAI"]));

            using var b = NewGame(start: false);
            var loaded = SaveLoader.Load(b.World, save, b.Data);
            var squad = b.World.Squads.Find(b.World.Player.Squad)!;

            Assert.True(squad.Autonomous);
            Assert.Equal(b.House.Key, squad.AssignedHome);

            b.World.RunTicks(3);

            Assert.Equal((Package, b.House.Key), (squad.Ai?.Replacement?.Id, squad.Ai?.Home?.Key));

            // Switched off: the keys go, and the package with them.
            b.World.Commands.Enqueue(new SetSquadAutonomy(squad.Id, false) { Tick = b.World.Tick });
            b.World.RunTicks(2);
            var off = PlayerPlatoon(SaveCapture.Capture(b.World, b.Data, loaded.Clock, loaded));

            Assert.False(off.Bools.ContainsKey(AutonomySave.AutonomousKey));
            Assert.False(off.Strings.GetValueOrDefault("replacementAI") is { Length: > 0 });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }
}
