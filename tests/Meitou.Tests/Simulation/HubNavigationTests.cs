using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Navigation;
using Meitou.Simulation;
using Xunit.v3;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The simulation on the navmesh of The Hub (the install's data): a long move order to Squin
/// loads its zones on the way as the game does and arrives.
/// </summary>
[Slow]
public class HubNavigationTests
{
    [Fact]
    public async Task A_long_move_order_from_the_hub_to_squin_loads_its_zones_on_the_way_and_arrives()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = InstallData.FullLoadOrder!;
        var levels = InstallData.Levels!;
        var dir = Path.Combine(Path.GetTempPath(), "meitou-longnav-" + Guid.NewGuid().ToString("N"));
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
            var squin = levels.Towns().First(t => db.Find(t.TownId)?.Name == "Squin").Position;
            var goal = new Vector3(squin.X, 0, squin.Z);
            var straight = Vector2.Distance(new(start.X, start.Z), new(goal.X, goal.Z));
            world.Commands.Enqueue(new MoveOrder([leader], goal) { Tick = 0 });

            // The game's UpdateNav, once a second: the zones round the walker and those its long route leads into load (waited for, as a still does),
            // zones idle for 30 s unload, and the walker asks for its path again when a mesh went live.
            var loaded = new Dictionary<ZoneCoordinate, int>();
            float walked = 0, best = float.MaxValue;
            int bestTick = 0, tick = 0;
            var last = start;
            for (; tick < 200_000; tick++)
            {
                if (tick % 30 == 0)
                {
                    var wanted = ZoneActivation.ZonesAround(last, 1).Concat(nav.Walkability.RouteZones(TimeSpan.FromSeconds(30))).Distinct().ToList();
                    var fresh = wanted.Where(z => !loaded.ContainsKey(z)).ToList();
                    foreach (var z in wanted) loaded[z] = tick;
                    await Task.WhenAll(fresh.Select(z => (Task)nav.LoadZone(z)));
                    foreach (var (z, seen) in loaded.ToList())
                        if (tick - seen > 900) { nav.UnloadZone(z); loaded.Remove(z); }
                    if (fresh.Count > 0) world.Commands.Enqueue(new RepathCommand { Tick = world.Tick, Zones = fresh.ToHashSet() });
                }
                world.RunTick();
                var p = world.Characters.Previous[leader.Slot].Position;
                walked += Vector2.Distance(new(last.X, last.Z), new(p.X, p.Z));
                last = p;
                float left = Vector2.Distance(new(p.X, p.Z), new(goal.X, goal.Z));
                if (left < MovementSystem.ArriveRadius) break;
                if (left < best - 50) { best = left; bestTick = tick; }
                Assert.True(tick - bestTick < 30 * 120, $"no progress for 2 minutes at {p}, {left:0} from the goal (walked {walked:0} of a straight {straight:0})");
            }
            Assert.True(Vector2.Distance(new(last.X, last.Z), new(goal.X, goal.Z)) < MovementSystem.ArriveRadius + 5, $"ended {last} for goal {goal} after {tick} ticks");
            Assert.True(walked < straight * 1.5f, $"walked {walked:0} for a straight {straight:0}");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// The player's long route from the Hub to Stack takes the roads by the original's rule (Nameless, road preference 0.3), as the off-screen squads do
    /// (docs/game/pathfinding.md "Roads": 34054 walked for 16898 straight, Observed); without a road preference it goes over the terrain.
    /// </summary>
    [Fact]
    public void A_long_route_from_the_hub_to_stack_follows_the_roads()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = InstallData.FullLoadOrder!;
        var levels = InstallData.Levels!;
        var dir = Path.Combine(Path.GetTempPath(), "meitou-roadnav-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var map = TerrainHeightmap.Open(install!);
            using var nav = new NavSystem(install!, db, levels, (x, z) => (float)map.HeightAt(x, z), cache: new NavMeshCache(dir));
            var data = PopulationData.Create(db, levels.Towns(), roads: levels.Roads());
            nav.Walkability.Roads = data.Roads;
            Vector3 Town(string name) => levels.Towns().First(t => db.Find(t.TownId)?.Name == name).Position;
            var (hub, stack) = (Town("The Hub"), Town("Stack"));
            float straight = Vector2.Distance(new(hub.X, hub.Z), new(stack.X, stack.Z));
            var roadPoints = levels.Roads().SelectMany(r => r.Points).Select(p => new Vector2(p.X, p.Z)).ToList();

            // The length walked and the share of it (sampled every 200 units) within 400 of a road point.
            (float Walked, float OnRoad) Walk(float preference)
            {
                var path = nav.Walkability.FindPath(hub, stack, new NavAgent { RoadPreference = preference });
                Assert.True(path.Found);
                float walked = 0;
                int on = 0, samples = 0;
                for (int k = 1; k < path.Points.Count; k++)
                {
                    var (a, b) = (new Vector2(path.Points[k - 1].X, path.Points[k - 1].Z), new Vector2(path.Points[k].X, path.Points[k].Z));
                    float length = Vector2.Distance(a, b);
                    for (float d = 0; d < length; d += 200, samples++)
                    {
                        var q = Vector2.Lerp(a, b, d / length);
                        if (roadPoints.Any(r => Vector2.DistanceSquared(r, q) < 400 * 400)) on++;
                    }
                    walked += length;
                }
                return (walked, on / (float)samples);
            }

            var road = Walk(data.RoadPreferenceOf(data.PlayerFaction));
            Assert.True(road.Walked > straight * 1.8f && road.OnRoad > 0.9f, $"by road: walked {road.Walked:0} for a straight {straight:0}, {road.OnRoad:P0} near a road");
            var terrain = Walk(0);
            Assert.True(terrain.Walked < straight * 1.3f && terrain.OnRoad < 0.5f, $"over the terrain: walked {terrain.Walked:0} for a straight {straight:0}, {terrain.OnRoad:P0} near a road");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
