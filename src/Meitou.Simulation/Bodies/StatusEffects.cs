using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// How a status effect reads to the player: the colour of its name on the HUD's State line.
/// </summary>
public enum EffectTone
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
public enum EffectStacking
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
    /// Stacks are added up to <see cref="StatusEffect.MaxStacks"/> and the time starts again at the new duration; the stat modifiers count once per
    /// stack.
    /// </summary>
    Stack = 2,
}

/// <summary>
/// One stat amount a status effect adds per stack, through the character's <see cref="CharacterStats.Modifiers"/>.
/// </summary>
/// <param name="Stat">
/// The stat (one with a stored value).
/// </param>
/// <param name="Amount">
/// The amount per stack (finite).
/// </param>
public readonly record struct EffectModifier(StatsEnumerated Stat, float Amount);

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
public readonly record struct ActiveEffect(string Id, long TicksLeft, int Stacks)
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
public readonly record struct EffectView(StatusEffect Effect, double HoursLeft, int Stacks);

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
    public EffectTone Tone { get; init; }

    /// <summary>
    /// What applying it again does.
    /// </summary>
    public EffectStacking Stacking { get; init; }

    /// <summary>
    /// The most stacks it reaches with <see cref="EffectStacking.Stack"/> (at least 1).
    /// </summary>
    public int MaxStacks { get; init; } = 1;

    /// <summary>
    /// The stat amounts it adds per stack while it lasts.
    /// </summary>
    public IReadOnlyList<EffectModifier> Modifiers { get; init; } = [];

    /// <summary>
    /// The key of its amounts in <see cref="CharacterStats.Modifiers"/>: <see cref="StatusEffects.ModifierPrefix"/> and the id.
    /// </summary>
    public string ModifierKey => StatusEffects.ModifierPrefix + Id;

    /// <summary>
    /// Puts the effect on character <paramref name="who"/> for <paramref name="hours"/> game hours (positive infinity: until removed) with
    /// <paramref name="stacks"/> stacks (counted only by <see cref="EffectStacking.Stack"/>); a character that has it already follows
    /// <see cref="Stacking"/>. From the mod's system outside the parallel phases, as <see cref="CharacterStats.Modifiers"/>. False when the id names
    /// no character in the world with stats, or the effect was not registered. Throws for hours that are not above 0 or stacks below 1.
    /// </summary>
    public bool Apply(World world, CharacterId who, double hours, int stacks = 1)
    {
        if (double.IsNaN(hours) || hours <= 0) throw new ArgumentOutOfRangeException(nameof(hours), hours, "An effect lasts more than 0 hours.");
        ArgumentOutOfRangeException.ThrowIfLessThan(stacks, 1);
        if (!StatusEffects.IsRegistered(this) || StatsOf(world, who) is not { } stats) return false;

        stats.Effects.Apply(this, StatusEffects.Ticks(world, hours), stacks, stats);
        return true;
    }

    /// <summary>
    /// Takes the effect, every stack, off character <paramref name="who"/> and its stat amounts with it; false when it did not have it. From the
    /// mod's system outside the parallel phases.
    /// </summary>
    public bool Remove(World world, CharacterId who) => StatsOf(world, who) is { EffectsOrNull: { } effects } stats && effects.Remove(Id, stats);

    /// <summary>
    /// The effect on <paramref name="stats"/>' character; null when it does not have it.
    /// </summary>
    public ActiveEffect? On(CharacterStats stats) => stats.EffectsOrNull?.Get(Id);

    /// <summary>
    /// The stats of the character <paramref name="who"/> names in the world; null when none.
    /// </summary>
    static CharacterStats? StatsOf(World world, CharacterId who) =>
        world.Characters.TryResolveNext(who, out int slot) ? world.Characters.Cold(slot)?.Stats : null;
}

/// <summary>
/// The status effects code mods added, in registration order (docs/modding.md "Status effects"), and the tick step that ends them. Filled at
/// startup before the first world is built and never changed while one runs; empty without mods, so the step does nothing and nothing is made.
/// </summary>
public static class StatusEffects
{
    /// <summary>
    /// The start of every effect's key in <see cref="CharacterStats.Modifiers"/>.
    /// </summary>
    public const string ModifierPrefix = "effect:";

