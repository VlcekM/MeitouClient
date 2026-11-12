using Meitou.Data;
using Meitou.Data.Fcs;

namespace Meitou.Simulation.Ai;

/// <summary>
/// What a character wants to buy (docs/game/ai-tasks.md goal 118): the original's AI list of (item function, wanted count, done count), built when
/// the AI is made from the CHARACTER record's <c>shopping</c> list (<c>FUN_1409a05b0</c>, <b>Verified</b>). An entry is an ITEM record (v0 the count,
/// v1 the percent chance, v2 the pass it belongs to); the list keeps the item's <c>item function</c>, not the item. Passes 0, 1, 2 ... are walked in
/// turn, each in record order, and the list ends at the first pass with no entry; an entry is kept when a roll of 0..100 does not exceed v1. A record
/// with no <c>shopping</c> list at all gets three independent chances of one unit each (food 3, drink 8, first aid 1). Goal 118 is wanted while some
/// entry has fewer done than wanted (<c>FUN_140998fa0</c> / <c>FUN_140999620</c>); every <c>done</c> goes back to 0 once a new game day has begun and
/// it is past 07:00 (the game clock's day counter against the list's remembered one, <c>FUN_140998e40</c>), and when the buyer is in another town
/// than the one it last bought in (<c>FUN_140998da0</c>). Not saved: the original builds it again whenever the character's AI is made. The buying
/// itself is <see cref="AiSystem"/>'s serial step.
/// </summary>
public sealed class ShoppingList
{
    /// <summary>
    /// The roll counters (under <see cref="RngPurpose.Spawn"/>, keyed by the character): an entry's chance by its place in the record, a default
    /// entry's by its place.
    /// </summary>
    const ulong EntryRoll = 0x5059_0000UL, DefaultRoll = 0x5059_8000UL;

    /// <summary>
    /// Item functions of the default list (<b>Verified</b>, <c>FUN_1409a05b0</c>): food (Bread, Rice Bowl), drink (Cactus Rum), first aid kit.
    /// </summary>
    static readonly int[] DefaultFunctions = [3, 8, 1];

    /// <summary>
    /// The hour the day's reset waits for (the original tests the game clock's hour above 7).
    /// </summary>
    const int ResetAfterHour = 7;

    /// <summary>
    /// The item function of each entry, how many of it are wanted and how many were bought today.
    /// </summary>
    readonly int[] _function, _wanted, _done;
    /// <summary>
    /// The day and town the bought counts are for (<c>int.MinValue</c> and -1: nobody has asked yet).
    /// </summary>
    int _day = int.MinValue, _town = -1;

    /// <summary>
    /// A list of item functions with the number wanted of each, none bought yet.
    /// </summary>
    internal ShoppingList(int[] function, int[] wanted)
    {
        this._function = function;
        this._wanted = wanted;
        _done = new int[function.Length];
    }

    /// <summary>
    /// A list that wants nothing.
    /// </summary>
    public ShoppingList() : this([], []) { }

    /// <summary>
    /// The number of entries.
    /// </summary>
    public int Count => _function.Length;

    /// <summary>
    /// The game day <paramref name="gameHours"/> falls in.
    /// </summary>
    public static int DayOf(double gameHours) => (int)Math.Floor(gameHours / 24);

    /// <summary>
    /// The list for the character <paramref name="key"/> made from the record <paramref name="recordId"/>.
    /// </summary>
    public static ShoppingList Make(GameDatabase db, string recordId, ulong seed, ulong key)
    {
        var links = db.Find(recordId)?.GetReferences("shopping");
        var functions = new List<int>();
        var counts = new List<int>();
        if (links is not { Count: > 0 })
        {
            for (int k = 0; k < DefaultFunctions.Length; k++)
            {
                if (Rng.Float(Rng.Hash(seed, key, RngPurpose.Spawn, DefaultRoll + (ulong)k)) > 0.5f)
                {
                    functions.Add(DefaultFunctions[k]);
                    counts.Add(1);
                }
            }

            return new ShoppingList([.. functions], [.. counts]);
        }

        for (int pass = 0; ; pass++)
        {
            bool any = false;
            for (int k = 0; k < links.Count; k++)
            {
                var values = links[k].Values;
                if (values.Value2 != pass) continue;
                any = true;
                if (Rng.Int(Rng.Hash(seed, key, RngPurpose.Spawn, EntryRoll + (ulong)k), 101) > values.Value1) continue;

                // Only ITEM records count (the original looks the name up among those); an unknown name is skipped.
                if (db.Find(links[k].TargetStringId) is not { Type: FcsRecordType.Item } item) continue;
                functions.Add(item.GetInt("item function"));
                counts.Add(values.Value0);
            }

            if (!any) break;
        }

        return new ShoppingList([.. functions], [.. counts]);
    }

