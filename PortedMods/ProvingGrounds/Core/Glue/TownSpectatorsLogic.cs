using System.Globalization;
using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// Port of upstream/ProvingGrounds/plugin/src/TownSpectators.h/.cpp (Proving Grounds by Shutori, GPLv3), M8 task 8.2 (TW §2.7, CAP 5, 16, 19, spike
// S10 / S16): the C++ decisions the port-map row TownSpectators lists as still in glue (Begin's seat and audience search and its two log texts
// :360-447, Invite :120-139, the guest and pending-release lists and ReleaseGuest / TickPendingReleases :92-119, ReleaseCharacter / Release /
// AbandonWorldState :336-359, Tick's dt, maintenance gate, 180 s seat timeout and 3 s re-order :448-482, TickChatter's start / reset / cue loop
// :285-308, ScheduleChatterCycle's seated list :254-259, ChatterFighters :161-177, ReactToResult's seated list :311-318) over
// ITownSpectatorsHost. The pure parts (ChatterRandom, the lines, the winners, the cycle and the result sayings) are Core.TownSpectators, used as
// they are; the policies are Core.TownSpectatorPolicy and Core.ArenaPerformance. The glue (src/Town/TownSpectators.cs) binds the host to a world
// and to CharacterIds; Core never sees an engine type. A seat is its Meitou seat id ("<pillow key>@seat0"), a building an ArenaBuilding.
//
// Port choices (docs/divergences.md "TownSpectators (task 8.2)"): a guest is held through the world's hold registry with the owner Spectator (spike
// S10; the C++ saves and restores Hold / Passive / the move speed and clears the AI's goals, none of which has a counterpart beyond the hold); the
// seat's occupant and claims are read once per maintenance pass for every guest's seat (one scan of the character table, not one per guest); the
// sit order (Meitou's implicit SitAround Operate task, spike S16) walks to the seat itself, so the 3 s re-order is given again only when that task
// is no longer under way; a deferred release keeps the Spectator hold until the guest is healthy (the C++'s deferred native resume).
// Parts: this file = the state, the guests' maintenance, the chatter, the result lines and the releases; TownSpectatorsLogic.Begin.cs = Begin and
// Invite.

/// <summary>A spectator seat: its Meitou seat id, the building that holds it (the Arena Pillow's key) and where it is.</summary>
public readonly record struct SpectatorSeat(string Id, string Building, Vector3 Position);

/// <summary>Port only (quality review): who is a seat's occupant when several characters are on it (src/Engine/Seats.cs Occupants walks the
/// character table in slot order). An operator at the seat holds it (Meitou's operator task is the seat's user); a resting AI sitter on its place
/// counts only when no operator does; the first found otherwise keeps it.</summary>
public static class SpectatorSeatRules
{
    /// <summary>Whether a candidate (<paramref name="operator"/>: an operator at the seat, else a resting sitter) replaces the occupant found so
    /// far (<paramref name="hasOccupant"/>, <paramref name="occupantIsOperator"/>).</summary>
    public static bool OccupantWins(bool @operator, bool hasOccupant, bool occupantIsOperator) =>
        @operator ? !occupantIsOperator : !hasOccupant;
}

/// <summary>What TownSpectators.cpp reads of the world and does to it. The glue implements it over PgEngine, ArenaIdentity, the seat adapter
/// (src/Engine/Seats.cs) and the world's hold registry (src/Town/TownSpectators.cs); the fake is in Tests/Glue/TownSpectatorsLogicTests.cs.</summary>
public interface ITownSpectatorsHost<TId>
{
    // ---------- the world ----------
    /// <summary><c>ou-&gt;isPaused()</c>: always false in the port's step (a paused world runs no SlowWorld).</summary>
    bool IsPaused { get; }
    /// <summary><c>GetTickCount()</c>: the sim clock's milliseconds.</summary>
    uint NowMs { get; }
    /// <summary><c>ou-&gt;getTimeStamp_inGameHours().getTotalHours()</c>.</summary>
    double TotalHours { get; }
    /// <summary><c>PGLog::Debug</c>.</summary>
    void Debug(string line);

