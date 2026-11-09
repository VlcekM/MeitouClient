using Meitou.Content;

namespace Meitou.Localisation;

/// <summary>
/// The language the game runs in and the files it brings (docs/formats/localisation.md): chosen from the install's <c>settings.cfg</c>
/// <c>language</c> and <c>steam_language</c> and the folders under <c>locale/</c>, then replaced by the first mod that ships a translation.
/// </summary>
/// <param name="Code">The locale code (<c>en_GB</c>, <c>de_DE</c>, ...) of the texts shown: the code the user's setting selects, or a translation
/// mod's own.</param>
/// <param name="IsDefault">The built-in English (<c>en_GB</c>): no files are added.</param>
/// <param name="OverlayFolder"><c>locale/&lt;code&gt;</c> of the user's language, registered as a resource folder after the base game's and before
/// the mods'; null for English or a missing folder.</param>
/// <param name="MessagesDirectory">The <c>LC_MESSAGES</c> folder the source-code strings (<c>main.mo</c>) are read from; null when there is
/// none.</param>
/// <param name="CoreTranslationFile"><c>locale/&lt;code&gt;/&lt;code&gt;.translation</c>, applied after the core data files; null for English, a
/// mod's language or a missing file.</param>
/// <param name="ModTranslationFile">The first mod's <c>locale/&lt;code&gt;/&lt;code&gt;.translation</c>, applied after all mods; null when no mod has
/// one.</param>
/// <param name="ModName">The mod whose translation set <see cref="Code"/>; null when the user's setting did.</param>
public sealed record GameLanguage(string Code, bool IsDefault, string? OverlayFolder, string? MessagesDirectory, string? CoreTranslationFile,
    string? ModTranslationFile, string? ModName = null)
{
    /// <summary>
    /// The locale of the English the code is written in; the original ignores a <c>locale/en_GB</c> folder.
    /// </summary>
    public const string DefaultCode = "en_GB";

    /// <summary>
    /// A mod's translation replaced the user's setting.
    /// </summary>
    public bool FromMod => ModName is not null;

    /// <summary>
    /// English with nothing added.
    /// </summary>
    public static GameLanguage English { get; } = new(DefaultCode, true, null, null, null, null);

    /// <summary>
    /// The Steam language names the original maps to locale codes (<c>FUN_140175a90</c>), in table order. The table also lists the nine codes
    /// the launcher offers; folders under <c>locale/</c> that are not in it are accepted as further locales.
    /// </summary>
    public static IReadOnlyList<(string Steam, string Code)> SteamLanguages { get; } =
    [
        ("english", "en_GB"), ("german", "de_DE"), ("french", "fr_FR"), ("russian", "ru_RU"), ("italian", "it_IT"),
        ("portuguese", "pt_BR"), ("spanish", "es_ES"), ("chinese", "zh_CN"), ("japanese", "ja_JP"), ("korean", "ko_KR"),
    ];

    /// <summary>
    /// The language of the install with its enabled mods (<see cref="ModList.ReadEnabled"/> in <c>mods.cfg</c> order).
    /// </summary>
    public static GameLanguage Detect(GameInstall install)
    {
        var mods = ModList.FindMods(install);
        return Detect(install, ModList.ReadEnabled(install).Where(mods.ContainsKey).Select(n => Path.GetDirectoryName(mods[n])!));
    }

    /// <summary>
    /// The language of <paramref name="install"/> given the folders of its loaded mods in load order. Rules (<b>Verified</b>
    /// <c>FUN_140175a90</c> selection, <c>Ogre_RegisterResourceLocations</c> overlay and mod language, <c>Fcs_GameLoadSequence</c> translation
    /// files):
    /// <list type="number">
    /// <item><c>language</c> from settings.cfg picks a locale code; when it is empty the Steam name in <c>steam_language</c> does. The original
    /// accepts the table's codes and the folders under <c>locale/</c> not starting with <c>_</c>; Meitou also accepts a code it has built-in texts
    /// for (<see cref="MeitouTranslations.HasEmbedded"/>). Other unknown or absent values are English.</item>
    /// <item>A non-English choice with a <c>locale/&lt;code&gt;</c> folder registers it as an overlay; its <c>&lt;code&gt;.translation</c> follows
    /// the core files.</item>
    /// <item>The first mod in load order whose <c>locale/&lt;X&gt;</c> has <c>LC_MESSAGES/main.mo</c> or <c>X.translation</c> makes X the active
    /// language (messages from that mod, no core translation); the first mod with an existing <c>X.translation</c> supplies the translation applied
    /// after all mods.</item>
    /// </list>
    /// </summary>
    public static GameLanguage Detect(GameInstall install, IEnumerable<string> modFolders)
    {
        string localeRoot = Path.Combine(install.Root, "locale");
        var settings = ReadSettings(Path.Combine(install.Root, "settings.cfg"));
        string code = Choose(settings.GetValueOrDefault("language") ?? "", settings.GetValueOrDefault("steam_language") ?? "", localeRoot);
        bool isDefault = code == DefaultCode;
        string? folder = isDefault ? null : Path.Combine(localeRoot, code);
        if (folder is not null && !Directory.Exists(folder)) folder = null;

        string? messages = folder is not null && Directory.Exists(Path.Combine(folder, "LC_MESSAGES")) ? Path.Combine(folder, "LC_MESSAGES") : null;
        string? core = folder is not null && File.Exists(Path.Combine(folder, code + ".translation")) ? Path.Combine(folder,
                code + ".translation") : null;
        string? modTranslation = null, modName = null;
        bool languageFromMod = false;

        foreach (var mod in modFolders)
        {
            if (LocateModTranslation(mod) is not { } located) continue;
            if (!languageFromMod)
            {
                // The first mod with a translation replaces the user's language (messages from the mod's own folder, no core translation).
                languageFromMod = true;
                modName = Path.GetFileName(mod);
                code = located.Code;
                messages = Directory.Exists(Path.Combine(located.Folder, "LC_MESSAGES")) ? Path.Combine(located.Folder, "LC_MESSAGES") : null;
                core = null;
                isDefault = false;
            }
            if (modTranslation is null && File.Exists(located.TranslationFile))
                modTranslation = located.TranslationFile;
            if (modTranslation is not null) break;
        }
        return new(code, isDefault && !languageFromMod, folder, messages, core, modTranslation, languageFromMod ? modName : null);
    }

    /// <summary>
    /// <b>Verified</b> <c>Mods_LocateModTranslation</c> (<c>14086f240</c>): the first sub-folder X (sorted) of <c>&lt;mod&gt;/locale</c> that holds
    /// <c>LC_MESSAGES/main.mo</c> or <c>X.translation</c>. The gate on a flag of the mod entry at its start is <b>Unknown</b> (always clear for mods
    /// found by the scan).
    /// </summary>
    static (string Code, string Folder, string TranslationFile)? LocateModTranslation(string modFolder)
    {
        string root = Path.Combine(modFolder, "locale");
        if (!Directory.Exists(root)) return null;
        foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            string code = Path.GetFileName(folder), translation = Path.Combine(folder, code + ".translation");
            if (File.Exists(Path.Combine(folder, "LC_MESSAGES", "main.mo")) || File.Exists(translation))
                return (code, folder, translation);
        }
        return null;
    }

    /// <summary>
    /// <b>Verified</b> <c>FUN_140175a90</c>, without the Steam client: a non-empty <paramref name="language"/> is looked up among the table's codes
    /// and the folders of <c>locale/</c>; with it empty the Steam name decides. Not found: English. (With Steam running the original also lets a
    /// changed Steam language override <c>language</c>, and it writes the choice back to settings.cfg; Meitou does neither.)
    /// </summary>
    static string Choose(string language, string steamLanguage, string localeRoot)
    {
        if (language.Length == 0)
            return SteamLanguages.FirstOrDefault(s => s.Steam == steamLanguage).Code ?? DefaultCode;
        if (SteamLanguages.Any(s => s.Code == language)) return language;
        if (language[0] != '_' && Directory.Exists(Path.Combine(localeRoot, language))) return language;
        if (MeitouTranslations.HasEmbedded(language)) return language;
        return DefaultCode;
    }

    /// <summary>
    /// The <c>key=value</c> lines of settings.cfg (keys case-sensitive, values trimmed); empty when the file is missing.
    /// </summary>
    static Dictionary<string, string> ReadSettings(string file)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) result[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return result;
    }
}