namespace Meitou.Simulation.Bodies;

/// <summary>
/// One character's status effects by id, ordinal order (docs/modding.md "Status effects"). Holds ids no running mod registers too (a save made with
/// a mod that is not loaded now): those keep their time and stacks, are not shown, add no stat amounts, and are saved again unchanged until
/// <see cref="StatusEffects.Forget"/> drops them.
/// </summary>
public sealed class ActiveStatusEffects
{
    #region Effects

    /// <summary>
    /// The effects by id.
    /// </summary>
    readonly SortedList<string, ActiveStatusEffect> _effects = new(StringComparer.Ordinal);

    /// <summary>
    /// The number of effects held.
    /// </summary>
    public int Count => _effects.Count;

    /// <summary>
    /// Every effect held, by id.
    /// </summary>
    public IEnumerable<ActiveStatusEffect> All => _effects.Values;

    /// <summary>
    /// The effect <paramref name="id"/>; null when it is not held.
    /// </summary>
    public ActiveStatusEffect? Get(string id) => _effects.TryGetValue(id, out var a) ? a : null;

    /// <summary>
    /// Puts <paramref name="entry"/>'s effect on for <paramref name="ticks"/> (-1: until removed) by its stacking rule, and its stat amounts on
    /// <paramref name="stats"/>.
    /// </summary>
    internal void Apply(StatusEffectEntry entry, long ticks, int stacks, CharacterStats stats)
    {
        var effect = entry.Effect;
        var next = _effects.TryGetValue(effect.Id, out var had) ? Again(effect, had, ticks, stacks) : First(effect, ticks, stacks);
        _effects[effect.Id] = next;
        SetModifiers(entry, next.Stacks, stats);
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
            if (a.Forever || StatusEffects.EntryOf(a.Id) is null) continue;

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
    static ActiveStatusEffect First(StatusEffect effect, long ticks, int stacks) => new(effect.Id, ticks, StacksOf(effect, stacks));

    /// <summary>
    /// The effect put on again over <paramref name="had"/>, by its <see cref="StatusEffect.Stacking"/>.
    /// </summary>
    static ActiveStatusEffect Again(StatusEffect effect, ActiveStatusEffect had, long ticks, int stacks) => effect.Stacking switch
    {
        StatusEffectStacking.Extend => had with { TicksLeft = had.Forever || ticks < 0 ? -1 : Sum(had.TicksLeft, ticks), Stacks = 1 },
        StatusEffectStacking.Stack => had with { TicksLeft = ticks, Stacks = StacksOf(effect, (long)had.Stacks + stacks) },
        _ => had with { TicksLeft = ticks, Stacks = 1 },
    };

    /// <summary>
    /// The stacks <paramref name="effect"/> may hold of <paramref name="stacks"/>: up to its most when it stacks, else 1.
    /// </summary>
    static int StacksOf(StatusEffect effect, long stacks) =>
        effect.Stacking == StatusEffectStacking.Stack ? (int)Math.Clamp(stacks, 1, effect.MaxStacks) : 1;

    /// <summary>
    /// Two tick counts added, held at <see cref="long.MaxValue"/>.
    /// </summary>
    static long Sum(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    /// <summary>
    /// Sets <paramref name="entry"/>'s stat amounts for <paramref name="stacks"/> stacks on <paramref name="stats"/>.
    /// </summary>
    static void SetModifiers(StatusEffectEntry entry, int stacks, CharacterStats stats)
    {
        foreach (var m in entry.Modifiers) stats.Modifiers.Set(entry.Effect.ModifierKey, m.Stat, m.Amount * stacks);
    }

    #endregion

    #region Saving

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
    /// Writes the effects into a STATS record's floats, first removing every key the record holds under <see cref="SaveKeyPrefix"/> (a record copied
    /// from a loaded save keeps the keys it was read with): the ticks left under <see cref="SaveKeyPrefix"/> (whole ticks; a float holds them exactly
    /// up to 16,777,216, about 5,100 game hours), the stacks above 1 under <see cref="StacksKeyPrefix"/>.
    /// </summary>
    internal void WriteSave(IDictionary<string, float> floats)
    {
        ClearSave(floats);
        foreach (var (id, a) in _effects)
        {
            floats[SaveKeyPrefix + id] = a.Forever ? -1 : a.TicksLeft;
            if (a.Stacks > 1) floats[StacksKeyPrefix + id] = a.Stacks;
        }
    }

    /// <summary>
    /// The effects saved in a STATS record's floats, with the registered ones' stacks held to their rule and their stat amounts set on
    /// <paramref name="stats"/>; null when the record has no key under <see cref="SaveKeyPrefix"/> (any such key makes a store, so the next save
    /// clears it). A time of 0 or less but -1 reads as one tick left, stacks below 1 as 1.
    /// </summary>
    internal static ActiveStatusEffects? ReadSave(IReadOnlyDictionary<string, float> floats, CharacterStats stats)
    {
        ActiveStatusEffects? effects = null;
        foreach (var (key, value) in floats)
        {
            if (!key.StartsWith(SaveKeyPrefix, StringComparison.Ordinal)) continue;

            effects ??= new();
            if (key.StartsWith(StacksKeyPrefix, StringComparison.Ordinal) || key.Length == SaveKeyPrefix.Length) continue;

            string id = key[SaveKeyPrefix.Length..];
            effects._effects[id] = new ActiveStatusEffect(id, SavedTicks(value), SavedStacks(floats, id));
        }

        effects?.Settle(stats);
        return effects;
    }

    /// <summary>
    /// The ticks a saved time stands for: -1 kept, a time not finite or below 1 as 1, else whole ticks.
    /// </summary>
    static long SavedTicks(float value)
    {
        if (value == -1) return -1;

        return float.IsFinite(value) && value >= 1 ? (long)value : 1;
    }

    /// <summary>
    /// The saved stacks of effect <paramref name="id"/>: 1 without the key or for a value not finite or below 1.
    /// </summary>
    static int SavedStacks(IReadOnlyDictionary<string, float> floats, string id)
    {
        if (!floats.TryGetValue(StacksKeyPrefix + id, out float s) || !float.IsFinite(s) || s < 1) return 1;

        return (int)Math.Min(s, int.MaxValue);
    }

    /// <summary>
    /// After a load: holds each registered effect's stacks to its rule and sets its stat amounts on <paramref name="stats"/>.
    /// </summary>
    void Settle(CharacterStats stats)
    {
        for (int k = 0; k < _effects.Count; k++)
        {
            var a = _effects.GetValueAtIndex(k);
            if (StatusEffects.EntryOf(a.Id) is not { } entry) continue;

            var held = a with { Stacks = StacksOf(entry.Effect, a.Stacks) };
            _effects.SetValueAtIndex(k, held);
            SetModifiers(entry, held.Stacks, stats);
        }
    }

    /// <summary>
    /// Removes every key under <see cref="SaveKeyPrefix"/> from <paramref name="floats"/>, without allocating when there is none.
    /// </summary>
    static void ClearSave(IDictionary<string, float> floats)
    {
        List<string>? old = null;
        foreach (string key in floats.Keys)
            if (key.StartsWith(SaveKeyPrefix, StringComparison.Ordinal)) (old ??= []).Add(key);

        if (old is null) return;
        foreach (string key in old) floats.Remove(key);
    }

    #endregion

    #region State hash

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

    #endregion
}