    // ---------- characters ----------
    TId None { get; }
    bool IsNull(TId c);
    /// <summary><c>c &amp;&amp; c-&gt;isValid()</c>.</summary>
    bool Valid(TId c);
    /// <summary>The C++ <c>Player(c)</c> (:56-61): one of the player's characters.</summary>
    bool IsPlayer(TId c);
    bool IsDead(TId c);
    bool IsUnconscious(TId c);
    /// <summary><c>c-&gt;isInCombatMode(true, true)</c>.</summary>
    bool InCombatMode(TId c);
    /// <summary><c>ArenaMedical::NeedsStabilization(c)</c>.</summary>
    bool NeedsStabilization(TId c);
    bool IsCarried(TId c);
    /// <summary><c>c-&gt;isCarryingSomething</c>.</summary>
    bool IsCarrying(TId c);
    /// <summary><c>c-&gt;getName()</c>.</summary>
    string Name(TId c);
    /// <summary><c>c-&gt;getGameData()-&gt;stringID</c> ("" for a stale id).</summary>
    string RecordId(TId c);
    /// <summary><c>c-&gt;getCurrentTownLocation()</c> as a Core TownKey.</summary>
    long CurrentTown(TId c);
    /// <summary>Port only (shared brief, census): the character belongs to the town faction 91 (Bloodstamp).</summary>
    bool IsTownFaction(TId c);
    /// <summary>The C++ <c>getPermajobCount() &gt; 0</c>'s stand-in (controller decision 8.2): a post or duty goal (Behaviour Post, Work or Turret,
    /// a town guard post, an engine mind, a town errand running). Meitou's own permanent-job list is empty for every town NPC.</summary>
    bool HasDuty(TId c);
    /// <summary>Port only: another owner of the hold registry holds the character (the aftercare, the external table).</summary>
    bool HeldByAnotherOwner(TId c);
    /// <summary><c>TownArena::IsFighter(c)</c>.</summary>
    bool IsTownFighter(TId c);
    /// <summary><c>seat-&gt;couldIOperate(actor)</c>'s actor half: the character may take the sit task at all (Meitou's task eligibility).</summary>
    bool CanSit(TId c);

    // ---------- buildings and seats ----------
    /// <summary>A held building as it is now (null: gone).</summary>
    PlacedBuilding? Resolve(ArenaBuildingRef held);
    /// <summary><c>ou-&gt;getObjectsWithinSphere(origin, radius, BUILDING, max, NULL)</c>.</summary>
    IReadOnlyList<PlacedBuilding>? BuildingsInSphere(Vector3 origin, float radius, int max);
    /// <summary><c>ou-&gt;getObjectsWithinSphere(origin, radius, CHARACTER, max, NULL)</c>.</summary>
    IReadOnlyList<TId>? CharactersInSphere(Vector3 origin, float radius, int max);
    /// <summary>The C++ <c>Seat(b)</c> (:71-81): the chair of an Arena Pillow 192 that stands complete; null for any other building.</summary>
    SpectatorSeat? SeatOf(ArenaBuilding building);
    /// <summary>Whether the seat still exists (the C++ <c>Seat(guest.seat.getBuilding())</c> not NULL).</summary>
    bool SeatExists(string seat);
    /// <summary>Reads each seat's state in one pass: <paramref name="occupants"/>[i] = the character sitting on <paramref name="seats"/>[i]
    /// (<c>seat-&gt;getOccupant()</c>: an operator at the seat, or an AI sitter resting on it; <see cref="None"/> when nobody), and
    /// <paramref name="claimed"/>[i] = someone else is on the way to it or (only when <paramref name="boardClaims"/>) a squad board claims it (the
    /// rest of <c>isFreeSlot</c>). An operator at the seat wins over a resting sitter on its place. One walk of the character table: Begin reads
    /// every pillow with the board claims; Tick reads only the seats of guests not yet seated, without them.</summary>
    void ReadSeats(IReadOnlyList<SpectatorSeat> seats, TId[] occupants, bool[] claimed, bool boardClaims);
    /// <summary>Whether <paramref name="c"/> sits on <paramref name="seat"/> now (<c>seat-&gt;getOccupant() == c</c> for a seated guest, O(1)).</summary>
    bool SittingOn(TId c, string seat);

