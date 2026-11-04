using System.Reflection;
using HarmonyLib;
using Meitou.Content;
using Meitou.Data;
using Meitou.Simulation;
using Meitou.Ui;

namespace Meitou.Modding;

/// <summary>A code mod that loaded: its mod, manifest, assemblies and entry instance.</summary>
public sealed class LoadedMod
{
    internal LoadedMod(ModInfo info, CodeModManifest manifest)
    {
        Info = info;
        Manifest = manifest;
    }

    public ModInfo Info { get; }
    public CodeModManifest Manifest { get; }
    public IReadOnlyList<Assembly> Assemblies { get; internal set; } = [];
    public IMod? Instance { get; internal set; }
}

/// <summary>Where a <see cref="ModHost"/> loads from: the enabled mods in order, the mods the player allowed to run code, the merged data and the running world.</summary>
public sealed record ModHostOptions(
    IReadOnlyList<ModInfo> Enabled, IReadOnlySet<string> Allowed, GameDatabase Data, PopulationData Population, Func<World?> World);

/// <summary>
/// The code mods of a session (docs/modding.md): loads the enabled, allowed ones at startup, runs their <see cref="IMod.Initialize"/> once, and holds what they
/// registered for the host: the simulation extensions every world is built with, the interface extensions, the debug rows, and the save data of their systems.
/// A mod that fails anywhere is left out with a log line and a notice; the game goes on.
/// </summary>
public sealed class ModHost
{
    readonly ModRegistry registry = new();
    readonly List<LoadedMod> mods = [];
    readonly List<string> notices = [];

    ModHost() { }

    public IReadOnlyList<LoadedMod> Mods => mods;

    /// <summary>What went wrong or was left out, one line per mod, for the player's notices.</summary>
    public IReadOnlyList<string> Notices => notices;

    /// <summary>What every world is built with; null without registrations.</summary>
    public SimulationExtensions? Simulation => registry.Simulation.IsEmpty ? null : registry.Simulation;

    public IReadOnlyList<IUiExtension> UiExtensions => registry.Ui;
    public IReadOnlyList<ModDebugCommand> DebugCommands => registry.DebugCommands;

    /// <summary>The record types mods claimed (number, mod's name for it).</summary>
    public IReadOnlyDictionary<int, string> RecordTypes => registry.RecordTypes;

    /// <summary>The enabled mods of <c>mods.cfg</c>, in order, that carry code (<see cref="ModInfo.HasCode"/>).</summary>
    public static List<ModInfo> EnabledCodeMods(GameInstall install)
    {
        var onDisk = ModList.Discover(install).ToDictionary(m => m.Name, StringComparer.Ordinal);
        return [.. ModList.ReadEnabled(install).Select(n => onDisk.GetValueOrDefault(n)).OfType<ModInfo>().Where(m => m.HasCode)];
    }

    /// <summary>Loads and initializes the code mods, in order. Never throws for a mod's fault.</summary>
    public static ModHost Load(ModHostOptions options)
    {
        var host = new ModHost();
        foreach (var info in options.Enabled) host.TryLoad(info, options);
        return host;
    }

