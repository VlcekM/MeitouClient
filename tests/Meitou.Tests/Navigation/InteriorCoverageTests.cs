using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Navigation;

namespace Meitou.Tests.Navigation;

/// <summary>
/// How much of the original tiles' interior meshes our build covers, per town zone (docs/game/pathfinding.md "Stairs under floors": a stair or storey the
/// builder loses shows as uncovered faces). The floors are the measured coverage less one point when the test was written; a drop means a building lost a
/// stair, a floor or its interior (issues #139, #142, #147, #149).
/// </summary>
[Slow]
public class InteriorCoverageTests
{
    [Theory]
    [InlineData(18, 35, 95, "Squin")]
    [InlineData(43, 11, 89, "Sho-Battai")]
    [InlineData(50, 33, 86, "Black Scratch")]
    [InlineData(20, 32, 97, "The Hub")]
    [InlineData(47, 16, 92, "Heft")]
    [InlineData(26, 13, 91, "World's End")]
    [InlineData(19, 29, 79, "Stack")]
    [InlineData(48, 45, 98, "Snailhouse lagoon")]
    public void A_town_zone_covers_at_least_its_share_of_the_original_interior_faces(int x, int z, int floorPercent, string town)
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var zone = new ZoneCoordinate(x, z);
        var groups = OriginalNavTiles.LoadInteriors(install!, zone);
        Assert.NotEmpty(groups);
        using var gatherer = new ZoneGeometryGatherer(install!, InstallData.FullLoadOrder!, InstallData.LevelsWithInteriors!, new CollisionCache(install!));
        var settings = new NavBuildSettings();
        var g = gatherer.Gather(zone);
        var mesh = ZoneKeptClosure.ResolveAlone(NavMeshPipeline.BuildZone(gatherer, g, settings, out _, interiors: true), settings);
        var coverage = OriginalNavTiles.CoverInteriors(groups, mesh, g.ZoneMin, g.ZoneMax);
        int faces = coverage.Sum(c => c.Faces), covered = coverage.Sum(c => c.Covered);
        Assert.True(faces > 0, $"{town} {zone}: no original interior face in the zone");
        double percent = 100.0 * covered / faces;
        var worst = coverage.Where(c => c.Faces > 0 && c.Covered < 0.9 * c.Faces).OrderBy(c => (double)c.Covered / c.Faces).Take(5)
            .Select(c => $"{c.Group.Uid} at {c.Group.Origin.X:0},{c.Group.Origin.Z:0} {c.Covered}/{c.Faces} missing y {c.MissingLow:0}..{c.MissingHigh:0}");
        Assert.True(percent >= floorPercent, $"{town} {zone}: {covered}/{faces} faces covered ({percent:0.0} %), floor {floorPercent} %; worst groups: {string.Join("; ", worst)}");
    }
}
