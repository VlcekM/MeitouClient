namespace Meitou.Content;

/// <summary>
/// The code-mod API range a game runs (docs/modding.md "Versioning"); <c>ModApi</c> in Meitou.Modding mirrors these for mods.
/// </summary>
public static class CodeModApi
{
    /// <summary>
    /// The newest code-mod API this build implements; a manifest asking for a later one is refused.
    /// </summary>
    public const int Newest = 2;
    /// <summary>
    /// The oldest code-mod API this build still loads.
    /// </summary>
    public const int Oldest = 1;

    /// <summary>
    /// The first API whose manifests may use <c>dependencies</c>, <c>loadAfter</c>, <c>incompatible</c> and <c>gameVersion</c>.
    /// </summary>
    public const int Dependencies = 2;
}

/// <summary>
/// One load pass so far, as the rules see it: the code mods that loaded (in the launcher: will load) and why the others did not. The loader and the
/// launcher both judge a mod against this, never against the mods merely enabled earlier, so a mod that is refused blocks nothing.
/// </summary>
public sealed class CodeModPass
{
    /// <summary>
    /// The mods loaded so far in load order, with their manifests.
    /// </summary>
    readonly List<(string Name, CodeModManifest Manifest)> _loaded = [];
    /// <summary>
    /// Why each refused mod did not load, by mod name.
    /// </summary>
    readonly Dictionary<string, string> _refused = new(StringComparer.Ordinal);

    /// <summary>
    /// The reason each refused mod did not load, by mod name.
    /// </summary>
    public IReadOnlyDictionary<string, string> Refusals => _refused;

    /// <summary>
    /// Records that the mod <paramref name="name"/> loaded (in the launcher: will load).
    /// </summary>
    public void Loaded(string name, CodeModManifest manifest) => _loaded.Add((name, manifest));

    /// <summary>
    /// Records that the mod <paramref name="name"/> did not load, and why; a later refusal of the same name replaces the reason.
    /// </summary>
    public void Refused(string name, string reason) => _refused[name] = reason;

    /// <summary>
    /// The loaded mod with this manifest id, if any.
    /// </summary>
    public (string Name, CodeModManifest Manifest)? LoadedWithId(string id)
    {
        foreach (var mod in _loaded)
            if (mod.Manifest.Id == id) return mod;
        return null;
    }

    /// <summary>
    /// The first loaded mod that matches.
    /// </summary>
    public (string Name, CodeModManifest Manifest)? FirstLoaded(Func<CodeModManifest, bool> match)
    {
        foreach (var mod in _loaded)
            if (match(mod.Manifest)) return mod;
        return null;
    }
}

/// <summary>
/// The rules about one code mod that need no code of it (docs/modding-dependencies.md "Rules"): shared by the game's loader, which refuses on the
/// first, and the launcher's preliminary check, which shows them all. What only the loader can know (DLLs, entry type, initialization) is not here.
/// </summary>
public static class CodeModChecks
{
    /// <summary>
    /// Every reason the mod at <paramref name="self"/> of <paramref name="list"/> (load order, disabled mods included) is refused before its code is
    /// opened, judged against what <paramref name="pass"/> has loaded; empty when none.
    /// </summary>
    public static List<string> Problems(CodeModManifest manifest, int self, IReadOnlyList<CodeModEntry> list, CodeModPass pass, CodeModVersion? game)
    {
        string?[] own =
        [
            ApiProblem(manifest), GameVersionProblem(manifest, game), DuplicateProblem(manifest, pass),
            ConflictProblem(manifest, pass), LoadAfterProblem(manifest, self, list),
        ];
        var lines = own.OfType<string>().ToList();
        foreach (var dependency in manifest.DependsOn)
            if (DependencyProblem(dependency, self, list, pass) is { } problem) lines.Add(problem);
        return lines;
    }

    /// <summary>
    /// The API range, and the API 2 keys an older manifest may not use.
    /// </summary>
    public static string? ApiProblem(CodeModManifest manifest)
    {
        if (manifest.ApiVersion is < CodeModApi.Oldest or > CodeModApi.Newest)
            return $"built for code-mod API {manifest.ApiVersion}; this Meitou runs API {CodeModApi.Oldest} to {CodeModApi.Newest}";
        var keys = manifest.Api2Keys().ToList();
        if (keys.Count > 0 && manifest.ApiVersion < CodeModApi.Dependencies)
            return $"uses {string.Join(", ", keys)}, which need apiVersion {CodeModApi.Dependencies}; it names {manifest.ApiVersion}";
        return null;
    }

    /// <summary>
    /// <c>gameVersion</c> against the running Meitou's release (a nightly counts as the release it leads to; docs/modding-dependencies.md
    /// "Manifest"). No game version (a build without a <c>v*</c> tag) passes, as does no range.
    /// </summary>
    public static string? GameVersionProblem(CodeModManifest manifest, CodeModVersion? game)
    {
        if (manifest.GameVersion is not { } range || game is not { } version || range.Contains(version.Release))
            return null;
        return $"built for Meitou {range}; this is {version}";
    }

