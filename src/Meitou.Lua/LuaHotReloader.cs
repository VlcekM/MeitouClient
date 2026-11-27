using System.Collections.Concurrent;
using System.Diagnostics;

namespace Meitou.Lua;

/// <summary>
/// Watches enabled Lua mod <c>scripts/</c> folders for <c>*.lua</c> edits and coalesces rapid writes
/// into debounced reload notifications for <see cref="KenshiLuaHost"/>.
/// </summary>
internal sealed class LuaHotReloader : IDisposable
{
    /// <summary>
    /// Default settle window before a modified file triggers a reload.
    /// </summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Active watchers keyed by mod id.
    /// </summary>
    readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Last modification timestamp (in <see cref="Stopwatch"/> ticks) for each dirty mod id.
    /// </summary>
    readonly ConcurrentDictionary<string, long> _dirty = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Settle window applied before a dirty mod is drained.
    /// </summary>
    readonly TimeSpan _debounce;

    /// <summary>
    /// Whether this instance has been disposed.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// Creates a hot-reloader with the given debounce window.
    /// </summary>
    public LuaHotReloader(TimeSpan? debounce = null)
    {
        _debounce = debounce ?? DefaultDebounce;
    }

    /// <summary>
    /// Starts watching <paramref name="modFolder"/>'s <c>scripts/</c> subtree for <c>*.lua</c> changes.
    /// Safe to call repeatedly for the same mod id; skips missing folders and reparse points.
    /// </summary>
    public void Watch(string modId, string modFolder)
    {
        if (_disposed || _watchers.ContainsKey(modId)) return;

        string? scriptsDir = LuaSandbox.SafePath(modFolder, "scripts");
        if (scriptsDir is null || !Directory.Exists(scriptsDir)) return;

        try
        {
            FileSystemWatcher watcher = new(scriptsDir, "*.lua")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            watcher.Changed += (_, _) => MarkDirty(modId);
            watcher.Created += (_, _) => MarkDirty(modId);
            watcher.Renamed += (_, _) => MarkDirty(modId);
            watcher.EnableRaisingEvents = true;
            _watchers[modId] = watcher;
        }
        catch (Exception)
        {
            // OS watcher limits or permission errors never prevent the mod from running.
        }
    }

    /// <summary>
    /// Returns every mod id whose latest script edit has settled past the debounce window and removes it from the pending set.
    /// </summary>
    public List<string> DrainReady()
    {
        List<string> ready = [];
        if (_dirty.IsEmpty) return ready;

        long now = Stopwatch.GetTimestamp();
        long settleTicks = (long)(_debounce.TotalSeconds * Stopwatch.Frequency);
        foreach (KeyValuePair<string, long> kv in _dirty)
        {
            if (now - kv.Value < settleTicks) continue;
            if (_dirty.TryRemove(kv.Key, out _)) ready.Add(kv.Key);
        }

        ready.Sort(StringComparer.OrdinalIgnoreCase);
        return ready;
    }

    /// <summary>
    /// Records the latest write timestamp for <paramref name="modId"/>.
    /// </summary>
    void MarkDirty(string modId) => _dirty[modId] = Stopwatch.GetTimestamp();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        foreach (FileSystemWatcher watcher in _watchers.Values)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception)
            {
                // Ignore watcher teardown errors.
            }
        }
        _watchers.Clear();
        _dirty.Clear();
    }
}
