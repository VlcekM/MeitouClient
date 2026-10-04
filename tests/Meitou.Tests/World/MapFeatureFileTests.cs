using System.Numerics;
using System.Text;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class MapFeatureFileTests
{
    static byte[] Build(int slots, Dictionary<int, (string Id, Vector3 Pos)[]> zones)
    {
        var blocks = new MemoryStream();
        var table = new List<(uint Offset, uint Size)>();
        long start = 8 + 8L * slots;
        for (int i = 0; i < slots; i++)
        {
            if (!zones.TryGetValue(i, out var list)) { table.Add((0, 0)); continue; }
            long at = blocks.Position;
            var w = new BinaryWriter(blocks, Encoding.UTF8, leaveOpen: true);
            w.Write(list.Length);
            foreach (var (id, pos) in list)
            {
                w.Write((byte)id.Length); w.Write(Encoding.UTF8.GetBytes(id));
                w.Write(pos.X); w.Write(pos.Y); w.Write(pos.Z);
                w.Write(1f); w.Write(2f); w.Write(1f);
                w.Write(0.6f); w.Write(0f); w.Write(0.8f); w.Write(0f); // w x y z
            }
            w.Flush();
            table.Add(((uint)(start + at), (uint)(blocks.Position - at)));
        }
        var ms = new MemoryStream();
        var h = new BinaryWriter(ms);
        h.Write("MF01"u8); h.Write(slots);
        foreach (var (o, s) in table) { h.Write(o); h.Write(s); }
        h.Write(blocks.ToArray());
        return ms.ToArray();
    }

    [Fact]
    public void Reads_per_zone_blocks()
    {
        var bytes = Build(130, new() { [1] = [("5-a.mod", new(1, 2, 3)), ("6-a.mod", new(4, 5, 6))], [129] = [("7-a.mod", new(-1, 0, 1))] });
        var file = MapFeatureFile.Read(bytes);
        Assert.Equal(130, file.Zones.Count);
        Assert.Equal(["5-a.mod", "6-a.mod"], file.Zones[1].Select(f => f.StringId));
        Assert.Equal(new Vector3(4, 5, 6), file.Zones[1][1].Position);
        Assert.Equal(new Vector3(1, 2, 1), file.Zones[1][1].Scale);
        Assert.Equal(new Quaternion(0, 0.8f, 0, 0.6f), file.Zones[1][1].Rotation);
        Assert.Equal(new ZoneCoordinate(1, 2), MapFeatureFile.ZoneOf(129));
        Assert.Equal(3, file.All().Count());
        Assert.Throws<InvalidDataException>(() => MapFeatureFile.Read([.. bytes, 0]));
        Assert.Throws<InvalidDataException>(() => MapFeatureFile.Read(bytes[..^1]));
    }

    [Fact]
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
        Assert.All(found, a => Assert.Equal(FcsRecordType.MAP_FEATURES, db.Find(a.Feature.StringId)!.Type));
    }
}
