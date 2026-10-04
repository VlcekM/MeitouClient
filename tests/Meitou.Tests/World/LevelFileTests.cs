using System.Numerics;
using System.Text;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class LevelFileTests
{
    static FcsFile ZoneData()
    {
        var file = new FcsFile { FileType = FcsFileType.V15, NextId = 12 };
        var state = new FcsRecord { Type = (int)FcsRecordType.GAMESTATE_BUILDING, Id = 7, Name = "0", StringId = "3-base-S23", ByteSize = 99 };
        state.Floats["world Y pos"] = 420.5f;
        state.Strings["owner faction ID"] = "1-gamedata.base";
        var list = new FcsRecord { Type = (int)FcsRecordType.INSTANCE_COLLECTION, Name = "0", StringId = WorldLevelData.BuildingListId };
        var placed = new FcsInstance { Id = "3-base", Target = "55-gamedata.base", Position = new Vector3(-100, 2.5f, 4700), Rotation = Quaternion.Identity };
        placed.States.Add("3-base-S23");
        list.Instances.Add(placed);
        file.Records.Add(state);
        file.Records.Add(list);
        return file;
    }

    [Fact]
    public void Format_15_with_trailer_round_trips_and_keeps_stored_record_sizes()
    {
        var level = new LevelFile { Format = 15, Data = ZoneData(), Trailer = [1, 2, 5] };
        var bytes = level.ToBytes();
        Assert.Equal(15, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(12, BitConverter.ToInt32(bytes, 4));    // next id right after the type: no header
        Assert.Equal(2, BitConverter.ToInt32(bytes, 8));     // record count
        Assert.Equal(99u, BitConverter.ToUInt32(bytes, 12)); // stored size kept even though it is wrong
        Assert.Equal([3, 1, 2, 5], Enumerable.Range(0, 4).Select(i => BitConverter.ToInt32(bytes, bytes.Length - 16 + 4 * i)));

        var read = LevelFile.Read(bytes);
        Assert.Equal(15, read.Format);
        Assert.Equal(FcsFileType.V15, read.Data.FileType);
        Assert.Equal([1, 2, 5], read.Trailer!);
        Assert.Equal(1, read.TrailerEntryInts);
        Assert.Equal(420.5f, read.Data.Records[0].Floats["world Y pos"]);
        Assert.Equal("55-gamedata.base", read.Data.Records[1].Instances[0].Target);
        Assert.Equal(bytes, read.ToBytes());
    }

    [Fact]
    public void Trailer_is_optional_and_must_end_the_file()
    {
        var bytes = new LevelFile { Format = 15, Data = ZoneData() }.ToBytes();
        Assert.Null(LevelFile.Read(bytes).Trailer);
        Assert.Equal(bytes, LevelFile.Read(bytes).ToBytes());

        Assert.Throws<FcsFormatException>(() => LevelFile.Read([.. bytes, 1, 0]));                    // stray bytes
        Assert.Throws<FcsFormatException>(() => LevelFile.Read([.. bytes, 2, 0, 0, 0, 9, 0, 0, 0])); // count 2, one value
        var empty = LevelFile.Read([.. bytes, 0, 0, 0, 0]);
        Assert.Equal([], empty.Trailer!);
    }

    [Fact]
    public void Format_16_header_is_kept()
    {
        var data = ZoneData();
        data.FileType = FcsFileType.V16;
        data.Author = "someone";
        var bytes = new LevelFile { Format = 16, Data = data, Trailer = [4] }.ToBytes();
        var read = LevelFile.Read(bytes);
        Assert.Equal("someone", read.Data.Author);
        Assert.Equal(bytes, read.ToBytes());
    }

    /// <summary>A hand-built format 13 file: tag list instead of flags, int ids for instances and states, 8-byte trailer entries.</summary>
    [Fact]
    public void Legacy_format_13_is_read()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void S(string s) { w.Write(Encoding.UTF8.GetByteCount(s)); w.Write(Encoding.UTF8.GetBytes(s)); }
        w.Write(13); w.Write(40); w.Write(1);                   // type, next id, record count
        w.Write(0); w.Write((int)FcsRecordType.INSTANCE_COLLECTION); w.Write(5); S("0"); S("0-buildinglist");
        w.Write(1); S("is complete"); w.Write(true);           // tags
        w.Write(1); S("is complete"); w.Write((byte)1);         // bools
        w.Write(1); S("height"); w.Write(2.5f);                 // floats
        w.Write(0);                                             // ints
        w.Write(0); w.Write(0);                                 // vec3, vec4
        w.Write(1); S("name"); S("hut");                        // strings
        w.Write(0);                                             // filenames
        w.Write(1); S("refs"); w.Write(1); S("9-gamedata.base"); w.Write(1); w.Write(2); w.Write(3);
        w.Write(1);                                             // instances
        w.Write(17); S("1113-gamedata.base"); w.Write(1f); w.Write(2f); w.Write(3f);
        w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f);     // w x y z
        w.Write(1); w.Write(23);                                // one state id
        w.Write(2); w.Write(1); w.Write(0x12345678); w.Write(5); w.Write(-7); // trailer: 2 entries of 2 ints
        var file = LevelFile.Read(ms.ToArray());

        Assert.Equal(13, file.Format);
        Assert.True(file.IsLegacy);
        Assert.Equal(40, file.Data.NextId);
        var r = Assert.Single(file.Data.Records);
        Assert.Equal("0-buildinglist", r.StringId);
        Assert.Equal([new KeyValuePair<string, bool>("is complete", true)], file.LegacyFieldTags![0]);
        Assert.True(r.Bools["is complete"]);
        Assert.Equal("hut", r.Strings["name"]);
        Assert.Equal(new FcsReference("9-gamedata.base", 1, 2, 3), r.References["refs"][0]);
        var i = Assert.Single(r.Instances);
        Assert.Equal(("17", "1113-gamedata.base", new Vector3(1, 2, 3)), (i.Id, i.Target, i.Position));
        Assert.Equal(Quaternion.Identity, i.Rotation);
        Assert.Equal(["23"], i.States);
        Assert.Equal(2, file.TrailerEntryInts);
        Assert.Equal([1, 0x12345678, 5, -7], file.Trailer!);
        Assert.Throws<NotSupportedException>(() => file.ToBytes());
    }

    [Fact]
    public void Zone_file_names_parse()
    {
        Assert.True(ZoneCoordinate.TryParseFileName(@"a\b\zone.31.45.zone", out var z));
        Assert.Equal(new ZoneCoordinate(31, 45), z);
        Assert.Equal("zone.31.45.zone", z.FileName);
        Assert.False(ZoneCoordinate.TryParseFileName("zone.31.zone", out _));
        Assert.False(ZoneCoordinate.TryParseFileName("zone.-1.2.zone", out _));
        Assert.False(ZoneCoordinate.TryParseFileName("leveldata.level", out _));
    }

    // --- Base game -------------------------------------------------------------------------------

    static IEnumerable<string> LevelFiles(GameInstall install) =>
        Directory.EnumerateFiles(install.DataDirectory, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".zone", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".level", StringComparison.OrdinalIgnoreCase));

    [Fact]
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
    public void Base_game_world_placements_are_consistent()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        var world = WorldLevelData.Load(install!);
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
        var db = GameDatabase.Load(LoadOrder.FromInstall(install!));
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

    [Fact]
    public void Base_game_heights_match_the_heightmap()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        var world = WorldLevelData.Load(install!);
        using var map = TerrainHeightmap.Open(install!);
        static double Median(IEnumerable<float> values) { var s = values.Order().ToList(); return s[s.Count / 2]; }

        // Road points lie on the terrain: half of them within about a unit.
        var roads = world.Roads().SelectMany(r => r.Points).Where(p => Math.Abs(p.X) < WorldLayout.HalfWorldSize && Math.Abs(p.Z) < WorldLayout.HalfWorldSize)
            .Select(p => Math.Abs(p.Y - map.HeightAt(p.X, p.Z))).ToList();
        Assert.Equal(20944, roads.Count);
        Assert.InRange(Median(roads), 0, 1.0);

        // A building's "world Y pos" is the terrain height plus the instance's Y, which is an offset above the ground.
        var placed = world.Buildings().Where(b => b.WorldY is not null).ToList();
        var withOffset = placed.Select(b => Math.Abs(b.WorldY!.Value - (map.HeightAt(b.Position.X, b.Position.Z) + b.Position.Y))).ToList();
        var withoutOffset = placed.Select(b => Math.Abs(b.WorldY!.Value - map.HeightAt(b.Position.X, b.Position.Z))).ToList();
        Assert.InRange(Median(withOffset), 0, 0.5);
        Assert.True(withOffset.Count(d => d < 0.05) > 3 * withoutOffset.Count(d => d < 0.05));

        // The scale 9800/65535 beats its neighbours clearly (a wrong scale shifts every height proportionally).
        int Exact(float max) => placed.Count(b => Math.Abs(b.WorldY!.Value - (map.HeightAt(b.Position.X, b.Position.Z) / WorldLayout.MaxHeight * max + b.Position.Y)) < 0.05);
        Assert.True(Exact(9800) > 2 * Exact(9790) && Exact(9800) > 2 * Exact(9810));
    }
}
