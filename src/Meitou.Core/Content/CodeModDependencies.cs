namespace Meitou.Content;

/// <summary>A mod of the mod list as the dependency rules see it: its name, its code manifest (null: data only or unreadable) and whether it is enabled.</summary>
public sealed record CodeModEntry(string Name, CodeModManifest? Manifest, bool Enabled);

/// <summary>
/// The code-mod dependency rules shared by the game's loader and the launcher (docs/modding.md "Dependencies"). <c>mods.cfg</c> is the only load order: a dependency
/// must be enabled, earlier in the list, and of a version its range accepts. Nothing here reorders on its own; <see cref="Arrange"/> is the launcher's best-effort fix.
/// </summary>
public static class CodeModDependencies
{
    /// <summary>
    /// What is wrong with one dependency of the mod at <paramref name="self"/> in <paramref name="list"/> (the list in load order), or null when nothing is. An absent or disabled
    /// optional dependency is not a problem; a present one must still load first.
    /// </summary>
    public static string? Problem(CodeModDependency dependency, int self, IReadOnlyList<CodeModEntry> list)
    {
        int at = IndexOf(dependency.Id, list);
        if (at < 0) return dependency.Optional ? null : $"needs code mod '{dependency.Id}', which is not installed";
        var target = list[at];
        if (!target.Enabled) return dependency.Optional ? null : $"needs {target.Name} ('{dependency.Id}'), which is not enabled";
        if (at > self) return $"needs {target.Name} ('{dependency.Id}'), which loads after it; move {target.Name} above it in the mod list";
        if (!dependency.Accepts(target.Manifest!.Version))
            return $"needs {target.Name} ('{dependency.Id}') {dependency.Range}; {target.Manifest.Version?.ToString() ?? "a version without a number"} is installed";
        return null;
    }

    /// <summary>Every problem of every enabled code mod in <paramref name="list"/>, by mod name; mods without problems are absent.</summary>
    public static Dictionary<string, List<string>> Check(IReadOnlyList<CodeModEntry> list)
    {
        var problems = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].Enabled || list[i].Manifest is not { } manifest) continue;
            foreach (var dependency in manifest.DependsOn)
                if (Problem(dependency, i, list) is { } problem)
                {
                    if (!problems.TryGetValue(list[i].Name, out var lines)) problems[list[i].Name] = lines = [];
                    lines.Add(problem);
                }
        }
        return problems;
    }

    /// <summary>
    /// A best-effort order (the launcher's Arrange button): the same names, each mod moved after the installed mods it depends on (optional ones too), otherwise
    /// keeping the current order. Mods in a dependency cycle keep their relative order after the rest. Enables nothing and fixes no version.
    /// </summary>
    public static List<string> Arrange(IReadOnlyList<CodeModEntry> list)
    {
        var waiting = new List<int>();
        for (int i = 0; i < list.Count; i++) waiting.Add(i);
        var placed = new HashSet<int>();
        var order = new List<string>(list.Count);
        while (waiting.Count > 0)
        {
            int next = waiting.FindIndex(i => Prerequisites(i, list).All(placed.Contains));
            if (next < 0) break;
            placed.Add(waiting[next]);
            order.Add(list[waiting[next]].Name);
            waiting.RemoveAt(next);
        }
        order.AddRange(waiting.Select(i => list[i].Name));
        return order;
    }

    /// <summary>The positions of the installed mods the mod at <paramref name="self"/> depends on.</summary>
    static IEnumerable<int> Prerequisites(int self, IReadOnlyList<CodeModEntry> list)
    {
        if (list[self].Manifest is not { } manifest) yield break;
        foreach (var dependency in manifest.DependsOn)
        {
            int at = IndexOf(dependency.Id, list);
            if (at >= 0 && at != self) yield return at;
        }
    }

    /// <summary>The position of the mod with manifest id <paramref name="id"/>: the first enabled one, else the first; -1 when none has it.</summary>
    public static int IndexOf(string id, IReadOnlyList<CodeModEntry> list)
    {
        int first = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Manifest?.Id != id) continue;
            if (list[i].Enabled) return i;
            if (first < 0) first = i;
        }
        return first;
    }

    /// <summary>Reads a mod's manifest for the rules, or null when it has none or it cannot be read (the loader reports that itself).</summary>
    public static CodeModManifest? TryReadManifest(ModInfo mod)
    {
        if (!mod.HasCode) return null;
        try { return CodeModManifest.Read(mod.CodeManifestPath); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
    }
}