    // ---------- orders (the glue's adapters) ----------
    /// <summary>The town AI is taken off the character (the hold registry, owner Spectator; spike S10). False: not held (a stale id, no hold members).</summary>
    bool Hold(TId c);
    /// <summary>The Spectator owner lets go; the NPC returns to its town AI only when no owner is left.</summary>
    void ReleaseHold(TId c);
    /// <summary><c>c-&gt;addOrder(seat, seat-&gt;getDefaultTask(), seat, ...)</c> + RUN (:475-477): the walk to the seat and the sit (spike S16). False:
    /// refused.</summary>
    bool Sit(TId c, string seat);
    /// <summary>Whether the sit task on <paramref name="seat"/> is still under way (walking there or sitting).</summary>
    bool OnItsWay(TId c, string seat);
    /// <summary>ReleaseGuest's <c>seat-&gt;stopOperating(actor)</c> + <c>removeJob(guest.task)</c> (:96-97): the sit task on that seat ended.</summary>
    void StandUp(TId c, string seat);

    // ---------- the match (chatter) ----------
    /// <summary><c>SparSession::IsActive()</c>.</summary>
    bool SparIsActive { get; }
    /// <summary><c>SparSession::GetMode() == MatchRules::ModeTeams1v1</c>.</summary>
    bool SparTeams1v1 { get; }
    TId SparActiveA { get; }
    TId SparActiveB { get; }
    int SparParticipantCount { get; }
    TId SparParticipant(int i);
    bool SparIsEliminated(TId c);
    /// <summary><c>speaker-&gt;say(line)</c> (CAP 16; the glue also writes the test-only PG-TOWN say line, by "crowd").</summary>
    void Say(TId c, string line);
}

/// <summary>TownSpectators.cpp's state and decisions for one world. Sim thread, serial steps only.</summary>
public sealed partial class TownSpectatorsLogic<TId> where TId : notnull
{
    /// <summary>C++: TownSpectators.cpp:29-36 <c>struct Guest</c> (the saved hold / passive / speed and the seat's task have no port: the hold
    /// registry and the sit adapter stand for them). <see cref="Occupant"/> is port only: the seat's occupant read at this maintenance pass.</summary>
    public struct Guest
    {
        public TId Actor;
        public SpectatorSeat Seat;
        public bool Seated;
        public float LastOrder;
        public TId Occupant;
        /// <summary>Port only (quality review): sit orders refused in a row (<see cref="ITownSpectatorsHost{TId}.Sit"/> false); released at
        /// <see cref="MaxSitRefusals"/>.</summary>
        public int SitRefusals;
    }

    /// <summary>C++: TownSpectators.cpp:39-43 <c>struct ChatterCue</c>.</summary>
    struct ChatterCue
    {
        public TId Speaker;
        public float Due;
    }

    /// <summary>C++ <c>Begin</c>'s search radius and caps (:368, :395).</summary>
    public const float SearchRadius = 2500f;
    public const int BuildingMax = 2048, CharacterMax = 512;
    /// <summary>C++ Tick (:467, :473): the seat timeout and the re-order interval, seconds.</summary>
    public const float SeatTimeout = 180f, ReorderSeconds = 3f;
    /// <summary>Port only (quality review): a guest whose sit order is refused this many times in a row is released (the C++'s addOrder is
    /// silent; without it the NPC would stand held for the 180 s timeout).</summary>
    public const int MaxSitRefusals = 2;

    readonly TId none;
    readonly IEqualityComparer<TId> eq = EqualityComparer<TId>.Default;

    // ---------- the file-level state (TownSpectators.cpp:37-54) ----------
    readonly List<Guest> guests = [];
    readonly List<TId> pendingReleases = [];
    readonly List<ChatterCue> chatterCues = [];
    ArenaBuildingRef? activeArena;
    TId lastChatterSpeaker;
    float elapsed;
    float nextChatterCycle;
    uint lastTick, lastMaintenance;
    uint nextAudience;
    uint chatterSeed;
    bool chatterActive;
    ArenaPerformance.Policy activePolicy = new();

