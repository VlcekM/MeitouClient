using System.Collections.Immutable;
using Meitou.Data;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Factions;

/// <summary>
/// BFrizz's Extra Extensions variables of one world (docs/game/dialogue.md "Variables"): the values lines set or added to, or a save gave, by
/// VARIABLE string id; a variable without one has its record's <c>value</c>. The plugin writes the shared game data, so a change is seen at once by
/// every reader: here the values are replaced whole on each change (serial phases only), and any thread reads the current set.
/// </summary>
public sealed class WorldVariables
{
    /// <summary>
    /// The values set so far, in string id order.
    /// </summary>
    volatile ImmutableSortedDictionary<string, int> _values = ImmutableSortedDictionary.Create<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// How many variables have a value of their own.
    /// </summary>
    public int Count => _values.Count;

    /// <summary>
    /// The values set so far, in string id order.
    /// </summary>
    public IEnumerable<KeyValuePair<string, int>> All => _values;

    /// <summary>
    /// The variable's current value: the one set, else its record's <c>value</c> (0 for a record without one).
    /// </summary>
    public int Value(GameDatabase db, string variable) =>
        _values.TryGetValue(variable, out int value) ? value : db.Find(variable)?.GetInt("value") ?? 0;

    /// <summary>
    /// <c>set variable</c> and a save's value: the variable becomes <paramref name="value"/>.
    /// </summary>
    public void Set(string variable, int value) => _values = _values.SetItem(variable, value);

    /// <summary>
    /// A line's effects (the plugin's <c>_doActions</c> hook): every <c>set variable</c>, then every <c>add to variable</c> (an int sum, wrapping as
    /// the plugin's does).
    /// </summary>
    public void Apply(GameDatabase db, IReadOnlyList<VariableChange> sets, IReadOnlyList<VariableChange> adds)
    {
        foreach (var (variable, value) in sets) Set(variable, value);
        foreach (var (variable, value) in adds) Set(variable, unchecked(Value(db, variable) + value));
    }

    /// <summary>
    /// Adds the values set to the world's state hash, in string id order.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(_values.Count);
        foreach (var (variable, value) in _values)
        {
            h.Add(Rng.StableHash(variable));
            h.Add(value);
        }
    }
}
