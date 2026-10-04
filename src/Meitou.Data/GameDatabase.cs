using Meitou.Data.Fcs;

namespace Meitou.Data;

/// <summary>
/// All game records after applying the files of a load order, with the merge rules in
/// docs/formats/fcs-mod.md ("Merging"): the game's own loader rules, which differ from the editor's in places.
/// </summary>
public sealed class GameDatabase
{
    readonly Dictionary<string, GameRecord> records = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, GameRecord> Records => records;

    /// <summary>Files applied so far, in order.</summary>
    public List<string> Files { get; } = [];

    /// <summary>Problems found while applying files. None of them stop loading.</summary>
    public List<GameDataIssue> Issues { get; } = [];

    public GameRecord? Find(string stringId) => records.GetValueOrDefault(stringId);

    public IEnumerable<GameRecord> OfType(FcsRecordType type) => records.Values.Where(r => r.Type == type);

    /// <summary>Reads and applies every file of <paramref name="loadOrder"/>.</summary>
    public static GameDatabase Load(LoadOrder loadOrder)
    {
        var db = new GameDatabase();
        db.Issues.AddRange(loadOrder.Issues);
        foreach (var entry in loadOrder.Entries)
            db.Apply(FcsReader.ReadFile(entry.Path), entry.Name);
        return db;
    }

    /// <summary>Applies one file on top of everything loaded so far.</summary>
    public void Apply(FcsFile file, string fileName)
    {
        Files.Add(fileName);
        foreach (var record in file.Records)
            Apply(record, fileName);
    }

    void Apply(FcsRecord source, string fileName)
    {
        var record = records.GetValueOrDefault(source.StringId);
        if (record is null)
        {
            // The game creates the record anyway (FUN_1406c0b50); the editor would skip it.
            if (source.IsModified)
                Issue(GameDataIssueKind.ModifiedRecordNotFound, fileName, source.StringId, $"changes record '{source.Name}', which no earlier file defines; created from the change");
            record = new GameRecord(source.StringId, source.RecordType, source.Name, fileName);
            records.Add(source.StringId, record);
        }
        else
        {
            if (!source.IsModified)
                Issue(GameDataIssueKind.RecordAlreadyDefined, fileName, source.StringId, $"defines record '{source.Name}' again (first defined by {record.DefinedBy}); fields merged");
            if (source.RecordType != record.Type)
                Issue(GameDataIssueKind.ChangedRecordType, fileName, source.StringId, $"type {source.RecordType} differs from {record.Type}; kept {record.Type}");
            if (source.IsRenamed)
                record.Name = source.Name;
            record.ModifiedBy.Add(fileName);
        }

        Merge(record.BoolTable, source.Bools);
        Merge(record.FloatTable, source.Floats);
        Merge(record.IntTable, source.Ints);
        Merge(record.Vector3Table, source.Vector3s);
        Merge(record.Vector4Table, source.Vector4s);
        Merge(record.StringTable, source.Strings);
        Merge(record.FilenameTable, source.Filenames);

        foreach (var (list, refs) in source.References)
        {
            var target = record.ReferenceList(list);
            foreach (var r in refs)
            {
                if (r.IsRemoved) target.Remove(r.TargetStringId);
                else target[r.TargetStringId] = new ReferenceValues(r.Value0, r.Value1, r.Value2);
            }
        }

        foreach (var si in source.Instances)
        {
            var instances = record.MutableInstances;
            if (si.IsRemoved)
            {
                // The game clears the instance but keeps its entry.
                if (instances.TryGetValue(si.Id, out var cleared))
                {
                    cleared.Target = "";
                    cleared.States.Clear();
                }
                continue;
            }
            if (!instances.TryGetValue(si.Id, out var instance))
                instances[si.Id] = instance = new GameInstance(si.Id);
            instance.Target = si.Target;
            instance.Position = si.Position;
            instance.Rotation = si.Rotation;
            instance.States.AddRange(si.States);
        }

        // Checked on the merged record after each change: true takes the record out, so a later file that
        // lists the id starts a fresh record.
        if (record.GetBool(FcsRecord.RemovedKey))
            records.Remove(record.StringId);
    }

    static void Merge<T>(Dictionary<string, T> into, OrderedDictionary<string, T> from)
    {
        foreach (var (k, v) in from) into[k] = v;
    }

    void Issue(GameDataIssueKind kind, string file, string stringId, string message) =>
        Issues.Add(new GameDataIssue(kind, file, stringId, message));
}

public enum GameDataIssueKind
{
    /// <summary>A file in the load order (e.g. from <c>mods.cfg</c>) doesn't exist.</summary>
    MissingFile,
    /// <summary>A record changes a record no earlier file defines; the game creates it from the change.</summary>
    ModifiedRecordNotFound,
    /// <summary>A record defines an id an earlier file already defined; its fields are merged.</summary>
    RecordAlreadyDefined,
    /// <summary>A record has a different type than the record it changes; the original type is kept.</summary>
    ChangedRecordType,
}

public sealed record GameDataIssue(GameDataIssueKind Kind, string File, string? StringId, string Message)
{
    public override string ToString() => StringId is null ? $"{File}: {Message}" : $"{File}: {StringId} {Message}";
}
