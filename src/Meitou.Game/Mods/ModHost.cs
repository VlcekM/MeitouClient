using System.Reflection;
using HarmonyLib;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Population;

namespace Meitou.Modding;

/// <summary>
/// A code mod that loaded: its mod, manifest, assemblies and entry instance.
/// </summary>
public sealed class LoadedMod
{
    /// <summary>
    /// A mod whose manifest has been read; its assemblies, entry instance, dependencies and exports are filled in as it loads.
    /// </summary>
    internal LoadedMod(ModInfo info, CodeModManifest manifest)
    {
        Info = info;
        Manifest = manifest;
    }

    /// <summary>
    /// The mod: its name, folder and data.
    /// </summary>
    public ModInfo Info { get; }
    /// <summary>
    /// The mod's code manifest.
    /// </summary>
    public CodeModManifest Manifest { get; }
    /// <summary>
    /// The mod's own assemblies, as its manifest lists them, loaded into its load context.
    /// </summary>
    public IReadOnlyList<Assembly> Assemblies { get; internal set; } = [];
    /// <summary>
    /// The mod's entry instance (the manifest's <c>entry</c> type); null until it is made.
    /// </summary>
    public IMod? Instance { get; internal set; }

    /// <summary>
    /// The declared dependencies that loaded (docs/modding-dependencies.md), in manifest order; an absent optional one is not here.
    /// </summary>
    public IReadOnlyList<LoadedMod> Dependencies { get; internal set; } = [];

    /// <summary>
    /// What a mod depending on this one resolves from it, by simple name: this mod's own <see cref="Assemblies"/> and, transitively, what its
    /// dependencies export, so a framework's types exist once. Other DLLs in its folder stay private.
    /// </summary>
    public IReadOnlyDictionary<string, Assembly> Exports { get; internal set; } = new Dictionary<string, Assembly>();
}

/// <summary>
/// Where a <see cref="ModHost"/> loads from: the enabled code mods in order, the merged data and the running world.
/// </summary>
/// <param name="SavedSettings">The player's saved settings of a mod by its id (setting key to text), or null.</param>
/// <param name="GameVersion">The running Meitou for <c>gameVersion</c> ranges; null passes every range.</param>
/// <param name="Disabled">The installed code mods not enabled, only so a dependent's refusal says "not enabled" rather than "not installed".</param>
public sealed record ModHostOptions(
    IReadOnlyList<ModInfo> Enabled, GameDatabase Data, PopulationData Population, Func<World?> World,
    Func<string, IReadOnlyDictionary<string, string>?>? SavedSettings = null, CodeModVersion? GameVersion = null,
    IReadOnlyList<ModInfo>? Disabled = null);

/// <summary>
/// The code mods of a session (docs/modding.md): loads the enabled ones at startup, runs their <see cref="IMod.Initialize"/> once, and holds what
/// they registered for the host: the simulation extensions every world is built with, the interface extensions, the debug rows, and the save data of
/// their systems. A mod that fails anywhere is left out with a log line and a notice; the game goes on.
/// </summary>
public sealed class ModHost
{
    /// <summary>
    /// The reason of a mod refused as one built for another version of the game (<see cref="CodeModFaults.BuiltForAnother"/>), whose notice is
    /// <see cref="CodeModFaults.Notice"/>.
    /// </summary>
    const string Outdated = "built for another version of the game";

    /// <summary>
    /// Every loaded mod's committed registrations.
    /// </summary>
    readonly ModRegistry _registry = new();
    /// <summary>
    /// The mods that loaded, in load order.
    /// </summary>
    readonly List<LoadedMod> _mods = [];
    /// <summary>
    /// The player's notices, one line per mod that was left out.
    /// </summary>
    readonly List<string> _notices = [];
    /// <summary>
    /// The load pass: which mods loaded and why the others were refused, for the dependency checks.
    /// </summary>
    readonly CodeModPass _pass = new();
    /// <summary>
    /// The mods whose code <see cref="Prepare"/> loaded ahead of their <see cref="IMod.Initialize"/>, with their place in the load order, in order; a
    /// mod whose data phase throws leaves it. Empty for a host made by <see cref="Load"/>.
    /// </summary>
    readonly List<(LoadedMod Mod, int Index)> _prepared = [];
    /// <summary>
    /// The pass of <see cref="Prepare"/>: the mods whose code loaded, for the dependency checks made before any mod is initialized.
    /// </summary>
    readonly CodeModPass _preparedPass = new();
    /// <summary>
    /// The enabled and disabled code mods <see cref="Prepare"/> was given, as the dependency checks read them.
    /// </summary>
    IReadOnlyList<CodeModEntry> _entries = [];
    /// <summary>
    /// The running Meitou <see cref="Prepare"/> was given, for <c>gameVersion</c> ranges.
    /// </summary>
    CodeModVersion? _gameVersion;

