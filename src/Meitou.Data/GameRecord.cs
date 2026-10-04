using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data;

/// <summary>
/// A record after every file in the load order has been applied (see <see cref="GameDatabase"/>).
/// </summary>
public sealed class GameRecord
{
    readonly Dictionary<string, object> fields = new(StringComparer.Ordinal);
    readonly OrderedDictionary<string, OrderedDictionary<string, ReferenceValues>> references = new(StringComparer.Ordinal);
    readonly OrderedDictionary<string, GameInstance> instances = new(StringComparer.Ordinal);

    internal GameRecord(string stringId, FcsRecordType type, string name, string definedBy)
    {
        StringId = stringId;
        Type = type;
        Name = name;
        DefinedBy = definedBy;
    }

    public string StringId { get; }
    public FcsRecordType Type { get; }
    public string Name { get; internal set; }

    /// <summary>The file that defined the record.</summary>
    public string DefinedBy { get; }

    /// <summary>Files that changed the record after <see cref="DefinedBy"/>, in load order.</summary>
    public List<string> ModifiedBy { get; } = [];

    /// <summary>
    /// Field values: <see cref="bool"/>, <see cref="float"/>, <see cref="int"/>, <see cref="Vector3"/>,
    /// <see cref="Vector4"/>, <see cref="string"/> or <see cref="GamePath"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object> Fields => fields;

    public IEnumerable<string> ReferenceLists => references.Keys;

    public IReadOnlyList<GameReference> GetReferences(string list) =>
        references.TryGetValue(list, out var refs) ? refs.Select(kv => new GameReference(kv.Key, kv.Value)).ToList() : [];

    public IReadOnlyDictionary<string, GameInstance> Instances => instances;

    public bool GetBool(string key, bool fallback = false) => fields.TryGetValue(key, out var v) && v is bool b ? b : fallback;
    public float GetFloat(string key, float fallback = 0) => fields.TryGetValue(key, out var v) && v is float f ? f : fallback;
    public int GetInt(string key, int fallback = 0) => fields.TryGetValue(key, out var v) && v is int i ? i : fallback;
    public string GetString(string key, string fallback = "") => fields.TryGetValue(key, out var v) && v is string s ? s : fallback;
    public string GetPath(string key, string fallback = "") => fields.TryGetValue(key, out var v) && v is GamePath p ? p.Value : fallback;

    internal void SetField(string key, object value) => fields[key] = value;
    internal bool RemoveField(string key) => fields.Remove(key);

    internal OrderedDictionary<string, ReferenceValues> ReferenceList(string list)
    {
        if (!references.TryGetValue(list, out var refs))
            references[list] = refs = new(StringComparer.Ordinal);
        return refs;
    }

    internal OrderedDictionary<string, GameInstance> MutableInstances => instances;

    public override string ToString() => $"{StringId} {Name} ({Type})";
}

/// <summary>A file path field (kept apart from strings: a string value never overrides a path).</summary>
public readonly record struct GamePath(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct ReferenceValues(int Value0, int Value1, int Value2);

public readonly record struct GameReference(string TargetStringId, ReferenceValues Values);

public sealed class GameInstance
{
    internal GameInstance(string id) => Id = id;

    public string Id { get; }
    public string Target { get; internal set; } = "";
    public Vector3 Position { get; internal set; }
    public Quaternion Rotation { get; internal set; } = Quaternion.Identity;
    public List<string> States { get; } = [];
}
