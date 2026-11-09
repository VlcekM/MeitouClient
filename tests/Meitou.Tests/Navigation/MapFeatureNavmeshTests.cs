using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;
using Meitou.Simulation;

namespace Meitou.Tests.Navigation;

/// <summary>
/// Map features in the navmesh and the long routes (docs/game/pathfinding.md "Map features", "Foliage meshes", "Long routes"),
/// against the Kenshi install. The points come from <c>meitou-tools navmesh --probe</c> / <c>--feature-tops</c> / <c>--route</c>;
/// the zones are built alone, as <c>--around 0</c> does.
/// </summary>
[Slow]
public class MapFeatureNavmeshTests
{
    static readonly object Gate = new();
    static readonly Dictionary<ZoneCoordinate, (ZoneGeometry Geometry, ZoneNavMesh Kept)> Built = [];

    static readonly ZoneCoordinate Rebirth = new(21, 19), Mourn = new(34, 46), Squin = new(18, 35);

    static (ZoneGeometry Geometry, ZoneNavMesh Kept) Zone(ZoneCoordinate zone)
    {
        var install = InstallData.Install!;
        lock (Gate)
        {
            if (Built.TryGetValue(zone, out var b)) return b;
            using var gatherer = new ZoneGeometryGatherer(install, InstallData.FullLoadOrder!, InstallData.LevelsWithInteriors!,
                new CollisionCache(install));
            var settings = new NavBuildSettings();
            var g = gatherer.Gather(zone);
            return Built[zone] = (g, ZoneKeptClosure.ResolveAlone(NavMeshPipeline.BuildZone(gatherer, g, settings, out _), settings));
        }
    }

    /// <summary>
    /// The terrain height at a point (the heightmap the gatherer reads).
    /// </summary>
    static float Ground(float x, float z)
    {
        using var map = TerrainHeightmap.Open(InstallData.Install!);
        return (float)map.HeightAt(x, z);
    }

    // Rebirth's statue foundation.
    static readonly (float X, float Z)[] StatueFoundation = [(-47503, -56300), (-47300, -56079)];

    /// <summary>
    /// The heights of the kept polygons over a point.
    /// </summary>
    static List<float> KeptHeights(ZoneNavMesh kept, float x, float z)
    {
        var polygons = new List<int>();
        kept.PolygonsAt(x, z, polygons);
        return [.. polygons.Select(p => kept.HeightAt(p, x, z))];
    }

    static string Describe(List<float> heights) =>
        heights.Count == 0
            ? "none"
            : string.Join(", ", heights.Select(h => h.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Rebirth_statue_foundation_is_solid_and_its_top_is_walked()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var (_, kept) = Zone(Rebirth);
        // Huge_Stone_Emperor_Statue's foundation (issue #161): terrain 724 / 738 inside it,
        // its top at 755.8 in the original's tile (ours 755.9 / 755.5).
        foreach (var (x, z) in StatueFoundation)
        {
            var heights = KeptHeights(kept, x, z);
            float ground = Ground(x, z);
            Assert.False(
                heights.Any(h => MathF.Abs(h - ground) < 12),
                $"{x},{z}: kept ground at the terrain {ground:0.0} inside the foundation ({Describe(heights)})");
            Assert.True(
                heights.Any(h => MathF.Abs(h - 755.8f) < 2),
                $"{x},{z}: no kept polygon on the foundation's top ~755.8 ({Describe(heights)})");
        }
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
                float length = Vector2.Distance(new(points[i - 1].X, points[i - 1].Z), new(points[i].X, points[i].Z));
                int n = (int)MathF.Ceiling(length / 16);
                for (int k = 0; k < n; k++)
                {
                    var q = Vector3.Lerp(points[i - 1], points[i], k / (float)n);
                    if (features.Any(f => q.X >= f[0].X && q.X <= f[1].X && q.Z >= f[0].Y && q.Z <= f[2].Y)) inside++;
                }
            }
            return inside;
        }

        // With the buildings alone the route walks through the statue and gravel piles (89 samples when written);
        // with the features it goes round them.
        Assert.True(Inside(buildings) > 40, "the route with the buildings alone no longer crosses a feature; the test no longer discriminates");
        Assert.Equal(0, Inside([.. buildings, .. features]));
    }
}
