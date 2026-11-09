using Meitou.Data.Fcs;

namespace Meitou.Data;

/// <summary>
/// All game records after applying the files of a load order, with the merge rules in docs/formats/fcs-mod.md ("Merging"): the game's own loader
/// rules, which differ from the editor's in places.
/// </summary>
public sealed class GameDatabase
{
    #region Records

    /// <summary>
    /// Every loaded record by string id; a record whose merged <c>REMOVED</c> bool is true is taken out.
    /// </summary>
    readonly Dictionary<string, GameRecord> _records = new(StringComparer.Ordinal);

    /// <summary>
    /// Ids of records a translation file created because no data file defined them (its entries for mods that are not loaded);
    /// <see cref="DropTranslationOnlyRecords"/> removes them after loading.
    /// </summary>
    readonly HashSet<string> _translationOnly = new(StringComparer.Ordinal);

    /// <summary>
    /// Every loaded record by string id.
    /// </summary>
    public IReadOnlyDictionary<string, GameRecord> Records => _records;

    /// <summary>
    /// Changes whenever a file, a translation or a post-pass adds, removes or changes records: what an index built over them checks.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// Files applied so far, in order.
    /// </summary>
    public List<string> Files { get; } = [];

    /// <summary>
    /// The header of each file applied (version, author, description), by file name.
    /// </summary>
    public Dictionary<string, FileHeader> Headers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Problems found while applying files. None of them stop loading.
    /// </summary>
    public List<GameDataIssue> Issues { get; } = [];

    /// <summary>
    /// The record of <paramref name="stringId"/>, or null when none is loaded.
    /// </summary>
    public GameRecord? Find(string stringId) => _records.GetValueOrDefault(stringId);

    /// <summary>
    /// Every record of <paramref name="type"/>, in no defined order (a scan of all records each call).
    /// </summary>
    public IEnumerable<GameRecord> OfType(FcsRecordType type) => _records.Values.Where(r => r.Type == type);

    /// <summary>
    /// Translation files applied, by file name, in order (not in <see cref="Files"/>: they are not data files a save depends on).
    /// </summary>
    public List<string> Translations { get; } = [];

    #endregion

    #region Loading

    /// <summary>
    /// Reads and applies every file of <paramref name="loadOrder"/>: the core files, the language's core translation, the mods, then the translation
    /// of the first mod that has one (docs/formats/localisation.md, "Data translation").
    /// </summary>
    public static GameDatabase Load(LoadOrder loadOrder)
    {
        var db = new GameDatabase();
        db.Issues.AddRange(loadOrder.Issues);
        bool coreTranslated = false;
        foreach (var entry in loadOrder.Entries)
        {
            if (!coreTranslated && !LoadOrder.BaseFiles.Contains(entry.Name))
            {
                db.LoadTranslation(loadOrder.Language.CoreTranslationFile);
                coreTranslated = true;
            }
            db.Apply(FcsReader.ReadFile(entry.Path), entry.Name);
        }
        if (!coreTranslated) db.LoadTranslation(loadOrder.Language.CoreTranslationFile);
        db.LoadTranslation(loadOrder.Language.ModTranslationFile);
        db.DropTranslationOnlyRecords();
        db.AddWallMasters();
        db.AddGearBlueprints();
        return db;
    }

    /// <summary>
    /// Applies a <c>.translation</c> file like any data file (the game loads it with the same loader, <b>Verified</b> <c>Fcs_LoadTranslationFile</c>
    /// → <c>GameDataContainer_Load</c>): its records change the names and text fields of records already loaded. A file that cannot be read is
    /// reported as an issue and skipped, as the game logs an error and goes on.
    /// </summary>
    public void LoadTranslation(string? path)
    {
        if (path is null) return;
        FcsFile file;
        try
        {
            file = FcsReader.ReadFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            Issues.Add(new GameDataIssue(GameDataIssueKind.TranslationNotLoaded, Path.GetFileName(path), null, e.Message));
            return;
        }
        ApplyTranslation(file, Path.GetFileName(path));
    }

