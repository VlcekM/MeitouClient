using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;

namespace Meitou.Tests;

public class GameDatabaseTests
{
    const uint New = 0x10, Modified = 0x11, Renamed = 0x13;

    static FcsRecord Record(string id, uint flags, string name = "thing", FcsRecordType type = FcsRecordType.Item) =>
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
    public void Mods_cfg_follows_the_game_rules()
    {
        string[] lines =
        ["A.mod", "", " B.mod ", "B.x.mod", "#C.mod", "/C.mod", ";C.mod", "C.MOD", "C", "rebirth.mod", "Dialogue.mod", "A.mod",];
        Assert.Equal(["A", "B.x", "A"], LoadOrder.ParseModsCfg(lines));
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
        var defined = files.SelectMany(f => f.Records)
            .Where(r => !r.IsModified)
            .Select(r => r.StringId)
            .ToHashSet();
        var everRemoved = files.SelectMany(f => f.Records)
            .Where(r => r.IsRemoved)
            .Select(r => r.StringId)
            .ToHashSet();
        // The gear blueprints' research is made at load, one <id>.TECH.1 per ARMOUR, CONTAINER and CROSSBOW (GameDatabase.AddGearBlueprints).
        defined.UnionWith(db.Records.Values
            .Where(r => r.Type is FcsRecordType.Armour or FcsRecordType.Container or FcsRecordType.Crossbow)
            .Select(r => r.StringId + ".TECH.1"));

        // Every surviving record was defined somewhere; every defined record either survives or was removed.
        Assert.All(db.Records.Keys, id => Assert.Contains(id, defined));
        Assert.All(defined.Where(id => !db.Records.ContainsKey(id)), id => Assert.Contains(id, everRemoved));

        // The base game is consistent under these rules: nothing to report, and no reference dangles.
        Assert.Empty(db.Issues);
        Assert.All(db.Records.Values, r =>
        {
            foreach (var list in r.ReferenceLists)
            {
                foreach (var reference in r.GetReferences(list))
                {
                    Assert.True(db.Find(reference.TargetStringId) is not null, $"{r.StringId} '{list}' -> {reference.TargetStringId}");
                }
            }
        });
    }
}
