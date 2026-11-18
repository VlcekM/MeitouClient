using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// A registered status effect with the copy of its stat amounts the game uses.
/// </summary>
/// <param name="Effect">
/// The effect the mod registered.
/// </param>
/// <param name="Modifiers">
/// Its <see cref="StatusEffect.Modifiers"/> as they were at registration.
/// </param>
sealed record StatusEffectEntry(StatusEffect Effect, StatusEffectModifier[] Modifiers);

/// <summary>
/// The status effects code mods added, in registration order (docs/modding.md "Status effects"), the tick step that ends them and what the HUD
/// shows. Filled at startup before the first world is built and never changed while one runs; empty without mods, so the step does nothing and
/// nothing is made.
/// </summary>
public static class StatusEffects
{
    #region Registry

    /// <summary>
    /// The start of every effect's key in <see cref="CharacterStats.Modifiers"/>.
    /// </summary>
    public const string ModifierPrefix = "effect:";

    /// <summary>
    /// The effects in registration order. Replaced whole on each registration (a load on another thread, such as the title screen's, reads a
    /// complete list).
    /// </summary>
    static volatile StatusEffect[] s_all = [];

    /// <summary>
    /// The effects by id, replaced whole on each registration as <see cref="s_all"/>.
    /// </summary>
    static volatile Dictionary<string, StatusEffectEntry> s_byId = new(StringComparer.Ordinal);

    /// <summary>
    /// The effects in registration order.
    /// </summary>
    public static IReadOnlyList<StatusEffect> All => s_all;

    /// <summary>
    /// The registered effect <paramref name="id"/>; null when no running mod added it.
    /// </summary>
    public static StatusEffect? Find(string id) => s_byId.GetValueOrDefault(id)?.Effect;

