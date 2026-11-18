using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// How a status effect reads to the player: the colour of its name on the HUD's State line.
/// </summary>
public enum StatusEffectTone
{
    /// <summary>
    /// The line's plain colour.
    /// </summary>
    Neutral = 0,
    /// <summary>
    /// The good colour (a tonic, a blessing).
    /// </summary>
    Good = 1,
    /// <summary>
    /// The bad colour (a poison, a sickness).
    /// </summary>
    Bad = 2,
}

/// <summary>
/// What applying a status effect a character already has does (<see cref="StatusEffect.Apply"/>).
/// </summary>
public enum StatusEffectStacking
{
    /// <summary>
    /// The effect's time starts again at the new duration; one stack.
    /// </summary>
    Refresh = 0,
    /// <summary>
    /// The new duration is added to the time left; one stack.
    /// </summary>
    Extend = 1,
    /// <summary>
    /// Stacks are added up to <see cref="StatusEffect.MaxStacks"/> and the time starts again at the new duration; the stat amounts count once per
    /// stack.
    /// </summary>
    Stack = 2,
}

/// <summary>
/// One stat amount a status effect adds per stack, through the character's <see cref="CharacterStats.Modifiers"/>.
/// </summary>
/// <param name="Stat">
/// The stat (one with a stored value; one per storage in an effect).
/// </param>
/// <param name="Amount">
/// The amount per stack (finite, and finite times the effect's <see cref="StatusEffect.MaxStacks"/>).
/// </param>
public readonly record struct StatusEffectModifier(StatsEnumerated Stat, float Amount);

/// <summary>
/// One status effect on a character: its id, the ticks left (-1: until removed) and its stacks.
/// </summary>
/// <param name="Id">
/// The effect's id (<see cref="StatusEffect.Id"/>).
/// </param>
/// <param name="TicksLeft">
/// Ticks until it ends; -1 while it lasts until removed.
/// </param>
/// <param name="Stacks">
/// Its stacks (1 unless the effect stacks).
/// </param>
public readonly record struct ActiveStatusEffect(string Id, long TicksLeft, int Stacks)
{
    /// <summary>
    /// It lasts until removed.
    /// </summary>
    public bool Forever => TicksLeft < 0;
}

/// <summary>
/// A status effect as the HUD shows it for a selected or inspected character: the registered effect, the game hours left (infinity: until removed)
/// and its stacks.
/// </summary>
/// <param name="Effect">
/// The registered effect (its name, description and tone).
/// </param>
/// <param name="HoursLeft">
/// Game hours left; positive infinity while it lasts until removed.
/// </param>
/// <param name="Stacks">
/// Its stacks.
/// </param>
public readonly record struct StatusEffectView(StatusEffect Effect, double HoursLeft, int Stacks);

/// <summary>
/// A named, timed status effect a code mod added (docs/modding.md "Status effects"): the player sees it on the HUD's State line, its stat amounts
/// go through the character's <see cref="CharacterStats.Modifiers"/> under <see cref="ModifierKey"/> while it lasts, it ends by itself on the tick
/// its time runs out, and saves keep it with the character.
/// </summary>
/// <param name="Id">
/// The effect's stable id: 1 to 48 letters, digits, '_', '.' or '-'; the save's key.
/// </param>
/// <param name="Name">
/// The name the player sees (1 to 32 characters).
/// </param>
public sealed record StatusEffect(string Id, string Name)
{
    /// <summary>
    /// The text under its name in the State line's tooltip.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// The colour of its name.
    /// </summary>
    public StatusEffectTone Tone { get; init; }

    /// <summary>
    /// What applying it again does.
    /// </summary>
    public StatusEffectStacking Stacking { get; init; }

    /// <summary>
    /// The most stacks it reaches with <see cref="StatusEffectStacking.Stack"/> (at least 1).
    /// </summary>
    public int MaxStacks { get; init; } = 1;

    /// <summary>
    /// The stat amounts it adds per stack while it lasts; copied when the effect is registered, so a later change of the list does nothing.
    /// </summary>
    public IReadOnlyList<StatusEffectModifier> Modifiers { get; init; } = [];

    /// <summary>
    /// The key of its amounts in <see cref="CharacterStats.Modifiers"/>: <see cref="StatusEffects.ModifierPrefix"/> and the id.
    /// </summary>
    public string ModifierKey => StatusEffects.ModifierPrefix + Id;

    /// <summary>
    /// Puts the effect on character <paramref name="who"/> for <paramref name="hours"/> game hours (positive infinity: until removed) with
    /// <paramref name="stacks"/> stacks (counted only by <see cref="StatusEffectStacking.Stack"/>); a character that has it already follows
    /// <see cref="Stacking"/>. From the mod's system outside the parallel phases, as <see cref="CharacterStats.Modifiers"/>. False when the id names
    /// no character in the world with stats, or this effect was not registered. Throws for hours that are not above 0 or stacks below 1.
    /// </summary>
    public bool Apply(World world, CharacterId who, double hours, int stacks = 1)
    {
        if (double.IsNaN(hours) || hours <= 0) throw new ArgumentOutOfRangeException(nameof(hours), hours, "An effect lasts more than 0 hours.");
        ArgumentOutOfRangeException.ThrowIfLessThan(stacks, 1);
        if (StatusEffects.EntryOf(this) is not { } entry || StatsOf(world, who) is not { } stats) return false;

        stats.Effects.Apply(entry, StatusEffects.Ticks(world, hours), stacks, stats);
        return true;
    }

    /// <summary>
    /// Takes the effect, every stack, off character <paramref name="who"/> and its stat amounts with it; false when it did not have it. From the
    /// mod's system outside the parallel phases.
    /// </summary>
    public bool Remove(World world, CharacterId who) => StatsOf(world, who) is { } stats && StatusEffects.Forget(stats, Id);

    /// <summary>
    /// The effect on <paramref name="stats"/>' character; null when it does not have it.
    /// </summary>
    public ActiveStatusEffect? On(CharacterStats stats) => stats.EffectsOrNull?.Get(Id);

    /// <summary>
    /// The effect on character <paramref name="who"/>; null when it does not have it or the id names no character in the world.
    /// </summary>
    public ActiveStatusEffect? On(World world, CharacterId who) => StatsOf(world, who) is { } stats ? On(stats) : null;

    /// <summary>
    /// The stats of the character <paramref name="who"/> names in the world; null when none.
    /// </summary>
    static CharacterStats? StatsOf(World world, CharacterId who) =>
        world.Characters.TryResolveNext(who, out int slot) ? world.Characters.Cold(slot)?.Stats : null;
}
