namespace Meitou.Data.Ui;

/// <summary>
/// Original option descriptions from installed gettext source references; no game prose is embedded.
/// </summary>
public sealed class NewGameHelpCatalog
{
    readonly Dictionary<int, string> _descriptions = [];
    public string EnglishAt(int line) => _descriptions.GetValueOrDefault(line, "");
    public string TextAt(int line) => Loc.Tr(EnglishAt(line));

    public static NewGameHelpCatalog Read(string? path) => From(PotTemplate.Read(path));

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