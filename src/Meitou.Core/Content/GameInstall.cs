using System.Text.Json;

namespace Meitou.Content;

/// <summary>
/// The user's original Kenshi installation. MeitouClient ships no game assets; everything is
/// read from here at runtime.
/// </summary>
public sealed class GameInstall
{
    /// <summary>
    /// The game's exe names: Steam's, then GOG's (<c>kenshi_GOG_x64.exe</c>, #336).
    /// </summary>
    static readonly string[] ExecutableNames = ["kenshi_x64.exe", "kenshi_GOG_x64.exe"];

    /// <summary>
    /// The environment variable that names the install's folder; <see cref="Locate"/> tries it first.
    /// </summary>
    public const string EnvironmentVariable = "KENSHI_PATH";
    /// <summary>
    /// The git-ignored file that names the install (<c>{"kenshiPath": "..."}</c>), searched from the working directory and the executable's
    /// folder upwards.
    /// </summary>
    public const string LocalConfigFile = "meitou.local.json";

    /// <summary>
    /// Wraps an already validated, full install path.
    /// </summary>
    GameInstall(string root) => Root = root;

    /// <summary>
    /// Full path of the install's folder (the one holding the game's exe and <c>data</c>).
    /// </summary>
    public string Root { get; }
    /// <summary>
    /// The install's <c>data</c> folder: <c>gamedata.base</c>, <c>mods.cfg</c> and the other base data files.
    /// </summary>
    public string DataDirectory => Path.Combine(Root, "data");
    /// <summary>
    /// The install's <c>mods</c> folder, one subfolder per local mod.
    /// </summary>
    public string ModsDirectory => Path.Combine(Root, "mods");
    /// <summary>
    /// The install's <c>save</c> folder, one subfolder per save.
    /// </summary>
    public string SaveDirectory => Path.Combine(Root, "save");