    /// <summary>
    /// Why <paramref name="effect"/> cannot be added after <paramref name="pending"/> more of the registering mod: a bad id or name, an id another
    /// effect has (case-insensitive), stacks below 1, an unknown tone or stacking, a null list or description, or a modifier on a stat without a
    /// stored value, on a storage another of its modifiers names, or whose amount times the most stacks is not finite; null when it can.
    /// </summary>
    public static string? Refusal(StatusEffect effect, IEnumerable<StatusEffect> pending)
    {
        if (effect.Id is null || effect.Id.Length is 0 or > 48 || !effect.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            return $"Status effect id '{effect.Id}' must be 1 to 48 letters, digits, '_', '.' or '-'.";
        if (string.IsNullOrWhiteSpace(effect.Name) || effect.Name.Length > 32) return "A status effect's name is 1 to 32 characters.";
        if (s_all.Concat(pending).Any(e => string.Equals(e.Id, effect.Id, StringComparison.OrdinalIgnoreCase)))
            return $"Status effect id '{effect.Id}' is taken.";
        if (effect.MaxStacks < 1) return "A status effect has at least 1 stack.";
        if (!Enum.IsDefined(effect.Stacking) || !Enum.IsDefined(effect.Tone)) return $"Status effect '{effect.Id}' has an unknown stacking or tone.";
        if (effect.Modifiers is null || effect.Description is null) return $"Status effect '{effect.Id}' has a null list or description.";

        return ModifierRefusal(effect);
    }

    /// <summary>
    /// Adds <paramref name="effect"/> after the others, with a copy of its stat amounts taken now; throws when <see cref="Refusal"/> refuses it.
    /// </summary>
    public static void Add(StatusEffect effect) => Add(effect, [.. effect.Modifiers]);

    /// <summary>
    /// Adds <paramref name="effect"/> after the others with <paramref name="modifiers"/>, the copy of its stat amounts taken when the mod asked
    /// (<c>ModContext.AddStatusEffect</c>), so a later change of the mod's list does nothing; throws when <see cref="Refusal"/> refuses it with them.
    /// </summary>
    public static void Add(StatusEffect effect, StatusEffectModifier[] modifiers)
    {
        if (Refusal(effect with { Modifiers = modifiers }, []) is { } refusal) throw new ArgumentException(refusal, nameof(effect));

        var byId = new Dictionary<string, StatusEffectEntry>(s_byId, StringComparer.Ordinal)
        {
            [effect.Id] = new StatusEffectEntry(effect, modifiers),
        };
        s_byId = byId;
        s_all = [.. s_all, effect];
    }

    /// <summary>
    /// Removes every effect (tests).
    /// </summary>
    internal static void Clear()
    {
        s_byId = new(StringComparer.Ordinal);
        s_all = [];
    }

    /// <summary>
    /// The entry of <paramref name="effect"/> itself when it is registered; null otherwise.
    /// </summary>
    internal static StatusEffectEntry? EntryOf(StatusEffect effect) =>
        s_byId.TryGetValue(effect.Id, out var e) && ReferenceEquals(e.Effect, effect) ? e : null;

    /// <summary>
    /// The entry registered under <paramref name="id"/>; null when none is.
    /// </summary>
    internal static StatusEffectEntry? EntryOf(string id) => s_byId.GetValueOrDefault(id);

    /// <summary>
    /// Why <paramref name="effect"/>'s modifiers are refused: a stat without storage, two on one storage, or an amount not finite at the most
    /// stacks; null when none is.
    /// </summary>
    static string? ModifierRefusal(StatusEffect effect)
    {
        var storages = new Dictionary<StatsEnumerated, StatsEnumerated>();
        foreach (var m in effect.Modifiers)
        {
            var storage = StatsInfo.StorageOf(m.Stat);
            if (storage == StatsEnumerated.None) return $"Status effect '{effect.Id}': {m.Stat} has no stored value to modify.";
            if (!storages.TryAdd(storage, m.Stat)) return SharedStorage(effect.Id, storages[storage], m.Stat);
            if (!float.IsFinite(m.Amount) || !float.IsFinite(m.Amount * effect.MaxStacks))
                return $"Status effect '{effect.Id}': a modifier's amount, and that times its most stacks, must be finite.";
        }

        return null;
    }

    /// <summary>
    /// The refusal of two modifiers on one stored value: the same stat twice, or two stats that share one (Medic, HiveMedic and Vet).
    /// </summary>
    static string SharedStorage(string id, StatsEnumerated first, StatsEnumerated second) => first == second
        ? $"Status effect '{id}': two modifiers change {first}; give one modifier with the sum."
        : $"Status effect '{id}': {first} and {second} share one stored value; give one modifier with the sum.";

    #endregion

    #region Characters

    /// <summary>
    /// Takes effect <paramref name="id"/> off <paramref name="stats"/>' character with its stat amounts, registered or not (an effect of a mod that is
    /// gone, or an id a mod renamed); false when the character did not have it. From a mod's system outside the parallel phases.
    /// </summary>
    public static bool Forget(CharacterStats stats, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return stats.EffectsOrNull is { } effects && effects.Remove(id, stats);
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
        if (s_all.Length == 0) return;

        var table = world.Characters;
        for (int slot = 0; slot < table.HighWater; slot++)
            if (table.Cold(slot)?.Stats is { EffectsOrNull.Count: > 0 } stats) stats.Effects.Tick(stats);
    }

    /// <summary>
    /// <paramref name="stats"/>' registered effects for the HUD, in registration order; empty when none.
    /// </summary>
    public static IReadOnlyList<StatusEffectView> Shown(CharacterStats stats, float tickSeconds)
    {
        var all = s_all;
        if (all.Length == 0 || stats.EffectsOrNull is not { Count: > 0 } active) return [];

        var shown = new List<StatusEffectView>();
        foreach (var effect in all)
        {
            if (active.Get(effect.Id) is not { } a) continue;

            double hours = a.Forever ? double.PositiveInfinity : a.TicksLeft * (double)tickSeconds / World.SecondsPerGameHour;
            shown.Add(new StatusEffectView(effect, hours, a.Stacks));
        }

        return shown.Count == 0 ? [] : shown.AsReadOnly();
    }

    #endregion
}
