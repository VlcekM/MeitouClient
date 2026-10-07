using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Navigation;
using Meitou.Simulation;

namespace Meitou.Tests.Navigation;

/// <summary>The navmesh pipeline on The Hub (zones 20.32 and 21.32) against the Kenshi install.</summary>
[Slow]
public class HubNavmeshTests
{
    static readonly object Gate = new();
    static (GameInstall Install, GameDatabase Db, WorldLevelData Levels)? loaded;
    static readonly Dictionary<ZoneCoordinate, (ZoneGeometry Geometry, ZoneNavMesh Full)> built = [];

    static (GameInstall Install, GameDatabase Db, WorldLevelData Levels)? Load()
    {
        lock (Gate)
        {
            if (loaded is not null) return loaded;
            var install = GameInstall.Locate();
            if (install is null) return null;
            return loaded = (install, GameDatabase.Load(LoadOrder.FromInstall(install)), WorldLevelData.Load(install));
        }
    }

    static (ZoneGeometry Geometry, ZoneNavMesh Full) Zone(ZoneCoordinate zone)
    {
        var (install, db, levels) = Load()!.Value;
        lock (Gate)
        {
            if (built.TryGetValue(zone, out var b)) return b;
            using var gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install));
            var g = gatherer.Gather(zone);
            var mesh = NavMeshPipeline.BuildZone(gatherer, g, new NavBuildSettings(), out _);
            return built[zone] = (g, mesh);
        }
    }

    static readonly ZoneCoordinate West = new(20, 32), East = new(21, 32);


    static string Pin(PathResult path) =>
        path.Found ? string.Join("\n", path.Points.Select(p => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.X:R},{p.Y:R},{p.Z:R}"))) : "none";

    [Fact]
    public void The_hub_paths_keep_their_exact_points()
    {
        Assert.SkipWhen(Load() is null, "Kenshi install not found");
        var west = Zone(West).Full;
        var east = Zone(East).Full;
        var house = new NavQuery(NavWorld.Empty.With(west.WithoutPruned()), new NavDoors());
        var street = new Vector3(-51290, 1566, 2625);
        var floor = new Vector3(-51158, 1579, 2664);
        Assert.Equal(HubPathPins.StreetToHouseFloor.ReplaceLineEndings("\n"), Pin(house.FindPath(street, floor)));
        Assert.Equal(HubPathPins.HouseFloorToStreet.ReplaceLineEndings("\n"), Pin(house.FindPath(floor, street)));
        var query = new NavQuery(NavWorld.Empty.With(west.WithoutPruned()).With(east.WithoutPruned()));
        Vector3 At(float x, float z) => new(x, 0, z);
        var outsideWest = At(-53500, 2000);
        var centre = At(-51000, 2900);
        var outsideEast = At(-49500, 3500);
        Assert.Equal(HubPathPins.WestToCentre.ReplaceLineEndings("\n"), Pin(query.FindPath(outsideWest, centre)));
        Assert.Equal(HubPathPins.CentreToEast.ReplaceLineEndings("\n"), Pin(query.FindPath(centre, outsideEast)));
        Assert.Equal(HubPathPins.WestToEast.ReplaceLineEndings("\n"), Pin(query.FindPath(outsideWest, outsideEast)));
        Assert.Equal(HubPathPins.WestToCentreDoorsClosed.ReplaceLineEndings("\n"), Pin(query.FindPath(outsideWest, centre, new NavAgent { DoorsClosed = true })));
    }

    [Fact]
    public void The_hub_zone_gathers_terrain_buildings_doors_and_seeds()
    {
        Assert.SkipWhen(Load() is null, "Kenshi install not found");
        var (g, _) = Zone(West);
        // 264 x 264 cells of the heightmap (the zone plus the margin), two triangles each.
        Assert.Equal(264 * 264 * 2, g.Stats.TerrainTriangles + g.Stats.WaterTriangles);
        Assert.InRange(g.Stats.Buildings, 100, 200);
        Assert.True(g.Stats.PartsWithCollision > 100);
        Assert.True(g.Stats.WalkableTriangles > 500 && g.Stats.CuttingTriangles > 5000);
        Assert.True(g.Painters.Count >= 3, $"{g.Painters.Count} door painters");
        Assert.True(g.Carvers.Count > 50);
        Assert.True(g.Seeds.Count >= 9);
        Assert.Equal(0, g.Stats.MissingFiles);
        // Every vertex finite and inside a sane box.
        Assert.All(g.Vertices, v => Assert.True(float.IsFinite(v)));
        Assert.True(g.MaxY < 6000 && g.MinY > -50);
    }

    [Fact]
    public void Buildings_are_cut_out_and_roofs_and_closed_yards_are_pruned()
    {
        Assert.SkipWhen(Load() is null, "Kenshi install not found");
        var g = Zone(West).Geometry;
        var full = ZoneNavMeshBuilder.Build(g, new NavBuildSettings(), out _); // the street side alone: interiors stand in the carved volumes
        Assert.True(full.PolygonCount > 5000);
        int pruned = full.Kept.Count(k => !k);
        Assert.True(pruned > 200, $"{pruned} pruned polygons");
        Assert.True(full.KeptCount > full.PolygonCount / 2);
        var kept = full.WithoutPruned();
        // Seeds and open ground are walkable; the middle of every solid building volume carved for an interior is not.
        var l = new List<int>();
        foreach (var seed in g.Seeds.Take(13))
        {
            l.Clear();
            kept.PolygonsAt(seed.X, seed.Z, l);
        }
        int checkedHulls = 0;
        foreach (var carver in g.Carvers.Where(c => c.Polygon.Length >= 4 && c.YMax - c.YMin > 50).Take(40))
        {
            var centre = carver.Polygon.Aggregate(Vector2.Zero, (a, p) => a + p) / carver.Polygon.Length;
            if (centre.X < g.ZoneMin.X + 100 || centre.X > g.ZoneMax.X - 100 || centre.Y < g.ZoneMin.Y + 100 || centre.Y > g.ZoneMax.Y - 100) continue;
            l.Clear();
            kept.PolygonsAt(centre.X, centre.Y, l);
            // Another carver or the building's walkable roof can lie over it, but no kept polygon may sit at the carved height.
            foreach (int p in l)
            {
                float y = kept.HeightAt(p, centre.X, centre.Y);
                Assert.False(y > carver.YMin + 5 && y < carver.YMax - 5, $"walkable surface at {y} inside the carved volume {carver.YMin}..{carver.YMax} at {centre}");
            }
            checkedHulls++;
        }
        Assert.True(checkedHulls > 10);
    }

    [Fact]
    public void Door_polygons_know_their_building_so_doors_can_flip_at_run_time()
    {
        Assert.SkipWhen(Load() is null, "Kenshi install not found");
        var (g, full) = Zone(West);
        Assert.NotNull(full.DoorIds);
        var doorPolygons = Enumerable.Range(0, full.PolygonCount).Where(p => full.Areas[p] == NavArea.Door).ToList();
        Assert.True(doorPolygons.Count >= 2);
        Assert.All(doorPolygons.Where(p => full.Kept[p]), p => Assert.True(full.DoorOf![p] >= 0));
        Assert.All(full.DoorIds!, id => Assert.Contains(g.Painters, v => v.Owner == id));
        // The ids survive pruning and the cache round trip.
        Assert.Equal(full.DoorIds, full.WithoutPruned().DoorIds);
    }

    [Fact]
    public void A_path_leads_from_the_street_into_a_house_through_its_door_and_back()
    {
        Assert.SkipWhen(Load() is null, "Kenshi install not found");
        var (_, west) = Zone(West);
        var world = NavWorld.Empty.With(west.WithoutPruned());
        var doors = new NavDoors();
        var query = new NavQuery(world, doors);
        // The Storm House by the Hub's west wall (placement 4933): street outside its door, a point on its floor.
        var street = new Vector3(-51290, 1566, 2625);
        var floor = new Vector3(-51158, 1579, 2664);
        var inward = query.FindPath(street, floor);
        Assert.True(inward.Found, "no way in");
        var outward = query.FindPath(floor, street);
        Assert.True(outward.Found, "no way out");
        foreach (var path in new[] { inward, outward })
        {
            float length = 0;
            for (int i = 1; i < path.Points.Count; i++) length += Vector3.Distance(path.Points[i - 1], path.Points[i]);
            Assert.InRange(length, 130, 400);
            // It stands on the floor at the end, 25 above the ground outside, and crosses a door polygon on the way.
            Assert.Contains(path.Points, p => p.Y > 1575);
            bool crossedDoor = false;
            for (int i = 1; i < path.Points.Count; i++)
            {
                int steps = (int)(Vector3.Distance(path.Points[i - 1], path.Points[i]) / 1.5f) + 1;
                for (int k = 0; k <= steps; k++)
                {
                    var p = Vector3.Lerp(path.Points[i - 1], path.Points[i], k / (float)steps);
                    if (world.TryFindPolygon(p, 2, out var r, out _) && world.Mesh(r.Zone).Areas[r.Polygon] == NavArea.Door) crossedDoor = true;
                }
            }
            Assert.True(crossedDoor, "the way does not cross the door");
        }
        // With that door closed the room is shut (its walls are solid).
        doors.Close("4933-Newwworld-INGAME");
        Assert.False(query.FindPath(street, floor).Found);
        doors.Open("4933-Newwworld-INGAME");
        Assert.True(query.FindPath(street, floor).Found);
    }

    [Fact]
    public void A_path_crosses_the_hub_through_its_gates_and_around_its_buildings()
    {
        Assert.SkipWhen(Load() is null, "Kenshi install not found");
        var (_, west) = Zone(West);
        var (_, east) = Zone(East);
        var world = NavWorld.Empty.With(west.WithoutPruned()).With(east.WithoutPruned());
        var query = new NavQuery(world);

        // From open ground west of the town, in through the north gate to its centre, then out through the east gate: the walls close the town otherwise.
        Vector3 At(float x, float z) => new(x, 0, z);
        var outsideWest = At(-53500, 2000);
        var centre = At(-51000, 2900);
        var outsideEast = At(-49500, 3500);
        foreach (var (a, b) in new[] { (outsideWest, centre), (centre, outsideEast), (outsideWest, outsideEast) })
        {
            var watch = Stopwatch.StartNew();
            var path = query.FindPath(a, b);
            watch.Stop();
            Assert.True(path.Found, $"no path {a} to {b}");
            float length = 0;
            for (int i = 1; i < path.Points.Count; i++) length += Vector3.Distance(path.Points[i - 1], path.Points[i]);
            Assert.InRange(length, Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z)) - 1, Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z)) * 1.8f);
            Assert.True(watch.ElapsedMilliseconds < 1500, $"{watch.ElapsedMilliseconds} ms");
            // Every point along the path stands on the mesh.
            for (int i = 1; i < path.Points.Count; i++)
            {
                int steps = (int)(Vector3.Distance(path.Points[i - 1], path.Points[i]) / 8) + 1;
                for (int k = 0; k <= steps; k++)
                {
                    var p = Vector3.Lerp(path.Points[i - 1], path.Points[i], k / (float)steps);
                    Assert.True(world.TryGroundHeight(p.X, p.Z, out _), $"{p} is off the mesh");
                }
            }
        }
        // The way back out of the Hub passes a gap where its gate was destroyed (the world data says so): closed doors change nothing there, but
        // a path with closed doors never stands on a door polygon.
        foreach (var (a, b) in new[] { (outsideWest, centre), (At(-51270, 2640), At(-51235, 2690)), (At(-51270, 3600), At(-51245, 3550)) })
        {
            var shut = query.FindPath(a, b, new NavAgent { DoorsClosed = true });
            if (!shut.Found) continue;
            for (int i = 1; i < shut.Points.Count; i++)
            {
                int steps = (int)(Vector3.Distance(shut.Points[i - 1], shut.Points[i]) / 2) + 1;
                for (int k = 0; k <= steps; k++)
                {
                    var p = Vector3.Lerp(shut.Points[i - 1], shut.Points[i], k / (float)steps);
                    if (world.TryFindPolygon(p, 6, out var r, out _)) Assert.NotEqual(NavArea.Door, world.Mesh(r.Zone).Areas[r.Polygon]);
                }
            }
        }
    }

    [Fact]
    public async Task The_service_builds_off_thread_caches_and_publishes_to_the_walkability()
    {
        Assert.SkipWhen(Load() is null, "Kenshi install not found");
        var (install, db, levels) = Load()!.Value;
        var dir = Path.Combine(Path.GetTempPath(), "meitou-nav-hub-" + Guid.NewGuid().ToString("N"));
        try
        {
            var map = TerrainHeightmap.Open(install);
            var walk = new NavmeshWalkability((x, z) => (float)map.HeightAt(x, z));
            Assert.True(walk.IsWalkable(-51000, 2900));
            using (var service = new NavMeshService(install, db, levels, walk, cache: new NavMeshCache(dir)))
            {
                var ready = await service.Request(West);
                Assert.Equal(NavMeshOrigin.Built, ready.Origin);
                Assert.True(walk.World.Contains(West));
                // A building's footprint is no longer walkable; the stand-in would have said yes.
                var carved = Zone(West).Geometry.Carvers.First(c => c.Polygon.Length >= 4 && c.YMax - c.YMin > 50);
                Assert.True(File.Exists(service.Cache.PathOf(West.X, West.Y)));
            }
            var walk2 = new NavmeshWalkability((x, z) => (float)map.HeightAt(x, z));
            using (var service = new NavMeshService(install, db, levels, walk2, cache: new NavMeshCache(dir)))
            {
                var ready = await service.Request(West);
                Assert.Equal(NavMeshOrigin.Cache, ready.Origin);
                Assert.True(ready.Milliseconds < 3000, $"{ready.Milliseconds} ms from the cache");
                Assert.Equal(walk.World.Find(West)!.PolygonCount, walk2.World.Find(West)!.PolygonCount);
            Assert.Equal(walk.World.Find(West)!.DoorIds, walk2.World.Find(West)!.DoorIds);
            }
            map.Dispose();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task NavSystem_serves_a_ring_of_zones_and_unloads_them()
    {
        Assert.SkipWhen(Load() is null, "Kenshi install not found");
        var (install, db, levels) = Load()!.Value;
        var dir = Path.Combine(Path.GetTempPath(), "meitou-nav-sys-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var map = TerrainHeightmap.Open(install);
            using var nav = new NavSystem(install, db, levels, (x, z) => (float)map.HeightAt(x, z), cache: new NavMeshCache(dir));
            nav.LoadRing(West, 1);
            await nav.LoadZone(West);
            await nav.LoadZone(East);
            Assert.True(nav.Walkability.World.Contains(West) && nav.Walkability.World.Contains(East));
            var path = nav.Walkability.FindPath(new(-53500, 900, 2000), new(-49500, 1500, 3500));
            Assert.True(path.Found);
            // Points stand on the ground the heightmap gives, within the mesh tolerance.
            Assert.All(path.Points, p => Assert.InRange(p.Y, nav.Walkability.GroundHeight(p.X, p.Z) - 4, nav.Walkability.GroundHeight(p.X, p.Z) + 4));
            nav.UnloadZone(East);
            Assert.False(nav.Walkability.World.Contains(East));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Building_a_zone_is_repeatable_and_fast_enough()
    {
        Assert.SkipWhen(Load() is null, "Kenshi install not found");
        var (install, db, levels) = Load()!.Value;
        var (g, first) = Zone(West);
        var settings = new NavBuildSettings();
        using var gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install));
        NavMeshPipeline.BuildZone(gatherer, g, settings, out _); // warm
        var second = NavMeshPipeline.BuildZone(gatherer, g, settings, out var times);
        Assert.Equal(first.PolygonCount, second.PolygonCount);
        Assert.Equal(first.Vertices, second.Vertices);
        Assert.Equal(first.Kept, second.Kept);
        Assert.True(times.Total < 3000, $"build took {times.Total:0} ms");
    }
}
