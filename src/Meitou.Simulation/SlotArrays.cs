namespace Meitou.Simulation;

/// <summary>
/// The arrays a publish hands out per character slot (a character's jobs, its stuck bolts): the last one is handed out again while the slot's
/// character and the list's items are the same, so an unchanged list costs no copy. Each slot is written by the one fill that owns it.
/// </summary>
sealed class SlotArrays<T>
{
    /// <summary>
    /// Per slot: the generation of the character it was made for and the array handed out last.
    /// </summary>
    (int Generation, T[]? Items)[] _slots = [];

    /// <summary>
    /// Makes room for <paramref name="count"/> slots; called before the fills run.
    /// </summary>
    public void Reserve(int count)
    {
        if (_slots.Length < count) Array.Resize(ref _slots, count);
    }

    /// <summary>
    /// The items of <paramref name="list"/> for slot <paramref name="slot"/>'s character of <paramref name="generation"/>: the last array when it
    /// holds the same items (structs by value, classes by reference, as the copy is shallow), else a new copy.
    /// </summary>
    public IReadOnlyList<T> Of(int slot, int generation, List<T>? list)
    {
        if (list is not { Count: > 0 }) return [];

        ref var kept = ref _slots[slot];
        if (kept.Items is { } items && kept.Generation == generation && Same(items, list)) return items;

        var copy = list.ToArray();
        kept = (generation, copy);
        return copy;
    }

    /// <summary>
    /// Whether <paramref name="items"/> holds the items of <paramref name="list"/> in order.
    /// </summary>
    static bool Same(T[] items, List<T> list)
    {
        if (items.Length != list.Count) return false;

        for (int i = 0; i < items.Length; i++)
        {
            if (!Equal(items[i], list[i])) return false;
        }

        return true;
    }

    /// <summary>
    /// Struct items by value, class items by reference.
    /// </summary>
    static bool Equal(T a, T b) => default(T) is null ? ReferenceEquals(a, b) : EqualityComparer<T>.Default.Equals(a, b);
}
