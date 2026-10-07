using System.Numerics;
using Meitou.Data.World;
using Meitou.Navigation;
using Meitou.Simulation;

namespace Meitou.Tests.Navigation;

/// <summary>Builder, pruner and queries on synthetic zones (no game install needed).</summary>
public class NavigationTests
{
    static readonly NavBuildSettings Settings = new() { Threads = 4 };

    /// <summary>A zone with flat ground at the given height; add obstacles and seeds, then build.</summary>
    sealed class Scene
    {
        public readonly ZoneGeometry G;
        public Scene(ZoneCoordinate zone, float groundY = 10)
        {
            var (ox, oz) = WorldLayout.ZoneOrigin(zone);
            G = new ZoneGeometry
            {
                Zone = zone,
                ZoneMin = new((float)ox, (float)oz),
                ZoneMax = new((float)ox + WorldLayout.ZoneSize, (float)oz + WorldLayout.ZoneSize),
                Margin = 72,
            };
            float m = 72;
            Quad(new(G.ZoneMin.X - m, groundY, G.ZoneMin.Y - m), new(G.ZoneMax.X + m, groundY, G.ZoneMax.Y + m), NavArea.Ground);
        }

        public float X0 => G.ZoneMin.X;
        public float Z0 => G.ZoneMin.Y;

        /// <summary>A horizontal rectangle facing up.</summary>
        public void Quad(Vector3 min, Vector3 max, byte area)
        {
            int a = G.AddVertex(new(min.X, min.Y, min.Z)), b = G.AddVertex(new(max.X, min.Y, min.Z));
            int c = G.AddVertex(new(max.X, min.Y, max.Z)), d = G.AddVertex(new(min.X, min.Y, max.Z));
            G.AddTriangle(a, d, c, area);
            G.AddTriangle(a, c, b, area);
        }

        /// <summary>A solid obstacle: the six faces as cutting triangles, outward-facing.</summary>
        public void Box(Vector3 min, Vector3 max)
        {
            var v = new int[8];
            for (int i = 0; i < 8; i++) v[i] = G.AddVertex(new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
            int[][] quads = [[0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4], [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6]];
            foreach (var q in quads)
            {
                G.AddTriangle(v[q[0]], v[q[1]], v[q[2]], NavArea.Null);
                G.AddTriangle(v[q[0]], v[q[2]], v[q[3]], NavArea.Null);
            }
            G.Carvers.Add(new NavVolume([new(min.X, min.Z), new(max.X, min.Z), new(max.X, max.Z), new(min.X, max.Z)], min.Y, max.Y));
        }

        public ZoneNavMesh Build(bool dropPruned = true)
        {
            var mesh = ZoneNavMeshBuilder.Build(G, Settings, out _);
            return dropPruned ? mesh.WithoutPruned() : mesh;
        }
    }

    static PathResult Route(NavWorld w, Vector3 a, Vector3 b, NavAgent? agent = null) => new NavQuery(w).FindPath(a, b, agent);

    static float Length(IReadOnlyList<Vector3> p)
    {
        float l = 0;
        for (int i = 1; i < p.Count; i++) l += Vector3.Distance(p[i - 1], p[i]);
        return l;
    }

    static bool CoversPoint(ZoneNavMesh m, float x, float z)
    {
        var l = new List<int>();
        m.PolygonsAt(x, z, l);
        return l.Count > 0;
    }

    [Fact]
    public void Open_ground_is_one_walkable_surface_and_paths_are_straight()
    {
        var s = new Scene(new(32, 32));
        s.G.Seeds.Add(new(s.X0 + 500, 10, s.Z0 + 500));
        var mesh = s.Build();
        Assert.True(mesh.PolygonCount > 0);
        Assert.True(CoversPoint(mesh, s.X0 + 2000, s.Z0 + 3000));
        var world = NavWorld.Empty.With(mesh);
        var a = new Vector3(s.X0 + 300, 10, s.Z0 + 300);
        var b = new Vector3(s.X0 + 4000, 10, s.Z0 + 3500);
        var path = Route(world, a, b);
        Assert.True(path.Found);
        // The corridor of polygons is chosen by edge midpoints, so the path can be a little longer than the straight line.
        Assert.InRange(Length(path.Points), Vector3.Distance(a, b) - 0.5f, Vector3.Distance(a, b) * 1.03f);
        Assert.All(path.Points, p => Assert.InRange(p.Y, 9, 11));
    }

