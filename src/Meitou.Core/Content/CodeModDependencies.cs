namespace Meitou.Content;

/// <summary>
/// A mod of the mod list as the dependency rules see it: its name, its code manifest (null: data only or unreadable), whether it is enabled and its
/// FCS header <c>dependencies</c> (<c>.mod</c> file names; docs/formats/fcs-mod.md "File"), which only <see cref="CodeModDependencies.Arrange"/>
/// reads.
/// </summary>
public sealed record CodeModEntry(string Name, CodeModManifest? Manifest, bool Enabled, IReadOnlyList<string>? HeaderDependencies = null)
{
    /// <summary>
    /// Why the mod's <c>meitou.json</c> could not be read; null when it was, or when the mod has none.
    /// </summary>
    public string? ManifestError { get; init; }

    /// <summary>
    /// The manifest id; for an unreadable manifest, the <c>id</c> it still names (so dependents can name it), else null.
    /// </summary>
    public string? Id
    {
        get => field ?? Manifest?.Id;
        init;
    }
}

/// <summary>
/// One ordering constraint of <see cref="CodeModDependencies.Arrange"/>: <see cref="Before"/> must load before <see cref="After"/>, for
/// <see cref="Why"/>.
/// </summary>
public sealed record OrderConstraint(string Before, string After, string Why)
{
    public override string ToString() => $"{After} after {Before} ({Why})";
}

/// <summary>
/// What Arrange proposes: the new order, or, when the constraints contradict each other, the unchanged order and the constraints that form a cycle.
/// </summary>
public sealed record ArrangeResult(IReadOnlyList<string> Order, IReadOnlyList<OrderConstraint> Cycle)
{
    /// <summary>
    /// Whether Arrange found a cycle and so left the order unchanged.
    /// </summary>
    public bool HasCycle => Cycle.Count > 0;
}

/// <summary>
/// The launcher's Arrange and the reading of mods for the rules (docs/modding-dependencies.md "Launcher"). <c>mods.cfg</c> is the only load order:
/// nothing here reorders on its own; <see cref="Arrange"/> is the launcher's proposed fix.
/// </summary>
public static class CodeModDependencies
{
    /// <summary>
    /// The launcher's Arrange: one graph of every constraint between enabled mods, the code-mod <c>dependencies</c> (optional ones too, as a present
    /// one must load first), <c>loadAfter</c> and the FCS header <c>dependencies</c>. Each mod keeps its place unless a mod it needs comes later;
    /// then that dependency moves up to just before it (its own prerequisites first), so the rest keeps its order and data overrides change least.
    /// Disabled mods add no constraint. A cycle, including a code constraint contradicting a data one, changes nothing and is returned. Enables
    /// nothing and fixes no version.
    /// </summary>
    public static ArrangeResult Arrange(IReadOnlyList<CodeModEntry> list)
    {
        var sort = new DependenciesFirst(list.Count, Constraints(list));
        if (sort.Run()) return new ArrangeResult([.. sort.Order.Select(i => list[i].Name)], []);
        var cycle = sort.Cycle.Select(c => new OrderConstraint(list[c.Before].Name, list[c.After].Name, c.Why));
        return new ArrangeResult([.. list.Select(m => m.Name)], [.. cycle]);
    }

