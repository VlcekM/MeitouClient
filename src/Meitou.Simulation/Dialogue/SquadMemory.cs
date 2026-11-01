namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The tags a squad's memory holds about a character (docs/game/dialogue.md "Squad memory"): the long-term ones are the editor's <c>CharacterPerceptionTags_LongTerm</c>
/// (DC 23 HAS_TAG reads them), the short-term ones the exe's <c>CharacterPerceptionTags_ShortTerm</c> (named by its debug dump <c>FUN_140675680</c>). Bit = 1 &lt;&lt; tag.
/// </summary>
public static class MemoryTag
{
    public const int None = 0, Intruder = 1, Lifesaver = 2, FreedMe = 3, StoleFromMe = 4, Captor = 5, Friendly = 6, DefeatedMySquad = 7, SquadLostToMe = 8,
        KilledMyFriend = 9, ScrewedThisGuy = 10;
    public const int Aggressor = 2, TempAlly = 3, TempEnemy = 4, Criminal = 7;
    /// <summary>Short-term tag 6 (unnamed in the dump): the squad looted or stripped the character; its NPC looting finder skips it (docs/game/carry-loot.md "NPC looting").</summary>
    public const int Looted = 6;
    /// <summary>DC 52 I_LOVE_THIS_GUY's long-term mask (lifesaver, freed me) and DC 53 I_HATE_THIS_GUY's (stole from me, captor, defeated my squad, killed my friend).</summary>
    public const uint LoveMask = 0xc, HateMask = 0x2b0;
}

/// <summary>
/// The squads' memories of characters (the original's <c>platoon+0x98</c>, docs/game/dialogue.md "Squad memory"): per squad and character a time and two tag sets. A player-faction
/// character's long-term tags and time live in one entry the squad keeps for the whole player faction (<c>FUN_140671840</c>); its short-term tags stay per character, and the squad's
/// temporary ally flag for the player is one bool (<c>platoon memory +0x28</c>). Times are game hours. Hashed by the dialogue system; saved on the squad's platoon (the original's keys,
/// <c>taglong</c> / <c>tagshort</c> / <c>tagtime</c>; <see cref="Saving.MemorySave"/>) and kept by the platoon while its squad is out of the world (<see cref="Take"/>, <see cref="Restore"/>).
/// </summary>
public sealed class SquadMemory
{
    public sealed class Entry
    {
        public double Time;
        public uint Long, Short;
    }

