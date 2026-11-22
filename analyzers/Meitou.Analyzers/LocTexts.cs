using System.Text.RegularExpressions;

namespace Meitou.Analyzers;

/// <summary>
/// The texts of Meitou's own that one file holds, as .github/scripts/loc-strings.mjs collects them: the literals translating calls take anywhere,
/// prose literals in player-facing files on lines that show them, and the captions of launcher .axaml files. Interpolated ones are built text,
/// which no catalog can match; the rest are messages every built-in catalog translates.
/// </summary>
static class LocTexts
{
    /// <summary>
    /// The axaml attributes that hold captions (the launcher translates them when it shows the window).
    /// </summary>
    static readonly Regex AxamlCaption = new(@"\s(Text|Content|ToolTip\.Tip|Watermark|PlaceholderText|Header|Title)=""([^""{][^""]*)""",
        RegexOptions.ECMAScript);

    /// <summary>
    /// The built texts and messages of C# source <paramref name="src"/>, whose repository-relative path ('/' separators) is
    /// <paramref name="path"/>.
    /// </summary>
    public static (List<CSharpLiteral> Built, List<LocMessage> Messages) FindInCSharp(string src, string path)
    {
        (List<CSharpLiteral> literals, string masked) = LiteralScanner.Scan(src);
        (HashSet<int> used, List<CallText> translated) = new TranslatingCalls(literals, masked).Find();
        bool launcher = path.StartsWith("launcher/", StringComparison.Ordinal);
        var built = new List<CSharpLiteral>();
        var messages = new List<LocMessage>();
        foreach (CallText call in translated.Where(c => ProseRules.HasWords(c.Literal.Value)))
            Add(call.Literal, call.Context, call.Plural, launcher, built, messages);

        if (ProseRules.IsPlayerFacing(path)) AddShownProse(src, literals, used, built, messages);

        return (built, messages);
    }

    /// <summary>
    /// The captions of a launcher .axaml file's text.
    /// </summary>
    public static IEnumerable<LocMessage> FindInAxaml(string text)
    {
        foreach (Match match in AxamlCaption.Matches(text))
        {
            Group caption = match.Groups[2];
            string id = UnescapeXml(caption.Value);
            if (ProseRules.IsProse(id))
                yield return new LocMessage { Id = id, Launcher = true, Start = caption.Index, End = caption.Index + caption.Length };
        }
    }

    /// <summary>
    /// Adds the prose literals no call takes, on lines that show their literals (not a log, exception, key or resource name).
    /// </summary>
    static void AddShownProse(string src, List<CSharpLiteral> literals, HashSet<int> used, List<CSharpLiteral> built, List<LocMessage> messages)
    {
        string[] lines = src.Split('\n');
        foreach (CSharpLiteral literal in literals.Where(l => !used.Contains(l.Start) && IsShownProse(l, lines)))
            Add(literal, null, null, launcher: false, built, messages);
    }

    /// <summary>
    /// Adds a literal as built text when interpolated, else as a message.
    /// </summary>
    static void Add(CSharpLiteral literal, string? context, string? plural, bool launcher, List<CSharpLiteral> built, List<LocMessage> messages)
    {
        if (literal.Interpolated)
        {
            built.Add(literal);
            return;
        }

        var message = new LocMessage
        {
            Context = context,
            Id = literal.Value,
            Plural = plural,
            Launcher = launcher,
            Start = literal.Start,
            End = literal.End,
        };
        messages.Add(message);
    }

    /// <summary>
    /// Whether a literal reads as prose on a line that shows its literals.
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

    /// <summary>
    /// Decodes the XML entities an axaml attribute may hold.
    /// </summary>
    static string UnescapeXml(string s)
    {
        return s
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&amp;", "&");
    }
}
