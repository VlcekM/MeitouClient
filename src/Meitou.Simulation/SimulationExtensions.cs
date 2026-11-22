using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Population;

namespace Meitou.Simulation;

/// <summary>
/// Where an extra system goes in the standard wiring (docs/modding.md "Registration surface"): before the first or after the last system of one
/// <see cref="StandardParts"/> member, or at the end. A part the world does not build (the sandbox has no aggro) puts the system at the end.
/// </summary>
public readonly record struct SystemAnchor
{
    /// <summary>
    /// An anchor at <paramref name="part"/> (one standard part, or none for the end).
    /// </summary>
    SystemAnchor(StandardParts part, bool after)
    {
        if (part != StandardParts.None && !BitOperations.IsPow2((uint)part))
            throw new ArgumentException("An anchor names one standard part.", nameof(part));

        Part = part;
        After = after;
    }

    /// <summary>
    /// The standard part the system goes next to (<see cref="StandardParts.None"/>: the end).
    /// </summary>
    public StandardParts Part { get; }
    /// <summary>
    /// Whether it goes after the part's last system (else before its first).
    /// </summary>
    public bool After { get; }

    /// <summary>
    /// After every standard system.
    /// </summary>
    public static SystemAnchor End => new(StandardParts.None, true);

    /// <summary>
    /// Before the first system of <paramref name="part"/>.
    /// </summary>
    public static SystemAnchor Before(StandardParts part) => new(part, false);

    /// <summary>
    /// After the last system of <paramref name="part"/>.
    /// </summary>
    public static SystemAnchor AfterPart(StandardParts part) => new(part, true);

    public override string ToString() => Part == StandardParts.None ? "end" : $"{(After ? "after" : "before")} {Part}";
}

/// <summary>
/// What a system factory gets each time a world's systems are built: the data and the standard systems already made, in tick order.
/// </summary>
public sealed class SystemBuildContext(PopulationData data, IReadOnlyList<ITickSystem> standard)
{
    /// <summary>
    /// The population data.
    /// </summary>
    public PopulationData Data { get; } = data;
    /// <summary>
    /// The standard systems already made, in tick order.
    /// </summary>
    public IReadOnlyList<ITickSystem> Standard { get; } = standard;

    /// <summary>
    /// The first standard system of a type (null when this world does not build it).
    /// </summary>
    public T? Find<T>() where T : class, ITickSystem => Standard.OfType<T>().FirstOrDefault();
}

/// <summary>
/// An extra system: made again for every world (each new game and load builds its systems afresh), placed at <see cref="Anchor"/>. A factory that
/// returns null leaves the system out of that world.
/// </summary>
public sealed record SystemFactory(string Owner, string Name, SystemAnchor Anchor, Func<SystemBuildContext, ITickSystem?> Create);

/// <summary>
/// A dialogue condition code a mod handles (docs/modding.md "Registration surface"), with the speaker and the subject the condition's <c>who</c>
/// picked (<see cref="CharacterId.None"/> when there is none). Handlers only read: they also run for the interface's previews and from other systems'
/// serial steps.
/// </summary>
public readonly record struct DialogueCheck(World World, CharacterId Speaker, CharacterId Subject, DialogueCondition Condition)
{
    /// <summary>
    /// A yes/no condition: passes when the computed answer equals <c>value &gt; 0</c>, as the vanilla ones do.
    /// </summary>
    public bool Matches(bool computed) => computed == Condition.Value > 0;

    /// <summary>
    /// A count condition: compared with the value by <c>compare by</c> (1 &lt;, 2 &gt;, else ==), as the vanilla ones do.
    /// </summary>
    public bool Compare(double value) => Condition.CompareBy switch
    {
        1 => value < Condition.Value,
        2 => value > Condition.Value,
        _ => value == Condition.Value,
    };
}

/// <summary>
/// A dialogue effect code a mod handles, run in a serial step as the line is said, with the NPC-side speaker and the conversation's target.
/// </summary>
public readonly record struct DialogueAct(World World, CharacterId Speaker, CharacterId Target, DialogueEffect Effect);

/// <summary>
/// A mod's dialogue condition code: whether the condition holds for the check.
/// </summary>
public delegate bool DialogueConditionHandler(DialogueCheck check);

/// <summary>
/// A mod's dialogue effect code, run as the line is said.
/// </summary>
public delegate void DialogueEffectHandler(DialogueAct act);

/// <summary>
/// A right click on an item whose record a mod handles (docs/modding.md "Item use"), run in a serial step: the player character whose own window
/// holds the item, the item, and that character's inventory (the item may be in a bag inside it; <see cref="Items.Inventory.RemoveOne"/> finds it).
/// </summary>
public readonly record struct ItemUse(World World, CharacterId User, Items.ItemInstance Item, Items.Inventory Inventory);

/// <summary>
/// A mod's item use: true when it used the click (the handler removes a used-up item itself), false to let the click equip or move the item as usual.
/// </summary>
public delegate bool ItemUseHandler(ItemUse use);

