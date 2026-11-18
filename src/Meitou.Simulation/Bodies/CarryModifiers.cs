namespace Meitou.Simulation.Bodies;

/// <summary>
/// The term of a character's carrying capacity a code mod's modifier changes (docs/modding.md "Carry capacity").
/// </summary>
public enum CarryTerm : byte
{
    /// <summary>
    /// Kilograms added to the capacity after the game's own formula (a backpack frame's +20 kg).
    /// </summary>
    Kilograms,

    /// <summary>
    /// A fraction of the game's own capacity added to it (a race trait's +0.25 carries a quarter more; -0.5 halves it).
    /// </summary>
    Scale,
}

/// <summary>
/// One amount a code mod adds to a character's carrying capacity (docs/modding.md "Carry capacity"): <paramref name="Key"/> names the source (a
/// trait, a piece of gear), <paramref name="Term"/> what it changes.
/// </summary>
public readonly record struct CarryModifier(string Key, CarryTerm Term, float Amount);

/// <summary>
/// A character's carrying capacity modifiers (Meitou, for code mods; docs/modding.md "Carry capacity"): keyed amounts applied to the capacity the
/// game works out from strength (<see cref="Encumbrance.Capacity(Meitou.Data.Gameplay.GameConstants, float, float, CarryModifiers?)"/> and the
/// window's <see cref="Encumbrance.WindowCapacity(Meitou.Data.Gameplay.GameConstants, float, float, CarryModifiers?)"/>): the capacity times
/// (1 + the <see cref="CarryTerm.Scale"/> sum) plus the <see cref="CarryTerm.Kilograms"/> sum, never below 0. Keys are shared by every mod, so a
/// mod prefixes its own with its id. State (hashed) but not saved, so the mod sets it again after a load. Change it outside the parallel phases
/// (docs/simulation.md "Threading"): the body step reads it.
/// </summary>
public sealed class CarryModifiers
{
    /// <summary>
    /// The most the kilogram sum adds or takes away (a sum past it is held at it, so many mods' amounts cannot overflow).
    /// </summary>
    const float MaxKilograms = 1_000_000;

    /// <summary>
    /// The most the scale sum adds or takes away (as <see cref="MaxKilograms"/>).
    /// </summary>
    const float MaxScale = 1_000;

    /// <summary>
    /// The modifiers, ordered by key (ordinal) then term, so sums and the state hash do not depend on the order they were set in.
    /// </summary>
    readonly List<CarryModifier> _list = [];

    /// <summary>
    /// Every modifier, ordered by key then term.
    /// </summary>
    public IReadOnlyList<CarryModifier> All => _list;

    /// <summary>
    /// The kilograms the modifiers add.
    /// </summary>
    public float Kilograms { get; private set; }

    /// <summary>
    /// The fraction of the capacity the modifiers add.
    /// </summary>
    public float Scale { get; private set; }

    /// <summary>
    /// Sets the amount <paramref name="key"/> adds to <paramref name="term"/>, replacing that key's earlier amount for the term. Throws for an empty
    /// key, an unknown term or an amount that is not finite.
    /// </summary>
    public void Set(string key, CarryTerm term, float amount)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!Enum.IsDefined(term)) throw new ArgumentOutOfRangeException(nameof(term), term, "Not a carry term.");
        if (!float.IsFinite(amount)) throw new ArgumentOutOfRangeException(nameof(amount), amount, "A carry modifier must be finite.");

        var modifier = new CarryModifier(key, term, amount);
        int at = Find(key, term);
        if (at >= 0) _list[at] = modifier;
        else _list.Insert(~at, modifier);
        Resum();
    }

    /// <summary>
    /// Removes <paramref name="key"/>'s amount on <paramref name="term"/>; false when it had none.
    /// </summary>
    public bool Remove(string key, CarryTerm term)
    {
        int at = Find(key, term);
        if (at < 0) return false;

        _list.RemoveAt(at);
        Resum();
        return true;
    }

    /// <summary>
    /// Removes every amount of <paramref name="key"/>; returns how many there were.
    /// </summary>
    public int RemoveAll(string key)
    {
        int removed = _list.RemoveAll(m => m.Key == key);
        if (removed > 0) Resum();
        return removed;
    }

    /// <summary>
    /// <paramref name="capacity"/> (the game's own, kg) with the modifiers: times (1 + <see cref="Scale"/>) plus <see cref="Kilograms"/>, at least 0.
    /// </summary>
    internal float Apply(float capacity) => MathF.Max(capacity * (1 + Scale) + Kilograms, 0);

    /// <summary>
    /// Adds the modifiers to the state hash, in their order.
    /// </summary>
    internal void AddTo(ref StateHasher h)
    {
        h.Add(_list.Count);
        foreach (var m in _list)
        {
            h.Add(Rng.StableHash(m.Key));
            h.Add((int)m.Term);
            h.Add(m.Amount);
        }
    }

    /// <summary>
    /// The index of the (key, term) modifier, or the complement of where it would go.
    /// </summary>
    int Find(string key, CarryTerm term)
    {
        int lo = 0, hi = _list.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int c = Compare(_list[mid], key, term);
            if (c == 0) return mid;
            if (c < 0) lo = mid + 1;
            else hi = mid - 1;
        }

        return ~lo;
    }

    /// <summary>
    /// The order of <paramref name="m"/> against (key, term): key ordinal first, then term.
    /// </summary>
    static int Compare(in CarryModifier m, string key, CarryTerm term)
    {
        int c = string.CompareOrdinal(m.Key, key);
        return c != 0 ? c : ((int)m.Term).CompareTo((int)term);
    }

    /// <summary>
    /// Recomputes both sums, adding in list order, each held within its limit (<see cref="MaxKilograms"/>, <see cref="MaxScale"/>).
    /// </summary>
    void Resum()
    {
        float kilograms = 0, scale = 0;
        foreach (var m in _list)
        {
            if (m.Term == CarryTerm.Kilograms) kilograms += m.Amount;
            else scale += m.Amount;
        }

        Kilograms = Math.Clamp(kilograms, -MaxKilograms, MaxKilograms);
        Scale = Math.Clamp(scale, -MaxScale, MaxScale);
    }
}
