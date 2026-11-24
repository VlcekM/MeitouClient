namespace Meitou.Game.Rendering.Ui;

/// <summary>
/// The font files the interface draws a character from when a text's own font lacks it (docs/formats/mygui.md "Fonts"; Meitou's, not in the
/// original): every <see cref="UiFont"/> asks them, in the order they were added, before its substitute glyph. Mods add them
/// (<see cref="Ui.UiRegistration.AddFallbackFont"/>); there are none by default, and with none every font draws as before. Files are only ever
/// added, for the life of the process.
/// </summary>
internal static class UiFontFallbacks
{
    /// <summary>
    /// Taken while the list changes.
    /// </summary>
    static readonly object Lock = new();

    /// <summary>
    /// The full paths added, in order; replaced whole on each change, so a reader takes it without the lock.
    /// </summary>
    static string[] s_paths = [];

    /// <summary>
    /// Raised on each change of <see cref="Paths"/>.
    /// </summary>
    static int s_version;

    /// <summary>
    /// The paths whose failure to open was reported.
    /// </summary>
    static readonly HashSet<string> Reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The full paths of the files added, in order.
    /// </summary>
    public static string[] Paths => Volatile.Read(ref s_paths);

    /// <summary>
    /// Changes whenever a file is added: a font that was asked for characters before forgets those it could not draw.
    /// </summary>
    public static int Version => Volatile.Read(ref s_version);

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
            Volatile.Write(ref s_version, s_version + 1);
            return true;
        }
    }

    /// <summary>
    /// Logs once per file that <paramref name="path"/> cannot be drawn from, and why.
    /// </summary>
    public static void ReportUnusable(string path, string why)
    {
        lock (Lock)
        {
            if (!Reported.Add(path)) return;
        }

        Console.Error.WriteLine($"ui        fallback font {path} is not used: {why}.");
    }
}
