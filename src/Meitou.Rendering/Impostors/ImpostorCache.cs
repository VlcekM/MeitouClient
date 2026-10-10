using System.Globalization;

namespace Meitou.Rendering.Impostors;

/// <summary>What one <see cref="ImpostorCache.Maintain"/> pass did.</summary>
public readonly record struct ImpostorCacheMaintenance(int Files, long Bytes, int Stale, int Evicted, long BytesAfter);

/// <summary>
/// The disk cache of baked atlases: <c>%LOCALAPPDATA%\Meitou\impostors\&lt;name&gt;_&lt;key&gt;.mimp</c> (overridden by <c>MEITOU_IMPOSTOR_CACHE</c> or the
/// constructor; never inside the repository). Files are written to a temporary name and moved into place, so a crash leaves no half file.
/// <para>
/// The cache is kept in bounds (<see cref="Maintain"/>, docs/impostors.md section 4): files of an older format or baker version (their keys
/// never match again), unreadable ones and abandoned temporary files are deleted, and when the files total more than <see cref="MaxBytes"/>
/// the least recently used go first. A file's last use is its last write time: <see cref="TryLoad"/> and <see cref="Save"/> set it.
/// </para>
/// </summary>
public sealed class ImpostorCache
{
    /// <summary>The default cap, 4096 MB: the 267 base-game atlases were 376 MB, but rocks are baked per biome and a long flight wanted more than 512 MB, so the old cap
    /// evicted and re-baked atlases on every trip (docs/impostors.md section 3).</summary>
    public const long DefaultMaxBytes = 4096L << 20;
    /// <summary>A temporary file older than this was left by a crashed process.</summary>
    static readonly TimeSpan AbandonedTemporary = TimeSpan.FromHours(1);
    /// <summary>Eviction goes down to this share of the cap, so it does not run again after every save.</summary>
    const double EvictTo = 0.9;
    /// <summary>A maintenance pass runs after this many saves.</summary>
    const int SavesPerPass = 16;

    readonly object gate = new();
    int saves, running;

