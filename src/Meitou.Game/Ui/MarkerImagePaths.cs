namespace Meitou.Game.Ui;

/// <summary>
/// Where the interface extensions' map marker images may come from (docs/modding.md "Map markers"): a path relative to the folder of the mod the
/// extension belongs to, never outside it (no rooted path, no <c>..</c> out of it), and at most <see cref="MaxPerMod"/> different files per
/// extension. It also remembers which problems were reported, so each is reported once.
/// </summary>
sealed class MarkerImagePaths
{
    /// <summary>
    /// The most different image files one extension's markers may name in a session.
    /// </summary>
    internal const int MaxPerMod = 64;

    /// <summary>
    /// Each extension's mod folder, full, ending in a separator.
    /// </summary>
    readonly Dictionary<string, string> _folders = new(StringComparer.Ordinal);

    /// <summary>
    /// The image files each extension's markers have named (full paths), for <see cref="MaxPerMod"/>.
    /// </summary>
    readonly Dictionary<string, HashSet<string>> _used = new(StringComparer.Ordinal);

    /// <summary>
    /// The problems reported so far (extension, image and problem).
    /// </summary>
    readonly HashSet<(string Owner, string Image, string Problem)> _reported = [];

    /// <summary>
    /// How paths compare on this system.
    /// </summary>
    static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Records the mod folder of extension <paramref name="owner"/>.
    /// </summary>
    internal void SetFolder(string owner, string folder) =>
        _folders[owner] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;

    /// <summary>
    /// Forgets extension <paramref name="owner"/> (it was removed).
    /// </summary>
    internal void Remove(string owner)
    {
        _folders.Remove(owner);
        _used.Remove(owner);
    }

    /// <summary>
    /// Why extension <paramref name="owner"/>'s marker image <paramref name="image"/> cannot be shown, or null when it can, with
    /// <paramref name="full"/> its full path. <paramref name="check"/> judges the file itself (exists, readable, its size); null refuses every image.
    /// </summary>
    internal string? Resolve(string owner, string image, Func<string, string?>? check, out string full)
    {
        full = "";
        if (check is null) return "this interface shows no image markers";
        if (!_folders.TryGetValue(owner, out var folder)) return "the extension belongs to no mod folder";
        if (string.IsNullOrWhiteSpace(image) || Path.IsPathRooted(image)) return "an image is named by a path relative to the mod's folder";

        full = Path.GetFullPath(Path.Combine(folder, image));
        if (!full.StartsWith(folder, PathComparison)) return "the path leads outside the mod's folder";

        var used = _used.TryGetValue(owner, out var known) ? known : _used[owner] = new(StringComparer.Ordinal);
        if (!used.Contains(full) && used.Count >= MaxPerMod) return $"more than {MaxPerMod} different image files";
        if (check(full) is { } problem) return problem;

        used.Add(full);
        return null;
    }

    /// <summary>
    /// True the first time this problem of this image of this extension is reported.
    /// </summary>
    internal bool FirstReport(string owner, string image, string problem) => _reported.Add((owner, image, problem));
}