    /// <summary>
    /// The effects in registration order.
    /// </summary>
    static readonly List<StatusEffect> Effects = [];

    /// <summary>
    /// The effects by id.
    /// </summary>
    static readonly Dictionary<string, StatusEffect> ById = new(StringComparer.Ordinal);

    /// <summary>
    /// The effects in registration order.
    /// </summary>
    public static IReadOnlyList<StatusEffect> All => Effects;

    /// <summary>
    /// The registered effect <paramref name="id"/>; null when no running mod added it.
    /// </summary>
    public static StatusEffect? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>
    /// Whether <paramref name="effect"/> itself is registered.
    /// </summary>
    internal static bool IsRegistered(StatusEffect effect) => ById.TryGetValue(effect.Id, out var e) && ReferenceEquals(e, effect);

    /// <summary>
    /// Why <paramref name="effect"/> cannot be added after <paramref name="pending"/> more of the registering mod: a bad id or name, an id another
    /// effect has (case-insensitive), stacks below 1, or a modifier on a stat without a stored value or not finite; null when it can.
    /// </summary>
    public static string? Refusal(StatusEffect effect, IEnumerable<StatusEffect> pending)
    {
        if (effect.Id.Length is 0 or > 48 || !effect.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            return $"Status effect id '{effect.Id}' must be 1 to 48 letters, digits, '_', '.' or '-'.";
        if (string.IsNullOrWhiteSpace(effect.Name) || effect.Name.Length > 32) return "A status effect's name is 1 to 32 characters.";
        if (Effects.Concat(pending).Any(e => string.Equals(e.Id, effect.Id, StringComparison.OrdinalIgnoreCase)))
            return $"Status effect id '{effect.Id}' is taken.";
        if (effect.MaxStacks < 1) return "A status effect has at least 1 stack.";
        if (!Enum.IsDefined(effect.Stacking) || !Enum.IsDefined(effect.Tone)) return $"Status effect '{effect.Id}' has an unknown stacking or tone.";
        if (effect.Modifiers is null || effect.Description is null) return $"Status effect '{effect.Id}' has a null list or description.";
        foreach (var m in effect.Modifiers)
        {
            if (StatsInfo.StorageOf(m.Stat) == StatsEnumerated.None) return $"Status effect '{effect.Id}': {m.Stat} has no stored value to modify.";
            if (!float.IsFinite(m.Amount)) return $"Status effect '{effect.Id}': a modifier's amount must be finite.";
        }

        return null;
    }

    /// <summary>
    /// Adds <paramref name="effect"/> after the others; throws when <see cref="Refusal"/> refuses it.
    /// </summary>
    public static void Add(StatusEffect effect)
    {
        if (Refusal(effect, []) is { } refusal) throw new ArgumentException(refusal, nameof(effect));

        Effects.Add(effect);
        ById[effect.Id] = effect;
    }

    /// <summary>
    /// Removes every effect (tests).
    /// </summary>
    internal static void Clear()
    {
        Effects.Clear();
        ById.Clear();
    }

    /// <summary>
    /// The ticks of <paramref name="hours"/> game hours, at least 1, rounded up; -1 for positive infinity.
    /// </summary>
    internal static long Ticks(World world, double hours)
    {
        if (double.IsPositiveInfinity(hours)) return -1;

        double ticks = Math.Ceiling(hours * World.SecondsPerGameHour / world.TickSeconds);
        return ticks >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)ticks);
    }

    /// <summary>
    /// One tick of every character's effects, once a tick after the slow world (a serial point): each registered effect with a time loses a tick
    /// and ends, its stat amounts with it, when none is left; in slot order, each character on its own. Nothing without registered effects.
    /// </summary>
    internal static void Step(World world)
    {
        if (Effects.Count == 0) return;

        var table = world.Characters;
        for (int slot = 0; slot < table.HighWater; slot++)
            if (table.Cold(slot)?.Stats is { EffectsOrNull.Count: > 0 } stats) stats.Effects.Tick(stats);
    }

    /// <summary>
    /// <paramref name="stats"/>' registered effects for the HUD, in registration order; empty when none.
    /// </summary>
    public static IReadOnlyList<EffectView> Shown(CharacterStats stats, float tickSeconds)
    {
        if (Effects.Count == 0 || stats.EffectsOrNull is not { Count: > 0 } active) return [];

        var shown = new List<EffectView>();
        foreach (var effect in Effects)
        {
            if (active.Get(effect.Id) is not { } a) continue;

            double hours = a.Forever ? double.PositiveInfinity : a.TicksLeft * (double)tickSeconds / World.SecondsPerGameHour;
            shown.Add(new EffectView(effect, hours, a.Stacks));
        }

        return shown.Count == 0 ? [] : shown.AsReadOnly();
    }
}