    /// <summary>
    /// <see cref="Apply(FcsFile, string)"/> for a translation file. A rename keeps the English name in <see cref="GameRecord.BaseName"/>; the
    /// <c>_original_*</c> strings (the English text the translator saw) are not kept, nothing reads them.
    /// </summary>
    public void ApplyTranslation(FcsFile file, string fileName)
    {
        Version++;
        Translations.Add(fileName);
        foreach (var source in file.Records)
        {
            foreach (var key in source.Strings.Keys.Where(k => k.StartsWith("_original_", StringComparison.Ordinal)).ToList())
                source.Strings.Remove(key);
            bool known = _records.ContainsKey(source.StringId);
            Apply(source, fileName, translation: true);
            if (!known && _records.ContainsKey(source.StringId)) _translationOnly.Add(source.StringId);
        }
    }

    /// <summary>
    /// Drops the placeholders a translation file created when no data file touched them. A translation file lists records of mods that may not be
    /// loaded; the game creates them from the translation's changes, Meitou drops them so a record with only a name and a description never reaches a
    /// list (Meitou choice, docs/formats/localisation.md).
    /// </summary>
    void DropTranslationOnlyRecords()
    {
        Version++;
        foreach (var id in _translationOnly) _records.Remove(id);
        _translationOnly.Clear();
    }

    /// <summary>
    /// The loader's post-pass (docs/formats/fcs-mod.md "Game loader", <b>Verified</b> <c>FUN_1406c34f0</c>): every BUILDING whose <c>link type</c> is
    /// not 3 (SINGLE: towers, gates) sets a <c>wall master</c> reference (values 0, 0, 0) on each record its <c>wall subsections</c> lists, back to
    /// itself; a subsection that is not loaded is skipped. Run once after the files.
    /// </summary>
    public void AddWallMasters()
    {
        Version++;
        foreach (var master in _records.Values)
        {
            if (master.Type != FcsRecordType.Building || master.GetInt("link type") == 3) continue;
            foreach (var sub in master.GetReferences("wall subsections"))
                if (_records.TryGetValue(sub.TargetStringId, out var section))
                    section.ReferenceList("wall master")[master.StringId] = new ReferenceValues(0, 0, 0);
        }
    }

    /// <summary>
    /// The research of gear blueprints (docs/game/buildings-production.md "Blueprints", <b>Verified</b> <c>Research::_setup</c> ->
    /// <c>_createBlueprintsFor</c>): each ARMOUR, CONTAINER and CROSSBOW gets a RESEARCH <c>&lt;id&gt;.TECH.1</c>, a copy of <c>RESEARCH_TEMPLATE</c>
    /// named as the item, <c>blueprint only</c>, <c>money</c> = trunc(ItemValue(item, 40, raw) x <c>blueprint price mult</c>), enabling the item's
    /// craft and showing as <c>BLUEPRINT_ITEM_ARMOUR</c> (armour) or <c>BLUEPRINT_ITEM_GEAR</c>. An id that already exists is kept (the original's
    /// handling of that is <b>Unknown</b>). Run once after the files and translations.
    /// </summary>
    public void AddGearBlueprints()
    {
        Version++;
        var template = Find("RESEARCH_TEMPLATE") is { Type: FcsRecordType.Research } t ? t : null;
        var prices = Gameplay.Economy.PriceConstants.FromDatabase(this);
        foreach (var item in _records.Values.Where(r => r.Type is FcsRecordType.Armour or FcsRecordType.Container or FcsRecordType.Crossbow).ToList())
        {
            string id = item.StringId + ".TECH.1";
            if (_records.ContainsKey(id)) continue;
            var research = new GameRecord(id, FcsRecordType.Research, item.BaseName, item.DefinedBy);
            if (item.Name != item.BaseName) research.Translate(item.Name);
            if (template is not null) CopyFields(template, research);
            research.BoolTable["blueprint only"] = true;
            research.IntTable["money"] = (int)(Gameplay.Economy.ItemValue.Of(this, item, 40, false, prices) * prices.BlueprintPriceMult);
            string enable = item.Type switch
            {
                FcsRecordType.Armour => "enable armour",
                FcsRecordType.Container => "enable backpack",
                _ => "enable crossbow"
            };
            research.ReferenceList(enable)[item.StringId] = new ReferenceValues(0, 0, 0);
            string blueprintItem = item.Type == FcsRecordType.Armour ? "BLUEPRINT_ITEM_ARMOUR" : "BLUEPRINT_ITEM_GEAR";
            research.ReferenceList("blueprint item")[blueprintItem] = new ReferenceValues(0, 0, 0);
            _records.Add(id, research);
        }
    }