    /// <summary>
    /// Made only by <see cref="Load"/> and <see cref="Prepare"/>.
    /// </summary>
    ModHost() { }

    /// <summary>
    /// The mods that loaded, in load order.
    /// </summary>
    public IReadOnlyList<LoadedMod> Mods => _mods;

    /// <summary>
    /// What went wrong or was left out, one line per mod, for the player's notices.
    /// </summary>
    public IReadOnlyList<string> Notices => _notices;

    /// <summary>
    /// The reason each refused code mod was not loaded, by mod name (the <c>.mod</c> file's stem, as in mods.cfg).
    /// </summary>
    public IReadOnlyDictionary<string, string> Refused => _pass.Refusals;

    /// <summary>
    /// What every world is built with; null without registrations.
    /// </summary>
    public SimulationExtensions? Simulation => _registry.Simulation.IsEmpty ? null : _registry.Simulation;

    /// <summary>
    /// Every mod's interface extensions, in load order.
    /// </summary>
    public IReadOnlyList<IUiExtension> UiExtensions => _registry.Ui;
    /// <summary>
    /// Every mod's debug menu commands, in load order.
    /// </summary>
    public IReadOnlyList<ModDebugCommand> DebugCommands => _registry.DebugCommands;
    /// <summary>
    /// Every mod's key actions, in load order.
    /// </summary>
    public IReadOnlyList<Game.Host.Input.ModKeyAction> KeyActions => _registry.KeyActions;

    /// <summary>
    /// A mod registered a combat hit feed: the host then gives each world a <see cref="World.CombatHitFeed"/> and drains it every frame.
    /// </summary>
    public bool WantsCombatHits => _registry.HitFeeds.Count > 0;

    /// <summary>
    /// The hits published since the last frame to every mod's feed, in load order, on the host's thread; a feed that throws is logged and gets
    /// no more.
    /// </summary>
    public void DeliverCombatHits(IReadOnlyList<Simulation.Combat.CombatHit> hits)
    {
        if (hits.Count == 0) return;
        foreach (var feed in _registry.HitFeeds)
        {
            if (feed.Faulted) continue;
            try
            {
                feed.Run(hits);
            }
            catch (Exception e)
            {
                feed.Faulted = true;
                Console.Error.WriteLine($"mod       {feed.Owner}: combat hit feed disabled: {e.GetType().Name}: {e.Message}");
                CodeModFaults.Tell(feed.Owner, e);
            }
        }
    }

    /// <summary>
    /// The mods' display-only effects (docs/modding.md "Visual effects"), played by the host once a frame.
    /// </summary>
    internal ModEffects Effects => _registry.Effects;

    /// <summary>
    /// The mods' display-only glows (docs/modding.md "Glow"), handed to the renderer by the host once a frame.
    /// </summary>
    internal ModGlows Glows => _registry.Glows;

    /// <summary>
    /// The mods' display-only ribbon trails (docs/modding.md "Trails"), sampled and handed to the particles by the host once a frame.
    /// </summary>
    internal ModTrails Trails => _registry.Trails;

    /// <summary>
    /// A mod registered a projectile feed: the host then compares the shown snapshots' bolts every frame.
    /// </summary>
    public bool WantsProjectiles => _registry.ProjectileFeeds.Count > 0;

    /// <summary>
    /// The bolts' changes since the last frame to every mod's projectile feed, in load order, on the host's thread; a feed that throws is logged
    /// and gets no more.
    /// </summary>
    public void DeliverProjectiles(IReadOnlyList<ProjectileEvent> changes)
    {
        if (changes.Count == 0) return;

        foreach (var feed in _registry.ProjectileFeeds)
        {
            if (feed.Faulted) continue;

            try
            {
                feed.Run(changes);
            }
            catch (Exception e)
            {
                feed.Faulted = true;
                Console.Error.WriteLine($"mod       {feed.Owner}: projectile feed disabled: {e.GetType().Name}: {e.Message}");
                CodeModFaults.Tell(feed.Owner, e);
            }
        }
    }

    /// <summary>
    /// Whether a mod arranges the faction tab's list.
    /// </summary>
    public bool ArrangesFactionList => _registry.FactionLists.Count > 0;

    /// <summary>
    /// The faction tab's rows as every mod's arranger leaves them, in load order, on the host's thread; one that throws (or returns null) is logged,
    /// skipped and gets no more calls.
    /// </summary>
    public IReadOnlyList<FactionListRow> ArrangeFactionList(IReadOnlyList<FactionListRow> rows)
    {
        foreach (var list in _registry.FactionLists)
        {
            if (list.Faulted) continue;

            try
            {
                rows = list.Run(rows) ?? throw new InvalidOperationException("returned no rows");
            }
            catch (Exception e)
            {
                list.Faulted = true;
                Console.Error.WriteLine($"mod       {list.Owner}: faction list arranger disabled: {e.GetType().Name}: {e.Message}");
                CodeModFaults.Tell(list.Owner, e);
            }
        }
        return rows;
    }