    [Fact]
    public void An_obstacle_is_cut_out_and_paths_go_around_it()
    {
        var s = new Scene(new(32, 32));
        float cx = s.X0 + 2300, cz = s.Z0 + 2300;
        s.Box(new(cx - 100, 0, cz - 100), new(cx + 100, 80, cz + 100));
        s.G.Seeds.Add(new(s.X0 + 500, 10, s.Z0 + 500));
        var mesh = s.Build();
        Assert.False(CoversPoint(mesh, cx, cz));
        Assert.True(CoversPoint(mesh, cx + 140, cz));
        var world = NavWorld.Empty.With(mesh);
        var a = new Vector3(cx - 400, 10, cz);
        var b = new Vector3(cx + 400, 10, cz);
        var path = Route(world, a, b);
        Assert.True(path.Found);
        Assert.True(Length(path.Points) > 800 + 40, $"length {Length(path.Points)}");
        // No leg of the path passes through the box.
        for (int i = 1; i < path.Points.Count; i++)
            for (int k = 0; k <= 20; k++)
            {
                var p = Vector3.Lerp(path.Points[i - 1], path.Points[i], k / 20f);
                Assert.False(Math.Abs(p.X - cx) < 98 && Math.Abs(p.Z - cz) < 98, $"point {p}");
            }
    }

    [Fact]
    public void Only_regions_near_a_seed_survive()
    {
        var s = new Scene(new(32, 32));
        float cx = s.X0 + 2300, cz = s.Z0 + 2300;
        // A closed yard: four walls 20 thick around a 300 x 300 square.
        s.Box(new(cx - 170, 0, cz - 170), new(cx + 170, 80, cz - 150));
        s.Box(new(cx - 170, 0, cz + 150), new(cx + 170, 80, cz + 170));
        s.Box(new(cx - 170, 0, cz - 150), new(cx - 150, 80, cz + 150));
        s.Box(new(cx + 150, 0, cz - 150), new(cx + 170, 80, cz + 150));
        s.G.Seeds.Add(new(s.X0 + 500, 10, s.Z0 + 500));
        var pruned = s.Build();
        Assert.True(CoversPoint(pruned, s.X0 + 1000, s.Z0 + 1000));
        Assert.False(CoversPoint(pruned, cx, cz), "the closed yard has no seed and is dropped");

        s.G.Seeds.Add(new(cx, 10, cz));
        var seeded = s.Build();
        Assert.True(CoversPoint(seeded, cx, cz));
        // The two regions are not connected: no path from outside into the yard.
        var world = NavWorld.Empty.With(seeded);
        Assert.False(Route(world, new(s.X0 + 500, 10, s.Z0 + 500), new(cx, 10, cz)).Found);
    }

    [Fact]
    public void Without_any_seed_the_largest_region_is_kept()
    {
        var s = new Scene(new(32, 32));
        float cx = s.X0 + 2300, cz = s.Z0 + 2300;
        s.Box(new(cx - 170, 0, cz - 170), new(cx + 170, 80, cz - 150));
        s.Box(new(cx - 170, 0, cz + 150), new(cx + 170, 80, cz + 170));
        s.Box(new(cx - 170, 0, cz - 150), new(cx - 150, 80, cz + 150));
        s.Box(new(cx + 150, 0, cz - 150), new(cx + 170, 80, cz + 150));
        s.G.Seeds.Add(new(s.X0 + 4000, 500, s.Z0 + 4000)); // far above the ground: matches nothing
        var mesh = s.Build();
        Assert.True(CoversPoint(mesh, s.X0 + 1000, s.Z0 + 1000));
        Assert.False(CoversPoint(mesh, cx, cz));
    }

