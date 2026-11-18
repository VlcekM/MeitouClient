using System.Text;

// The game-free part of upstream/ProvingGrounds/plugin/src/SparStats.cpp (Proving Grounds by Shutori, GPLv3), task 3.2a (PA §2.8, §2.9):
// the per-match counter table keyed by fighter, the elimination order, the frozen podium snapshot (Core SparPodium.Snapshot) and the three
// podium fighters captured at freeze time. src/Game/SparStats.cs keys it by CharacterId and reads names and the match roster through it.
namespace ProvingGrounds.Core.Glue;

/// <summary>What SparStats.cpp reads of the running match (C++: <c>SparSession::GetParticipantCount</c>, <c>GetParticipant</c>,
/// <c>GetTeam</c>, <c>IsSparringOpponent</c>) and of a character (<c>Character::getName</c>). The caller passes it to each call that reads
/// the match (OnHit, OnBlock, OnDodge, Freeze) and SparStatsState never keeps it past that call; the glue's implementation is a struct built
/// per call over that call's world and task 3.3's SparSession (src/Game/SparStats.cs RosterView), so nothing holds a world between calls.</summary>
public interface ISparRoster<TKey> where TKey : struct, IEquatable<TKey>
{
    /// <summary>C++ <c>SparSession::GetParticipantCount()</c>.</summary>
    int ParticipantCount { get; }
    /// <summary>C++ <c>SparSession::GetParticipant(i)</c>; null for a NULL slot (the C++ skips it, SparStats.cpp:170-171).</summary>
    TKey? Participant(int index);
    /// <summary>C++ <c>SparSession::GetTeam(c)</c>.</summary>
    MatchRules.MatchTeam Team(TKey fighter);
    /// <summary>C++ <c>SparSession::IsSparringOpponent(attacker, defender)</c>.</summary>
    bool IsSparringOpponent(TKey attacker, TKey defender);
    /// <summary>C++ <c>c-&gt;getName()</c>; null reads as "" (a stale character).</summary>
    string? Name(TKey fighter);
}

/// <summary>Whether a fighter id still resolves (the glue: this tick's character table). Used only by
/// <see cref="SparStatsState{TKey}.GetPodiumCharacter{TFighters}(TFighters, int)"/>.</summary>
public interface ISparFighters<TKey> where TKey : struct, IEquatable<TKey>
{
    bool Exists(TKey fighter);
}

/// <summary>
/// SparStats.cpp's globals and functions (the C++ order), minus the engine: <c>g_live</c>, <c>g_elimOrder</c>, <c>g_snap</c>, <c>g_hasSnap</c>,
/// <c>g_podiumChars</c>. One object per world (the glue's SparStats owns it). Floats are the C++'s floats, added in the C++ order.
/// <para>One difference from the C++ (docs/divergences.md "SparStats live rows"): a live row is a reference object, so the C++'s dangling
/// <c>atk</c> pointer in OnHit (the two EnsureLive calls at SparStats.cpp:110-111: when adding the defender's row reallocates the vector) cannot
/// lose the attacker's counts.</para>
/// <para>Rows are pooled: <see cref="ResetForMatch"/> keeps the row objects and <see cref="EnsureLive"/> reuses them with every field reset, so
/// a match start and a new fighter's first blow allocate nothing once the pool has grown to the largest match seen (the C++'s
/// <c>clear()</c> also keeps the vector's capacity).</para>
/// </summary>
public sealed class SparStatsState<TKey> where TKey : struct, IEquatable<TKey>
{
    /// <summary>C++: SparStats.cpp:19-29 <c>LiveStats</c> (the character is the list's key).</summary>
    public sealed class LiveStats
    {
        public TKey Character;
        public float DamageDealt;
        public float DamageTaken;
        public float DamageMitigated;
        public int HitsLanded;
        public int Blocks;
        public int Misses;
        public int Dodges;
    }

