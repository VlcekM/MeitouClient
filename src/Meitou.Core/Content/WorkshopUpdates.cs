using System.Text.RegularExpressions;
using Meitou.Steam;

namespace Meitou.Content;

/// <summary>
/// When Steam last updated each installed Kenshi workshop item: the <c>timeupdated</c> of its entry in <c>appworkshop_233860.acf</c>
/// (<c>steamapps/workshop</c>, beside <c>content/233860/&lt;item&gt;</c>; a Valve key-value text file, <b>Observed</b> on a Windows Steam
/// library with 43 items: each item has a block under <c>WorkshopItemsInstalled</c> holding <c>size</c>, <c>timeupdated</c> (Unix seconds)
/// and <c>manifest</c>, and another under <c>WorkshopItemDetails</c>; only the installed section is read). Each file is read once per change
/// of its write time; a file that cannot be read (Steam writing it, no access) gives no times and is tried again next time.
/// </summary>
public static partial class WorkshopUpdates
{
    /// <summary>
    /// The update times read from each record file, by its path, with the file's write time they were read at.
    /// </summary>
    static readonly Dictionary<string, (DateTime Written, Dictionary<ulong, long> Updated)> ReadFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When Steam last updated workshop item <paramref name="item"/>, whose folder is <paramref name="folder"/> (Unix seconds); null when the
    /// record file cannot be read or has no entry for the item.
    /// </summary>
    public static long? TimeUpdated(string folder, ulong item)
    {
        string file = Path.GetFullPath(Path.Combine(folder, "..", "..", "..", $"appworkshop_{MacSteam.KenshiAppId}.acf"));
        lock (ReadFiles)
        {
            if (Read(file) is not { } updated) return null;

            return updated.TryGetValue(item, out long time) ? time : null;
        }
    }

    /// <summary>
    /// The times of record file <paramref name="file"/>, from the cache while its write time is unchanged; null when it is missing or cannot be
    /// read. A read during which the file changed is used but not cached.
    /// </summary>
    static Dictionary<ulong, long>? Read(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;

            var written = File.GetLastWriteTimeUtc(file);
            if (ReadFiles.TryGetValue(file, out var cached) && cached.Written == written) return cached.Updated;

            string text;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                text = reader.ReadToEnd();
            }

            var updated = Parse(text);
            if (File.GetLastWriteTimeUtc(file) == written) ReadFiles[file] = (written, updated);
            return updated;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Each item's <c>timeupdated</c> in the <c>WorkshopItemsInstalled</c> section of a record file's text (the whole text when the section's
    /// end is not found); an item without one is left out.
    /// </summary>
    static Dictionary<ulong, long> Parse(string text)
    {
        int start = text.IndexOf("\"WorkshopItemsInstalled\"", StringComparison.Ordinal);
        int end = text.IndexOf("\"WorkshopItemDetails\"", Math.Max(start, 0), StringComparison.Ordinal);
        string installed = start >= 0 && end > start ? text[start..end] : text;

        var updated = new Dictionary<ulong, long>();
        foreach (Match m in ItemBlock().Matches(installed))
        {
            if (ulong.TryParse(m.Groups["id"].Value, out ulong id) && long.TryParse(m.Groups["time"].Value, out long time))
                updated.TryAdd(id, time);
        }

        return updated;
    }

    /// <summary>
    /// An item's block whose keys include <c>timeupdated</c>: the quoted item id, an opening brace, then the key before the block closes.
    /// </summary>
    [GeneratedRegex(@"""(?<id>\d+)""\s*\{[^{}]*?""timeupdated""\s*""(?<time>\d+)""")]
    private static partial Regex ItemBlock();
}
