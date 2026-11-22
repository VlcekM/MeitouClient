namespace Meitou.Analyzers;

/// <summary>
/// The interpolated literals of a C# file that a player reads, so no catalog can match them, as .github/scripts/loc-strings.mjs collects its
/// built texts: those a translating call takes anywhere, and prose ones in player-facing files on lines that show their literals.
/// </summary>
static class BuiltTexts
{
    /// <summary>
    /// The built texts of <paramref name="src"/>, whose repository-relative path ('/' separators) is <paramref name="path"/>.
    /// </summary>
    public static IEnumerable<CSharpLiteral> Find(string src, string path)
    {
        (List<CSharpLiteral> literals, string masked) = LiteralScanner.Scan(src);
        (HashSet<int> used, List<CSharpLiteral> messages) = new TranslatingCalls(literals, masked).Find();
        IEnumerable<CSharpLiteral> translated = messages.Where(l => l.Interpolated && ProseRules.HasWords(l.Value));
        if (!ProseRules.IsPlayerFacing(path)) return translated;

        string[] lines = src.Split('\n');
        IEnumerable<CSharpLiteral> shown = literals.Where(l => !used.Contains(l.Start) && l.Interpolated && IsShownProse(l, lines));
        return translated.Concat(shown);
    }

    /// <summary>
    /// Whether a literal reads as prose on a line that shows its literals (not a log, exception, key or resource name).
    /// </summary>
    static bool IsShownProse(CSharpLiteral literal, string[] lines)
    {
        string line = lines[literal.Line - 1];
        if (ProseRules.IsNotShown(BlankFirst(line, literal.Raw))) return false;
        if (ProseRules.IsDictionaryKey(line, literal.Raw)) return false;

        return ProseRules.IsProse(literal.Value);
    }

    /// <summary>
    /// <paramref name="line"/> with the first occurrence of <paramref name="raw"/> blanked.
    /// </summary>
    static string BlankFirst(string line, string raw)
    {
        int at = line.IndexOf(raw, StringComparison.Ordinal);
        if (at < 0) return line;

        return line.Substring(0, at) + new string(' ', raw.Length) + line.Substring(at + raw.Length);
    }
}
