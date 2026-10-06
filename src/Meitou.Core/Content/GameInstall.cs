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
    /// from <c>bin/</c> finds the repository root's file). Returns null when none is configured.
    /// </summary>
    public static GameInstall? Locate()
    {
        foreach (var candidate in Candidates())
            if (!string.IsNullOrWhiteSpace(candidate) && IsValid(candidate))
                return new GameInstall(Path.GetFullPath(candidate));
        return null;
    }

    static IEnumerable<string?> Candidates()
    {
        yield return Environment.GetEnvironmentVariable(EnvironmentVariable);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                yield return ReadLocalConfig(Path.Combine(dir.FullName, LocalConfigFile));
    }

    static string? ReadLocalConfig(string file)
    {
        if (!File.Exists(file)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.TryGetProperty("kenshiPath", out var p) ? p.GetString() : null;
    }
}
