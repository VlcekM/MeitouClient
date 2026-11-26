using Meitou.Data.Save;
using Meitou.Simulation.Saving;

namespace Meitou.Simulation;

/// <summary>
/// The characters' lasting ids (docs/modding.md "Character ids"), for code mods that keep data per character: <see cref="GetOrGive"/> gives a
/// character one the first time a mod asks, and it stays with the character (its <see cref="CharacterCold.LastingId"/>) for life; <see cref="Find"/>
/// goes the other way and <see cref="KeptIds"/> tells the ids of characters the game still keeps from those gone for good. Saved beside the save
/// (<see cref="SaveLastingIds"/>, by <see cref="SaveCapture"/>) and given back by the load, a character of an NPC platoon when its platoon is made.
/// Until a mod asks for an id, or a save with ids is loaded, nothing here holds anything, the state hash is as without it and no file is written.
/// </summary>
public sealed class LastingIds(World world)
{
    #region Ids

    /// <summary>
    /// XORed into the world seed for the high half of the ids, so two games' ids differ.
    /// </summary>
    const ulong PrefixSalt = 0x1A57_11D5UL;

    /// <summary>
    /// The error of <see cref="GetOrGive"/> during a save.
    /// </summary>
    const string DuringSave =
        "LastingIds.GetOrGive cannot run during a save: give ids as data is first stored; IModSaveData.Write reads CharacterCold.LastingId";

    /// <summary>
    /// The high half of every id given (0: not chosen yet): chosen with the first id from the world seed and the game hours, kept with the save, so a
    /// line of saves that lost its file (and starts its counter again) gives ids that differ from the old ones.
    /// </summary>
    uint _prefix;

    /// <summary>
    /// The counter the next id's low half is made from (0: none given yet; it starts at 1, so no id is 0).
    /// </summary>
    ulong _next;

    /// <summary>
    /// The lasting id of <paramref name="character"/>, given now if it has none: the high half chosen from the world seed, the low half a counter,
    /// both kept with the save, so no two characters of one game's line of saves get the same. <see cref="LastingId.None"/> for a character not in
    /// the world. Call it on the thread that ticks the world, between ticks or in a serial phase (a mod system's <c>Inputs</c>, <c>Schedule</c>,
    /// <c>Commit</c> or <c>SlowWorld</c>, <c>IWorldReady</c>, <c>IModSaveData.Read</c>), and not during a save: from the capture until the next
    /// tick, which covers <c>IModSaveData.Write</c>, it throws, as in a parallel phase, whether or not the character already has an id, so a save
    /// never gives one. Read <see cref="CharacterCold.LastingId"/> there instead.
    /// </summary>
    public LastingId GetOrGive(CharacterId character)
    {
        var table = world.Characters;
        if (table.Phase == TablePhase.Parallel)
            throw new InvalidOperationException("LastingIds.GetOrGive runs in the serial phases only; read CharacterCold.LastingId in a parallel one");
        if (table.SaveHold) throw new InvalidOperationException(DuringSave);

        if (!table.TryResolveNext(character, out int slot) || table.Cold(slot) is not { } cold) return LastingId.None;
        if (!cold.LastingId.IsNone) return cold.LastingId;

        var id = Next();
        cold.LastingId = id;
        table.IndexLasting(id, character);

        // A character made from a save's NPC platoon is made again from it when its platoon comes back: the id goes with its save handle.
        if (cold.Save is { } link) (_bySave ??= [])[link.Source.Handle] = new SavedId(id, link.Source.RecordId);
        return id;
    }

    /// <summary>
    /// The character in the world that has lasting id <paramref name="id"/>, dead or alive; <see cref="CharacterId.None"/> when none has: not given in
    /// this line of saves, the character's squad is out of the world (an NPC platoon off screen) or the character is gone for good
    /// (<see cref="KeptIds"/> tells these apart).
    /// </summary>
    public CharacterId Find(LastingId id) => id.IsNone ? CharacterId.None : world.Characters.FindLasting(id);

    /// <summary>
    /// Whether any id was given or loaded: only then are they hashed and saved.
    /// </summary>
    internal bool Any => _next > 0 || world.Characters.LastingCount > 0 || _bySave is { Count: > 0 };

