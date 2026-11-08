using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Save;

namespace Meitou.Simulation;

/// <summary>A unique character's state (docs/game/factions-squads-towns.md "Unique characters", <c>NPC is</c> values): 1 is also the state of one never made.</summary>
public enum UniqueState : byte { Dead = 0, Alive = 1, Imprisoned = 2 }

/// <summary>One CHARACTER record with <c>unique</c>: whether a squad made it, the live character, its state and whether the player was involved.</summary>
public sealed class UniqueEntry
{
    /// <summary>A squad made it: no squad makes it again (it gets the replacement or nobody) until the entry is released.</summary>
    public bool Claimed { get; set; }
    /// <summary>The character while it is in the world (none: not in the world, or not made yet).</summary>
    public CharacterId Handle { get; set; } = CharacterId.None;
    public UniqueState State { get; set; } = UniqueState.Alive;
    /// <summary>The player killed, imprisoned or holds it.</summary>
    public bool Player { get; set; }
    /// <summary>The handle a loaded save gave it, written back while its character has not been made in this world.</summary>
    public Hand SavedHandle { get; set; } = Hand.Null;
}

/// <summary>
/// The original's <c>UniqueNPCManager</c> (docs/game/factions-squads-towns.md "Unique characters"): one map from unique CHARACTER record to its <see cref="UniqueEntry"/>.
/// Squad creation claims a free unique and makes the first <c>unique replacement spawn</c> (or nobody) for a claimed one; a dead unique stays claimed and never
/// comes back. Changed in serial phases only. Saved in the CAMERA record (<c>usedUniques*</c>, docs/formats/save.md).
/// </summary>
public sealed class UniqueCharacters
{
    readonly SortedDictionary<string, UniqueEntry> entries = new(StringComparer.Ordinal);
    readonly Dictionary<CharacterId, string> byHandle = [];
    bool seeded;

    public int Count => entries.Count;
    /// <summary>Bumped by every change to a state or player flag (an entry made, released, dead, captured, freed, loaded or seeded): world states read it.</summary>
    public long Version { get; private set; }
    public IEnumerable<KeyValuePair<string, UniqueEntry>> All => entries;
    public UniqueEntry? Find(string recordId) => entries.GetValueOrDefault(recordId);
    /// <summary>The state a query sees: an absent entry is alive (the original's default).</summary>
    public UniqueState StateOf(string recordId) => entries.TryGetValue(recordId, out var e) ? e.State : UniqueState.Alive;
    public bool IsClaimed(string recordId) => entries.TryGetValue(recordId, out var e) && e.Claimed;
    /// <summary>The unique record of a character in the world (null: not a tracked unique).</summary>
    public string? RecordOf(CharacterId id) => byHandle.GetValueOrDefault(id);

    public static bool IsUnique(GameRecord? record) => record is { Type: FcsRecordType.CHARACTER or FcsRecordType.ANIMAL_CHARACTER } && record.GetBool("unique");

    UniqueEntry Entry(string recordId)
    {
        if (!entries.TryGetValue(recordId, out var e)) entries[recordId] = e = new UniqueEntry();
        return e;
    }

    /// <summary>The create gate (<c>FUN_1405836e0</c>): claims a free unique and returns true; false when a squad already made it.</summary>
    public bool TryClaim(string recordId)
    {
        var e = Entry(recordId);
        if (e.Claimed) return false;
        e.Claimed = true;
        Unbind(e);
        return true;
    }

    /// <summary>
    /// A unique character came into the world (made, or brought back from a stored or saved squad): its handle. Claimed too (engine choice: the original records only the
    /// handle here; a unique in the world is never made a second time).
    /// </summary>
    public void Bind(string recordId, CharacterId id)
    {
        var e = Entry(recordId);
        e.Claimed = true;
        Unbind(e);
        e.Handle = id;
        byHandle[id] = recordId;
    }

    void Unbind(UniqueEntry e)
    {
        if (!e.Handle.IsNone) byHandle.Remove(e.Handle);
        e.Handle = CharacterId.None;
    }

