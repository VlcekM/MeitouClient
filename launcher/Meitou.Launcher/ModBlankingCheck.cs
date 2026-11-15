using System.Collections.Concurrent;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// The Mods page's check for mods that blank the ones loaded before them (<see cref="ModBlanking"/>, docs/launcher.md "Mod list"). A data
/// file is scanned once, off the UI thread, and kept; the walk over the enabled list runs again whenever the list changes.
/// </summary>
sealed class ModBlankingCheck
{
    /// <summary>
    /// The scans by file path; null for a file that could not be read. <see cref="Reset"/> starts a new table, so a run it overtook cannot
    /// leave an old scan in it.
    /// </summary>
    ConcurrentDictionary<string, ModRecordScan?> _scans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The number of the latest run: an earlier one that ends after it started is dropped.
    /// </summary>
    int _run;

    /// <summary>
    /// Forgets the scans (the mods are read again: their files may have changed) and drops a run that has not ended.
    /// </summary>
    public void Reset()
    {
        _run++;
        _scans = new ConcurrentDictionary<string, ModRecordScan?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks the <paramref name="enabled"/> mods (name and <c>.mod</c> path, in load order) after the core files of
    /// <paramref name="install"/>: the mods that blank earlier ones, by name. Null when a later run or a <see cref="Reset"/> overtook this one.
    /// Call it from the UI thread.
    /// </summary>
    public async Task<Dictionary<string, IReadOnlyList<BlankedFile>>?> RunAsync(GameInstall install,
            IReadOnlyList<(string Name, string Path)> enabled)
    {
        int run = ++_run;
        var scans = _scans;
        var core = ModBlanking.CoreFiles(install);
        var blanked = await Task.Run(() =>
        {
            Parallel.ForEach(core.Concat(enabled.Select(e => e.Path)), path => scans.GetOrAdd(path, ModRecordScan.TryRead));
            return ModBlanking.Check([.. core.Select(c => scans[c])], [.. enabled.Select(e => (e.Name, scans[e.Path]))]);
        });
        return run == _run ? blanked : null;
    }
}