    /// <summary>
    /// The settings the mods registered, in load order (docs/modding.md "Settings").
    /// </summary>
    public IReadOnlyList<ModSettingsGroup> Settings => _registry.Settings;

    /// <summary>
    /// The record types mods claimed (number, mod's name for it).
    /// </summary>
    public IReadOnlyDictionary<int, string> RecordTypes => _registry.RecordTypes;

    /// <summary>
    /// The enabled mods of <c>mods.cfg</c>, in order, that carry code (<see cref="ModInfo.HasCode"/>).
    /// </summary>
    public static List<ModInfo> EnabledCodeMods(GameInstall install) => CodeMods(install).Enabled;

    /// <summary>
    /// The code mods on disk: the enabled ones in <c>mods.cfg</c> order, then the others.
    /// </summary>
    /// <remarks>A folder that cannot be read gives none (logged): a failing scan must not stop a game that may have no code mods at all.</remarks>
    public static (List<ModInfo> Enabled, List<ModInfo> Disabled) CodeMods(GameInstall install)
    {
        try
        {
            var discovered = ModList.Discover(install);
            var onDisk = discovered.Where(m => m.HasCode).ToDictionary(m => m.Name, StringComparer.Ordinal);
            List<ModInfo> enabled = [];
            foreach (string name in ModList.ReadEnabled(install))
            {
                if (onDisk.GetValueOrDefault(name) is { } code) enabled.Add(code);
            }
            return (enabled, [.. onDisk.Values.Except(enabled)]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"mods      the mod folders could not be read, no code mods: {e.Message}");
            return ([], []);
        }
    }

    /// <summary>
    /// Loads and initializes the code mods, in order. Never throws for a mod's fault.
    /// </summary>
    public static ModHost Load(ModHostOptions options)
    {
        var host = new ModHost();
        var entries = Entries(options.Enabled, options.Disabled);
        for (int i = 0; i < options.Enabled.Count; i++) host.TryLoad(options.Enabled[i], options, entries, i);
        return host;
    }

    /// <summary>
    /// The dependency checks' view of the enabled code mods, in order, then the disabled ones.
    /// </summary>
    static List<CodeModEntry> Entries(IReadOnlyList<ModInfo> enabled, IReadOnlyList<ModInfo>? disabled) =>
    [
        .. enabled.Select(m => CodeModDependencies.ReadEntry(m, enabled: true)),
        .. (disabled ?? []).Select(m => CodeModDependencies.ReadEntry(m, enabled: false)),
    ];

    /// <summary>
    /// The first half of <see cref="Load"/> for a session with a data phase (docs/modding.md "Data phase"): checks and loads the code of the enabled
    /// mods, in order, and makes their entry instances, without initializing them; then <see cref="EditData"/> and <see cref="Start"/>. A refusal
    /// here is the same log line, notice and reason as in <see cref="Load"/>. Never throws for a mod's fault.
    /// </summary>
    public static ModHost Prepare(IReadOnlyList<ModInfo> enabled, IReadOnlyList<ModInfo>? disabled = null, CodeModVersion? gameVersion = null)
    {
        var host = new ModHost { _entries = Entries(enabled, disabled), _gameVersion = gameVersion };
        for (int i = 0; i < enabled.Count; i++) host.TryPrepare(enabled[i], i);
        return host;
    }

