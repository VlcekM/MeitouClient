using System.Globalization;

namespace Meitou.Content;

/// <summary>
/// A code mod's <c>version</c> (docs/modding.md "Dependencies"): <c>MAJOR.MINOR.PATCH</c>, whole numbers. A pre-release or build suffix (<c>-beta.1</c>, <c>+abc</c>) is
/// allowed and ignored when comparing, so <c>1.2.0-beta</c> satisfies <c>&gt;=1.2.0</c>.
/// </summary>
public readonly record struct CodeModVersion(int Major, int Minor, int Patch) : IComparable<CodeModVersion>
{
    public static bool TryParse(string text, out CodeModVersion version)
    {
        version = default;
        int end = text.IndexOfAny(['-', '+']);
        var parts = (end < 0 ? text : text[..end]).Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (int i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        version = new CodeModVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public int CompareTo(CodeModVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>
/// A dependency's version range: comparators separated by spaces, all of which must hold (<c>&gt;=1.2.0 &lt;2.0.0</c>). Operators: <c>&gt;=</c>, <c>&gt;</c>,
/// <c>&lt;=</c>, <c>&lt;</c>, <c>=</c>; a bare version means <c>=</c>.
/// </summary>
public sealed class CodeModVersionRange
{
    readonly List<(string Op, CodeModVersion Version)> comparators;

    CodeModVersionRange(string text, List<(string, CodeModVersion)> comparators)
    {
        Text = text;
        this.comparators = comparators;
    }

    public string Text { get; }

    /// <summary>Reads a range; throws <see cref="InvalidDataException"/> with the reason when it is not one.</summary>
    public static CodeModVersionRange Parse(string text)
    {
        var list = new List<(string, CodeModVersion)>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string op = token.StartsWith(">=") || token.StartsWith("<=") ? token[..2] : token[0] is '>' or '<' or '=' ? token[..1] : "=";
            string rest = token[0] is '>' or '<' or '=' ? token[op.Length..] : token;
            if (!CodeModVersion.TryParse(rest, out var version)) throw new InvalidDataException($"'{token}' is not a version comparator (e.g. >=1.2.0)");
            list.Add((op, version));
        }
        if (list.Count == 0) throw new InvalidDataException("an empty version range; leave \"version\" out to accept any version");
        return new CodeModVersionRange(text.Trim(), list);
    }

    public bool Contains(CodeModVersion version) => comparators.All(c => Holds(c.Op, version.CompareTo(c.Version)));

    static bool Holds(string op, int order) => op switch
    {
        ">=" => order >= 0,
        ">" => order > 0,
        "<=" => order <= 0,
        "<" => order < 0,
        _ => order == 0,
    };

    public override string ToString() => Text;
}