    [Fact]
    public void A_gap_narrower_than_the_footprint_is_not_a_passage()
    {
        var s = new Scene(new(32, 32));
        float cx = s.X0 + 2300, cz = s.Z0 + 2300;
        // A long wall across the way with one gap of 10 units.
        s.Box(new(cx - 1000, 0, cz - 10), new(cx - 5, 80, cz + 10));
        s.Box(new(cx + 5, 0, cz - 10), new(cx + 1000, 80, cz + 10));
        s.G.Seeds.Add(new(s.X0 + 500, 10, s.Z0 + 500));
        var world = NavWorld.Empty.With(s.Build());
        var a = new Vector3(cx, 10, cz - 100);
        var b = new Vector3(cx, 10, cz + 100);
        var slim = Route(world, a, b, new NavAgent { Radius = 4 });   // needs 8 of 10
        Assert.True(slim.Found);
        Assert.True(Length(slim.Points) < 260, $"slim {Length(slim.Points)}");
        var fat = Route(world, a, b, new NavAgent { Radius = 7 });    // needs 14 of 10: round the end of the wall
        Assert.True(fat.Found);
        Assert.True(Length(fat.Points) > 1500, $"fat {Length(fat.Points)}");
    }

    [Fact]
    public void Paths_cross_a_zone_border()
    {
        var left = new Scene(new(31, 32));
        var right = new Scene(new(32, 32));
        left.G.Seeds.Add(new(left.X0 + 500, 10, left.Z0 + 500));
        right.G.Seeds.Add(new(right.X0 + 500, 10, right.Z0 + 500));
        var world = NavWorld.Empty.With(left.Build()).With(right.Build());
        var a = new Vector3(left.X0 + 4000, 10, left.Z0 + 2000);
        var b = new Vector3(right.X0 + 600, 10, right.Z0 + 2600);
        var path = Route(world, a, b);
        Assert.True(path.Found);
        Assert.InRange(Length(path.Points), Vector3.Distance(a, b) - 0.5f, Vector3.Distance(a, b) * 1.03f);
        Assert.True(world.TryGroundHeight(right.X0 + 100, right.Z0 + 100, out float h));
        Assert.InRange(h, 9, 11);
        // Removing a zone leaves the other usable and unlinked.
        var alone = world.Without(new ZoneCoordinate(32, 32));
        Assert.False(alone.Contains(new ZoneCoordinate(32, 32)));
        Assert.False(Route(alone, a, b).Found);
        Assert.True(Route(alone, a, new Vector3(left.X0 + 100, 10, left.Z0 + 100)).Found);
    }

    [Fact]
    public void Water_costs_more_and_a_high_factor_makes_paths_go_round()
    {
        var s = new Scene(new(32, 32));
        float cx = s.X0 + 2300, cz = s.Z0 + 2300;
        // A lake 600 across, its surface above the ground.
        s.Quad(new(cx - 300, 10.5f, cz - 300), new(cx + 300, 10.5f, cz + 300), NavArea.Water);
        s.G.Seeds.Add(new(s.X0 + 500, 10, s.Z0 + 500));
        var mesh = s.Build();
        Assert.Contains(NavArea.Water, mesh.Areas);
        var world = NavWorld.Empty.With(mesh);
        var a = new Vector3(cx - 500, 10, cz);
        var b = new Vector3(cx + 500, 10, cz);
        var wader = Route(world, a, b, new NavAgent { WaterFactor = 1 });
        var avoider = Route(world, a, b, new NavAgent { WaterFactor = 20 });
        Assert.True(wader.Found && avoider.Found);
        Assert.Equal(1000, Length(wader.Points), 1);
        Assert.True(Length(avoider.Points) > 1000 + 100, $"avoider length {Length(avoider.Points)}");
    }

    [Fact]
    public void Doors_are_open_ground_that_a_closed_door_blocks()
    {
        var s = new Scene(new(32, 32));
        float cx = s.X0 + 2300, cz = s.Z0 + 2300;
        // A wall across the whole zone with a 40-unit gate painted as a door.
        s.Box(new(s.X0 - 72, 0, cz - 10), new(cx - 20, 80, cz + 10));
        s.Box(new(cx + 20, 0, cz - 10), new(s.G.ZoneMax.X + 72, 80, cz + 10));
        s.G.Painters.Add(new NavVolume([new(cx - 20, cz - 10), new(cx + 20, cz - 10), new(cx + 20, cz + 10), new(cx - 20, cz + 10)], 0, 40));
        s.G.Seeds.Add(new(s.X0 + 500, 10, s.Z0 + 500));
        var mesh = s.Build();
        Assert.Contains(NavArea.Door, mesh.Areas);
        var world = NavWorld.Empty.With(mesh);
        var a = new Vector3(cx, 10, cz - 300);
        var b = new Vector3(cx, 10, cz + 300);
        var open = Route(world, a, b);
        Assert.True(open.Found);
        Assert.Equal(600, Length(open.Points), 1);
        Assert.False(Route(world, a, b, new NavAgent { DoorsClosed = true }).Found);
    }

