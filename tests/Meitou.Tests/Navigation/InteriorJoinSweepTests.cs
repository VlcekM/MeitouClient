using Meitou.Data.World;
using Meitou.Navigation;

namespace Meitou.Tests.Navigation;

/// <summary>
/// The join sweep (docs/game/pathfinding.md "Join sweep") over the interior coverage zones, the Ancient Labs (28.43, 37.35) and the Ash Domes (54.59) that
/// the unseeded-interior rule freed: no intact interior the original connects (mesh connections in its tile's streaming set) is sealed in ours, i.e. not
/// reached from the street in this zone's build resolved alone or a neighbour's. Ruins are left out (the original's own stitch cannot join 18 of them,
/// "Ruin joins"). The floor on the connected intact interiors matched to ours keeps a silent drop in matching from passing vacuously; it is the count
/// when the test was written less about a tenth (87 in all; The Hub has 2, its other 19 are ruins).
/// </summary>
[Slow]
public class InteriorJoinSweepTests
{
    [Theory]
    [InlineData(18, 35, 8, "Squin")]
    [InlineData(19, 29, 11, "Stack")]
    [InlineData(50, 33, 5, "Black Scratch")]
    [InlineData(47, 16, 10, "Heft")]
    [InlineData(20, 32, 2, "The Hub")]
    [InlineData(43, 11, 19, "Sho-Battai")]
    [InlineData(26, 13, 14, "World's End")]
    [InlineData(48, 45, 5, "Snailhouse lagoon")]
    [InlineData(28, 43, 1, "Ancient Lab")]
    [InlineData(37, 35, 1, "Ancient Lab")]
    [InlineData(54, 59, 2, "Ash Domes")]
    public void No_intact_interior_the_original_connects_is_sealed(int x, int z, int matchedFloor, string place)
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var zone = new ZoneCoordinate(x, z);
        var groups = OriginalNavTiles.LoadInteriors(install!, zone);
        Assert.NotEmpty(groups);
        using var gatherer = new ZoneGeometryGatherer(install!, InstallData.FullLoadOrder!, InstallData.LevelsWithInteriors!, new CollisionCache(install!));
        var settings = new NavBuildSettings();
        var full = NavMeshPipeline.BuildZone(gatherer, gatherer.Gather(zone), settings, out _, out var inner, interiors: true);
        var joins = InteriorJoinSweep.Compare(gatherer, zone, groups, InteriorJoinSweep.Sites(gatherer, zone, settings),
            InteriorJoinSweep.Reached(zone, full, inner, settings), settings);
        var intact = joins.Where(j => j.Original && j.Site is { Destroyed: false }).ToList();
        var sealedOnes = intact.Where(j => j.Sealed).Select(j => $"{j.Group.Uid}: {j.Site!.Key} {j.Site.Name} ({j.Group.Connections} connections)").ToList();
        Assert.True(sealedOnes.Count == 0, $"{place} {zone}: {sealedOnes.Count} of {intact.Count} connected intact interiors sealed: {string.Join("; ", sealedOnes)}");
        Assert.True(intact.Count >= matchedFloor, $"{place} {zone}: {intact.Count} connected intact interiors matched to ours ({groups.Count} original groups), floor {matchedFloor}");
    }
}