    void TryLoad(ModInfo info, ModHostOptions options)
    {
        if (!options.Allowed.Contains(info.Name))
        {
            Refuse(info.Name, "runs code of its own and has not been allowed to (allow it in the Meitou launcher)");
            return;
        }
        LoadedMod? mod = null;
        try
        {
            mod = Prepare(info);
            Initialize(mod, options);
            mods.Add(mod);
            Console.WriteLine($"mod       {mod.Manifest.Id}: loaded from {info.Name} ({string.Join(", ", mod.Assemblies.Select(a => a.GetName().Name))})");
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

    void Refuse(string mod, string reason)
    {
        Console.Error.WriteLine($"mod       {mod}: not loaded: {reason}");
        notices.Add($"Mod {mod} not loaded: {reason}");
    }

    /// <summary>The manifest checks, the assemblies and the entry instance.</summary>
    LoadedMod Prepare(ModInfo info)
    {
        CodeModManifest manifest;
        try { manifest = CodeModManifest.Read(info.CodeManifestPath); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { throw new ModLoadException($"bad {ModList.CodeManifestFile}: {e.Message}"); }
        if (manifest.ApiVersion != ModApi.Version)
            throw new ModLoadException($"built for code-mod API {manifest.ApiVersion}; this Meitou has API {ModApi.Version}");
        if (mods.FirstOrDefault(m => m.Manifest.Id == manifest.Id) is { } twin)
            throw new ModLoadException($"id '{manifest.Id}' is taken by mod {twin.Info.Name}");
        var mod = new LoadedMod(info, manifest);
        mod.Assemblies = LoadAssemblies(info, manifest);
        mod.Instance = CreateEntry(mod);
        return mod;
    }

    static List<Assembly> LoadAssemblies(ModInfo info, CodeModManifest manifest)
    {
        var context = new ModLoadContext(info.Folder, manifest.Id);
        var loaded = new List<Assembly>();
        foreach (var file in manifest.Assemblies)
        {
            var path = Path.Combine(info.Folder, file);
            if (!File.Exists(path)) throw new ModLoadException($"{file} is missing from the mod folder");
            if (ModLoadContext.IsShipped(Path.GetFileNameWithoutExtension(file)))
                throw new ModLoadException($"{file} is one of the game's own assemblies; list only the mod's");
            loaded.Add(context.LoadFromAssemblyPath(path));
        }
        return loaded;
    }

    static IMod CreateEntry(LoadedMod mod)
    {
        var type = mod.Assemblies.Select(a => a.GetType(mod.Manifest.Entry, throwOnError: false)).FirstOrDefault(t => t is not null)
            ?? throw new ModLoadException($"entry type {mod.Manifest.Entry} is not in its assemblies");
        if (!typeof(IMod).IsAssignableFrom(type)) throw new ModLoadException($"entry type {type.FullName} does not implement {typeof(IMod).FullName}");
        if (type.GetConstructor(Type.EmptyTypes) is null) throw new ModLoadException($"entry type {type.FullName} has no public parameterless constructor");
        return (IMod)Activator.CreateInstance(type)!;
    }

    /// <summary>Runs <see cref="IMod.Initialize"/>; commits the registrations when it returns, removes the mod's patches when it throws.</summary>
    void Initialize(LoadedMod mod, ModHostOptions options)
    {
        var context = new ModContext(mod, registry, options.Data, options.Population, options.World);
        try { mod.Instance!.Initialize(context); }
        catch
        {
            if (context.HasHarmony) context.Harmony.UnpatchAll(context.Id);
            throw;
        }
        finally { context.Open = false; }
        registry.Commit(context.Id, context.Pending);
    }

    /// <summary>
    /// The save data of the world's mod systems (<see cref="IModSaveData"/>) into <paramref name="files"/>, one file per mod by id; between ticks. A mod without such
    /// systems, or whose write fails, keeps the file it had (a save made without the mod keeps the mod's data too).
    /// </summary>
    public void CaptureSaveData(World world, IDictionary<string, byte[]> files)
    {
        foreach (var group in SaveableSystems(world).GroupBy(s => s.Tag.ModId))
        {
            try { files[group.Key] = ModSaveFile.Write([.. group.Select(s => (s.Tag.Name, Written(s.System)))]); }
            catch (Exception e) { Console.Error.WriteLine($"mod       {group.Key}: save data not written: {e}"); }
        }
    }

    /// <summary>Hands each mod system of a world just loaded its saved data; between ticks, before the first. A system without data stays as made.</summary>
    public void RestoreSaveData(World world, IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var group in SaveableSystems(world).GroupBy(s => s.Tag.ModId))
        {
            if (!files.TryGetValue(group.Key, out var file)) continue;
            try
            {
                var entries = ModSaveFile.Read(file);
                foreach (var (system, tag) in group)
                    if (entries.TryGetValue(tag.Name, out var data)) system.Read(new MemoryStream(data, writable: false));
            }
            catch (Exception e) { Console.Error.WriteLine($"mod       {group.Key}: save data not read: {e}"); }
        }
    }

    IEnumerable<(IModSaveData System, ModSystemTag Tag)> SaveableSystems(World world)
    {
        foreach (var system in world.Systems)
            if (system is IModSaveData saveable && registry.TagOf(system) is { } tag) yield return (saveable, tag);
    }

    static byte[] Written(IModSaveData system)
    {
        using var stream = new MemoryStream();
        system.Write(stream);
        return stream.ToArray();
    }

    /// <summary>Registers the mods' interface extensions with a new interface; one that throws is logged and skipped.</summary>
    public List<IDisposable> RegisterUi(Func<IUiExtension, IDisposable> register)
    {
        var registrations = new List<IDisposable>();
        foreach (var extension in registry.Ui)
        {
            try { registrations.Add(register(extension)); }
            catch (Exception e) { Console.Error.WriteLine($"mod       interface extension {extension.Id} not registered: {e}"); }
        }
        return registrations;
    }

    /// <summary>The Harmony version every mod patches with, for the log.</summary>
    public static string HarmonyVersion => typeof(Harmony).Assembly.GetName().Version?.ToString() ?? "?";
}

/// <summary>A mod refused for a reason the player can act on (no stack trace in the log).</summary>
sealed class ModLoadException(string message) : Exception(message);
