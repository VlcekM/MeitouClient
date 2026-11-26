namespace Meitou.Simulation;

/// <summary>
/// The code mods' tags on characters (docs/modding.md "Character tags"): one free string per mod id on a character, so several mods can mark one
/// character without overwriting each other (a rival, a war leader, a named outlaw and the mod that named it). Kept by the character's lasting id
/// (<see cref="LastingIds"/>), which <see cref="Set"/> gives when the character has none, so a tag goes wherever the id goes: across saves and
/// loads, squad moves, renames, and an NPC's platoon leaving the world and coming back; a new character in a reused slot has no id, so no tags.
/// Saved with the ids (<see cref="Saving.SaveCapture"/>, <see cref="Data.Save.SaveLastingIds"/>). Until a tag is set or loaded nothing here is hashed.
/// </summary>
public sealed class CharacterTags(World world)
{
    #region Tags

    /// <summary>
    /// The error of a set or remove outside the serial phases.
    /// </summary>
    const string Parallel = "CharacterTags.Set runs in the serial phases only; read the tags in a parallel one";

    /// <summary>
    /// The error of a set or remove during a save.
    /// </summary>
    const string DuringSave = "CharacterTags.Set cannot run during a save: set tags as data is first stored";

    /// <summary>
    /// The tags by lasting id, each by mod id in ordinal order; an id with no tags has no entry.
    /// </summary>
    readonly Dictionary<LastingId, SortedDictionary<string, string>> _byId = [];

    /// <summary>
    /// No tags, handed out for an id that has none.
    /// </summary>
    static readonly IReadOnlyDictionary<string, string> NoTags = new SortedDictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Sets mod <paramref name="mod"/>'s tag on <paramref name="character"/> to <paramref name="tag"/> (null or empty removes it), giving the character
    /// a lasting id when it has none; false when the character is not in the world. Another mod's tag on the character is untouched. Call it where
    /// <see cref="LastingIds.GetOrGive"/> may run: the thread that ticks the world, between ticks or in a serial phase, not during a save.
    /// </summary>
    public bool Set(CharacterId character, string mod, string? tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(mod);
        GuardWrite();

        if (string.IsNullOrEmpty(tag)) return Remove(character, mod);

        var id = world.LastingIds.GetOrGive(character);
        if (id.IsNone) return false;

        if (!_byId.TryGetValue(id, out var tags)) _byId[id] = tags = new SortedDictionary<string, string>(StringComparer.Ordinal);
        tags[mod] = tag;
        return true;
    }

    /// <summary>
    /// Mod <paramref name="mod"/>'s tag on <paramref name="character"/>; null when it set none, or the character is not in the world. Any phase.
    /// </summary>
    public string? Get(CharacterId character, string mod) => Get(IdOf(character), mod);

    /// <summary>
    /// Mod <paramref name="mod"/>'s tag on the character with lasting id <paramref name="id"/>, also while that character is out of the world; null
    /// when it set none. Any phase.
    /// </summary>
    public string? Get(LastingId id, string mod) =>
        _byId.TryGetValue(id, out var tags) && tags.TryGetValue(mod, out string? tag) ? tag : null;

    /// <summary>
    /// Every mod's tag on <paramref name="character"/>, by mod id in ordinal order (empty: none). Any phase; do not keep it across a set.
    /// </summary>
    public IReadOnlyDictionary<string, string> Of(CharacterId character) => Of(IdOf(character));

    /// <summary>
    /// Every mod's tag on the character with lasting id <paramref name="id"/>, by mod id in ordinal order (empty: none). Any phase.
    /// </summary>
    public IReadOnlyDictionary<string, string> Of(LastingId id) => _byId.TryGetValue(id, out var tags) ? tags : NoTags;

