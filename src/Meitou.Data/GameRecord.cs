using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data;

/// <summary>
/// A record after every file in the load order has been applied (see <see cref="GameDatabase"/>).
/// </summary>
public sealed class GameRecord
{
    readonly OrderedDictionary<string, OrderedDictionary<string, ReferenceValues>> _references = new(StringComparer.Ordinal);
    readonly OrderedDictionary<string, GameInstance> _instances = new(StringComparer.Ordinal);
    string? _baseName;

    internal GameRecord(string stringId, FcsRecordType type, string name, string definedBy)
    {
        StringId = stringId;
        Type = type;
        Name = name;
        DefinedBy = definedBy;
    }

    public string StringId { get; }
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
    internal Dictionary<string, bool> BoolTable { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, float> FloatTable { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> IntTable { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, Vector3> Vector3Table { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, Vector4> Vector4Table { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> StringTable { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> FilenameTable { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, bool> Bools => BoolTable;
    public IReadOnlyDictionary<string, float> Floats => FloatTable;
    public IReadOnlyDictionary<string, int> Ints => IntTable;
    public IReadOnlyDictionary<string, Vector3> Vector3S => Vector3Table;
    public IReadOnlyDictionary<string, Vector4> Vector4S => Vector4Table;
    public IReadOnlyDictionary<string, string> Strings => StringTable;

    /// <summary>
    /// File path fields (a separate table from <see cref="Strings"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string> Filenames => FilenameTable;

    public IEnumerable<string> ReferenceLists => _references.Keys;

    public IReadOnlyList<GameReference> GetReferences(string list) =>
        _references.TryGetValue(list, out var refs) ? refs.Select(kv => new GameReference(kv.Key, kv.Value)).ToList() : [];

    public IReadOnlyDictionary<string, GameInstance> Instances => _instances;

    public bool GetBool(string key, bool fallback = false) => BoolTable.GetValueOrDefault(key, fallback);
    public float GetFloat(string key, float fallback = 0) => FloatTable.GetValueOrDefault(key, fallback);
    public int GetInt(string key, int fallback = 0) => IntTable.GetValueOrDefault(key, fallback);
    public string GetString(string key, string fallback = "") => StringTable.GetValueOrDefault(key, fallback);
    public string GetPath(string key, string fallback = "") => FilenameTable.GetValueOrDefault(key, fallback);

    internal OrderedDictionary<string, ReferenceValues> ReferenceList(string list)
    {
        if (!_references.TryGetValue(list, out var refs))
            _references[list] = refs = new(StringComparer.Ordinal);

        return refs;
    }

    internal OrderedDictionary<string, GameInstance> MutableInstances => _instances;

    public override string ToString() => $"{StringId} {Name} ({Type})";
}

public readonly record struct ReferenceValues(int Value0, int Value1, int Value2);

public readonly record struct GameReference(string TargetStringId, ReferenceValues Values);

public sealed class GameInstance
{
    internal GameInstance(string id) => Id = id;

    public string Id { get; }

    /// <summary>
    /// The placed record; empty if a later file cleared the instance (the game keeps the entry).
    /// </summary>
    public string Target { get; internal set; } = "";

    public bool IsCleared => Target.Length == 0;
    public Vector3 Position { get; internal set; }
    public Quaternion Rotation { get; internal set; } = Quaternion.Identity;
    public List<string> States { get; } = [];
}
