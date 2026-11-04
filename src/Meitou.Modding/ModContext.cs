using HarmonyLib;
using Meitou.Content;
using Meitou.Data;
using Meitou.Simulation;
using Meitou.Ui;

namespace Meitou.Modding;

/// <summary>
/// What a mod gets in <see cref="IMod.Initialize"/> (docs/modding.md "Lifecycle"): the merged data, its folder, a log, its random purpose, its Harmony instance, the
/// command queue, and the registration surface. Registering is only allowed during <see cref="IMod.Initialize"/>; the registrations take effect when it returns.
/// </summary>
public sealed class ModContext
{
    readonly ModRegistry registry;
    readonly Func<World?> world;
    Harmony? harmony;

    internal ModContext(LoadedMod mod, ModRegistry registry, GameDatabase data, PopulationData population, Func<World?> world)
    {
        Mod = mod;
        this.registry = registry;
        this.world = world;
        Data = data;
        Population = population;
        RngPurpose = (RngPurpose)(uint)(Rng.StableHash(mod.Manifest.Id) | 0x8000_0000UL);
    }

    internal LoadedMod Mod { get; }
    internal ModRegistrations Pending { get; } = new();
    internal bool Open { get; set; } = true;

    /// <summary>The manifest id: keys the mod's save data, its Harmony instance and its log lines.</summary>
    public string Id => Mod.Manifest.Id;

    /// <summary>The mod's name (its <c>.mod</c> file's stem), as <c>mods.cfg</c> lists it.</summary>
    public string Name => Mod.Info.Name;

    public string Folder => Mod.Info.Folder;
    public CodeModManifest Manifest => Mod.Manifest;

    /// <summary>The game data with every enabled mod merged in.</summary>
    public GameDatabase Data { get; }

    /// <summary>The simulation's view of the data (factions, relations, bodies, items...), shared by every world.</summary>
    public PopulationData Population { get; }

    /// <summary>The mod's own purpose for <see cref="Rng.Hash"/>: derived from the manifest id, never one of the vanilla purposes, so its rolls never share a stream.</summary>
    public RngPurpose RngPurpose { get; }

    /// <summary>The mod's Harmony instance (id: the manifest id) on the game's one copy of Harmony.</summary>
    public Harmony Harmony => harmony ??= new Harmony(Id);

    internal bool HasHarmony => harmony is not null;

    /// <summary>The running world's tick, for a command's <see cref="SimCommand.Tick"/>; 0 before the first world.</summary>
    public long Tick => world()?.Tick ?? 0;

    /// <summary>Queues a command for the running world (any thread); its <see cref="SimCommand.Tick"/> says when it applies. Dropped (and logged) before the first world.</summary>
    public void Enqueue(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (world() is { } w) w.Commands.Enqueue(command);
        else Log($"no world yet: dropped {command.GetType().Name}");
    }

    /// <summary>A line in the game's log (log.txt), prefixed with the mod id.</summary>
    public void Log(string message) => Console.WriteLine($"mod       {Id}: {message}");

    /// <summary>A line in the game's error output, prefixed with the mod id.</summary>
    public void LogError(string message) => Console.Error.WriteLine($"mod       {Id}: {message}");

    /// <summary>
    /// A system made for every world (each new game and load builds its systems afresh) and placed at <paramref name="anchor"/> in the tick order. It follows the
    /// threading rules of docs/simulation.md "Threading"; with <see cref="IStateHashed"/> its state goes into the world's state hash, with <see cref="IModSaveData"/> into
    /// saves. <paramref name="name"/> is unique within the mod and keys the system's save data.
    /// </summary>
    public void AddSystem(string name, Func<SystemBuildContext, ITickSystem> factory, SystemAnchor anchor)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A system needs a name.", nameof(name));
        if (Pending.Systems.Any(s => s.Name == name)) throw new ArgumentException($"The mod already has a system named '{name}'.", nameof(name));
        Pending.Systems.Add(new SystemFactory(Id, name, anchor, factory));
    }

    /// <summary>A dialogue condition code (<see cref="SimulationExtensions.FirstModCode"/> and up) that FCS data names in a DIALOG_ACTION's <c>condition name</c>.</summary>
    public void AddDialogueCondition(int code, DialogueConditionHandler handler)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(handler);
        registry.CheckFree(ModCodeKind.DialogueCondition, code, Pending, Id);
        Pending.Conditions[code] = handler;
    }

    /// <summary>A dialogue effect code (<see cref="SimulationExtensions.FirstModCode"/> and up) that FCS data names in a DIALOG_ACTION's <c>action name</c>.</summary>
    public void AddDialogueEffect(int code, DialogueEffectHandler handler)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(handler);
        registry.CheckFree(ModCodeKind.DialogueEffect, code, Pending, Id);
        Pending.Effects[code] = handler;
    }

    /// <summary>
    /// Claims a record type number (<see cref="SimulationExtensions.FirstModCode"/> and up, not a vanilla <c>itemType</c>) for the mod's own FCS records, so two mods
    /// never read each other's. The records load and merge like any other; read them with <see cref="Records"/>.
    /// </summary>
    public void AddRecordType(int type, string name)
    {
        CheckOpen();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A record type needs a name.", nameof(name));
        if (Enum.IsDefined((Meitou.Data.Fcs.FcsRecordType)type)) throw new ArgumentException($"Record type {type} is a vanilla type.", nameof(type));
        registry.CheckFree(ModCodeKind.RecordType, type, Pending, Id);
        Pending.RecordTypes[type] = name;
    }

    /// <summary>The merged records of a type, in string id order.</summary>
    public IEnumerable<GameRecord> Records(int type) =>
        Data.Records.Values.Where(r => (int)r.Type == type).OrderBy(r => r.StringId, StringComparer.Ordinal);

    /// <summary>Interface contributions (styles, controls, actions, slot contributions; docs/modding.md "Registration surface"), registered when the interface is made.</summary>
    public void AddUi(IUiExtension extension)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(extension);
        Pending.Ui.Add(extension);
    }

    /// <summary>
    /// A row of the debug menu (F10), after the vanilla rows. <paramref name="run"/> runs on the host's thread while a tick may be in flight: it must not touch the
    /// world, only read the context and queue commands; its return is the status line.
    /// </summary>
    public void AddDebugCommand(string label, Func<DebugCommandContext, string> run, bool hasField = false)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A debug command needs a label.", nameof(label));
        Pending.DebugCommands.Add(new ModDebugCommand(Id, label, hasField, run));
    }

    void CheckOpen()
    {
        if (!Open) throw new InvalidOperationException($"{Id}: registering is only allowed during IMod.Initialize.");
    }
}

/// <summary>What a mod's debug command gets: the row's typed text, the selected characters, the camera target, the tick, and the command queue.</summary>
public sealed record DebugCommandContext(string Field, IReadOnlyList<CharacterId> Selected, System.Numerics.Vector3 Target, long Tick, Action<SimCommand> Enqueue);

/// <summary>A debug menu row a mod added.</summary>
public sealed record ModDebugCommand(string Owner, string Label, bool HasField, Func<DebugCommandContext, string> Run);
