namespace Meitou.Data.World;

/// <summary>
/// Keeps the foliage layout cache's folder in bounds (<see cref="FoliageLayoutCache"/>): abandoned temporary files, keys unused for long and, over the
/// byte cap, the least recently used keys and files go. Thread-safe.
/// </summary>
public sealed class FoliageLayoutCacheUpkeep
{
    /// <summary>
    /// A temporary file older than this is a write that never finished.
    /// </summary>
    static readonly TimeSpan AbandonedTemporary = TimeSpan.FromHours(1);
    /// <summary>
    /// Another key's folder untouched for this long is deleted.
    /// </summary>
    static readonly TimeSpan StaleKey = TimeSpan.FromDays(30);

    /// <summary>
    /// The folder of this key's files.
    /// </summary>
    readonly string _directory;
    /// <summary>
    /// How many hits and writes asked for a pass; the first starts it.
    /// </summary>
    int _maintained;

    /// <summary>
    /// Upkeep of the cache under <paramref name="root"/> whose own key is <paramref name="keyName"/> (its folder <paramref name="directory"/>).
    /// </summary>
    public FoliageLayoutCacheUpkeep(string root, string keyName, string directory, long maxBytes)
    {
        Root = root;
        KeyName = keyName;
        _directory = directory;
        MaxBytes = maxBytes;
    }

    /// <summary>
    /// The folder holding one folder per key.
    /// </summary>
    public string Root { get; }
    /// <summary>
    /// The name of this key's folder, kept by every pass.
    /// </summary>
    public string KeyName { get; }
    /// <summary>
    /// The most the files may total in bytes; 0 or less is no cap.
    /// </summary>
    public long MaxBytes { get; set; }

    /// <summary>
    /// Starts a background pass the first time the cache is used (a hit or a write).
    /// </summary>
    public void TouchOnce()
    {
        if (Interlocked.Increment(ref _maintained) == 1) MaintainInBackground();
    }

    /// <summary>
    /// A <see cref="Maintain"/> pass on a worker; failures are ignored.
    /// </summary>
    public void MaintainInBackground() => Task.Run(MaintainQuietly);

    /// <summary>
    /// Keeps the cache in bounds: abandoned temporary files go, keys unused for 30 days go (the folder of a key is touched when a start opens it for
    /// writing and on each pass), and when the files total more than <see cref="MaxBytes"/> the other keys go whole, least recently used first, then
    /// this key's oldest files, down to 90% of the cap. Returns (files, bytes before, files deleted).
    /// </summary>
    public (int Files, long Bytes, int Deleted) Maintain(DateTime? now = null)
    {
        if (!Directory.Exists(Root)) return default;
        var time = now ?? DateTime.UtcNow;
        int deleted = 0, files = 0;
        long total = 0;
        TouchOwnKey(time);

        var keys = new List<(DirectoryInfo Dir, long Bytes)>();
        foreach (var dir in new DirectoryInfo(Root).EnumerateDirectories())
        {
            long bytes = 0;
            foreach (var f in dir.EnumerateFiles())
            {
                if (f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    if (time - f.LastWriteTimeUtc > AbandonedTemporary && TryDelete(f)) deleted++;
                    continue;
                }
                if (!f.Extension.Equals(".mfl", StringComparison.OrdinalIgnoreCase)) continue;
                bytes += f.Length;
                files++;
            }
            if (dir.Name != KeyName && time - dir.LastWriteTimeUtc > StaleKey)
            {
                deleted += DeleteDirectory(dir);
                continue;
            }
            total += bytes;
            keys.Add((dir, bytes));
        }

        long before = total;
        if (MaxBytes > 0 && total > MaxBytes) deleted += Evict(keys, total);
        return (files, before, deleted);
    }

    /// <summary>
    /// The pass of <see cref="MaintainInBackground"/>, its file system failures ignored.
    /// </summary>
    void MaintainQuietly()
    {
        try
        {
            Maintain();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Marks this key's folder as used now; a folder that cannot be touched is left as it is.
    /// </summary>
    void TouchOwnKey(DateTime time)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(_directory, time);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Over the cap: the other keys go whole, least recently used first, then this key's oldest files, down to 90% of the cap. Returns the
    /// files deleted.
    /// </summary>
    int Evict(List<(DirectoryInfo Dir, long Bytes)> keys, long total)
    {
        int deleted = 0;
        long target = (long)(MaxBytes * 0.9);
        foreach (var (dir, bytes) in keys.Where(k => k.Dir.Name != KeyName).OrderBy(k => k.Dir.LastWriteTimeUtc))
        {
            if (total <= target) break;
            deleted += DeleteDirectory(dir);
            total -= bytes;
        }
        if (total <= target || keys.FirstOrDefault(k => k.Dir.Name == KeyName).Dir is not { } mine) return deleted;

        foreach (var f in mine.EnumerateFiles("*.mfl").OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= target) break;
            long length = f.Length;
            if (TryDelete(f))
            {
                total -= length;
                deleted++;
            }
        }
        return deleted;
    }

    /// <summary>
    /// Deletes a file; false when it cannot go.
    /// </summary>
    static bool TryDelete(FileInfo f)
    {
        try
        {
            f.Delete();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes a folder's files and then the folder; returns the files deleted.
    /// </summary>
    static int DeleteDirectory(DirectoryInfo dir)
    {
        int n = 0;
        foreach (var f in dir.EnumerateFiles()) if (TryDelete(f)) n++;
        try
        {
            dir.Delete();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return n;
    }
}
