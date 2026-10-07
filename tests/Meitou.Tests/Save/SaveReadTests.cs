using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using static Meitou.Tests.Save.SaveSamples;

namespace Meitou.Tests.Save;

/// <summary>Reading saves: the file layer on synthetic data, then the user's saves in place (docs/formats/save.md).</summary>
public class SaveReadTests
{
    [Fact]
    public void Slot_lists_after_the_records_round_trip()
    {
        var file = new SaveFile();
        file.Records.Add(new FcsRecord { Type = (int)FcsRecordType.CAMERA, StringId = "1--INGAME" });
        file.SlotLists.Add([1, 2, 5]);
        file.SlotLists.Add([]);
        file.SlotLists.Add([7]);
        var bytes = file.ToBytes();
        var back = SaveFile.Read(bytes);
        Assert.Equal(3, back.SlotLists.Count);
        Assert.Equal([1, 2, 5], back.SlotLists[0]);
        Assert.Empty(back.SlotLists[1]);
        Assert.Equal(bytes, back.ToBytes());
    }

    [Fact]
    public void Strings_that_are_not_utf8_survive_a_round_trip()
    {
        // The ids of TERRAIN_DECALS instances are raw 4-byte integers in a string slot.
        var file = new SaveFile();
        var decals = new FcsRecord { Type = (int)FcsRecordType.TERRAIN_DECALS, StringId = "1--INGAME" };
        decals.Instances.Add(new FcsInstance { Id = LosslessUtf8.GetString([0x81, 0, 0, 0]), Target = "-" });
        decals.Instances.Add(new FcsInstance { Id = LosslessUtf8.GetString([0xC3, 0xA9, 0xFF, 0xFE]), Target = "é" });
        file.Records.Add(decals);
        var bytes = file.ToBytes();
        var back = SaveFile.Read(bytes);
        Assert.Equal(bytes, back.ToBytes());
        Assert.Equal("é\udcff\udcfe", back.Records[0].Instances[1].Id);
    }

    [Fact]
    public void Hands_read_and_write_by_prefix_and_a_missing_I_is_the_null_handle()
    {
        var r = new FcsRecord();
        Assert.True(Hand.Read(r, "handle").IsNull);
        new Hand(1, 480, -542769920, 3, 769409152).Write(r, "handle");
        Assert.Equal(["handleTYPE", "handleC", "handleCS", "handleI", "handleS"], r.Ints.Keys);
        var h = Hand.Read(r, "handle");
        Assert.Equal(new Hand(1, 480, -542769920, 3, 769409152), h);
        Assert.Equal(FcsRecordType.CHARACTER, h.RecordType);
        Assert.Equal(["handle"], Hand.Prefixes(r));
        // A null handle may keep stale CS and S values; only the type counts.
        Assert.True(new Hand(11, 0, 123, 0, 456).IsNull);
        Assert.Equal(Hand.Null, Hand.Read(new FcsRecord(), "x"));
    }