    // ---------- reusable buffers (no allocation per tick or poll: up to 120 guests) ----------
    readonly List<SpectatorSeat> seatBuffer = [];
    TId[] occupantBuffer = new TId[TownSpectatorPolicyCap];
    bool[] claimedBuffer = new bool[TownSpectatorPolicyCap];
    readonly List<TId> seated = [];
    readonly List<string> fighterNames = [];
    const int TownSpectatorPolicyCap = 120;

    /// <summary>Port only: guests whose maintenance step threw (each released and forgotten; the first three logged).</summary>
    int guestFailures;
    public int GuestFailures => guestFailures;
    /// <summary>Port only (test verb, divergence row): candidates Begin left out for the permanent-job stand-in (<see cref="ITownSpectatorsHost{TId}.HasDuty"/>),
    /// by another owner's hold, and invites the hold refused, at the last Begin.</summary>
    public int ExcludedByDuty { get; private set; }
    public int ExcludedByHold { get; private set; }
    public int HoldRefused { get; private set; }
    /// <summary>Port only (quality review): sit orders refused since the last Begin (the first one logged).</summary>
    public int SitRefused { get; private set; }
    bool sitRefusalLogged;

    public TownSpectatorsLogic(TId none)
    {
        this.none = none;
        lastChatterSpeaker = none;
    }

    public IReadOnlyList<Guest> Guests => guests;
    public IReadOnlyList<TId> PendingReleases => pendingReleases;
    public ArenaBuildingRef? ActiveArena => activeArena;
    public float Elapsed => elapsed;
    public bool ChatterActive => chatterActive;
    public ArenaPerformance.Policy ActivePolicy => activePolicy;
    public int CueCount => chatterCues.Count;

    /// <summary>Whether <paramref name="c"/> waits in the pending-release list (its native resume deferred while unhealthy).</summary>
    public bool IsPending(TId c)
    {
        for (int i = 0; i < pendingReleases.Count; ++i) if (eq.Equals(pendingReleases[i], c)) return true;
        return false;
    }

    // C++: TownSpectators.cpp:62-66
    bool Healthy(ITownSpectatorsHost<TId> h, TId c) =>
        !h.IsNull(c) && h.Valid(c) && !h.IsDead(c) && !h.IsUnconscious(c) && !h.InCombatMode(c) && !h.NeedsStabilization(c) &&
        !h.IsCarried(c) && !h.IsCarrying(c);

    // C++: TownSpectators.cpp:88-91
    TownSpectatorPolicy.ReleaseDisposition ReleaseDisposition(ITownSpectatorsHost<TId> h, TId c)
    {
        if (h.IsNull(c) || !h.Valid(c)) return TownSpectatorPolicy.ReleaseDisposition.ForgetRelease;
        return TownSpectatorPolicy.ReleaseFor(true, h.IsDead(c), h.IsPlayer(c), Healthy(h, c));
    }

    // C++: TownSpectators.cpp:92-105. The C++ returns at once for an invalid character; the port still lets the Spectator hold go (a stale entry).
    // A deferred guest keeps the hold until TickPendingReleases resumes it (the C++ defers only the native AI's resume).
    void ReleaseGuest(ITownSpectatorsHost<TId> h, in Guest guest)
    {
        TId c = guest.Actor;
        if (h.IsNull(c) || !h.Valid(c)) { h.ReleaseHold(c); return; }
        h.StandUp(c, guest.Seat.Id);   // nothing when its sit task is no longer on this seat
        var disposition = ReleaseDisposition(h, c);
        if (disposition == TownSpectatorPolicy.ReleaseDisposition.DeferNativeResume) pendingReleases.Add(c);
        else h.ReleaseHold(c);   // ResumeNativeNow; ForgetRelease (dead, a player: nothing to resume, the hold goes)
    }

    // C++: TownSpectators.cpp:106-114
    void TickPendingReleases(ITownSpectatorsHost<TId> h)
    {
        for (int i = 0; i < pendingReleases.Count;)
        {
            TId c = pendingReleases[i];
            var disposition = ReleaseDisposition(h, c);
            if (disposition == TownSpectatorPolicy.ReleaseDisposition.DeferNativeResume) { ++i; continue; }
            h.ReleaseHold(c);   // ResumeNativeNow: the AI back; ForgetRelease: the hold goes
            pendingReleases.RemoveAt(i);
        }
    }

