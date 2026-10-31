using System.Numerics;
using Meitou.Data.World;
using Meitou.Navigation;

namespace Meitou.Tests.Navigation;

/// <summary>
/// Zones loaded without their neighbours (docs/game/pathfinding.md "Zone borders", "Long routes"), on the Fog Islands (zone 14.26, one seed) of the install:
/// a zone published alone keeps the ground that goes on into the next zone, and a goal in another block of loaded zones is a long route through the zones between.
/// </summary>
[Slow]
public class ZoneBorderTests
{
    static readonly ZoneCoordinate FogIslands = new(14, 26);
    /// <summary>Where a saved character stood, 2,300 from the zone's borders, on ground the original's tile covers and the zone's own seed does not reach.</summary>
    static readonly Vector3 Inland = new(-80620, 709, -24171);

    [Fact]
    public void A_zone_built_alone_keeps_the_ground_that_reaches_its_border()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, "Kenshi install not found");
        using var gatherer = new ZoneGeometryGatherer(install!, InstallData.FullLoadOrder!, InstallData.Levels!, new CollisionCache(install!));
        var g = gatherer.Gather(FogIslands);
        var settings = new NavBuildSettings();
        var built = NavMeshPipeline.BuildZone(gatherer, g, settings, out _, interiors: false);
        Assert.True(g.Seeds.Count <= 1, $"{g.Seeds.Count} seeds: no longer the one-seed case");
        // The built mesh is pruned by its own seed only (269 of 8,292 polygons); the border rule applies when the zone is published with no neighbour loaded.
        Assert.True(built.KeptCount < built.PolygonCount / 2, $"{built.KeptCount} of {built.PolygonCount} polygons kept before publishing");
        var mesh = ZoneKeptClosure.ResolveAlone(built, settings);
        var here = new List<int>();
        mesh.PolygonsAt(Inland.X, Inland.Z, here);
        Assert.NotEmpty(here);
        Assert.True(mesh.KeptCount > built.PolygonCount / 2, $"{mesh.KeptCount} of {built.PolygonCount} polygons kept");
    }

    [Fact]
    public async Task A_goal_in_another_block_of_loaded_zones_is_a_long_route_through_the_zones_between()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var dir = Path.Combine(Path.GetTempPath(), "meitou-zoneborder-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var map = TerrainHeightmap.Open(install!);
            using var nav = new NavSystem(install!, InstallData.FullLoadOrder!, InstallData.Levels!, (x, z) => (float)map.HeightAt(x, z), cache: new NavMeshCache(dir));
            var far = new ZoneCoordinate(17, 26);
            await nav.LoadZone(FogIslands);
            await nav.LoadZone(far);
            var (fx, fz) = WorldLayout.ZoneOrigin(far);
            var goal = new Vector3((float)fx + WorldLayout.ZoneSize / 2f, 0, (float)fz + WorldLayout.ZoneSize / 2f);
            var path = nav.Walkability.FindPath(Inland, goal);
            Assert.True(path.IsProvisional, $"{path.Points.Count} points, exact {path.Exact}");
            // The terrain's route through 15.26 and 16.26, not one straight step from the nearest reachable point to the goal.
            Assert.True(path.Points.Count - path.Exact > 1, $"{path.Points.Count - path.Exact} provisional point(s)");
            Assert.Contains(nav.Walkability.RouteZones(TimeSpan.FromMinutes(1)), z => z == new ZoneCoordinate(15, 26));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