    /// <summary>The character leaves the world (its squad stored or saved): its entry stays claimed with its state.</summary>
    public void Left(CharacterId id)
    {
        if (byHandle.Remove(id, out var recordId)) entries[recordId].Handle = CharacterId.None;
    }

    /// <summary>A living unique leaves the world for good without being kept (its squad is remade from its template later): free to be made again. A dead one stays claimed.</summary>
    public void Release(string recordId)
    {
        if (!entries.TryGetValue(recordId, out var e) || e.State == UniqueState.Dead) return;
        Unbind(e);
        entries.Remove(recordId);
        Version++;
    }

    /// <summary>
    /// Death (<c>FUN_1407a6200</c>): state 0; the player flag is set (never cleared here) when the killer is the player's. The faction of the dead character
    /// (<paramref name="faction"/>, -1: unknown) is queued, once per unique, for its prosperity drop (<see cref="TakeFallen"/>).
    /// </summary>
    public void Died(string recordId, bool byPlayer, int faction = -1)
    {
        Version++;
        var e = Entry(recordId);
        if (e.State != UniqueState.Dead && faction >= 0) fallen.Add(faction);
        e.State = UniqueState.Dead;
        e.Player |= byPlayer;
    }

    readonly List<int> fallen = [];

    /// <summary>The factions of the uniques that died since the last call, in order (the population lowers their prosperity, <c>FUN_1407a6200</c>).</summary>
    internal List<int> TakeFallen()
    {
        var list = new List<int>(fallen);
        fallen.Clear();
        return list;
    }

    /// <summary>Imprisoned or captured (<c>FUN_140330600</c>): state 2 unless dead; the player flag set when the captor is the player's.</summary>
    public void Captured(string recordId, bool byPlayer)
    {
        var e = Entry(recordId);
        if (e.State == UniqueState.Dead) return;
        e.State = UniqueState.Imprisoned;
        Version++;
        e.Player |= byPlayer;
    }

    /// <summary>Freed (the periodic refresh, <c>FUN_1405cf470</c>): state 1, player cleared.</summary>
    public void Freed(string recordId)
    {
        var e = Entry(recordId);
        e.State = UniqueState.Alive;
        e.Player = false;
        Version++;
    }

    /// <summary>A loaded save's entry (claimed, as the save writes only made ones and re-derives the prisoners).</summary>
    public void Load(string recordId, UniqueState state, bool player, Hand handle)
    {
        var e = Entry(recordId);
        e.Claimed = true;
        e.State = state;
        e.Player = player;
        e.SavedHandle = handle;
        Version++;
    }

    /// <summary>
    /// New game and load (<c>FUN_1409a8970</c>), once per world: the unique leader, squad and squad2 of every SQUAD_TEMPLATE's first <c>prisoners</c> template are
    /// imprisoned and unclaimed. An entry already there (a loaded save's) is kept.
    /// </summary>
    public void SeedPrisoners(GameDatabase db)
    {
        if (seeded) return;
        seeded = true;
        Version++;
        foreach (var template in db.OfType(FcsRecordType.SQUAD_TEMPLATE))
        {
            var refs = template.GetReferences("prisoners");
            if (refs.Count == 0 || db.Find(refs[0].TargetStringId) is not { } prisoners) continue;
            foreach (var list in (string[])["leader", "squad", "squad2"])
                foreach (var r in prisoners.GetReferences(list))
                    if (!entries.ContainsKey(r.TargetStringId) && IsUnique(db.Find(r.TargetStringId)))
                        entries[r.TargetStringId] = new UniqueEntry { State = UniqueState.Imprisoned };
        }
    }

    internal void Hash(ref StateHasher h)
    {
        h.Add(entries.Count);
        foreach (var (id, e) in entries)
        {
            h.Add(Rng.StableHash(id));
            h.Add(e.Claimed);
            h.Add((int)e.State);
            h.Add(e.Player);
            h.Add(e.Handle.IsNone);
        }
    }
}
