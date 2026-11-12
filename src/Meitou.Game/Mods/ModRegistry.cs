using System.Runtime.CompilerServices;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;

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
    /// The equipment slots, in registration order.
    /// </summary>
    public List<ModSlot> Slots { get; } = [];
    /// <summary>
    /// The player interface extensions.
    /// </summary>
    public List<IUiExtension> Ui { get; } = [];
    /// <summary>
    /// The debug menu's commands.
    /// </summary>
    public List<ModDebugCommand> DebugCommands { get; } = [];
    /// <summary>
    /// The combat hit feeds.
    /// </summary>
    public List<ModHitFeed> HitFeeds { get; } = [];
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
    /// Every mod's debug menu commands, in load order.
    /// </summary>
    public List<ModDebugCommand> DebugCommands { get; } = [];
    /// <summary>
    /// Every mod's combat hit feeds, in load order.
    /// </summary>
    public List<ModHitFeed> HitFeeds { get; } = [];
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
            Claim(ModCodeKind.DialogueCondition, code, mod, () => Simulation.DialogueConditions[code] = handler);
        foreach (var (code, handler) in r.Effects)
            Claim(ModCodeKind.DialogueEffect, code, mod, () => Simulation.DialogueEffects[code] = handler);
        foreach (var (type, name) in r.RecordTypes) Claim(ModCodeKind.RecordType, type, mod, () => RecordTypes[type] = name);
        foreach (var slot in r.Slots) ModSlots.Add(slot);
        Ui.AddRange(r.Ui);
        DebugCommands.AddRange(r.DebugCommands);
        HitFeeds.AddRange(r.HitFeeds);
        if (r.Settings is not null) Settings.Add(r.Settings);
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
    /// The factory with its systems tagged with the mod (<see cref="TagOf"/>); a system whose factory throws is left out of the world, with a
    /// log line.
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
                return null;
            }
            if (system is not null) _made.AddOrUpdate(system, new ModSystemTag(mod, factory.Name));
            return system;
        },
    };
}