namespace Meitou.Data.Save;

public sealed record SaveSlot(string Name, string Folder, DateTime ModifiedUtc, bool Writable);
public sealed record SaveSlotInfo(string Version, string Faction, int Funds, int Members, int Day, int Hour, int Minute, string Area);

/// <summary>Menu save discovery and safe named slots. Reading an original slot never makes it a write destination.</summary>
public static class SaveCatalog
{
    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name != name.Trim() || name.EndsWith('.') ||
            name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) || name is "." or "..")
            throw new ArgumentException("Use a name of 1-80 characters without path separators or trailing dots/spaces.", nameof(name));
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9')
            throw new ArgumentException("That name is reserved by the operating system.", nameof(name));
        return name;
    }
    public static string Folder(string root, string name) => Path.Combine(Path.GetFullPath(root), ValidateName(name));
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
    /// Deletes a listed save: its whole folder. Only a real folder named like the slot that holds a <c>quick.save</c> goes (never a link, so nothing outside the save folder is touched);
    /// a failure comes back as the message, null when the save is gone.
    /// </summary>
    public static string? Delete(SaveSlot slot)
    {
        try
        {
            var info = new DirectoryInfo(Path.GetFullPath(slot.Folder));
            if (info.Parent is null || info.Name != slot.Name || info.LinkTarget is not null || !File.Exists(Path.Combine(info.FullName, SaveFolder.QuickSaveName)))
                return $"'{slot.Name}' is not a save folder.";
            info.Delete(recursive: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
    }
    public static SaveSlotInfo ReadInfo(SaveSlot slot)
    {
        var game = new SaveGame(SaveFile.Read(File.ReadAllBytes(Path.Combine(slot.Folder, SaveFolder.QuickSaveName))));
        var c = game.Camera;
        return new(c.Version, c.PlayerFactionName, c.PlayerMoney, c.Members, c.Day, c.Hour, c.Minute, c.Area);
    }
}
