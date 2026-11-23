using Meitou.Content;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// A Proton found in Steam (Linux): its folder, which holds the <c>proton</c> script, and the Steam folder it runs under.
/// </summary>
sealed record ProtonInstall(string Name, string Directory, string SteamRoot)
{
    /// <summary>
    /// The <c>proton</c> script, run with <c>python3</c>.
    /// </summary>
    public string Script => Path.Combine(Directory, "proton");

    /// <summary>
    /// The Protons of this machine's Steam, by folder name: <c>steamapps/common/Proton*</c> of every Steam library (Steam's
    /// <c>libraryfolders.vdf</c>) and every folder of <c>compatibilitytools.d</c> holding a <c>proton</c> script. None off Linux, without
    /// Steam, or for Flatpak Steam (its Proton runs inside the sandbox only).
    /// </summary>
    public static List<ProtonInstall> Find()
    {
        if (!OperatingSystem.IsLinux()) return [];

        var steamRoots = GameInstall.SteamFolders().Where(folder => !folder.Contains("/.var/app/", StringComparison.Ordinal)).ToList();
        if (steamRoots.Count == 0) return [];

        string steamRoot = RealPath(steamRoots[0]);
        var candidates = GameInstall.SteamLibraries()
            .SelectMany(library => Folders(Path.Combine(library, "steamapps", "common")).Where(IsProtonName))
            .Concat(steamRoots.SelectMany(root => Folders(Path.Combine(root, "compatibilitytools.d"))));
        return [.. candidates
            .Where(folder => File.Exists(Path.Combine(folder, "proton")))
            .Select(RealPath)
            .Distinct(StringComparer.Ordinal)
            .Select(folder => new ProtonInstall(Path.GetFileName(folder), folder, steamRoot))
            .OrderBy(proton => proton.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// <paramref name="path"/> as the Windows build sees it under Proton: Wine's <c>Z:</c> drive is the Unix root, with backslashes.
    /// </summary>
    public static string WindowsPath(string path) => "Z:" + Path.GetFullPath(path).Replace('/', '\\');

    /// <summary>
    /// Whether a <c>steamapps/common</c> folder is one of Valve's Protons (<c>Proton 9.0</c>, <c>Proton - Experimental</c>).
    /// </summary>
    static bool IsProtonName(string folder) => Path.GetFileName(folder).StartsWith("Proton", StringComparison.Ordinal);

    /// <summary>
    /// The folders in <paramref name="parent"/>; none when it is missing or cannot be read.
    /// </summary>
    static IEnumerable<string> Folders(string parent)
    {
        try
        {
            return System.IO.Directory.Exists(parent) ? System.IO.Directory.GetDirectories(parent) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// <paramref name="path"/> with its symbolic links resolved (<c>~/.steam/steam</c> is a link to Steam's folder), so one Proton is listed once.
    /// </summary>
    static string RealPath(string path)
    {
        try
        {
            return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(path);
        }
    }
}
