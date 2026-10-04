namespace Meitou.Data.Fcs;

/// <summary>
/// The record schema from <c>fcs.def</c>: which fields each record type has, their defaults and descriptions.
/// Format: docs/formats/fcs-mod.md, section "Schema".
/// </summary>
public sealed class FcsSchema
{
    readonly Dictionary<string, List<FcsFieldDef>> fieldsByType = new(StringComparer.Ordinal);

    /// <summary>Record type names in order of first appearance.</summary>
    public IEnumerable<string> TypeNames => fieldsByType.Keys;

    readonly Dictionary<string, HashSet<string>> ownedByType = new(StringComparer.Ordinal);

    public IReadOnlyList<FcsFieldDef> FieldsOf(string typeName) =>
        fieldsByType.TryGetValue(typeName, out var fields) ? fields : [];

    /// <summary>True if <paramref name="referenceList"/> of <paramref name="typeName"/> holds child records owned by the record.</summary>
    public bool IsOwned(string typeName, string referenceList) =>
        ownedByType.TryGetValue(typeName, out var owned) && owned.Contains(referenceList);

    /// <summary>
    /// Finds the field a stored key belongs to. Looped fields are declared once with a trailing number
    /// (<c>text0</c>) and stored as <c>text0</c>, <c>text1</c>, ...
    /// </summary>
    public FcsFieldDef? FindField(string typeName, string key)
    {
        var fields = FieldsOf(typeName);
        foreach (var f in fields)
            if (f.Name == key) return f;
        var stem = key.TrimEnd("0123456789".ToCharArray());
        if (stem.Length == key.Length) return null;
        foreach (var f in fields)
            if (f.IsLooped && f.Name.TrimEnd("0123456789".ToCharArray()) == stem) return f;
        return null;
    }

    public static FcsSchema Load(string path) => Parse(File.ReadAllLines(path));

