using HarmonyLib;
using Meitou.Content;
using Meitou.Data;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;

namespace Meitou.Modding;

/// <summary>
/// What a mod gets in <see cref="IMod.Initialize"/> (docs/modding.md "Lifecycle"): the merged data, its folder, a log, its random purpose, its
/// Harmony instance, the command queue, and the registration surface. Registering is only allowed during <see cref="IMod.Initialize"/>; the
/// registrations take effect when it returns.
/// </summary>
public sealed class ModContext
{
    /// <summary>
    /// The registry the mod's registrations are committed to when its <see cref="IMod.Initialize"/> returns.
    /// </summary>
    readonly ModRegistry _registry;
    /// <summary>
    /// The current world, if any: the tick and the command queue.
    /// </summary>
    readonly Func<World?> _world;
    /// <summary>
    /// The mod's saved setting values (key to text), applied by <see cref="AddSettings"/>; null when none were saved.
    /// </summary>
    readonly IReadOnlyDictionary<string, string>? _savedSettings;
    /// <summary>
    /// The mod's Harmony instance, made on first use.
    /// </summary>
    Harmony? _harmony;

    /// <summary>
    /// Made by the host for one mod before its <see cref="IMod.Initialize"/>; its random purpose comes from the manifest id.
    /// </summary>
    internal ModContext(LoadedMod mod, ModRegistry registry, GameDatabase data, PopulationData population, Func<World?> world,
        IReadOnlyDictionary<string, string>? savedSettings = null)
    {
        Mod = mod;
        this._registry = registry;
        this._world = world;
        this._savedSettings = savedSettings;
        Data = data;
        Population = population;
        RngPurpose = (RngPurpose)(uint)(Rng.StableHash(mod.Manifest.Id) | 0x8000_0000UL);
    }

    /// <summary>
    /// The mod this context belongs to.
    /// </summary>
    internal LoadedMod Mod { get; }
    /// <summary>
    /// What the mod registers during its <see cref="IMod.Initialize"/>, committed when it returns.
    /// </summary>
    internal ModRegistrations Pending { get; } = new();
    /// <summary>
    /// Whether registering is allowed: true during <see cref="IMod.Initialize"/>, cleared when it returns.
    /// </summary>
    internal bool Open { get; set; } = true;

    /// <summary>
    /// The manifest id: keys the mod's save data, its Harmony instance and its log lines.
    /// </summary>
    public string Id => Mod.Manifest.Id;

    /// <summary>
    /// The mod's name (its <c>.mod</c> file's stem), as <c>mods.cfg</c> lists it.
    /// </summary>
    public string Name => Mod.Info.Name;

    /// <summary>
    /// The mod's folder (where its <c>.mod</c> file and code are).
    /// </summary>
    public string Folder => Mod.Info.Folder;
    /// <summary>
    /// The mod's code manifest.
    /// </summary>
    public CodeModManifest Manifest => Mod.Manifest;

    /// <summary>
    /// The game data with every enabled mod merged in.
    /// </summary>
    public GameDatabase Data { get; }

    /// <summary>
    /// The simulation's view of the data (factions, relations, bodies, items...), shared by every world.
    /// </summary>
    public PopulationData Population { get; }

    /// <summary>
    /// The mod's own purpose for <see cref="Rng.Hash"/>: derived from the manifest id, never one of the vanilla purposes, so its rolls never share a
    /// stream.
    /// </summary>
    public RngPurpose RngPurpose { get; }

    /// <summary>
    /// The mod's Harmony instance (id: the manifest id) on the game's one copy of Harmony.
    /// </summary>
    public Harmony Harmony => _harmony ??= new Harmony(Id);

    /// <summary>
    /// Whether the mod used its Harmony instance.
    /// </summary>
    internal bool HasHarmony => _harmony is not null;

