using System.Text.RegularExpressions;

namespace Meitou.Analyzers;

/// <summary>
/// The literals that translating calls take, as .github/scripts/loc-strings.mjs finds them in masked code (calls, argumentSpans, addCall,
/// isValue): every literal of the first argument (two for TrIn and Plural) that is a value the argument can be, not a key.
/// </summary>
sealed class TranslatingCalls
{
    /// <summary>
    /// A call that translates its arguments, up to its opening parenthesis: the name is in one of the groups.
    /// </summary>
    static readonly Regex Calls = new(
        @"(?:(?<![A-Za-z0-9_])Loc\.(Tr|TrIn|Plural|Mark|TrCaption)|(?<![A-Za-z0-9_])LauncherText\.(Translate)|(?<![A-Za-z0-9_.])(UpperTr|UpperCaption|Tr))"
        + @"[ \t\r\n\f\v]*\(",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Code starting with an arrow: the literal is a switch arm's pattern.
    /// </summary>
    static readonly Regex ArmPattern = new(@"^[ \t\r\n\f\v]*=>", RegexOptions.CultureInvariant);

    /// <summary>
    /// Code ending in an equality operator: the literal is a comparison's operand.
    /// </summary>
    static readonly Regex Comparison = new(@"[=!]=[ \t\r\n\f\v]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Code ending in a name or a generic's '>': the parenthesis after it opens another method's arguments.
    /// </summary>
    static readonly Regex Callee = new(@"[A-Za-z0-9_>][ \t\r\n\f\v]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// The literals of the source, sorted by offset.
    /// </summary>
    readonly List<CSharpLiteral> _literals;

    /// <summary>
    /// The source with comments and literal text blanked.
    /// </summary>
    readonly string _masked;

    /// <summary>
    /// Prepares the search of one source's calls.
    /// </summary>
    public TranslatingCalls(List<CSharpLiteral> literals, string masked)
    {
        _literals = literals;
        _masked = masked;
    }

    /// <summary>
    /// The offsets of the literals the calls take, and those the calls translate with their context and plural (interpolated ones are built text).
    /// </summary>
    public (HashSet<int> Used, List<CallText> Translated) Find()
    {
        var used = new HashSet<int>();
        var messages = new List<CallText>();
        foreach (Match call in Calls.Matches(_masked))
            AddCall(call, used, messages);

        return (used, messages);
    }

    /// <summary>
    /// Adds the literals of one call: TrIn's second argument's (its first is their context), Plural's first paired with its second's.
    /// </summary>
    void AddCall(Match call, HashSet<int> used, List<CallText> messages)
    {
        string name = call.Groups[1].Success ? call.Groups[1].Value : call.Groups[2].Success ? call.Groups[2].Value : call.Groups[3].Value;
        List<(int From, int To)> args = ArgumentSpans(call.Index + call.Length);
        List<CSharpLiteral> first = InArgument(args, 0);
        used.UnionWith(first.Select(l => l.Start));
        if (name is not ("TrIn" or "Plural"))
        {
            messages.AddRange(first.Select(l => new CallText(l, null, null)));
            return;
        }

        List<CSharpLiteral> second = InArgument(args, 1);
        used.UnionWith(second.Select(l => l.Start));
        string? context = first.FirstOrDefault()?.Value;
        if (name == "TrIn") messages.AddRange(second.Select(l => new CallText(l, context, null)));
        else messages.AddRange(PluralPairs(first, second));
    }

    /// <summary>
    /// Each of Plural's plural forms with its singular: the first argument's literal at the same index, or its first one.
    /// </summary>
    static IEnumerable<CallText> PluralPairs(List<CSharpLiteral> first, List<CSharpLiteral> second)
    {
        for (int k = 0; k < second.Count; k++)
        {
            CSharpLiteral? singular = k < first.Count ? first[k] : first.FirstOrDefault();
            if (singular is not null) yield return new CallText(singular, null, second[k].Value);
        }
    }

    /// <summary>
    /// The literals inside argument <paramref name="n"/> that are values of it.
    /// </summary>
    List<CSharpLiteral> InArgument(List<(int From, int To)> args, int n)
    {
        if (n >= args.Count) return [];

        (int from, int to) = args[n];
        return _literals
            .Where(l => l.Start >= from && l.End <= to && IsValue(l, from))
            .ToList();
    }

    /// <summary>
    /// The spans of the arguments of a call whose parenthesis opens just before <paramref name="i"/> (top-level commas split them).
    /// </summary>
    List<(int From, int To)> ArgumentSpans(int i)
    {
        var spans = new List<(int From, int To)>();
        int depth = 0;
        int from = i;
        for (int j = i; j < _masked.Length; j++)
        {
            char ch = _masked[j];
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}' && depth > 0) depth--;
            else if (ch == ',' && depth == 0)
            {
                spans.Add((from, j));
                from = j + 1;
            }
            else if (ch == ')' && depth == 0)
            {
                spans.Add((from, j));
                return spans;
            }
        }

        spans.Add((from, _masked.Length));
        return spans;
    }

    /// <summary>
    /// Whether a literal in an argument starting at <paramref name="from"/> is a value the argument can be, not a key: an indexer's key, a
    /// switch arm's pattern, a comparison's operand or another method's argument (props.GetValueOrDefault("Caption")) is not.
    /// </summary>
    bool IsValue(CSharpLiteral literal, int from)
    {
        if (literal.Indexer || ArmPattern.IsMatch(Slice(literal.End, literal.End + 8))) return false;
        if (Comparison.IsMatch(Slice(literal.Start - 8, literal.Start))) return false;

        int depth = 0;
        for (int j = literal.Start - 1; j >= from; j--)
        {
            char ch = _masked[j];
            if (ch is ')' or ']' or '}') depth++;
            else if (ch is '(' or '[' or '{')
            {
                if (depth == 0) return !(ch == '(' && Callee.IsMatch(Slice(j - 40, j)));
                depth--;
            }
        }

        return true;
    }

    /// <summary>
    /// masked[from, to), both clamped to the source.
    /// </summary>
    string Slice(int from, int to)
    {
        int start = Math.Max(0, Math.Min(from, _masked.Length));
        int end = Math.Max(start, Math.Min(to, _masked.Length));
        return _masked.Substring(start, end - start);
    }
}

/// <summary>
/// A literal a translating call translates, with the msgctxt it is asked under and the plural it is paired with (or null).
/// </summary>
readonly struct CallText(CSharpLiteral literal, string? context, string? plural)
{
    /// <summary>
    /// The literal: the msgid (or built text when interpolated).
    /// </summary>
    public CSharpLiteral Literal { get; } = literal;

    /// <summary>
    /// TrIn's context, or null.
    /// </summary>
    public string? Context { get; } = context;

    /// <summary>
    /// Plural's plural form, or null.
    /// </summary>
    public string? Plural { get; } = plural;
}
