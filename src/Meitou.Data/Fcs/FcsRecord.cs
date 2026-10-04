using System.Numerics;

namespace Meitou.Data.Fcs;

/// <summary>One game-data record (an item, character, dialogue line, ...).</summary>
public sealed class FcsRecord
{
    /// <summary>Name of the bool property that marks a record as deleted.</summary>
    public const string RemovedKey = "REMOVED";

    /// <summary>
    /// Size of the serialized record in bytes, including this field. Older editor versions wrote 0, and
    /// readers ignore it. <see cref="FcsWriter"/> writes the real size unless this is 0.
    /// </summary>
    public uint ByteSize { get; set; }

    /// <summary>Record type number; see <see cref="FcsRecordType"/>.</summary>
    public int Type { get; set; }

    public FcsRecordType RecordType
    {
        get => (FcsRecordType)Type;
        set => Type = (int)value;
    }

    /// <summary>Numeric id; only meaningful in files older than format 7, where string ids didn't exist.</summary>
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Unique key, <c>"&lt;number&gt;-&lt;creating file&gt;"</c>, e.g. <c>1292-gamedata.base</c>.</summary>
    public string StringId { get; set; } = "";

    /// <summary>Raw flags: <see cref="IsModified"/>, <see cref="IsRenamed"/> and <see cref="SaveCounter"/>.</summary>
    public uint Flags { get; set; }

    /// <summary>
    /// True if this record changes a record defined by an earlier file (fields not listed keep their values);
    /// false if it defines the record. (A second definition of an existing id is an error in the editor, which
    /// still merges its fields into the existing record; what the game does is unknown.)
    /// </summary>
    public bool IsModified => (Flags & 1) != 0;

    /// <summary>True if a modifying record also renames the record to <see cref="Name"/>.</summary>
    public bool IsRenamed => (Flags & 3) == 3;

    /// <summary>The file's save counter when this record was last changed (bits 4 and up of <see cref="Flags"/>).</summary>
    public uint SaveCounter => Flags >> 4;

    /// <summary>True if the record deletes the record with this string id (bool property <c>REMOVED</c>).</summary>
    public bool IsRemoved => Bools.TryGetValue(RemovedKey, out var removed) && removed;

    public OrderedDictionary<string, bool> Bools { get; } = [];
    public OrderedDictionary<string, float> Floats { get; } = [];
    public OrderedDictionary<string, int> Ints { get; } = [];
    public OrderedDictionary<string, Vector3> Vector3s { get; } = [];

    /// <summary>Quaternion-valued fields, stored x, y, z, w.</summary>
    public OrderedDictionary<string, Vector4> Vector4s { get; } = [];
    public OrderedDictionary<string, string> Strings { get; } = [];
    public OrderedDictionary<string, string> Filenames { get; } = [];

    /// <summary>Links to other records, grouped by category (e.g. <c>conditions</c>).</summary>
    public OrderedDictionary<string, List<FcsReference>> References { get; } = [];

    /// <summary>Other records placed inside this one (building interiors, towns, ...).</summary>
    public List<FcsInstance> Instances { get; } = [];

    public override string ToString() => $"{StringId} {Name} ({RecordType})";
}

/// <summary>A link to another record, with three values whose meaning depends on the category.</summary>
public readonly record struct FcsReference(string TargetStringId, int Value0, int Value1, int Value2)
{
    /// <summary>Values written for a reference a mod removes.</summary>
    public const int RemovedValue = int.MaxValue;

    /// <summary>True if this entry removes the reference from the record it modifies.</summary>
    public bool IsRemoved => Value2 == RemovedValue;

    public static FcsReference Removed(string targetStringId) => new(targetStringId, RemovedValue, RemovedValue, RemovedValue);
}

/// <summary>A placement of a record at a position.</summary>
public sealed class FcsInstance
{
    public string Id { get; set; } = "";

    /// <summary>StringId of the placed record. Empty means a mod removes this instance.</summary>
    public string Target { get; set; } = "";

    public bool IsRemoved => Target.Length == 0;

    public Vector3 Position { get; set; }

    /// <summary>Rotation. Stored in the file as w, x, y, z.</summary>
    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    /// <summary>String ids of state records (list <c>states</c> in the editor).</summary>
    public List<string> States { get; } = [];
}
