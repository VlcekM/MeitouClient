namespace Meitou.Data.Ui;

/// <summary>
/// Original option descriptions from installed gettext source references; no game prose is embedded.
/// </summary>
public sealed class NewGameHelpCatalog
{
    /// <summary>
    /// The English descriptions by their source line in <c>kenshi/titlescreen.cpp</c> (lines 557 to 564).
    /// </summary>
    readonly Dictionary<int, string> _descriptions = [];
    /// <summary>
    /// The English description at source line <paramref name="line"/>; "" when the template lacks it.
    /// </summary>
    public string EnglishAt(int line) => _descriptions.GetValueOrDefault(line, "");
    /// <summary>
    /// The description at source line <paramref name="line"/> in the active language.
    /// </summary>
    public string TextAt(int line) => Loc.Tr(EnglishAt(line));

    /// <summary>
    /// <see cref="From"/> the gettext template at <paramref name="path"/>.
    /// </summary>
    public static NewGameHelpCatalog Read(string? path) => From(PotTemplate.Read(path));

    /// <summary>
    /// The catalog of the template's new game option descriptions: per source line, the longest msgid (a label shares its line with its description).
    /// </summary>
    public static NewGameHelpCatalog From(IEnumerable<PotEntry> entries)
    {
        var result = new NewGameHelpCatalog();
        // A label and its description share a source line; the description is the longer msgid.
        foreach (var entry in entries)
            foreach (var (file, line) in entry.References)
                if (file == "kenshi/titlescreen.cpp" && line is >= 557 and <= 564 && entry.Id.Length > result.EnglishAt(line).Length)
                    result._descriptions[line] = entry.Id;
        return result;
    }
}