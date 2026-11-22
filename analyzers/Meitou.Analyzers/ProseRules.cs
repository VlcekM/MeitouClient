using System.Text.RegularExpressions;

namespace Meitou.Analyzers;

/// <summary>
/// Which files a player reads and which literals read as words a player sees: the rules of .github/scripts/loc-strings.mjs (SCOPE, NOT_SHOWN,
/// hasWords, isProse, isDictionaryKey). JavaScript's \w, \b and \d are ASCII, so these patterns run in ECMAScript mode.
/// </summary>
static class ProseRules
{
    /// <summary>
    /// The options of every pattern: JavaScript's character classes (ECMAScript mode combines with IgnoreCase and Multiline only).
    /// </summary>
    const RegexOptions Js = RegexOptions.ECMAScript;

    /// <summary>
    /// Folders and files whose prose the player reads (repository-relative, '/' separators).
    /// </summary>
    static readonly Regex[] Scope =
    [
        new(@"^src/Meitou\.Game/(Settings|Title|Ui|Building|Tutorials|Mods|Host/Photo)/", Js),
        new(@"^src/Meitou\.Game/(SettingsMenu|PlayerInterface)[^/]*\.cs$", Js),
        new(@"^launcher/Meitou\.Launcher/", Js),
        new(@"^src/Meitou\.Game/Rendering/(Frame/(Enhancements|WorldFrame)|Post/(PostProcessOptions|Upscaling)|Upscalers/FrameGenerationSetup)\.cs$", Js),
        new(@"^src/Meitou\.(Core/Content/CoveredMods|Data/Textures/TextureQuality)\.cs$", Js),
    ];

    /// <summary>
    /// Lines whose literals are never shown: logs, exceptions, assertions, resource names, layout and widget properties.
    /// </summary>
    static readonly Regex NotShown = new(string.Join("|",
    [
        @"Console\.", @"\bthrow\b", @"Exception\(", @"\bDebug\.", @"\bTrace\.",
        @"\bAssert", @"nameof\(", @"\bRegex", @"GetManifest", @"DllImport",
        @"\bcon\.", @"usage:", @"BeginGuest|BeginNative", @"\.Get[A-Z]\w*\(""",
        @"\bFind\(", @"StartsWith|EndsWith", @"\bType ==|\bName ==", @"Diagnostic",
        @"\[""(TextAlign|FontName|ImageTexture|ImageName|Skin|Align|Colour)""\]", @"\bcase\b",
    ]), Js);

    /// <summary>
    /// A placeholder or interpolation hole: braces with no brace inside.
    /// </summary>
    static readonly Regex Hole = new(@"\{[^{}]*\}", Js);

    /// <summary>
    /// A word of two letters or more.
    /// </summary>
    static readonly Regex Word = new("[A-Za-z]{2,}", Js);

    /// <summary>
    /// A lower-case word followed by two spaces or more: a column layout, not a sentence.
    /// </summary>
    static readonly Regex Columns = new("^[a-z]+ {2,}", Js);

    /// <summary>
    /// A path, URL, colour or dotted identifier.
    /// </summary>
    static readonly Regex PathLike = new(@"\\|^\w+://|^#|^[a-z_]+\.[a-z_.]+$", Js | RegexOptions.IgnoreCase);

    /// <summary>
    /// A file name with one of the game's or Meitou's extensions.
    /// </summary>
    static readonly Regex FileName = new(@"^[\w.-]+\.(ttf|dds|png|mesh|xml|json|txt|cfg|mod|layout|mo|po|exe|dll|cs|wav|bnk|ogg|mp4)$",
        Js | RegexOptions.IgnoreCase);

    /// <summary>
    /// A single word a player reads: a capitalised word (hyphenated parts allowed) or an acronym of four letters or more.
    /// </summary>
    static readonly Regex SingleWord = new("^([A-Z][a-z]+(-[a-z]+)*|[A-Z]{4,})[.!?…:]?$", Js);

    /// <summary>
    /// A run of letters.
    /// </summary>
    static readonly Regex Letters = new("[A-Za-z]+", Js);

    /// <summary>
    /// Whether a repository-relative path is a file whose prose the player reads.
    /// </summary>
    public static bool IsPlayerFacing(string path)
    {
        return Scope.Any(s => s.IsMatch(path));
    }

    /// <summary>
    /// Whether a line of code (its literal blanked) never shows its literals.
    /// </summary>
    public static bool IsNotShown(string code)
    {
        return NotShown.IsMatch(code);
    }

    /// <summary>
    /// Whether <paramref name="text"/> holds a word outside its placeholders (a bare number format has nothing to translate).
    /// </summary>
    public static bool HasWords(string text)
    {
        return Word.IsMatch(Hole.Replace(text, " "));
    }

    /// <summary>
    /// Whether <paramref name="text"/> reads as words a player sees, not an identifier, path, format or code.
    /// </summary>
    public static bool IsProse(string text)
    {
        if (!HasWords(text)) return false;

        string words = Hole.Replace(text, "X").Trim();
        if (Columns.IsMatch(words) || PathLike.IsMatch(words)) return false;
        if (words.IndexOf(' ') < 0 && words.IndexOf('/') >= 0) return false;
        if (FileName.IsMatch(words)) return false;
        if (words.IndexOf(' ') < 0) return SingleWord.IsMatch(words);

        return Letters.Matches(words)
            .Cast<Match>()
            .Any(m => m.Length >= 3);
    }

    /// <summary>
    /// Whether the literal whose source is <paramref name="raw"/> is an indexer key on its line (["Name"] = ...): a property name, not text.
    /// </summary>
    public static bool IsDictionaryKey(string line, string raw)
    {
        return line.Contains("[\"" + raw + "\"]") || line.Contains("[$\"" + raw + "\"]");
    }
}
