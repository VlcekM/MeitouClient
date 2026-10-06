using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;

namespace Meitou.Tests;

public class GameDatabaseTests
{
    const uint New = 0x10, Modified = 0x11, Renamed = 0x13;

    static FcsRecord Record(string id, uint flags, string name = "thing", FcsRecordType type = FcsRecordType.ITEM) =>
        new() { StringId = id, Flags = flags, Name = name, RecordType = type };

    static FcsFile File(params FcsRecord[] records)
    {
        var file = new FcsFile();
        file.Records.AddRange(records);
        return file;
    }

    [Fact]
    public void Later_file_overrides_listed_fields_and_keeps_the_rest()
    {
        var baseRecord = Record("1-a.mod", New);
        baseRecord.Floats["weight"] = 2;
        baseRecord.Ints["value"] = 10;
        var change = Record("1-a.mod", Modified, name: "ignored");
        change.Floats["weight"] = 5;

        var db = new GameDatabase();
        db.Apply(File(baseRecord), "a.mod");
        db.Apply(File(change), "b.mod");

        var r = db.Find("1-a.mod")!;
        Assert.Equal(5f, r.GetFloat("weight"));
        Assert.Equal(10, r.GetInt("value"));
        Assert.Equal("thing", r.Name);
        Assert.Equal("a.mod", r.DefinedBy);
        Assert.Equal(["b.mod"], r.ModifiedBy);
        Assert.Empty(db.Issues);
    }

    [Fact]
    public void Renaming_record_changes_the_name()
    {
        var db = new GameDatabase();
        db.Apply(File(Record("1-a.mod", New)), "a.mod");
        db.Apply(File(Record("1-a.mod", Renamed, name: "better thing")), "b.mod");
        Assert.Equal("better thing", db.Find("1-a.mod")!.Name);
    }

    [Fact]
    public void Rename_bit_alone_renames()
    {
        var db = new GameDatabase();
        db.Apply(File(Record("1-a.mod", New)), "a.mod");
        db.Apply(File(Record("1-a.mod", 0x12, name: "better thing")), "b.mod");
        Assert.Equal("better thing", db.Find("1-a.mod")!.Name);
    }

    [Fact]
    public void Removed_flag_deletes_record_and_a_later_file_starts_it_fresh()
    {
        var keep = Record("2-a.mod", New);
        keep.Bools[FcsRecord.RemovedKey] = false;
        var baseRecord = Record("1-a.mod", New);
        baseRecord.Ints["value"] = 10;
        var remove = Record("1-a.mod", Modified);
        remove.Bools[FcsRecord.RemovedKey] = true;

        var db = new GameDatabase();
        db.Apply(File(baseRecord, keep), "a.mod");
        db.Apply(File(remove), "b.mod");
        Assert.Null(db.Find("1-a.mod"));
        Assert.False(db.Find("2-a.mod")!.Bools[FcsRecord.RemovedKey]);

        db.Apply(File(Record("1-a.mod", Modified)), "c.mod");
        var fresh = db.Find("1-a.mod")!;
        Assert.Equal("c.mod", fresh.DefinedBy);
        Assert.Empty(fresh.Ints);
    }

    [Fact]
    public void Modified_record_without_base_is_created()
    {
        var change = Record("9-x.mod", Modified, name: "orphan");
        change.Ints["value"] = 3;
        var db = new GameDatabase();
        db.Apply(File(change), "b.mod");

        var r = db.Find("9-x.mod")!;
        Assert.Equal("orphan", r.Name);
        Assert.Equal(3, r.GetInt("value"));
        Assert.Equal(GameDataIssueKind.ModifiedRecordNotFound, Assert.Single(db.Issues).Kind);
    }

    [Fact]
    public void References_are_added_overwritten_and_removed_per_target()
    {
        var baseRecord = Record("1-a.mod", New);
        baseRecord.References["items"] = [new("10-a.mod", 1, 0, 0), new("11-a.mod", 2, 0, 0), new("13-a.mod", 4, 0, 0)];
        var change = Record("1-a.mod", Modified);
        change.References["items"] =
        [
            new("10-a.mod", 7, 0, 0), FcsReference.Removed("11-a.mod"), new("12-a.mod", 3, 0, 0),
            new("13-a.mod", 5, 0, int.MaxValue), // only all three values at int.MaxValue remove
        ];

        var db = new GameDatabase();
        db.Apply(File(baseRecord), "a.mod");
        db.Apply(File(change), "b.mod");

        Assert.Equal(
            [
                new GameReference("10-a.mod", new(7, 0, 0)), new GameReference("13-a.mod", new(5, 0, int.MaxValue)),
                new GameReference("12-a.mod", new(3, 0, 0)),
            ],
            db.Find("1-a.mod")!.GetReferences("items"));
    }