    [Fact]
    public void Walkability_answers_from_the_mesh_and_falls_back_where_a_zone_is_missing()
    {
        var s = new Scene(new(32, 32));
        float cx = s.X0 + 2300, cz = s.Z0 + 2300;
        s.Box(new(cx - 100, 0, cz - 100), new(cx + 100, 80, cz + 100));
        s.G.Seeds.Add(new(s.X0 + 500, 10, s.Z0 + 500));
        var w = new NavmeshWalkability((x, z) => 123);
        // Not loaded: the open-ground stand-in (everything above the water at height 123).
        Assert.True(w.IsWalkable(cx, cz));
        Assert.Equal(123, w.GroundHeight(cx, cz));
        w.SetWorld(NavWorld.Empty.With(s.Build()));
        Assert.False(w.IsWalkable(cx, cz));
        Assert.True(w.IsWalkable(cx + 300, cz));
        Assert.InRange(w.GroundHeight(cx + 300, cz), 9, 11);
        var path = w.FindPath(new(cx - 400, 10, cz), new(cx + 400, 10, cz));
        Assert.True(path.Found);
        Assert.True(Length(path.Points) > 840);
        // A zone that is not loaded keeps the stand-in.
        Assert.True(w.IsWalkable(s.X0 + 5000, s.Z0));
    }

    [Fact]
    public void Cache_round_trips_and_rejects_other_keys()
    {
        var s = new Scene(new(32, 32));
        s.Box(new(s.X0 + 2000, 0, s.Z0 + 2000), new(s.X0 + 2100, 80, s.Z0 + 2100));
        s.G.Seeds.Add(new(s.X0 + 500, 10, s.Z0 + 500));
        var mesh = s.Build();
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meitou-nav-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new NavMeshCache(dir);
            uint settings = NavMeshCache.SettingsHash(Settings);
            Assert.Null(cache.TryLoad(32, 32, 7, settings));
            cache.Save(mesh, 7, settings);
            var back = cache.TryLoad(32, 32, 7, settings)!;
            Assert.Equal(mesh.PolygonCount, back.PolygonCount);
            Assert.Equal(mesh.Vertices, back.Vertices);
            Assert.Equal(mesh.Areas, back.Areas);
            Assert.Equal(mesh.Polygons, back.Polygons);
            Assert.Equal(mesh.Neighbours, back.Neighbours);
            Assert.Equal(mesh.Links, back.Links);
            Assert.Null(cache.TryLoad(32, 32, 8, settings));
            Assert.Null(cache.TryLoad(32, 32, 7, settings + 1));
            Assert.Null(cache.TryLoad(31, 32, 7, settings));
            File.WriteAllBytes(cache.PathOf(32, 32), [1, 2, 3]);
            Assert.Null(cache.TryLoad(32, 32, 7, settings));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_funnel_pulls_the_path_tight_round_a_corner_and_keeps_clear_of_it()
    {
        var s = new Scene(new(32, 32));
        float cx = s.X0 + 2300, cz = s.Z0 + 2300;
        s.Box(new(cx - 300, 0, cz - 20), new(cx + 100, 80, cz + 20));
        s.G.Seeds.Add(new(s.X0 + 500, 10, s.Z0 + 500));
        var world = NavWorld.Empty.With(s.Build());
        var a = new Vector3(cx - 200, 10, cz - 150);
        var b = new Vector3(cx - 200, 10, cz + 150);
        var path = Route(world, a, b, new NavAgent { Radius = 4 });
        Assert.True(path.Found);
        // Round the nearer end of the wall (cx - 300), at least about the radius away from it.
        Assert.Contains(path.Points, p => p.X < cx - 300);
        foreach (var p in path.Points)
            Assert.False(p.X > cx - 300 - 2f && p.X < cx + 100 + 2f && Math.Abs(p.Z - cz) < 20 + 2f, $"{p} is too close to the wall");
        float plain = Length(path.Points);
        var fat = Route(world, a, b, new NavAgent { Radius = 20 });
        Assert.True(fat.Found);
        Assert.True(Length(fat.Points) > plain);
    }
}