/// <summary>
/// One character's status effects by id, ordinal order (docs/modding.md "Status effects"). Holds ids no running mod registers too (a save made with
/// a mod that is not loaded now): those keep their time and stacks, are not shown, add no stat amounts, and are saved again unchanged.
/// </summary>
public sealed class ActiveEffects
{
    /// <summary>
    /// The STATS key prefix of an effect's ticks left (-1: until removed): <c>meitou effect &lt;id&gt;</c>.
    /// </summary>
    public const string SaveKeyPrefix = "meitou effect ";

    /// <summary>
    /// The STATS key prefix of an effect's stacks, written only above 1: <c>meitou effect stacks &lt;id&gt;</c> (an id has no space, so it never
    /// reads as an effect named "stacks ...").
    /// </summary>
    public const string StacksKeyPrefix = "meitou effect stacks ";

    /// <summary>
    /// The effects by id.
    /// </summary>
    readonly SortedList<string, ActiveEffect> _effects = new(StringComparer.Ordinal);

    /// <summary>
    /// The number of effects held.
    /// </summary>
    public int Count => _effects.Count;

    /// <summary>
    /// Every effect held, by id.
    /// </summary>
    public IEnumerable<ActiveEffect> All => _effects.Values;

    /// <summary>
    /// The effect <paramref name="id"/>; null when it is not held.
    /// </summary>
    public ActiveEffect? Get(string id) => _effects.TryGetValue(id, out var a) ? a : null;

    /// <summary>
    /// Puts <paramref name="effect"/> on for <paramref name="ticks"/> (-1: until removed) by its stacking rule, and its stat amounts on
    /// <paramref name="stats"/>.
    /// </summary>
    internal void Apply(StatusEffect effect, long ticks, int stacks, CharacterStats stats)
    {
        var next = _effects.TryGetValue(effect.Id, out var had) ? Again(effect, had, ticks, stacks) : First(effect, ticks, stacks);
        _effects[effect.Id] = next;
        SetModifiers(effect, next.Stacks, stats);
    }

    /// <summary>
    /// Takes effect <paramref name="id"/> off, and its stat amounts off <paramref name="stats"/>; false when it was not held.
    /// </summary>
    internal bool Remove(string id, CharacterStats stats)
    {
        if (!_effects.Remove(id)) return false;

        stats.ModifiersOrNull?.RemoveAll(StatusEffects.ModifierPrefix + id);
        return true;
    }

    /// <summary>
    /// One tick: every registered effect with a time loses a tick; those left with none end (in id order).
    /// </summary>
    internal void Tick(CharacterStats stats)
    {
        List<string>? ended = null;
        for (int k = 0; k < _effects.Count; k++)
        {
            var a = _effects.GetValueAtIndex(k);
            if (a.Forever || StatusEffects.Find(a.Id) is null) continue;

            var next = a with { TicksLeft = a.TicksLeft - 1 };
            _effects.SetValueAtIndex(k, next);
            if (next.TicksLeft <= 0) (ended ??= []).Add(a.Id);
        }

        if (ended is null) return;
        foreach (string id in ended) Remove(id, stats);
    }