    #endregion

    #region Merging

    /// <summary>
    /// Copies every value and reference of <paramref name="from"/> onto <paramref name="to"/>, replacing values of the same key and kind.
    /// </summary>
    static void CopyFields(GameRecord from, GameRecord to)
    {
        foreach (var (k, v) in from.BoolTable) to.BoolTable[k] = v;
        foreach (var (k, v) in from.FloatTable) to.FloatTable[k] = v;
        foreach (var (k, v) in from.IntTable) to.IntTable[k] = v;
        foreach (var (k, v) in from.Vector3Table) to.Vector3Table[k] = v;
        foreach (var (k, v) in from.Vector4Table) to.Vector4Table[k] = v;
        foreach (var (k, v) in from.StringTable) to.StringTable[k] = v;
        foreach (var (k, v) in from.FilenameTable) to.FilenameTable[k] = v;
        foreach (var list in from.ReferenceLists)
            foreach (var r in from.GetReferences(list)) to.ReferenceList(list)[r.TargetStringId] = r.Values;
    }

    /// <summary>
    /// Applies one file on top of everything loaded so far.
    /// </summary>
    public void Apply(FcsFile file, string fileName)
    {
        Version++;
        Files.Add(fileName);
        Headers[fileName] = new(file.Version, file.Author, file.Description);
        foreach (var record in file.Records)
            Apply(record, fileName);
    }

    /// <summary>
    /// Merges one record of a file: finds or creates the record, merges each value table, the references and the instances, then removes the record
    /// when its merged <c>REMOVED</c> bool is true.
    /// </summary>
    void Apply(FcsRecord source, string fileName, bool translation = false)
    {
        var record = EnsureRecord(source, fileName, translation);
        Merge(record.BoolTable, source.Bools);
        Merge(record.FloatTable, source.Floats);
        Merge(record.IntTable, source.Ints);
        Merge(record.Vector3Table, source.Vector3S);
        Merge(record.Vector4Table, source.Vector4S);
        Merge(record.StringTable, source.Strings);
        Merge(record.FilenameTable, source.Filenames);
        MergeReferences(record, source);
        MergeInstances(record, source);

        // Checked on the merged record after each change: true takes the record out, so a later file that
        // lists the id starts a fresh record.
        if (record.GetBool(FcsRecord.RemovedKey))
            _records.Remove(record.StringId);
    }

