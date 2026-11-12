using System.Text;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// Reads and updates only the <c>language</c> line of the game's settings.cfg, keeping its encoding, line endings and every other line.
/// </summary>
internal static class KenshiLanguageSettings
{
    /// <summary>
    /// The value of <c>language</c> in the install's settings.cfg (trimmed), or null when the file or the key is missing or unreadable.
    /// </summary>
    public static string? GetLanguage(GameInstall install)
    {
        string path = Path.Combine(install.Root, "settings.cfg");
        try
        {
            if (!File.Exists(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            (Encoding encoding, int preambleLength) = DetectEncoding(bytes);
            string text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            return FindLanguage(text, out int valueStart, out int contentEnd) ? text[valueStart..contentEnd].Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes <c>language=<paramref name="code"/></c> into the install's settings.cfg, replacing the existing line or appending one.
    /// </summary>
    public static void SetLanguage(GameInstall install, string code)
    {
        string path = Path.Combine(install.Root, "settings.cfg");
        byte[] original = File.Exists(path) ? File.ReadAllBytes(path) : [];
        (Encoding encoding, int preambleLength) = DetectEncoding(original);
        string text = encoding.GetString(original, preambleLength, original.Length - preambleLength);
        string updated = ReplaceLanguage(text, code);
        byte[] body = encoding.GetBytes(updated);
        byte[] result = new byte[preambleLength + body.Length];
        original.AsSpan(0, preambleLength).CopyTo(result);
        body.CopyTo(result, preambleLength);
        File.WriteAllBytes(path, result);
    }

    /// <summary>
    /// <paramref name="text"/> with its <c>language</c> value replaced by <paramref name="code"/>, or the line appended when it has none.
    /// </summary>
    static string ReplaceLanguage(string text, string code)
    {
        if (FindLanguage(text, out int valueStart, out int contentEnd))
            return string.Concat(text.AsSpan(0, valueStart), code, text.AsSpan(contentEnd));
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : text.Contains('\n') ? "\n" : text.Contains('\r') ? "\r" : Environment.NewLine;
        string separator = text.Length == 0 || text.EndsWith('\n') || text.EndsWith('\r') ? "" : newline;
        return string.Concat(text, separator, "language=", code, newline);
    }

    /// <summary>
    /// The value span of the first <c>language=</c> line (after the <c>=</c> and its blanks, up to the line end).
    /// </summary>
    static bool FindLanguage(string text, out int valueStart, out int contentEnd)
    {
        for (int lineStart = 0; lineStart < text.Length;)
        {
            contentEnd = lineStart;
            while (contentEnd < text.Length && text[contentEnd] is not ('\r' or '\n')) contentEnd++;
            int equals = text.IndexOf('=', lineStart, contentEnd - lineStart);
            if (equals > lineStart && text[lineStart..equals].Trim().Equals("language", StringComparison.Ordinal))
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
    /// The text encoding of settings.cfg from its byte order mark (UTF-32, UTF-16 or UTF-8), and the mark's length; UTF-8 without one.
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
        return (new UTF8Encoding(false), 0);
    }
}
