using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The tags a squad's memory holds about a character (docs/game/dialogue.md "Squad memory"): the long-term ones are the editor's
/// <c>CharacterPerceptionTags_LongTerm</c> (DC 23 HAS_TAG reads them), the short-term ones the exe's <c>CharacterPerceptionTags_ShortTerm</c> (named
/// by its debug dump <c>FUN_140675680</c>). Bit = 1 &lt;&lt; tag.
/// </summary>
public static class MemoryTag
{
    /// <summary>
    /// The long-term tags (<c>CharacterPerceptionTags_LongTerm</c>).
    /// </summary>
    public const int None = 0, Intruder = 1, Lifesaver = 2, FreedMe = 3, StoleFromMe = 4, Captor = 5, Friendly = 6,
        DefeatedMySquad = 7, SquadLostToMe = 8,
        KilledMyFriend = 9, ScrewedThisGuy = 10;
    /// <summary>
    /// The short-term tags the dump names (<c>CharacterPerceptionTags_ShortTerm</c>).
    /// </summary>
    public const int Aggressor = 2, TempAlly = 3, TempEnemy = 4, Criminal = 7;
    /// <summary>
    /// Short-term tag 6 (unnamed in the dump): the squad looted or stripped the character; its NPC looting finder skips it (docs/game/carry-loot.md
    /// "NPC looting").
    /// </summary>
    public const int Looted = 6;
    /// <summary>
    /// DC 52 I_LOVE_THIS_GUY's long-term mask (lifesaver, freed me) and DC 53 I_HATE_THIS_GUY's (stole from me, captor, defeated my squad, killed my
    /// friend).
    /// </summary>
    public const uint LoveMask = 0xc, HateMask = 0x2b0;
    /// <summary>
    /// The grudge test of the put-in-bed desire (<c>FUN_140599390</c>, <b>Verified</b>): long-term tags 1, 4, 5, 7, 8, 9, 10 and short-term tags 1,
    /// 2, 4, 5, 7.
    /// </summary>
    public const uint GrudgeLongMask = 0x7b2, GrudgeShortMask = 0xb6;
}

/// <summary>
/// The squads' memories of characters (the original's <c>platoon+0x98</c>, docs/game/dialogue.md "Squad memory"): per squad and character a time and
/// two tag sets. A player-faction character's long-term tags and time live in one entry the squad keeps for the whole player faction
/// (<c>FUN_140671840</c>); its short-term tags stay per character, and the squad's temporary ally flag for the player is one bool (<c>platoon memory
/// +0x28</c>). Times are game hours. Hashed by the dialogue system; saved on the squad's platoon (the original's keys, <c>taglong</c> /
/// <c>tagshort</c> / <c>tagtime</c>; <see cref="Saving.MemorySave"/>) and kept by the platoon while its squad is out of the world
/// (<see cref="Take"/>, <see cref="Restore"/>).
/// </summary>
public sealed class SquadMemory
{
    /// <summary>
    /// One squad's memory of one character: the game hour of the last note and the long-term and short-term tag bits.
    /// </summary>
    public sealed class Entry
    {
        /// <summary>
        /// Game hours of the last note.
        /// </summary>
        public double Time;
        /// <summary>
        /// The long-term and short-term tag bits (bit = 1 &lt;&lt; tag).
        /// </summary>
        public uint Long, Short;
    }

