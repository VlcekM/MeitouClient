using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Navigation;
using Meitou.Simulation;
using Xunit.v3;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>The simulation on the navmesh of The Hub (the install's data): a move order across the walled town goes through its gates.</summary>
[Slow]
public class HubNavigationTests
{
    [Fact]
    public async Task A_move_order_across_the_hub_walks_round_the_buildings_through_the_gates_to_the_goal()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = GameDatabase.Load(LoadOrder.FromInstall(install!));
        var levels = WorldLevelData.Load(install!);
        var dir = Path.Combine(Path.GetTempPath(), "meitou-hubnav-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var map = TerrainHeightmap.Open(install!);
            using var nav = new NavSystem(install!, db, levels, (x, z) => (float)map.HeightAt(x, z), cache: new NavMeshCache(dir));
            await nav.LoadZone(new ZoneCoordinate(20, 32));
            await nav.LoadZone(new ZoneCoordinate(21, 32));

            var data = PopulationData.Create(db, levels.Towns());
            var population = new PopulationSystem(data);
            var paths = new PathService(nav.Walkability, synchronous: true);
            using var world = new SimWorld(new WorldSettings { Seed = 1, Threads = 1, PublishSnapshots = false }, nav.Walkability,
                [new PlayerSystem(), new MovementSystem(paths)]);
            var squad = population.StartPlayer(world, NewGameStart.Find(NewGameStart.LoadAll(db), NewGameStart.DefaultName)!);
            var leader = squad.Leader;
            var start = world.Characters.Previous[leader.Slot].Position;
            var goal = new Vector3(-50400, 0, 3200);   // across the wall from the start, outside the town
            var straight = Vector2.Distance(new(start.X, start.Z), new(goal.X, goal.Z));
            world.Commands.Enqueue(new MoveOrder([leader], goal) { Tick = 0 });

            float walked = 0;
            var last = start;
            bool onMesh = true;
            for (int i = 0; i < 4000; i++)
            {
                world.RunTick();
                var p = world.Characters.Previous[leader.Slot].Position;
                walked += Vector2.Distance(new(last.X, last.Z), new(p.X, p.Z));
                onMesh &= nav.Walkability.IsWalkable(p.X, p.Z);
                last = p;
                if (Vector2.Distance(new(p.X, p.Z), new(goal.X, goal.Z)) < MovementSystem.ArriveRadius) break;
            }
            Assert.True(Vector2.Distance(new(last.X, last.Z), new(goal.X, goal.Z)) < MovementSystem.ArriveRadius + 5, $"ended {last} for goal {goal}");
            Assert.True(onMesh, "never left the walkable mesh");
            Assert.True(walked > straight * 1.1f, $"walked {walked:0} for a straight {straight:0}: a detour round the wall");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
