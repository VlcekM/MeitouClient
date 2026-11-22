namespace Meitou.Analyzers;

/// <summary>
/// One place in the code or a launcher .axaml file where a text of Meitou's own needs a translation in every built-in catalog.
/// </summary>
sealed class LocMessage
{
    /// <summary>
    /// The msgctxt it is asked under (Loc.TrIn's first argument), or null.
    /// </summary>
    public string? Context { get; set; }

    /// <summary>
    /// The English text: the msgid.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// The English plural (Loc.Plural's second argument): the msgid_plural, or null for a singular message.
    /// </summary>
    public string? Plural { get; set; }

    /// <summary>
    /// Whether the launcher translates it here (a call or caption in a launcher file): the launcher reads no game catalog, so a text Kenshi's
    /// own catalog translates still needs Meitou's entry.
    /// </summary>
    public bool Launcher { get; set; }

    /// <summary>
    /// The offset of the text's first character in its file.
    /// </summary>
    public int Start { get; set; }

    /// <summary>
    /// The offset just past the text in its file.
    /// </summary>
    public int End { get; set; }
}