    public static FcsSchema Parse(IEnumerable<string> lines)
    {
        var schema = new FcsSchema();
        string[] owners = [];
        int lineNumber = 0;
        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;

            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']'))
                    throw new FormatException($"fcs.def line {lineNumber}: unterminated section header.");
                owners = line[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (var owner in owners)
                    schema.fieldsByType.TryAdd(owner, []);
                continue;
            }

            // Translation flags (appended at the end of the file) and editor visibility rules
            // (`condition "field" if "other" is VALUE`, under a "CONDITIONS:" heading). Not needed yet.
            if (line.StartsWith("TRANSLATE:") || line.StartsWith("condition ")) continue;

            int colon = line.IndexOf(':');
            if (colon <= 0)
                throw new FormatException($"fcs.def line {lineNumber}: expected 'name: value'.");
            var name = line[..colon].Trim();
            var rest = line[(colon + 1)..].Trim();
            // "name:" with nothing after it is an editor category heading, not a field.
            if (rest.Length == 0) continue;

            // "OWNED: conditions" marks the reference list "conditions" as holding child records owned by this one.
            if (name == "OWNED")
            {
                foreach (var owner in owners)
                {
                    if (!schema.ownedByType.TryGetValue(owner, out var owned))
                        schema.ownedByType[owner] = owned = new(StringComparer.Ordinal);
                    owned.Add(rest);
                }
                continue;
            }

            var field = ParseField(name, rest, lineNumber);
            foreach (var owner in owners)
                schema.fieldsByType[owner].Add(field);
        }
        return schema;
    }

    static FcsFieldDef ParseField(string name, string rest, int lineNumber)
    {
        int i = 0;
        string value;
        bool quoted = rest[0] == '"';
        if (quoted)
        {
            value = ReadQuoted(rest, ref i, lineNumber);
        }
        else
        {
            int start = i;
            while (i < rest.Length && !char.IsWhiteSpace(rest[i]) && rest[i] != '(') i++;
            value = rest[start..i];
        }

        var groups = new List<string>();
        SkipSpace(rest, ref i);
        while (i < rest.Length && rest[i] == '(')
        {
            int close = rest.IndexOf(')', i);
            if (close < 0) throw new FormatException($"fcs.def line {lineNumber}: unterminated '('.");
            groups.Add(rest[(i + 1)..close].Trim());
            i = close + 1;
            SkipSpace(rest, ref i);
        }

        // Bare words between the value and the description, e.g. "looped".
        var modifiers = new List<string>();
        while (i < rest.Length && rest[i] != '"')
        {
            int start = i;
            while (i < rest.Length && !char.IsWhiteSpace(rest[i])) i++;
            modifiers.Add(rest[start..i]);
            SkipSpace(rest, ref i);
        }

        var description = i < rest.Length && rest[i] == '"' ? ReadQuoted(rest, ref i, lineNumber) : "";
        var kind = Classify(value, quoted, groups);
        return new FcsFieldDef(name, kind, value, kind is FcsFieldKind.Reference or FcsFieldKind.Instance ? value : null, groups, description)
        {
            Modifiers = modifiers,
        };
    }

    static FcsFieldKind Classify(string value, bool quoted, List<string> groups)
    {
        if (quoted) return value.Contains('|') ? FcsFieldKind.Filename : FcsFieldKind.String;
        if (value is "True" or "False") return FcsFieldKind.Bool;
        if (value.StartsWith('#')) return FcsFieldKind.Colour;
        if (value.StartsWith("TEXTURE_")) return FcsFieldKind.Texture;
        if (int.TryParse(value, out _)) return FcsFieldKind.Int;
        if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            return FcsFieldKind.Float;
        if (value.Contains('.')) return FcsFieldKind.Enum;
        if (value.Length > 0 && value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_'))
            return groups.Count == 2 ? FcsFieldKind.Instance : FcsFieldKind.Reference;
        return FcsFieldKind.Unknown;
    }

    static string ReadQuoted(string s, ref int i, int lineNumber)
    {
        var sb = new System.Text.StringBuilder();
        for (i++; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i]).Append(s[++i]); continue; }
            if (s[i] == '"') { i++; SkipSpace(s, ref i); return sb.ToString(); }
            sb.Append(s[i]);
        }
        throw new FormatException($"fcs.def line {lineNumber}: unterminated string.");
    }

    static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }
}

/// <summary>One field of a record type in <c>fcs.def</c>.</summary>
/// <param name="Default">Default value as written (unquoted for strings; the type name for references).</param>
/// <param name="ReferenceType">Target record type for <see cref="FcsFieldKind.Reference"/> / <see cref="FcsFieldKind.Instance"/>.</param>
/// <param name="Groups">Parenthesised defaults, e.g. <c>"0, 24,0"</c> for reference values.</param>
public sealed record FcsFieldDef(
    string Name, FcsFieldKind Kind, string Default, string? ReferenceType, IReadOnlyList<string> Groups, string Description)
{
    /// <summary>Words after the value; only <c>looped</c> is known.</summary>
    public IReadOnlyList<string> Modifiers { get; init; } = [];

    /// <summary>A numbered series: declared as <c>name0</c>, stored as <c>name0</c>, <c>name1</c>, ...</summary>
    public bool IsLooped => Modifiers.Contains("looped");
}

public enum FcsFieldKind
{
    Unknown,
    Bool,
    Int,
    Float,
    String,
    /// <summary>A file path; the default is an editor file filter like <c>"Ogre mesh|*.mesh"</c>.</summary>
    Filename,
    /// <summary><c>Enum.VALUE</c>, an enum from fcs_enums.def.</summary>
    Enum,
    /// <summary><c>#RRGGBB</c>.</summary>
    Colour,
    /// <summary><c>TEXTURE_ANY</c> / <c>TEXTURE_DDS</c>.</summary>
    Texture,
    /// <summary>A reference list to records of <see cref="FcsFieldDef.ReferenceType"/>.</summary>
    Reference,
    /// <summary>Placed instances (<c>TYPE (pos) (rot)</c>).</summary>
    Instance,
}