    /// <summary>
    /// Starts the day's list over once a new game day has begun and it is past 07:00.
    /// </summary>
    void NewDay(int today, int hour)
    {
        if (today == _day || hour <= ResetAfterHour) return;
        StartOver(today);
    }

    /// <summary>
    /// Whether something is left to buy today in the town <paramref name="site"/> the buyer stands in (-1: none): goal 118's desire (0 once every
    /// entry is done).
    /// </summary>
    public bool Pending(int today, int hour, int site = -1)
    {
        NewDay(today, hour);

        // Standing in another town than the one it last bought in starts the list over at every evaluation, as the day's reset does, which also
        // records the day (FUN_140998fa0 calls FUN_140998e40, Verified).
        if (_town >= 0 && site >= 0 && _town != site) StartOver(today);
        for (int k = 0; k < _function.Length; k++)
            if (_wanted[k] > _done[k]) return true;
        return false;
    }

    /// <summary>
    /// The day's reset (<c>FUN_140998e40</c>): remembers <paramref name="today"/> and sets every done back to 0.
    /// </summary>
    void StartOver(int today)
    {
        _day = today;
        Array.Clear(_done);
    }

    /// <summary>
    /// The item function of entry <paramref name="k"/> while it still has units to buy today, else -1.
    /// </summary>
    public int OpenFunction(int k) => _wanted[k] > _done[k] ? _function[k] : -1;

    /// <summary>
    /// Starts a buying call at a shop of the town <paramref name="site"/> (<c>FUN_1409a14b0</c> opens with <c>FUN_140998da0</c>, <b>Verified</b>):
    /// the list starts over when the buyer is in another town than the one it last bought in, and when a new game day has begun.
    /// </summary>
    public void Begin(int site, int today, int hour)
    {
        NewDay(today, hour);
        if (_town != site)
        {
            _town = site;
            Array.Clear(_done);
        }
    }

    /// <summary>
    /// The first entry that still has units to buy today and whose item function is not in <paramref name="skipped"/> (the functions no seller had an
    /// item of in this call); -1 when none.
    /// </summary>
    public int NextOpen(ReadOnlySpan<int> skipped)
    {
        for (int k = 0; k < _function.Length; k++)
            if (_wanted[k] > _done[k] && !skipped.Contains(_function[k])) return k;
        return -1;
    }

    /// <summary>
    /// The item function of entry <paramref name="k"/>.
    /// </summary>
    public int FunctionOf(int k) => _function[k];

    /// <summary>
    /// Whether entry <paramref name="k"/> still has units to buy today.
    /// </summary>
    public bool IsOpen(int k) => _wanted[k] > _done[k];

    /// <summary>
    /// Counts one unit of entry <paramref name="k"/> as bought. The original does so before the sale, so a unit the buyer cannot pay for or has no
    /// room for is used up too.
    /// </summary>
    public void Use(int k) => _done[k]++;

    /// <summary>
    /// Whether the function mask has the item function <paramref name="f"/> (functions of 64 or more are never in a mask).
    /// </summary>
    public static bool Stocks(ulong mask, int f) => (uint)f < 64 && (mask >> f & 1) != 0;

    /// <summary>
    /// The bit of an item function in a stock mask (none for 64 or more).
    /// </summary>
    public static ulong Bit(int f) => (uint)f < 64 ? 1UL << f : 0;

    /// <summary>
    /// Adds the bought counts and their day and town to the world's state hash (nothing for a list nobody asked yet).
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        // A list nobody has asked yet is the one its seed makes: nothing to add.
        if (_day == int.MinValue && _town < 0) return;
        hasher.Add(_day);
        hasher.Add(_town);
        for (int k = 0; k < _done.Length; k++) hasher.Add(_done[k]);
    }
}