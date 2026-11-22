namespace Meitou.Analyzers;

/// <summary>
/// One entry of a .po file.
/// </summary>
sealed class PoEntry
{
    /// <summary>
    /// The msgctxt, or null.
    /// </summary>
    public string? Context { get; set; }

    /// <summary>
    /// The msgid, or null before its line is read (the header's is empty).
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// The msgid_plural, or null for a singular entry.
    /// </summary>
    public string? Plural { get; set; }

    /// <summary>
    /// The translated forms (msgstr, or msgstr[0], msgstr[1], ...); a form not given is null.
    /// </summary>
    public List<string?> Forms { get; } = [];

    /// <summary>
    /// The comment lines, flags included.
    /// </summary>
    public List<string> Comments { get; } = [];
}
