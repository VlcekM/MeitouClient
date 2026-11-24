namespace Meitou.Game.Ui;

/// <summary>
/// The code mods' replacements of the game's MyGUI layout files (docs/modding.md "Layout replacements"): a layout the interface loads by name is
/// read from the mod's file instead. One mod per layout, the first in load order; kept for the process, as the mods' equipment slots are.
/// </summary>
public static class ModLayouts
{
    /// <summary>
    /// The replacement of each layout file name (case-insensitive, with its <c>.layout</c>): the owning mod and the full path of its file.
    /// </summary>
    static readonly Dictionary<string, (string Owner, string Path)> Files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether any layout is replaced.
    /// </summary>
    public static bool Any => Files.Count > 0;

    /// <summary>
    /// The layout file name <paramref name="layout"/> stands for (a file name, its <c>.layout</c> added when missing); null for one with a folder in
    /// it or blank.
    /// </summary>
    public static string? FileName(string layout)
    {
        if (string.IsNullOrWhiteSpace(layout) || layout.IndexOfAny(['/', '\\', ':']) >= 0) return null;

        return layout.EndsWith(".layout", StringComparison.OrdinalIgnoreCase) ? layout : layout + ".layout";
    }

    /// <summary>
    /// Replaces <paramref name="layout"/> (a checked file name) with <paramref name="path"/> for <paramref name="owner"/>; false (nothing changed)
    /// when another mod replaced it already.
    /// </summary>
    internal static bool Add(string owner, string layout, string path)
    {
        if (Files.TryGetValue(layout, out var taken) && taken.Owner != owner) return false;

        Files[layout] = (owner, path);
        return true;
    }

    /// <summary>
    /// <paramref name="resolve"/> (the interface's asset lookup) with the replaced layouts answered by their mods' files.
    /// </summary>
    internal static Func<string, string?> Over(Func<string, string?> resolve)
    {
        if (!Any) return resolve;

        return file => Files.TryGetValue(Path.GetFileName(file), out var replaced) && File.Exists(replaced.Path) ? replaced.Path : resolve(file);
    }

    /// <summary>
    /// Removes every replacement (tests).
    /// </summary>
    internal static void Clear() => Files.Clear();
}