    /// <summary>
    /// From the start of a save's capture until the next tick begins, <see cref="GetOrGive"/> throws (<see cref="SaveCapture.Capture"/>).
    /// </summary>
    internal void HoldForSave() => world.Characters.SaveHold = true;

    /// <summary>
    /// A new id.
    /// </summary>
    LastingId Next()
    {
        if (_prefix == 0) _prefix = Math.Max(1u, (uint)(Rng.Mix(world.Seed ^ PrefixSalt ^ BitConverter.DoubleToUInt64Bits(world.GameHours)) >> 32));
        if (_next == 0) _next = 1;
        return new LastingId((ulong)_prefix << 32 | (uint)_next++);
    }

    #endregion

    #region Kept

    /// <summary>
    /// The ids of every character the game still keeps, dead or alive: in the world, in a squad stored off screen, or in a loaded save's record that
    /// is still kept (an NPC platoon not made since the load, a dead player character the save keeps as read). An id a mod holds that is not among
    /// them belongs to a character gone for good (killed and removed, its platoon despawned or wiped out), so the mod may drop its data. A scan of
    /// the world: call it now and then (once a game day, after a load), not per character per tick. Serial phases or between ticks.
    /// </summary>
    public HashSet<LastingId> KeptIds()
    {
        var kept = new HashSet<LastingId>();
        AddTableIds(kept);
        AddStoredIds(kept);
        AddSavedIds(kept);
        return kept;
    }

    /// <summary>
    /// The ids of the characters in the table.
    /// </summary>
    void AddTableIds(HashSet<LastingId> kept)
    {
        var table = world.Characters;
        for (int slot = 0; slot < table.HighWater; slot++)
            if (table.Cold(slot) is { LastingId.IsNone: false } cold) kept.Add(cold.LastingId);
    }

    /// <summary>
    /// The ids of the members, living and dead, of the squads stored off screen (<see cref="StoredSquad"/>).
    /// </summary>
    void AddStoredIds(HashSet<LastingId> kept)
    {
        foreach (var platoon in world.Platoons.All)
        {
            if (platoon.Stored is not { } stored) continue;

            foreach (var m in stored.Members.Concat(stored.Dead))
                if (!m.Cold.LastingId.IsNone) kept.Add(m.Cold.LastingId);
        }
    }

    /// <summary>
    /// The ids the loaded save's records hold for characters not in the world: kept while their saved member is not gone and its platoon not
    /// despawned or wiped out; a record no saved NPC platoon holds (a player platoon's) is kept.
    /// </summary>
    void AddSavedIds(HashSet<LastingId> kept)
    {
        if (_bySave is null) return;

        var members = new Dictionary<Hand, (SavedMember Member, SavedPlatoon Platoon)>();
        foreach (var sp in world.Saved?.Platoons ?? [])
            foreach (var m in sp.Members) members.TryAdd(m.Source.Handle, (m, sp));

        foreach (var (hand, saved) in _bySave)
        {
            if (kept.Contains(saved.Id)) continue;
            if (!members.TryGetValue(hand, out var found)) kept.Add(saved.Id);
            else if (!found.Member.Gone && !found.Platoon.Despawned && found.Platoon.PlatoonId >= 0) kept.Add(saved.Id);
        }
    }

    #endregion

    #region Saves

    /// <summary>
    /// An id a save's character holds, with the record that character was made from (an entry is only ever written back to a character of the same
    /// record).
    /// </summary>
    readonly record struct SavedId(LastingId Id, string Record);

    /// <summary>
    /// The ids of characters made from the loaded save, by their save handle: the save's entries not made yet, and those made (an NPC platoon's
    /// member is made again from its save records each time its platoon comes back). Null until there is one.
    /// </summary>
    Dictionary<Hand, SavedId>? _bySave;

    /// <summary>
    /// Takes a loaded save's ids (<see cref="SaveLastingIds.Read"/>) before its characters are made: each entry whose handle names a character of
    /// <paramref name="save"/> made from the entry's record, with its tags (<see cref="CharacterTags"/>). The counter goes past every id the file
    /// holds, kept or not, so none is given again.
    /// </summary>
    internal void Load(SaveLastingIds.Contents contents, SaveGame save)
    {
        if (contents.Entries.Count == 0 && contents.Next == 0) return;

        _prefix = contents.Prefix;
        ulong highest = contents.Entries.Count > 0 ? contents.Entries.Max(e => e.Id & 0xFFFF_FFFFUL) : 0;
        _next = Math.Max(contents.Next, highest + 1);

        var byHand = CharactersByHand(save);
        _bySave = [];
        foreach (var e in contents.Entries)
        {
            if (!byHand.TryGetValue(e.Hand, out var sc) || sc.RecordId != e.Record) continue;
            if (_bySave.TryAdd(e.Hand, new SavedId(new LastingId(e.Id), e.Record))) world.CharacterTags.Load(new LastingId(e.Id), e.Tags);
        }
    }

