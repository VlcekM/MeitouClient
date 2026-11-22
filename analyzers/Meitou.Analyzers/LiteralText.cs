using System.Text.RegularExpressions;

namespace Meitou.Analyzers;

/// <summary>
/// Reads one C# string literal's extent and text, as .github/scripts/loc-strings.mjs does (readLiteral and its helpers).
/// </summary>
static class LiteralText
{
    /// <summary>
    /// A literal's opening: dollars, '@', dollars, then three or more quotes or one.
    /// </summary>
    static readonly Regex Head = new(@"^(\$+)?(@)?(\$+)?(""""""+|"")", RegexOptions.CultureInvariant);

    /// <summary>
    /// An escape sequence of a regular literal.
    /// </summary>
    static readonly Regex Escape = new(@"\\(u[0-9a-fA-F]{4}|x[0-9a-fA-F]{1,4}|.)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The literal starting at <paramref name="i"/>, or null when none starts there (also when a word character or a quote precedes it).
    /// </summary>
    public static CSharpLiteral? Read(string src, int i)
    {
        Match head = Head.Match(src.Substring(i, Math.Min(8, src.Length - i)));
        if (!head.Success) return null;
        if (i > 0 && (IsWordChar(src[i - 1]) || src[i - 1] == '"')) return null;

        int dollars = head.Groups[1].Success ? head.Groups[1].Length : head.Groups[3].Length;
        string quote = head.Groups[4].Value;
        int start = i + head.Length;
        CSharpLiteral literal = quote.Length >= 3
            ? ReadRaw(src, start, quote, dollars)
            : ReadQuoted(src, start, head.Groups[2].Success, dollars > 0);

        literal.Interpolated = dollars > 0;
        literal.Start = i;
        literal.Open = head.Length;
        return literal;
    }

    /// <summary>
    /// The offset of the '}' that closes an interpolation hole whose code starts at <paramref name="j"/> (literals inside it skipped whole).
    /// </summary>
    public static int HoleEnd(string src, int j)
    {
        int depth = 0;
        while (j < src.Length)
        {
            if (Read(src, j) is { } literal)
            {
                j = literal.End;
                continue;
            }

            char ch = src[j];
            if (ch is '{' or '(' or '[') depth++;
            else if (ch is ')' or ']' && depth > 0) depth--;
            else if (ch == '}')
            {
                if (depth == 0) return j;
                depth--;
            }

            j++;
        }

        return j;
    }

    /// <summary>
    /// Whether <paramref name="ch"/> is in JavaScript's ASCII \w.
    /// </summary>
    public static bool IsWordChar(char ch)
    {
        return ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';
    }

    /// <summary>
    /// A raw literal whose text starts at <paramref name="start"/> and ends at the next <paramref name="quote"/>.
    /// </summary>
    static CSharpLiteral ReadRaw(string src, int start, string quote, int dollars)
    {
        int close = src.IndexOf(quote, start, StringComparison.Ordinal);
        if (close < 0) close = src.Length;

        string raw = src.Substring(start, close - start);
        return new CSharpLiteral
        {
            Value = DedentRaw(raw),
            Raw = raw,
            Holes = dollars > 0 ? RawHoles(src, start, close, dollars) : [],
            End = Math.Min(src.Length, close + quote.Length),
            Close = quote.Length,
        };
    }

    /// <summary>
    /// A one-quote literal (regular or verbatim) whose text starts at <paramref name="start"/>.
    /// </summary>
    static CSharpLiteral ReadQuoted(string src, int start, bool verbatim, bool interpolated)
    {
        var holes = new List<(int From, int To)>();
        int close = ClosingQuote(src, start, verbatim, interpolated, holes);
        string raw = src.Substring(start, Math.Min(close, src.Length) - start);
        return new CSharpLiteral
        {
            Value = Decode(raw, verbatim),
            Raw = raw,
            Holes = holes,
            End = close + 1,
            Close = 1,
        };
    }

    /// <summary>
    /// The offset of the quote (or line break) that ends a one-quote literal whose text starts at <paramref name="j"/>; adds its holes.
    /// </summary>
    static int ClosingQuote(string src, int j, bool verbatim, bool interpolated, List<(int From, int To)> holes)
    {
        while (j < src.Length)
        {
            char ch = src[j];
            char next = j + 1 < src.Length ? src[j + 1] : '\0';
            if (verbatim && ch == '"' && next == '"') j += 2;
            else if (!verbatim && ch == '\\') j += 2;
            else if (interpolated && ch == '{' && next == '{') j += 2;
            else if (interpolated && ch == '{') j = AddHole(src, j + 1, holes) + 1;
            else if (ch is '"' or '\n') return j;
            else j++;
        }

        return j;
    }

    /// <summary>
    /// Adds the hole whose code starts at <paramref name="from"/> and returns the offset of its closing brace.
    /// </summary>
    static int AddHole(string src, int from, List<(int From, int To)> holes)
    {
        int end = HoleEnd(src, from);
        holes.Add((from, end));
        return end;
    }

    /// <summary>
    /// The holes of a raw interpolated literal's text src[from, to) with <paramref name="dollars"/> dollar signs: code between that many braces.
    /// </summary>
    static List<(int From, int To)> RawHoles(string src, int from, int to, int dollars)
    {
        var holes = new List<(int From, int To)>();
        string open = new('{', dollars);
        int j = from;
        while ((j = src.IndexOf(open, j, StringComparison.Ordinal)) >= 0 && j < to)
            j = AddHole(src, j + dollars, holes) + dollars;

        return holes;
    }

    /// <summary>
    /// The text of a regular or verbatim literal's source.
    /// </summary>
    static string Decode(string raw, bool verbatim)
    {
        if (verbatim) return raw.Replace("\"\"", "\"");

        return Escape.Replace(raw, DecodeEscape);
    }

    /// <summary>
    /// The character one escape sequence stands for (an unknown escape keeps its letter).
    /// </summary>
    static string DecodeEscape(Match match)
    {
        string e = match.Groups[1].Value;
        if (e[0] is 'u' or 'x') return ((char)Convert.ToInt32(e.Substring(1), 16)).ToString();

        return e switch
        {
            "n" => "\n",
            "t" => "\t",
            "r" => "\r",
            "0" => "\0",
            _ => e,
        };
    }

    /// <summary>
    /// The text of a raw string literal: the lines inside the quotes, less the closing line's indentation.
    /// </summary>
    static string DedentRaw(string raw)
    {
        string[] lines = raw.Split('\n');
        if (lines.Length < 3) return raw;

        int indent = lines[lines.Length - 1].Length;
        IEnumerable<string> inner = lines
            .Skip(1)
            .Take(lines.Length - 2)
            .Select(l => l.Length > indent ? l.Substring(indent) : "");
        return string.Join("\n", inner);
    }
}