    // C++: TownSpectators.cpp:147-152
    void ResetChatter()
    {
        chatterCues.Clear();
        lastChatterSpeaker = none;
        nextChatterCycle = 0.0f;
        chatterActive = false;
    }

    // C++: TownSpectators.cpp:153-160 (the occupant is the guest's read of this pass)
    bool IsSeatedGuest(ITownSpectatorsHost<TId> h, TId character)
    {
        if (h.IsNull(character)) return false;
        for (int i = 0; i < guests.Count; ++i)
        {
            if (!eq.Equals(guests[i].Actor, character) || !guests[i].Seated) continue;
            return h.SeatExists(guests[i].Seat.Id) && eq.Equals(guests[i].Occupant, character);
        }
        return false;
    }

    // IsSeatedGuest for guest i itself (the guests are distinct): no search of the list.
    bool SeatedAt(ITownSpectatorsHost<TId> h, int i)
    {
        Guest g = guests[i];
        return g.Seated && !h.IsNull(g.Actor) && h.SeatExists(g.Seat.Id) && eq.Equals(g.Occupant, g.Actor);
    }

    // C++: TownSpectators.cpp:161-177 (the names: Core.TownSpectators reads fighters as names)
    List<string> ChatterFighters(ITownSpectatorsHost<TId> h)
    {
        fighterNames.Clear();
        if (!h.SparIsActive) return fighterNames;
        if (h.SparTeams1v1)
        {
            TId a = h.SparActiveA, b = h.SparActiveB;
            if (!h.IsNull(a) && h.Valid(a) && !h.IsDead(a) && !h.IsUnconscious(a)) fighterNames.Add(h.Name(a));
            if (!h.IsNull(b) && h.Valid(b) && !h.IsDead(b) && !h.IsUnconscious(b)) fighterNames.Add(h.Name(b));
            return fighterNames;
        }
        for (int i = 0; i < h.SparParticipantCount; ++i)
        {
            TId f = h.SparParticipant(i);
            if (!h.IsNull(f) && h.Valid(f) && !h.IsDead(f) && !h.IsUnconscious(f) && !h.SparIsEliminated(f)) fighterNames.Add(h.Name(f));
        }
        return fighterNames;
    }

    // The seated guests in guest order (:255-259 / :313-317) into the reused list, and the last speaker's index there (-1: not in it).
    int CollectSeated(ITownSpectatorsHost<TId> h)
    {
        seated.Clear();
        for (int i = 0; i < guests.Count; ++i)
            if (SeatedAt(h, i)) seated.Add(guests[i].Actor);
        if (h.IsNull(lastChatterSpeaker)) return -1;
        for (int i = 0; i < seated.Count; ++i) if (eq.Equals(seated[i], lastChatterSpeaker)) return i;
        return -1;
    }

    // C++: TownSpectators.cpp:254-284 (Core.TownSpectators.ScheduleChatterCycle decides; the glue appends the cues)
    void ScheduleChatterCycle(ITownSpectatorsHost<TId> h)
    {
        int last = CollectSeated(h);
        var cycle = TownSpectators.ScheduleChatterCycle(activePolicy, seated.Count, last, elapsed, ref chatterSeed, h.NowMs);
        nextChatterCycle = cycle.NextChatterCycle;
        for (int i = 0; i < cycle.Cues.Count; ++i)
            chatterCues.Add(new ChatterCue { Speaker = seated[cycle.Cues[i].SeatedIndex], Due = cycle.Cues[i].Due });
    }

    // C++: TownSpectators.cpp:285-308
    void TickChatter(ITownSpectatorsHost<TId> h)
    {
        if (!activePolicy.ChatterEnabled) return;
        if (!h.SparIsActive)
        {
            ResetChatter();
            return;
        }
        if (!chatterActive)
        {
            chatterActive = true;
            chatterSeed = TownSpectators.ChatterStartSeed(h.NowMs, guests.Count);
            ScheduleChatterCycle(h);
        }
        else if (elapsed >= nextChatterCycle)
        {
            ScheduleChatterCycle(h);
        }
        for (int i = 0; i < chatterCues.Count;)
        {
            if (elapsed < chatterCues[i].Due) { ++i; continue; }
            TId speaker = chatterCues[i].Speaker;
            if (IsSeatedGuest(h, speaker))
            {
                h.Say(speaker, TownSpectators.ChatterLine(ChatterFighters(h), ref chatterSeed, h.NowMs));
                lastChatterSpeaker = speaker;
            }
            chatterCues.RemoveAt(i);
        }
    }

