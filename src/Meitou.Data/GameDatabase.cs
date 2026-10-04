using Meitou.Data.Fcs;

namespace Meitou.Data;

/// <summary>
/// All game records after applying the files of a load order, with the merge rules in
/// docs/formats/fcs-mod.md ("Merging"). Those are the editor's rules; the game's own loader is assumed
/// to match until shown otherwise.
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
            if (source.IsModified)
            {
                // The editor can load these as placeholders or skip them; without the base there's nothing to change.
                Issue(GameDataIssueKind.ModifiedRecordNotFound, fileName, source.StringId, $"changes record '{source.Name}', which no earlier file defines");
                return;
            }
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

        foreach (var (k, v) in source.Bools) record.SetField(k, v);
        foreach (var (k, v) in source.Floats) record.SetField(k, v);
        foreach (var (k, v) in source.Ints) record.SetField(k, v);
        foreach (var (k, v) in source.Vector3s) record.SetField(k, v);
        foreach (var (k, v) in source.Vector4s) record.SetField(k, v);
        foreach (var (k, v) in source.Strings)
            if (!record.Fields.TryGetValue(k, out var old) || old is string)
                record.SetField(k, v);
        foreach (var (k, v) in source.Filenames) record.SetField(k, new GamePath(v));

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
                instances.Remove(si.Id);
                continue;
            }
            if (!instances.TryGetValue(si.Id, out var instance))
                instances[si.Id] = instance = new GameInstance(si.Id);
            instance.Target = si.Target;
            instance.Position = si.Position;
            instance.Rotation = si.Rotation;
            foreach (var state in si.States)
                if (!instance.States.Contains(state))
                    instance.States.Add(state);
        }

        // REMOVED is a marker, not data: true deletes the record, and the key itself is never kept.
        if (record.Fields.TryGetValue(FcsRecord.RemovedKey, out var removed))
        {
            record.RemoveField(FcsRecord.RemovedKey);
            if (removed is true)
                records.Remove(record.StringId);
        }
    }

    void Issue(GameDataIssueKind kind, string file, string stringId, string message) =>
        Issues.Add(new GameDataIssue(kind, file, stringId, message));
}

public enum GameDataIssueKind
{
    /// <summary>A file in the load order (e.g. from <c>mods.cfg</c>) doesn't exist.</summary>
    MissingFile,
    /// <summary>A record changes a record no earlier file defines; it is skipped.</summary>
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
