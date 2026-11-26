namespace Meitou.Data.Gameplay;

/// <summary>
/// How a <see cref="VariableTest"/> compares the variable's <c>value</c> with the reference's val0.
/// </summary>
public enum VariableCompare
{
    /// <summary>
    /// <c>variable equals</c>: value == val0.
    /// </summary>
    Equals,

    /// <summary>
    /// <c>variable less than</c>: value &lt; val0.
    /// </summary>
    LessThan,

    /// <summary>
    /// <c>variable greater than</c>: value &gt; val0.
    /// </summary>
    GreaterThan,
}

/// <summary>
/// One BFrizz's Extra Extensions variable condition (docs/game/dialogue.md "Variables"): the referenced VARIABLE's string id, the comparison and the
/// reference's val0.
/// </summary>
public readonly record struct VariableTest(string Variable, VariableCompare Compare, int Value)
{
    /// <summary>
    /// Whether the variable's current <paramref name="value"/> passes.
    /// </summary>
    public bool Holds(int value) => Compare switch
    {
        VariableCompare.Equals => value == Value,
        VariableCompare.LessThan => value < Value,
        _ => value > Value,
    };
}

/// <summary>
/// One Extra Extensions variable effect of a line (<c>set variable</c> or <c>add to variable</c>): the referenced VARIABLE's string id and val0.
/// </summary>
public readonly record struct VariableChange(string Variable, int Value);

/// <summary>
/// BFrizz's Extra Extensions (RE_Kenshi plugin, workshop 3625832102) variables as the data declares them (docs/game/dialogue.md "Variables"): records
/// of the plugin's type 1000 (VARIABLE) holding one int <c>value</c>, and the reference lists that test and change them.
/// </summary>
public static class VariableData
{
    /// <summary>
    /// The plugin's record type number for VARIABLE.
    /// </summary>
    public const int RecordType = 1000;

    /// <summary>
    /// The three condition lists in the order the plugin tests them, each with its comparison.
    /// </summary>
    static readonly (string List, VariableCompare Compare)[] TestLists =
    [
        ("variable equals", VariableCompare.Equals),
        ("variable less than", VariableCompare.LessThan),
        ("variable greater than", VariableCompare.GreaterThan),
    ];

    /// <summary>
    /// Whether <paramref name="id"/> names a record with an int <c>value</c>, the only ones the plugin reads or writes.
    /// </summary>
    public static bool HasValue(GameDatabase db, string id) => db.Find(id) is { } variable && variable.Ints.ContainsKey("value");

    /// <summary>
    /// A DIALOGUE / DIALOGUE_LINE / WORD_SWAPS record's variable conditions (the plugin's <c>checkTags</c> hook): every reference of the three lists
    /// whose record has a <c>value</c>, in list order; the others are skipped.
    /// </summary>
    public static IReadOnlyList<VariableTest> DialogueTests(GameDatabase db, GameRecord record)
    {
        var tests = new List<VariableTest>();
        foreach (var (list, compare) in TestLists)
        {
            foreach (var r in record.GetReferences(list))
            {
                if (HasValue(db, r.TargetStringId)) tests.Add(new VariableTest(r.TargetStringId, compare, r.Values.Value0));
            }
        }
        return tests;
    }

    /// <summary>
    /// A WORLD_EVENT_STATE's variable conditions (the plugin's <c>isTrue</c> hook): of each of the three lists the record has, only the first
    /// reference whose record has a <c>value</c> is tested. <paramref name="neverHolds"/>: a list the record has holds no such reference, which fails
    /// the state.
    /// </summary>
    public static IReadOnlyList<VariableTest> WorldStateTests(GameDatabase db, GameRecord record, out bool neverHolds)
    {
        var tests = new List<VariableTest>();
        neverHolds = false;
        var lists = record.ReferenceLists.ToHashSet(StringComparer.Ordinal);
        foreach (var (list, compare) in TestLists)
        {
            if (!lists.Contains(list)) continue;

            var first = record.GetReferences(list).FirstOrDefault(r => HasValue(db, r.TargetStringId));
            if (first.TargetStringId is null) neverHolds = true;
            else tests.Add(new VariableTest(first.TargetStringId, compare, first.Values.Value0));
        }
        return tests;
    }

    /// <summary>
    /// A line's <paramref name="list"/> (<c>set variable</c> or <c>add to variable</c>): every reference whose record has a <c>value</c>, in order.
    /// </summary>
    public static IReadOnlyList<VariableChange> Changes(GameDatabase db, GameRecord record, string list) =>
        [.. record.GetReferences(list)
            .Where(r => HasValue(db, r.TargetStringId))
            .Select(r => new VariableChange(r.TargetStringId, r.Values.Value0))];
}
