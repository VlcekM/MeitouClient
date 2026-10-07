using Meitou.Data.Fcs;

namespace Meitou.Data.Save;

/// <summary>
/// An object handle of the save (docs/formats/save.md "Object handles and slot lists"): the class of the object, the container it lives in with the
/// container's serial, its slot in that container and its own random serial. Stored as five ints under one key prefix
/// (<c>handleTYPE</c>, <c>handleC</c>, <c>handleCS</c>, <c>handleI</c>, <c>handleS</c>). Type 11 is the null handle; the other parts of a null handle are
/// stale values nobody reads, so <see cref="IsNull"/> tests the type only.
/// </summary>
public readonly record struct Hand(int Type, int C, int CS, int I, int S)
{
    /// <summary>The <c>TYPE</c> of the null handle (<see cref="FcsRecordType.NULL_ITEM"/>).</summary>
    public const int NullType = 11;

    public static readonly Hand Null = new(NullType, 0, 0, 0, 0);

    public bool IsNull => Type == NullType;

    /// <summary>The class of the object when the type is a known <see cref="FcsRecordType"/>.</summary>
    public FcsRecordType RecordType => (FcsRecordType)Type;

    /// <summary>
    /// Reads the handle stored under <paramref name="prefix"/>. A record without <c>&lt;prefix&gt;I</c> holds none, which reads as <see cref="Null"/>
    /// (the game's reader does the same); a missing other part reads as 0.
    /// </summary>
    public static Hand Read(FcsRecord record, string prefix)
    {
        if (!record.Ints.TryGetValue(prefix + "I", out int i)) return Null;
        return new Hand(Get(record, prefix + "TYPE"), Get(record, prefix + "C"), Get(record, prefix + "CS"), i, Get(record, prefix + "S"));
    }

    /// <summary>True when the record has all five parts under the prefix.</summary>
    public static bool IsComplete(FcsRecord record, string prefix) =>
        record.Ints.ContainsKey(prefix + "TYPE") && record.Ints.ContainsKey(prefix + "C") && record.Ints.ContainsKey(prefix + "CS") &&
        record.Ints.ContainsKey(prefix + "I") && record.Ints.ContainsKey(prefix + "S");

    /// <summary>Writes the five parts under <paramref name="prefix"/> in the order the game does (TYPE, C, CS, I, S).</summary>
    public void Write(FcsRecord record, string prefix)
    {
        record.Ints[prefix + "TYPE"] = Type;
        record.Ints[prefix + "C"] = C;
        record.Ints[prefix + "CS"] = CS;
        record.Ints[prefix + "I"] = I;
        record.Ints[prefix + "S"] = S;
    }

    /// <summary>The prefixes of the handles in a record: every int key ending in <c>TYPE</c> whose prefix also has an <c>I</c>.</summary>
    public static IEnumerable<string> Prefixes(FcsRecord record)
    {
        foreach (var key in record.Ints.Keys)
            if (key.EndsWith("TYPE", StringComparison.Ordinal) && record.Ints.ContainsKey(key[..^4] + "I"))
                yield return key[..^4];
    }

    static int Get(FcsRecord record, string key) => record.Ints.GetValueOrDefault(key);

    public override string ToString() => IsNull ? "null" : $"{RecordType}[C{C}/{CS} I{I} S{S}]";
}
