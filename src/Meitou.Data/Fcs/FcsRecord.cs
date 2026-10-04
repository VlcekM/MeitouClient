using System.Numerics;

namespace Meitou.Data.Fcs;

/// <summary>One game-data record (an item, character, dialogue line, ...).</summary>
public sealed class FcsRecord
{
    /// <summary>Unknown. 0 in Kenshi's own files except <c>rebirth.mod</c>.</summary>
    public int Unknown { get; set; }

    /// <summary>Record kind. The number-to-name mapping is mostly unknown (19 = DIALOGUE_LINE).</summary>
    public int Type { get; set; }

    /// <summary>0 in every sample seen.</summary>
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Unique key, <c>"&lt;number&gt;-&lt;creating file&gt;"</c>, e.g. <c>1292-gamedata.base</c>.</summary>
    public string StringId { get; set; } = "";

    /// <summary>Record state flags; meaning not yet known (see docs/formats/fcs-mod.md).</summary>
    public uint Flags { get; set; }

    public OrderedDictionary<string, bool> Bools { get; } = [];
    public OrderedDictionary<string, float> Floats { get; } = [];
    public OrderedDictionary<string, int> Ints { get; } = [];
    public OrderedDictionary<string, Vector3> Vector3s { get; } = [];
    public OrderedDictionary<string, Vector4> Vector4s { get; } = [];
    public OrderedDictionary<string, string> Strings { get; } = [];
    public OrderedDictionary<string, string> Filenames { get; } = [];

    /// <summary>Links to other records, grouped by category (e.g. <c>conditions</c>).</summary>
    public OrderedDictionary<string, List<FcsReference>> References { get; } = [];

    /// <summary>Other records placed inside this one (building interiors, towns, ...).</summary>
    public List<FcsInstance> Instances { get; } = [];

    public override string ToString() => $"{StringId} {Name} (type {Type})";
}

/// <summary>A link to another record, with three values whose meaning depends on the category.</summary>
public readonly record struct FcsReference(string TargetStringId, int Value0, int Value1, int Value2);

/// <summary>A placement of a record at a position.</summary>
public sealed class FcsInstance
{
    public string Id { get; set; } = "";

    /// <summary>StringId of the placed record.</summary>
    public string Target { get; set; } = "";

    public Vector3 Position { get; set; }

    /// <summary>Rotation quaternion, stored as read (component order not yet confirmed).</summary>
    public Vector4 Rotation { get; set; }

    public List<string> States { get; } = [];
}