/// <summary>
/// A combat move a mod has a character play: the technique and its progress (0 to 1). Animation only: it lands no blow.
/// </summary>
public readonly record struct CombatMove(Data.Gameplay.Combat.CombatTechnique Technique, float Progress);

/// <summary>
/// A mod's combat move for a character the game plays nothing for (docs/modding.md "Combat moves"); null for none. Asked in the animation's
/// parallel step, whose state is hashed and whose root motion moves the character, so it must be deterministic, thread-safe and read-only.
/// </summary>
public delegate CombatMove? CombatMoveSource(World world, CharacterId id, long tick);

/// <summary>
/// A combat move source and the mod that registered it (named in its fault's log line).
/// </summary>
public sealed record ModCombatMove(string Owner, CombatMoveSource Source);

/// <summary>
/// A goal type a mod claimed (docs/modding.md "Chooser goals and jobs"): the mod's id and the name its log lines give the goal.
/// </summary>
public sealed record ModGoalType(string Owner, string Name);

/// <summary>
/// What code mods add to the simulation (docs/modding.md): extra systems at anchors, dialogue codes, combat moves and item uses. Filled once at
/// startup, before the first world is built, and read by <see cref="StandardSystems.Build"/> for every world; never changed while a world runs.
/// </summary>
public sealed class SimulationExtensions
{
    /// <summary>
    /// The lowest dialogue condition, dialogue effect, record type, job type and NPC goal type number a mod may register; the vanilla numbers stay
    /// below it.
    /// </summary>
    public const int FirstModCode = 1000;

    /// <summary>
    /// The extra systems' factories.
    /// </summary>
    public List<SystemFactory> Systems { get; } = [];
    /// <summary>
    /// The mods' dialogue conditions by code.
    /// </summary>
    public Dictionary<int, DialogueConditionHandler> DialogueConditions { get; } = [];
    /// <summary>
    /// The mods' dialogue effects by code.
    /// </summary>
    public Dictionary<int, DialogueEffectHandler> DialogueEffects { get; } = [];
    /// <summary>
    /// The mods' combat move sources, in load order; the first that answers wins.
    /// </summary>
    public List<ModCombatMove> CombatMoves { get; } = [];
    /// <summary>
    /// The mods' item uses by item record string id.
    /// </summary>
    public Dictionary<string, ItemUseHandler> ItemUses { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The mods' own job types (no game job) and the jobs panel's name of each (docs/modding.md "Mod jobs").
    /// </summary>
    public Dictionary<Data.Gameplay.Ai.TaskType, string> JobTypes { get; } = [];
    /// <summary>
    /// The mods' offers of their job types on the player's buildings, in registration order, one per job type (docs/modding.md "Mod jobs").
    /// </summary>
    public List<Production.ModJobOffer> JobOffers { get; } = [];
    /// <summary>
    /// The mods' own goal types, each with the mod that claimed it and its name (docs/modding.md "Chooser goals and jobs";
    /// <see cref="Ai.ChooserRegistry"/>).
    /// </summary>
    public Dictionary<Data.Gameplay.Ai.TaskType, ModGoalType> GoalTypes { get; } = [];
    /// <summary>
    /// The mod that claimed each task type number, as a job or a goal type: a task row from 1000 up is that mod's (<see cref="Ai.TaskHandlers"/>
    /// refuses another mod's handler for it).
    /// </summary>
    public Dictionary<Data.Gameplay.Ai.TaskType, string> TaskOwners { get; } = [];

    /// <summary>
    /// Whether the mods add nothing.
    /// </summary>
    public bool IsEmpty => Systems.Count == 0 && DialogueConditions.Count == 0 && DialogueEffects.Count == 0 && CombatMoves.Count == 0
        && ItemUses.Count == 0 && JobTypes.Count == 0 && GoalTypes.Count == 0;

    /// <summary>
    /// Each extra system's factory, for log lines that name the mod and the system; weak, so a dropped world's systems go with it.
    /// </summary>
    static readonly ConditionalWeakTable<ITickSystem, SystemFactory> MadeBy = new();

    /// <summary>
    /// The factory an extra system was made by (<see cref="SystemFactory.Owner"/>, <see cref="SystemFactory.Name"/>); null for a standard system.
    /// </summary>
    public static SystemFactory? FactoryOf(ITickSystem system) => MadeBy.TryGetValue(system, out var factory) ? factory : null;

    /// <summary>
    /// The player's one notice for a mod built for another version of the game (<see cref="Content.CodeModFaults.Tell"/>) when
    /// <paramref name="system"/>, whose hook threw <paramref name="e"/>, was made by a mod; nothing for a standard system. Any thread.
    /// </summary>
    internal static void TellOutdated(ITickSystem system, Exception e)
    {
        if (FactoryOf(system) is { } factory) Content.CodeModFaults.Tell(factory.Owner, e);
    }

    /// <summary>
    /// Notes the factory an extra system was made by.
    /// </summary>
    internal static void Made(ITickSystem system, SystemFactory factory) => MadeBy.AddOrUpdate(system, factory);
}