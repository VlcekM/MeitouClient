using Meitou.Content;

namespace Meitou.Desktop;

/// <summary>
/// Writes this user's XDG desktop entries (the freedesktop.org Desktop Entry Specification), which give Linux desktops a program's name and icon.
/// </summary>
public static class XdgDesktopEntry
{
    /// <summary>
    /// The user's <c>applications</c> folder: under <c>$XDG_DATA_HOME</c>, else <c>~/.local/share</c>; null when neither is a full path.
    /// </summary>
    public static string? ApplicationsDirectory()
    {
        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? "";
        if (!Path.IsPathFullyQualified(dataHome))
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        return Path.IsPathFullyQualified(dataHome) ? Path.Combine(dataHome, "applications") : null;
    }

    /// <summary>
    /// Writes the entry <paramref name="text"/> to <paramref name="path"/> when it differs from what is there.
    /// </summary>
    public static void WriteEntry(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, text);
    }

    /// <summary>
    /// Copies <paramref name="icon"/> to <paramref name="path"/> when it differs from what is there; a missing resource writes nothing.
    /// </summary>
    public static void WriteIcon(string path, Stream? icon)
    {
        if (icon is null) return;

        using var buffer = new MemoryStream();
        icon.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Quotes a literal argument for an entry's Exec key, including escaping its field-code marker.
    /// </summary>
    public static string QuoteArgument(string argument)
    {
        string quoted = argument.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%");

        return "\"" + EscapeValue(quoted) + "\"";
    }

    /// <summary>
    /// Escapes the characters handled by the desktop entry value parser.
    /// </summary>
    public static string EscapeValue(string value) => value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}