    [Fact]
    public void Instances_merge_by_id_and_empty_target_clears()
    {
        var baseRecord = Record("1-a.mod", New);
        baseRecord.Instances.Add(new FcsInstance { Id = "door", Target = "5-a.mod", Position = new(1, 0, 0), States = { "open" } });
        baseRecord.Instances.Add(new FcsInstance { Id = "lamp", Target = "6-a.mod", States = { "lit" } });
        var change = Record("1-a.mod", Modified);
        change.Instances.Add(new FcsInstance { Id = "door", Target = "5-a.mod", Position = new(2, 0, 0), States = { "open" } });
        change.Instances.Add(new FcsInstance { Id = "lamp", Target = "" });

        var db = new GameDatabase();
        db.Apply(File(baseRecord), "a.mod");
        db.Apply(File(change), "b.mod");

        var instances = db.Find("1-a.mod")!.Instances;
        Assert.Equal(2f, instances["door"].Position.X);
        Assert.Equal(["open", "open"], instances["door"].States);
        Assert.True(instances["lamp"].IsCleared);
        Assert.Empty(instances["lamp"].States);
    }

    [Fact]
    public void Each_value_kind_is_its_own_table()
    {
        var baseRecord = Record("1-a.mod", New);
        baseRecord.Filenames["mesh"] = "a.mesh";
        var change = Record("1-a.mod", Modified);
        change.Strings["mesh"] = "b.mesh";

        var db = new GameDatabase();
        db.Apply(File(baseRecord), "a.mod");
        db.Apply(File(change), "b.mod");

        var r = db.Find("1-a.mod")!;
        Assert.Equal("a.mesh", r.GetPath("mesh"));
        Assert.Equal("b.mesh", r.GetString("mesh"));
    }

    [Fact]
    public void Problems_are_reported_without_stopping()
    {
        var db = new GameDatabase();
        db.Apply(File(Record("1-a.mod", New)), "a.mod");
        db.Apply(File(Record("9-x.mod", Modified), Record("1-a.mod", New), Record("1-a.mod", Modified, type: FcsRecordType.WEAPON)), "b.mod");

        Assert.Equal(
            [GameDataIssueKind.ModifiedRecordNotFound, GameDataIssueKind.RecordAlreadyDefined, GameDataIssueKind.ChangedRecordType],
            db.Issues.Select(i => i.Kind));
        Assert.Equal(FcsRecordType.ITEM, db.Find("1-a.mod")!.Type);
    }

    [Fact]
    public void Mods_cfg_follows_the_game_rules()
    {
        Assert.Equal(
            ["A", "B.x", "A"],
            LoadOrder.ParseModsCfg(["A.mod", "", " B.mod ", "B.x.mod", "#C.mod", "/C.mod", ";C.mod", "C.MOD", "C", "rebirth.mod", "Dialogue.mod", "A.mod"]));
    }

    /// <summary>
    /// "Iron Rock" is defined in gamedata.base and changed by rebirth.mod. Expectations are derived from the
    /// two raw records, so the test needs no copied game data.
    /// </summary>
    [Fact]
    [Slow]
    public void Iron_rock_merges_rebirth_changes_over_gamedata_base()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        const string id = "14520-rebirth.mod";
        var order = LoadOrder.BaseGame(install!);
        var db = GameDatabase.Load(order);
        var baseRecord = FcsReader.ReadFile(Path.Combine(install!.DataDirectory, "gamedata.base")).Records.Single(r => r.StringId == id);
        var change = FcsReader.ReadFile(Path.Combine(install.DataDirectory, "rebirth.mod")).Records.Single(r => r.StringId == id);
        Assert.True(change.IsModified);

        var merged = db.Find(id)!;
        Assert.Equal(baseRecord.Name, merged.Name);
        Assert.Equal("gamedata.base", merged.DefinedBy);
        Assert.Contains("rebirth.mod", merged.ModifiedBy);

        foreach (var (key, value) in change.Floats) Assert.Equal(value, merged.GetFloat(key));
        foreach (var (key, value) in change.Ints) Assert.Equal(value, merged.GetInt(key));
        foreach (var (key, value) in change.Bools) Assert.Equal(value, merged.GetBool(key));
        foreach (var (key, value) in baseRecord.Floats.Where(kv => !change.Floats.ContainsKey(kv.Key)))
            Assert.Equal(value, merged.GetFloat(key));
        foreach (var (list, refs) in baseRecord.References)
            Assert.Equal(refs.Select(r => r.TargetStringId), merged.GetReferences(list).Select(r => r.TargetStringId));
    }

    [Fact]
    [Slow]
    public void Base_game_loads_with_every_record_accounted_for()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        var order = LoadOrder.BaseGame(install!);
        var db = GameDatabase.Load(order);

        var files = order.Entries.Select(e => FcsReader.ReadFile(e.Path)).ToList();
        var defined = files.SelectMany(f => f.Records).Where(r => !r.IsModified).Select(r => r.StringId).ToHashSet();
        var everRemoved = files.SelectMany(f => f.Records).Where(r => r.IsRemoved).Select(r => r.StringId).ToHashSet();

        // Every surviving record was defined somewhere; every defined record either survives or was removed.
        Assert.All(db.Records.Keys, id => Assert.Contains(id, defined));
        Assert.All(defined.Where(id => !db.Records.ContainsKey(id)), id => Assert.Contains(id, everRemoved));

        // The base game is consistent under these rules: nothing to report, and no reference dangles.
        Assert.Empty(db.Issues);
        Assert.All(db.Records.Values, r =>
        {
            foreach (var list in r.ReferenceLists)
                foreach (var reference in r.GetReferences(list))
                    Assert.True(db.Find(reference.TargetStringId) is not null, $"{r.StringId} '{list}' -> {reference.TargetStringId}");
        });
    }
}
