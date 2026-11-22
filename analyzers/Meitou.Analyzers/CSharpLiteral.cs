namespace Meitou.Analyzers;

/// <summary>
/// A string literal of C# source as .github/scripts/loc-strings.mjs reads it (regular, verbatim, interpolated or raw).
/// </summary>
sealed class CSharpLiteral
{
    /// <summary>
    /// The text the literal makes: escapes decoded, an interpolation's holes as written. The id meitou.built lists.
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>
    /// The source between the quotes.
    /// </summary>
    public string Raw { get; set; } = "";

    /// <summary>
    /// Whether the literal has a '$' (its text is built, so no catalog can match it).
    /// </summary>
    public bool Interpolated { get; set; }

    /// <summary>
    /// Whether the literal is alone between brackets: an indexer's key.
    /// </summary>
    public bool Indexer { get; set; }

    /// <summary>
    /// The 1-based line the literal starts on.
    /// </summary>
    public int Line { get; set; }

    /// <summary>
    /// The offset of the literal's first character (its '$', '@' or quote).
    /// </summary>
    public int Start { get; set; }

    /// <summary>
    /// The offset just past the literal's closing quote.
    /// </summary>
    public int End { get; set; }

    /// <summary>
    /// The length of the opening ($, @ and quotes).
    /// </summary>
    public int Open { get; set; }

    /// <summary>
    /// The length of the closing quotes.
    /// </summary>
    public int Close { get; set; }

    /// <summary>
    /// The spans [from, to) of the interpolation holes' code.
    /// </summary>
    public List<(int From, int To)> Holes { get; set; } = [];
}
