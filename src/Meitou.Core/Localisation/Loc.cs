using System.Globalization;
using System.Text;
using Meitou.Content;

namespace Meitou.Localisation;

/// <summary>
/// The game's text localisation (docs/formats/localisation.md): the active <see cref="GameLanguage"/> and its gettext catalog, and the lookups that
/// turn the English source strings of the code into the player's language. Everything is the identity until <see cref="Use(GameInstall)"/> runs (and
/// stays so for English), so a string with no translation always shows its English text. The game's catalog translates the strings that exist as a
/// <c>msgid</c> in its <c>main.pot</c>; Meitou's own texts (<see cref="MeitouTranslations"/>) are looked up after it. Lookups are exact.
/// </summary>
public static class Loc
{
    /// <summary>
    /// The active language; English until <see cref="Use(GameLanguage)"/> runs.
    /// </summary>
    static GameLanguage s_language = GameLanguage.English;
    /// <summary>
    /// The game's catalog of the active language (<see cref="GettextCatalog.Empty"/> for English) and Meitou's own texts in it.
    /// </summary>
    static GettextCatalog s_catalog = GettextCatalog.Empty, s_meitou = GettextCatalog.Empty;
    /// <summary>
    /// The .NET culture numbers are written in for the active language; invariant until <see cref="Use(GameLanguage)"/> runs.
    /// </summary>
    static CultureInfo s_culture = CultureInfo.InvariantCulture;

    /// <summary>
    /// The active language (English until <see cref="Use(GameInstall)"/> ran).
    /// </summary>
    public static GameLanguage Language => s_language;

    /// <summary>
    /// True when a catalog is loaded, i.e. some strings may differ from their English source.
    /// </summary>
    public static bool Translating => s_catalog.Count > 0 || s_meitou.Count > 0;

    /// <summary>
    /// Selects the language of <paramref name="install"/> and loads its message catalog.
    /// </summary>
    public static void Use(GameInstall install) => Use(GameLanguage.Detect(install));

    /// <summary>
    /// Makes <paramref name="selected"/> the active language and loads its message catalog.
    /// </summary>
    public static void Use(GameLanguage selected)
    {
        var loaded = GettextCatalog.Load(selected.MessagesDirectory);
        var own = MeitouTranslations.Load(selected.Code, selected.OverlayFolder);
        s_language = selected;
        s_catalog = loaded;
        s_meitou = own;
        s_culture = CultureOf(selected.Code);
    }

    /// <summary>
    /// <paramref name="english"/> in the active language: the game's translation, else Meitou's own, else unchanged (so a text that was never a
    /// <c>msgid</c> passes through).
    /// </summary>
    public static string Tr(string english) => !Translating ? english : s_catalog.Find(english) ?? s_meitou.Find(english) ?? english;

    /// <summary>
    /// <paramref name="english"/> under the message context <paramref name="context"/> (<c>msgctxt</c>, <c>pgettext</c>): a Meitou text whose msgid
    /// the game's catalog already translates with another sense ("Loaded" is its ranged-weapon label). The context's entry in either catalog, else
    /// <see cref="Tr(string)"/>.
    /// </summary>
    public static string TrIn(string context, string english) =>
        !Translating ? english : s_catalog.Find(english, context) ?? s_meitou.Find(english, context) ?? Tr(english);

    /// <summary>
    /// <see cref="Tr(string)"/> followed by <see cref="Format"/>: the boost-style placeholders <c>{1}</c>, <c>{1,num}</c> and <c>{1,num=fix,p=2}</c>
    /// of the pot (1-based), so a translation may reorder them.
    /// </summary>
    public static string Tr(string english, params object?[] args) => Format(Tr(english), args);

    /// <summary>
    /// A UI layout's caption in the active language (<c>docs/native-ui.md</c>): the exact <c>msgid</c>, else the same text ignoring case — the
    /// layouts caption a section in capitals (<c>"SKILLS"</c>, <c>"WEAPON I"</c>) while the catalog keys are written normally (<c>"Skills"</c>,
    /// <c>"Weapon I"</c>). Anything with no translation passes through.
    /// </summary>
    public static string TrCaption(string english)
    {
        if (!Translating || english.Length == 0) return english;
        return s_catalog.Find(english) ?? s_meitou.Find(english) ?? s_catalog.FindIgnoringCase(english) ?? s_meitou.FindIgnoringCase(english)
                ?? english;
    }

