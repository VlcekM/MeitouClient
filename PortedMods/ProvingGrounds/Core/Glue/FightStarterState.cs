namespace ProvingGrounds.Core.Glue;

// The game-free state of FightStarter (task 3.1): the C++'s two vectors, the queue between the hit observer and the next serial step, and
// the keep-alive clock of KOWatcher.cpp that drives FightStarter::KeepEngaged(Match). src/Game/FightStarter.cs keys them by CharacterId.

/// <summary>
/// An ordered list of (fighter, value) pairs with the C++'s vector semantics: find the first entry of a fighter, add one only when absent,
/// set in place or append, erase the first match (later entries keep their order). C++: FightStarter.cpp:50-51 (<c>g_originalStates</c>,
/// <c>g_focusAssignments</c>), :106-114 FindOriginalState, :118-129 RememberOriginalState's add, :137-145 FindAssignedTarget, :147-163
/// SetAssignedTarget, :165-174 ClearAssignedTarget, :422-426 the state's erase, :458-462 AbandonWorldState's clear.
/// </summary>
public sealed class FighterList<TKey, TValue> where TKey : IEquatable<TKey>
{
    readonly List<(TKey Key, TValue Value)> entries = [];

    public int Count => entries.Count;

    /// <summary>The index of the first entry of <paramref name="key"/>, or -1. C++ :106-114.</summary>
    public int Find(TKey key)
    {
        for (int i = 0; i < entries.Count; ++i)
            if (entries[i].Key.Equals(key)) return i;
        return -1;
    }

    public bool TryGet(TKey key, out TValue value)
    {
        int i = Find(key);
        value = i >= 0 ? entries[i].Value : default!;
        return i >= 0;
    }

    /// <summary>Appends when the fighter has no entry yet; false (nothing changed) when it has one. C++ :118, :129.</summary>
    public bool AddIfAbsent(TKey key, TValue value)
    {
        if (Find(key) >= 0) return false;
        entries.Add((key, value));
        return true;
    }

    /// <summary>Updates the fighter's entry in place, or appends one. C++ :147-163.</summary>
    public void Set(TKey key, TValue value)
    {
        int i = Find(key);
        if (i >= 0) entries[i] = (key, value);
        else entries.Add((key, value));
    }

    /// <summary>Erases the fighter's first entry; false when it had none. C++ :165-174, :422-426.</summary>
    public bool Remove(TKey key)
    {
        int i = Find(key);
        if (i < 0) return false;
        entries.RemoveAt(i);
        return true;
    }

    /// <summary>C++ :460-461.</summary>
    public void Clear() => entries.Clear();

    /// <summary>The entry at <paramref name="index"/>, in insertion order.</summary>
    public (TKey Key, TValue Value) this[int index] => entries[index];
}

/// <summary>
/// The incoming attacks a hit observer saw this tick, for the next serial step to answer with FightStarter::ReactToIncomingAttack
/// (FightStarter.cpp:573-589; the C++ calls it from its hit hook, CombatHooks.cpp:246-247). Meitou's observer must not change the world
/// (CombatHit.cs:47-53, CAP 3), so it only records here: a fixed ring allocated once, no lock (the observer and the drain both run serially on
/// the tick thread, CombatSystem.Apply.cs:49, :210; World.CombatHits.cs:71-91). When full, a new entry is dropped and counted.
/// </summary>
public sealed class IncomingAttackQueue<T>
{
    readonly T[] defenders, attackers;
    int head, count;

    public IncomingAttackQueue(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        defenders = new T[capacity];
        attackers = new T[capacity];
    }

    public int Count => count;

    /// <summary>Entries refused because the ring was full (since construction).</summary>
    public long Dropped { get; private set; }

    public bool TryAdd(T defender, T attacker)
    {
        if (count == defenders.Length) { Dropped++; return false; }
        int i = (head + count) % defenders.Length;
        defenders[i] = defender;
        attackers[i] = attacker;
        count++;
        return true;
    }

    public bool TryTake(out T defender, out T attacker)
    {
        if (count == 0) { defender = default!; attacker = default!; return false; }
        defender = defenders[head];
        attacker = attackers[head];
        head = (head + 1) % defenders.Length;
        count--;
        return true;
    }

    public void Clear() { head = 0; count = 0; }
}

