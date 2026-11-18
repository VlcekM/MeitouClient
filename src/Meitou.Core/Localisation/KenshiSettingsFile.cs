using System.Text;
using Meitou.Content;

namespace Meitou.Localisation;

/// <summary>
/// Reads and updates single <c>key=value</c> lines of the game's settings.cfg, keeping its encoding, line endings and every other line. The game
/// sets a key the same way: it replaces the line of that key or appends one (<b>Verified</b> <c>FUN_14017e1d0</c>).
/// </summary>
public static class KenshiSettingsFile
{
    /// <summary>
    /// The value of <paramref name="key"/> in the install's settings.cfg (trimmed), or null when the file or the key is missing or unreadable.
    /// </summary>
    public static string? Get(GameInstall install, string key)
    {
        string path = PathOf(install);
        try
        {
            if (!File.Exists(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            (Encoding encoding, int preambleLength) = DetectEncoding(bytes);
            string text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            return Find(text, key, out int valueStart, out int contentEnd) ? text[valueStart..contentEnd].Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes each <c>key=value</c> of <paramref name="values"/> into the install's settings.cfg, replacing the key's line or appending one; the
    /// file is written only when that changes it.
    /// </summary>
    public static void Set(GameInstall install, params ReadOnlySpan<(string Key, string Value)> values)
    {
        string path = PathOf(install);
        byte[] original = File.Exists(path) ? File.ReadAllBytes(path) : [];
        (Encoding encoding, int preambleLength) = DetectEncoding(original);
        string text = encoding.GetString(original, preambleLength, original.Length - preambleLength);
        string updated = text;
        foreach (var (key, value) in values) updated = Replace(updated, key, value);
        if (updated == text && original.Length > 0) return;

        byte[] body = encoding.GetBytes(updated);
        byte[] result = new byte[preambleLength + body.Length];
        original.AsSpan(0, preambleLength).CopyTo(result);
        body.CopyTo(result, preambleLength);
        AtomicFile.WriteAllBytes(path, result);
    }

    /// <summary>
    /// The install's settings.cfg.
    /// </summary>
    static string PathOf(GameInstall install) => Path.Combine(install.Root, "settings.cfg");

    /// <summary>
    /// <paramref name="text"/> with the value of <paramref name="key"/> replaced by <paramref name="value"/>, or the line appended when it has none.
    /// </summary>
    static string Replace(string text, string key, string value)
    {
        if (Find(text, key, out int valueStart, out int contentEnd))
            return string.Concat(text.AsSpan(0, valueStart), value, text.AsSpan(contentEnd));

        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : text.Contains('\n') ? "\n"
            : text.Contains('\r') ? "\r"
            : Environment.NewLine;
        string separator = text.Length == 0 || text.EndsWith('\n') || text.EndsWith('\r') ? "" : newline;
        return string.Concat(text, separator, key, "=", value, newline);
    }

    /// <summary>
    /// The value span of the first line of <paramref name="key"/> (after the <c>=</c> and its blanks, up to the line end).
    /// </summary>
    static bool Find(string text, string key, out int valueStart, out int contentEnd)
    {
        for (int lineStart = 0; lineStart < text.Length;)
        {
            contentEnd = lineStart;
            while (contentEnd < text.Length && text[contentEnd] is not ('\r' or '\n')) contentEnd++;
            int equals = text.IndexOf('=', lineStart, contentEnd - lineStart);
            if (equals > lineStart && text[lineStart..equals].Trim().Equals(key, StringComparison.Ordinal))
            {
                valueStart = equals + 1;
                while (valueStart < contentEnd && char.IsWhiteSpace(text[valueStart])) valueStart++;
                return true;
            }
            if (contentEnd == text.Length) break;
            lineStart = contentEnd + (text[contentEnd] == '\r' && contentEnd + 1 < text.Length && text[contentEnd + 1] == '\n' ? 2 : 1);
        }
        valueStart = contentEnd = -1;
        return false;
    }

    /// <summary>
    /// The text encoding of settings.cfg from its byte order mark (UTF-32, UTF-16 or UTF-8), and the mark's length; without one Latin-1, so every
    /// byte of a line it does not change is written back as it was.
    /// </summary>
    static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            return (new UTF32Encoding(true, false), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            return (new UTF32Encoding(false, false), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new UTF8Encoding(false), 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return (Encoding.BigEndianUnicode, 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return (Encoding.Unicode, 2);
        return (Encoding.Latin1, 0);
    }
}
