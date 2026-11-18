using System.Text;

namespace Meitou.Content;

/// <summary>
/// Writes a file whole or not at all: the bytes go to a file beside the target, which then takes the target's name in one step. A crash, a kill or
/// a full disk during the write leaves the old file as it was, where a write in place left a player's settings or mod list cut short. The bytes
/// are not forced to the disk before the rename (a pause on every settings change), so a power cut can still cost the new file.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// What the target's name gets for the file being written.
    /// </summary>
    const string Suffix = ".new";

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> in <paramref name="encoding"/> (null: UTF-8 without a byte order mark, as
    /// <see cref="File.WriteAllText(string, string?)"/>).
    /// </summary>
    public static void WriteAllText(string path, string text, Encoding? encoding = null)
    {
        if (encoding is null) Write(path, temp => File.WriteAllText(temp, text));
        else Write(path, temp => File.WriteAllText(temp, text, encoding));
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/>.
    /// </summary>
    public static void WriteAllBytes(string path, byte[] bytes) => Write(path, temp => File.WriteAllBytes(temp, bytes));

    /// <summary>
    /// Has <paramref name="write"/> write the file under its temporary name, then gives it <paramref name="path"/>'s. When either step fails the
    /// temporary file goes and the failure is the caller's, as a write in place's was.
    /// </summary>
    public static void Write(string path, Action<string> write)
    {
        string temp = path + Suffix;
        try
        {
            write(temp);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Deletes <paramref name="path"/> if it can.
    /// </summary>
    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The write's own failure is the one to report.
        }
    }
}
