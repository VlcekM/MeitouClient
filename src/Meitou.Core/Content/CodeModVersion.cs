using System.Globalization;

namespace Meitou.Content;

/// <summary>
/// A code mod's <c>version</c> (docs/modding-dependencies.md "Versions"): a Semantic Versioning 2.0.0 version, <c>MAJOR.MINOR.PATCH</c> with an
/// optional pre-release (<c>-beta.1</c>) and build metadata (<c>+abc</c>). Precedence is SemVer's: a pre-release is lower than its release
/// (<c>1.2.0-beta &lt; 1.2.0</c>), pre-release identifiers compare one by one (numbers numerically, below text), and build metadata is ignored, also
/// by equality.
/// </summary>
public readonly record struct CodeModVersion(int Major, int Minor, int Patch) : IComparable<CodeModVersion>
{
    /// <summary>
    /// The pre-release identifiers after <c>-</c>, dot-separated; empty for a release.
    /// </summary>
    public string Prerelease { get => field ?? ""; init; }

    /// <summary>
    /// The build metadata after <c>+</c>; empty when none. Kept for display, ignored when comparing.
    /// </summary>
    public string Build { get => field ?? ""; init; }

    /// <summary>
    /// Whether the version has pre-release identifiers, so it sorts below its release.
    /// </summary>
    public bool IsPrerelease => Prerelease.Length > 0;

    /// <summary>
    /// Parses a SemVer 2.0.0 version (<c>1.2.3</c>, <c>1.2.3-beta.1</c>, <c>1.2.3+abc</c>); false when <paramref name="text"/> is not one.
    /// </summary>
    public static bool TryParse(string text, out CodeModVersion version)
    {
        version = default;
        int plus = text.IndexOf('+');
        string build = plus < 0 ? "" : text[(plus + 1)..];
        string rest = plus < 0 ? text : text[..plus];
        int dash = rest.IndexOf('-');
        string prerelease = dash < 0 ? "" : rest[(dash + 1)..];
        string core = dash < 0 ? rest : rest[..dash];
        if (plus >= 0 && !Identifiers(build, numericRule: false)) return false;
        if (dash >= 0 && !Identifiers(prerelease, numericRule: true)) return false;
        if (!TryParseCore(core, out int major, out int minor, out int patch)) return false;
        version = new CodeModVersion(major, minor, patch) { Prerelease = prerelease, Build = build };
        return true;
    }

    /// <summary>
    /// Parses <c>MAJOR.MINOR.PATCH</c>: three numbers without leading zeros that fit an int.
    /// </summary>
    static bool TryParseCore(string core, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        var parts = core.Split('.');
        if (parts.Length != 3 || !parts.All(IsNumber)) return false;
        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch);
    }

    /// <summary>
    /// Dot-separated, non-empty identifiers of ASCII letters, digits and <c>-</c>; pre-release numbers may not have leading zeros.
    /// </summary>
    static bool Identifiers(string text, bool numericRule)
    {
        foreach (var part in text.Split('.'))
        {
            if (part.Length == 0 || !part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
            if (numericRule && part.All(char.IsAsciiDigit) && !IsNumber(part)) return false;
        }
        return true;
    }

    /// <summary>
    /// Digits without a leading zero (a lone <c>0</c> is fine).
    /// </summary>
    static bool IsNumber(string part) => part.Length > 0 && part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0');

    /// <summary>
    /// SemVer precedence: major, minor, patch, then the pre-release identifiers; build metadata is ignored.
    /// </summary>
    public int CompareTo(CodeModVersion other)
    {
        int order = Major != other.Major ? Major.CompareTo(other.Major)
            : Minor != other.Minor ? Minor.CompareTo(other.Minor)
            : Patch.CompareTo(other.Patch);
        return order != 0 ? order : ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>
    /// SemVer 11.3/11.4: a release beats any pre-release; otherwise identifier by identifier, and more identifiers win a tie.
    /// </summary>
    static int ComparePrerelease(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return b.Length.CompareTo(a.Length);
        var left = a.Split('.');
        var right = b.Split('.');
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
            if (CompareIdentifier(left[i], right[i]) is var order and not 0) return order;
        return left.Length.CompareTo(right.Length);
    }

    /// <summary>
    /// Compares two pre-release identifiers: numbers numerically (by length, then digits), a number below text, text ordinally.
    /// </summary>
    static int CompareIdentifier(string a, string b)
    {
        bool aNumber = a.All(char.IsAsciiDigit);
        bool bNumber = b.All(char.IsAsciiDigit);
        if (aNumber && bNumber)
            return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        if (aNumber != bNumber) return aNumber ? -1 : 1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    /// <summary>
    /// Same precedence: build metadata does not count.
    /// </summary>
    public bool Equals(CodeModVersion other) => CompareTo(other) == 0;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    /// <summary>
    /// Same major, minor and patch, whatever the pre-release.
    /// </summary>
    public bool SameRelease(CodeModVersion other) => Major == other.Major && Minor == other.Minor && Patch == other.Patch;

    /// <summary>
    /// The release this version leads to: <c>MAJOR.MINOR.PATCH</c> without pre-release or build (<c>0.16.1-nightly.5</c> gives <c>0.16.1</c>).
    /// </summary>
    public CodeModVersion Release => new(Major, Minor, Patch);

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (IsPrerelease ? "-" + Prerelease : "") + (Build.Length > 0 ? "+" + Build : "");
}

/// <summary>
/// A dependency's version range: comparators separated by spaces, all of which must hold (<c>&gt;=1.2.0 &lt;2.0.0</c>). Operators: <c>&gt;=</c>,
/// <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>, <c>=</c>; a bare version means <c>=</c>. A pre-release is accepted only when a comparator names a
/// pre-release of the same <c>MAJOR.MINOR.PATCH</c> (as npm and Cargo do), so <c>&lt;2.0.0</c> does not take <c>2.0.0-alpha</c> and nothing takes an
/// unasked-for pre-release.
/// </summary>
public sealed class CodeModVersionRange
{
    /// <summary>
    /// The range's comparators in text order: operator (<c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c> or <c>=</c>) and version.
    /// </summary>
    readonly List<(string Op, CodeModVersion Version)> _comparators;

    /// <summary>
    /// Keeps the parsed range; <see cref="Parse"/> is the way in.
    /// </summary>
    CodeModVersionRange(string text, List<(string, CodeModVersion)> comparators)
    {
        Text = text;
        this._comparators = comparators;
    }

    /// <summary>
    /// The range as written in the manifest, trimmed.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Reads a range; throws <see cref="InvalidDataException"/> with the reason when it is not one.
    /// </summary>
    public static CodeModVersionRange Parse(string text)
    {
        var list = new List<(string, CodeModVersion)>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string op = token.StartsWith(">=") || token.StartsWith("<=") ? token[..2] : token[0] is '>' or '<' or '=' ? token[..1] : "=";
            string rest = token[0] is '>' or '<' or '=' ? token[op.Length..] : token;
            if (!CodeModVersion.TryParse(rest, out var version))
                throw new InvalidDataException($"'{token}' is not a version comparator (e.g. >=1.2.0)");
            list.Add((op, version));
        }
        if (list.Count == 0)
            throw new InvalidDataException("an empty version range; leave \"version\" out to accept any version");
        return new CodeModVersionRange(text.Trim(), list);
    }

    /// <summary>
    /// Whether <paramref name="version"/> meets every comparator; a pre-release must also be opted into by a comparator naming a pre-release of
    /// its release.
    /// </summary>
    public bool Contains(CodeModVersion version) =>
        _comparators.All(c => Holds(c.Op, version.CompareTo(c.Version))) && (!version.IsPrerelease || OptsIntoPrerelease(version));

    /// <summary>
    /// Whether a comparator names a pre-release; <c>gameVersion</c> refuses such a range, as it compares releases only.
    /// </summary>
    public bool NamesPrerelease => _comparators.Any(c => c.Version.IsPrerelease);

    /// <summary>
    /// Whether a comparator names a pre-release of the same <c>MAJOR.MINOR.PATCH</c> as <paramref name="version"/>.
    /// </summary>
    bool OptsIntoPrerelease(CodeModVersion version) => _comparators.Any(c => c.Version.IsPrerelease && c.Version.SameRelease(version));

    /// <summary>
    /// Whether a comparison result <paramref name="order"/> (version against the comparator's) satisfies operator <paramref name="op"/>.
    /// </summary>
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