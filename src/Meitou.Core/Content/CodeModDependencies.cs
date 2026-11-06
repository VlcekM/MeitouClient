namespace Meitou.Content;

/// <summary>
/// A mod of the mod list as the dependency rules see it: its name, its code manifest (null: data only or unreadable), whether it is enabled and its FCS header
/// <c>dependencies</c> (<c>.mod</c> file names; docs/formats/fcs-mod.md "File"), which only <see cref="CodeModDependencies.Arrange"/> reads.
/// </summary>
public sealed record CodeModEntry(string Name, CodeModManifest? Manifest, bool Enabled, IReadOnlyList<string>? HeaderDependencies = null)
{
    /// <summary>Why the mod's <c>meitou.json</c> could not be read; null when it was, or when the mod has none.</summary>
    public string? ManifestError { get; init; }
}

/// <summary>One ordering constraint of <see cref="CodeModDependencies.Arrange"/>: <see cref="Before"/> must load before <see cref="After"/>, for <see cref="Why"/>.</summary>
public sealed record OrderConstraint(string Before, string After, string Why)
{
    public override string ToString() => $"{After} after {Before} ({Why})";
}

/// <summary>
/// What Arrange proposes: the new order, or, when the constraints contradict each other, the unchanged order and the constraints that form a cycle.
/// </summary>
public sealed record ArrangeResult(IReadOnlyList<string> Order, IReadOnlyList<OrderConstraint> Cycle)
{
    public bool HasCycle => Cycle.Count > 0;
}

/// <summary>
/// The code-mod dependency rules shared by the game's loader and the launcher (docs/modding.md "Dependencies"). <c>mods.cfg</c> is the only load order: a dependency
/// must be enabled, earlier in the list, and of a version its range accepts. Nothing here reorders on its own; <see cref="Arrange"/> is the launcher's proposed fix.
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

    /// <summary>
    /// The launcher's Arrange: one graph of every constraint between enabled mods, the code-mod <c>dependencies</c> (optional ones too, as a present one must load
    /// first) and the FCS header <c>dependencies</c>, then the order that keeps the current one as far as the constraints allow (each step takes the earliest mod
    /// whose prerequisites are placed). Disabled mods add no constraint and keep their place among the rest. A cycle, including a code constraint contradicting
    /// a data one, changes nothing and is returned. Enables nothing and fixes no version.
    /// </summary>
    public static ArrangeResult Arrange(IReadOnlyList<CodeModEntry> list)
    {
        var constraints = Constraints(list);
        var prerequisites = list.Select((_, i) => constraints.Where(c => c.After == i).Select(c => c.Before).ToHashSet()).ToList();
        var order = StableTopologicalOrder(prerequisites);
        if (order.Count == list.Count) return new ArrangeResult([.. order.Select(i => list[i].Name)], []);
        var stuck = Enumerable.Range(0, list.Count).Except(order).ToHashSet();
        return new ArrangeResult([.. list.Select(m => m.Name)], FindCycle(stuck, constraints, list));
    }

    /// <summary>Every constraint between two enabled mods, by position, with its reason.</summary>
    static List<(int Before, int After, string Why)> Constraints(IReadOnlyList<CodeModEntry> list)
    {
        var constraints = new List<(int, int, string)>();
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].Enabled) continue;
            foreach (var dependency in list[i].Manifest?.DependsOn ?? [])
                if (EnabledIndexOf(m => m.Manifest?.Id == dependency.Id, list) is var at and >= 0 && at != i)
                    constraints.Add((at, i, $"code: needs '{dependency.Id}'"));
            foreach (var id in list[i].Manifest?.LoadAfter ?? [])
                if (EnabledIndexOf(m => m.Manifest?.Id == id, list) is var at and >= 0 && at != i)
                    constraints.Add((at, i, $"code: loads after '{id}'"));
            foreach (var file in list[i].HeaderDependencies ?? [])
                if (EnabledIndexOf(m => m.Name + ".mod" == file, list) is var at and >= 0 && at != i)
                    constraints.Add((at, i, $"data: its .mod header needs {file}"));
        }
        return constraints;
    }

    static int EnabledIndexOf(Func<CodeModEntry, bool> match, IReadOnlyList<CodeModEntry> list)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].Enabled && match(list[i])) return i;
        return -1;
    }

    /// <summary>Positions in an order where each comes after its prerequisites, the earliest ready one first; stops short when the rest wait on each other.</summary>
    static List<int> StableTopologicalOrder(IReadOnlyList<HashSet<int>> prerequisites)
    {
        var waiting = Enumerable.Range(0, prerequisites.Count).ToList();
        var placed = new HashSet<int>();
        var order = new List<int>(waiting.Count);
        while (waiting.Count > 0)
        {
            int next = waiting.FindIndex(i => prerequisites[i].IsSubsetOf(placed));
            if (next < 0) break;
            placed.Add(waiting[next]);
            order.Add(waiting[next]);
            waiting.RemoveAt(next);
        }
        return order;
    }

    /// <summary>
    /// One cycle among the mods that could not be placed: every one of them waits on another of them, so following prerequisites from any of them repeats.
    /// </summary>
    static List<OrderConstraint> FindCycle(HashSet<int> stuck, List<(int Before, int After, string Why)> constraints, IReadOnlyList<CodeModEntry> list)
    {
        var path = new List<(int Before, int After, string Why)>();
        var seen = new Dictionary<int, int>();
        int at = stuck.Min();
        while (!seen.ContainsKey(at))
        {
            seen[at] = path.Count;
            var edge = constraints.First(c => c.After == at && stuck.Contains(c.Before));
            path.Add(edge);
            at = edge.Before;
        }
        return [.. path.Skip(seen[at]).Reverse().Select(c => new OrderConstraint(list[c.Before].Name, list[c.After].Name, c.Why))];
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

    /// <summary>A mod's manifest, or why it cannot be read; both null for a mod without one. Never throws.</summary>
    public static (CodeModManifest? Manifest, string? Error) ReadManifest(ModInfo mod)
    {
        try { return mod.HasCode ? (CodeModManifest.Read(mod.CodeManifestPath), null) : (null, null); }
        catch (Exception e) when (e is not OutOfMemoryException) { return (null, e.Message); }
    }

    /// <summary>
    /// Reads a mod's manifest for the rules, or null when it has none or it cannot be read (the loader reports that itself, per mod). A scan over many mods
    /// must survive any one of them, so no failure of this mod's manifest escapes.
    /// </summary>
    public static CodeModManifest? TryReadManifest(ModInfo mod)
    {
        return ReadManifest(mod).Manifest;
    }
}