    /// <summary>
    /// Every constraint between two enabled mods, by position, with its reason.
    /// </summary>
    static List<(int Before, int After, string Why)> Constraints(IReadOnlyList<CodeModEntry> list)
    {
        var constraints = new List<(int, int, string)>();
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].Enabled) continue;
            foreach (var dependency in list[i].Manifest?.DependsOn ?? [])
                if (EnabledIndexOf(m => m.Id == dependency.Id, list) is var at and >= 0 && at != i)
                    constraints.Add((at, i, $"code: needs '{dependency.Id}'"));
            foreach (var id in list[i].Manifest?.LoadAfter ?? [])
                if (EnabledIndexOf(m => m.Id == id, list) is var at and >= 0 && at != i)
                    constraints.Add((at, i, $"code: loads after '{id}'"));
            foreach (var file in list[i].HeaderDependencies ?? [])
                if (EnabledIndexOf(m => m.Name + ".mod" == file, list) is var at and >= 0 && at != i)
                    constraints.Add((at, i, $"data: its .mod header needs {file}"));
        }
        return constraints;
    }

    /// <summary>
    /// The index of the first enabled entry of <paramref name="list"/> that <paramref name="match"/> accepts; -1 when none.
    /// </summary>
    static int EnabledIndexOf(Func<CodeModEntry, bool> match, IReadOnlyList<CodeModEntry> list)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].Enabled && match(list[i])) return i;
        return -1;
    }

    /// <summary>
    /// A depth-first order over positions in list order: before a mod is placed, its unplaced prerequisites are, in their list order. A prerequisite
    /// still being placed closes a cycle, which is kept as the constraints along it.
    /// </summary>
    sealed class DependenciesFirst(int count, List<(int Before, int After, string Why)> constraints)
    {
        /// <summary>
        /// 0 waiting, 1 being placed, 2 placed per position.
        /// </summary>
        readonly byte[] _state = new byte[count];
        /// <summary>
        /// The constraints from the first mod being placed down to the one being placed now; a cycle is cut from it.
        /// </summary>
        readonly List<(int Before, int After, string Why)> _path = [];

        /// <summary>
        /// The positions in placing order, filled by <see cref="Run"/>.
        /// </summary>
        public List<int> Order { get; } = new(count);
        /// <summary>
        /// The constraints along the cycle <see cref="Run"/> stopped at; empty when there was none.
        /// </summary>
        public List<(int Before, int After, string Why)> Cycle { get; private set; } = [];

        /// <summary>
        /// Places every position in list order; false when a cycle stops it (<see cref="Cycle"/> then holds it).
        /// </summary>
        public bool Run()
        {
            for (int i = 0; i < count; i++)
                if (!Place(i)) return false;
            return true;
        }

        /// <summary>
        /// Places <paramref name="mod"/> after its unplaced prerequisites, lowest position first; false when one of them closes a cycle.
        /// </summary>
        bool Place(int mod)
        {
            if (_state[mod] == 2) return true;
            _state[mod] = 1;
            foreach (var edge in constraints.Where(c => c.After == mod).OrderBy(c => c.Before))
            {
                if (_state[edge.Before] == 1)
                {
                    Cycle = [.. _path.SkipWhile(e => e.After != edge.Before), edge];
                    return false;
                }
                _path.Add(edge);
                if (!Place(edge.Before)) return false;
                _path.RemoveAt(_path.Count - 1);
            }
            _state[mod] = 2;
            Order.Add(mod);
            return true;
        }
    }

    /// <summary>
    /// The position of the mod with manifest id <paramref name="id"/>: the first enabled one, else the first; -1 when none has it.
    /// </summary>
    public static int IndexOf(string id, IReadOnlyList<CodeModEntry> list)
    {
        int first = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Id != id) continue;
            if (list[i].Enabled) return i;
            if (first < 0) first = i;
        }
        return first;
    }

    /// <summary>
    /// A mod as the rules see it: its manifest, or why it cannot be read (and the id it still names); both null for a mod without one. Never throws,
    /// so a scan over many mods survives any one of them; the loader reports a bad manifest itself when it reaches that mod.
    /// </summary>
    public static CodeModEntry ReadEntry(ModInfo mod, bool enabled)
    {
        if (!mod.HasCode) return new CodeModEntry(mod.Name, null, enabled);
        try
        {
            return new CodeModEntry(mod.Name, CodeModManifest.Read(mod.CodeManifestPath), enabled);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new CodeModEntry(mod.Name, null, enabled) { ManifestError = e.Message, Id = CodeModManifest.TryReadId(mod.CodeManifestPath) };
        }
    }
}