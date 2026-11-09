using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meitou.Content;

/// <summary>
/// Places a Kenshi install made by something other than Steam can be: the game launchers' own lists (GOG's registry, Heroic's installed list,
/// Lutris's game files), the usual install folders of GOG and Wine prefixes, and the folder Meitou itself sits in. They only produce
/// <em>candidates</em>: <see cref="GameInstall.Locate"/> keeps the first one that
/// <see cref="GameInstall.IsValid"/> accepts, so a stale or wrong entry costs nothing.
/// </summary>
public static partial class GameInstallSources
{
    /// <summary>
    /// The candidates from every launcher on this machine, in the order they are tried; scanned once per process. Windows: its registry and Heroic's
    /// list (the Wine and Linux launcher folders do not exist there).
    /// </summary>
    public static IEnumerable<string> Launchers() => LauncherList.Value;

    /// <summary>
    /// <see cref="Launchers()"/>'s folders, gathered once on first use: the registry and Heroic on Windows, the Unix launchers elsewhere.
    /// </summary>
    static readonly Lazy<string[]> LauncherList = new(() =>
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return OperatingSystem.IsWindows()
            ? [.. Registered(), .. HeroicInstalls(Path.Combine(appData, "heroic", "gog_store", "installed.json"))]
            : [.. Launchers(home, appData, Environment.GetEnvironmentVariable)];
    });

    /// <summary>
    /// The entries of a folder listing, none when it cannot be read (another user's folder, a broken mount).
    /// </summary>
    static string[] Safe(Func<IEnumerable<string>> list)
    {
        try
        {
            return [.. list()];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
    }

    /// <summary>
    /// A path from a launcher's file without quotes, with a leading <c>~</c> as the user's home.
    /// </summary>
    static string Unquote(string value)
    {
        value = value.Trim().Trim('"', '\'');
        return value == "~" || value.StartsWith("~/", StringComparison.Ordinal)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + value[1..] : value;
    }

    /// <summary>
    /// The XDG base folders (<c>XDG_DATA_HOME</c>, <c>XDG_CONFIG_HOME</c>, <c>XDG_DATA_DIRS</c>): the variable when it holds an absolute path, else
    /// the specification's default under <see cref="From"/>'s <c>home</c>. Its <c>env</c> reads an environment variable (null when unset).
    /// </summary>
    public readonly record struct Xdg(string DataHome, string ConfigHome, IReadOnlyList<string> DataDirs)
    {
        /// <summary>
        /// The XDG folders read through <paramref name="env"/>: a variable that is not an absolute path gives the specification's default under
        /// <paramref name="home"/>, and the Flatpak export folders are added to the data folders.
        /// </summary>
        public static Xdg From(string home, Func<string, string?> env)
        {
            string Home(string variable, string fallback) => env(variable) is { } v && Path.IsPathRooted(v) ? v : Path.Combine(home, fallback);
            var dataHome = Home("XDG_DATA_HOME", Path.Combine(".local", "share"));
            var dirs = (env("XDG_DATA_DIRS") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathRooted).ToList();
            if (dirs.Count == 0) dirs = ["/usr/local/share", "/usr/share"];
            dirs.Add("/var/lib/flatpak/exports/share");
            dirs.Add(Path.Combine(dataHome, "flatpak", "exports", "share"));
            return new(dataHome, Home("XDG_CONFIG_HOME", ".config"), dirs);
        }
    }

    /// <summary>
    /// Meitou's own per-user settings file, <c>meitou/config.json</c> in <c>XDG_CONFIG_HOME</c> (<c>~/.config</c>), where <c>{"kenshiPath":
    /// "..."}</c> names the install. Linux and macOS only (Windows keeps <c>meitou.local.json</c> next to the executable); null elsewhere.
    /// </summary>
    public static string? UserConfigFile(Func<string, string?> env) =>
        OperatingSystem.IsWindows() ? null : Path.Combine(Xdg.From(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), env).ConfigHome,
                "meitou", "config.json");

    /// <summary>
    /// The candidates from the files and folders under <paramref name="home"/> (and <paramref name="appData"/> on Windows): Heroic, Lutris, Bottles,
    /// <c>.desktop</c> launchers, a default Wine prefix. The XDG folders come from <paramref name="env"/> (null: the specification's defaults).
    /// </summary>
    public static IEnumerable<string> Launchers(string home, string? appData = null, Func<string, string?>? env = null)
    {
        var xdg = Xdg.From(home, env ?? (_ => null));
        var configs = new List<string> { xdg.ConfigHome, Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config") };
        if (!string.IsNullOrEmpty(appData)) configs.Add(appData);
        foreach (var config in configs)
            foreach (var path in HeroicInstalls(Path.Combine(config, "heroic", "gog_store", "installed.json"))) yield return path;
        string[] lutris = [Path.Combine(xdg.ConfigHome, "lutris", "games"), Path.Combine(home, ".var", "app", "net.lutris.Lutris", "config", "lutris",
                "games")];
        foreach (var games in lutris)
            foreach (var path in LutrisInstalls(games)) yield return path;
        foreach (var directory in xdg.DataDirs.Prepend(xdg.DataHome).Select(d => Path.Combine(d, "applications")).Distinct())
            foreach (var path in DesktopEntries(directory)) yield return path;
        yield return Path.Combine(home, "Games", "Heroic", "Kenshi");
        var prefixes = new List<string> { Path.Combine(home, ".wine") };
        foreach (var bottles in new[] { Path.Combine(xdg.DataHome, "bottles", "bottles"), Path.Combine(home, ".var", "app", "com.usebottles.bottles",
                "data", "bottles", "bottles") })
            if (Directory.Exists(bottles)) prefixes.AddRange(Safe(() => Directory.EnumerateDirectories(bottles)));
        if (OperatingSystem.IsMacOS())
            foreach (var bottles in new[]
            {
                Path.Combine(home, "Library", "Application Support", "CrossOver", "Bottles"),
                Path.Combine(home, "Library", "Application Support", "Whisky", "Bottles"),
                Path.Combine(home, "Library", "Containers", "com.isaacmarovitz.Whisky", "Data", "Library", "Application Support", "Whisky",
                        "Bottles"),
            })
                if (Directory.Exists(bottles)) prefixes.AddRange(Safe(() => Directory.EnumerateDirectories(bottles)));
        // The registry of every Wine prefix we know of: what a GOG installer wrote there says where it installed Kenshi.
        foreach (var heroic in configs)
            foreach (var prefix in HeroicPrefixes(Path.Combine(heroic, "heroic", "GamesConfig"))) prefixes.Add(prefix);
        foreach (var games in lutris)
            foreach (var prefix in LutrisPrefixes(games)) prefixes.Add(prefix);
        foreach (var prefix in prefixes.SelectMany(PrefixAndSiblings).Distinct())
            foreach (var path in WinePrefixInstalls(prefix)) yield return path;
        foreach (var prefix in prefixes)
            foreach (var path in InPrefix(Path.Combine(prefix, "drive_c"))) yield return path;
    }

    /// <summary>
    /// The folder Steam records for Kenshi (app 233860) in <paramref name="steamapps"/>: <c>common/&lt;installdir&gt;</c> from
    /// <c>appmanifest_233860.acf</c>; null without the file.
    /// </summary>
    public static string? SteamInstallDirectory(string steamapps)
    {
        try
        {
            var manifest = Path.Combine(steamapps, "appmanifest_233860.acf");
            if (!File.Exists(manifest)) return null;
            var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
            return m.Success ? Path.Combine(steamapps, "common", m.Groups[1].Value) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// A prefix, and when it is one of a launcher's shared <c>default</c> folders (Heroic: <c>Prefixes/default/&lt;game&gt;</c>) its sibling prefixes
    /// too.
    /// </summary>
    static IEnumerable<string> PrefixAndSiblings(string prefix)
    {
        yield return prefix;
        var parent = Path.GetDirectoryName(prefix.TrimEnd('/', '\\'));
        if (parent is not null && Path.GetFileName(parent) == "default" && Directory.Exists(parent))
            foreach (var sibling in Safe(() => Directory.EnumerateDirectories(parent))) yield return sibling;
    }

    /// <summary>
    /// The <c>winePrefix</c> of every game in Heroic's <c>GamesConfig</c> folder (one JSON file per game).
    /// </summary>
    public static IEnumerable<string> HeroicPrefixes(string gamesConfig)
    {
        if (!Directory.Exists(gamesConfig)) yield break;
        List<string> found = [];
        foreach (var file in Safe(() => Directory.EnumerateFiles(gamesConfig, "*.json")))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var game in doc.RootElement.EnumerateObject())
                    if (game.Value.ValueKind == JsonValueKind.Object && game.Value.TryGetProperty("winePrefix", out var prefix)
                            && prefix.ValueKind == JsonValueKind.String && prefix.GetString() is { Length: > 0 } text)
                        found.Add(Unquote(text));
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        }
        foreach (var prefix in found) yield return prefix;
    }

    /// <summary>
    /// The <c>prefix:</c> of every Lutris game file that mentions Kenshi.
    /// </summary>
    public static IEnumerable<string> LutrisPrefixes(string gamesDirectory)
    {
        if (!Directory.Exists(gamesDirectory)) yield break;
        foreach (var file in Safe(() => Directory.EnumerateFiles(gamesDirectory, "*.yml")))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (!text.Contains("kenshi", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (Match m in LutrisPrefix().Matches(text))
                yield return Unquote(m.Groups[1].Value);
        }
    }

    /// <summary>
    /// The Kenshi folders a Wine prefix's registry (<c>system.reg</c>, a text file; <c>pfx/system.reg</c> for a Proton prefix) names, as Linux paths:
    /// the <c>path</c> of a GOG game called Kenshi under
    /// <c>Software\Wow6432Node\GOG.com\Games</c>, and the <c>InstallLocation</c> / Inno Setup <c>App Path</c> of an uninstall entry called Kenshi
    /// (<b>Verified</b> on a Heroic prefix: GOG's keys and value names, Wine's doubled backslashes).
    /// </summary>
    public static IEnumerable<string> WinePrefixInstalls(string prefix)
    {
        var registry = new[] { Path.Combine(prefix, "system.reg"), Path.Combine(prefix, "pfx", "system.reg") }.FirstOrDefault(File.Exists);
        if (registry is null) yield break;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(registry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        var root = File.Exists(Path.Combine(prefix, "system.reg")) ? prefix : Path.Combine(prefix, "pfx");
        var found = new List<string>();
        void Flush(string? section, Dictionary<string, string> values)
        {
            if (section is null) return;
            bool gog = section.Contains(@"GOG.com\Games\", StringComparison.OrdinalIgnoreCase);
            bool uninstall = section.Contains(@"CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase);
            string? name = gog ? values.GetValueOrDefault("gamename") : uninstall ? values.GetValueOrDefault("displayname") : null;
            if (name is null || !name.Contains("Kenshi", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var key in new[] { "path", "installlocation", "inno setup: app path" })
                if (values.GetValueOrDefault(key) is { Length: > 0 } windows && ToUnixPath(root, windows) is { } unix)
                    found.Add(unix);
        }
        string? current = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (line.StartsWith('['))
            {
                Flush(current, values);
                values = new(StringComparer.OrdinalIgnoreCase);
                var end = line.IndexOf(']');
                current = end > 0 ? line[1..end].Replace(@"\\", @"\") : null;
            }
            else if (current is not null && Regex.Match(line, "^\"([^\"]+)\"=\"(.*)\"\\s*$") is { Success: true } m)
                values[m.Groups[1].Value] = m.Groups[2].Value.Replace(@"\\", @"\").Replace("\\\"", "\"");
        }
        Flush(current, values);
        foreach (var path in found) yield return path;
    }

    /// <summary>
    /// A Windows path of a prefix (<c>Z:\mnt\Games\Kenshi</c>, <c>C:\GOG Games\Kenshi</c>) as a Linux path: the drive letter's <c>dosdevices</c>
    /// link, else <c>drive_c</c> and <c>/</c> for Z.
    /// </summary>
    public static string? ToUnixPath(string prefix, string windowsPath)
    {
        var m = Regex.Match(windowsPath.Trim().Trim('"'), @"^([A-Za-z]):[\\/]*(.*)$");
        if (!m.Success) return null;
        char drive = char.ToLowerInvariant(m.Groups[1].Value[0]);
        string rest = m.Groups[2].Value.Replace('\\', '/').TrimEnd('/');
        string link = Path.Combine(prefix, "dosdevices", drive + ":");
        string baseDirectory;
        try
        {
            baseDirectory = Directory.Exists(link) ? new DirectoryInfo(link).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? link : link;
        }
        catch (IOException) { baseDirectory = link; }
        if (!Directory.Exists(baseDirectory))
            baseDirectory = drive == 'c' ? Path.Combine(prefix, "drive_c") : drive == 'z' ? "/" : link;
        return Path.Combine(baseDirectory, rest);
    }

    /// <summary>
    /// The usual GOG folders inside a drive's root or a Wine prefix's <c>drive_c</c>.
    /// </summary>
    public static IEnumerable<string> InPrefix(string driveRoot)
    {
        yield return Path.Combine(driveRoot, "GOG Games", "Kenshi");
        yield return Path.Combine(driveRoot, "Program Files (x86)", "GOG Galaxy", "Games", "Kenshi");
        yield return Path.Combine(driveRoot, "Program Files", "GOG Galaxy", "Games", "Kenshi");

        // A Windows Steam bottle (CrossOver/Whisky): its default and secondary library folders.
        yield return Path.Combine(driveRoot, "Program Files (x86)", "Steam", "steamapps", "common", "Kenshi");
        yield return Path.Combine(driveRoot, "Program Files", "Steam", "steamapps", "common", "Kenshi");
        yield return Path.Combine(driveRoot, "SteamLibrary", "steamapps", "common", "Kenshi");
    }

    /// <summary>
    /// The <c>install_path</c> of every game in Heroic's GOG list (<c>installed.json</c>). Every entry is returned, not only Kenshi's: the list holds
    /// ids, not names, and
    /// <see cref="GameInstall.IsValid"/> picks the right folder.
    /// </summary>
    public static IEnumerable<string> HeroicInstalls(string installedJson)
    {
        if (!File.Exists(installedJson)) yield break;
        List<string> paths = [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(installedJson));
            if (doc.RootElement.TryGetProperty("installed", out var installed) && installed.ValueKind == JsonValueKind.Array)
                foreach (var game in installed.EnumerateArray())
                    if (game.ValueKind == JsonValueKind.Object && game.TryGetProperty("install_path", out var path)
                            && path.GetString() is { Length: > 0 } text)
                        paths.Add(text);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        foreach (var path in paths) yield return path;
    }

    /// <summary>
    /// The folder of the <c>exe</c>, and the <c>working_dir</c>, of every Lutris game file in <paramref name="gamesDirectory"/> that mentions Kenshi;
    /// a relative path is the game's
    /// <c>prefix</c>'s (skipped without one).
    /// </summary>
    public static IEnumerable<string> LutrisInstalls(string gamesDirectory)
    {
        if (!Directory.Exists(gamesDirectory)) yield break;
        foreach (var file in Safe(() => Directory.EnumerateFiles(gamesDirectory, "*.yml")))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (!text.Contains("kenshi", StringComparison.OrdinalIgnoreCase)) continue;
            var prefix = LutrisPrefix().Match(text) is { Success: true } p ? Unquote(p.Groups[1].Value) : null;
            foreach (Match m in LutrisExe().Matches(text))
            {
                var value = Unquote(m.Groups[2].Value);
                if (!Path.IsPathRooted(value))
                {
                    if (prefix is null) continue;
                    value = Path.Combine(prefix, value);
                }
                if ((m.Groups[1].Value == "working_dir" ? value : Path.GetDirectoryName(value)) is { Length: > 0 } folder)
                    yield return folder;
            }
        }
    }

    /// <summary>
    /// A Lutris game file's <c>exe:</c> or <c>working_dir:</c> line; group 2 is the path.
    /// </summary>
    [GeneratedRegex(@"^\s*(exe|working_dir):\s*(\S.*)$", RegexOptions.Multiline)]
    private static partial Regex LutrisExe();

    /// <summary>
    /// A Lutris game file's Wine <c>prefix:</c> line; group 1 is the path.
    /// </summary>
    [GeneratedRegex(@"^\s*prefix:\s*(\S.*)$", RegexOptions.Multiline)]
    private static partial Regex LutrisPrefix();

    /// <summary>
    /// The folder a <c>.desktop</c> launcher that is named Kenshi starts from: its <c>Path=</c>, else the folder of the program in its <c>Exec=</c>
    /// (a Wine/Heroic/Lutris launcher puts the game's exe there; only absolute paths count). Looks in <paramref name="directory"/> and two levels of
    /// folders below it (Wine keeps its launchers in <c>wine/Programs</c>).
    /// </summary>
    public static IEnumerable<string> DesktopEntries(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        var found = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.desktop", new EnumerationOptions { RecurseSubdirectories = true,
                    MaxRecursionDepth = 2, IgnoreInaccessible = true }))
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                var name = DesktopField(text, "Name");
                if (name is null || !name.Contains("Kenshi", StringComparison.OrdinalIgnoreCase)) continue;
                if (DesktopField(text, "Path") is { Length: > 0 } path) found.Add(path);
                if (DesktopField(text, "Exec") is { } exec)
                    foreach (Match m in ExecPath().Matches(exec))
                    {
                        // Only a program's absolute path: not WINEPREFIX=/x, a URL (steam://...) or a switch like /unix (whose folder is the root).
                        var program = m.Groups[1].Value.Trim('"', '\'');
                        if (Path.IsPathRooted(program) && !program.Contains('=') && Path.GetDirectoryName(program) is { Length: > 0 } folder
                                && Path.GetPathRoot(folder) != folder)
                            found.Add(folder);
                    }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var path in found) yield return path;
    }

    /// <summary>
    /// The value of the first <c><paramref name="key"/>=</c> line of a <c>.desktop</c> file's text, trimmed; null when it has none.
    /// </summary>
    static string? DesktopField(string text, string key)
    {
        var m = Regex.Match(text, "^" + key + @"=(.*)$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// A path in a <c>.desktop</c> <c>Exec=</c> line: a quoted or bare token holding a slash or backslash.
    /// </summary>
    [GeneratedRegex(@"(""[^""]*[/\\][^""]*""|[^\s""]*[/\\][^\s""]*)")]
    private static partial Regex ExecPath();

    /// <summary>
    /// Windows: the uninstall entry of Kenshi (<c>InstallLocation</c>, else the folder of its <c>DisplayIcon</c> or <c>UninstallString</c>) from the
    /// machine's and the user's list of installed programs, then GOG's own <c>PATH</c> under <c>GOG.com\Games</c>, and the usual GOG folders on every
    /// fixed drive. Nothing off Windows.
    /// </summary>
    static IEnumerable<string> Registered()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        List<string> paths = [];
        void Uninstall(Microsoft.Win32.RegistryKey hive, string key)
        {
            using var list = hive.OpenSubKey(key);
            foreach (var id in list?.GetSubKeyNames() ?? [])
            {
                using var entry = list!.OpenSubKey(id);
                if (entry?.GetValue("DisplayName") is not string name || !name.Contains("Kenshi", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (entry.GetValue("InstallLocation") is string location && location.Length > 0)
                    paths.Add(location.Trim('"'));
                foreach (var value in new[] { "DisplayIcon", "UninstallString" })
                    if (entry.GetValue(value) is string command
                            && Path.GetDirectoryName(command.Split(',')[0].Trim().Trim('"')) is { Length: > 0 } folder)
                        paths.Add(folder);
            }
        }
        try
        {
            foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                Uninstall(Microsoft.Win32.Registry.LocalMachine, key);
                Uninstall(Microsoft.Win32.Registry.CurrentUser, key);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        foreach (var path in paths) yield return path;
        foreach (var path in Gog()) yield return path;
    }

    /// <summary>
    /// GOG on Windows: the <c>PATH</c> of the GOG game whose <c>GAMENAME</c> is Kenshi under <c>HKLM\SOFTWARE\WOW6432Node\GOG.com\Games</c>, and the
    /// usual GOG folders on every fixed drive. Nothing off Windows.
    /// </summary>
    static IEnumerable<string> Gog()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        List<string> paths = [];
        try
        {
            using var games = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            foreach (var id in games?.GetSubKeyNames() ?? [])
            {
                using var game = games!.OpenSubKey(id);
                if (game?.GetValue("GAMENAME") is string name && name.Contains("Kenshi", StringComparison.OrdinalIgnoreCase)
                        && game.GetValue("PATH") is string path)
                    paths.Add(path);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        foreach (var path in paths) yield return path;
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            foreach (var path in InPrefix(drive.RootDirectory.FullName)) yield return path;
    }
}