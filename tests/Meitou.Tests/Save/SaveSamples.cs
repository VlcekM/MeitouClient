using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Save;

namespace Meitou.Tests.Save;

/// <summary>
/// The user's own saves, read in place and never changed: the folders under <c>%LOCALAPPDATA%\kenshi\save</c> and the install's <c>save</c> folder
/// (only those that hold a <c>quick.save</c>). Tests that use them skip when there are none.
/// </summary>
public static class SaveSamples
{
    public static IReadOnlyList<string> Folders { get; } = Find();

    static List<string> Find()
    {
        var roots = new List<string>();
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(local)) roots.Add(Path.Combine(local, "kenshi", "save"));
        if (GameInstall.Locate() is { } install) roots.Add(install.SaveDirectory);
        var folders = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
                if (File.Exists(Path.Combine(dir, SaveFolder.QuickSaveName))) folders.Add(dir);
        return folders;
    }

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var f in Folders) data.Add(f);
        if (Folders.Count == 0) data.Add("");
        return data;
    }

    public static void SkipWithoutSaves(string folder) => Assert.SkipWhen(folder.Length == 0, "No saves with a quick.save found");

    static readonly Dictionary<string, SaveGame> cache = [];
    static readonly Lock gate = new();

    /// <summary>The save, loaded once per test run; tests must not change it (they copy what they want to change).</summary>
    public static SaveGame Load(string folder)
    {
        lock (gate)
        {
            if (!cache.TryGetValue(folder, out var game)) cache[folder] = game = SaveGame.Load(folder);
            return game;
        }
    }

    public static GameDatabase? BaseDatabase() => InstallData.BaseGame;

    public static string Temp(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), "meitou-save-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
