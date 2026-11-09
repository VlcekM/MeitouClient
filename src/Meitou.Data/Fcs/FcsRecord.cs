using System.Numerics;

namespace Meitou.Data.Fcs;

/// <summary>
/// One game-data record (an item, character, dialogue line, ...).
/// </summary>
public sealed class FcsRecord
{
    /// <summary>
    /// Name of the bool property that marks a record as deleted.
    /// </summary>
    public const string RemovedKey = "REMOVED";

    /// <summary>
    /// Size of the serialized record in bytes, including this field. Older editor versions wrote 0, and
    /// readers ignore it. <see cref="FcsWriter"/> writes the real size unless this is 0.
    /// </summary>
    public uint ByteSize { get; set; }

    /// <summary>
    /// Record type number; see <see cref="FcsRecordType"/>.
    /// </summary>
    public int Type { get; set; }

    /// <summary>
    /// <see cref="Type"/> as a <see cref="FcsRecordType"/> (a number outside the enum stays as it is).
    /// </summary>
    public FcsRecordType RecordType
    {
        get => (FcsRecordType)Type;
        set => Type = (int)value;
    }

    /// <summary>
    /// Numeric id; only meaningful in files older than format 7, where string ids didn't exist.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The record's display name; a record with <see cref="IsRenamed"/> set renames the earlier one to it.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Unique key, <c>"&lt;number&gt;-&lt;creating file&gt;"</c>, e.g. <c>1292-gamedata.base</c>.
    /// </summary>
    public string StringId { get; set; } = "";

    /// <summary>
    /// Raw flags: <see cref="IsModified"/>, <see cref="IsRenamed"/> and <see cref="SaveCounter"/>.
    /// </summary>
    public uint Flags { get; set; }

    /// <summary>
    /// True if this record changes a record defined by an earlier file (fields not listed keep their values);
    /// false if it defines the record. (A second definition of an existing id is an error in the editor; both the
    /// editor and the game merge its fields into the existing record.)
    /// </summary>
    public bool IsModified => (Flags & 1) != 0;

    /// <summary>
    /// True if the record renames an existing record to <see cref="Name"/> (the game checks only this bit).
    /// </summary>
    public bool IsRenamed => (Flags & 2) != 0;

    /// <summary>
    /// The file's save counter when this record was last changed (bits 4 and up of <see cref="Flags"/>).
    /// </summary>
    public uint SaveCounter => Flags >> 4;

    /// <summary>
    /// True if the record deletes the record with this string id (bool property <c>REMOVED</c>).
    /// </summary>
    public bool IsRemoved => Bools.TryGetValue(RemovedKey, out var removed) && removed;

    /// <summary>
    /// Bool fields by key, in file order (stored as a byte, 0 or 1).
    /// </summary>
    public OrderedDictionary<string, bool> Bools { get; } = [];

    /// <summary>
    /// Float fields by key, in file order.
    /// </summary>
    public OrderedDictionary<string, float> Floats { get; } = [];

    /// <summary>
    /// Int fields by key, in file order.
    /// </summary>
    public OrderedDictionary<string, int> Ints { get; } = [];

    /// <summary>
    /// Vector fields (three floats) by key, in file order.
    /// </summary>
    public OrderedDictionary<string, Vector3> Vector3S { get; } = [];

    /// <summary>
    /// Quaternion-valued fields, stored x, y, z, w.
    /// </summary>
    public OrderedDictionary<string, Vector4> Vector4S { get; } = [];

    /// <summary>
    /// String fields by key, in file order.
    /// </summary>
    public OrderedDictionary<string, string> Strings { get; } = [];

    /// <summary>
    /// Filename fields (paths to meshes, textures, ...) by key, in file order; stored as strings.
    /// </summary>
    public OrderedDictionary<string, string> Filenames { get; } = [];

    /// <summary>
    /// Links to other records, grouped by category (e.g. <c>conditions</c>).
    /// </summary>
    public OrderedDictionary<string, List<FcsReference>> References { get; } = [];

    /// <summary>
    /// Other records placed inside this one (building interiors, towns, ...).
    /// </summary>
    public List<FcsInstance> Instances { get; } = [];

    public override string ToString() => $"{StringId} {Name} ({RecordType})";
}

/// <summary>
/// A link to another record, with three values whose meaning depends on the category.
/// </summary>
public readonly record struct FcsReference(string TargetStringId, int Value0, int Value1, int Value2)
{
    /// <summary>
    /// Values written for a reference a mod removes.
    /// </summary>
    public const int RemovedValue = int.MaxValue;

    /// <summary>
    /// True if this entry removes the reference from the record it modifies (the game requires all three values).
    /// </summary>
    public bool IsRemoved => Value0 == RemovedValue && Value1 == RemovedValue && Value2 == RemovedValue;

    /// <summary>
    /// A reference entry that removes the link to <paramref name="targetStringId"/> (all three values <see cref="RemovedValue"/>).
    /// </summary>
    public static FcsReference Removed(string targetStringId) => new(targetStringId, RemovedValue, RemovedValue, RemovedValue);
}

/// <summary>
/// A placement of a record at a position.
/// </summary>
public sealed class FcsInstance
{
    /// <summary>
    /// The instance's id within the record (a list entry id such as <c>"1"</c>); a later file's instance of the same id merges over
    /// it (<see cref="GameDatabase"/>).
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// StringId of the placed record. Empty means a mod removes this instance.
    /// </summary>
    public string Target { get; set; } = "";

    /// <summary>
    /// True when the instance removes the one of its id (an empty <see cref="Target"/>).
    /// </summary>
    public bool IsRemoved => Target.Length == 0;

    /// <summary>
    /// Where the target record is placed (three floats).
    /// </summary>
    public Vector3 Position { get; set; }

    /// <summary>
    /// Rotation. Stored in the file as w, x, y, z.
    /// </summary>
    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    /// <summary>
    /// String ids of state records (list <c>states</c> in the editor).
    /// </summary>
    public List<string> States { get; } = [];
}