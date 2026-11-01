using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class LevelFileTests
{
    // --- Base game -------------------------------------------------------------------------------

    static IEnumerable<string> LevelFiles(GameInstall install) =>
        Directory.EnumerateFiles(install.DataDirectory, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".zone", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".level", StringComparison.OrdinalIgnoreCase));

    [Fact]
    [Slow]
    public void Every_base_game_level_file_parses_to_its_end_and_round_trips()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        var formats = new Dictionary<int, int>();
        int trailers = 0, wideTrailers = 0;
        foreach (var path in LevelFiles(install!))
        {
            var bytes = File.ReadAllBytes(path);
            var file = LevelFile.Read(bytes); // throws unless records and trailer consume the file exactly
            formats[file.Format] = formats.GetValueOrDefault(file.Format) + 1;
            if (file.Trailer is not null) trailers++;
            if (file.TrailerEntryInts == 2) wideTrailers++;
            if (!file.IsLegacy)
                Assert.True(file.ToBytes().AsSpan().SequenceEqual(bytes), $"{path} does not round-trip");
            else
                Assert.DoesNotContain(Path.Combine("data", "newland"), path); // legacy formats only outside the Newland world
            if (file.Trailer is { Length: > 0 } t && file.TrailerEntryInts == 1)
                Assert.True(t.SequenceEqual(t.Order()) && t.All(v => v > 0), $"{path}: trailer is not ascending positive ints");
        }
        Assert.Equal(715, formats.Values.Sum());
        Assert.Equal(new Dictionary<int, int> { [10] = 2, [13] = 4, [15] = 680, [16] = 29 }, formats);
        Assert.Equal(657, trailers);
        Assert.Equal(3, wideTrailers);
    }

    [Fact]
    [Slow]
    public void Base_game_world_placements_are_consistent()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        var world = WorldLevelData.LoadBaseGame(install!);
        Assert.Equal(["(base)", "Newwworld", "Dialogue", "rebirth"], world.Layers.Select(l => l.ToString()));
        Assert.Equal(500, world.Zones.Count);
        Assert.All(world.Zones.Keys, z => Assert.True(z.IsInsideGrid));

        // Every placed building lies inside the zone whose file places it.
        var buildings = world.Buildings().ToList();
        Assert.Equal(11714, buildings.Count);
        Assert.All(buildings, b => Assert.Equal(b.Zone, WorldLayout.ZoneOf(b.Position.X, b.Position.Z)));
        // The other building-list entries sit at the origin and point at records of their own zone file.
        var local = world.BuildingListEntries().Where(e => e.TargetsZoneRecord).ToList();
        Assert.All(local, e => Assert.Equal(Vector3.Zero, e.Position));
        Assert.All(local, e => Assert.Equal(FcsRecordType.INVENTORY_STATE, world.Zones[e.Zone].Find(e.BuildingId)!.Type));

        // Targets resolve against the merged game data, except three buildings that no base file defines.
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var missing = buildings.Select(b => b.BuildingId).Where(id => db.Find(id) is null).Distinct().Order().ToList();
        Assert.Equal(["97576-Newwworld.mod", "97577-Newwworld.mod", "97578-Newwworld.mod"], missing);
        Assert.All(buildings.Where(b => db.Find(b.BuildingId) is not null), b => Assert.Equal(FcsRecordType.BUILDING, db.Find(b.BuildingId)!.Type));
        var towns = world.Towns().ToList();
        Assert.Equal(304, towns.Count);
        Assert.All(towns, t => Assert.Equal(FcsRecordType.TOWN, db.Find(t.TownId)?.Type));

        // A town's GAMESTATE_TOWN lists the zone its placement lies in (when it lists any).
        var townZones = world.TownZones();
        var listed = towns.Where(t => townZones.GetValueOrDefault(t.InstanceId) is { Count: > 0 }).ToList();
        Assert.Equal(224, listed.Count);
        Assert.All(listed, t => Assert.Contains(WorldLayout.ZoneOf(t.Position.X, t.Position.Z), townZones[t.InstanceId]));
    }
}