    /// <summary>
    /// The character in the world on which mod <paramref name="mod"/> set <paramref name="tag"/>, the one with the lowest lasting id when several
    /// are; <see cref="CharacterId.None"/> when none in the world has it (its squad may be off screen: <see cref="FindIds"/> also lists those). It
    /// looks through every tagged character: call it at a load or an event, not per character per tick.
    /// </summary>
    public CharacterId Find(string mod, string tag)
    {
        foreach (var id in FindIds(mod, tag))
            if (world.LastingIds.Find(id) is { IsNone: false } found) return found;

        return CharacterId.None;
    }

    /// <summary>
    /// The lasting ids of every character, in the world or out of it, on which mod <paramref name="mod"/> set <paramref name="tag"/>, in id order.
    /// </summary>
    public List<LastingId> FindIds(string mod, string tag)
    {
        var found = new List<LastingId>();
        foreach (var (id, tags) in _byId)
            if (tags.TryGetValue(mod, out string? t) && string.Equals(t, tag, StringComparison.Ordinal)) found.Add(id);

        found.Sort((a, b) => a.Value.CompareTo(b.Value));
        return found;
    }

    /// <summary>
    /// Whether any tag was set or loaded: only then are tags hashed.
    /// </summary>
    internal bool Any => _byId.Count > 0;

    /// <summary>
    /// The character's lasting id, without giving one.
    /// </summary>
    LastingId IdOf(CharacterId character)
    {
        var table = world.Characters;
        return table.TryResolveNext(character, out int slot) && table.Cold(slot) is { } cold ? cold.LastingId : LastingId.None;
    }

    /// <summary>
    /// Removes mod <paramref name="mod"/>'s tag from <paramref name="character"/>; false when the character is not in the world.
    /// </summary>
    bool Remove(CharacterId character, string mod)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(character, out int slot) || table.Cold(slot) is not { } cold) return false;
        if (cold.LastingId.IsNone || !_byId.TryGetValue(cold.LastingId, out var tags)) return true;

        tags.Remove(mod);
        if (tags.Count == 0) _byId.Remove(cold.LastingId);
        return true;
    }

    /// <summary>
    /// Throws outside the serial phases and during a save, so tags change only where lasting ids may be given.
    /// </summary>
    void GuardWrite()
    {
        var table = world.Characters;
        if (table.Phase == TablePhase.Parallel) throw new InvalidOperationException(Parallel);
        if (table.SaveHold) throw new InvalidOperationException(DuringSave);
    }

    #endregion

    #region Saves

    /// <summary>
    /// Takes the tags a loaded save's entry gives the character with lasting id <paramref name="id"/> (<see cref="LastingIds.Load"/>); empty mod ids
    /// and tags are skipped.
    /// </summary>
    internal void Load(LastingId id, IReadOnlyDictionary<string, string>? tags)
    {
        if (id.IsNone || tags is null) return;

        foreach (var (mod, tag) in tags)
        {
            if (mod.Length == 0 || tag.Length == 0) continue;

            if (!_byId.TryGetValue(id, out var kept)) _byId[id] = kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
            kept[mod] = tag;
        }
    }

    /// <summary>
    /// The tags to write with <paramref name="id"/>'s entry in the ids file; null when it has none.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? ForSave(LastingId id) => _byId.TryGetValue(id, out var tags) ? tags : null;

    /// <summary>
    /// Adds the tags of the living characters in the table to the state hash, in slot order then mod id, as <see cref="LastingIds.Hash"/> adds their
    /// ids: a save writes every one of them, so a save and load hash alike (tags of characters out of the world are not hashed).
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        var table = world.Characters;
        for (int slot = 0; slot < table.HighWater; slot++)
        {
            if (!table.Previous[slot].Alive || table.Cold(slot) is not { LastingId.IsNone: false } cold) continue;
            if (!_byId.TryGetValue(cold.LastingId, out var tags)) continue;

            hasher.Add(slot);
            foreach (var (mod, tag) in tags)
            {
                hasher.Add(Rng.StableHash(mod));
                hasher.Add(Rng.StableHash(tag));
            }
        }
    }

    #endregion
}
