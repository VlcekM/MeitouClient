namespace Meitou.Analyzers;

/// <summary>
/// src/Meitou.Core/Localisation/locale/meitou.ignore: prose the code holds that is not Meitou's to translate, in two sections split at the
/// '# Not shown' line: texts Kenshi's own catalog translates, and texts never shown (widget, skin, icon, font, brand and record names).
/// </summary>
sealed class IgnoreList
{
    /// <summary>
    /// The texts Kenshi's own catalog translates (a Meitou entry cannot override a game msgid).
    /// </summary>
    readonly HashSet<string> _game;

    /// <summary>
    /// The texts never shown.
    /// </summary>
    readonly HashSet<string> _notShown;

    /// <summary>
    /// Reads the list from its text (null: an empty list).
    /// </summary>
    public IgnoreList(string? text)
    {
        string[] lines = text?.Split('\n') ?? [];
        int split = Array.FindIndex(lines, l => l.StartsWith("# Not shown", StringComparison.Ordinal));
        _game = Entries(split < 0 ? lines : lines.Take(split));
        _notShown = split < 0 ? [] : Entries(lines.Skip(split));
    }

    /// <summary>
    /// Whether a message without a context is on the list: a game text only where the launcher does not show it (it reads no game catalog).
    /// </summary>
    public bool Ignores(LocMessage message)
    {
        if (message.Context is not null) return false;

        return _notShown.Contains(message.Id) || (_game.Contains(message.Id) && !message.Launcher);
    }

    /// <summary>
    /// A section's texts: one per line ('\n' for a line break), '#' lines and blank lines skipped.
    /// </summary>
    static HashSet<string> Entries(IEnumerable<string> lines)
    {
        IEnumerable<string> entries = lines
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Replace("\\n", "\n"));
        return new HashSet<string>(entries, StringComparer.Ordinal);
    }
}