    /// <summary>
    /// The effect as first put on: one stack, or <paramref name="stacks"/> up to the most for a stacking effect.
    /// </summary>
    static ActiveEffect First(StatusEffect effect, long ticks, int stacks) =>
        new(effect.Id, ticks, effect.Stacking == EffectStacking.Stack ? Math.Min(stacks, effect.MaxStacks) : 1);

    /// <summary>
    /// The effect put on again over <paramref name="had"/>, by its <see cref="StatusEffect.Stacking"/>.
    /// </summary>
    static ActiveEffect Again(StatusEffect effect, ActiveEffect had, long ticks, int stacks) => effect.Stacking switch
    {
        EffectStacking.Extend => had with { TicksLeft = had.Forever || ticks < 0 ? -1 : Sum(had.TicksLeft, ticks), Stacks = 1 },
        EffectStacking.Stack => had with { TicksLeft = ticks, Stacks = (int)Math.Min((long)had.Stacks + stacks, effect.MaxStacks) },
        _ => had with { TicksLeft = ticks, Stacks = 1 },
    };

    /// <summary>
    /// Two tick counts added, held at <see cref="long.MaxValue"/>.
    /// </summary>
    static long Sum(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    /// <summary>
    /// Sets <paramref name="effect"/>'s stat amounts for <paramref name="stacks"/> stacks on <paramref name="stats"/>.
    /// </summary>
    static void SetModifiers(StatusEffect effect, int stacks, CharacterStats stats)
    {
        foreach (var m in effect.Modifiers) stats.Modifiers.Set(effect.ModifierKey, m.Stat, m.Amount * stacks);
    }

    /// <summary>
    /// Writes the effects into a STATS record's floats: the ticks left under <see cref="SaveKeyPrefix"/> (whole ticks; a float holds them exactly up
    /// to 16,777,216, about 5,100 game hours), the stacks above 1 under <see cref="StacksKeyPrefix"/>.
    /// </summary>
    internal void WriteSave(IDictionary<string, float> floats)
    {
        foreach (var (id, a) in _effects)
        {
            floats[SaveKeyPrefix + id] = a.Forever ? -1 : a.TicksLeft;
            if (a.Stacks > 1) floats[StacksKeyPrefix + id] = a.Stacks;
        }
    }

    /// <summary>
    /// The effects saved in a STATS record's floats, with the registered ones' stat amounts set on <paramref name="stats"/>; null when it has none.
    /// A time of 0 or less but -1 reads as one tick left, stacks below 1 as 1.
    /// </summary>
    internal static ActiveEffects? ReadSave(IReadOnlyDictionary<string, float> floats, CharacterStats stats)
    {
        ActiveEffects? effects = null;
        foreach (var (key, value) in floats)
        {
            if (!key.StartsWith(SaveKeyPrefix, StringComparison.Ordinal) || key.StartsWith(StacksKeyPrefix, StringComparison.Ordinal)) continue;

            string id = key[SaveKeyPrefix.Length..];
            if (id.Length == 0) continue;

            long ticks = value == -1 ? -1 : !float.IsFinite(value) || value < 1 ? 1 : (long)value;
            int stacks = floats.TryGetValue(StacksKeyPrefix + id, out float s) && float.IsFinite(s) && s >= 1 ? (int)Math.Min(s, int.MaxValue) : 1;
            (effects ??= new())._effects[id] = new ActiveEffect(id, ticks, stacks);
        }

        effects?.SetAllModifiers(stats);
        return effects;
    }

    /// <summary>
    /// Sets every registered effect's stat amounts on <paramref name="stats"/> (after a load).
    /// </summary>
    void SetAllModifiers(CharacterStats stats)
    {
        foreach (var (id, a) in _effects)
            if (StatusEffects.Find(id) is { } effect) SetModifiers(effect, a.Stacks, stats);
    }

    /// <summary>
    /// Adds the effects to the state hash, in id order.
    /// </summary>
    internal void AddTo(ref StateHasher h)
    {
        h.Add(_effects.Count);
        foreach (var (id, a) in _effects)
        {
            h.Add(Rng.StableHash(id));
            h.Add(a.TicksLeft);
            h.Add(a.Stacks);
        }
    }
}
