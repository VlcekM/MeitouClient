using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;

namespace Meitou.Modding;

/// <summary>
/// What a mod gets in <see cref="IModDataPhase.EditData"/> (docs/modding.md "Data phase"): the merged records so far to read, and record changes
/// in the form of a <c>.mod</c> file's records, queued and applied in order when it returns, each merged as a data file's record listed after
/// every loaded one (docs/formats/fcs-mod.md "Merging"). Changes can be queued only during <see cref="IModDataPhase.EditData"/>.
/// </summary>
public sealed class ModDataContext
{
    /// <summary>
    /// The changes queued, in order.
    /// </summary>
    readonly List<FcsRecord> _changes = [];

    /// <summary>
    /// Made by the host for one mod before its <see cref="IModDataPhase.EditData"/>.
    /// </summary>
    internal ModDataContext(LoadedMod mod, GameDatabase data)
    {
        Mod = mod;
        Data = data;
    }

    /// <summary>
    /// The mod this context belongs to.
    /// </summary>
    internal LoadedMod Mod { get; }

    /// <summary>
    /// Whether changes may be queued: true during <see cref="IModDataPhase.EditData"/>, cleared when it returns.
    /// </summary>
    internal bool Open { get; set; } = true;

    /// <summary>
    /// The mod's manifest id.
    /// </summary>
    public string Id => Mod.Manifest.Id;

    /// <summary>
    /// The mod's name (its <c>.mod</c> file's stem, as in mods.cfg).
    /// </summary>
    public string Name => Mod.Info.Name;

    /// <summary>
    /// The mod's folder (its <c>.mod</c> file's).
    /// </summary>
    public string Folder => Mod.Info.Folder;

    /// <summary>
    /// The mod's code manifest.
    /// </summary>
    public CodeModManifest Manifest => Mod.Manifest;

    /// <summary>
    /// The merged records: every data file and translation, and the changes of the mods before this one. Read it only: change records through
    /// <see cref="Edit"/>, <see cref="Add"/>, <see cref="Remove"/> and <see cref="Apply"/>, which this mod's reads do not see until it returns.
    /// Writing to it directly (its records' tables, its methods that change records) is unsupported: such a change is not checked, not counted
    /// as the mod's and not in the navmesh cache key.
    /// </summary>
    public GameDatabase Data { get; }

    /// <summary>
    /// The data file the changes count as made by (a record's <c>ModifiedBy</c>, and the file named in a problem): the mod's own <c>.mod</c> file.
    /// </summary>
    internal string FileName => Path.GetFileName(Mod.Info.Path);

    /// <summary>
    /// The changes queued, in order.
    /// </summary>
    internal IReadOnlyList<FcsRecord> Changes => _changes;

    /// <summary>
    /// Queues a change of the loaded record <paramref name="stringId"/> and returns it to fill: a value set replaces the record's value of that
    /// key, a reference set adds or replaces that entry (<see cref="FcsReference.Removed"/> takes it out), and setting <see cref="FcsRecord.Name"/>
    /// with flag 2 in <see cref="FcsRecord.Flags"/> renames the record. Throws <see cref="ArgumentException"/> when no such record is loaded.
    /// </summary>
    public FcsRecord Edit(string stringId)
    {
        EnsureOpen();
        var record = Data.Find(stringId) ?? throw new ArgumentException($"no record {stringId} is loaded", nameof(stringId));
        return Queue(new FcsRecord { StringId = stringId, RecordType = record.Type, Name = record.Name, Flags = 1 });
    }

    /// <summary>
    /// Queues a new record and returns it to fill. Throws <see cref="ArgumentException"/> when a record of that id is loaded already.
    /// </summary>
    public FcsRecord Add(string stringId, FcsRecordType type, string name)
    {
        EnsureOpen();
        if (Data.Find(stringId) is not null) throw new ArgumentException($"record {stringId} is loaded already", nameof(stringId));

        return Queue(new FcsRecord { StringId = stringId, RecordType = type, Name = name });
    }

    /// <summary>
    /// Queues the removal of the loaded record <paramref name="stringId"/> (its <c>REMOVED</c> bool, as a data file removes one). Throws
    /// <see cref="ArgumentException"/> when no such record is loaded.
    /// </summary>
    public void Remove(string stringId) => Edit(stringId).Bools[FcsRecord.RemovedKey] = true;

    /// <summary>
    /// Queues <paramref name="record"/> as a data file's record, as it is (its flags decide whether it defines, changes or renames).
    /// </summary>
    public void Apply(FcsRecord record) => Queue(record);

    /// <summary>
    /// Writes a line to the game's log, tagged with the mod's id.
    /// </summary>
    public void Log(string message) => Console.WriteLine($"mod       {Id}: {message}");

    /// <summary>
    /// Writes a line to the game's error log, tagged with the mod's id.
    /// </summary>
    public void LogError(string message) => Console.Error.WriteLine($"mod       {Id}: {message}");

    /// <summary>
    /// Adds <paramref name="record"/> to the queue and returns it.
    /// </summary>
    FcsRecord Queue(FcsRecord record)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(record);
        _changes.Add(record);
        return record;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> once <see cref="IModDataPhase.EditData"/> has returned: a change queued later would never be
    /// applied.
    /// </summary>
    void EnsureOpen()
    {
        if (!Open) throw new InvalidOperationException("game data changes can be queued only during EditData");
    }
}
