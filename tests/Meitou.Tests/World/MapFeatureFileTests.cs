using System.Text;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class MapFeatureFileTests
{
    [Fact]
    [Slow]
    public void Base_game_features_lie_in_their_zones_and_name_map_features()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        var file = MapFeatureFile.Open(install!); // throws unless the blocks tile the file exactly
        Assert.Equal(WorldLayout.ZoneCount * WorldLayout.ZoneCount, file.Zones.Count);
        var all = file.All().ToList();
        Assert.Equal(1821, all.Count);

        // All but five lie inside the zone of their slot (slot index = y * 64 + x).
        Assert.Equal(1816, all.Count(a => WorldLayout.ZoneOf(a.Feature.Position.X, a.Feature.Position.Z) == a.Zone));

        // Rotations are unit quaternions stored w first: most are pure yaw (x = z = 0).
        Assert.True(all.Count(a => Math.Abs(a.Feature.Rotation.Length() - 1) < 1e-3) >= all.Count - 1);
        Assert.True(all.Count(a => a.Feature.Rotation.X == 0 && a.Feature.Rotation.Z == 0) > all.Count / 2);

        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var found = all.Where(a => db.Find(a.Feature.StringId) is not null).ToList();
        Assert.Equal(1810, found.Count);
        Assert.All(found, a => Assert.Equal(FcsRecordType.MapFeatures, db.Find(a.Feature.StringId)!.Type));
    }
}