    /// <summary>
    /// A <c>loadAfter</c> mod that is enabled but later in the list.
    /// </summary>
    public static string? LoadAfterProblem(CodeModManifest manifest, int self, IReadOnlyList<CodeModEntry> list)
    {
        foreach (var id in manifest.LoadAfter)
        {
            int at = CodeModDependencies.IndexOf(id, list);
            if (at > self && list[at].Enabled)
                return $"must load after {list[at].Name} ('{id}'); move {list[at].Name} above it in the mod list";
        }
        return null;
    }

    /// <summary>
    /// A loaded mod that conflicts with this one (declared by either side); the later of the two is refused.
    /// </summary>
    static string? ConflictProblem(CodeModManifest manifest, CodeModPass pass)
    {
        if (pass.FirstLoaded(manifest.ConflictsWith) is not { } rival) return null;
        return $"is incompatible with {rival.Name} ('{rival.Manifest.Id}'), which loads first; disable one of them";
    }

    /// <summary>
    /// A loaded mod with the same id.
    /// </summary>
    static string? DuplicateProblem(CodeModManifest manifest, CodeModPass pass) =>
        pass.LoadedWithId(manifest.Id) is { } twin ? $"id '{manifest.Id}' is taken by mod {twin.Name}, which loads first" : null;

    /// <summary>
    /// What is wrong with one dependency (docs/modding-dependencies.md "Rules"): a loaded one of a version out of range; else not installed, not
    /// enabled, later in the list, or earlier but refused (its reason). An optional one that is absent, disabled or refused is left out, not a
    /// problem.
    /// </summary>
    static string? DependencyProblem(CodeModDependency dependency, int self, IReadOnlyList<CodeModEntry> list, CodeModPass pass)
    {
        if (pass.LoadedWithId(dependency.Id) is { } loaded)
            return VersionProblem(dependency, loaded.Name, loaded.Manifest.Version);
        int earlier = LastEnabledBefore(dependency.Id, self, list);
        int at = CodeModDependencies.IndexOf(dependency.Id, list);
        if (earlier < 0 && at > self && list[at].Enabled)
            return $"needs {list[at].Name} ('{dependency.Id}'), which loads after it; move {list[at].Name} above it in the mod list";
        if (dependency.Optional) return null;
        if (earlier >= 0)
            return $"needs {list[earlier].Name} ('{dependency.Id}'), which is refused: {WhyRefused(list[earlier].Name, pass)}";
        if (at >= 0) return $"needs {list[at].Name} ('{dependency.Id}'), which is not enabled";
        return $"needs code mod '{dependency.Id}', which is not installed";
    }

    /// <summary>
    /// The refusal when the installed version of a dependency is outside its range; null when it fits.
    /// </summary>
    static string? VersionProblem(CodeModDependency dependency, string name, CodeModVersion? installed)
    {
        if (dependency.Accepts(installed)) return null;
        return $"needs {name} ('{dependency.Id}') {dependency.Range}; {installed?.ToString() ?? "a version without a number"} is installed";
    }

    /// <summary>
    /// The reason the mod <paramref name="name"/> did not load in <paramref name="pass"/>, else a generic one.
    /// </summary>
    static string WhyRefused(string name, CodeModPass pass) => pass.Refusals.GetValueOrDefault(name) ?? "it did not load";

    /// <summary>
    /// The last enabled mod before <paramref name="self"/> with this id (the one that would have served); -1 when none.
    /// </summary>
    static int LastEnabledBefore(string id, int self, IReadOnlyList<CodeModEntry> list)
    {
        for (int i = Math.Min(self, list.Count) - 1; i >= 0; i--)
            if (list[i].Enabled && list[i].Id == id) return i;
        return -1;
    }

    /// <summary>
    /// The launcher's preliminary check: for each enabled code mod, in load order, the reasons the game would refuse it that are known without its
    /// code, by mod name. A mod with a problem will not load, so it serves no
    /// dependent and blocks no later mod. Passing does not promise a mod runs: its DLLs and initialization are checked only at start.
    /// </summary>
    public static Dictionary<string, List<string>> Check(
        IReadOnlyList<CodeModEntry> list, CodeModVersion? game = null)
    {
        var problems = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var pass = new CodeModPass();
        for (int i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            if (!entry.Enabled || (entry.Manifest is null && entry.ManifestError is null)) continue;
            var lines = entry.Manifest is { } manifest
                ? Problems(manifest, i, list, pass, game)
                : [$"bad {ModList.CodeManifestFile}: {entry.ManifestError}"];
            if (lines.Count > 0)
            {
                problems[entry.Name] = lines;
                pass.Refused(entry.Name, lines[0]);
            }
            else pass.Loaded(entry.Name, entry.Manifest!);
        }
        return problems;
    }
}