    /// <summary>
    /// Orders character ids by slot, then generation.
    /// </summary>
    static int CompareIds(CharacterId a, CharacterId b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Generation.CompareTo(b.Generation);

    /// <summary>
    /// The entries per squad and character, sorted for the hash and save order.
    /// </summary>
    readonly SortedDictionary<(int Squad, CharacterId Who), Entry> _entries = new(Comparer<(int, CharacterId)>.Create((a, b) =>
        a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : CompareIds(a.Item2, b.Item2)));
    /// <summary>
    /// The same entries hashed, for the lookups (the relation tests make several per pair of characters); the sorted map keeps the hash and
    /// save order.
    /// </summary>
    readonly Dictionary<(int Squad, CharacterId Who), Entry> _index = [];
    /// <summary>
    /// Per squad: its entry for the whole player faction (long-term tags and time).
    /// </summary>
    readonly SortedDictionary<int, Entry> _player = [];
    /// <summary>
    /// The squads with the TEMP_ALLY flag for the player.
    /// </summary>
    readonly SortedSet<int> _tempPlayerAlly = [];
    /// <summary>
    /// Squads holding a TEMP_ENEMY tag on someone: the aggro scan's gate (a derived index, not hashed).
    /// </summary>
    readonly SortedSet<int> _tempEnemySquads = [];

    /// <summary>
    /// Whether the memory holds nothing that is hashed.
    /// </summary>
    public bool IsEmpty => _entries.Count == 0 && _player.Count == 0 && _tempPlayerAlly.Count == 0;

    /// <summary>
    /// A player character, or a character of the player's faction.
    /// </summary>
    static bool PlayerSide(World world, CharacterCold c) => c.IsPlayer || (world.Player.Faction >= 0 && c.Faction == world.Player.Faction);

    /// <summary>
    /// The entry a lookup finds (<c>FUN_140673a90</c>): the squad's player entry for a player-faction character, else the character's own; null:
    /// never remembered.
    /// </summary>
    public Entry? Find(World world, int squad, CharacterId who) => Read(world, squad, who).Found;

    /// <summary>
    /// <see cref="HasShort"/>'s tag bits and <see cref="Find"/>'s entry from one lookup, for a test that needs several of them: the short-term tags
    /// of the character's own entry, with TEMP_ALLY of a player-faction character the squad's player flag; (0, null) when the squad or the character
    /// is not there.
    /// </summary>
    public (uint Short, Entry? Found) Read(World world, int squad, CharacterId who)
    {
        if (squad < 0 || !world.Characters.TryResolveNext(who, out int s) || world.Characters.Cold(s) is not { } c) return (0, null);
        _index.TryGetValue((squad, who), out var own);
        uint tags = own?.Short ?? 0;
        if (!PlayerSide(world, c)) return (tags, own);
        tags = tags & ~(1u << MemoryTag.TempAlly) | (_tempPlayerAlly.Contains(squad) ? 1u << MemoryTag.TempAlly : 0);
        return (tags, _player.GetValueOrDefault(squad));
    }

    /// <summary>
    /// A long-term tag and the time (<c>FUN_140673930</c>; tag 0 only remembers): what DA 19 REMEMBER_CHARACTER and the end of a player conversation
    /// (<c>FUN_140674830</c>) write.
    /// </summary>
    public void Remember(World world, int squad, CharacterId who, int tag)
    {
        if (squad < 0 || (uint)tag > 31 || !world.Characters.TryResolveNext(who, out int s) || world.Characters.Cold(s) is not { } c) return;
        var e = PlayerSide(world, c) ? Get(_player, squad) : Own(squad, who);
        e.Long |= 1u << tag;
        e.Time = world.GameHours;
    }

    /// <summary>
    /// A short-term tag on the character's own entry and the time (<c>FUN_140674300</c>): never TEMP_ENEMY on a character of the squad's own faction;
    /// for a player-faction character TEMP_ALLY also sets the squad's player flag, and the squad's player entry, when there is one, takes the time.
    /// </summary>
    public void Flag(World world, int squad, CharacterId who, int tag)
    {
        if (squad < 0 || (uint)tag > 31 || !world.Characters.TryResolveNext(who, out int s) || world.Characters.Cold(s) is not { } c) return;
        if (tag == MemoryTag.TempEnemy && world.Squads.Find(squad) is { } q && q.Faction == c.Faction) return;
        var e = Own(squad, who);
        e.Short |= 1u << tag;
        e.Time = world.GameHours;
        if (tag == MemoryTag.TempEnemy) _tempEnemySquads.Add(squad);
        if (!PlayerSide(world, c)) return;
        if (tag == MemoryTag.TempAlly) _tempPlayerAlly.Add(squad);
        if (_player.TryGetValue(squad, out var p)) p.Time = world.GameHours;
    }

    /// <summary>
    /// Clears a short-term tag on the character's own entry (<c>FUN_1406788d0</c>, <b>Verified</b>), the mirror of <see cref="Flag"/>; the time
    /// stays. <see cref="AnyTempEnemy"/> stays right.
    /// </summary>
    public void ClearShort(int squad, CharacterId who, int tag)
    {
        if (squad < 0 || (uint)tag > 31 || !_index.TryGetValue((squad, who), out var e) || (e.Short >> tag & 1) == 0) return;
        e.Short &= ~(1u << tag);
        if (tag == MemoryTag.TempEnemy && !_entries.Any(kv => kv.Key.Squad == squad && (kv.Value.Short >> MemoryTag.TempEnemy & 1) != 0))
            _tempEnemySquads.Remove(squad);
    }

    /// <summary>
    /// Whether the squad holds short-term <paramref name="tag"/> on the character (<c>FUN_140678830</c>): TEMP_ALLY on a player-faction character is
    /// the squad's player flag.
    /// </summary>
    public bool HasShort(World world, int squad, CharacterId who, int tag) => (Read(world, squad, who).Short >> tag & 1) != 0;

    /// <summary>
    /// The hours since the squad last noted the character (<c>FUN_140673290</c>); 9999.99 when it never did.
    /// </summary>
    public double HoursSince(World world, int squad, CharacterId who) => Find(world, squad, who) is { } e ? world.GameHours - e.Time : 9999.99;

    /// <summary>
    /// Copies a squad's memory to another (<c>FUN_1405e85e0</c>, a character separating into a new squad, DA 28). Engine reading: the entries are
    /// copied as they are.
    /// </summary>
    public void Copy(int from, int to)
    {
        foreach (var ((squad, who), e) in _entries.Where(kv => kv.Key.Squad == from).ToArray())
            _entries[(to, who)] = _index[(to, who)] = new Entry { Time = e.Time, Long = e.Long, Short = e.Short };
        if (_player.TryGetValue(from, out var p)) _player[to] = new Entry { Time = p.Time, Long = p.Long, Short = p.Short };
        if (_tempPlayerAlly.Contains(from)) _tempPlayerAlly.Add(to);
        if (_tempEnemySquads.Contains(from)) _tempEnemySquads.Add(to);
    }

    /// <summary>
    /// Short-term tags fade (<c>FUN_1406724e0</c>, the platoon slow step): an entry whose whole hours since its last write exceed 6 loses all its
    /// short tags; its long tags and time stay. The squad's player TEMP_ALLY flag is not touched.
    /// </summary>
    public void Age(World world)
    {
        List<int>? lost = null;
        foreach (var ((squad, _), e) in _entries)
            if (e.Short != 0 && (int)(world.GameHours - e.Time) > 6)
            {
                if ((e.Short >> MemoryTag.TempEnemy & 1) != 0) (lost ??= []).Add(squad);
                e.Short = 0;
            }
        foreach (var e in _player.Values)
            if (e.Short != 0 && (int)(world.GameHours - e.Time) > 6) e.Short = 0;
        Forget(world, ref lost);
        if (lost is null) return;
        foreach (int squad in lost)
            if (!_entries.Any(kv => kv.Key.Squad == squad && (kv.Value.Short >> MemoryTag.TempEnemy & 1) != 0)) _tempEnemySquads.Remove(squad);
    }

    /// <summary>
    /// The size above which a memory forgets its old entries, and the age in game hours from which they go (<c>FUN_1406724e0</c>, <b>Verified</b>:
    /// 0x28 and 0x30).
    /// </summary>
    const int ForgetAbove = 40, ForgetAfterHours = 48;

    /// <summary>
    /// A memory of more than <see cref="ForgetAbove"/> entries erases each entry older than <see cref="ForgetAfterHours"/> hours whose character is
    /// not one of the player's faction (or no longer resolves), all tags with it (<c>FUN_1406724e0</c> with <c>FUN_1409fdfe0</c>, <b>Verified</b>).
    /// </summary>
    void Forget(World world, ref List<int>? lost)
    {
        var counts = new Dictionary<int, int>();
        foreach (var ((squad, _), _) in _entries) counts[squad] = counts.GetValueOrDefault(squad) + 1;
        List<(int Squad, CharacterId Who)>? gone = null;
        foreach (var ((squad, who), e) in _entries)
        {
            if (counts[squad] <= ForgetAbove || (int)(world.GameHours - e.Time) <= ForgetAfterHours) continue;
            bool player = world.Characters.TryResolveNext(who, out int slot) && world.Characters.Cold(slot) is { } c
                && c.Faction == world.Player.Faction;
            if (!player) (gone ??= []).Add((squad, who));
        }
        if (gone is null) return;
        foreach (var key in gone)
        {
            if (_entries.TryGetValue(key, out var e) && (e.Short >> MemoryTag.TempEnemy & 1) != 0) (lost ??= []).Add(key.Squad);
            _entries.Remove(key);
            _index.Remove(key);
        }
    }

    /// <summary>
    /// Whether the squad holds a TEMP_ENEMY tag on any character (the aggro scan skips the per-target lookup for squads that do not).
    /// </summary>
    public bool AnyTempEnemy(int squad) => squad >= 0 && _tempEnemySquads.Contains(squad);

    /// <summary>
    /// A squad's memory as plain data (null when it has none): what a platoon keeps while its squad is out of the world and what a save writes
    /// (<see cref="Saving.MemorySave"/>).
    /// </summary>
    public SquadMemoryData? Snapshot(int squad)
    {
        if (squad < 0) return null;
        SquadMemoryData? data = null;
        foreach (var ((q, who), e) in _entries)
            if (q == squad) (data ??= new()).Entries.Add(new SquadMemoryData.Item(who, e.Time, e.Long, e.Short));
        if (_player.TryGetValue(squad, out var p)) (data ??= new()).Player = new SquadMemoryData.Item(CharacterId.None, p.Time, p.Long, p.Short);
        if (_tempPlayerAlly.Contains(squad)) (data ??= new()).TempAlly = true;
        return data;
    }

    /// <summary>
    /// <see cref="Snapshot"/> and forget: the squad leaves the world and its platoon keeps the memory.
    /// </summary>
    public SquadMemoryData? Take(int squad)
    {
        var data = Snapshot(squad);
        if (data is null) return null;
        foreach (var item in data.Entries)
        {
            _entries.Remove((squad, item.Who));
            _index.Remove((squad, item.Who));
        }
        _player.Remove(squad);
        _tempPlayerAlly.Remove(squad);
        _tempEnemySquads.Remove(squad);
        return data;
    }

    /// <summary>
    /// Puts a snapshot into the memory of <paramref name="squad"/> (replacing what it holds about the same characters).
    /// </summary>
    public void Restore(int squad, SquadMemoryData? data)
    {
        if (squad < 0 || data is null) return;
        foreach (var item in data.Entries)
        {
            _entries[(squad, item.Who)] = _index[(squad, item.Who)] = new Entry { Time = item.Time, Long = item.Long, Short = item.Short };
            if ((item.Short >> MemoryTag.TempEnemy & 1) != 0) _tempEnemySquads.Add(squad);
        }
        if (data.Player is { } p) _player[squad] = new Entry { Time = p.Time, Long = p.Long, Short = p.Short };
        if (data.TempAlly) _tempPlayerAlly.Add(squad);
    }

    /// <summary>
    /// The squad's own entry for <paramref name="who"/>, made when there is none.
    /// </summary>
    Entry Own(int squad, CharacterId who)
    {
        if (!_index.TryGetValue((squad, who), out var e)) _entries[(squad, who)] = _index[(squad, who)] = e = new Entry();
        return e;
    }

    /// <summary>
    /// The entry of <paramref name="key"/> in <paramref name="map"/>, made when there is none.
    /// </summary>
    static Entry Get<TKey>(SortedDictionary<TKey, Entry> map, TKey key) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var e)) map[key] = e = new Entry();
        return e;
    }

    /// <summary>
    /// Adds the entries, the player entries and the TEMP_ALLY squads in their sorted order to the world hash; nothing while empty.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        if (IsEmpty) return;
        hasher.Add(0x4D656D6F);
        foreach (var ((squad, who), e) in _entries)
        {
            hasher.Add(squad);
            hasher.Add(who);
            hasher.Add(e.Time);
            hasher.Add((ulong)e.Long);
            hasher.Add((ulong)e.Short);
        }
        foreach (var (squad, e) in _player)
        {
            hasher.Add(squad);
            hasher.Add(e.Time);
            hasher.Add((ulong)e.Long);
            hasher.Add((ulong)e.Short);
        }
        foreach (int squad in _tempPlayerAlly) hasher.Add(squad);
    }
}

/// <summary>
/// One squad's memory as plain data (<see cref="SquadMemory.Snapshot"/>): an item per remembered character (game hours of the last note, long-term
/// and short-term tag bits), the item the squad keeps for the whole player faction (<c>Who</c> none) and its TEMP_ALLY flag for the player
/// (docs/game/dialogue.md "Squad memory").
/// </summary>
public sealed class SquadMemoryData
{
    /// <summary>
    /// One remembered character: its id (none for the player entry), the game hour of the last note and its tag bits.
    /// </summary>
    public readonly record struct Item(CharacterId Who, double Time, uint Long, uint Short);

    /// <summary>
    /// An item per remembered character.
    /// </summary>
    public List<Item> Entries { get; } = [];
    /// <summary>
    /// The squad's entry for the whole player faction; null: none.
    /// </summary>
    public Item? Player { get; set; }
    /// <summary>
    /// The squad's TEMP_ALLY flag for the player.
    /// </summary>
    public bool TempAlly { get; set; }
}