    /// <summary>
    /// The id the save gives its character <paramref name="sc"/>, by its handle (none when it gives none, or when a character in the world already has
    /// it: one that left its saved platoon keeps it); called as a character is made from a save, before it enters the table.
    /// </summary>
    internal LastingId ForSaved(SaveCharacter sc) =>
        _bySave is not null && sc.Handle is { IsNull: false } handle && _bySave.TryGetValue(handle, out var saved) && Find(saved.Id).IsNone
            ? saved.Id
            : LastingId.None;

    /// <summary>
    /// The file for a save just captured (null when no id was given or loaded): each character written with an id under the handle it was written
    /// with (<paramref name="written"/>), then the loaded save's entries whose characters the new save still holds unchanged (an NPC platoon never
    /// made, a dead player character kept as it was) with the same record. An entry whose character the save no longer has is dropped; an id and a
    /// handle are written once. Each entry carries its character's tags (<see cref="CharacterTags"/>). The counter and the prefix are always
    /// written, so an id given before the capture is never given again.
    /// </summary>
    internal byte[]? Write(SaveGame game, IReadOnlyDictionary<CharacterCold, Hand> written)
    {
        if (!Any) return null;

        var byHand = CharactersByHand(game);
        var entries = new List<SaveLastingIds.Entry>();
        var ids = new HashSet<LastingId>();
        var hands = new HashSet<Hand>();
        foreach (var (cold, hand) in written)
            if (!cold.LastingId.IsNone) AddEntry(entries, ids, hands, byHand, cold.LastingId, hand, null, world.CharacterTags);
        foreach (var (hand, saved) in _bySave ?? []) AddEntry(entries, ids, hands, byHand, saved.Id, hand, saved.Record, world.CharacterTags);

        return SaveLastingIds.Write(entries, _prefix, _next);
    }

    /// <summary>
    /// A save's characters by handle (the first of a repeated one).
    /// </summary>
    static Dictionary<Hand, SaveCharacter> CharactersByHand(SaveGame game)
    {
        var byHand = new Dictionary<Hand, SaveCharacter>();
        foreach (var sc in game.Platoons.SelectMany(p => p.Characters))
            if (sc.Handle is { IsNull: false } hand) byHand.TryAdd(hand, sc);

        return byHand;
    }

    /// <summary>
    /// Adds the entry of <paramref name="id"/> under <paramref name="hand"/> when the save has a character there (made from
    /// <paramref name="record"/>, when given) and neither the id nor the handle has an entry yet, with the id's tags.
    /// </summary>
    static void AddEntry(List<SaveLastingIds.Entry> entries, HashSet<LastingId> ids, HashSet<Hand> hands, Dictionary<Hand, SaveCharacter> byHand,
        LastingId id, Hand hand, string? record, CharacterTags tags)
    {
        if (!byHand.TryGetValue(hand, out var sc) || record is not null && sc.RecordId != record) return;
        if (ids.Contains(id) || hands.Contains(hand)) return;

        ids.Add(id);
        hands.Add(hand);
        entries.Add(new SaveLastingIds.Entry(hand, id.Value, sc.RecordId, tags.ForSave(id)));
    }

    /// <summary>
    /// Adds the ids to the state hash: the prefix, the counter, the save's entries held and every character in the table with an id, in slot order.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        var table = world.Characters;
        hasher.Add(_prefix);
        hasher.Add(_next);
        hasher.Add(_bySave?.Count ?? 0);
        hasher.Add(table.LastingCount);
        for (int slot = 0; slot < table.HighWater; slot++)
        {
            if (!table.Previous[slot].Alive || table.Cold(slot) is not { LastingId.IsNone: false } cold) continue;

            hasher.Add(slot);
            hasher.Add(cold.LastingId.Value);
        }
    }

    #endregion
}
