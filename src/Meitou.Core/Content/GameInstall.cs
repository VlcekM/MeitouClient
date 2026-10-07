using System.Text.Json;

namespace Meitou.Content;

/// <summary>
/// The user's original Kenshi installation. MeitouClient ships no game assets; everything is
/// read from here at runtime.
/// </summary>
public sealed class GameInstall
{
    public const string EnvironmentVariable = "KENSHI_PATH";
    public const string LocalConfigFile = "meitou.local.json";

    GameInstall(string root) => Root = root;

    public string Root { get; }
    public string DataDirectory => Path.Combine(Root, "data");
    public string ModsDirectory => Path.Combine(Root, "mods");
    public string SaveDirectory => Path.Combine(Root, "save");

    /// <summary>
    /// Steam workshop items of Kenshi (app 233860) in the same Steam library, if the install is a Steam one:
    /// <c>steamapps/workshop/content/233860</c> next to <c>steamapps/common/&lt;install&gt;</c>.
    /// </summary>
    public string? WorkshopDirectory
    {
        get
        {
            var common = Path.GetDirectoryName(Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var steamapps = common is null ? null : Path.GetDirectoryName(common);
            if (steamapps is null || !string.Equals(Path.GetFileName(common), "common", StringComparison.OrdinalIgnoreCase)) return null;
            var dir = Path.Combine(steamapps, "workshop", "content", "233860");
            return Directory.Exists(dir) ? dir : null;
        }
    }

    /// <summary>True when <paramref name="root"/> looks like a Kenshi install.</summary>
    public static bool IsValid(string root) =>
        Directory.Exists(Path.Combine(root, "data")) &&
        File.Exists(Path.Combine(root, "data", "gamedata.base"));

    public static GameInstall Open(string root)
    {
        root = Path.GetFullPath(root);
        if (!IsValid(root))
            throw new DirectoryNotFoundException($"'{root}' is not a Kenshi installation (data/gamedata.base not found).");
        return new GameInstall(root);
    }

    /// <summary>
    /// Finds the install from <c>KENSHI_PATH</c>, then <c>meitou.local.json</c> (<c>{"kenshiPath": "..."}</c>)
    /// in the working directory or next to the executable, or in one of their parent directories (so a test or tool run
    /// from <c>bin/</c> finds the repository root's file), then a Steam install (<c>steamapps/common/Kenshi</c> in any of the
    /// libraries Steam lists). Returns null when none is found.
    /// </summary>
    public static GameInstall? Locate()
    {
        foreach (var candidate in Candidates())
            if (!string.IsNullOrWhiteSpace(candidate) && IsValid(candidate))
                return new GameInstall(Path.GetFullPath(candidate));
        return null;
    }

    /// <summary>
    /// <see cref="Locate"/>, else (a release run by hand without Steam) asks for the folder on the console and keeps it in
    /// <c>meitou.local.json</c> next to the executable, so it is asked once. Null when nothing is found and the input is not a console or
    /// the answer is empty.
    /// </summary>
    public static GameInstall? LocateOrAsk()
    {
        if (Locate() is { } found) return found;
        if (Console.IsInputRedirected) return null;
        Console.WriteLine("Kenshi was not found. Paste the Kenshi folder (the one with kenshi_x64.exe and data\\), or press Enter to quit.");
        while (true)
        {
            Console.Write("Kenshi folder: ");
            var line = Console.ReadLine()?.Trim().Trim('"');
            if (string.IsNullOrEmpty(line)) return null;
            if (!IsValid(line))
            {
                Console.WriteLine($"'{line}' is not a Kenshi folder (data\\gamedata.base not found).");
                continue;
            }
            try
            {
                SaveLocalConfig(AppContext.BaseDirectory, line);
                Console.WriteLine($"Saved to {Path.Combine(AppContext.BaseDirectory, LocalConfigFile)}.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Could not save {LocalConfigFile} ({e.Message}); it will be asked for again next time.");
            }
            return Open(line);
        }
    }

    static IEnumerable<string?> Candidates()
    {
        yield return Environment.GetEnvironmentVariable(EnvironmentVariable);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                yield return ReadLocalConfig(Path.Combine(dir.FullName, LocalConfigFile));
        foreach (var library in SteamLibraries())
            yield return Path.Combine(library, "steamapps", "common", "Kenshi");
    }

    /// <summary>
    /// Writes <c>meitou.local.json</c> with <paramref name="root"/> into <paramref name="directory"/>, so <see cref="Locate"/> finds the
    /// install next time (a release's first start asks for the folder and keeps it this way).
    /// </summary>
    public static void SaveLocalConfig(string directory, string root) =>
        File.WriteAllText(Path.Combine(directory, LocalConfigFile),
            JsonSerializer.Serialize(new Dictionary<string, string> { ["kenshiPath"] = Path.GetFullPath(root) }, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>
    /// The Steam libraries on this machine: Steam's own folder (<c>SteamPath</c> in the user's registry) and every <c>"path"</c> in its
    /// <c>steamapps/libraryfolders.vdf</c>. Empty off Windows or without Steam.
    /// </summary>
    static IEnumerable<string> SteamLibraries()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        string? steam;
        try { steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { steam = null; }
        if (string.IsNullOrEmpty(steam)) yield break;
        yield return steam;
        string text;
        try { text = File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    static string? ReadLocalConfig(string file)
    {
        if (!File.Exists(file)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.TryGetProperty("kenshiPath", out var p) ? p.GetString() : null;
    }
}