    static int CompareIds(CharacterId a, CharacterId b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Generation.CompareTo(b.Generation);

    readonly SortedDictionary<(int Squad, CharacterId Who), Entry> entries = new(Comparer<(int, CharacterId)>.Create((a, b) =>
        a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : CompareIds(a.Item2, b.Item2)));
    // The same entries hashed, for the lookups (the relation tests make several per pair of characters); the sorted map keeps the hash and save order.
    readonly Dictionary<(int Squad, CharacterId Who), Entry> index = [];
    readonly SortedDictionary<int, Entry> player = [];
    readonly SortedSet<int> tempPlayerAlly = [];
    // Squads holding a TEMP_ENEMY tag on someone: the aggro scan's gate (a derived index, not hashed).
    readonly SortedSet<int> tempEnemySquads = [];

    public bool IsEmpty => entries.Count == 0 && player.Count == 0 && tempPlayerAlly.Count == 0;

    static bool PlayerSide(World world, CharacterCold c) => c.IsPlayer || (world.Player.Faction >= 0 && c.Faction == world.Player.Faction);

    /// <summary>The entry a lookup finds (<c>FUN_140673a90</c>): the squad's player entry for a player-faction character, else the character's own; null: never remembered.</summary>
    public Entry? Find(World world, int squad, CharacterId who) => Read(world, squad, who).Found;

    /// <summary>
    /// <see cref="HasShort"/>'s tag bits and <see cref="Find"/>'s entry from one lookup, for a test that needs several of them: the short-term tags of the character's own entry, with
    /// TEMP_ALLY of a player-faction character the squad's player flag; (0, null) when the squad or the character is not there.
    /// </summary>
    public (uint Short, Entry? Found) Read(World world, int squad, CharacterId who)
    {
        if (squad < 0 || !world.Characters.TryResolveNext(who, out int s) || world.Characters.Cold(s) is not { } c) return (0, null);
        index.TryGetValue((squad, who), out var own);
        uint tags = own?.Short ?? 0;
        if (!PlayerSide(world, c)) return (tags, own);
        tags = tags & ~(1u << MemoryTag.TempAlly) | (tempPlayerAlly.Contains(squad) ? 1u << MemoryTag.TempAlly : 0);
        return (tags, player.GetValueOrDefault(squad));
    }

    /// <summary>
    /// A long-term tag and the time (<c>FUN_140673930</c>; tag 0 only remembers): what DA 19 REMEMBER_CHARACTER and the end of a player conversation (<c>FUN_140674830</c>) write.
    /// </summary>
    public void Remember(World world, int squad, CharacterId who, int tag)
    {
        if (squad < 0 || (uint)tag > 31 || !world.Characters.TryResolveNext(who, out int s) || world.Characters.Cold(s) is not { } c) return;
        var e = PlayerSide(world, c) ? Get(player, squad) : Own(squad, who);
        e.Long |= 1u << tag;
        e.Time = world.GameHours;
    }

    /// <summary>
    /// A short-term tag on the character's own entry and the time (<c>FUN_140674300</c>): never TEMP_ENEMY on a character of the squad's own faction; for a player-faction character
    /// TEMP_ALLY also sets the squad's player flag, and the squad's player entry, when there is one, takes the time.
    /// </summary>
    public void Flag(World world, int squad, CharacterId who, int tag)
    {
        if (squad < 0 || (uint)tag > 31 || !world.Characters.TryResolveNext(who, out int s) || world.Characters.Cold(s) is not { } c) return;
        if (tag == MemoryTag.TempEnemy && world.Squads.Find(squad) is { } q && q.Faction == c.Faction) return;
        var e = Own(squad, who);
        e.Short |= 1u << tag;
        e.Time = world.GameHours;
        if (tag == MemoryTag.TempEnemy) tempEnemySquads.Add(squad);
        if (!PlayerSide(world, c)) return;
        if (tag == MemoryTag.TempAlly) tempPlayerAlly.Add(squad);
        if (player.TryGetValue(squad, out var p)) p.Time = world.GameHours;
    }

    /// <summary>Whether the squad holds short-term <paramref name="tag"/> on the character (<c>FUN_140678830</c>): TEMP_ALLY on a player-faction character is the squad's player flag.</summary>
    public bool HasShort(World world, int squad, CharacterId who, int tag) => (Read(world, squad, who).Short >> tag & 1) != 0;

    /// <summary>The hours since the squad last noted the character (<c>FUN_140673290</c>); 9999.99 when it never did.</summary>
    public double HoursSince(World world, int squad, CharacterId who) => Find(world, squad, who) is { } e ? world.GameHours - e.Time : 9999.99;

    /// <summary>
    /// Copies a squad's memory to another (<c>FUN_1405e85e0</c>, a character separating into a new squad, DA 28). Engine reading: the entries are copied as they are.
    /// </summary>
    public void Copy(int from, int to)
    {
        foreach (var ((squad, who), e) in entries.Where(kv => kv.Key.Squad == from).ToArray()) entries[(to, who)] = index[(to, who)] = new Entry { Time = e.Time, Long = e.Long, Short = e.Short };
        if (player.TryGetValue(from, out var p)) player[to] = new Entry { Time = p.Time, Long = p.Long, Short = p.Short };
        if (tempPlayerAlly.Contains(from)) tempPlayerAlly.Add(to);
        if (tempEnemySquads.Contains(from)) tempEnemySquads.Add(to);
    }

    /// <summary>
    /// Short-term tags fade (<c>FUN_1406724e0</c>, the platoon slow step): an entry whose whole hours since its last write exceed 6 loses all its short tags;
    /// its long tags and time stay. The squad's player TEMP_ALLY flag is not touched.
    /// </summary>
    public void Age(World world)
    {
        List<int>? lost = null;
        foreach (var ((squad, _), e) in entries)
            if (e.Short != 0 && (int)(world.GameHours - e.Time) > 6)
            {
                if ((e.Short >> MemoryTag.TempEnemy & 1) != 0) (lost ??= []).Add(squad);
                e.Short = 0;
            }
        foreach (var e in player.Values)
            if (e.Short != 0 && (int)(world.GameHours - e.Time) > 6) e.Short = 0;
        if (lost is null) return;
        foreach (int squad in lost)
            if (!entries.Any(kv => kv.Key.Squad == squad && (kv.Value.Short >> MemoryTag.TempEnemy & 1) != 0)) tempEnemySquads.Remove(squad);
    }

    /// <summary>Whether the squad holds a TEMP_ENEMY tag on any character (the aggro scan skips the per-target lookup for squads that do not).</summary>
    public bool AnyTempEnemy(int squad) => squad >= 0 && tempEnemySquads.Contains(squad);

    /// <summary>
    /// A squad's memory as plain data (null when it has none): what a platoon keeps while its squad is out of the world and what a save writes (<see cref="Saving.MemorySave"/>).
    /// </summary>
    public SquadMemoryData? Snapshot(int squad)
    {
        if (squad < 0) return null;
        SquadMemoryData? data = null;
        foreach (var ((q, who), e) in entries)
            if (q == squad) (data ??= new()).Entries.Add(new SquadMemoryData.Item(who, e.Time, e.Long, e.Short));
        if (player.TryGetValue(squad, out var p)) (data ??= new()).Player = new SquadMemoryData.Item(CharacterId.None, p.Time, p.Long, p.Short);
        if (tempPlayerAlly.Contains(squad)) (data ??= new()).TempAlly = true;
        return data;
    }

    /// <summary><see cref="Snapshot"/> and forget: the squad leaves the world and its platoon keeps the memory.</summary>
    public SquadMemoryData? Take(int squad)
    {
        var data = Snapshot(squad);
        if (data is null) return null;
        foreach (var item in data.Entries) { entries.Remove((squad, item.Who)); index.Remove((squad, item.Who)); }
        player.Remove(squad);
        tempPlayerAlly.Remove(squad);
        tempEnemySquads.Remove(squad);
        return data;
    }

    /// <summary>Puts a snapshot into the memory of <paramref name="squad"/> (replacing what it holds about the same characters).</summary>
    public void Restore(int squad, SquadMemoryData? data)
    {
        if (squad < 0 || data is null) return;
        foreach (var item in data.Entries)
        {
            entries[(squad, item.Who)] = index[(squad, item.Who)] = new Entry { Time = item.Time, Long = item.Long, Short = item.Short };
            if ((item.Short >> MemoryTag.TempEnemy & 1) != 0) tempEnemySquads.Add(squad);
        }
        if (data.Player is { } p) player[squad] = new Entry { Time = p.Time, Long = p.Long, Short = p.Short };
        if (data.TempAlly) tempPlayerAlly.Add(squad);
    }

    Entry Own(int squad, CharacterId who)
    {
        if (!index.TryGetValue((squad, who), out var e)) entries[(squad, who)] = index[(squad, who)] = e = new Entry();
        return e;
    }

    static Entry Get<TKey>(SortedDictionary<TKey, Entry> map, TKey key) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var e)) map[key] = e = new Entry();
        return e;
    }

    public void Hash(ref StateHasher hasher)
    {
        if (IsEmpty) return;
        hasher.Add(0x4D656D6F);
        foreach (var ((squad, who), e) in entries) { hasher.Add(squad); hasher.Add(who); hasher.Add(e.Time); hasher.Add((ulong)e.Long); hasher.Add((ulong)e.Short); }
        foreach (var (squad, e) in player) { hasher.Add(squad); hasher.Add(e.Time); hasher.Add((ulong)e.Long); hasher.Add((ulong)e.Short); }
        foreach (int squad in tempPlayerAlly) hasher.Add(squad);
    }
}

/// <summary>
/// One squad's memory as plain data (<see cref="SquadMemory.Snapshot"/>): an item per remembered character (game hours of the last note, long-term and short-term tag bits), the item the
/// squad keeps for the whole player faction (<c>Who</c> none) and its TEMP_ALLY flag for the player (docs/game/dialogue.md "Squad memory").
/// </summary>
public sealed class SquadMemoryData
{
    public readonly record struct Item(CharacterId Who, double Time, uint Long, uint Short);

    public List<Item> Entries { get; } = [];
    public Item? Player { get; set; }
    public bool TempAlly { get; set; }
}