    // Port only: the occupant of each guest's seat, kept on the guest for this pass. A seated guest checks its own seat (O(1): it sits there or
    // the C++ releases it as stood up, whoever took the seat); only while some guest is not yet seated is the character table walked, for those
    // guests' seats alone and without the board claims (Tick never reads them).
    void ReadGuestSeats(ITownSpectatorsHost<TId> h)
    {
        seatBuffer.Clear();
        for (int i = 0; i < guests.Count; ++i)
        {
            var g = guests[i];
            if (g.Seated) { g.Occupant = h.SittingOn(g.Actor, g.Seat.Id) ? g.Actor : none; guests[i] = g; }
            else seatBuffer.Add(g.Seat);
        }
        if (seatBuffer.Count == 0) return;
        EnsureBuffers(seatBuffer.Count);
        h.ReadSeats(seatBuffer, occupantBuffer, claimedBuffer, boardClaims: false);
        for (int i = 0, k = 0; i < guests.Count; ++i)
        {
            var g = guests[i];
            if (g.Seated) continue;
            g.Occupant = occupantBuffer[k++];
            guests[i] = g;
        }
    }

    void EnsureBuffers(int n)
    {
        if (occupantBuffer.Length < n) occupantBuffer = new TId[n];
        if (claimedBuffer.Length < n) claimedBuffer = new bool[n];
        for (int i = 0; i < n; ++i) { occupantBuffer[i] = none; claimedBuffer[i] = false; }
    }

    // ---------- the public calls (TownSpectators.h) ----------

    // C++: TownSpectators.cpp:311-335
    public void ReactToResult(ITownSpectatorsHost<TId> h, SparPodium.Snapshot result)
    {
        if (!activePolicy.ChatterEnabled || result.Outcome == SparPodium.OutcomeKind.Stopped || guests.Count == 0) return;
        ReadGuestSeats(h);   // port only: the occupants as they are now (no maintenance pass may have run since the last read)
        int last = CollectSeated(h);
        if (seated.Count == 0) return;
        var said = TownSpectators.ReactToResult(activePolicy, result, guestCount: guests.Count, seatedCount: seated.Count, lastSpeakerIndex: last,
            ref chatterSeed, h.NowMs);
        for (int i = 0; i < said.Count; ++i)
        {
            TId speaker = seated[said[i].SeatedIndex];
            h.Say(speaker, said[i].Line);
            lastChatterSpeaker = speaker;
        }
    }

    // C++: TownSpectators.cpp:336-350. The C++ leaves a pending entry's resume to the caller (corpse cleanup, task 8.6); the port also lets the
    // Spectator hold go, so the caller's own hold is the only one left.
    public void ReleaseCharacter(ITownSpectatorsHost<TId> h, TId character)
    {
        if (h.IsNull(character)) return;
        for (int i = 0; i < guests.Count; ++i)
            if (eq.Equals(guests[i].Actor, character))
            {
                ReleaseGuest(h, guests[i]);
                guests.RemoveAt(i);
                break;
            }
        bool wasPending = false;
        for (int i = 0; i < pendingReleases.Count;)
        {
            if (eq.Equals(pendingReleases[i], character)) { pendingReleases.RemoveAt(i); wasPending = true; }
            else ++i;
        }
        if (wasPending) h.ReleaseHold(character);
    }

    // C++: TownSpectators.cpp:351-355
    public void Release(ITownSpectatorsHost<TId> h)
    {
        for (int i = 0; i < guests.Count; ++i) ReleaseGuest(h, guests[i]);
        if (guests.Count > 0) h.Debug("Proving Grounds: arena spectators released from arena control");
        guests.Clear(); ResetChatter(); activeArena = null; lastTick = 0; lastMaintenance = 0;
    }

