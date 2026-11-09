using Meitou.Data;
using Meitou.Data.Fcs;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// A character's personality tag (<c>PersonalityTags</c>, docs/game/dialogue.md "Enums"), which DC_PERSONALITY_TAG tests. The original keeps one
/// index per character (status <c>+0xd8</c>), rolled once when the character is set up (<c>FUN_14062b210</c>, <c>FUN_140624b70</c>,
/// docs/game/dialogue.md "Personality roll"): from the first <c>personality</c> reference of the CHARACTER, else of its FACTION, else the <c>blank
/// personality</c> record. One of the record's <c>always</c> tags when it lists any, else a weighted pick over its <c>common</c> (3) and <c>rare</c>
/// (0.2) tags and the six default tags (weight 1) that its <c>never</c> tags do not strike.
/// </summary>
public static class Personality
{
    /// <summary>
    /// The tag numbers read here (NONE, HONORABLE, TRAITOROUS, NORMAL) and the tag count; the valid tags are 1 to 14.
    /// </summary>
    public const int None = 0, Honorable = 1, Traitorous = 2, Normal = 13, Count = 15;

    /// <summary>
    /// The tags every character may roll when its record does not strike them (HONORABLE, TRAITOROUS, SMART, DUMB, BRAVE, FEARFUL), weight 1 each.
    /// </summary>
    static readonly int[] Defaults = [1, 2, 5, 6, 9, 10];

    /// <summary>
    /// The character's tag: its saved or earlier one, else rolled now (and kept on <paramref name="cold"/>); <paramref name="factionId"/> is the
    /// record id of its faction.
    /// </summary>
    public static int Of(World world, GameDatabase db, CharacterId id, CharacterCold cold, string? factionId)
    {
        if (cold.Personality > 0) return cold.Personality;

        cold.Personality = Roll(db, cold.RecordId, factionId, Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Dialogue, 0x9E75));
        return cold.Personality;
    }

    /// <summary>
    /// The roll for a CHARACTER record of a faction (see the type).
    /// </summary>
    public static int Roll(GameDatabase db, string characterId, string? factionId, ulong roll)
    {
        var record = RecordOf(db, characterId, factionId);
        var always = new List<int>();
        var pool = new List<(int Tag, float Weight)>();
        var never = new HashSet<int>();
        bool Free(int tag) => !pool.Exists(e => e.Tag == tag); // the first listing of a tag wins
        if (record is not null)
        {
            // Each list runs from index 0 to the first missing key; a tag of 0 is skipped.
            for (int i = 0; record.Ints.TryGetValue("tags always" + i, out int tag); i++)
            {
                if (Valid(tag)) always.Add(tag);
            }

            if (always.Count > 0) return always[Rng.Int(roll, always.Count)];

            for (int i = 0; record.Ints.TryGetValue("tags common" + i, out int tag); i++)
            {
                if (Valid(tag) && Free(tag)) pool.Add((tag, 3f));
            }

            for (int i = 0; record.Ints.TryGetValue("tags rare" + i, out int tag); i++)
            {
                if (Valid(tag) && Free(tag)) pool.Add((tag, 0.2f));
            }

            for (int i = 0; record.Ints.TryGetValue("tags never" + i, out int tag); i++)
            {
                if (Valid(tag)) never.Add(tag);
            }
        }

        // The never tags strike only the default tags, not the ones the record lists.
        foreach (int tag in Defaults)
        {
            if (Free(tag) && !never.Contains(tag)) pool.Add((tag, 1f));
        }

        if (pool.Count == 0) return None;

        float total = 0;
        foreach (var (_, weight) in pool) total += weight;

        float pick = Rng.Float(roll) * total, cumulative = 0;
        foreach (var (tag, weight) in pool)
        {
            cumulative += weight;
            if (cumulative > pick) return tag;
        }

        return pool[^1].Tag;
    }

    /// <summary>
    /// Whether <paramref name="tag"/> is a valid tag (1 to 14).
    /// </summary>
    static bool Valid(int tag) => tag is > None and < Count;

    /// <summary>
    /// The PERSONALITY record of a character (<c>FUN_14062b210</c>): only the first <c>personality</c> reference of the CHARACTER, if it resolves;
    /// else the first of its FACTION; else the <c>blank personality</c> record.
    /// </summary>
    static GameRecord? RecordOf(GameDatabase db, string characterId, string? factionId)
    {
        var fromCharacter = First(db, db.Find(characterId));
        if (fromCharacter is not null) return fromCharacter;

        var fromFaction = factionId is { Length: > 0 } ? First(db, db.Find(factionId)) : null;
        if (fromFaction is not null) return fromFaction;

        return db.Find("blank personality") is { Type: FcsRecordType.Personality } blank ? blank : null;
    }

    /// <summary>
    /// The PERSONALITY record of <paramref name="owner"/>'s first <c>personality</c> reference; null when it has none or it does not resolve to one.
    /// </summary>
    static GameRecord? First(GameDatabase db, GameRecord? owner) =>
        owner?.GetReferences("personality") is { Count: > 0 } list
            && db.Find(list[0].TargetStringId) is { Type: FcsRecordType.Personality } p ? p : null;
}