    [Fact]
    public void Camera_lists_are_written_in_the_games_order_and_spelling()
    {
        var camera = new SaveCamera(new FcsRecord());
        camera.SetZones([new ZoneCoordinate(20, 3), new ZoneCoordinate(19, 28), new ZoneCoordinate(19, 5), new ZoneCoordinate(19, 5)]);
        var list = camera.Record.References["zones"];
        Assert.Equal([new FcsReference("0", 19, 5, 1), new FcsReference("1", 19, 28, 1), new FcsReference("2", 20, 3, 1)], list);
        camera.SetZones(Enumerable.Range(0, 12).Select(i => new ZoneCoordinate(i, 0)));
        Assert.Equal(["0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "a", "b"], camera.Record.References["zones"].Select(r => r.TargetStringId));
        camera.SetMods(["gamedata.base", "Newwworld.mod", "rebirth.mod"]);
        Assert.Equal(["base", "Newwworld", "rebirth"], camera.Mods);
        Assert.Equal(new FcsReference("base", -1, 0, 0), camera.Record.References["mods"][0]);
    }

    [Fact]
    public void Folders_are_layered_and_a_later_layer_hides_an_earlier_one()
    {
        string root = Temp("layers");
        string a = Path.Combine(root, "a"), b = Path.Combine(root, "b");
        Directory.CreateDirectory(Path.Combine(a, "platoon"));
        Directory.CreateDirectory(Path.Combine(b, "platoon"));
        File.WriteAllText(Path.Combine(a, "quick.save"), "a");
        File.WriteAllText(Path.Combine(a, "platoon", "X_0.platoon"), "a");
        File.WriteAllText(Path.Combine(b, "platoon", "X_0.platoon"), "b");
        Directory.CreateDirectory(Path.Combine(b, "zone"));
        File.WriteAllText(Path.Combine(b, "zone", "zone.19.28.zone"), "b");
        try
        {
            var folder = new SaveFolder([a, b]);
            Assert.Equal("a", File.ReadAllText(folder.Resolve("quick.save")!));
            Assert.Equal("b", File.ReadAllText(folder.Resolve("platoon\\X_0.platoon")!));
            Assert.Equal([new ZoneCoordinate(19, 28)], folder.ZoneFiles);
            Assert.Equal(b, folder.TopLayer);
            Assert.False(folder.Exists("zone/zone.1.1.zone"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void Every_file_of_a_save_reads_and_writes_back_to_the_same_bytes(string folder)
    {
        SkipWithoutSaves(folder);
        int files = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = File.ReadAllBytes(path);
            var file = SaveFile.Read(bytes);
            Assert.True(bytes.AsSpan().SequenceEqual(file.ToBytes()), $"{Path.GetRelativePath(folder, path)} differs after a round trip");
            files++;
        }
        Assert.True(files > 2);
    }

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void A_save_loads_into_its_typed_views(string folder)
    {
        SkipWithoutSaves(folder);
        var game = Load(folder);
        var camera = game.Camera;
        Assert.Equal(SaveGame.GameVersion, camera.Version);
        Assert.InRange(camera.Hour, 0, 23);
        Assert.InRange(camera.Minute, 0, 59);
        Assert.True(camera.Day >= 1);
        Assert.Equal("Nameless", camera.PlayerFactionName);
        // The data files in load order: the base game's four.
        Assert.Equal(["base", "Newwworld", "Dialogue", "rebirth"], camera.Mods);
        Assert.Equal(103, game.Factions.Count);
        Assert.All(game.Factions, f => Assert.NotNull(f.War));
        Assert.Equal(103, game.Factions.Select(f => f.War).Distinct().Count());   // one war state each, paired by the faction id
        Assert.Equal("204-gamedata.base", game.PlayerFaction!.Id);
        Assert.Equal("Nameless", game.PlayerFaction.Name);
        Assert.NotEmpty(game.Platoons);
        Assert.NotEmpty(game.Towns);
        Assert.NotNull(game.TownInstanceList);
        Assert.NotNull(game.Biomes);
        Assert.NotNull(game.Research);
        Assert.Equal(game.Platoons.Where(p => p.IsLoaded).Sum(p => p.Characters.Count), game.Characters.Count());
        Assert.NotEmpty(game.Zones);
    }

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void The_camera_zone_list_is_in_hex_grid_order_and_names_the_zone_files(string folder)
    {
        SkipWithoutSaves(folder);
        var game = Load(folder);
        var list = game.Camera.Record.References["zones"];
        Assert.All(list.Select((r, i) => (r, i)), t =>
        {
            Assert.Equal(t.i.ToString("x"), t.r.TargetStringId);
            Assert.Equal(1, t.r.Value2);
        });
        var zones = game.Camera.Zones;
        Assert.Equal(zones.OrderBy(z => z.X).ThenBy(z => z.Y), zones);
        // Every listed zone has a file; the only file the list does not name is the one the game itself warns about ("Extra zone file").
        Assert.DoesNotContain(game.Problems, p => p.StartsWith("Zone file missing"));
        Assert.All(game.Problems.Where(p => p.StartsWith("Extra zone file")), p => Assert.Equal("Extra zone file: zone.41.13.zone", p));
    }

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void Platoons_point_at_their_files_and_agree_with_them(string folder)
    {
        SkipWithoutSaves(folder);
        var game = Load(folder);
        int unloaded = 0;
        foreach (var p in game.Platoons)
        {
            Assert.Equal(p.Record.StringId, p.Name);
            Assert.Equal(34, p.Handle.Type);
            Assert.Equal(0, p.Handle.I);
            if (p.FileName is null)
            {
                // A platoon whose characters are not in memory (its content file is just "platoon/") has no file.
                Assert.Equal("platoon/", p.ContentFile);
                Assert.False(p.IsLoaded);
                unloaded++;
                continue;
            }
            Assert.Equal(SaveFolder.PlatoonName(p.Name), p.FileName);
            if (!p.IsLoaded) { Assert.Fail($"{p.FileName} is not in the folder"); }
            // Not every file is this game's own: a quicksave folder holds a header-carrying type 17 platoon file (docs/formats/save.md).
            if (p.File!.Data.FileType != FcsFileType.V15) continue;
            // The file's own count is exact. The PLATOON record's copy can be off by a character or two in a save made after play (55 of 207 platoons
            // of one autosave, by -7 to +3); the sample made early in a game matched everywhere.
            Assert.Equal(p.Characters.Count, p.Collection!.Ints["char count"]);
            Assert.Equal(p.Characters.Count, p.Collection.Instances.Count);
            Assert.InRange(p.CharCount - p.Characters.Count, -7, 3);
            // Characters share the platoon's container and serial and take distinct slots from 1 (compact 1..n in all but two platoons of the three saves).
            var h = p.Handle;
            var slots = p.Characters.Select(c => c.Handle.I).ToList();
            Assert.Equal(slots.Count, slots.Distinct().Count());
            Assert.All(slots, i => Assert.InRange(i, 1, p.Characters.Count + 4));
            foreach (var c in p.Characters)
            {
                Assert.Equal(1, c.Handle.Type);
                Assert.Equal(h.C, c.Handle.C);
                Assert.Equal(h.CS, c.Handle.CS);
                Assert.NotNull(c.GameState);
                Assert.NotNull(c.Ai);
                Assert.NotNull(c.Inventory);
                Assert.NotNull(c.Medical);
                Assert.NotNull(c.Stats);
                Assert.NotNull(c.Appearance);
                Assert.Equal(6, c.Instance.States.Count);
            }
        }
        Assert.True(game.Platoons.Count - unloaded > 0);
    }

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void Every_handle_has_five_parts_and_a_known_type(string folder)
    {
        SkipWithoutSaves(folder);
        var game = Load(folder);
        var types = new HashSet<int>();
        int handles = 0;
        foreach (var (name, file) in game.AllFiles())
            foreach (var r in file.Records)
                foreach (var prefix in Hand.Prefixes(r))
                {
                    Assert.True(Hand.IsComplete(r, prefix), $"{name}: {r.StringId} {prefix} is missing a part");
                    types.Add(Hand.Read(r, prefix).Type);
                    handles++;
                }
        Assert.True(handles > 1000);
        Assert.Subset(new HashSet<int> { 0, 1, 11, 13, 34, 85 }, types);
    }

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void Slot_lists_of_quick_save_follow_from_the_platoons_and_towns(string folder)
    {
        SkipWithoutSaves(folder);
        var game = Load(folder);
        var lists = game.Quick.SlotLists;
        Assert.Equal(4, lists.Count);
        Assert.All(lists, l => Assert.Equal(l.Order(), l));
        // List 0: the container of every platoon.
        Assert.Equal(game.Platoons.Select(p => p.Handle.C).Distinct().Order(), lists[0]);
        // List 1: the container of every town and nest, and of every town handle that is referenced.
        var towns = new SortedSet<int>(game.Towns.Select(t => t.Handle.C));
        foreach (var (_, file) in game.AllFiles())
            foreach (var r in file.Records)
                foreach (var prefix in Hand.Prefixes(r))
                    if (Hand.Read(r, prefix) is { IsNull: false, Type: 13 or 85 } h) towns.Add(h.C);
        Assert.Equal(towns, lists[1]);
        // Lists 2 and 3 (Unknown meaning): both hold every zone container 1..4096 and every platoon container.
        foreach (var l in new[] { lists[2], lists[3] })
        {
            Assert.Equal(Enumerable.Range(1, 4096), l.Where(c => c <= 4096));
            Assert.All(lists[0], c => Assert.Contains(c, l));
        }
    }

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void Zone_files_hold_their_buildings_in_their_own_container(string folder)
    {
        SkipWithoutSaves(folder);
        var game = Load(folder);
        foreach (var zone in game.Zones.Values)
        {
            Assert.Single(zone.File.SlotLists);
            var slots = zone.File.SlotLists[0].ToHashSet();
            foreach (var s in zone.BuildingStates)
            {
                var h = Hand.Read(s, "handle");
                Assert.Equal(zone.ContainerId, h.C);
                Assert.Equal(11111, h.CS);
                Assert.Contains(h.I, slots);
            }
            // nextId is the highest id in the file, or one more (docs/formats/save.md).
            Assert.InRange(zone.File.Data.NextId - zone.File.Records.Max(r => r.Id), 0, 1);
        }
    }

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void Handles_resolve_against_the_save(string folder)
    {
        SkipWithoutSaves(folder);
        var game = Load(folder);
        var handles = game.BuildHandles();
        foreach (var p in game.Platoons) Assert.Same(p, handles.Platoon(p.Handle));
        foreach (var c in game.Characters) Assert.Same(c, handles.Character(c.Handle));
        foreach (var t in game.Towns) Assert.Same(t, handles.Town(t.Handle));
        // The selected character of the CAMERA names a player character.
        var selected = game.Camera.SelectedCharacter;
        if (!selected.IsNull)
        {
            var c = handles.Character(selected);
            Assert.NotNull(c);
            Assert.Equal("204-gamedata.base", c.OwnerFactionId);
        }
        // Buildings: the handles of the town buildings of the zones we hold resolve.
        int resolved = 0;
        foreach (var zone in game.Zones.Values)
            foreach (var s in zone.BuildingStates)
                if (handles.Building(Hand.Read(s, "handle")) == s) resolved++;
        Assert.Equal(game.Zones.Values.Sum(z => z.BuildingStates.Count()), resolved);
    }

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void Zones_overlay_the_base_placements_by_instance_id(string folder)
    {
        SkipWithoutSaves(folder);
        var install = Meitou.Content.GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var game = Load(folder);
        var level = WorldLevelData.Load(install!);
        int checkedZones = 0;
        foreach (var zone in game.Zones.Values)
        {
            level.Zones.TryGetValue(zone.Coordinate, out var baseDb);
            var merged = zone.Overlay(baseDb);
            if (zone.BuildingList is null) { Assert.True(merged.Count == (baseDb?.Find(WorldLevelData.BuildingListId)?.Instances.Count ?? 0)); continue; }
            var saved = zone.BuildingList.Instances.Select(i => i.Id).ToHashSet();
            // Everything the save lists is in the result with the saved target; every base placement the save lacks is kept.
            Assert.All(zone.BuildingList.Instances, i => Assert.Equal(i.Target, merged.Single(m => m.InstanceId == i.Id).Target));
            if (baseDb?.Find(WorldLevelData.BuildingListId) is { } list)
            {
                Assert.All(list.Instances.Keys, id => Assert.Contains(merged, m => m.InstanceId == id));
                Assert.All(merged.Where(m => m.Overrides), m => Assert.Contains(m.InstanceId, saved));
            }
            checkedZones++;
        }
        Assert.True(checkedZones > 0);
    }
}
