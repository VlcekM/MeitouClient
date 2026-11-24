using System.Runtime.CompilerServices;
using Meitou.Content;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;

namespace Meitou.Modding;

/// <summary>
/// The kinds of numbered code a mod claims; each code has one owner (<see cref="ModRegistry.CheckFree"/>).
/// </summary>
enum ModCodeKind
{
    /// <summary>
    /// A dialogue condition's code (<see cref="ModContext.AddDialogueCondition"/>).
    /// </summary>
    DialogueCondition,
    /// <summary>
    /// A dialogue effect's code (<see cref="ModContext.AddDialogueEffect"/>).
    /// </summary>
    DialogueEffect,
    /// <summary>
    /// A record type's number (<see cref="ModContext.AddRecordType"/>).
    /// </summary>
    RecordType
}

/// <summary>
/// What one mod registered during its <see cref="IMod.Initialize"/>; committed to the <see cref="ModRegistry"/> only when that returns.
/// </summary>
sealed class ModRegistrations
{
    /// <summary>
    /// The tick system factories, in registration order.
    /// </summary>
    public List<SystemFactory> Systems { get; } = [];
    /// <summary>
    /// The dialogue condition handlers by code.
    /// </summary>
    public Dictionary<int, DialogueConditionHandler> Conditions { get; } = [];
    /// <summary>
    /// The dialogue effect handlers by code.
    /// </summary>
    public Dictionary<int, DialogueEffectHandler> Effects { get; } = [];
    /// <summary>
    /// The record types' names by type number.
    /// </summary>
    public Dictionary<int, string> RecordTypes { get; } = [];
    /// <summary>
    /// The combat move sources, in registration order.
    /// </summary>
    public List<CombatMoveSource> CombatMoves { get; } = [];
    /// <summary>
    /// The item use handlers by item record string id.
    /// </summary>
    public Dictionary<string, ItemUseHandler> ItemUses { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The mod's own job types and their names.
    /// </summary>
    public Dictionary<Data.Gameplay.Ai.TaskType, string> JobTypes { get; } = [];
    /// <summary>
    /// The mod's offers of its job types on the player's buildings, in registration order.
    /// </summary>
    public List<ModJobOffer> JobOffers { get; } = [];
    /// <summary>
    /// The mod's own NPC goal types and their names.
    /// </summary>
    public Dictionary<Data.Gameplay.Ai.TaskType, string> GoalTypes { get; } = [];
    /// <summary>
    /// The equipment slots, in registration order.
    /// </summary>
    public List<ModSlot> Slots { get; } = [];
    /// <summary>
    /// The layout replacements: the layout file name and the full path of the mod's file, in registration order.
    /// </summary>
    public List<(string Layout, string Path)> Layouts { get; } = [];
    /// <summary>
    /// The wear rules, in registration order.
    /// </summary>
    public List<Data.Gameplay.WearRule> WearRules { get; } = [];
    /// <summary>
    /// The character editor row sources, in registration order.
    /// </summary>
    public List<Data.Characters.LookRowSource> LookRows { get; } = [];
    /// <summary>
    /// The look rolls, in registration order.
    /// </summary>
    public List<Data.Characters.LookRoll> LookRolls { get; } = [];
    /// <summary>
    /// The look extras sources, in registration order.
    /// </summary>
    public List<Data.Characters.LookExtrasSource> LookExtras { get; } = [];
    /// <summary>
    /// The medical meters, in registration order.
    /// </summary>
    public List<ModMeter> Meters { get; } = [];
    /// <summary>
    /// The skills, in registration order.
    /// </summary>
    public List<ModSkill> Skills { get; } = [];
    /// <summary>
    /// The status effects, in registration order, each with its stat amounts as copied when the mod asked.
    /// </summary>
    public List<(StatusEffect Effect, StatusEffectModifier[] Modifiers)> StatusEffects { get; } = [];
    /// <summary>
    /// The player interface extensions.
    /// </summary>
    public List<IUiExtension> Ui { get; } = [];
    /// <summary>
    /// The folder of the mod each interface extension belongs to (its map marker images are files under it).
    /// </summary>
    public Dictionary<IUiExtension, string> UiFolders { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>
    /// The translations of the mod each interface extension belongs to (its texts are shown through them).
    /// </summary>
    public Dictionary<IUiExtension, Localisation.ModTranslations> UiTexts { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>
    /// The debug menu's commands.
    /// </summary>
    public List<ModDebugCommand> DebugCommands { get; } = [];
    /// <summary>
    /// The key actions.
    /// </summary>
    public List<Game.Host.Input.ModKeyAction> KeyActions { get; } = [];
    /// <summary>
    /// The world states the mod claimed.
    /// </summary>
    public List<Simulation.Factions.ModWorldState> WorldStates { get; } = [];
    /// <summary>
    /// The combat hit feeds.
    /// </summary>
    public List<ModHitFeed> HitFeeds { get; } = [];
    /// <summary>
    /// The projectile feeds.
    /// </summary>
    public List<ModProjectileFeed> ProjectileFeeds { get; } = [];
    /// <summary>
    /// The faction tab list arrangers.
    /// </summary>
    public List<ModFactionList> FactionLists { get; } = [];
    /// <summary>
    /// The save-written listeners.
    /// </summary>
    public List<ModSaveWritten> SavesWritten { get; } = [];
    /// <summary>
    /// The per-frame callbacks.
    /// </summary>
    public List<ModFrameView> Frames { get; } = [];
    /// The placement rules.
    /// </summary>
    public List<ModPlacementRule> PlacementRules { get; } = [];
    /// <summary>
    /// The mod's settings group; null when it registered none.
    /// </summary>
    public ModSettingsGroup? Settings { get; set; }
}

/// <summary>
/// A mod's host-side view of the resolved combat hits (<see cref="ModContext.AddCombatHitFeed"/>); faulted once it threw.
/// </summary>
sealed class ModHitFeed(string owner, Action<IReadOnlyList<CombatHit>> run)
{
    /// <summary>
    /// The id of the mod that registered the feed.
    /// </summary>
    public string Owner => owner;
    /// <summary>
    /// Hands the feed the hits resolved in a tick.
    /// </summary>
    public Action<IReadOnlyList<CombatHit>> Run => run;
    /// <summary>
    /// Set once the feed threw; a faulted feed is not called again.
    /// </summary>
    public bool Faulted { get; set; }
}

/// <summary>
/// A mod's listener for saves written (<see cref="ModContext.AddSaveWritten"/>); faulted once it threw.
/// </summary>
sealed class ModSaveWritten(string owner, Action<string> run)
{
    /// <summary>
    /// The id of the mod that registered it.
    /// </summary>
    public string Owner => owner;
    /// <summary>
    /// Tells it the full path of the save folder written.
    /// </summary>
    public Action<string> Run => run;
    /// <summary>
    /// Set once it threw; a faulted listener is not called again.
    /// </summary>
    public bool Faulted { get; set; }
}

/// <summary>
/// A mod's per-frame callback (<see cref="ModContext.AddFrame"/>); faulted once it threw.
/// </summary>
sealed class ModFrameView(string owner, Action<ModFrame> run)
{
    /// <summary>
    /// The id of the mod that registered it.
    /// </summary>
    public string Owner => owner;
    /// <summary>
    /// Hands it the frame.
    /// </summary>
    public Action<ModFrame> Run => run;
    /// <summary>
    /// Set once it threw; a faulted callback is not called again.
    /// </summary>
    public bool Faulted { get; set; }
}

/// <summary>
/// A mod's arrangement of the faction tab's list (<see cref="ModContext.ArrangeFactionList"/>); faulted once it threw.
/// </summary>
sealed class ModFactionList(string owner, Func<IReadOnlyList<FactionListRow>, IReadOnlyList<FactionListRow>> run)
{
    /// <summary>
    /// The id of the mod that registered it.
    /// </summary>
    public string Owner => owner;
    /// <summary>
    /// The rows to show for the rows given.
    /// </summary>
    public Func<IReadOnlyList<FactionListRow>, IReadOnlyList<FactionListRow>> Run => run;
    /// <summary>
    /// Set once it threw; a faulted arranger is not called again.
    /// </summary>
    public bool Faulted { get; set; }
}

/// <summary>
/// A system a mod's factory made, for its save data: the mod and the name it was registered under.
/// </summary>
sealed record ModSystemTag(string ModId, string Name);

/// <summary>
/// Every mod's committed registrations, in load order, and which mod made each system of a world.
/// </summary>
sealed class ModRegistry
{
    /// <summary>
    /// The owner of each claimed code, by kind and code.
    /// </summary>
    readonly Dictionary<(ModCodeKind Kind, int Code), string> _owners = [];
    /// <summary>
    /// The owner of each item record's use, by record string id.
    /// </summary>
    readonly Dictionary<string, string> _itemUseOwners = new(StringComparer.Ordinal);
    /// <summary>
    /// The owner of each mod job type.
    /// </summary>
    readonly Dictionary<Data.Gameplay.Ai.TaskType, string> _jobTypeOwners = [];
    /// <summary>
    /// The owner of each mod NPC goal type.
    /// </summary>
    readonly Dictionary<Data.Gameplay.Ai.TaskType, string> _goalTypeOwners = [];
    /// <summary>
    /// The world states mods claimed, by WORLD_EVENT_STATE string id.
    /// </summary>
    readonly Dictionary<string, Simulation.Factions.ModWorldState> _worldStates = new(StringComparer.Ordinal);
    /// <summary>
    /// The mod and name each world's mod system was made under (weakly held, gone with the world).
    /// </summary>
    readonly ConditionalWeakTable<ITickSystem, ModSystemTag> _made = new();

    /// <summary>
    /// The simulation's mod part: the systems (tagged with their mod) and the dialogue handlers.
    /// </summary>
    public SimulationExtensions Simulation { get; } = new();
    /// <summary>
    /// Every mod's interface extensions, in load order.
    /// </summary>
    public List<IUiExtension> Ui { get; } = [];
    /// <summary>
    /// The folder of the mod each interface extension belongs to.
    /// </summary>
    public Dictionary<IUiExtension, string> UiFolders { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>
    /// The id of the mod each interface extension belongs to.
    /// </summary>
    public Dictionary<IUiExtension, string> UiOwners { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>
    /// The translations of the mod each interface extension belongs to.
    /// </summary>
    public Dictionary<IUiExtension, Localisation.ModTranslations> UiTexts { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>
    /// Every mod's debug menu commands, in load order.
    /// </summary>
    public List<ModDebugCommand> DebugCommands { get; } = [];
    /// <summary>
    /// Every mod's key actions, in load order, numbered from <see cref="Game.Host.Input.InputBindings.FirstModAction"/>.
    /// </summary>
    public List<Game.Host.Input.ModKeyAction> KeyActions { get; } = [];
    /// <summary>
    /// Every mod's combat hit feeds, in load order.
    /// </summary>
    public List<ModHitFeed> HitFeeds { get; } = [];
    /// <summary>
    /// Every mod's projectile feeds, in load order.
    /// </summary>
    public List<ModProjectileFeed> ProjectileFeeds { get; } = [];
    /// <summary>
    /// Every mod's faction tab list arrangers, in load order.
    /// </summary>
    public List<ModFactionList> FactionLists { get; } = [];
    /// <summary>
    /// Every mod's save-written listeners, in load order.
    /// </summary>
    public List<ModSaveWritten> SavesWritten { get; } = [];
    /// <summary>
    /// Every mod's per-frame callbacks, in load order.
    /// </summary>
    public List<ModFrameView> Frames { get; } = [];
    /// <summary>
    /// The player interface the mods' <see cref="ModInterface"/>s act on (docs/modding.md "Interface actions").
    /// </summary>
    public ModInterfaceHub Interface { get; } = new();
    /// Every mod's placement rules, in load order.
    /// </summary>
    public List<ModPlacementRule> PlacementRules { get; } = [];
    /// <summary>
    /// The mods' queued and playing display-only effects (<see cref="ModContext.PlayEffect(string, System.Numerics.Vector3, float,
    /// System.Numerics.Vector3?)"/>).
    /// </summary>
    public ModEffects Effects { get; } = new();
    /// <summary>
    /// The mods' display-only glows (<see cref="ModContext.Glow"/>).
    /// </summary>
    public ModGlows Glows { get; } = new();
    /// <summary>
    /// The mods' display-only ribbon trails (<see cref="ModContext.KeepTrail"/>).
    /// </summary>
    public ModTrails Trails { get; } = new();
    /// <summary>
    /// The record types' names by type number.
    /// </summary>
    public Dictionary<int, string> RecordTypes { get; } = [];
    /// <summary>
    /// Every mod's settings group, in load order.
    /// </summary>
    public List<ModSettingsGroup> Settings { get; } = [];

    /// <summary>
    /// Throws when a code is below the mods' range or another mod (or this one) has it already.
    /// </summary>
    public void CheckFree(ModCodeKind kind, int code, ModRegistrations pending, string mod)
    {
        if (code < SimulationExtensions.FirstModCode)
        {
            string reason = $"Mod codes start at {SimulationExtensions.FirstModCode}; lower numbers are the game's.";
            throw new ArgumentOutOfRangeException(nameof(code), code, reason);
        }
        if (_owners.TryGetValue((kind, code), out var owner)) throw new InvalidOperationException($"{kind} {code} is taken by mod '{owner}'.");
        bool mine = kind switch
        {
            ModCodeKind.DialogueCondition => pending.Conditions.ContainsKey(code),
            ModCodeKind.DialogueEffect => pending.Effects.ContainsKey(code),
            _ => pending.RecordTypes.ContainsKey(code),
        };
        if (mine) throw new InvalidOperationException($"{kind} {code} is registered twice by mod '{mod}'.");
    }

    /// <summary>
    /// Adds a mod's registrations once its <see cref="IMod.Initialize"/> has returned: systems tagged with the mod, codes claimed, the rest appended.
    /// </summary>
    public void Commit(string mod, ModRegistrations r)
    {
        foreach (var factory in r.Systems) Simulation.Systems.Add(Tagged(mod, factory));
        foreach (var (code, handler) in r.Conditions)
            Claim(ModCodeKind.DialogueCondition, code, mod, () => Simulation.DialogueConditions[code] = Guarded(mod, code, handler));
        foreach (var (code, handler) in r.Effects)
            Claim(ModCodeKind.DialogueEffect, code, mod, () => Simulation.DialogueEffects[code] = Guarded(mod, code, handler));
        foreach (var (type, name) in r.RecordTypes) Claim(ModCodeKind.RecordType, type, mod, () => RecordTypes[type] = name);
        foreach (var source in r.CombatMoves) Simulation.CombatMoves.Add(new ModCombatMove(mod, source));
        foreach (var (record, handler) in r.ItemUses)
        {
            _itemUseOwners[record] = mod;
            Simulation.ItemUses[record] = Guarded(mod, record, handler);
        }
        foreach (var (type, name) in r.JobTypes)
        {
            _jobTypeOwners[type] = mod;
            Simulation.JobTypes[type] = name;
            Simulation.TaskOwners[type] = mod;
        }
        Simulation.JobOffers.AddRange(r.JobOffers);
        foreach (var (type, name) in r.GoalTypes)
        {
            _goalTypeOwners[type] = mod;
            Simulation.GoalTypes[type] = new ModGoalType(mod, name);
            Simulation.TaskOwners[type] = mod;
        }
        foreach (var slot in r.Slots) ModSlots.Add(slot);
        foreach (var (layout, path) in r.Layouts)
        {
            if (!Game.Ui.ModLayouts.Add(mod, layout, path))
                Console.Error.WriteLine($"mod       {mod}: layout {layout} is replaced by another mod already; its replacement is left out");
        }
        foreach (var rule in r.WearRules) Data.Gameplay.WearRules.Add(mod, rule);
        foreach (var source in r.LookRows) Data.Characters.ModLooks.AddRows(mod, source);
        foreach (var roll in r.LookRolls) Data.Characters.ModLooks.AddRoll(mod, roll);
        foreach (var source in r.LookExtras) Data.Characters.ModLooks.AddExtras(mod, source);
        foreach (var meter in r.Meters) ModMeters.Add(meter);
        foreach (var skill in r.Skills) ModSkills.Add(skill);
        foreach (var (effect, modifiers) in r.StatusEffects) StatusEffects.Add(effect, modifiers);
        Ui.AddRange(r.Ui);
        foreach (var (extension, folder) in r.UiFolders) UiFolders[extension] = folder;
        foreach (var extension in r.Ui) UiOwners[extension] = mod;
        foreach (var (extension, texts) in r.UiTexts) UiTexts[extension] = texts;
        DebugCommands.AddRange(r.DebugCommands);
        foreach (var state in r.WorldStates)
        {
            _worldStates[state.Record] = state;
            global::Meitou.Simulation.Factions.ModWorldStates.Claim(state);
        }
        foreach (var action in r.KeyActions)
        {
            action.Action = Game.Host.Input.InputBindings.FirstModAction + KeyActions.Count;
            KeyActions.Add(action);
        }
        HitFeeds.AddRange(r.HitFeeds);
        ProjectileFeeds.AddRange(r.ProjectileFeeds);
        FactionLists.AddRange(r.FactionLists);
        SavesWritten.AddRange(r.SavesWritten);
        Frames.AddRange(r.Frames);
        PlacementRules.AddRange(r.PlacementRules);
        if (r.Settings is not null) Settings.Add(r.Settings);
    }

    /// <summary>
    /// The world states mods claimed.
    /// </summary>
    public IReadOnlyCollection<Simulation.Factions.ModWorldState> WorldStates => _worldStates.Values;

    /// <summary>
    /// Throws when another mod (or this one) claimed the world state <paramref name="record"/> already.
    /// </summary>
    public void CheckWorldStateFree(string record, ModRegistrations pending, string mod)
    {
        if (_worldStates.TryGetValue(record, out var owner))
            throw new InvalidOperationException($"World state '{record}' is claimed by mod '{owner.Owner}'.");
        if (pending.WorldStates.Any(s => s.Record == record))
            throw new InvalidOperationException($"World state '{record}' is claimed twice by mod '{mod}'.");
    }

    /// <summary>
    /// Throws when another mod (or this one) handles the use of <paramref name="record"/> already.
    /// </summary>
    public void CheckItemUseFree(string record, ModRegistrations pending, string mod)
    {
        if (_itemUseOwners.TryGetValue(record, out var owner))
            throw new InvalidOperationException($"The use of item '{record}' is taken by mod '{owner}'.");
        if (pending.ItemUses.ContainsKey(record))
            throw new InvalidOperationException($"The use of item '{record}' is registered twice by mod '{mod}'.");
    }

    /// <summary>
    /// Throws when job type <paramref name="type"/> is below the mods' range, another mod (or this one) holds it already as a job type, or another
    /// mod holds it as an NPC goal type (one task type number has one owning mod; that mod may claim it as both).
    /// </summary>
    public void CheckJobTypeFree(Data.Gameplay.Ai.TaskType type, ModRegistrations pending, string mod)
    {
        if ((int)type < SimulationExtensions.FirstModCode)
        {
            string reason = $"Mod job types start at {SimulationExtensions.FirstModCode}; lower numbers are the game's tasks.";
            throw new ArgumentOutOfRangeException(nameof(type), type, reason);
        }
        if (_jobTypeOwners.TryGetValue(type, out var owner))
            throw new InvalidOperationException($"Job type {type} is taken by mod '{owner}'.");
        if (_goalTypeOwners.TryGetValue(type, out var goalOwner) && goalOwner != mod)
            throw new InvalidOperationException($"Task type {type} is taken by mod '{goalOwner}' as an NPC goal type.");
        if (pending.JobTypes.ContainsKey(type))
            throw new InvalidOperationException($"Job type {type} is registered twice by mod '{mod}'.");
    }

    /// <summary>
    /// Throws when NPC goal type <paramref name="type"/> is below the mods' range, another mod (or this one) holds it already as a goal type, or
    /// another mod holds it as a job type (one task type number has one owning mod; that mod may claim it as both).
    /// </summary>
    public void CheckGoalTypeFree(Data.Gameplay.Ai.TaskType type, ModRegistrations pending, string mod)
    {
        if ((int)type < SimulationExtensions.FirstModCode)
        {
            string reason = $"Mod goal types start at {SimulationExtensions.FirstModCode}; lower numbers are the game's tasks.";
            throw new ArgumentOutOfRangeException(nameof(type), type, reason);
        }
        if (_goalTypeOwners.TryGetValue(type, out var owner))
            throw new InvalidOperationException($"Goal type {type} is taken by mod '{owner}'.");
        if (_jobTypeOwners.TryGetValue(type, out var jobOwner) && jobOwner != mod)
            throw new InvalidOperationException($"Task type {type} is taken by mod '{jobOwner}' as a job type.");
        if (pending.GoalTypes.ContainsKey(type))
            throw new InvalidOperationException($"Goal type {type} is registered twice by mod '{mod}'.");
    }

    /// <summary>
    /// The mod and name a world's system was made under; null for a standard system.
    /// </summary>
    public ModSystemTag? TagOf(ITickSystem system) => _made.TryGetValue(system, out var tag) ? tag : null;

    /// <summary>
    /// Records the mod as the code's owner and adds its handler.
    /// </summary>
    void Claim(ModCodeKind kind, int code, string mod, Action add)
    {
        _owners[(kind, code)] = mod;
        add();
    }

    /// <summary>
    /// The handler with its throws caught: a mod's fault is logged and the click counts as used, so the item stays where it is.
    /// </summary>
    static ItemUseHandler Guarded(string mod, string record, ItemUseHandler handler) => use =>
    {
        try
        {
            return handler(use);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod       {mod}: use of item '{record}' failed: {e}");
            CodeModFaults.Tell(mod, e);
            return true;
        }
    };

    /// <summary>
    /// The condition with its throws caught: a throw computes "no", as a code no mod handles, and is logged once. It is not dropped, since the
    /// interface also asks conditions between ticks (who can be talked to), so a drop would make the world depend on what the interface asked.
    /// </summary>
    static DialogueConditionHandler Guarded(string mod, int code, DialogueConditionHandler handler)
    {
        int logged = 0;
        return check =>
        {
            try
            {
                return handler(check);
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref logged, 1) == 0)
                    Console.Error.WriteLine($"mod       {mod}: dialogue condition {code} failed, answered as unhandled: {e}");

                CodeModFaults.Tell(mod, e);

                return check.Matches(false);
            }
        };
    }

    /// <summary>
    /// The effect with its throws caught: a throw does nothing more (what the handler did before it stands) and is logged once.
    /// </summary>
    static DialogueEffectHandler Guarded(string mod, int code, DialogueEffectHandler handler)
    {
        int logged = 0;
        return act =>
        {
            try
            {
                handler(act);
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref logged, 1) == 0)
                    Console.Error.WriteLine($"mod       {mod}: dialogue effect {code} failed: {e}");

                CodeModFaults.Tell(mod, e);
            }
        };
    }

    /// <summary>
    /// The factory with its systems tagged with the mod (<see cref="TagOf"/>); a system whose factory throws is left out of the world, with a
    /// log line, and the player's notice when the mod was built for another version of the game (<see cref="CodeModFaults.Tell"/>).
    /// </summary>
    SystemFactory Tagged(string mod, SystemFactory factory) => factory with
    {
        Create = context =>
        {
            ITickSystem? system;
            try
            {
                system = factory.Create(context);
            }
            catch (Exception e)
            {
                // A mod's fault leaves its system out of this world; the world is still made.
                Console.Error.WriteLine($"mod       {mod}: system '{factory.Name}' not made: {e}");
                CodeModFaults.Tell(mod, e);
                return null;
            }
            if (system is not null) _made.AddOrUpdate(system, new ModSystemTag(mod, factory.Name));
            return system;
        },
    };
}