    readonly List<LiveStats> livePool = [];                     // C++ :31 g_live: the first liveCount entries are the live rows, the rest pooled
    int liveCount;
    readonly LiveRows liveRows;
    readonly List<TKey> elimOrder = [];                         // C++ :32 g_elimOrder
    readonly SparPodium.Snapshot snap = new();                  // C++ :33 g_snap
    bool hasSnap;                                               // C++ :34 g_hasSnap
    readonly TKey?[] podiumChars = new TKey?[3];                // C++ :35 g_podiumChars

    public SparStatsState() => liveRows = new LiveRows(this);

    /// <summary>How many fighters have a live row (the F10 phase row).</summary>
    public int LiveCount => liveCount;
    /// <summary>The live rows in insertion order (tests, probes). The rows are reused by the next match: read, do not keep.</summary>
    public IReadOnlyList<LiveStats> Live => liveRows;

    /// <summary>The first <see cref="liveCount"/> rows of the pool (one object per state, so reading allocates nothing).</summary>
    sealed class LiveRows(SparStatsState<TKey> owner) : IReadOnlyList<LiveStats>
    {
        public int Count => owner.liveCount;
        public LiveStats this[int index] => (uint)index < (uint)owner.liveCount ? owner.livePool[index] : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<LiveStats> GetEnumerator()
        {
            for (int i = 0; i < owner.liveCount; ++i)
                yield return owner.livePool[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    /// <summary>The elimination order (tests, probes).</summary>
    public IReadOnlyList<TKey> EliminationOrder => elimOrder;

    // ---------- anonymous namespace of the C++ ----------

    // C++: SparStats.cpp:37-45
    LiveStats? FindLive(TKey c)
    {
        for (int i = 0; i < liveCount; ++i)
        {
            if (livePool[i].Character.Equals(c))
                return livePool[i];
        }
        return null;
    }

    // C++: SparStats.cpp:47-56
    LiveStats EnsureLive(TKey c)
    {
        LiveStats? existing = FindLive(c);
        if (existing is not null)
            return existing;
        // LiveStats row = {}; row.character = c; push_back: a pooled row with every field reset, or a new one
        LiveStats row;
        if (liveCount < livePool.Count)
        {
            row = livePool[liveCount];
            row.DamageDealt = 0.0f;
            row.DamageTaken = 0.0f;
            row.DamageMitigated = 0.0f;
            row.HitsLanded = 0;
            row.Blocks = 0;
            row.Misses = 0;
            row.Dodges = 0;
        }
        else
        {
            row = new LiveStats();
            livePool.Add(row);
        }
        row.Character = c;
        ++liveCount;
        return row;
    }

    // C++: SparStats.cpp:58-66
    bool AlreadyEliminated(TKey c)
    {
        for (int i = 0; i < elimOrder.Count; ++i)
        {
            if (elimOrder[i].Equals(c))
                return true;
        }
        return false;
    }

    // C++: SparStats.cpp:68-77
    static int ParticipantId<TRoster>(TRoster roster, TKey c) where TRoster : ISparRoster<TKey>
    {
        int count = roster.ParticipantCount;
        for (int i = 0; i < count; ++i)
        {
            if (roster.Participant(i) is { } p && p.Equals(c))
                return i;
        }
        return -1;
    }

    // ---------- namespace SparStats ----------

    /// <summary>C++: SparStats.cpp:82-85 (the save path's call, WorldLifecycle.cpp:130).</summary>
    public void AbandonWorldState()
    {
        ResetForMatch();
    }

    // C++: SparStats.cpp:87-96
    public void ResetForMatch()
    {
        liveCount = 0;   // g_live.clear(): the rows stay pooled
        elimOrder.Clear();
        hasSnap = false;
        snap.Clear();
        podiumChars[0] = null;
        podiumChars[1] = null;
        podiumChars[2] = null;
    }

    /// <summary>A blow that landed or missed; a null fighter is the C++'s NULL. A miss counts only here and in <see cref="OnDodge"/>, as the
    /// attacker's miss and the defender's dodge, only between sparring opponents (a whiff never reaches it).</summary>
    // C++: SparStats.cpp:98-128
    public void OnHit<TRoster>(TRoster roster, TKey? attacker, TKey? defender, float appliedDamage, float incomingDamage, bool isMiss)
        where TRoster : ISparRoster<TKey>
    {
        if (attacker is not { } a || defender is not { } d)
            return;
        if (!roster.IsSparringOpponent(a, d))
            return;

        LiveStats atk = EnsureLive(a);
        LiveStats def = EnsureLive(d);
        if (isMiss)
        {
            ++atk.Misses;
            ++def.Dodges;
            return;
        }

        if (appliedDamage < 0.0f)
            appliedDamage = 0.0f;
        if (incomingDamage < appliedDamage)
            incomingDamage = appliedDamage;

        atk.DamageDealt += appliedDamage;
        def.DamageTaken += appliedDamage;
        def.DamageMitigated += incomingDamage - appliedDamage;
        ++atk.HitsLanded;
    }

    // C++: SparStats.cpp:130-139
    public void OnBlock<TRoster>(TRoster roster, TKey? defender, TKey? attacker) where TRoster : ISparRoster<TKey>
    {
        if (attacker is not { } a || defender is not { } d)
            return;
        if (!roster.IsSparringOpponent(a, d))
            return;

        ++EnsureLive(d).Blocks;
        EnsureLive(a);
    }

    // C++: SparStats.cpp:141-150
    public void OnDodge<TRoster>(TRoster roster, TKey? attacker, TKey? defender) where TRoster : ISparRoster<TKey>
    {
        if (attacker is not { } a || defender is not { } d)
            return;
        if (!roster.IsSparringOpponent(a, d))
            return;

        ++EnsureLive(a).Misses;
        ++EnsureLive(d).Dodges;
    }

    /// <summary>Port only (task 3.2b, CombatHooks' fallback when the per-blow ApplyBlow patch is not available, Core/Glue/CombatHookLogic.cs
    /// <see cref="CounterFallback{TId}"/>): counts a match participant's blocks, misses and dodges with no paired opponent (the combat
    /// counters do not say who was on the other side). A character that is not a participant is ignored.</summary>
    public void AddUnpairedCounts<TRoster>(TRoster roster, TKey? fighter, int blocks, int misses, int dodges) where TRoster : ISparRoster<TKey>
    {
        if (fighter is not { } f || ParticipantId(roster, f) < 0)
            return;
        LiveStats row = EnsureLive(f);
        row.Blocks += blocks;
        row.Misses += misses;
        row.Dodges += dodges;
    }

    // C++: SparStats.cpp:152-157
    public void OnEliminated(TKey? who)
    {
        if (who is not { } w || AlreadyEliminated(w))
            return;
        elimOrder.Add(w);
    }

    // C++: SparStats.cpp:159-232
    public void Freeze<TRoster>(TRoster roster, SparPodium.OutcomeKind outcome, MatchRules.MatchMode mode, TKey? lastStandingOrNull)
        where TRoster : ISparRoster<TKey>
    {
        snap.Clear();
        snap.Outcome = outcome;
        snap.Mode = mode;
        snap.LastStandingId = -1;
        snap.FighterCount = 0;
        snap.EliminationCount = 0;

        int count = roster.ParticipantCount;
        for (int i = 0; i < count && snap.FighterCount < 16; ++i)
        {
            if (roster.Participant(i) is not { } c)
                continue;

            ref SparPodium.FighterRow row = ref snap.Fighters[snap.FighterCount];
            row.Id = i;
            row.Team = roster.Team(c);
            row.Name = CutName(roster.Name(c));
            row.EliminationIndex = -1;

            LiveStats? stats = FindLive(c);
            if (stats is not null)
            {
                row.DamageDealt = stats.DamageDealt;
                row.DamageTaken = stats.DamageTaken;
                row.DamageMitigated = stats.DamageMitigated;
                row.HitsLanded = stats.HitsLanded;
                row.Blocks = stats.Blocks;
                row.Misses = stats.Misses;
                row.Dodges = stats.Dodges;
            }

            ++snap.FighterCount;
        }

        for (int e = 0; e < elimOrder.Count && snap.EliminationCount < 16; ++e)
        {
            int id = ParticipantId(roster, elimOrder[e]);
            if (id < 0)
                continue;
            for (int f = 0; f < snap.FighterCount; ++f)
            {
                if (snap.Fighters[f].Id == id)
                {
                    snap.Fighters[f].EliminationIndex = snap.EliminationCount;
                    break;
                }
            }
            snap.EliminationIds[snap.EliminationCount++] = id;
        }

        if (lastStandingOrNull is { } last)
            snap.LastStandingId = ParticipantId(roster, last);

        SparPodium.BuildPodium(snap);

        podiumChars[0] = null;
        podiumChars[1] = null;
        podiumChars[2] = null;
        for (int p = 0; p < snap.PodiumCount && p < 3; ++p)
        {
            int id = snap.Podium[p].Fighter.Id;
            if (id >= 0 && id < count)
                podiumChars[p] = roster.Participant(id);
        }

        hasSnap = true;
    }

    /// <summary>C++: SparStats.cpp:234-237 (the C++ returns a const reference). The same object as <see cref="GetMutableSnapshot"/> for the
    /// life of this state: readers must not write to it, and its rows are cleared in place by the next <see cref="Freeze"/>,
    /// <see cref="ResetForMatch"/> or <see cref="ClearSnapshot"/>, so copy whatever must outlive that.</summary>
    public SparPodium.Snapshot GetSnapshot() => snap;

    /// <summary>C++: SparStats.cpp:239-242 (LeaderboardStore::ApplyFromSnapshot writes the rating and Marks fields into it). The same object
    /// as <see cref="GetSnapshot"/>, cleared in place by the next Freeze / ResetForMatch / ClearSnapshot.</summary>
    public SparPodium.Snapshot GetMutableSnapshot() => snap;

    // C++: SparStats.cpp:244-247
    public bool HasSnapshot() => hasSnap;

    // C++: SparStats.cpp:249-256
    public void ClearSnapshot()
    {
        hasSnap = false;
        snap.Clear();
        podiumChars[0] = null;
        podiumChars[1] = null;
        podiumChars[2] = null;
    }

    /// <summary>The fighter captured for podium slot 0 = winner / 1st, 1 = 2nd, 2 = 3rd at freeze time, or null.</summary>
    // C++: SparStats.cpp:258-263
    public TKey? GetPodiumCharacter(int podiumIndex)
    {
        if (!hasSnap || podiumIndex < 0 || podiumIndex >= 3)
            return null;
        return podiumChars[podiumIndex];
    }

    /// <summary><see cref="GetPodiumCharacter(int)"/> resolved against the live table: null when the fighter captured at freeze time no longer
    /// exists (the C++ would hand out a dangling <c>Character*</c>).</summary>
    public TKey? GetPodiumCharacter<TFighters>(TFighters fighters, int podiumIndex) where TFighters : ISparFighters<TKey>
        => GetPodiumCharacter(podiumIndex) is { } id && fighters.Exists(id) ? id : null;

    /// <summary><c>std::strncpy(row.name, name.c_str(), 63); row.name[63] = '\0'</c> (SparStats.cpp:178-182): the name up to its first NUL,
    /// cut to <see cref="SparPodium.NameMaxChars"/> UTF-8 bytes without splitting a character (the C++ could split one; docs/divergences.md).
    /// A lone surrogate counts as the 3 bytes .NET encodes it as. Null gives "".</summary>
    public static string CutName(string? name) => CutUtf8(name, SparPodium.NameMaxChars);

    /// <summary>The longest prefix of <paramref name="text"/> (up to its first NUL) whose UTF-8 encoding fits <paramref name="maxBytes"/>,
    /// never ending inside a character.</summary>
    public static string CutUtf8(string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        int nul = text.IndexOf('\0');
        ReadOnlySpan<char> s = nul >= 0 ? text.AsSpan(0, nul) : text.AsSpan();
        int bytes = 0, end = 0;
        while (end < s.Length)
        {
            Rune.DecodeFromUtf16(s[end..], out Rune rune, out int chars);
            int n = rune.Utf8SequenceLength;   // a lone surrogate decodes as U+FFFD: 3 bytes, as .NET's encoder writes it
            if (bytes + n > maxBytes)
                break;
            bytes += n;
            end += chars;
        }
        return end == text.Length ? text : s[..end].ToString();
    }
}
