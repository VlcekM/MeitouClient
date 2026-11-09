using System.Reflection;
using HarmonyLib;
using Meitou.Content;
using Meitou.Data;
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
    /// Made only by <see cref="Load"/>.
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
            }
        }
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
            var onDisk = ModList.Discover(install).Where(m => m.HasCode).ToDictionary(m => m.Name, StringComparer.Ordinal);
            List<ModInfo> enabled = [.. ModList.ReadEnabled(install).Select(n => onDisk.GetValueOrDefault(n)).OfType<ModInfo>()];
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
        var entries = options.Enabled.Select(m => CodeModDependencies.ReadEntry(m, enabled: true))
            .Concat((options.Disabled ?? []).Select(m => CodeModDependencies.ReadEntry(m, enabled: false)))
            .ToList();
        for (int i = 0; i < options.Enabled.Count; i++) host.TryLoad(options.Enabled[i], options, entries, i);
        return host;
    }

    /// <summary>
    /// Loads one mod (manifest, assemblies, entry instance, <see cref="IMod.Initialize"/>); a failure refuses it with a log line and a notice.
    /// </summary>
    void TryLoad(ModInfo info, ModHostOptions options, IReadOnlyList<CodeModEntry> entries, int index)
    {
        LoadedMod? mod = null;
        try
        {
            mod = Prepare(info, entries, index, options.GameVersion);
            Initialize(mod, options);
            _mods.Add(mod);
            _pass.Loaded(info.Name, mod.Manifest);
            string assemblies = string.Join(", ", mod.Assemblies.Select(a => a.GetName().Name));
            Console.WriteLine($"mod       {mod.Manifest.Id}: loaded from {info.Name} ({assemblies})");
        }
        catch (ModLoadException e)
        {
            Refuse(info.Name, e.Message);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod       {info.Name}: {e}");
            Refuse(info.Name, $"failed to start: {e.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Leaves a mod out: a log line, a notice, and its reason for the dependency checks.
    /// </summary>
    void Refuse(string mod, string reason)
    {
        Console.Error.WriteLine($"mod       {mod}: not loaded: {reason}");
        _notices.Add($"Mod {mod} not loaded: {reason}");
        _pass.Refused(mod, reason);
    }

    /// <summary>
    /// The manifest checks, the assemblies and the entry instance.
    /// </summary>
    LoadedMod Prepare(ModInfo info, IReadOnlyList<CodeModEntry> entries, int index, CodeModVersion? gameVersion)
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
        if (CodeModChecks.Problems(manifest, index, entries, _pass, gameVersion) is [var problem, ..])
            throw new ModLoadException(problem);
        var mod = new LoadedMod(info, manifest);
        mod.Dependencies = LoadedDependencies(manifest);
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
    List<LoadedMod> LoadedDependencies(CodeModManifest manifest)
    {
        var resolved = new List<LoadedMod>();
        foreach (var dependency in manifest.DependsOn)
        {
            if (_mods.FirstOrDefault(m => m.Manifest.Id == dependency.Id) is { } loaded) resolved.Add(loaded);
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
            ?? throw new ModLoadException($"entry type {mod.Manifest.Entry} is not in its assemblies");
        if (!typeof(IMod).IsAssignableFrom(type))
            throw new ModLoadException($"entry type {type.FullName} does not implement {typeof(IMod).FullName}");
        if (type.GetConstructor(Type.EmptyTypes) is null)
            throw new ModLoadException($"entry type {type.FullName} has no public parameterless constructor");
        return (IMod)Activator.CreateInstance(type)!;
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
    /// mod without such systems, or whose write fails, keeps the file it had (a save made without the mod keeps the mod's data too).
    /// </summary>
    public void CaptureSaveData(World world, IDictionary<string, byte[]> files)
    {
        foreach (var group in SaveableSystems(world).GroupBy(s => s.Tag.ModId))
        {
            try
            {
                files[group.Key] = ModSaveFile.Write([.. group.Select(s => (s.Tag.Name, Written(s.System)))]);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"mod       {group.Key}: save data not written: {e}");
            }
        }
    }

    /// <summary>
    /// Hands each mod system of a world just loaded its saved data; between ticks, before the first. A system without data stays as made.
    /// </summary>
    public void RestoreSaveData(World world, IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var group in SaveableSystems(world).GroupBy(s => s.Tag.ModId))
        {
            if (!files.TryGetValue(group.Key, out var file)) continue;
            try
            {
                var entries = ModSaveFile.Read(file);
                foreach (var (system, tag) in group)
                    if (entries.TryGetValue(tag.Name, out var data))
                        system.Read(new MemoryStream(data, writable: false));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"mod       {group.Key}: save data not read: {e}");
            }
        }
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
    public List<IDisposable> RegisterUi(Func<IUiExtension, IDisposable> register)
    {
        var registrations = new List<IDisposable>();
        foreach (var extension in _registry.Ui)
        {
            try
            {
                registrations.Add(register(extension));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"mod       interface extension {extension.Id} not registered: {e}");
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