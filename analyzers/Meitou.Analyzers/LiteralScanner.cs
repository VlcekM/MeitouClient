using System.Text.RegularExpressions;

namespace Meitou.Analyzers;

/// <summary>
/// The string literals of C# source and the source with comments and literal text blanked (line breaks and an interpolation's holes kept), as
/// .github/scripts/loc-strings.mjs scans it: literals inside interpolation holes included, regular literals joined by '+' merged.
/// </summary>
sealed class LiteralScanner
{
    /// <summary>
    /// Code ending in an opening bracket: what precedes an indexer's key.
    /// </summary>
    static readonly Regex BeforeKey = new(@"\[[ \t\r\n\f\v]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Code starting with a closing bracket: what follows an indexer's key.
    /// </summary>
    static readonly Regex AfterKey = new(@"^[ \t\r\n\f\v]*\]", RegexOptions.CultureInvariant);

    /// <summary>
    /// Only a '+' between two literals.
    /// </summary>
    static readonly Regex Concatenation = new(@"^[ \t\r\n\f\v]*\+[ \t\r\n\f\v]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// The source scanned.
    /// </summary>
    readonly string _src;

    /// <summary>
    /// The source with comments and literal text blanked.
    /// </summary>
    readonly char[] _masked;

    /// <summary>
    /// The offset each line starts at.
    /// </summary>
    readonly List<int> _lineStarts = [0];

    /// <summary>
    /// The literals found, in the order they were completed.
    /// </summary>
    readonly List<CSharpLiteral> _literals = [];

    /// <summary>
    /// Prepares a scan of <paramref name="src"/>.
    /// </summary>
    LiteralScanner(string src)
    {
        _src = src;
        _masked = src.ToCharArray();
        for (int k = 0; k < src.Length; k++)
            if (src[k] == '\n') _lineStarts.Add(k + 1);
    }

    /// <summary>
    /// The literals of <paramref name="src"/> sorted by offset, and the masked source.
    /// </summary>
    public static (List<CSharpLiteral> Literals, string Masked) Scan(string src)
    {
        var scanner = new LiteralScanner(src);
        scanner.ScanRange(0, src.Length);
        List<CSharpLiteral> sorted = scanner._literals
            .OrderBy(l => l.Start)
            .ToList();
        return (sorted, new string(scanner._masked));
    }

    /// <summary>
    /// Scans src[from, to) as code: blanks comments and literals, records literals and scans their holes as code.
    /// </summary>
    void ScanRange(int from, int to)
    {
        int i = from;
        CSharpLiteral? previous = null;
        while (i < to)
        {
            if (SkipCommentOrChar(i, to) is int skipped)
            {
                i = skipped;
                continue;
            }

            CSharpLiteral? literal = LiteralText.Read(_src, i);
            if (literal is null)
            {
                i++;
                continue;
            }

            Record(literal, from);
            previous = MergeOrAdd(previous, literal);
            i = literal.End;
        }
    }

    /// <summary>
    /// The offset past the comment or character literal at <paramref name="i"/>, blanked, or null when none starts there.
    /// </summary>
    int? SkipCommentOrChar(int i, int to)
    {
        if (string.CompareOrdinal(_src, i, "//", 0, 2) == 0)
        {
            int lineEnd = _src.IndexOf('\n', i);
            int end = Math.Min(lineEnd < 0 ? _src.Length : lineEnd, to);
            Blank(i, end);
            return end;
        }

        if (string.CompareOrdinal(_src, i, "/*", 0, 2) == 0)
        {
            int close = _src.IndexOf("*/", i, StringComparison.Ordinal);
            int end = close < 0 ? to : Math.Min(close + 2, to);
            Blank(i, end);
            return end;
        }

        if (_src[i] != '\'') return null;

        int charEnd = SkipChar(i);
        Blank(i + 1, charEnd - 1);
        return charEnd;
    }

    /// <summary>
    /// The offset after the character literal starting at <paramref name="i"/> (at a line break when it is not closed: an apostrophe in a
    /// #region title).
    /// </summary>
    int SkipChar(int i)
    {
        int j = i + 1;
        while (j < _src.Length && _src[j] != '\'' && _src[j] != '\n')
            j += _src[j] == '\\' ? 2 : 1;

        return j < _src.Length && _src[j] == '\'' ? j + 1 : j;
    }

    /// <summary>
    /// Sets the literal's line and whether it is an indexer key, blanks its text and scans its holes as code.
    /// </summary>
    void Record(CSharpLiteral literal, int from)
    {
        int i = literal.Start;
        literal.Line = LineOf(i);
        int before = Math.Max(from, i - 8);
        string after = _src.Substring(Math.Min(literal.End, _src.Length), Math.Max(0, Math.Min(8, _src.Length - literal.End)));
        literal.Indexer = BeforeKey.IsMatch(_src.Substring(before, i - before)) && AfterKey.IsMatch(after);

        int text = literal.Start + literal.Open;
        foreach ((int h0, int h1) in literal.Holes)
        {
            Blank(text, h0);
            ScanRange(h0, h1);
            text = h1;
        }

        Blank(text, literal.End - literal.Close);
    }

    /// <summary>
    /// Merges a regular literal that follows <paramref name="previous"/> after only a '+' into it, else adds it; returns the one to merge into next.
    /// </summary>
    CSharpLiteral MergeOrAdd(CSharpLiteral? previous, CSharpLiteral literal)
    {
        bool joined = previous is { Interpolated: false } && !literal.Interpolated
            && previous.End <= literal.Start
            && Concatenation.IsMatch(_src.Substring(previous.End, literal.Start - previous.End));
        if (!joined)
        {
            _literals.Add(literal);
            return literal;
        }

        previous!.Value += literal.Value;
        previous.Raw += literal.Raw;
        previous.End = literal.End;
        return previous;
    }

    /// <summary>
    /// Blanks src[from, to) in the masked source, line breaks kept.
    /// </summary>
    void Blank(int from, int to)
    {
        for (int k = Math.Max(0, from); k < Math.Min(to, _masked.Length); k++)
            if (_masked[k] != '\n') _masked[k] = ' ';
    }

    /// <summary>
    /// The 1-based line of offset <paramref name="pos"/>.
    /// </summary>
    int LineOf(int pos)
    {
        int lo = 0;
        int hi = _lineStarts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_lineStarts[mid] <= pos) lo = mid;
            else hi = mid - 1;
        }

        return lo + 1;
    }
}
