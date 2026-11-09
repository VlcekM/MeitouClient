namespace Meitou.Data.Save;

/// <summary>
/// A save slot found on disk: its folder name, full path, the quick save's last write time (UTC) and whether Meitou may write to it.
/// </summary>
public sealed record SaveSlot(string Name, string Folder, DateTime ModifiedUtc, bool Writable);

/// <summary>
/// What the menu shows of a slot, read from its quick save's camera record: game version, player faction, money, member count, game time and area.
/// </summary>
public sealed record SaveSlotInfo(string Version, string Faction, int Funds, int Members, int Day, int Hour, int Minute, string Area);

/// <summary>
/// Menu save discovery and safe named slots. Reading an original slot never makes it a write destination.
/// </summary>
public static class SaveCatalog
{
    /// <summary>
    /// <paramref name="name"/> when it is a safe folder name (1 to 80 characters, no path or control characters, no trailing dot or space, not a
    /// reserved device name); else throws <see cref="ArgumentException"/>.
    /// </summary>
    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name != name.Trim() || name.EndsWith('.') ||
            name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) || name is "." or "..")
            throw new ArgumentException("Use a name of 1-80 characters without path separators or trailing dots/spaces.", nameof(name));
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && stem[3] is >= '0' and <= '9')
            throw new ArgumentException("That name is reserved by the operating system.", nameof(name));
        return name;
    }

    /// <summary>
    /// The full path of slot <paramref name="name"/> under <paramref name="root"/> (the name validated).
    /// </summary>
    public static string Folder(string root, string name) => Path.Combine(Path.GetFullPath(root), ValidateName(name));

    /// <summary>
    /// The slots (folders holding a quick save) under the roots, newest first then by name; a folder found twice keeps its first, so the writable
    /// root wins.
    /// </summary>
    public static IReadOnlyList<SaveSlot> Scan(string writableRoot, params string[] readOnlyRoots)
    {
        var found = new Dictionary<string, SaveSlot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, writable) in new[] { (writableRoot, true) }.Concat(readOnlyRoots.Select(r => (r, false))))
        {
            if (!Directory.Exists(root)) continue;
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                string folder = Path.GetFullPath(directory), quick = Path.Combine(folder, SaveFolder.QuickSaveName);
                if (File.Exists(quick)) found.TryAdd(folder, new(Path.GetFileName(folder), folder, File.GetLastWriteTimeUtc(quick), writable));
            }
        }
        return found.Values.OrderByDescending(s => s.ModifiedUtc).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Reads the menu details of <paramref name="slot"/> from its quick save.
    /// </summary>
    public static SaveSlotInfo ReadInfo(SaveSlot slot)
    {
        var game = new SaveGame(SaveFile.Read(File.ReadAllBytes(Path.Combine(slot.Folder, SaveFolder.QuickSaveName))));
        var c = game.Camera;
        return new(c.Version, c.PlayerFactionName, c.PlayerMoney, c.Members, c.Day, c.Hour, c.Minute, c.Area);
    }
}