    public ImpostorCache(string? root = null, long? maxBytes = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_CACHE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "impostors");
        MaxBytes = maxBytes ?? FromEnvironment();
    }

    public string Root { get; }

    /// <summary>The most the files may total, in bytes; 0 or less is no cap. <c>MEITOU_IMPOSTOR_CACHE_MB</c> or the constructor; <see cref="FoliageRenderer.ImpostorCacheMb"/> in the viewer.</summary>
    public long MaxBytes { get; set; }

    static long FromEnvironment() =>
        double.TryParse(Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_CACHE_MB"), NumberStyles.Float, CultureInfo.InvariantCulture, out double mb)
            ? (long)(mb * 1048576) : DefaultMaxBytes;

    public string PathFor(ImpostorSource source)
    {
        var name = new string(source.Name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (name.Length > 40) name = name[..40];
        return Path.Combine(Root, $"{name}_{source.Key[..24]}.mimp");
    }

    /// <summary>The cached atlas, or null when there is none or it is unreadable (it is then baked and written again). A hit counts as a use.
    /// <paramref name="firstLevel"/>: the levels above it are not loaded (<see cref="ImpostorAtlas.Read"/>).</summary>
    public ImpostorAtlas? TryLoad(ImpostorSource source, int firstLevel = 0)
    {
        var path = PathFor(source);
        if (!File.Exists(path)) return null;
        try
        {
            ImpostorAtlas? atlas;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                atlas = ImpostorAtlas.Read(stream, firstLevel);
            if (atlas is not null) Touch(path);
            return atlas;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Save(ImpostorSource source, ImpostorAtlas atlas)
    {
        var path = PathFor(source);
        Directory.CreateDirectory(Root);
        var temporary = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            atlas.Write(stream);
        File.Move(temporary, path, overwrite: true);
        if (Interlocked.Increment(ref saves) % SavesPerPass == 0) MaintainInBackground();
    }

    static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A <see cref="Maintain"/> pass on a worker (skipped when one is running); failures are ignored, the cache is only a cache.</summary>
    public void MaintainInBackground()
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
        Task.Run(() =>
        {
            try { Maintain(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { Volatile.Write(ref running, 0); }
        });
    }

    /// <summary>
    /// Deletes what the cache no longer needs: files of an older <see cref="ImpostorAtlas.FormatVersion"/> / <see cref="ImpostorAtlas.BakerVersion"/>
    /// than this build's or not an atlas at all, temporary files an hour old, then, when what is left is over
    /// <see cref="MaxBytes"/>, the least recently used files until it is under 90% of it. Other files in the folder are left alone.
    /// </summary>
    public ImpostorCacheMaintenance Maintain(DateTime? now = null)
    {
        lock (gate)
        {
            if (!Directory.Exists(Root)) return default;
            var time = now ?? DateTime.UtcNow;
            var files = new List<FileInfo>();
            int stale = 0, evicted = 0;
            foreach (var info in new DirectoryInfo(Root).EnumerateFiles())
            {
                if (info.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    if (info.Name.Contains(".mimp.", StringComparison.OrdinalIgnoreCase) && time - info.LastWriteTimeUtc > AbandonedTemporary && Delete(info)) stale++;
                    continue;
                }
                if (!info.Extension.Equals(".mimp", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsStale(info)) { if (Delete(info)) stale++; continue; }
                files.Add(info);
            }
            long bytes = files.Sum(f => f.Length), after = bytes;
            if (MaxBytes > 0 && bytes > MaxBytes)
            {
                long target = (long)(MaxBytes * EvictTo);
                foreach (var info in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (after <= target) break;
                    long length = info.Length;
                    if (Delete(info)) { after -= length; evicted++; }
                }
            }
            return new ImpostorCacheMaintenance(files.Count, bytes, stale, evicted, after);
        }
    }

    /// <summary>The atlas files in the folder and their total size (the Tab panel's question before <see cref="Clear"/>).</summary>
    public (int Files, long Bytes) Measure()
    {
        lock (gate)
        {
            if (!Directory.Exists(Root)) return default;
            var files = new DirectoryInfo(Root).EnumerateFiles("*.mimp").ToList();
            return (files.Count, files.Sum(f => f.Length));
        }
    }

    /// <summary>
    /// Deletes every atlas file (the Tab panel's button): each is baked again the next time it is needed and not resident. Temporary files
    /// are left to the save that is writing them; a file another process holds stays. Returns what was deleted.
    /// </summary>
    public (int Files, long Bytes) Clear()
    {
        lock (gate)
        {
            if (!Directory.Exists(Root)) return default;
            int files = 0;
            long bytes = 0;
            foreach (var info in new DirectoryInfo(Root).EnumerateFiles("*.mimp"))
            {
                long length = info.Length;
                if (Delete(info)) { files++; bytes += length; }
            }
            return (files, bytes);
        }
    }

    /// <summary>
    /// Whether the file is dead weight: not an atlas (short, wrong magic) or one of an older format or baker version than this build's (its
    /// key can never match again). A newer version is left, since another build sharing the folder may still read it (the least-recently-used
    /// rule takes it in time). A damaged body is found when the file is read.
    /// </summary>
    static bool IsStale(FileInfo info)
    {
        try
        {
            Span<byte> header = stackalloc byte[12];
            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12) return true;
            if (BitConverter.ToUInt32(header) != ImpostorAtlas.Magic) return true;
            int format = BitConverter.ToInt32(header[4..]), baker = BitConverter.ToInt32(header[8..]);
            return format < ImpostorAtlas.FormatVersion || (format == ImpostorAtlas.FormatVersion && baker < ImpostorAtlas.BakerVersion);
        }
        catch (IOException) { return false; }   // in use by another process: leave it
        catch (UnauthorizedAccessException) { return false; }
    }

    static bool Delete(FileInfo info)
    {
        try { info.Delete(); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