    /// <summary>
    /// The entry instance of a declared dependency (docs/modding-dependencies.md "Using a dependency"), typed as the framework's own type or an
    /// interface it implements; null for an optional dependency that did not load. Dependencies initialize first, so what they registered is in
    /// place. Throws for an id the manifest does not declare, or when the entry is not a <typeparamref name="T"/>. For an optional one, test
    /// <see cref="HasDependency"/> first and keep code naming its types in a separate non-inlined method: the JIT resolves a method's types when it
    /// compiles it.
    /// </summary>
    public T? Dependency<T>(string id) where T : class
    {
        var declared = Manifest.DependsOn.FirstOrDefault(d => d.Id == id)
            ?? throw new InvalidOperationException($"{Id}: '{id}' is not in the manifest's dependencies; declare it to use it.");
        if (Mod.Dependencies.FirstOrDefault(m => m.Manifest.Id == declared.Id) is not { } loaded) return null;
        if (loaded.Instance is T entry) return entry;
        string actual = loaded.Instance?.GetType().FullName ?? "none";
        throw new InvalidCastException($"{Id}: dependency '{id}' has entry {actual}, which is not a {typeof(T).FullName}.");
    }

    /// <summary>
    /// Whether a declared dependency loaded: always true for a required one (the mod would have been refused), false for an absent optional one.
    /// </summary>
    public bool HasDependency(string id) => Mod.Dependencies.Any(m => m.Manifest.Id == id);

    /// <summary>
    /// The running world's tick, for a command's <see cref="SimCommand.Tick"/>; 0 before the first world.
    /// </summary>
    public long Tick => _world()?.Tick ?? 0;

