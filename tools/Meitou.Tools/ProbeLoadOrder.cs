using Meitou.Content;
using Meitou.Data;

/// <summary>
/// The load order a probe runs: the install's (<c>mods.cfg</c>), or a feedback report's given with <c>--load-order &lt;file&gt;</c> (one mod name
/// per line, as the report lists them: <c>1. `Name.mod`</c> or a bare name). Its mods are found among the install's mods and workshop items and in
/// each <c>--mod-dir &lt;folder&gt;</c> (a folder of mod folders or of workshop item folders, as a steamcmd download leaves them).
/// </summary>
static class ProbeLoadOrder
{
    /// <summary>
    /// Takes <c>--load-order</c> and every <c>--mod-dir</c> out of <paramref name="args"/> and returns the load order they name, or the install's
    /// without <c>--load-order</c>. A mod found nowhere is left out and said on standard error.
    /// </summary>
    public static LoadOrder Take(GameInstall install, List<string> args)
    {
        var dirs = new List<string>();
        while (TakeOption(args, "--mod-dir") is { } dir) dirs.Add(dir);
        if (TakeOption(args, "--load-order") is not { } file) return LoadOrder.FromInstall(install);

        var order = new LoadOrder();
        var found = Found(install, dirs);
        var mods = Names(file).Where(n => !IsBase(n)).Select(n => n + ".mod");
        foreach (var name in LoadOrder.BaseFiles.Concat(mods).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Locate(install, found, name) is { } path) order.Entries.Add(new LoadOrderEntry(name, path));
            else
                Console.Error.WriteLine($"load order: {name} not found");
        }

        return order;
    }

    /// <summary>
    /// Removes <paramref name="name"/> and its value from <paramref name="args"/>; null when it is not there.
    /// </summary>
    public static string? TakeOption(List<string> args, string name)
    {
        int at = args.IndexOf(name);
        if (at < 0 || at + 1 >= args.Count) return null;

        string value = args[at + 1];
        args.RemoveRange(at, 2);
        return value;
    }

    /// <summary>
    /// The mod names a load order file lists, without list numbers, backticks or the <c>.mod</c> extension.
    /// </summary>
    static IEnumerable<string> Names(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            string name = line.Trim().TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' ', '-').Trim().Trim('`');
            if (name.EndsWith(".mod", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".base", StringComparison.OrdinalIgnoreCase))
                name = name[..name.LastIndexOf('.')];
            if (name.Length > 0) yield return name;
        }
    }

    /// <summary>
    /// Whether a listed name is one of the core files (<see cref="LoadOrder.BaseFiles"/>), which come first whatever the list says.
    /// </summary>
    static bool IsBase(string name) =>
        LoadOrder.BaseFiles.Any(b => string.Equals(System.IO.Path.GetFileNameWithoutExtension(b), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The mods on disk by file name without extension: the install's, then those under the extra folders (first found wins).
    /// </summary>
    static Dictionary<string, string> Found(GameInstall install, List<string> dirs)
    {
        var found = new Dictionary<string, string>(LoadOrder.FindMods(install), StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var mod in Directory.EnumerateFiles(dir, "*.mod", SearchOption.AllDirectories))
                found.TryAdd(System.IO.Path.GetFileNameWithoutExtension(mod), mod);
        }

        return found;
    }

    /// <summary>
    /// Where a load order entry's file is: a base file in the install's data folder, else a found mod; null when nowhere.
    /// </summary>
    static string? Locate(GameInstall install, Dictionary<string, string> found, string name)
    {
        string inData = System.IO.Path.Combine(install.DataDirectory, name);
        if (LoadOrder.BaseFiles.Contains(name, StringComparer.OrdinalIgnoreCase) && File.Exists(inData)) return inData;

        return found.GetValueOrDefault(System.IO.Path.GetFileNameWithoutExtension(name));
    }
}
