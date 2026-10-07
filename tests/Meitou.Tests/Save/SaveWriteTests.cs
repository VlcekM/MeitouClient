using System.Numerics;
using System.Text.RegularExpressions;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using static Meitou.Tests.Save.SaveSamples;

namespace Meitou.Tests.Save;

/// <summary>Writing saves: new records, derived data, and the temp-folder commit (docs/formats/save.md).</summary>
public partial class SaveWriteTests
{
    static SaveGame Small(out SavePlatoon platoon)
    {
        var b = new SaveBuilder(7);
        b.Game.Camera.Day = 3;
        b.Game.Camera.Hour = 9;
        b.Game.Camera.Minute = 41;
        b.AddFaction("204-gamedata.base", "Nameless", true, 1000);
        var other = b.AddFaction("10-x", "Other", false, 1000, [new SaveRelation("10-x", 100, 0, 0), new SaveRelation("204-gamedata.base", -20, 0, 0)], platoonCounter: 1);
        Assert.Equal(-20, other.Relations[1].Relation);
        b.AddTown("Town state A", "9-x-INGAME", "10-x", false, new ZoneCoordinate(20, 30));
        b.AddEmptyWorldState();
        platoon = b.AddPlatoon("Nameless_0", "Nameless", "204-gamedata.base", "5-x", new Vector3(100, 20, 300), money: 12);
        b.AddCharacter(platoon, "7-x", "Beep", "204-gamedata.base", new Vector3(100, 20, 300), Quaternion.Identity, isLeader: true);
        b.AddCharacter(platoon, "8-x", "Boop", "204-gamedata.base", new Vector3(103, 20, 300), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1));
        b.Game.Camera.SetMods(["gamedata.base", "x.mod"]);
        b.Game.UpdateDerived();
        return b.Game;
    }

    [Fact]
    public void A_built_save_has_the_games_ids_handles_and_derived_lists()
    {
        var game = Small(out var platoon);
        // Ids: <n>-quick.save-INGAME in quick.save, <n>--INGAME in a platoon file, numbers unique in the file, nextId the highest.
        Assert.All(game.Quick.Records, r => Assert.Matches(@"^\d+-quick\.save-INGAME$|^Nameless_0$", r.StringId));
        Assert.Equal(game.Quick.Records.Count, game.Quick.Records.Select(r => r.StringId).Distinct().Count());
        Assert.Equal(game.Quick.Records.Max(r => r.Id), game.Quick.Data.NextId);
        Assert.All(platoon.File!.Records, r => Assert.Matches(@"^\d+--INGAME$", r.StringId));
        Assert.Equal(platoon.File.Records.Select(r => r.Id).Distinct().Count(), platoon.File.Records.Count);

        // Handles: the platoon's, then its characters' in the platoon's container with slots 1, 2.
        var h = platoon.Handle;
        Assert.Equal((34, 0, 0), (h.Type, h.I, h.S));
        Assert.Equal([1, 2], platoon.Characters.Select(c => c.Handle.I));
        Assert.All(platoon.Characters, c => Assert.Equal((1, h.C, h.CS), (c.Handle.Type, c.Handle.C, c.Handle.CS)));
        Assert.NotEqual(platoon.Characters[0].Handle.S, platoon.Characters[1].Handle.S);

        // Derived: char counts, slot lists, byte sizes (child counts only where the game keeps them).
        Assert.Equal(2, platoon.CharCount);
        Assert.Equal(2, platoon.Collection!.Ints["char count"]);
        Assert.Equal(4, game.Quick.SlotLists.Count);
        Assert.Equal([h.C], game.Quick.SlotLists[0]);
        Assert.Equal(game.Towns.Select(t => t.Handle.C), game.Quick.SlotLists[1]);
        Assert.Equal(Enumerable.Range(1, 4096), game.Quick.SlotLists[2]);
        Assert.Equal(0u, platoon.Collection.ByteSize);
        Assert.Equal(2u, game.FactionCollections.Sum(c => c.ByteSize));
        Assert.Empty(game.Camera.Zones);
        Assert.Equal(["base", "x"], game.Camera.Mods);
    }

    [Fact]
    public void A_built_save_writes_and_reads_back_the_same()
    {
        var game = Small(out _);
        string folder = Path.Combine(Temp("built"), "mysave");
        try
        {
            game.Write(folder);
            var back = SaveGame.Load(folder);
            Assert.Equal(3, back.Camera.Day);
            Assert.Equal((9, 41), (back.Camera.Hour, back.Camera.Minute));
            Assert.Equal(2, back.Factions.Count);
            Assert.NotNull(back.Factions[1].War);
            Assert.Equal(["Nameless_0"], back.Platoons.Select(p => p.Name));
            var platoon = back.Platoons[0];
            Assert.True(platoon.IsLoaded);
            Assert.Equal(["Beep", "Boop"], platoon.Characters.Select(c => c.Name));
            Assert.Equal(new Vector3(103, 20, 300), platoon.Characters[1].Position);
            Assert.Equal(game.Quick.ToBytes(), back.Quick.ToBytes());
            Assert.Equal(game.Platoons[0].File!.ToBytes(), platoon.File!.ToBytes());
            Assert.Empty(back.Problems);
            // The working folder is gone after the commit.
            Assert.DoesNotContain(Directory.GetDirectories(Path.GetDirectoryName(folder)!), d => Path.GetFileName(d).StartsWith("_current"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    [Fact]
    public void Writing_over_a_save_replaces_the_folder_as_a_whole()
    {
        var game = Small(out var platoon);
        string root = Temp("replace");
        string folder = Path.Combine(root, "slot");
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "platoon"));
            File.WriteAllText(Path.Combine(folder, "platoon", "Old_0.platoon"), "x");
            File.WriteAllText(Path.Combine(folder, "stale.txt"), "x");
            game.Write(folder);
            Assert.False(File.Exists(Path.Combine(folder, "platoon", "Old_0.platoon")));
            Assert.False(File.Exists(Path.Combine(folder, "stale.txt")));
            Assert.True(File.Exists(Path.Combine(folder, "platoon", "Nameless_0.platoon")));
            Assert.False(Directory.Exists(folder + ".old"));
            // A write that fails leaves the old save and no working folder.
            platoon.File!.Data.Records.Add(null!);
            Assert.ThrowsAny<Exception>(() => game.Write(folder));
            Assert.True(File.Exists(Path.Combine(folder, "quick.save")));
            Assert.DoesNotContain(Directory.GetDirectories(root), d => Path.GetFileName(d).StartsWith("_current"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_character_can_be_removed_with_everything_it_holds()
    {
        var game = Small(out var platoon);
        var builder = new SaveBuilder(game, 1);
        var keep = platoon.Characters[0];
        var gone = platoon.Characters[1];
        // Give the second one an item and a backpack with an item in it.
        var file = platoon.File!;
        int next = 100;
        FcsRecord Item(string id) => new() { Type = (int)FcsRecordType.INVENTORY_ITEM_STATE, Id = ++next, StringId = $"{next}--INGAME", Name = "0" };
        var inner = new FcsRecord { Type = (int)FcsRecordType.INVENTORY_STATE, Id = 90, StringId = "90--INGAME", Name = "0" };
        var innerItem = Item("a");
        inner.Instances.Add(new FcsInstance { Id = "1", Target = innerItem.StringId });
        var pack = Item("b");
        pack.Instances.Add(new FcsInstance { Id = "1", Target = inner.StringId });
        var sword = Item("c");
        gone.Inventory!.Instances.Add(new FcsInstance { Id = "1", Target = pack.StringId });
        gone.Inventory.Instances.Add(new FcsInstance { Id = "2", Target = sword.StringId });
        file.Records.AddRange([inner, innerItem, pack, sword]);
        int before = file.Records.Count;

        builder.RemoveCharacter(platoon, gone);
        Assert.Equal([keep], platoon.Characters);
        Assert.Equal(1, platoon.CharCount);
        Assert.Equal(1, platoon.Collection!.Ints["char count"]);
        Assert.Equal(before - 6 - 4, file.Records.Count);   // six states and the four items
        Assert.DoesNotContain(file.Records, r => r.StringId == pack.StringId || r.StringId == innerItem.StringId);
        Assert.Single(platoon.Collection.Instances);
    }

    [Fact]
    public void Camera_zones_follow_the_zone_files_when_derived_data_is_updated()
    {
        var game = Small(out _);
        var zone = new SaveFile();
        zone.Records.Add(new FcsRecord { Type = (int)FcsRecordType.INSTANCE_COLLECTION, Id = 1, StringId = "1-buildinglist", Name = "0" });
        var state = new FcsRecord { Type = (int)FcsRecordType.GAMESTATE_BUILDING, Id = 2, StringId = "2-x-INGAME-S23", Name = "0" };
        new Hand(0, SaveZone.ContainerIdOf(new ZoneCoordinate(20, 30)), 11111, 5, 99).Write(state, "handle");
        zone.Records.Add(state);
        game.Zones[new ZoneCoordinate(20, 30)] = new SaveZone(new ZoneCoordinate(20, 30), zone);
        game.Zones[new ZoneCoordinate(19, 28)] = new SaveZone(new ZoneCoordinate(19, 28), new SaveFile());
        game.UpdateDerived();
        Assert.Equal([new ZoneCoordinate(19, 28), new ZoneCoordinate(20, 30)], game.Camera.Zones);
        Assert.Equal(["0", "1"], game.Camera.Record.References["zones"].Select(r => r.TargetStringId));
        // A new zone file gets the slot list it lacks: the slots of its buildings.
        Assert.Equal([5], game.Zones[new ZoneCoordinate(20, 30)].File.SlotLists[0]);
        Assert.Equal(30 * 64 + 20 + 1, game.Zones[new ZoneCoordinate(20, 30)].ContainerId);
    }

    [Fact]
    public void Child_count_sizes_follow_the_rule_of_the_games_files()
    {
        FcsRecord R(FcsRecordType t, string id, int instances)
        {
            var r = new FcsRecord { Type = (int)t, StringId = id };
            for (int i = 0; i < instances; i++) r.Instances.Add(new FcsInstance());
            return r;
        }
        Assert.Equal(3u, SaveGame.ChildCountSize(R(FcsRecordType.INVENTORY_STATE, "1--INGAME", 3), "platoon/X.platoon"));
        Assert.Equal(2u, SaveGame.ChildCountSize(R(FcsRecordType.INSTANCE_COLLECTION, "1-quick.save-INGAME", 2), "quick.save"));
        Assert.Equal(0u, SaveGame.ChildCountSize(R(FcsRecordType.INSTANCE_COLLECTION, "1-X.platoon-INGAME", 2), "platoon/X.platoon"));
        Assert.Equal(0u, SaveGame.ChildCountSize(R(FcsRecordType.INSTANCE_COLLECTION, "0-buildinglist", 2), "zone/zone.1.1.zone"));
        Assert.Equal(4u, SaveGame.ChildCountSize(R(FcsRecordType.INSTANCE_COLLECTION, "1-itemlist", 4), "zone/zone.1.1.zone"));
        Assert.Equal(0u, SaveGame.ChildCountSize(R(FcsRecordType.BIOMES, "1-quick.save-INGAME", 9), "quick.save"));
        Assert.Equal(0u, SaveGame.ChildCountSize(R(FcsRecordType.INVENTORY_STATE, "1--INGAME", 0), "platoon/X.platoon"));
    }

    [Fact]
    public void The_blank_portrait_atlas_is_a_valid_2048_square_rgba_png()
    {
        var png = SavePortraits.Blank();
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        Assert.Equal(2048, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(2048, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Equal((8, 6), (png[24], png[25]));
        // Walk the chunks: each CRC must check, the IDAT must inflate to the filtered rows.
        int at = 8;
        var idat = new MemoryStream();
        var types = new List<string>();
        while (at < png.Length)
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            types.Add(type);
            if (type == "IDAT") idat.Write(png, at + 8, length);
            at += 12 + length;
        }
        Assert.Equal(["IHDR", "IDAT", "IEND"], types);
        Assert.Equal(png.Length, at);
        idat.Position = 0;
        using var inflated = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
        var sink = new MemoryStream();
        inflated.CopyTo(sink);
        Assert.Equal(2048L * (1 + 2048 * 4), sink.Length);
        Assert.True(png.Length < 100_000);
    }

    [Fact]
    public void A_built_save_carries_the_portrait_atlas_to_its_folder()
    {
        var game = Small(out _);
        string folder = Path.Combine(Temp("png"), "slot");
        try
        {
            game.Write(folder);
            Assert.Equal(SavePortraits.Blank(), File.ReadAllBytes(Path.Combine(folder, "portraits_texture.png")));
            Assert.Equal(SavePortraits.Blank(), SaveGame.Load(folder).Portraits);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    // ------------------------------------------------------------------ against the user's saves

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void A_loaded_save_written_to_a_folder_is_the_same_files(string folder)
    {
        SkipWithoutSaves(folder);
        var game = Load(folder);
        string target = Path.Combine(Temp("copy"), "copy");
        try
        {
            game.Write(target);
            var original = new SaveFolder([folder]);
            var copy = SaveFolder.Open(target);
            Assert.Equal(original.Names, copy.Names);
            foreach (var name in original.Names)
                Assert.True(original.ReadAllBytes(name).AsSpan().SequenceEqual(copy.ReadAllBytes(name)), $"{name} differs");
        }
        finally { Directory.Delete(Path.GetDirectoryName(target)!, recursive: true); }
    }

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void Derived_data_computed_for_a_real_save_is_what_the_game_wrote(string folder)
    {
        SkipWithoutSaves(folder);
        var original = Load(folder);
        var game = original.Clone();
        game.UpdateDerived();
        Assert.Equal(original.Quick.SlotLists[0], game.Quick.SlotLists[0]);
        Assert.Equal(original.Quick.SlotLists[1], game.Quick.SlotLists[1]);
        Assert.Equal(original.Quick.SlotLists[2], game.Quick.SlotLists[2]);
        Assert.Equal(original.Quick.SlotLists[3], game.Quick.SlotLists[3]);
        var was = original.AllFiles().ToDictionary(f => f.Name, f => f.File);
        foreach (var (name, file) in game.AllFiles())
        {
            var old = was[name];
            if (file.Data.FileType != FcsFileType.V15) continue;   // saved by the mod editor, left as it is
            Assert.True(file.Data.NextId - old.Data.NextId is 0 or -1, $"{name}: nextId {file.Data.NextId} was {old.Data.NextId}");
            Assert.Equal(old.Records.Select(r => r.ByteSize), file.Records.Select(r => r.ByteSize));
            if (name.StartsWith("zone/")) Assert.Equal(old.SlotLists.Count, file.SlotLists.Count);
        }
        // The zone list names every file, including the one the original left out.
        Assert.Equal(game.Zones.Keys, game.Camera.Zones);
    }

    [Fact]
    public void Hand_prefixes_of_the_builders_records_are_complete()
    {
        var game = Small(out _);
        foreach (var (_, file) in game.AllFiles().Append(("q", game.Quick)))
            foreach (var r in file.Records)
                foreach (var prefix in Hand.Prefixes(r))
                    Assert.True(Hand.IsComplete(r, prefix), $"{r.StringId} {prefix}");
    }

    /// <summary>Key names with their numbers folded: <c>flesh3</c> and <c>flesh5</c> are one key <c>flesh#</c>.</summary>
    static IEnumerable<string> Keys(FcsRecord r) =>
        r.Bools.Keys.Select(k => "b:" + Fold(k)).Concat(r.Floats.Keys.Select(k => "f:" + Fold(k))).Concat(r.Ints.Keys.Select(k => "i:" + Fold(k)))
            .Concat(r.Vector3s.Keys.Select(k => "v3:" + Fold(k))).Concat(r.Vector4s.Keys.Select(k => "v4:" + Fold(k))).Concat(r.Strings.Keys.Select(k => "s:" + Fold(k)))
            .Concat(r.Filenames.Keys.Select(k => "fn:" + Fold(k))).Concat(r.References.Keys.Select(k => "ref:" + Fold(k)));

    static string Fold(string key) => Digits().Replace(key, "#");

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [Theory, Trait("Category", "Slow")]
    [MemberData(nameof(SaveSamples.Names), MemberType = typeof(SaveSamples))]
    public void New_records_have_the_key_sets_of_the_real_ones(string folder)
    {
        SkipWithoutSaves(folder);
        var real = Load(folder);
        var game = Small(out var platoon);

        void Compare(string what, FcsRecord made, IEnumerable<FcsRecord> reals, params string[] allowedMissing) =>
            Check(what, made, reals, k => allowedMissing.Contains(k));

        void Check(string what, FcsRecord made, IEnumerable<FcsRecord> reals, Func<string, bool> allowed)
        {
            var seen = new HashSet<string>();
            var always = (HashSet<string>?)null;
            foreach (var r in reals)
            {
                var keys = Keys(r).ToHashSet();
                seen.UnionWith(keys);
                always = always is null ? keys : [.. always.Intersect(keys)];
            }
            var ours = Keys(made).ToHashSet();
            Assert.True(ours.IsSubsetOf(seen), $"{what}: keys the game never writes: {string.Join(", ", ours.Except(seen))}");
            var missing = always!.Except(ours).Where(k => !allowed(k)).ToList();
            Assert.True(missing.Count == 0, $"{what}: keys every real record has and ours lacks: {string.Join(", ", missing)}");
        }

        var chars = real.Characters.Where(c => c.GameState!.Ints.ContainsKey("TI day")).ToList();
        var c0 = platoon.Characters[0];
        Compare("GAMESTATE_CHARACTER", c0.GameState!, chars.Select(c => c.GameState!));
        Compare("GAMESTATE_AI", c0.Ai!, chars.Select(c => c.Ai!));
        Compare("INVENTORY_STATE", c0.Inventory!, chars.Select(c => c.Inventory!));
        Compare("PLATOON", platoon.Record, real.Platoons.Select(p => p.Record), "mission employer#", "mission target#", "mission town#");
        Compare("GAMESTATE_TOWN", game.Towns[0].Record, real.Towns.Where(t => !t.IsNest).Select(t => t.Record), "ref:trade goods");
        Compare("GAMESTATE_FACTION", game.Factions[1].Record, real.Factions.Where(f => !f.IsPlayer).Select(f => f.Record));
        Compare("GAMESTATE_FACTION (player)", game.Factions[0].Record, real.Factions.Where(f => f.IsPlayer).Select(f => f.Record));
        Compare("WAR_SAVESTATE", game.Factions[1].War!, real.Factions.Select(f => f.War!));
        // CAMERA: the lists of unique characters, the per-dialogue timers and the selection list belong to a game in progress.
        Check("CAMERA", game.Camera.Record, [real.Camera.Record], k => k.Contains("usedUniques") || k.Contains(".mod") || k.Contains("-gamedata.base") || k.Contains("selected_characters"));
    }
}
