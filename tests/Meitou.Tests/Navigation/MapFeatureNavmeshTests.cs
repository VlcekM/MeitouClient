using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Navigation;
using Meitou.Simulation;

namespace Meitou.Tests.Navigation;

/// <summary>
/// Map features in the navmesh and the long routes (docs/game/pathfinding.md "Map features", "Foliage meshes", "Long routes"), against the Kenshi install. The
/// points come from <c>meitou-tools navmesh --probe</c> / <c>--feature-tops</c> / <c>--route</c>; the zones are built alone, as <c>--around 0</c> does.
/// </summary>
[Slow]
public class MapFeatureNavmeshTests
{
    static readonly object Gate = new();
    static readonly Dictionary<ZoneCoordinate, (ZoneGeometry Geometry, ZoneNavMesh Kept)> built = [];

    static readonly ZoneCoordinate Rebirth = new(21, 19), Mourn = new(34, 46), Squin = new(18, 35);

    static (ZoneGeometry Geometry, ZoneNavMesh Kept) Zone(ZoneCoordinate zone)
    {
        var install = InstallData.Install!;
        lock (Gate)
        {
            if (built.TryGetValue(zone, out var b)) return b;
            using var gatherer = new ZoneGeometryGatherer(install, InstallData.FullLoadOrder!, InstallData.LevelsWithInteriors!, new CollisionCache(install));
            var settings = new NavBuildSettings();
            var g = gatherer.Gather(zone);
            return built[zone] = (g, ZoneKeptClosure.ResolveAlone(NavMeshPipeline.BuildZone(gatherer, g, settings, out _), settings));
        }
    }

    /// <summary>The terrain height at a point (the heightmap the gatherer reads).</summary>
    static float Ground(float x, float z)
    {
        using var map = TerrainHeightmap.Open(InstallData.Install!);
        return (float)map.HeightAt(x, z);
    }

    // Rebirth's statue foundation, a gravel pile (21.19), Mourn's tube band and wall walkway (34.46), Squin's canopy (18.35).
    static readonly (float X, float Z)[] StatueFoundation = [(-47503, -56300), (-47300, -56079)];
    static readonly (float X, float Z) GravelPile = (-46794, -57195);
    static readonly (float X, float Z, float Top)[] TubeBand = [(12600, 66216, 1078.5f), (12800, 66216, 1078.5f), (13200, 66216, 1078.5f), (12400, 66304, 1140.1f), (13000, 66456, 1246.5f)];
    static readonly (float X, float Z) WallWalkway = (12600, 66150);
    static readonly (float X, float Z) SquinCanopy = (-60300, 15908);

    /// <summary>The heights of the kept polygons over a point.</summary>
    static List<float> KeptHeights(ZoneNavMesh kept, float x, float z)
    {
        var polygons = new List<int>();
        kept.PolygonsAt(x, z, polygons);
        return [.. polygons.Select(p => kept.HeightAt(p, x, z))];
    }

    static string Describe(List<float> heights) => heights.Count == 0 ? "none" : string.Join(", ", heights.Select(h => h.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Rebirth_statue_foundation_is_solid_and_its_top_is_walked()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var (_, kept) = Zone(Rebirth);
        // Huge_Stone_Emperor_Statue's foundation (issue #161): terrain 724 / 738 inside it, its top at 755.8 in the original's tile (ours 755.9 / 755.5).
        foreach (var (x, z) in StatueFoundation)
        {
            var heights = KeptHeights(kept, x, z);
            float ground = Ground(x, z);
            Assert.False(heights.Any(h => MathF.Abs(h - ground) < 12), $"{x},{z}: kept ground at the terrain {ground:0.0} inside the foundation ({Describe(heights)})");
            Assert.True(heights.Any(h => MathF.Abs(h - 755.8f) < 2), $"{x},{z}: no kept polygon on the foundation's top ~755.8 ({Describe(heights)})");
        }
    }

    [Fact]
    public void A_gravel_pile_has_no_ground_inside_it()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var (_, kept) = Zone(Rebirth);
        // Gravel-Pile02 at -46794,1033,-57195: terrain 1045.7 at its centre (walked through without features), its top 1159.5 (the original's 1152.6).
        var (x, z) = GravelPile;
        var heights = KeptHeights(kept, x, z);
        float ground = Ground(x, z);
        Assert.False(heights.Any(h => MathF.Abs(h - ground) < 12), $"kept ground at the terrain {ground:0.0} inside the gravel pile ({Describe(heights)})");
        Assert.True(heights.Any(h => MathF.Abs(h - 1159.5f) < 4), $"no kept polygon on the gravel pile's top ~1159.5 ({Describe(heights)})");
    }