/// <summary>
/// KOWatcher's two clocks that call FightStarter::KeepEngaged(Match) (KOWatcher.cpp:24-49, :66-78, :233-237): a 0.10 s scan driven by the
/// real-time dt from <c>GetTickCount()</c> (the port: <c>PgClock.NowMs</c>, the same unsigned subtraction, so it is right across the 2^32
/// wrap; the first dt after a reset is 0 and dt is capped at 1 s), and a 0.50 s keep-alive advanced by the scan interval at each scan.
/// </summary>
public sealed class FightKeepAliveCadence
{
    /// <summary>C++: KOWatcher.cpp:24 <c>kKoScanIntervalSec</c>.</summary>
    public const float ScanIntervalSec = 0.10f;
    /// <summary>C++: KOWatcher.cpp:25 <c>kKeepAliveIntervalSec</c>.</summary>
    public const float KeepAliveIntervalSec = 0.50f;

    float scanElapsedSec = ScanIntervalSec;   // C++ :27 g_scanElapsedSec
    float keepAliveElapsedSec;                // C++ :28 g_keepAliveElapsedSec
    uint lastTick;                            // C++ :29 g_lastTick (0 = not started)

    /// <summary>C++ :36-43 AdvanceClock's dt for a started clock: <c>(now - last) / 1000</c> unsigned, capped at 1 s.</summary>
    public static float Dt(uint now, uint last)
    {
        float dt = (float)unchecked(now - last) / 1000.0f;
        return dt > 1.0f ? 1.0f : dt;
    }

    /// <summary>One frame at <paramref name="nowMs"/>: true when the 0.10 s scan is due. C++ :36-43, :76-78.</summary>
    public bool Scan(uint nowMs)
    {
        float dt = lastTick == 0 ? 0.0f : Dt(nowMs, lastTick);
        lastTick = nowMs;
        return FrameCadencePolicy.Advance(ref scanElapsedSec, dt, ScanIntervalSec);
    }

    /// <summary>At a scan that retargeted nobody: true when the keep-alive is due. C++ :236-237.</summary>
    public bool KeepAlive() => FrameCadencePolicy.Advance(ref keepAliveElapsedSec, ScanIntervalSec, KeepAliveIntervalSec);

    /// <summary>C++ :233 after a retarget.</summary>
    public void ResetKeepAlive() => FrameCadencePolicy.Reset(ref keepAliveElapsedSec);

    /// <summary>C++ :44-49 ResetRuntimeState's clock part (no match active): the keep-alive restarts from 0, the scan is due on the next frame
    /// (the C++ resets <c>g_scanElapsedSec</c> and then sets it to the interval, :46-47; only the second write counts), the clock restarts.</summary>
    public void Reset()
    {
        FrameCadencePolicy.Reset(ref keepAliveElapsedSec);
        scanElapsedSec = ScanIntervalSec;
        lastTick = 0;
    }
}

/// <summary>
/// Meitou-only (no C++ counterpart; divergences.md "The disengage cleanup"): the tick each fighter was last given an attack order. The C++
/// disengage is synchronous (FightStarter.cpp:370-386), but in Meitou an attack order given this tick (a hit reaction answered at the start of
/// SlowWorld, a keep-alive) and the disengage's StopCommand both run at tick+1, where PlayerSystem's stop (PlayerSystem.cs:323-333) runs before
/// CombatSystem queues the earlier-deferred AttackOrder (StandardSystems.cs:543/549, CombatSystem.cs:345-357): the queued order would restart
/// the fight. A fighter ordered in the disengage's own tick therefore gets a second StopCommand at tick+2.
/// </summary>
public sealed class AttackOrderStamps<TKey> where TKey : IEquatable<TKey>
{
    readonly FighterList<TKey, long> ticks = new();

    public int Count => ticks.Count;

    /// <summary>The fighter was given an attack order in <paramref name="tick"/>.</summary>
    public void Stamp(TKey fighter, long tick) => ticks.Set(fighter, tick);

    /// <summary>At the full disengage: forgets the fighter and answers whether it was ordered in <paramref name="tick"/> (then it needs the
    /// late stop).</summary>
    public bool TakeOrderedIn(TKey fighter, long tick)
    {
        bool same = ticks.TryGet(fighter, out long t) && t == tick;
        ticks.Remove(fighter);
        return same;
    }

    public void Clear() => ticks.Clear();
}