    /// <summary>
    /// The record a source changes or defines: the existing one (warned about unless the source is a change), or a new one.
    /// </summary>
    GameRecord EnsureRecord(FcsRecord source, string fileName, bool translation)
    {
        var record = _records.GetValueOrDefault(source.StringId);
        if (!translation) _translationOnly.Remove(source.StringId);
        if (record is null)
        {
            // The game creates the record anyway (FUN_1406c0b50); the editor would skip it. A translation lists records of mods that need not be
            // loaded: no issue for those.
            if (source.IsModified && !translation)
                Issue(GameDataIssueKind.ModifiedRecordNotFound, fileName, source.StringId,
                    $"changes record '{source.Name}', which no earlier file defines; created from the change");
            record = new GameRecord(source.StringId, source.RecordType, source.Name, fileName);
            _records.Add(source.StringId, record);
            return record;
        }

        if (!source.IsModified)
            Issue(GameDataIssueKind.RecordAlreadyDefined, fileName, source.StringId,
                $"defines record '{source.Name}' again (first defined by {record.DefinedBy}); fields merged");
        if (source.RecordType != record.Type)
            Issue(GameDataIssueKind.ChangedRecordType, fileName, source.StringId,
                $"type {source.RecordType} differs from {record.Type}; kept {record.Type}");
        if (source.IsRenamed)
        {
            if (translation) record.Translate(source.Name);
            else record.Name = source.Name;
        }
        record.ModifiedBy.Add(fileName);
        return record;
    }

    /// <summary>
    /// Merges the reference lists of <paramref name="source"/>: an entry with the removed values deletes the target from the list, any other sets
    /// its values.
    /// </summary>
    static void MergeReferences(GameRecord record, FcsRecord source)
    {
        foreach (var (list, refs) in source.References)
        {
            var target = record.ReferenceList(list);
            foreach (var r in refs)
            {
                if (r.IsRemoved) target.Remove(r.TargetStringId);
                else target[r.TargetStringId] = new ReferenceValues(r.Value0, r.Value1, r.Value2);
            }
        }
    }

    /// <summary>
    /// Merges the instances of <paramref name="source"/> by id: a removed one clears the entry it names (target and states) but keeps it; any other
    /// sets target, position and rotation and appends its states.
    /// </summary>
    static void MergeInstances(GameRecord record, FcsRecord source)
    {
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
    }

    /// <summary>
    /// Sets every key of <paramref name="from"/> in <paramref name="into"/>, replacing a value of the same key.
    /// </summary>
    static void Merge<T>(Dictionary<string, T> into, OrderedDictionary<string, T> from)
    {
        foreach (var (k, v) in from) into[k] = v;
    }

    #endregion

    #region Issues

    /// <summary>
    /// Records a problem found while applying <paramref name="file"/>.
    /// </summary>
    void Issue(GameDataIssueKind kind, string file, string stringId, string message) =>
        Issues.Add(new GameDataIssue(kind, file, stringId, message));

    #endregion
}

/// <summary>
/// What kind of problem a <see cref="GameDataIssue"/> reports.
/// </summary>
public enum GameDataIssueKind
{
    /// <summary>
    /// A file in the load order (e.g. from <c>mods.cfg</c>) doesn't exist.
    /// </summary>
    MissingFile,
    /// <summary>
    /// A record changes a record no earlier file defines; the game creates it from the change.
    /// </summary>
    ModifiedRecordNotFound,
    /// <summary>
    /// A translation file could not be read; the game logs an error and goes on without it.
    /// </summary>
    TranslationNotLoaded,
    /// <summary>
    /// A record defines an id an earlier file already defined; its fields are merged.
    /// </summary>
    RecordAlreadyDefined,
    /// <summary>
    /// A record has a different type than the record it changes; the original type is kept.
    /// </summary>
    ChangedRecordType,
    /// <summary>
    /// A world level file could not be read (a mod's file with its zero bytes turned to spaces); it is left out.
    /// </summary>
    LevelFileNotLoaded,
}

/// <summary>
/// A data file's version, author and description.
/// </summary>
public sealed record FileHeader(int Version, string Author, string Description);

/// <summary>
/// A problem found while building the load order or applying a file: its kind, the file, the record's string id (null when it concerns the whole
/// file) and a message.
/// </summary>
public sealed record GameDataIssue(GameDataIssueKind Kind, string File, string? StringId, string Message)
{
    public override string ToString() => StringId is null ? $"{File}: {Message}" : $"{File}: {StringId} {Message}";
}