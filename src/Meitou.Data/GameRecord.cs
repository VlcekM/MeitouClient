using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data;

/// <summary>
/// A record after every file in the load order has been applied (see <see cref="GameDatabase"/>).
/// </summary>
public sealed class GameRecord
{
    /// <summary>
    /// The reference lists by name, each mapping a target string id to its values, in the order first set.
    /// </summary>
    readonly OrderedDictionary<string, OrderedDictionary<string, ReferenceValues>> _references = new(StringComparer.Ordinal);

    /// <summary>
    /// The placed instances by id, in the order first set.
    /// </summary>
    readonly OrderedDictionary<string, GameInstance> _instances = new(StringComparer.Ordinal);

    /// <summary>
    /// The data files' name once a translation renamed the record; null while it has not.
    /// </summary>
    string? _baseName;

    /// <summary>
    /// A record created by <see cref="GameDatabase"/> from the first file that names <paramref name="stringId"/>.
    /// </summary>
    internal GameRecord(string stringId, FcsRecordType type, string name, string definedBy)
    {
        StringId = stringId;
        Type = type;
        Name = name;
        DefinedBy = definedBy;
    }

    /// <summary>
    /// The unique key, <c>"&lt;number&gt;-&lt;creating file&gt;"</c> (<see cref="FcsRecord.StringId"/>).
    /// </summary>
    public string StringId { get; }

    /// <summary>
    /// The type of the first file's record; a later file with another type is reported and ignored.
    /// </summary>
    public FcsRecordType Type { get; }

    /// <summary>
    /// The name shown to the player: a translation file renames records (docs/formats/localisation.md), so this is in the active language.
    /// </summary>
    public string Name { get; internal set; }

    /// <summary>
    /// The name the data files gave the record, before a translation file renamed it. Code that finds a record by its English name (the player's
    /// faction, a start, the global constants) must compare this, never <see cref="Name"/>.
    /// </summary>
    public string BaseName => _baseName ?? Name;

    /// <summary>
    /// Renames the record from a translation file, keeping the first name as <see cref="BaseName"/>.
    /// </summary>
    internal void Translate(string translated)
    {
        _baseName ??= Name;
        Name = translated;
    }

    /// <summary>
    /// The file that defined the record.
    /// </summary>
    public string DefinedBy { get; }

    /// <summary>
    /// Files that changed the record after <see cref="DefinedBy"/>, in load order.
    /// </summary>
    public List<string> ModifiedBy { get; } = [];

