using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using Meitou.Simulation;

/// <summary>
/// A character's state as text, one line per field (its hot state, then its cold state and what that owns), for <c>meitou-tools mod-check</c> to set
/// two runs side by side. Read by reflection, the simulation's own types and tuples by their fields (none of their property getters runs), so it
/// follows the tables without a list of its own; collections are listed item by item, other values written as their own text. It lists fields the
/// state hash does not read too, so a field can differ here without changing the hash.
/// </summary>
static class StateFields
{
    /// <summary>
    /// How deep the listing descends, how many items of a collection it lists and how many lines it writes in all.
    /// </summary>
    const int MaxDepth = 6, MaxItems = 32, MaxLines = 5000;

    /// <summary>
    /// The fields of the character in <paramref name="slot"/>, as (path, value) in field order; empty for an empty slot.
    /// </summary>
    public static List<(string Path, string Value)> Of(World world, int slot)
    {
        var table = world.Characters;
        var lines = new Walk(table);
        if (slot >= table.HighWater) return lines.Lines;

        lines.Add("hot", table.Previous[slot], 0);
        lines.Add("cold", table.Cold(slot), 0);
        return lines.Lines;
    }

    /// <summary>
    /// The lines whose values differ between two listings, as "path = a | b"; a field one listing lacks shows as "(none)".
    /// </summary>
    public static IEnumerable<string> Differences(List<(string Path, string Value)> a, List<(string Path, string Value)> b)
    {
        var other = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, value) in b) other[path] = value;
        var mine = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, value) in a)
        {
            mine.Add(path);
            string theirs = other.GetValueOrDefault(path, "(none)");
            if (theirs != value) yield return $"{path} = {value} | {theirs}";
        }

        foreach (var (path, value) in b)
            if (!mine.Contains(path)) yield return $"{path} = (none) | {value}";
    }

    /// <summary>
    /// One listing being written: its lines, their paths and the objects already listed (a second reference to one is named, not listed again).
    /// </summary>
    sealed class Walk(CharacterTable table)
    {
        /// <summary>
        /// The objects already listed.
        /// </summary>
        readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The paths written, so a repeated one (two map keys with the same text) gets a number.
        /// </summary>
        readonly HashSet<string> _paths = new(StringComparer.Ordinal);

        /// <summary>
        /// The lines so far.
        /// </summary>
        public List<(string Path, string Value)> Lines { get; } = [];

        /// <summary>
        /// Lists <paramref name="value"/> under <paramref name="path"/>: a plain value as one line, a simulation type or tuple field by field, a
        /// collection item by item, anything else as its text.
        /// </summary>
        public void Add(string path, object? value, int depth)
        {
            if (value is null || IsPlain(value.GetType()))
            {
                Line(path, Format(value));
                return;
            }

            // behaviour and the world, not this character's state
            if (value is Delegate or World or ITickSystem) return;

            if (value is CharacterCold other && depth > 0)
            {
                Line(path, Character(other));
                return;
            }

            AddObject(path, value, depth);
        }

        /// <summary>
        /// Adds one line, unless the listing is full; a path already written gets a number.
        /// </summary>
        void Line(string path, string value)
        {
            if (Lines.Count >= MaxLines) return;

            string unique = path;
            for (int n = 2; !_paths.Add(unique); n++) unique = $"{path} #{n}";
            Lines.Add((unique, value));
        }

        /// <summary>
        /// Another character met in the listing, by its slot and name.
        /// </summary>
        string Character(CharacterCold other)
        {
            for (int i = 0; i < table.HighWater; i++)
                if (ReferenceEquals(table.Cold(i), other)) return $"character in slot {i} '{other.Name}'";
            return $"character '{other.Name}' in no slot";
        }

        /// <summary>
        /// Lists an object that is not a plain value, once: by fields, by items or as text.
        /// </summary>
        void AddObject(string path, object value, int depth)
        {
            var type = value.GetType();
            if (!type.IsValueType && !_seen.Add(value))
            {
                Line(path, "(listed above)");
                return;
            }

            if (depth >= MaxDepth) Line(path, type.Name);
            else if (IsSimulationType(type) || IsTuple(type)) AddFields(path, value, type, depth);
            else if (value is IDictionary map) AddEntries(path, map, depth);
            else if (value is IEnumerable items) AddItems(path, items, depth);
            else Line(path, value.ToString() ?? type.Name);
        }

        /// <summary>
        /// Lists every instance field of <paramref name="value"/>, its own and its base types'.
        /// </summary>
        void AddFields(string path, object value, Type type, int depth)
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = type; t is not null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
            {
                foreach (var field in t.GetFields(fields))
                {
                    if (field.FieldType.IsByRefLike || field.FieldType.IsPointer) continue;
                    Add($"{path}.{FieldName(field)}", field.GetValue(value), depth + 1);
                }
            }
        }

        /// <summary>
        /// Lists a map's first entries by key, in its own order, then its count.
        /// </summary>
        void AddEntries(string path, IDictionary map, int depth)
        {
            int n = 0;
            foreach (DictionaryEntry entry in map)
            {
                if (n++ == MaxItems) break;
                Add($"{path}[{Format(entry.Key)}]", entry.Value, depth + 1);
            }

            Line($"{path}.Count", map.Count.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Lists a collection's first items by index, then its count (for one that does not keep a count, how many were seen).
        /// </summary>
        void AddItems(string path, IEnumerable items, int depth)
        {
            int n = 0;
            foreach (object? item in items)
            {
                if (n == MaxItems)
                {
                    n++;
                    break;
                }

                Add($"{path}[{n++}]", item, depth + 1);
            }

            string count = items is ICollection c ? c.Count.ToString(CultureInfo.InvariantCulture)
                : n > MaxItems ? $"more than {MaxItems}"
                : n.ToString(CultureInfo.InvariantCulture);
            Line($"{path}.Count", count);
        }
    }

    /// <summary>
    /// Whether a type is written as one value: numbers, flags, text and the vector types.
    /// </summary>
    static bool IsPlain(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type.Namespace == "System.Numerics";

    /// <summary>
    /// Whether the listing descends into a type: the simulation's own (its data records and other projects' types are written as text).
    /// </summary>
    static bool IsSimulationType(Type type) => type.Namespace?.StartsWith("Meitou.Simulation", StringComparison.Ordinal) == true;

    /// <summary>
    /// Whether a type is a value tuple, listed by its items so the simulation objects in it are listed too.
    /// </summary>
    static bool IsTuple(Type type) => type.IsValueType && type.Namespace == "System" && type.Name.StartsWith("ValueTuple`", StringComparison.Ordinal);

    /// <summary>
    /// A field's name, an auto-property's backing field by its property's.
    /// </summary>
    static string FieldName(FieldInfo field) =>
        field.Name.StartsWith('<') && field.Name.IndexOf('>') is > 1 and var end ? field.Name[1..end] : field.Name;

    /// <summary>
    /// A plain value as text, floating point numbers to their last bit as the state hash reads them (two runs a rounding apart must not print alike).
    /// </summary>
    static string Format(object? value) => value switch
    {
        null => "null",
        float f => Float(f),
        double d => Double(d),
        Vector2 v => $"<{Float(v.X)}, {Float(v.Y)}>",
        Vector3 v => $"<{Float(v.X)}, {Float(v.Y)}, {Float(v.Z)}>",
        IFormattable f when value.GetType().Namespace == "System.Numerics" => f.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>
    /// A float as the state hash reads it: -0 as 0, a NaN with its bits.
    /// </summary>
    static string Float(float v)
    {
        if (v == 0) return "0";
        if (float.IsNaN(v)) return $"NaN 0x{BitConverter.SingleToInt32Bits(v):X8}";
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A double as the state hash reads it: -0 as 0, a NaN with its bits.
    /// </summary>
    static string Double(double v)
    {
        if (v == 0) return "0";
        if (double.IsNaN(v)) return $"NaN 0x{BitConverter.DoubleToInt64Bits(v):X16}";
        return v.ToString("R", CultureInfo.InvariantCulture);
    }
}