    /// <summary>
    /// The plural message <paramref name="singular"/> / <paramref name="plural"/> for <paramref name="n"/> (<c>ngettext</c>): the catalog's form for
    /// <paramref name="n"/> by its <c>Plural-Forms</c> rule, else the English one. <paramref name="args"/> fill the placeholders (usually
    /// <paramref name="n"/> itself).
    /// </summary>
    public static string Plural(string singular, string plural, long n, params object?[] args) =>
        Format((Translating ? s_catalog.FindPlural(singular, n) ?? s_meitou.FindPlural(singular, n) : null) ?? (n == 1 ? singular : plural), args);

    /// <summary>
    /// Substitutes <c>{N}</c>, <c>{N,num}</c> and <c>{N,num=fix,p=K}</c> (N is 1-based) with <paramref name="args"/>: <c>num</c> writes a number in
    /// the language's culture, <c>fix</c> with <c>p</c> fixes the digits after the point, <c>p</c> alone keeps that many significant digits. Text
    /// that is not a placeholder is kept.
    /// </summary>
    public static string Format(string pattern, params object?[] args)
    {
        if (args.Length == 0 || pattern.IndexOf('{') < 0) return pattern;
        var result = new StringBuilder(pattern.Length + 16);
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            int close;
            if (c != '{' || (close = pattern.IndexOf('}', i + 1)) < 0)
            {
                result.Append(c);
                continue;
            }
            var spec = pattern.AsSpan(i + 1, close - i - 1);
            int comma = spec.IndexOf(',');
            var number = comma < 0 ? spec : spec[..comma];
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 1 || index > args.Length)
            {
                result.Append(c);
                continue;
            }
            result.Append(Render(args[index - 1], comma < 0 ? default : spec[(comma + 1)..]));
            i = close;
        }
        return result.ToString();
    }

    /// <summary>
    /// One placeholder's value as text with its <paramref name="options"/> (<c>num</c>, <c>fix</c>, <c>p=K</c>): strings as they are, numbers in
    /// <see cref="s_culture"/> when <c>num</c> is given, else invariant.
    /// </summary>
    static string Render(object? value, ReadOnlySpan<char> options)
    {
        if (value is null) return "";
        if (value is string text) return text;
        if (options.IsEmpty)
            return value is IFormattable plain ? plain.ToString(null, CultureInfo.InvariantCulture) : value.ToString() ?? "";
        bool num = false, fix = false;
        int precision = -1;
        foreach (var option in options.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (option == "num") num = true;
            else if (option == "num=fix" || option == "fix")
            {
                num = true;
                fix = true;
            }
            else if (option.StartsWith("p=", StringComparison.Ordinal) && int.TryParse(option.AsSpan(2), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int p)) precision = p;
        }
        if (!num || value is not IFormattable number) return value.ToString() ?? "";
        if (fix && precision >= 0)
            return number.ToString("F" + precision.ToString(CultureInfo.InvariantCulture), s_culture);
        // Without fix, p is the significant digits (the stream's general format).
        if (precision > 0 && value is float or double or decimal)
            return number.ToString("G" + precision.ToString(CultureInfo.InvariantCulture), s_culture);
        return value is float or double or decimal ? number.ToString("#,0.######", s_culture) : number.ToString("N0", s_culture);
    }

    /// <summary>
    /// The .NET culture of a locale code like <c>de_DE</c>; the invariant culture when the runtime does not know it.
    /// </summary>
    static CultureInfo CultureOf(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code.Replace('_', '-'));
        }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    /// <summary>
    /// The path of text file <paramref name="name"/> (<c>tips.txt</c>, <c>namesM.txt</c>): the active language's own copy in
    /// <c>locale/&lt;code&gt;</c> when it has one, else <paramref name="dataDirectory"/>'s (the order the game's resource lookup gives, base then
    /// language folder). Null when neither exists.
    /// </summary>
    public static string? FindTextFile(string dataDirectory, string name)
    {
        if (s_language.OverlayFolder is { } folder && Path.Combine(folder, name) is var own && File.Exists(own))
            return own;
        var basic = Path.Combine(dataDirectory, name);
        return File.Exists(basic) ? basic : null;
    }
}