    // One table per value kind, as in the game: a value only replaces the same key of the same kind, so a
    // record can hold e.g. a string and a filename under one key.
    /// <summary>
    /// The bool values by key.
    /// </summary>
    internal Dictionary<string, bool> BoolTable { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The float values by key.
    /// </summary>
    internal Dictionary<string, float> FloatTable { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The int values by key.
    /// </summary>
    internal Dictionary<string, int> IntTable { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The vector values by key.
    /// </summary>
    internal Dictionary<string, Vector3> Vector3Table { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The quaternion values by key, stored x, y, z, w.
    /// </summary>
    internal Dictionary<string, Vector4> Vector4Table { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The string values by key.
    /// </summary>
    internal Dictionary<string, string> StringTable { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The file path values by key.
    /// </summary>
    internal Dictionary<string, string> FilenameTable { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The bool values by key.
    /// </summary>
    public IReadOnlyDictionary<string, bool> Bools => BoolTable;

    /// <summary>
    /// The float values by key.
    /// </summary>
    public IReadOnlyDictionary<string, float> Floats => FloatTable;

    /// <summary>
    /// The int values by key.
    /// </summary>
    public IReadOnlyDictionary<string, int> Ints => IntTable;

    /// <summary>
    /// The vector values by key.
    /// </summary>
    public IReadOnlyDictionary<string, Vector3> Vector3S => Vector3Table;

    /// <summary>
    /// The quaternion values by key, stored x, y, z, w.
    /// </summary>
    public IReadOnlyDictionary<string, Vector4> Vector4S => Vector4Table;

    /// <summary>
    /// The string values by key (file paths are in <see cref="Filenames"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string> Strings => StringTable;

    /// <summary>
    /// File path fields (a separate table from <see cref="Strings"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string> Filenames => FilenameTable;

    /// <summary>
    /// The names of the record's reference lists (a list stays named when a mod removes all its entries).
    /// </summary>
    public IEnumerable<string> ReferenceLists => _references.Keys;

    /// <summary>
    /// The entries of reference list <paramref name="list"/> in the order first set; empty when there is no such list. Builds a new list each call.
    /// </summary>
    public IReadOnlyList<GameReference> GetReferences(string list) =>
        _references.TryGetValue(list, out var refs) ? refs.Select(kv => new GameReference(kv.Key, kv.Value)).ToList() : [];

    /// <summary>
    /// The placed instances by id, in the order first set; a cleared one has an empty target.
    /// </summary>
    public IReadOnlyDictionary<string, GameInstance> Instances => _instances;

    /// <summary>
    /// The bool <paramref name="key"/>, or <paramref name="fallback"/> when the record has none.
    /// </summary>
    public bool GetBool(string key, bool fallback = false) => BoolTable.GetValueOrDefault(key, fallback);

    /// <summary>
    /// The float <paramref name="key"/>, or <paramref name="fallback"/> when the record has none.
    /// </summary>
    public float GetFloat(string key, float fallback = 0) => FloatTable.GetValueOrDefault(key, fallback);

    /// <summary>
    /// The int <paramref name="key"/>, or <paramref name="fallback"/> when the record has none.
    /// </summary>
    public int GetInt(string key, int fallback = 0) => IntTable.GetValueOrDefault(key, fallback);

    /// <summary>
    /// The string <paramref name="key"/>, or <paramref name="fallback"/> when the record has none.
    /// </summary>
    public string GetString(string key, string fallback = "") => StringTable.GetValueOrDefault(key, fallback);

    /// <summary>
    /// The file path <paramref name="key"/>, or <paramref name="fallback"/> when the record has none.
    /// </summary>
    public string GetPath(string key, string fallback = "") => FilenameTable.GetValueOrDefault(key, fallback);

    /// <summary>
    /// The reference list <paramref name="list"/> to change, created empty when the record has none.
    /// </summary>
    internal OrderedDictionary<string, ReferenceValues> ReferenceList(string list)
    {
        if (!_references.TryGetValue(list, out var refs))
            _references[list] = refs = new(StringComparer.Ordinal);

        return refs;
    }

    /// <summary>
    /// The instances for <see cref="GameDatabase"/> to merge into.
    /// </summary>
    internal OrderedDictionary<string, GameInstance> MutableInstances => _instances;

    public override string ToString() => $"{StringId} {Name} ({Type})";
}

/// <summary>
/// The three ints of a reference entry, whose meaning depends on the list (<see cref="FcsReference"/>).
/// </summary>
public readonly record struct ReferenceValues(int Value0, int Value1, int Value2);

/// <summary>
/// One entry of a merged reference list: the target's string id and its values.
/// </summary>
public readonly record struct GameReference(string TargetStringId, ReferenceValues Values);

/// <summary>
/// A placed instance of a merged record (<see cref="FcsInstance"/> after every file has been applied).
/// </summary>
public sealed class GameInstance
{
    /// <summary>
    /// An empty instance of <paramref name="id"/>, filled in by <see cref="GameDatabase"/>.
    /// </summary>
    internal GameInstance(string id) => Id = id;

    /// <summary>
    /// The instance's id within its record (<see cref="FcsInstance.Id"/>).
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// The placed record; empty if a later file cleared the instance (the game keeps the entry).
    /// </summary>
    public string Target { get; internal set; } = "";

    /// <summary>
    /// True when a later file cleared the instance (<see cref="Target"/> is empty).
    /// </summary>
    public bool IsCleared => Target.Length == 0;

    /// <summary>
    /// Where the target is placed, as the last file that set the instance gave it.
    /// </summary>
    public Vector3 Position { get; internal set; }

    /// <summary>
    /// The placement's rotation, as the last file that set the instance gave it.
    /// </summary>
    public Quaternion Rotation { get; internal set; } = Quaternion.Identity;

    /// <summary>
    /// String ids of state records, appended by every file that sets the instance; cleared with it.
    /// </summary>
    public List<string> States { get; } = [];
}