    /// <summary>Port only: a Release that threw part way (the glue's backstop has let every Spectator hold go): the lists are dropped as Release
    /// would leave them, the pending ones with them (their holds are gone).</summary>
    public void ResetAfterFailedRelease()
    {
        guests.Clear(); pendingReleases.Clear(); ResetChatter(); activeArena = null; lastTick = 0; lastMaintenance = 0;
    }

    // C++: TownSpectators.cpp:356-359 (no actor access; the hold registry is emptied by TownArena on the world change)
    public void AbandonWorldState()
    {
        guests.Clear(); pendingReleases.Clear(); ResetChatter(); activeArena = null; lastTick = 0;
        lastMaintenance = 0; elapsed = 0; nextAudience = 0; activePolicy = new ArenaPerformance.Policy();
        guestFailures = 0;
    }

    // C++: TownSpectators.cpp:448-482. Port only: a guest whose step throws is released (its own try) and forgotten; the others go on.
    public void Tick(ITownSpectatorsHost<TId> h)
    {
        TickPendingReleases(h);
        if (guests.Count == 0) return;
        uint now = h.NowMs;
        float dt = lastTick != 0 ? (now - lastTick) / 1000.0f : 0;
        lastTick = now;
        if (h.IsPaused) return;
        elapsed += dt;
        if (lastMaintenance != 0 && activePolicy.MaintenanceMs != 0 &&
            now - lastMaintenance < activePolicy.MaintenanceMs) return;
        lastMaintenance = now;
        PlacedBuilding? arena = activeArena is { } a ? h.Resolve(a) : null;
        ReadGuestSeats(h);
        for (int i = 0; i < guests.Count;)
        {
            bool keep;
            try { keep = Maintain(h, i, arena); }
            catch (Exception e)
            {
                TId bad = guests[i].Actor;
                if (++guestFailures <= 3)
                    h.Debug("Proving Grounds: spectator guest failed error=" + e.GetType().Name + ": " + e.Message);
                try { ReleaseGuest(h, guests[i]); } catch { try { h.ReleaseHold(bad); } catch { } }
                keep = false;
            }
            if (keep) ++i;
            else guests.RemoveAt(i);
        }
        TickChatter(h);
    }

    // C++: TownSpectators.cpp:462-479 (one guest; false: released, erase it)
    bool Maintain(ITownSpectatorsHost<TId> h, int i, PlacedBuilding? arena)
    {
        Guest guest = guests[i];
        TId c = guest.Actor;
        bool seatOk = h.SeatExists(guest.Seat.Id);
        bool seatedNow = seatOk && !h.IsNull(c) && eq.Equals(guest.Occupant, c);
        bool taken = seatOk && !h.IsNull(guest.Occupant) && !seatedNow;
        if (arena is null || !Healthy(h, c) || h.IsPlayer(c) || !seatOk || taken ||
            !TownKey.Same(h.CurrentTown(c), arena.Value.Town) || (guest.Seated && !seatedNow) || (!seatedNow && elapsed >= SeatTimeout))
        {
            ReleaseGuest(h, guest);
            return false;
        }
        if (seatedNow)
        {
            guest.Seated = true;   // the C++'s Passive standing order: none (the hold stands for it)
        }
        else if (elapsed - guest.LastOrder >= ReorderSeconds)
        {
            guest.LastOrder = elapsed;
            if (!h.OnItsWay(c, guest.Seat.Id))
            {
                if (h.Sit(c, guest.Seat.Id)) guest.SitRefusals = 0;   // the sit task walks at full speed itself (the C++'s RUN)
                else
                {
                    // port only (quality review): a refused start (Meitou's TaskStart: asleep, fleeing, carried, not eligible) is counted, the
                    // first of each Begin logged, and the guest released after MaxSitRefusals in a row
                    ++SitRefused;
                    if (!sitRefusalLogged)
                    {
                        sitRefusalLogged = true;
                        h.Debug("Proving Grounds: spectator sit order refused seat=" + guest.Seat.Id + " (a guest is released after " +
                            MaxSitRefusals.ToString(CultureInfo.InvariantCulture) + " refusals in a row)");
                    }
                    if (++guest.SitRefusals >= MaxSitRefusals)
                    {
                        ReleaseGuest(h, guest);
                        return false;
                    }
                }
            }
        }
        guests[i] = guest;
        return true;
    }
}
