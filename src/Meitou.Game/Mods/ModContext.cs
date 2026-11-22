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
    /// Plays a named sound event on a character, from any thread (docs/modding.md "Sounds"): <see cref="World.PostSound(string, CharacterId,
    /// string?, string?)"/> on the running world; nothing before the first world or without audio.
    /// </summary>
    public void PostSound(string eventName, CharacterId on, string? switchGroup = null, string? switchValue = null)
    {
        CheckSound(eventName, default);
        _world()?.PostSound(eventName, on, switchGroup, switchValue);
    }

    /// <summary>
    /// Plays a named sound event at a world point, from any thread: <see cref="World.PostSound(string, System.Numerics.Vector3, string?,
    /// string?)"/> on the running world; nothing before the first world or without audio.
    /// </summary>
    public void PostSound(string eventName, System.Numerics.Vector3 at, string? switchGroup = null, string? switchValue = null)
    {
        CheckSound(eventName, at);
        _world()?.PostSound(eventName, at, switchGroup, switchValue);
    }

    /// <summary>
    /// Plays a named sound event on the interface, from any thread: <see cref="World.PostInterfaceSound"/> on the running world; nothing before the
    /// first world or without audio.
    /// </summary>
    public void PostInterfaceSound(string eventName, string? switchGroup = null, string? switchValue = null)
    {
        CheckSound(eventName, default);
        _world()?.PostInterfaceSound(eventName, switchGroup, switchValue);
    }

    /// <summary>
    /// Throws, as the world's calls do, for an empty event name or a point that is not finite, also before the first world.
    /// </summary>
    static void CheckSound(string eventName, System.Numerics.Vector3 at)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        if (!float.IsFinite(at.X + at.Y + at.Z)) throw new ArgumentOutOfRangeException(nameof(at), at, "A sound's point must be finite.");
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
        if (name == ModWorldStateSave.EntryName) throw new ArgumentException($"'{name}' is the game's name in the mod's save data.", nameof(name));
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
    /// A rule asked whether a wearer may wear an item (docs/modding.md "Wear rules"): <paramref name="rule"/> gets the item's record id, the
    /// wearer's RACE id and whether it is an animal, after the item's own race lists pass, and false refuses the item everywhere a race limit
    /// applies. It may run on any thread, so it must be quick, read no world state and answer the same for the same arguments. A throw counts as
    /// yes and is logged once.
    /// </summary>
    public void AddWearRule(Data.Gameplay.WearRule rule)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(rule);
        Pending.WearRules.Add(rule);
    }

    /// <summary>
    /// Rows in the character editor (docs/modding.md "Character editor rows"): <paramref name="source"/> is asked, on the interface's thread, for the
    /// rows of each character the editor shows (its race, sex, look and whether it is a new game's creation) and answers sliders, lists and colours
    /// for any tab, shown after the game's own. Their values are kept on the look record under this mod's id, so the editor's CONFIRM, the save and
    /// EXPORT carry them; read them with <see cref="Data.Characters.ModLooks"/>. Return none for a character the mod has nothing for.
    /// </summary>
    public void AddEditorRows(Data.Characters.LookRowSource source)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(source);
        Pending.LookRows.Add(source);
    }

    /// <summary>
    /// This mod's values for a look (docs/modding.md "Character editor rows"): <paramref name="roll"/> writes them on every generated character's
    /// look, after the game's own rolls, and on the editor's RANDOMISE (<see cref="Data.Characters.LookRollContext.Tab"/>, the tab rolled, null for
    /// RANDOMISE ALL), with a random sequence of its own. It may run on any thread, so it must be quick, read no world state and write the same
    /// values for the same arguments. A throw keeps what it wrote and is logged once.
    /// </summary>
    public void AddLookRoll(Data.Characters.LookRoll roll)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(roll);
        Pending.LookRolls.Add(roll);
    }

    /// <summary>
    /// Additions to how people are drawn (docs/modding.md "Look extras"): <paramref name="source"/> is asked each time a person's look is built
    /// (its look record, race, sex and head) and answers pose weights set over the look's own and colour tints on disks of the skin, or null. It may
    /// run on any thread, so it must be quick, read no world state and answer the same for the same arguments. A throw, or a tint that is not
    /// valid, adds nothing to that look and is logged once.
    /// </summary>
    public void AddLookExtras(Data.Characters.LookExtrasSource source)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(source);
        Pending.LookExtras.Add(source);
    }

    /// <summary>
    /// A bar on the medical panel after Hunger (docs/modding.md "Medical meters") labelled <paramref name="label"/>: the mod's systems set each
    /// character's reading in the tick with <see cref="Simulation.Bodies.ModMeter.Set"/>, and the panel shows it for the selected or inspected
    /// character. A label another meter has, or a fifth meter of all mods together, refuses it.
    /// </summary>
    public Simulation.Bodies.ModMeter AddMedicalMeter(string label)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(label);
        if (Simulation.Bodies.ModMeters.Refusal(label, Pending.Meters.Select(m => m.Label)) is { } refusal)
            throw new ArgumentException(refusal, nameof(label));

        var meter = new Simulation.Bodies.ModMeter(label);
        Pending.Meters.Add(meter);
        return meter;
    }

    /// <summary>
    /// A skill (docs/modding.md "Skills") listed at the foot of its <see cref="Simulation.Bodies.ModSkill.Column"/> on the stats window, with its
    /// own description pane; the mod's systems read, set and train each character's level through it, and saves keep the levels. An id another
    /// skill has, or a column without room, refuses it.
    /// </summary>
    public Simulation.Bodies.ModSkill AddSkill(Simulation.Bodies.ModSkill skill)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(skill);
        if (Simulation.Bodies.ModSkills.Refusal(skill, Pending.Skills) is { } refusal) throw new ArgumentException(refusal, nameof(skill));
        if (Game.Ui.StatsWindow.ModRoom(skill.Column, Pending.Skills.Count(s => s.Column == skill.Column)) <= 0)
            throw new ArgumentException($"The stats window's {skill.Column} column has no room for skill '{skill.Id}'.", nameof(skill));

        Pending.Skills.Add(skill);
        return skill;
    }

    /// <summary>
    /// A named, timed status effect (docs/modding.md "Status effects"): the mod's systems put it on and take it off characters with
    /// <see cref="Simulation.Bodies.StatusEffect.Apply"/> and <see cref="Simulation.Bodies.StatusEffect.Remove"/>; the game shows it on the HUD's
    /// State line, adds its stat amounts while it lasts, ends it when its time runs out and saves it with the character. Its stat amounts are copied
    /// here. An id another effect has refuses it.
    /// </summary>
    public Simulation.Bodies.StatusEffect AddStatusEffect(Simulation.Bodies.StatusEffect effect)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(effect);
        // The stat amounts are copied now: a later change of the mod's list, even inside Initialize, does nothing.
        Simulation.Bodies.StatusEffectModifier[]? modifiers = effect.Modifiers is null ? null : [.. effect.Modifiers];
        var asked = modifiers is null ? effect : effect with { Modifiers = modifiers };
        if (Simulation.Bodies.StatusEffects.Refusal(asked, Pending.StatusEffects.Select(p => p.Effect)) is { } refusal)
            throw new ArgumentException(refusal, nameof(effect));

        Pending.StatusEffects.Add((effect, modifiers!));
        return effect;
    }

    /// <summary>
    /// A combat move for a character the game plays nothing for (docs/modding.md "Combat moves"): <paramref name="source"/> answers a
    /// <see cref="CombatMove"/> (technique and progress) or null; the first mod's answer wins. It runs in the animation's parallel step for every
    /// stepping character, and what it answers is part of the hashed world (the move's layers and root motion), so it must be cheap,
    /// deterministic, thread-safe and read-only. A fault (a throw, no technique, a progress that is not finite) drops the source from the next tick.
    /// </summary>
    public void AddCombatMove(CombatMoveSource source)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(source);
        Pending.CombatMoves.Add(source);
    }

    /// <summary>
    /// A right click on an item of <paramref name="record"/> (an item record's string id) in a player character's own inventory window, with no
    /// other character's window open (docs/modding.md "Item use"): <paramref name="handler"/> runs in a serial step of the tick and may change the
    /// world. It returns true when it used the click (removing a used-up item itself), false to let the click equip or move the item as usual. One
    /// mod per record; a throw is logged and leaves the item where it is.
    /// </summary>
    public void AddItemUse(string record, ItemUseHandler handler)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(record)) throw new ArgumentException("An item use needs a record id.", nameof(record));
        _registry.CheckItemUseFree(record, Pending, Id);
        Pending.ItemUses[record] = handler;
    }

    /// <summary>
    /// A job type of the mod's own (docs/modding.md "Mod jobs"): a task type number (<see cref="SimulationExtensions.FirstModCode"/> and up, so
    /// never a game task), which the mod puts in characters' job lists and runs itself. The jobs panel names it <paramref name="name"/>; a save keeps
    /// it in its place in the list under Meitou keys (the original's list never holds it), and a load puts it back while the type is registered.
    /// One mod per number, whether claimed as a job or a goal type (a mod may claim one number as both).
    /// </summary>
    public void AddJobType(Data.Gameplay.Ai.TaskType type, string name)
    {
        CheckOpen();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A job type needs a name.", nameof(name));

        _registry.CheckJobTypeFree(type, Pending, Id);
        Pending.JobTypes[type] = name;
    }

    /// <summary>
    /// An offer of one of the mod's job types on the player's buildings (docs/modding.md "Mod jobs"): the right-click menu of a finished building
    /// of the player's faction whose record the offer names shows its caption to a selection holding a character it is for, and choosing it gives
    /// each such character the job on that building, in the place of the one of that type it held. After the type's <see cref="AddJobType"/>; one
    /// offer per type. Refused: what <see cref="Simulation.Production.ModJobOffer.Refusal"/> names, a caption that is the label of one of the
    /// game's own verbs, and a building that is no BUILDING of the loaded data.
    /// </summary>
    public void AddJobOffer(Simulation.Production.ModJobOffer offer)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(offer);
        if (!Pending.JobTypes.ContainsKey(offer.Type))
            throw new InvalidOperationException($"Job type {offer.Type} is not one mod '{Id}' has claimed (AddJobType comes first).");
        if (Pending.JobOffers.Any(o => o.Type == offer.Type))
            throw new InvalidOperationException($"Job type {offer.Type} is offered twice by mod '{Id}'.");
        if (JobOfferRefusal(offer) is { } refusal) throw refusal;

        Pending.JobOffers.Add(offer with { Buildings = [.. offer.Buildings.Distinct(StringComparer.Ordinal)] });
    }

    /// <summary>
    /// Why the mod's job offer cannot be registered, or null: its own refusal, a caption the game's verbs have, or a building the data lacks. A
    /// message for the mod's author, not the player.
    /// </summary>
    ArgumentException? JobOfferRefusal(Simulation.Production.ModJobOffer offer)
    {
        if (offer.Refusal() is { } refusal) return new ArgumentException(refusal, nameof(offer));
        if (ContextMenuWindow.IsGameVerb(offer.Caption))
            return new ArgumentException($"'{offer.Caption}' is the label of one of the game's own verbs.", nameof(offer));

        string? missing = offer.Buildings.FirstOrDefault(b => Data.Find(b) is not { Type: global::Meitou.Data.Fcs.FcsRecordType.Building });
        return missing is null ? null : new ArgumentException($"'{missing}' is not a BUILDING of the loaded data.", nameof(offer));
    }

    /// <summary>
    /// A goal type of the mod's own (docs/modding.md "Chooser goals and jobs"): a task type number (<see cref="SimulationExtensions.FirstModCode"/>
    /// and up, so never a game task) that AI_TASK records of a data mod name in AI packages and goal lists; characters whose lists hold it weigh it in
    /// the AI's chooser by the desire of the goal one of the mod's systems registers (<see cref="Meitou.Simulation.Ai.IChooserOwner"/>), and the
    /// mod acts it out. Log lines and notices name it <paramref name="name"/>. One mod per number, whether claimed as a goal or a job type (a mod may
    /// claim one number as both); without the mod the goal stays inert.
    /// </summary>
    public void AddGoalType(Data.Gameplay.Ai.TaskType type, string name)
    {
        CheckOpen();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A goal type needs a name.", nameof(name));

        _registry.CheckGoalTypeFree(type, Pending, Id);
        Pending.GoalTypes[type] = name;
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
        Pending.UiFolders[extension] = Folder;
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
    /// Plays a display-only effect on <paramref name="anchor"/> (docs/modding.md "Visual effects"): a world point, a character's bone or held
    /// weapon, or a building, the system's origin at the anchor, its axes turned to the anchor's frame and its emitters scaled by its stretch,
    /// following it while it emits; while the anchor cannot be located (not drawn, weapon sheathed, building gone) it pauses, until its lifetime
    /// ends. <paramref name="effect"/> is an EFFECT record's string id or a particle system name, <paramref name="seconds"/> how long it emits (0
    /// to <see cref="ModEffects.MaxSeconds"/>), <paramref name="tint"/> the particles' colour (rgb, 1 the full channel, up to
    /// <see cref="ModEffects.MaxTint"/>; null: their texture's). Each call plays a new one (<see cref="KeepEffect"/> renews one). On the host's
    /// thread, from a host-side callback such as a combat hit feed, never a tick system; started at the next frame. False when rejected (logged
    /// once per mod and problem).
    /// </summary>
    public bool PlayEffect(string effect, EffectAnchor anchor, float seconds = 1, System.Numerics.Vector3? tint = null) =>
        _registry.Effects.Play(Id, effect, anchor, tint, seconds);

    /// <summary>
    /// Plays a display-only effect at a world point: <see cref="PlayEffect(string, EffectAnchor, float, System.Numerics.Vector3?)"/> on
    /// <see cref="EffectAnchor.At"/>.
    /// </summary>
    public bool PlayEffect(string effect, System.Numerics.Vector3 at, float seconds = 1, System.Numerics.Vector3? tint = null) =>
        PlayEffect(effect, EffectAnchor.At(at), seconds, tint);

    /// <summary>
    /// Plays a display-only effect on a character's <paramref name="bone"/> (null: the chest, <c>Bip01 Spine2</c>):
    /// <see cref="PlayEffect(string, EffectAnchor, float, System.Numerics.Vector3?)"/> on <see cref="EffectAnchor.Bone"/>.
    /// </summary>
    public bool PlayEffect(string effect, CharacterId on, string? bone = null, float seconds = 1, System.Numerics.Vector3? tint = null) =>
        PlayEffect(effect, EffectAnchor.Bone(on, bone ?? EffectAnchor.ChestBone), seconds, tint);

    /// <summary>
    /// Plays <paramref name="effect"/> on <paramref name="anchor"/> as <see cref="PlayEffect(string, EffectAnchor, float,
    /// System.Numerics.Vector3?)"/>, but as this mod's one for that effect and anchor: called again before it ends it is renewed to
    /// <paramref name="seconds"/> from then rather than restarted (another tint restarts it), so a mod keeps an aura up by calling it within
    /// <see cref="ModEffects.MaxSeconds"/>.
    /// </summary>
    public bool KeepEffect(string effect, EffectAnchor anchor, float seconds, System.Numerics.Vector3? tint = null) =>
        _registry.Effects.Keep(Id, effect, anchor, tint, seconds);

    /// <summary>
    /// Ends this mod's kept <paramref name="effect"/> on <paramref name="anchor"/> (<see cref="KeepEffect"/>); its particles live out. False when
    /// it keeps none there.
    /// </summary>
    public bool StopEffect(string effect, EffectAnchor anchor) => _registry.Effects.Stop(Id, effect, anchor);

    /// <summary>
    /// Makes <paramref name="target"/> give off light of its own (docs/modding.md "Glow"), display only: a whole character, the blade it holds
    /// drawn, or its parts in one slot. <paramref name="colour"/> is rgb (1 the full channel, up to <see cref="ModGlows.MaxColour"/>),
    /// <paramref name="seconds"/> its lifetime (0 to <see cref="ModGlows.MaxSeconds"/>, on the game clock), <paramref name="strength"/> 0 to
    /// <see cref="ModGlows.MaxStrength"/>. One glow a target: it replaces the one before, fading from where that stood, so calling it again
    /// before it ends keeps it up with no restart. Particles are separate (<see cref="KeepEffect"/>). On the host's thread, as
    /// <see cref="PlayEffect(string, EffectAnchor, float, System.Numerics.Vector3?)"/>. False when rejected (logged once per mod and problem).
    /// </summary>
    public bool Glow(GlowTarget target, System.Numerics.Vector3 colour, float seconds, float strength = 1) =>
        _registry.Glows.Start(Id, target, colour, seconds, strength);

    /// <summary>
    /// Fades out early the glow this mod set on <paramref name="target"/>; false when it set none there.
    /// </summary>
    public bool StopGlow(GlowTarget target) => _registry.Glows.Stop(Id, target);

    /// <summary>
    /// Whether <paramref name="effect"/> (an EFFECT record's string id or a particle system name) names a particle system the game has now, so a
    /// mod can choose another before asking for it. False before the particles load, while they are off, or off the host's thread.
    /// </summary>
    public bool HasEffect(string effect) => _registry.Effects.Has(effect);

    /// <summary>
    /// Arranges the faction tab's relations list (docs/modding.md "Faction list"): <paramref name="arrange"/> gets the rows the tab would show,
    /// sorted by name, and returns the rows to show in order: reordered, renamed, left out, or with headings (<see cref="FactionListRow.Faction"/>
    /// -1). Called on the host's thread whenever the tab is drawn, so it must not touch the world; mods chain in load order. A throw or a null is
    /// logged and the arranger is not called again.
    /// </summary>
    public void ArrangeFactionList(Func<IReadOnlyList<FactionListRow>, IReadOnlyList<FactionListRow>> arrange)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(arrange);
        Pending.FactionLists.Add(new ModFactionList(Id, arrange));
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
    /// Claims the WORLD_EVENT_STATE <paramref name="record"/> (docs/modding.md "World states"): while the mod has set a value on it in a world
    /// (<see cref="Simulation.Factions.ModWorldState.Set"/>, in a serial phase), every data record that tests the state reads that value; until then,
    /// and without the mod, its own conditions answer. The values are saved in the mod's save data. A record that is no WORLD_EVENT_STATE, or one
    /// another mod claimed, refuses the mod.
    /// </summary>
    public Simulation.Factions.ModWorldState AddWorldState(string record)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(record);
        if (Data.Find(record) is not { Type: global::Meitou.Data.Fcs.FcsRecordType.WorldEventState })
            throw new ArgumentException($"'{record}' is not a WORLD_EVENT_STATE of the loaded data.", nameof(record));
        _registry.CheckWorldStateFree(record, Pending, Id);

        var state = new Simulation.Factions.ModWorldState(Id, record);
        Pending.WorldStates.Add(state);
        return state;
    }

    /// <summary>
    /// A key action (docs/modding.md "Key actions"): a row named <paramref name="label"/> on the settings' Controls page after the game's, bound to
    /// those of <paramref name="defaults"/> that no other action holds, rebindable and saved as the game's are (under <paramref name="name"/>, unique
    /// within the mod). Each press in the game view runs <paramref name="pressed"/> on the host's thread while a tick may be in flight: it must not
    /// touch the world, only read the context and queue commands. A throw is logged and the action gets no more presses.
    /// </summary>
    public Game.Host.Input.ModKeyAction AddKeyAction(string name, string label, Action<Game.Host.Input.ModKeyContext> pressed,
        params Game.Host.Input.Binding[] defaults)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(pressed);
        ArgumentNullException.ThrowIfNull(defaults);
        if (Game.Host.Input.ModKeyAction.Refusal(name, label, defaults, Pending.KeyActions) is { } refusal)
            throw new ArgumentException(refusal, nameof(name));

        var action = new Game.Host.Input.ModKeyAction(Id, Name, name, label, pressed, [.. defaults]);
        Pending.KeyActions.Add(action);
        return action;
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