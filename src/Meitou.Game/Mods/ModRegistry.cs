using System.Runtime.CompilerServices;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Modding;

enum ModCodeKind { DialogueCondition, DialogueEffect, RecordType }

/// <summary>What one mod registered during its <see cref="IMod.Initialize"/>; committed to the <see cref="ModRegistry"/> only when that returns.</summary>
sealed class ModRegistrations
{
    public List<SystemFactory> Systems { get; } = [];
    public Dictionary<int, DialogueConditionHandler> Conditions { get; } = [];
    public Dictionary<int, DialogueEffectHandler> Effects { get; } = [];
    public Dictionary<int, string> RecordTypes { get; } = [];
    public List<IUiExtension> Ui { get; } = [];
    public List<ModDebugCommand> DebugCommands { get; } = [];
    public List<ModHitFeed> HitFeeds { get; } = [];
    public ModSettingsGroup? Settings { get; set; }
}

/// <summary>A mod's host-side view of the resolved combat hits (<see cref="ModContext.AddCombatHitFeed"/>); faulted once it threw.</summary>
sealed class ModHitFeed(string owner, Action<IReadOnlyList<CombatHit>> run)
{
    public string Owner => owner;
    public Action<IReadOnlyList<CombatHit>> Run => run;
    public bool Faulted { get; set; }
}

/// <summary>A system a mod's factory made, for its save data: the mod and the name it was registered under.</summary>
sealed record ModSystemTag(string ModId, string Name);

/// <summary>Every mod's committed registrations, in load order, and which mod made each system of a world.</summary>
sealed class ModRegistry
{
    readonly Dictionary<(ModCodeKind Kind, int Code), string> _owners = [];
    readonly ConditionalWeakTable<ITickSystem, ModSystemTag> _made = new();

    public SimulationExtensions Simulation { get; } = new();
    public List<IUiExtension> Ui { get; } = [];
    public List<ModDebugCommand> DebugCommands { get; } = [];
    public List<ModHitFeed> HitFeeds { get; } = [];
    public Dictionary<int, string> RecordTypes { get; } = [];
    public List<ModSettingsGroup> Settings { get; } = [];

    /// <summary>Throws when a code is below the mods' range or another mod (or this one) has it already.</summary>
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

    public void Commit(string mod, ModRegistrations r)
    {
        foreach (var factory in r.Systems) Simulation.Systems.Add(Tagged(mod, factory));
        foreach (var (code, handler) in r.Conditions)
            Claim(ModCodeKind.DialogueCondition, code, mod, () => Simulation.DialogueConditions[code] = handler);
        foreach (var (code, handler) in r.Effects)
            Claim(ModCodeKind.DialogueEffect, code, mod, () => Simulation.DialogueEffects[code] = handler);
        foreach (var (type, name) in r.RecordTypes) Claim(ModCodeKind.RecordType, type, mod, () => RecordTypes[type] = name);
        Ui.AddRange(r.Ui);
        DebugCommands.AddRange(r.DebugCommands);
        HitFeeds.AddRange(r.HitFeeds);
        if (r.Settings is not null) Settings.Add(r.Settings);
    }

    /// <summary>The mod and name a world's system was made under; null for a standard system.</summary>
    public ModSystemTag? TagOf(ITickSystem system) => _made.TryGetValue(system, out var tag) ? tag : null;

    void Claim(ModCodeKind kind, int code, string mod, Action add)
    {
        _owners[(kind, code)] = mod;
        add();
    }

    SystemFactory Tagged(string mod, SystemFactory factory) => factory with
    {
        Create = context =>
        {
            ITickSystem? system;
            try { system = factory.Create(context); }
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