    /// <summary>
    /// Loads one mod's code ahead of its initialization (<see cref="Prepare"/>); a failure refuses it with a log line and a notice.
    /// </summary>
    void TryPrepare(ModInfo info, int index)
    {
        try
        {
            var mod = Prepare(info, _entries, index, _gameVersion, _preparedPass, [.. _prepared.Select(p => p.Mod)]);
            _prepared.Add((mod, index));
            _preparedPass.Loaded(info.Name, mod.Manifest);
        }
        catch (ModLoadException e)
        {
            RefuseEarly(info.Name, e.Message);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod       {info.Name}: {e}");
            RefuseEarly(info.Name, CodeModFaults.BuiltForAnother(e) ? Outdated : $"failed to start: {e.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Leaves a mod out before the initializations (<see cref="Refuse"/>), its reason in both passes.
    /// </summary>
    void RefuseEarly(string mod, string reason)
    {
        Refuse(mod, reason);
        _preparedPass.Refused(mod, reason);
    }

    /// <summary>
    /// The data phase (docs/modding.md "Data phase"): each prepared mod named in <paramref name="phased"/> (the mods <see cref="ModDataScan"/> found
    /// declaring <see cref="IModDataPhase"/>, by name), in load order, edits <paramref name="data"/>; its queued changes are checked whole and then
    /// merged when it returns, so the next mod reads them. One that throws or queues a change that cannot be merged has none of its changes merged
    /// and is refused; a mod with a required dependency that was refused or skipped here, directly or through others, is skipped (its own
    /// dependency check then refuses it at <see cref="Start"/>). Never throws for a mod's fault. On the thread reading the data, before anything is
    /// built from it.
    /// </summary>
    public void EditData(GameDatabase data, IReadOnlySet<string> phased)
    {
        var lost = new HashSet<LoadedMod>();
        foreach (var (mod, index) in _prepared.ToList())
        {
            if (LostDependency(mod, lost) is { } gone)
            {
                lost.Add(mod);
                if (phased.Contains(mod.Info.Name))
                    Console.Error.WriteLine($"mod       {mod.Manifest.Id}: data phase skipped: its dependency '{gone.Manifest.Id}' was left out");
                continue;
            }
            if (!phased.Contains(mod.Info.Name))
            {
                if (mod.Instance is IModDataPhase) NoteUnscannedDataPhase(mod);
                continue;
            }
            if (mod.Instance is not IModDataPhase editor) continue;

            if (!TryEditData(mod, index, editor, data)) lost.Add(mod);
        }
    }

    /// <summary>
    /// The log line for a mod whose entry is an <see cref="IModDataPhase"/> the metadata scan did not find (a base class in another assembly, or
    /// metadata the scan cannot read), whose <see cref="IModDataPhase.EditData"/> therefore never runs.
    /// </summary>
    internal static void NoteUnscannedDataPhase(LoadedMod mod) =>
        Console.Error.WriteLine($"mod       {mod.Manifest.Id}: implements IModDataPhase, but not on its entry type or a base class in the same "
            + "assembly, so its EditData does not run (declare IModDataPhase on the entry type)");

    /// <summary>
    /// The first required dependency of <paramref name="mod"/> in <paramref name="lost"/> (refused or skipped in this data phase), or null.
    /// </summary>
    static LoadedMod? LostDependency(LoadedMod mod, HashSet<LoadedMod> lost)
    {
        foreach (var dependency in mod.Dependencies)
            if (lost.Contains(dependency) && IsRequired(mod, dependency)) return dependency;

        return null;
    }

    /// <summary>
    /// Whether <paramref name="mod"/>'s manifest requires <paramref name="dependency"/> (not <c>optional</c>).
    /// </summary>
    static bool IsRequired(LoadedMod mod, LoadedMod dependency) =>
        mod.Manifest.DependsOn.FirstOrDefault(d => d.Id == dependency.Manifest.Id) is { Optional: false };

    /// <summary>
    /// Runs one mod's <see cref="IModDataPhase.EditData"/>, checks its changes and merges them; false when the mod was refused (a throw, or a change
    /// that cannot be merged), with none of its changes merged.
    /// </summary>
    bool TryEditData(LoadedMod mod, int index, IModDataPhase editor, GameDatabase data)
    {
        var context = new ModDataContext(mod, data);
        List<FcsRecord> changes = [];
        string? failure;
        try
        {
            editor.EditData(context);
            context.Open = false;

            // Copied before the check, so a mod's thread still writing to the records it queued cannot change them between check and merge.
            changes = ModDataChanges.Snapshot(context.Changes);
            failure = ModDataChanges.Problem(changes) is { } problem ? $"a game data change it made cannot be applied: {problem}" : null;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod       {mod.Info.Name}: {e}");
            failure = CodeModFaults.BuiltForAnother(e) ? Outdated : $"failed to change the game data: {e.GetBaseException().Message}";
        }
        finally
        {
            context.Open = false;
        }

        if (failure is null && TryApplyChanges(context, changes, data)) return true;

        _prepared.Remove((mod, index));
        Refuse(mod.Info.Name, failure ?? "its game data changes failed to apply");
        return false;
    }

    /// <summary>
    /// A data phase's checked changes (<paramref name="changes"/>, the copy of the mod's queue) into <paramref name="data"/>, in order, as made by
    /// the mod's own data file, and their fingerprint into <see cref="GameDatabase.CodeEdits"/>. False when the merge threw, which the check before
    /// it rules out; the changes merged before the throw then stay (a merge cannot be undone in place), and the line says so.
    /// </summary>
    static bool TryApplyChanges(ModDataContext context, List<FcsRecord> changes, GameDatabase data)
    {
        if (changes.Count == 0) return true;

        try
        {
            foreach (var change in changes) data.Change(change, context.FileName);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod       {context.Id}: data phase changes failed part way and are partly applied: {e}");
            return false;
        }

        data.CodeEdits.Add(ModDataChanges.Fingerprint(context.Id, changes));
        Console.WriteLine($"mod       {context.Id}: data phase changed {changes.Count} record(s)");
        return true;
    }

    /// <summary>
    /// The second half of <see cref="Load"/> after <see cref="Prepare"/>: initializes the prepared mods in load order, each checked again against the
    /// mods initialized before it (a dependency that failed to initialize refuses it, as in <see cref="Load"/>). <paramref name="options"/> gives the
    /// data, population, world and settings; its mod lists are not read (<see cref="Prepare"/>'s are). Never throws for a mod's fault.
    /// </summary>
    public void Start(ModHostOptions options)
    {
        foreach (var (mod, index) in _prepared) TryStart(mod, index, options);
    }

    /// <summary>
    /// Initializes one prepared mod; a failure refuses it with a log line and a notice.
    /// </summary>
    void TryStart(LoadedMod mod, int index, ModHostOptions options)
    {
        try
        {
            if (CodeModChecks.Problems(mod.Manifest, index, _entries, _pass, _gameVersion) is [var problem, ..])
                throw new ModLoadException(problem);
            DropUnstartedDependencies(mod);
            Initialize(mod, options);
            Started(mod);
        }
        catch (ModLoadException e)
        {
            Refuse(mod.Info.Name, e.Message);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod       {mod.Info.Name}: {e}");
            Refuse(mod.Info.Name, CodeModFaults.BuiltForAnother(e) ? Outdated : $"failed to start: {e.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Takes the optional dependencies that loaded their code but did not initialize out of <paramref name="mod"/>'s, each with the log line of
    /// <see cref="LoadedDependencies"/>; the mod's assemblies stay bound to what those exported.
    /// </summary>
    void DropUnstartedDependencies(LoadedMod mod)
    {
        foreach (var gone in mod.Dependencies.Where(d => !_mods.Contains(d)))
            Console.WriteLine($"mod       {mod.Manifest.Id}: optional dependency '{gone.Manifest.Id}' did not load; going on without it");
        mod.Dependencies = [.. mod.Dependencies.Where(_mods.Contains)];
    }

    /// <summary>
    /// Loads one mod (manifest, assemblies, entry instance, <see cref="IMod.Initialize"/>); a failure refuses it with a log line and a notice.
    /// </summary>
    void TryLoad(ModInfo info, ModHostOptions options, IReadOnlyList<CodeModEntry> entries, int index)
    {
        LoadedMod? mod = null;
        try
        {
            mod = Prepare(info, entries, index, options.GameVersion, _pass, _mods);
            Initialize(mod, options);
            Started(mod);
        }
        catch (ModLoadException e)
        {
            Refuse(info.Name, e.Message);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod       {info.Name}: {e}");
            Refuse(info.Name, CodeModFaults.BuiltForAnother(e) ? Outdated : $"failed to start: {e.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Counts an initialized mod as loaded, with its log line.
    /// </summary>
    void Started(LoadedMod mod)
    {
        _mods.Add(mod);
        _pass.Loaded(mod.Info.Name, mod.Manifest);
        string assemblies = string.Join(", ", mod.Assemblies.Select(a => a.GetName().Name));
        Console.WriteLine($"mod       {mod.Manifest.Id}: loaded from {mod.Info.Name} ({assemblies})");
    }

    /// <summary>
    /// Leaves a mod out: a log line, a notice (for one built for another version of the game, <see cref="CodeModFaults.Notice"/>), and its reason
    /// for the dependency checks.
    /// </summary>
    void Refuse(string mod, string reason)
    {
        Console.Error.WriteLine($"mod       {mod}: not loaded: {reason}");
        _notices.Add(reason == Outdated ? CodeModFaults.Notice(mod) : $"Mod {mod} not loaded: {reason}");
        _pass.Refused(mod, reason);
    }

    /// <summary>
    /// The manifest checks against <paramref name="pass"/>, the assemblies with the exports of the <paramref name="loaded"/> mods it depends on, and
    /// the entry instance.
    /// </summary>
    static LoadedMod Prepare(ModInfo info, IReadOnlyList<CodeModEntry> entries, int index, CodeModVersion? gameVersion, CodeModPass pass,
        IReadOnlyList<LoadedMod> loaded)
    {
        CodeModManifest manifest;
        try
        {
            manifest = CodeModManifest.Read(info.CodeManifestPath);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new ModLoadException($"bad {ModList.CodeManifestFile}: {e.Message}");
        }
        if (CodeModChecks.Problems(manifest, index, entries, pass, gameVersion) is [var problem, ..])
            throw new ModLoadException(problem);
        var mod = new LoadedMod(info, manifest);
        mod.Dependencies = LoadedDependencies(manifest, loaded);
        var imports = Imports(mod.Dependencies);
        mod.Assemblies = LoadAssemblies(info, manifest, imports);
        mod.Exports = Exported(mod.Assemblies, imports);
        mod.Instance = CreateEntry(mod);
        return mod;
    }

    /// <summary>
    /// The loaded mods this one depends on, in manifest order (docs/modding-dependencies.md "Rules"). The shared checks have already refused it for a
    /// required one that is not loaded; an optional one that is absent or did not load is left out.
    /// </summary>
    static List<LoadedMod> LoadedDependencies(CodeModManifest manifest, IReadOnlyList<LoadedMod> loaded)
    {
        var resolved = new List<LoadedMod>();
        foreach (var dependency in manifest.DependsOn)
        {
            if (loaded.FirstOrDefault(m => m.Manifest.Id == dependency.Id) is { } found) resolved.Add(found);
            else Console.WriteLine($"mod       {manifest.Id}: optional dependency '{dependency.Id}' did not load; going on without it");
        }
        return resolved;
    }

    /// <summary>
    /// What the dependencies export, merged; two of them exporting different assemblies under one simple name refuse the mod.
    /// </summary>
    static Dictionary<string, Assembly> Imports(IReadOnlyList<LoadedMod> dependencies)
    {
        var imports = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencies)
            foreach (var (simple, assembly) in dependency.Exports)
            {
                if (imports.TryGetValue(simple, out var other) && !ReferenceEquals(other, assembly))
                    throw new ModLoadException($"two of its dependencies export different {simple}.dll; it cannot use both");
                imports[simple] = assembly;
            }
        return imports;
    }

    /// <summary>
    /// What a mod exports: its dependencies' exports and its own assemblies, by simple name.
    /// </summary>
    static Dictionary<string, Assembly> Exported(IReadOnlyList<Assembly> own, IReadOnlyDictionary<string, Assembly> imports)
    {
        var exports = new Dictionary<string, Assembly>(imports, StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in own) exports[assembly.GetName().Name!] = assembly;
        return exports;
    }

    /// <summary>
    /// Loads the manifest's assemblies into the mod's own load context; refuses a missing file, one of the game's assemblies or one a
    /// dependency exports.
    /// </summary>
    static List<Assembly> LoadAssemblies(ModInfo info, CodeModManifest manifest, IReadOnlyDictionary<string, Assembly> imports)
    {
        var context = new ModLoadContext(info.Folder, manifest.Id, imports);
        var loaded = new List<Assembly>();
        foreach (var file in manifest.Assemblies)
        {
            var path = Path.Combine(info.Folder, file);
            if (!File.Exists(path)) throw new ModLoadException($"{file} is missing from the mod folder");
            if (ModLoadContext.IsShipped(Path.GetFileNameWithoutExtension(file)))
                throw new ModLoadException($"{file} is one of the game's own assemblies; list only the mod's");
            if (imports.ContainsKey(Path.GetFileNameWithoutExtension(file)))
                throw new ModLoadException($"{file} comes from one of its dependencies; list only the mod's own assemblies");
            loaded.Add(context.LoadFromAssemblyPath(path));
        }
        return loaded;
    }

    /// <summary>
    /// The mod's entry instance: its manifest's entry type, which must implement <see cref="IMod"/> and have a public parameterless constructor.
    /// </summary>
    static IMod CreateEntry(LoadedMod mod)
    {
        var type = mod.Assemblies.Select(a => a.GetType(mod.Manifest.Entry, throwOnError: false)).FirstOrDefault(t => t is not null)
            ?? throw EntryMissing(mod);
        if (!typeof(IMod).IsAssignableFrom(type))
            throw new ModLoadException($"entry type {type.FullName} does not implement {typeof(IMod).FullName}");
        if (type.GetConstructor(Type.EmptyTypes) is null)
            throw new ModLoadException($"entry type {type.FullName} has no public parameterless constructor");
        return (IMod)Activator.CreateInstance(type)!;
    }

    /// <summary>
    /// Why the mod's entry type was not found: an assembly whose types cannot load as built for another version of the game (an entry that
    /// implements a type the game no longer has or no longer shows is not found by name; <see cref="CodeModFaults.BuiltForAnother"/>), else that it
    /// is not in the assemblies.
    /// </summary>
    static Exception EntryMissing(LoadedMod mod)
    {
        foreach (var assembly in mod.Assemblies)
        {
            try
            {
                assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e) when (CodeModFaults.BuiltForAnother(e))
            {
                return e;
            }
        }

        return new ModLoadException($"entry type {mod.Manifest.Entry} is not in its assemblies");
    }

    /// <summary>
    /// Runs <see cref="IMod.Initialize"/>; commits the registrations when it returns, removes the mod's patches when it throws.
    /// </summary>
    void Initialize(LoadedMod mod, ModHostOptions options)
    {
        var context = new ModContext(mod, _registry, options.Data, options.Population, options.World, options.SavedSettings?.Invoke(mod.Manifest.Id));
        try
        {
            mod.Instance!.Initialize(context);
        }
        catch
        {
            if (context.HasHarmony) context.Harmony.UnpatchAll(context.Id);
            _registry.Effects.Drop(context.Id);
            _registry.Glows.Drop(context.Id);
            _registry.Trails.Drop(context.Id);
            TellDependenciesItFailed(mod);
            throw;
        }
        finally
        {
            context.Open = false;
        }
        _registry.Commit(context.Id, context.Pending);
    }

    /// <summary>
    /// A dependent failed in <see cref="IMod.Initialize"/>: every mod it depends on, directly or through others, that implements
    /// <see cref="IModDependents"/> drops what it holds for it (docs/modding-dependencies.md "Failed initialization").
    /// </summary>
    internal static void TellDependenciesItFailed(LoadedMod mod)
    {
        foreach (var dependency in DependencyClosure(mod))
        {
            if (dependency.Instance is not IModDependents framework) continue;
            try
            {
                framework.DependentFailed(mod.Manifest.Id);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"mod       {dependency.Manifest.Id}: cleanup after {mod.Manifest.Id} failed: {e}");
                CodeModFaults.Tell(dependency.Manifest.Id, e);
            }
        }
    }

    /// <summary>
    /// The mods <paramref name="mod"/> depends on, transitively, each once, nearest first.
    /// </summary>
    static List<LoadedMod> DependencyClosure(LoadedMod mod)
    {
        var all = new List<LoadedMod>();
        var queue = new Queue<LoadedMod>(mod.Dependencies);
        while (queue.TryDequeue(out var next))
        {
            if (all.Contains(next)) continue;
            all.Add(next);
            foreach (var deeper in next.Dependencies) queue.Enqueue(deeper);
        }
        return all;
    }

    /// <summary>
    /// The code mods of this session for a save (docs/formats/save.md "Mod data"): the loaded ones in load order, with ids and versions.
    /// </summary>
    public CodeModEnvironment Environment(string gameVersion)
    {
        var entries = _mods.Select(m => new CodeModEnvironmentEntry(m.Manifest.Id, m.Manifest.Version?.ToString(), m.Info.Name));
        return new CodeModEnvironment(gameVersion, [.. entries]);
    }

    /// <summary>
    /// The save data of the world's mod systems (<see cref="IModSaveData"/>) into <paramref name="files"/>, one file per mod by id; between ticks. A
    /// mod without such systems, or whose write fails, keeps the file it had (a save made without the mod keeps the mod's data too). A mod that
    /// claimed world states gets the same entries, then its states' entry (<see cref="ModWorldStateSave"/>): where it would keep its file, the file's
    /// entries of systems it still registers are kept instead, so a removed or renamed system's data is not carried on.
    /// </summary>
    public void CaptureSaveData(World world, IDictionary<string, byte[]> files)
    {
        var systems = SaveableSystems(world).ToLookup(s => s.Tag.ModId);
        foreach (var mod in SavingMods(systems))
        {
            var entries = SystemEntries(mod, systems[mod]);
            if (OwnedWorldStates(mod) is { } owned)
            {
                entries = entries is { Count: > 0 } ? entries : EarlierEntries(files, mod);
                entries.Add((ModWorldStateSave.EntryName, ModWorldStateSave.Write(ModWorldStateSave.ValuesOf(world, owned))));
            }
            if (entries is not null) files[mod] = ModSaveFile.Write(entries);
        }
    }

    /// <summary>
    /// The entries of <paramref name="mod"/>'s saveable systems in this world, in system order; null, with a log line, when one's write throws.
    /// </summary>
    static List<(string Name, byte[] Data)>? SystemEntries(string mod, IEnumerable<(IModSaveData System, ModSystemTag Tag)> systems)
    {
        try
        {
            return systems.Select(s => (s.Tag.Name, Written(s.System))).ToList();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod       {mod}: save data not written: {e}");
            CodeModFaults.Tell(mod, e);
            return null;
        }
    }

    /// <summary>
    /// Hands each mod system of a world just loaded its saved data, and each mod's world states their saved values; between ticks, before the first.
    /// A system without data stays as made, a state without a value answers from its record. A fault in a mod's systems' data does not stop its
    /// states' values, nor the other way round.
    /// </summary>
    public void RestoreSaveData(World world, IReadOnlyDictionary<string, byte[]> files)
    {
        var systems = SaveableSystems(world).ToLookup(s => s.Tag.ModId);
        foreach (var mod in SavingMods(systems))
        {
            if (!files.TryGetValue(mod, out var file)) continue;
            Dictionary<string, byte[]> entries;
            try
            {
                entries = ModSaveFile.Read(file);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"mod       {mod}: save data not read: {e}");
                continue;
            }
            RestoreSystems(mod, systems[mod], entries);
            if (OwnedWorldStates(mod) is { } owned) RestoreWorldStates(mod, world, owned, entries);
        }
    }

    /// <summary>
    /// Hands <paramref name="mod"/>'s systems their entries; a throwing <see cref="IModSaveData.Read"/> stops that mod's systems' restore, logged.
    /// </summary>
    static void RestoreSystems(string mod, IEnumerable<(IModSaveData System, ModSystemTag Tag)> systems, Dictionary<string, byte[]> entries)
    {
        try
        {
            foreach (var (system, tag) in systems)
                if (entries.TryGetValue(tag.Name, out var data))
                    system.Read(new MemoryStream(data, writable: false));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod       {mod}: save data not read: {e}");
            CodeModFaults.Tell(mod, e);
        }
    }

    /// <summary>
    /// Sets <paramref name="mod"/>'s world state values from its states' entry, if the file has one; a bad entry is logged and sets nothing more.
    /// </summary>
    static void RestoreWorldStates(string mod, World world, IReadOnlyDictionary<string, Simulation.Factions.ModWorldState> owned,
        Dictionary<string, byte[]> entries)
    {
        if (!entries.TryGetValue(ModWorldStateSave.EntryName, out var states)) return;
        try
        {
            ModWorldStateSave.Read(states, world, owned);
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine($"mod       {mod}: world states not read: {e.Message}");
        }
    }

    /// <summary>
    /// The entries of <paramref name="mod"/>'s file in <paramref name="files"/> (the loaded save's) for the systems the mod still registers, in the
    /// file's order: what a mod whose systems are all out of this world (or whose write failed) keeps. Empty for no file or an unreadable one
    /// (logged).
    /// </summary>
    List<(string Name, byte[] Data)> EarlierEntries(IDictionary<string, byte[]> files, string mod)
    {
        if (!files.TryGetValue(mod, out var file)) return [];

        Dictionary<string, byte[]> earlier;
        try
        {
            earlier = ModSaveFile.Read(file);
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine($"mod       {mod}: earlier save data not kept: {e.Message}");
            return [];
        }
        var registered = _registry.Simulation.Systems
            .Where(f => f.Owner == mod)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.Ordinal);
        return [.. earlier.Where(e => registered.Contains(e.Key)).Select(e => (e.Key, e.Value))];
    }

    /// <summary>
    /// The mods with save data: those with saveable systems, in system order, then those that claimed world states, in load order.
    /// </summary>
    IEnumerable<string> SavingMods(ILookup<string, (IModSaveData System, ModSystemTag Tag)> systems) =>
        systems
            .Select(g => g.Key)
            .Concat(_mods.Select(m => m.Manifest.Id).Where(id => OwnedWorldStates(id) is not null))
            .Distinct();

    /// <summary>
    /// The WORLD_EVENT_STATEs <paramref name="mod"/> claimed, by record id; null for none.
    /// </summary>
    IReadOnlyDictionary<string, Simulation.Factions.ModWorldState>? OwnedWorldStates(string mod)
    {
        if (_registry.WorldStates.Count == 0) return null;

        var owned = _registry.WorldStates
            .Where(s => s.Owner == mod)
            .ToDictionary(s => s.Record, StringComparer.Ordinal);
        return owned.Count == 0 ? null : owned;
    }

    /// <summary>
    /// The world's systems that keep mod save data, with the mod and name they were made under.
    /// </summary>
    IEnumerable<(IModSaveData System, ModSystemTag Tag)> SaveableSystems(World world)
    {
        foreach (var system in world.Systems)
            if (system is IModSaveData saveable && _registry.TagOf(system) is { } tag) yield return (saveable, tag);
    }

    /// <summary>
    /// A system's save data, written to a byte array.
    /// </summary>
    static byte[] Written(IModSaveData system)
    {
        using var stream = new MemoryStream();
        system.Write(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// The mod and the registered name of a world's system; null for a standard system.
    /// </summary>
    public (string ModId, string Name)? OwnerOf(ITickSystem system) => _registry.TagOf(system) is { } tag ? (tag.ModId, tag.Name) : null;

    /// <summary>
    /// Registers the mods' interface extensions with a new interface; one that throws is logged and skipped.
    /// </summary>
    public List<IDisposable> RegisterUi(Func<IUiExtension, IDisposable> register) => RegisterUi((extension, _) => register(extension));

    /// <summary>
    /// Registers the mods' interface extensions with a new interface, each with its mod's folder (null when unknown); one that throws is logged and
    /// skipped.
    /// </summary>
    public List<IDisposable> RegisterUi(Func<IUiExtension, string?, IDisposable> register)
    {
        var registrations = new List<IDisposable>();
        foreach (var extension in _registry.Ui)
        {
            try
            {
                registrations.Add(register(extension, _registry.UiFolders.GetValueOrDefault(extension)));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"mod       interface extension {extension.Id} not registered: {e}");
                if (_registry.UiOwners.GetValueOrDefault(extension) is { } owner) CodeModFaults.Tell(owner, e);
            }
        }
        return registrations;
    }

    /// <summary>
    /// The Harmony version every mod patches with, for the log.
    /// </summary>
    public static string HarmonyVersion => typeof(Harmony).Assembly.GetName().Version?.ToString() ?? "?";
}

/// <summary>
/// A mod refused for a reason the player can act on (no stack trace in the log).
/// </summary>
sealed class ModLoadException(string message) : Exception(message);