    [Fact]
    public void Mourns_tube_does_not_join_the_defensive_wall_walkway()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var (_, kept) = Zone(Mourn);
        // Vast_Land Tube 01's band past where its top runs inside Defensive Wall IV 34.46:20-/22- (FeatureSeamCells): kept without the seam rule, not in the original.
        foreach (var (x, z, top) in TubeBand)
        {
            var heights = KeptHeights(kept, x, z);
            Assert.False(heights.Any(h => MathF.Abs(h - top) < 8), $"{x},{z}: kept polygon on the tube's top {top} ({Describe(heights)})");
        }
        // The wall's walkway beside it (y ~1048, over the wall's solid 1035..1046) stays kept.
        var walkway = KeptHeights(kept, WallWalkway.X, WallWalkway.Z);
        Assert.True(walkway.Any(h => MathF.Abs(h - 1048) < 2), $"no kept walkway at {WallWalkway} ~1048 ({Describe(walkway)})");
    }

    [Fact]
    public void Squins_obstacle_canopy_keeps_the_ground_under_it()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var (g, kept) = Zone(Squin);
        var (x, z) = SquinCanopy;
        float ground = Ground(x, z);
        // An obstacle foliage mesh open below has a face ~51 over the terrain (420.6 under 471.9); the inside rule is not applied to it, so the ground stays as in the original's tile.
        Assert.True(FeatureOrFoliageHeights(g, x, z).Any(y => y > ground + 40 && y < ground + 60), "the canopy's face over the point is gone; the point no longer tests a canopy");
        var heights = KeptHeights(kept, x, z);
        Assert.True(heights.Any(h => MathF.Abs(h - ground) < 2), $"no kept ground at the terrain {ground:0.0} under the canopy ({Describe(heights)})");
    }

    /// <summary>The heights of the gathered map feature and foliage triangles over a point.</summary>
    static List<float> FeatureOrFoliageHeights(ZoneGeometry g, float x, float z)
    {
        var result = new List<float>();
        for (int t = g.BuildingLastTriangle; t < g.TriangleCount; t++)
        {
            Vector3 V(int k) { int i = g.Indices[t * 3 + k]; return new(g.Vertices[i * 3], g.Vertices[i * 3 + 1], g.Vertices[i * 3 + 2]); }
            Vector3 a = V(0), b = V(1), c = V(2);
            if (NavGeometry.Barycentric(x, z, a, b, c, out float u, out float v, out float w) && u >= 0 && v >= 0 && w >= 0) result.Add(u * a.Y + v * b.Y + w * c.Y);
        }
        return result;
    }

    [Fact]
    public void The_long_route_past_the_Rebirth_statue_goes_round_the_features()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = InstallData.FullLoadOrder!;
        var levels = InstallData.LevelsWithInteriors!;
        // As the game's route grid (NavSystem's routeObstacles) and `meitou-tools navmesh --route -47500,-58500,-47500,-53500` build it.
        using var map = TerrainHeightmap.Open(install!);
        Func<float, float, float> terrain = (x, z) => (float)map.HeightAt(x, z);
        Func<double, double, float> placement = (x, z) => (float)map.HeightAt(x, z);
        var raw = map.Downsample(2, out int size);
        var grid = new HeightWindow(0, 0, 2, size, size, raw);
        Func<float, float, float> slope = (x, z) => grid.TryHeightAt(x, z, out float h) ? h : terrain(x, z);
        var footprints = new BuildingFootprints(install!, db, levels);
        var buildings = footprints.Obstacles(levels.Buildings(), levels, placement);
        var features = footprints.FeatureObstacles(placement);
        Assert.NotEmpty(features);
        var from = new Vector3(-47500, terrain(-47500, -58500), -58500);
        var to = new Vector3(-47500, terrain(-47500, -53500), -53500);

        // Samples every 16 units along the route inside a feature rectangle.
        int Inside(IReadOnlyList<Vector2[]> obstacles)
        {
            var points = new CoarseRoute(terrain, () => slope, () => obstacles).Find(from, to, WaterCost.Uniform(1));
            Assert.True(points.Count >= 2, "no route");
            int inside = 0;
            for (int i = 1; i < points.Count; i++)
            {
                int n = (int)MathF.Ceiling(Vector2.Distance(new(points[i - 1].X, points[i - 1].Z), new(points[i].X, points[i].Z)) / 16);
                for (int k = 0; k < n; k++)
                {
                    var q = Vector3.Lerp(points[i - 1], points[i], k / (float)n);
                    if (features.Any(f => q.X >= f[0].X && q.X <= f[1].X && q.Z >= f[0].Y && q.Z <= f[2].Y)) inside++;
                }
            }
            return inside;
        }

        // With the buildings alone the route walks through the statue and gravel piles (89 samples when written); with the features it goes round them.
        Assert.True(Inside(buildings) > 40, "the route with the buildings alone no longer crosses a feature; the test no longer discriminates");
        Assert.Equal(0, Inside([.. buildings, .. features]));
    }
}
