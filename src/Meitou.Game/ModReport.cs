using Meitou.Content;
using Meitou.Data;
using Meitou.Localisation;

namespace Meitou.Game;

/// <summary>
/// One line of the Settings MODS page: a mod in load order with the header of its file and the data problems found in it, or a
/// <c>[1]---[...]---</c> separator of mods.cfg (<paramref name="Version"/> null, no issues listed on the row). A mod whose file was not found has no version either.
/// </summary>
sealed record ModRow(string Name, int? Version, string Author, string Description, IReadOnlyList<string> Issues, bool Separator = false)
{
    /// <summary>The row of mod <paramref name="name"/>: its file's header (none when the file was not found) and the problems filed under its file.</summary>
    public static ModRow Of(string name, GameDatabase db, ILookup<string, GameDataIssue> issues, IReadOnlyDictionary<string, string>? refused = null)
    {
        string file = name + ".mod";
        var problems = issues[file].Select(i => i.ToString()).ToList();
        if (refused is not null && refused.TryGetValue(name, out var reason)) problems.Insert(0, ModReport.RefusedText(reason));
        if (!db.Headers.TryGetValue(file, out var header)) return new(name, null, "", "", problems);

        return new(name, header.Version, header.Author, header.Description, problems);
    }
}

/// <summary>The mods the game data was loaded from, for the Settings MODS page and its summary line (docs/formats/overview.md, "Load order").</summary>
sealed class ModReport(List<ModRow> rows, List<string> otherIssues, int enabled, int loaded, string? customList)
{
    public IReadOnlyList<ModRow> Rows => rows;

    /// <summary>The issue line of a code mod the game refused to load.</summary>
    public static string RefusedText(string reason) => $"{Loc.Tr("Script mod not loaded")}: {reason}";

    /// <summary>Problems in no mod's file: the core files and the translations.</summary>
    public IReadOnlyList<string> OtherIssues => otherIssues;

    /// <summary>Mods the list file names, separator files included (no duplicates or core files).</summary>
    public int Enabled => enabled;

    /// <summary>Those of them whose .mod file was found and applied; never more than <see cref="Enabled"/>.</summary>
    public int Loaded => loaded;

    /// <summary>The file name of the <c>--mods-cfg</c> list; null: the install's own data/mods.cfg.</summary>
    public string? CustomList => customList;

    public int Problems => rows.Sum(r => r.Issues.Count) + otherIssues.Count;

    /// <summary>"Mods: 40 loaded", "Mods: 38 of 40 loaded", each with ", 11 problems" when there are some.</summary>
    public string Summary
    {
        get
        {
            string text = loaded == enabled ? Loc.Tr("Mods: {1} loaded", loaded) : Loc.Tr("Mods: {1} of {2} loaded", loaded, enabled);
            return Problems == 0 ? text : $"{text}, {Loc.Plural("{1} problem", "{1} problems", Problems, Problems)}";
        }
    }

    /// <summary>A "[1]---[...]---" dummy mod file that groups the load order; a mod that merely starts with a bracket is a mod.</summary>
    static bool IsSeparator(string name) => name.StartsWith('[') && name.Contains("---", StringComparison.Ordinal);

    /// <param name="refused">The code mods the game refused to load, by mod name, with the reason; each counts as an issue of its row.</param>
    public static ModReport Build(GameInstall install, GameDatabase db, IReadOnlyDictionary<string, string>? refused = null)
    {
        var issues = db.Issues.ToLookup(i => i.File, StringComparer.Ordinal);
        var rows = new List<ModRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var path = ModList.ModsCfgPath(install);
        foreach (var line in File.Exists(path) ? File.ReadAllLines(path) : [])
        {
            if (ModList.ParseModsCfg([line]).FirstOrDefault() is not { } name || !seen.Add(name)) continue;

            rows.Add(IsSeparator(name) ? new(name + ".mod", null, "", "", [], true) : ModRow.Of(name, db, issues, refused));
        }

        var shown = rows.Where(r => !r.Separator).Select(r => r.Name + ".mod").ToHashSet(StringComparer.Ordinal);
        var other = db.Issues.Where(i => !shown.Contains(i.File)).Select(i => i.ToString()).ToList();
        // A refused mod that mods.cfg does not list has no row: its reason goes with the problems no row owns.
        foreach (var (mod, reason) in refused ?? new Dictionary<string, string>())
            if (!seen.Contains(mod)) other.Add($"{mod}: {RefusedText(reason)}");
        int loaded = seen.Count(n => db.Headers.ContainsKey(n + ".mod"));
        string? custom = ModList.ModsCfgOverride is { } file ? Path.GetFileName(file) : null;
        return new(rows, other, seen.Count, loaded, custom);
    }
}
