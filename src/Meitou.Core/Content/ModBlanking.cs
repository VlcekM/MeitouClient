namespace Meitou.Content;

/// <summary>
/// An earlier mod whose records a mod blanks, and how many of them.
/// </summary>
/// <param name="Mod">The mod that made the records, by name.</param>
/// <param name="Records">How many of its records lose file paths.</param>
public readonly record struct BlankedFile(string Mod, int Records);

/// <summary>
/// Finds the mods that blank what loads before them (docs/animation.md "Animation files", docs/launcher.md "Mod list"): a patch made of stub
/// records, each stated whole with its paths empty, is meant to load above the mods it names, whose full records then overwrite the stubs.
/// Loaded below them it empties their meshes, textures and animation files, as the game merges an empty value like any other.
/// </summary>
public static class ModBlanking
{
    /// <summary>
    /// The core data files every load order starts with, by full path, in load order.
    /// </summary>
    public static IReadOnlyList<string> CoreFiles(GameInstall install) =>
        [.. ModList.CoreModNames.Select(n => n + ".mod").Prepend("gamedata.base").Select(f => Path.Combine(install.DataDirectory, f))];

    /// <summary>
    /// Walks the <paramref name="core"/> files' scans, then the enabled <paramref name="mods"/> in load order (a null scan is a file that could
    /// not be read), and answers by name the mods that blank earlier mods, with whose records they hit, most records first. The base game's
    /// records are not counted: no place in the list would spare them.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<BlankedFile>> Check(IReadOnlyList<ModRecordScan?> core,
            IReadOnlyList<(string Name, ModRecordScan? Scan)> mods)
    {
        // String id -> the filename keys that hold a path so far.
        var paths = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var scan in core.OfType<ModRecordScan>())
            foreach (var record in scan.Records) Apply(record, paths);

        var places = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int mod = 0; mod < mods.Count; mod++) places.TryAdd(mods[mod].Name, mod);

        var result = new Dictionary<string, IReadOnlyList<BlankedFile>>(StringComparer.Ordinal);
        for (int mod = 0; mod < mods.Count; mod++)
        {
            if (mods[mod].Scan is not { } scan) continue;

            var hit = Merge(scan, mod, paths, places);
            if (hit.Count == 0) continue;

            result[mods[mod].Name] = [.. hit
                .OrderByDescending(h => h.Value)
                .ThenBy(h => h.Key)
                .Select(h => new BlankedFile(mods[h.Key].Name, h.Value))];
        }
        return result;
    }

    /// <summary>
    /// Merges the records of the mod at <paramref name="place"/> into <paramref name="paths"/> and counts the records its stubs blank by their
    /// maker's place in the list; only makers that load before it count, a later one overwrites the stub.
    /// </summary>
    static Dictionary<int, int> Merge(ModRecordScan scan, int place, Dictionary<string, HashSet<string>> paths, Dictionary<string, int> places)
    {
        var hit = new Dictionary<int, int>();
        foreach (var record in scan.Records)
        {
            if (!Apply(record, paths)) continue;
            if (Maker(record.StringId, places) is { } maker && maker < place) hit[maker] = hit.GetValueOrDefault(maker) + 1;
        }
        return hit;
    }

    /// <summary>
    /// Merges <paramref name="record"/> into <paramref name="paths"/> as the game does. True when it is a stub that blanks: stated whole (a
    /// change that empties a path is its author's intent) and emptying more of the paths set so far than it gives itself (a full record that
    /// restates another's with one path dropped is an override, not a stub).
    /// </summary>
    static bool Apply(ScannedRecord record, Dictionary<string, HashSet<string>> paths)
    {
        // REMOVED drops the record with every field; a later record of the id starts afresh.
        if (record.Removed)
        {
            paths.Remove(record.StringId);
            return false;
        }

        paths.TryGetValue(record.StringId, out var keys);
        int emptied = 0;
        if (keys is not null)
            foreach (string key in record.Blank)
                if (keys.Remove(key)) emptied++;

        if (record.Filled.Count > 0)
        {
            keys ??= paths[record.StringId] = new HashSet<string>(StringComparer.Ordinal);
            keys.UnionWith(record.Filled);
        }

        return record.IsNew && emptied > record.Filled.Count;
    }

    /// <summary>
    /// Where the mod that made the record <paramref name="stringId"/> sits in the list: a string id ends in the name of the file that created it
    /// (<c>5007294-Power Up Battle Motions.mod</c>). Null for the base game's (a core file, or no <c>.mod</c> name: <c>.base</c>,
    /// <c>.quack</c>) and when that mod is not in the list, so the record is nobody's here: a patch's stub for a mod that is not installed,
    /// which another patch's copy of the same record may have filled. A renamed mod's records keep the old name and are not found.
    /// </summary>
    static int? Maker(string stringId, Dictionary<string, int> places)
    {
        string file = stringId[(stringId.IndexOf('-') + 1)..];
        if (!file.EndsWith(".mod", StringComparison.OrdinalIgnoreCase)) return null;

        return places.TryGetValue(file[..^4], out int place) ? place : null;
    }
}
