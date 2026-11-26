using System.Xml;
using System.Xml.Linq;
using Meitou.Data.Ui;

namespace Meitou.Game.Rendering.Ui;

/// <summary>
/// The font files the interface draws a character from when a text's own font lacks it (docs/formats/mygui.md "Fonts"; Meitou's, not in the
/// original): every <see cref="UiFont"/> asks them, in the order they were added, before its substitute glyph. Mods add them
/// (<see cref="Ui.UiRegistration.AddFallbackFont"/>); in Meitou mode the Kenshi folder's Chinese, Japanese and Korean fonts follow
/// (<see cref="UseInstall"/>), in Faithful mode only the mods' are asked. Files are only ever added, for the life of the process.
/// </summary>
internal static class UiFontFallbacks
{
    /// <summary>
    /// The languages whose standard font Meitou mode falls back to, in this order: Chinese first (most mods in a script the English fonts lack
    /// are Chinese), then the kana and Hangul the Chinese font may lack.
    /// </summary>
    static readonly string[] InstallLanguages = ["zh_CN", "ja_JP", "ko_KR"];

    /// <summary>
    /// The resource whose font file each language's fallback is.
    /// </summary>
    const string InstallFontResource = "Kenshi_StandardFont_Medium";

    /// <summary>
    /// Taken while the list changes.
    /// </summary>
    static readonly object Lock = new();

    /// <summary>
    /// The full paths added, in order; replaced whole on each change, so a reader takes it without the lock.
    /// </summary>
    static string[] s_paths = [];

    /// <summary>
    /// The Kenshi folder's language fonts (<see cref="UseInstall"/>), asked after <see cref="s_paths"/> in Meitou mode; set once at start.
    /// </summary>
    static string[] s_installPaths = [];

    /// <summary>
    /// The paths that could not be used (reported once); no font tries them again.
    /// </summary>
    static readonly HashSet<string> Unusable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The full paths of the files added, in order.
    /// </summary>
    public static string[] Paths => Volatile.Read(ref s_paths);

    /// <summary>
    /// Changes whenever a file is added (the lists only grow, so their lengths): a font that was asked for characters before forgets those it
    /// could not draw, and text painted before is painted again.
    /// </summary>
    public static int Version => Paths.Length + Volatile.Read(ref s_installPaths).Length;

    /// <summary>
    /// The files a font draws a missing character from, in order: the mods' and, unless <paramref name="faithful"/>, then the install's.
    /// </summary>
    public static string[] For(bool faithful)
    {
        var paths = Paths;
        var install = Volatile.Read(ref s_installPaths);
        if (faithful || install.Length == 0) return paths;
        return [.. paths, .. install];
    }

    /// <summary>
    /// Adds the standard font of each of <see cref="InstallLanguages"/> under the Kenshi folder <paramref name="installRoot"/>
    /// (<c>locale/&lt;code&gt;/gui/fonts/kenshi_fonts.xml</c>'s <see cref="InstallFontResource"/>) as Meitou mode's last fallbacks; a language
    /// without the folder, the resource or its file is left out.
    /// </summary>
    public static void UseInstall(string installRoot)
    {
        var found = new List<string>();
        foreach (var language in InstallLanguages)
        {
            if (InstallFont(Path.Combine(installRoot, "locale", language, "gui", "fonts")) is { } path) found.Add(path);
        }

        Volatile.Write(ref s_installPaths, [.. found]);
    }

    /// <summary>
    /// Adds font file <paramref name="path"/> after those added already; false when it was added already.
    /// </summary>
    public static bool Add(string path)
    {
        string full = Path.GetFullPath(path);
        lock (Lock)
        {
            foreach (var known in s_paths)
            {
                if (string.Equals(known, full, StringComparison.OrdinalIgnoreCase)) return false;
            }

            Volatile.Write(ref s_paths, [.. s_paths, full]);
            return true;
        }
    }

    /// <summary>
    /// The full path of the <see cref="InstallFontResource"/> font file in language font folder <paramref name="folder"/>; null when it has none.
    /// </summary>
    static string? InstallFont(string folder)
    {
        string fonts = Path.Combine(folder, "kenshi_fonts.xml");
        if (!File.Exists(fonts)) return null;

        XDocument document;
        try
        {
            document = XDocument.Load(fonts);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }

        var resource = document.Descendants("Resource").FirstOrDefault(r => (string?)r.Attribute("name") == InstallFontResource);
        if (resource is null || MyGuiTrueTypeFont.Read(resource) is not { } font) return null;

        string path = Path.GetFullPath(Path.Combine(folder, font.Source));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> was found unusable (<see cref="ReportUnusable"/>).
    /// </summary>
    public static bool IsUnusable(string path)
    {
        lock (Lock) return Unusable.Contains(path);
    }

    /// <summary>
    /// Marks <paramref name="path"/> unusable for every font and logs once that it cannot be drawn from, and why.
    /// </summary>
    public static void ReportUnusable(string path, string why)
    {
        lock (Lock)
        {
            if (!Unusable.Add(path)) return;
        }

        Console.Error.WriteLine($"ui        fallback font {path} is not used: {why}.");
    }
}
