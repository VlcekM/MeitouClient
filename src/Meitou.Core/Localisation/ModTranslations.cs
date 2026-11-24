using System.Text;

namespace Meitou.Localisation;

/// <summary>
/// A code mod's own gettext catalogues for the game's language (docs/modding.md "Translations"): each read from
/// <c>&lt;folder&gt;/locale/&lt;language&gt;/LC_MESSAGES/&lt;domain&gt;.mo</c> (else <c>.po</c>), looked up in the order they were added. Lookups by
/// the English source text, as the game's own catalogue is. Added to while the mod initializes; read on any thread after.
/// </summary>
public sealed class ModTranslations
{
    /// <summary>
    /// The catalogues in the order they were added; replaced whole, so a reader never sees a list being changed.
    /// </summary>
    volatile GettextCatalog[] _catalogs = [];

    /// <summary>
    /// Whether any catalogue was read: without one every text is shown as given.
    /// </summary>
    public bool Any => _catalogs.Length > 0;

    /// <summary>
    /// Reads the catalogue <paramref name="domain"/> of <paramref name="language"/> (a locale code, <see cref="GameLanguage.Code"/>) under
    /// <paramref name="folder"/>'s <c>locale</c>: the language's folder by its exact name ignoring case, else the first folder of the same language
    /// (<c>de</c> of <c>de_DE</c>). False when there is none or it cannot be read (logged).
    /// </summary>
    public bool Add(string folder, string domain, string language)
    {
        var read = Read(folder, domain, language);
        if (read is null) return false;

        _catalogs = [.. _catalogs, read];
        return true;
    }

    /// <summary>
    /// Adds the messages given directly (<see cref="GettextCatalog.Of"/>) as a catalogue of <paramref name="language"/>, when that is the game's
    /// <paramref name="gameLanguage"/>: the same code ignoring case, or the same language part before <c>_</c> (<c>de</c> of <c>de_DE</c>), as a
    /// catalogue folder is picked. False (nothing added) for another language.
    /// </summary>
    public bool Add(string language, string gameLanguage, IEnumerable<KeyValuePair<string, string>> messages, string? context = null)
    {
        var catalog = GettextCatalog.Of(messages, context);
        if (!SameLanguage(language, gameLanguage)) return false;

        _catalogs = [.. _catalogs, catalog];
        return true;
    }

    /// <summary>
    /// The translation of <paramref name="english"/> in the catalogues (first that has it), or null.
    /// </summary>
    public string? Find(string english, string? context = null)
    {
        foreach (var catalog in _catalogs)
            if (catalog.Find(english, context) is { } found) return found;
        return null;
    }

    /// <summary>
    /// The plural form for <paramref name="n"/> of <paramref name="singular"/> in the catalogues (first that has it, by its own plural rule), or
    /// null.
    /// </summary>
    public string? FindPlural(string singular, long n)
    {
        foreach (var catalog in _catalogs)
            if (catalog.FindPlural(singular, n) is { } found) return found;
        return null;
    }

    /// <summary>
    /// A text the mod shows on a game surface (a node, a menu row, a setting's label): its translation in the mod's catalogues, else unchanged.
    /// </summary>
    public string Text(string english) => english.Length == 0 || !Any ? english : Find(english) ?? english;

    /// <summary>
    /// <paramref name="english"/> in the game's language: the mod's catalogues, then the game's and Meitou's (<see cref="Loc.Tr(string)"/>), else
    /// unchanged.
    /// </summary>
    public string Tr(string english) => (Any ? Find(english) : null) ?? Loc.Tr(english);

    /// <summary>
    /// <paramref name="english"/> under the message context <paramref name="context"/>: the mod's entry for it, else <see cref="Loc.TrIn"/>.
    /// </summary>
    public string TrIn(string context, string english) => (Any ? Find(english, context) : null) ?? Loc.TrIn(context, english);

    /// <summary>
    /// The plural message for <paramref name="n"/>: the mod's form, else the game's (<see cref="Loc.Plural"/>), its placeholders filled with
    /// <paramref name="args"/>.
    /// </summary>
    public string Plural(string singular, string plural, long n, params object?[] args) =>
        Any && FindPlural(singular, n) is { } found ? Loc.Format(found, args) : Loc.Plural(singular, plural, n, args);

    /// <summary>
    /// Whether the locale code <paramref name="language"/> stands for <paramref name="gameLanguage"/>: the same code, else the same language part,
    /// case ignored.
    /// </summary>
    static bool SameLanguage(string language, string gameLanguage) =>
        language.Equals(gameLanguage, StringComparison.OrdinalIgnoreCase)
        || language.Split('_')[0].Equals(gameLanguage.Split('_')[0], StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The folder of <paramref name="language"/> under <paramref name="localeRoot"/>: its exact name ignoring case, else the first (ordinal order)
    /// whose language part before <c>_</c> is the same; null for none.
    /// </summary>
    static string? LanguageFolder(string localeRoot, string language)
    {
        if (!Directory.Exists(localeRoot)) return null;

        var folders = Directory.GetDirectories(localeRoot).Order(StringComparer.Ordinal).ToArray();
        string part = language.Split('_')[0];
        return folders.FirstOrDefault(f => Path.GetFileName(f).Equals(language, StringComparison.OrdinalIgnoreCase))
            ?? folders.FirstOrDefault(f => Path.GetFileName(f).Split('_')[0].Equals(part, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The entry <paramref name="name"/> of <paramref name="folder"/>, its case ignored (a file or folder); null for none.
    /// </summary>
    static string? Child(string folder, string name, bool file = false)
    {
        var entries = file ? Directory.GetFiles(folder) : Directory.GetDirectories(folder);
        return entries.Order(StringComparer.Ordinal).FirstOrDefault(e => Path.GetFileName(e).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The catalogue <paramref name="domain"/> of <paramref name="language"/> under <paramref name="folder"/>: its <c>.mo</c>, else its <c>.po</c>;
    /// null for none or one that cannot be read (logged).
    /// </summary>
    static GettextCatalog? Read(string folder, string domain, string language)
    {
        try
        {
            if (LanguageFolder(Path.Combine(folder, "locale"), language) is not { } languageFolder) return null;
            if (Child(languageFolder, "LC_MESSAGES") is not { } messages) return null;

            if (Child(messages, domain + ".mo", file: true) is { } mo)
                return GettextCatalog.ReadMo(File.ReadAllBytes(mo));
            if (Child(messages, domain + ".po", file: true) is { } po)
                return GettextCatalog.ReadPo(File.ReadAllText(po, Encoding.UTF8));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"locale    {folder} {domain}: {e.Message}; the mod's texts stay as written");
        }
        return null;
    }
}
