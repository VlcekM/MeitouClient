using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// One temporary amount a code mod adds to a stat (docs/modding.md "Stat modifiers"): <paramref name="Key"/> names the source (a drug, a perk, a
/// sickness), <paramref name="Stat"/> is the stat's storage.
/// </summary>
public readonly record struct StatModifier(string Key, StatsEnumerated Stat, float Amount);

/// <summary>
/// A character's stat modifiers (Meitou, for code mods; docs/modding.md "Stat modifiers"): keyed amounts added to the stored stats on every read of
/// <see cref="CharacterStats"/>, before every multiplier. Training, saving and the debug stat boost keep the stored values, so a modifier is never
/// trained or saved; it is state (hashed) but not persisted, so the mod sets it again after a load. Medic, HiveMedic and Vet share one storage, as
/// their values do. Change it outside the parallel phases (docs/simulation.md "Threading"): other characters' work reads the stats.
/// </summary>
public sealed class StatModifiers
{
    /// <summary>
    /// The modifiers, ordered by key (ordinal) then stat number, so sums and the state hash do not depend on the order they were set in.
    /// </summary>
    readonly List<StatModifier> _list = [];

    /// <summary>
    /// The sum of the amounts by storage stat number.
    /// </summary>
    readonly float[] _sums = new float[StatsInfo.Count];

    /// <summary>
    /// Every modifier, ordered by key then stat.
    /// </summary>
    public IReadOnlyList<StatModifier> All => _list;

    /// <summary>
    /// What the modifiers add to <paramref name="stat"/> (its storage's sum; 0 for a number without storage).
    /// </summary>
    public float this[StatsEnumerated stat] => StatsInfo.StorageOf(stat) is var s && s != StatsEnumerated.None ? _sums[(int)s] : 0;

    /// <summary>
    /// Sets the amount <paramref name="key"/> adds to <paramref name="stat"/>, replacing that key's earlier amount for the stat. Throws for a stat
    /// without storage, an empty key or an amount that is not finite.
    /// </summary>
    public void Set(string key, StatsEnumerated stat, float amount)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!float.IsFinite(amount)) throw new ArgumentOutOfRangeException(nameof(amount), amount, "A stat modifier must be finite.");

        var storage = StatsInfo.StorageOf(stat);
        if (storage == StatsEnumerated.None) throw new ArgumentException($"{stat} has no stored value to modify.", nameof(stat));

        var modifier = new StatModifier(key, storage, amount);
        int at = Find(key, storage);
        if (at >= 0) _list[at] = modifier;
        else _list.Insert(~at, modifier);
        Resum(storage);
    }

    /// <summary>
    /// Removes <paramref name="key"/>'s amount on <paramref name="stat"/>; false when it had none.
    /// </summary>
    public bool Remove(string key, StatsEnumerated stat)
    {
        var storage = StatsInfo.StorageOf(stat);
        int at = Find(key, storage);
        if (at < 0) return false;

        _list.RemoveAt(at);
        Resum(storage);
        return true;
    }

    /// <summary>
    /// Removes every amount of <paramref name="key"/>; returns how many there were.
    /// </summary>
    public int RemoveAll(string key)
    {
        int removed = _list.RemoveAll(m => m.Key == key);
        if (removed > 0) ResumAll();
        return removed;
    }

    /// <summary>
    /// Adds the modifiers to the state hash, in their order.
    /// </summary>
    internal void AddTo(ref StateHasher h)
    {
        h.Add(_list.Count);
        foreach (var m in _list)
        {
            h.Add(Rng.StableHash(m.Key));
            h.Add((int)m.Stat);
            h.Add(m.Amount);
        }
    }

    /// <summary>
    /// The index of the (key, storage) modifier, or the complement of where it would go.
    /// </summary>
    int Find(string key, StatsEnumerated storage)
    {
        int lo = 0, hi = _list.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int c = Compare(_list[mid], key, storage);
            if (c == 0) return mid;
            if (c < 0) lo = mid + 1;
            else hi = mid - 1;
        }

        return ~lo;
    }

    /// <summary>
    /// The order of <paramref name="m"/> against (key, storage): key ordinal first, then stat number.
    /// </summary>
    static int Compare(in StatModifier m, string key, StatsEnumerated storage)
    {
        int c = string.CompareOrdinal(m.Key, key);
        return c != 0 ? c : ((int)m.Stat).CompareTo((int)storage);
    }

    /// <summary>
    /// Recomputes one storage's sum, adding in list order.
    /// </summary>
    void Resum(StatsEnumerated storage)
    {
        float sum = 0;
        foreach (var m in _list)
            if (m.Stat == storage) sum += m.Amount;
        _sums[(int)storage] = sum;
    }

    /// <summary>
    /// Recomputes every sum, adding in list order.
    /// </summary>
    void ResumAll()
    {
        Array.Clear(_sums);
        foreach (var m in _list) _sums[(int)m.Stat] += m.Amount;
    }
}
