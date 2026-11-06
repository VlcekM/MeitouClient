namespace Meitou.Content;

/// <summary>The code-mod API range a game runs (docs/modding.md "Versioning"); <c>ModApi</c> in Meitou.Modding mirrors these for mods.</summary>
public static class CodeModApi
{
    public const int Newest = 2;
    public const int Oldest = 1;

    /// <summary>The first API whose manifests may use <c>dependencies</c>, <c>loadAfter</c>, <c>incompatible</c> and <c>gameVersion</c>.</summary>
    public const int Dependencies = 2;
}

/// <summary>
/// The rules about one code mod that need no code of it (docs/modding-dependencies.md "Rules"): shared by the game's loader, which refuses on them, and the launcher's
/// preliminary check, which shows them. What only the loader can know (DLLs, entry type, initialization) is not here.
/// </summary>
public static class CodeModChecks
{
    /// <summary>The API range, and the API 2 keys an older manifest may not use.</summary>
    public static string? ApiProblem(CodeModManifest manifest)
    {
        if (manifest.ApiVersion is < CodeModApi.Oldest or > CodeModApi.Newest)
            return $"built for code-mod API {manifest.ApiVersion}; this Meitou runs API {CodeModApi.Oldest} to {CodeModApi.Newest}";
        var keys = manifest.Api2Keys().ToList();
        if (keys.Count > 0 && manifest.ApiVersion < CodeModApi.Dependencies)
            return $"uses {string.Join(", ", keys)}, which need apiVersion {CodeModApi.Dependencies}; it names {manifest.ApiVersion}";
        return null;
    }

    /// <summary><c>gameVersion</c> against the running Meitou; no game version (an untagged build) passes, as does no range.</summary>
    public static string? GameVersionProblem(CodeModManifest manifest, CodeModVersion? game)
    {
        if (manifest.GameVersion is not { } range || game is not { } version || range.ContainsByPrecedence(version)) return null;
        return $"built for Meitou {range}; this is {version}";
    }

    /// <summary>A <c>loadAfter</c> mod that is enabled but later in the list.</summary>
    public static string? LoadAfterProblem(CodeModManifest manifest, int self, IReadOnlyList<CodeModEntry> list)
    {
        foreach (var id in manifest.LoadAfter)
        {
            int at = CodeModDependencies.IndexOf(id, list);
            if (at > self && list[at].Enabled) return $"must load after {list[at].Name} ('{id}'); move {list[at].Name} above it in the mod list";
        }
        return null;
    }

    /// <summary>An earlier enabled mod that conflicts with this one (declared by either side); the later of the two is refused.</summary>
    public static string? ConflictProblem(CodeModManifest manifest, int self, IReadOnlyList<CodeModEntry> list)
    {
        for (int i = 0; i < self; i++)
            if (list[i] is { Enabled: true, Manifest: { } other } && manifest.ConflictsWith(other))
                return $"is incompatible with {list[i].Name} ('{other.Id}'), which loads first; disable one of them";
        return null;
    }

    /// <summary>An earlier enabled mod with the same id.</summary>
    public static string? DuplicateProblem(CodeModManifest manifest, int self, IReadOnlyList<CodeModEntry> list)
    {
        for (int i = 0; i < self; i++)
            if (list[i] is { Enabled: true, Manifest: { } other } && other.Id == manifest.Id) return $"id '{manifest.Id}' is taken by mod {list[i].Name}";
        return null;
    }

    /// <summary>
    /// The launcher's preliminary check: for each enabled code mod, in load order, the reasons the game would refuse it that are known without its code, by mod
    /// name. A dependency that will not load (its own problem, or not allowed to run code when <paramref name="trusted"/> is given) refuses its dependents too.
    /// Passing it does not promise a mod runs: its DLLs and initialization are checked only at start.
    /// </summary>
    public static Dictionary<string, List<string>> Check(IReadOnlyList<CodeModEntry> list, IReadOnlySet<string>? trusted = null, CodeModVersion? game = null)
    {
        var problems = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var blocked = new Dictionary<string, string>(StringComparer.Ordinal);   // id -> why it will not load
        var seen = new HashSet<string>(StringComparer.Ordinal);                // the first enabled mod with an id is the one dependents get
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].Enabled) continue;
            var lines = ProblemsOf(i, list, game, blocked);
            if (lines.Count > 0) problems[list[i].Name] = lines;
            if (list[i].Manifest is not { } manifest || !seen.Add(manifest.Id)) continue;
            if (lines.Count > 0) blocked[manifest.Id] = lines[0];
            else if (trusted is not null && !trusted.Contains(list[i].Name)) blocked[manifest.Id] = "it is not allowed to run code";
        }
        return problems;
    }

    static List<string> ProblemsOf(int self, IReadOnlyList<CodeModEntry> list, CodeModVersion? game, Dictionary<string, string> blocked)
    {
        var entry = list[self];
        if (entry.ManifestError is { } error) return [$"bad {ModList.CodeManifestFile}: {error}"];
        if (entry.Manifest is not { } manifest) return [];
        string?[] own =
        [
            ApiProblem(manifest), DuplicateProblem(manifest, self, list), GameVersionProblem(manifest, game),
            ConflictProblem(manifest, self, list), LoadAfterProblem(manifest, self, list),
        ];
        var lines = own.OfType<string>().ToList();
        foreach (var dependency in manifest.DependsOn)
            if (DependencyProblem(dependency, self, list, blocked) is { } problem) lines.Add(problem);
        return lines;
    }

    static string? DependencyProblem(CodeModDependency dependency, int self, IReadOnlyList<CodeModEntry> list, Dictionary<string, string> blocked)
    {
        if (CodeModDependencies.Problem(dependency, self, list) is { } problem) return problem;
        if (!blocked.TryGetValue(dependency.Id, out var why)) return null;
        int at = CodeModDependencies.IndexOf(dependency.Id, list);
        string name = at >= 0 ? list[at].Name : dependency.Id;
        return dependency.Optional ? null : $"needs {name} ('{dependency.Id}'), which will not load: {why}";
    }
}