    /// <summary>
    /// Queues a command for the running world (any thread); its <see cref="SimCommand.Tick"/> says when it applies. Dropped (and logged) before the
    /// first world.
    /// </summary>
    public void Enqueue(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_world() is { } w) w.Commands.Enqueue(command);
        else Log($"no world yet: dropped {command.GetType().Name}");
    }

    /// <summary>
    /// A line in the game's log (log.txt), prefixed with the mod id.
    /// </summary>
    public void Log(string message) => Console.WriteLine($"mod       {Id}: {message}");

    /// <summary>
    /// A line in the game's error output, prefixed with the mod id.
    /// </summary>
    public void LogError(string message) => Console.Error.WriteLine($"mod       {Id}: {message}");

    /// <summary>
    /// A system made for every world (each new game and load builds its systems afresh) and placed at <paramref name="anchor"/> in the tick order. It
    /// follows the threading rules of docs/simulation.md "Threading"; with <see cref="IStateHashed"/> its state goes into the world's state hash,
    /// with <see cref="IModSaveData"/> into saves. <paramref name="name"/> is unique within the mod and keys the system's save data.
    /// </summary>
    public void AddSystem(string name, Func<SystemBuildContext, ITickSystem> factory, SystemAnchor anchor)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A system needs a name.", nameof(name));
        if (Pending.Systems.Any(s => s.Name == name)) throw new ArgumentException($"The mod already has a system named '{name}'.", nameof(name));
        Pending.Systems.Add(new SystemFactory(Id, name, anchor, factory));
    }

    /// <summary>
    /// A dialogue condition code (<see cref="SimulationExtensions.FirstModCode"/> and up) that FCS data names in a DIALOG_ACTION's <c>condition
    /// name</c>.
    /// </summary>
    public void AddDialogueCondition(int code, DialogueConditionHandler handler)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(handler);
        _registry.CheckFree(ModCodeKind.DialogueCondition, code, Pending, Id);
        Pending.Conditions[code] = handler;
    }

    /// <summary>
    /// A dialogue effect code (<see cref="SimulationExtensions.FirstModCode"/> and up) that FCS data names in a DIALOG_ACTION's <c>action name</c>.
    /// </summary>
    public void AddDialogueEffect(int code, DialogueEffectHandler handler)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(handler);
        _registry.CheckFree(ModCodeKind.DialogueEffect, code, Pending, Id);
        Pending.Effects[code] = handler;
    }

    /// <summary>
    /// Claims a record type number (<see cref="SimulationExtensions.FirstModCode"/> and up, not a vanilla <c>itemType</c>) for the mod's own FCS
    /// records, so two mods never read each other's. The records load and merge like any other; read them with <see cref="Records"/>.
    /// </summary>
    public void AddRecordType(int type, string name)
    {
        CheckOpen();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A record type needs a name.", nameof(name));
        if (Enum.IsDefined((Data.Fcs.FcsRecordType)type)) throw new ArgumentException($"Record type {type} is a vanilla type.", nameof(type));
        _registry.CheckFree(ModCodeKind.RecordType, type, Pending, Id);
        Pending.RecordTypes[type] = name;
    }

    /// <summary>
    /// An equipment slot (docs/modding.md "Equipment slots"): the inventory section <see cref="ModSlot.Section"/> takes the ARMOUR
    /// and ITEM records whose FCS <c>slot</c> is <see cref="ModSlot.FcsSlot"/>, and the inventory window shows it at its place.
    /// A section or <c>slot</c> of the game or of another slot refuses it.
    /// </summary>
    public void AddEquipmentSlot(ModSlot slot)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(slot);
        if (ModSlots.Refusal(slot) is { } refusal) throw new ArgumentException(refusal, nameof(slot));
        if (Pending.Slots.Any(s => s.Section == slot.Section || s.FcsSlot == slot.FcsSlot))
            throw new ArgumentException($"The mod already has a slot with section '{slot.Section}' or FCS slot {slot.FcsSlot}.", nameof(slot));
        Pending.Slots.Add(slot);
    }

    /// <summary>
    /// The merged records of a type, in string id order.
    /// </summary>
    public IEnumerable<GameRecord> Records(int type) =>
        Data.Records.Values.Where(r => (int)r.Type == type).OrderBy(r => r.StringId, StringComparer.Ordinal);

    /// <summary>
    /// Interface contributions (styles, controls, actions, slot contributions; docs/modding.md "Registration surface"), registered when the interface
    /// is made.
    /// </summary>
    public void AddUi(IUiExtension extension)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(extension);
        Pending.Ui.Add(extension);
    }

    /// <summary>
    /// The resolved combat hits for display (docs/modding.md "Hit feed"): once a frame on the host's thread, the hits of the ticks since the last
    /// frame, oldest first; not called for a frame without any. A tick may be in flight, so <paramref name="view"/> must not touch the world, and the
    /// list is reused: copy what you keep. A throw is logged and the feed gets no more hits.
    /// </summary>
    public void AddCombatHitFeed(Action<IReadOnlyList<Simulation.Combat.CombatHit>> view)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(view);
        Pending.HitFeeds.Add(new ModHitFeed(Id, view));
    }

    /// <summary>
    /// A row of the debug menu (F10), after the vanilla rows. <paramref name="run"/> runs on the host's thread while a tick may be in flight: it must
    /// not touch the world, only read the context and queue commands; its return is the status line.
    /// </summary>
    public void AddDebugCommand(string label, Func<DebugCommandContext, string> run, bool hasField = false)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("A debug command needs a label.", nameof(label));
        Pending.DebugCommands.Add(new ModDebugCommand(Id, label, hasField, run));
    }

    /// <summary>
    /// The mod's settings (docs/modding.md "Settings"): the public properties of <paramref name="settings"/> marked <see cref="SettingAttribute"/>,
    /// one group under the mod on the Settings screen's Mods page. The player's saved values are set on the object before this returns; the values it
    /// has now are the defaults. Once per mod.
    /// </summary>
    public ModSettingsGroup AddSettings(object settings)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.GetType().IsValueType)
            throw new ArgumentException($"{settings.GetType().Name} is a struct: the page would change a copy. Use a class.", nameof(settings));
        if (Pending.Settings is not null) throw new InvalidOperationException($"{Id}: AddSettings is allowed once per mod.");
        var group = new ModSettingsGroup(Id, Name, settings);
        if (_savedSettings is not null)
            foreach (string key in group.Load(_savedSettings)) Log($"saved setting '{key}' is not a value of a setting; ignored");
        Pending.Settings = group;
        return group;
    }

    /// <summary>
    /// Throws once <see cref="IMod.Initialize"/> has returned: registering is only allowed during it.
    /// </summary>
    void CheckOpen()
    {
        if (!Open) throw new InvalidOperationException($"{Id}: registering is only allowed during IMod.Initialize.");
    }
}

/// <summary>
/// What a mod's debug command gets: the row's typed text, the selected characters, the camera target, the tick, and the command queue.
/// </summary>
public sealed record DebugCommandContext(
    string Field, IReadOnlyList<CharacterId> Selected, System.Numerics.Vector3 Target, long Tick, Action<SimCommand> Enqueue);

/// <summary>
/// A debug menu row a mod added.
/// </summary>
public sealed record ModDebugCommand(string Owner, string Label, bool HasField, Func<DebugCommandContext, string> Run);