    /// <summary>
    /// The install's game exe: the first of <see cref="ExecutableNames"/> that exists, else the Steam name (missing).
    /// </summary>
    public string Executable =>
        ExecutableNames.Select(name => Path.Combine(Root, name)).FirstOrDefault(File.Exists) ?? Path.Combine(Root, ExecutableNames[0]);

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
            if (steamapps is null || !string.Equals(Path.GetFileName(common), "common", StringComparison.OrdinalIgnoreCase))
                return null;
            var dir = Path.Combine(steamapps, "workshop", "content", "233860");
            return Directory.Exists(dir) ? dir : null;
        }
    }

    /// <summary>
    /// True when <paramref name="root"/> looks like a Kenshi install.
    /// </summary>
    public static bool IsValid(string root) =>
        Directory.Exists(Path.Combine(root, "data")) &&
        File.Exists(Path.Combine(root, "data", "gamedata.base"));

    /// <summary>
    /// Opens the install at <paramref name="root"/>; throws <see cref="DirectoryNotFoundException"/> when it has no <c>data/gamedata.base</c>.
    /// </summary>
    public static GameInstall Open(string root)
    {
        root = Path.GetFullPath(root);
        if (!IsValid(root))
            throw new DirectoryNotFoundException($"'{root}' is not a Kenshi installation (data/gamedata.base not found).");
        return new GameInstall(root);
    }

    /// <summary>
    /// Finds the install from <c>KENSHI_PATH</c>, then <c>meitou.local.json</c> (<c>{"kenshiPath": "..."}</c>) in the working directory or next to
    /// the executable, or in one of their parent directories (so a test or tool run from <c>bin/</c> finds the repository root's file), then the
    /// per-user <c>meitou/config.json</c> in <c>XDG_CONFIG_HOME</c> (Linux, macOS), then a Steam install (<c>steamapps/common/Kenshi</c> in any of
    /// the libraries Steam lists), then the other launchers' installs (<see cref="GameInstallSources"/>: GOG, Heroic, Lutris, Bottles, Wine
    /// prefixes). The folder the executable sits in and the one above it are tried before Steam. Returns null when none is found.
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
                // Next to the executable; an AppImage or a system folder is read-only, so then the per-user file.
                try
                {
                    SaveLocalConfig(AppContext.BaseDirectory, line);
                    Console.WriteLine($"Saved to {Path.Combine(AppContext.BaseDirectory, LocalConfigFile)}.");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (GameInstallSources.UserConfigFile(Environment.GetEnvironmentVariable) is not { } userConfig)
                        throw;
                    Directory.CreateDirectory(Path.GetDirectoryName(userConfig)!);
                    SaveConfigFile(userConfig, line);
                    Console.WriteLine($"Saved to {userConfig}.");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Could not save {LocalConfigFile} ({e.Message}); it will be asked for again next time.");
            }
            return Open(line);
        }
    }

    /// <summary>
    /// The folders <see cref="Locate"/> tries, in its order; entries may be null or empty (an unset variable, an unreadable config file).
    /// </summary>
    static IEnumerable<string?> Candidates()
    {
        yield return Environment.GetEnvironmentVariable(EnvironmentVariable);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                yield return ReadLocalConfig(Path.Combine(dir.FullName, LocalConfigFile));
        if (GameInstallSources.UserConfigFile(Environment.GetEnvironmentVariable) is { } userConfig)
            yield return ReadLocalConfig(userConfig);
        // The folder this executable sits in, or the one above it (a build dropped into the game's folder).
        yield return AppContext.BaseDirectory;
        yield return Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        foreach (var library in SteamLibraries())
        {
            yield return Path.Combine(library, "steamapps", "common", "Kenshi");

            // An install under another folder name: Steam's own record of it (appmanifest_233860.acf, the Kenshi app).
            if (GameInstallSources.SteamInstallDirectory(Path.Combine(library, "steamapps")) is { } installDir)
                yield return installDir;
        }
        foreach (var path in GameInstallSources.Launchers()) yield return path;
    }

    /// <summary>
    /// Writes <c>meitou.local.json</c> with <paramref name="root"/> into <paramref name="directory"/>, so <see cref="Locate"/> finds the
    /// install next time (a release's first start asks for the folder and keeps it this way).
    /// </summary>
    public static void SaveLocalConfig(string directory, string root) => SaveConfigFile(Path.Combine(directory, LocalConfigFile), root);

    /// <summary>
    /// Writes the config file at <paramref name="path"/> with the full path of <paramref name="root"/> as <c>kenshiPath</c>.
    /// </summary>
    static void SaveConfigFile(string path, string root)
    {
        // Written by hand, not JsonSerializer: the launcher is Native AOT, where reflection-based serialization is unavailable.
        using var file = File.Create(path);
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("kenshiPath", Path.GetFullPath(root));
        json.WriteEndObject();
    }

    /// <summary>
    /// The Steam libraries on this machine: Steam's own folder (<c>SteamPath</c> in the user's registry on Windows; <c>~/.local/share/Steam</c>,
    /// <c>~/.steam/steam</c> or the Flatpak one on Linux) and every <c>"path"</c> in its <c>steamapps/libraryfolders.vdf</c>. Empty without Steam.
    /// </summary>
    static IEnumerable<string> SteamLibraries()
    {
        foreach (var steam in SteamFolders())
        {
            yield return steam;
            string text;
            try
            {
                text = File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                yield return m.Groups[1].Value.Replace(@"\\", @"\");
        }
    }

    /// <summary>
    /// Steam's own folders that exist on this machine, by platform (see <see cref="SteamLibraries"/>).
    /// </summary>
    static IEnumerable<string> SteamFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            string? steam;
            try
            {
                steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            }
            catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { steam = null; }
            if (!string.IsNullOrEmpty(steam)) yield return steam;
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // Steam itself installs to ~/.local/share/Steam whatever XDG_DATA_HOME says, so both are tried.
            var candidates = new[] { Path.Combine(GameInstallSources.Xdg.From(home, Environment.GetEnvironmentVariable).DataHome, "Steam"),
                    Path.Combine(home, ".local/share/Steam"), Path.Combine(home, ".steam/steam"), Path.Combine(home,
                            ".var/app/com.valvesoftware.Steam/.local/share/Steam") };
            foreach (var steam in candidates.Distinct())
                if (Directory.Exists(steam)) yield return steam;
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Steam's Mac client; a Windows Steam bottle is covered by GameInstallSources' Wine prefixes.
            var steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Steam");
            if (Directory.Exists(steam)) yield return steam;
        }
    }

    /// <summary>
    /// The <c>kenshiPath</c> string of the config file at <paramref name="file"/>; null when it is missing, broken or has none.
    /// </summary>
    static string? ReadLocalConfig(string file)
    {
        if (!File.Exists(file)) return null;

        // A broken file (an interrupted save) is skipped, so the other sources and the prompt still run.
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("kenshiPath", out var p)
                    && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
}