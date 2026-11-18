using System.Globalization;

namespace ProvingGrounds.Core.Glue;

// The decisions of upstream/ProvingGrounds/plugin/src/SparSession.cpp (Proving Grounds by Shutori, GPLv3), task 3.3, over one host interface so
// they run without the game (E3). src/Game/SparSession.cs is the public surface (SparSession.h) and binds the host through PgEngine and the other
// subsystems (src/Game/PgSparHost.cs, the E1 table of engine calls in SparSession.cs's header); Tests/Glue/SparSessionLogicTests.cs runs the same
// code over a fake. Every function below is the C++ function of the same name, in the C++'s order, with its line range.

/// <summary>C++: SparSession.h:12-18 <c>StopReason</c> (the int CombatBalanceLog writes as <c>stop_reason</c>).</summary>
public enum SparStopReason { Manual = 0, Ko = 1, Death = 2, Invalid = 3 }

/// <summary>What SparSession and KOWatcher read of and do to the game and the other modules (one member per C++ call; the E1 table in
/// src/Game/SparSession.cs says what replaces each in Meitou). <typeparamref name="TId"/>: a character handle (the C++ <c>Character*</c>).</summary>
public interface ISparHost<TId>
{
    // ---------- engine reads ----------
    /// <summary>The "no character" id (a C++ null pointer).</summary>
    TId None { get; }
    bool IsNone(TId c);
    /// <summary><c>c->isValid()</c>.</summary>
    bool Exists(TId c);
    /// <summary><c>c->isDead()</c>.</summary>
    bool IsDead(TId c);
    /// <summary><c>c->isUnconcious()</c> (playing dead counts, crawling does not: CAP 3).</summary>
    bool IsUnconscious(TId c);
    /// <summary><c>c->getName()</c> of a non-null character.</summary>
    string Name(TId c);
    /// <summary><c>c->getPosition()</c>; false for a stale id.</summary>
    bool TryGetPosition(TId c, out float x, out float y, out float z);
    /// <summary><c>GetTickCount()</c> (the port's PgClock ms).</summary>
    uint NowMs { get; }

    // ---------- the Teams 1v1 bench (C++ TownAftercare::Reserve/ReleaseBenchOrders, setStandingOrder, CharMovement) ----------
    /// <summary><c>TownAftercare::ControlAvailable()</c>: the bench can hold NPCs (the port: spike S10, Engaged).</summary>
    bool BenchControlAvailable { get; }
    /// <summary><c>TownAftercare::ReserveBenchOrders(c->getOrdersReciever())</c>: false when it cannot be held.</summary>
    bool ReserveBench(TId c);
    /// <summary><c>TownAftercare::ReleaseBenchOrders(receiver)</c>.</summary>
    void ReleaseBench(TId c);
    /// <summary><c>setStandingOrder(PASSIVE, true)</c>, <c>(HOLD, hold)</c>, <c>(AGG, false)</c> (SparSession.cpp:235-237).</summary>
    void SetBenchStance(TId c, bool hold);
    /// <summary><c>movement &amp;&amp; movement->isCurrentlyMoving()</c>, and then <c>movement->destination</c>.</summary>
    bool TryGetMoveDestination(TId c, out float x, out float y, out float z);
    /// <summary><c>clearAllAIGoals()</c>, <c>addOrder(MOVE_CUS_ORDERED, pos)</c>, <c>movement->setDestination</c>, <c>setDestination</c>
    /// (SparSession.cpp:247-250).</summary>
    void Recall(TId c, float x, float y, float z);
    /// <summary><c>lookatPosition(pos, true)</c>.</summary>
    void LookAt(TId c, float x, float y, float z);

    // ---------- FightStarter ----------
    void RememberMatchFighters(TId[] fighters, int count);
    void EngageMatch(TId[] fighters, MatchRules.MatchTeam[] teams, int count, MatchRules.MatchMode mode);
    void DisengageMatch(TId[] fighters, int count);
    void RetargetMatchAfterElimination(TId[] allFighters, int allCount, TId[]? remainingFighters, MatchRules.MatchTeam[]? remainingTeams,
        int remainingCount, MatchRules.MatchMode mode);
    void KeepEngaged(TId a, TId b);
    void KeepEngagedMatch(TId[] fighters, MatchRules.MatchTeam[] teams, int count, MatchRules.MatchMode mode);

    // ---------- ArenaIngress (the Teams 1v1 walk-in, M4 task 4.3) ----------
    TId GetWalkInFighter();
    void ClearWalkIn();
    bool IsWalkInPending();
    bool BeginWalkIn(TId c, MatchRules.MatchTeam team);
    void TickWalkIn();
    bool IsWalkInArrived();

    // ---------- TownArena (M7) ----------
    bool TownIsAftercare();
    void TownCancelPlannedNpcBout(string reason);
    void TownOnMatchStarted();
    bool TownOwnsMatch();
    bool TownIsPlayerMatch();
    float TownActiveMarksMultiplier();
    int TownActiveMarksBonus();
    void TownOnMatchResult(SparPodium.Snapshot snapshot);
    void TownOnMatchAborted();

    // ---------- ResultsUI (M4) ----------
    void ResultsCancel();
    void ResultsClose();
    void ResultsScheduleShow(float delaySeconds);

    // ---------- SparStats ----------
    void StatsResetForMatch();
    void StatsFreeze(SparPodium.OutcomeKind outcome, MatchRules.MatchMode mode, TId lastStanding);
    /// <summary><c>GetMutableSnapshot()</c> = <c>GetSnapshot()</c> (one object).</summary>
    SparPodium.Snapshot StatsSnapshot();
    void StatsOnEliminated(TId c);

    // ---------- LeaderboardStore (task 5.1: Core/Glue/LeaderboardStoreLogic.cs, src/Game/LeaderboardStore.cs) ----------
    void ApplyFromSnapshot(SparPodium.Snapshot snapshot, LeaderboardData.Kind kind, float marksMultiplier, int marksTeamBonus);

    // ---------- CombatBalanceLog ----------
    void CombatLogStart(MatchRules.MatchMode mode, TId[] fighters, MatchRules.MatchTeam[] teams, int count);
    void CombatLogFinish(SparPodium.Snapshot snapshot, SparStopReason reason);
    void CombatLogAbort(string reason);
    void CombatLogAbandon();

    // ---------- PrisonerUtil (M6) ----------
    /// <summary><c>PrisonerUtil::BeginReturnToCages(returnStatus)</c>: the status text ("" for none).</summary>
    string BeginReturnToCages();

    // ---------- port only ----------
    /// <summary>A save the save safety prepared (or a released autosave) has not been captured yet (task 2.5: nothing may start a match in
    /// that window; the C++ prepared and saved in one call).</summary>
    bool SaveInFlight { get; }
}

/// <summary>
/// SparSession.cpp's state and decisions: the running match (mode, fighters, teams, who is out), the Teams 1v1 bench and promotion, the start
/// with its refusals and the stop with its results. One object per world. The game is reached only through the <see cref="ISparHost{TId}"/>
/// given to each call. Sim thread, serial steps.
/// </summary>
public sealed class SparSessionLogic<TId> where TId : IEquatable<TId>
{
    /// <summary>C++: SparSession.cpp:37-42 <c>BenchSlot</c> (<c>receiver</c> = reserved for the slot's fighter).</summary>
    struct BenchSlot
    {
        public bool Reserved;
        public float X, Y, Z;
        public uint LastRecall;
    }

    bool active;                                                    // C++ :32 g_active
    MatchRules.MatchMode mode = MatchRules.MatchMode.TeamAvB;       // C++ :33 g_mode
    TId[] fighters = [];                                            // C++ :34 g_fighters (first fighterCount entries)
    MatchRules.MatchTeam[] teams = [];                              // C++ :35 g_teams
    int fighterCount;
    bool[] eliminated = [];                                         // C++ :36 g_eliminated
    int eliminatedCount;
    BenchSlot[] bench = [];                                         // C++ :43 g_bench
    int benchCount;
    bool[] retired = [];                                            // C++ :44 g_retired
    int retiredCount;
    string status = "Idle";                                         // C++ :45 g_status
    TId activeA, activeB, interBoutWaiting;                         // C++ :46-48
    SparPodium.OutcomeKind pendingOutcome = SparPodium.OutcomeKind.Stopped;   // C++ :49
    MatchRules.MatchEndKind pendingEndKind = MatchRules.MatchEndKind.None;    // C++ :50
    TId pendingLastStanding;                                        // C++ :51
    bool hasPendingOutcome;                                         // C++ :52

    readonly TId[] one = new TId[1];                                // the C++ &c single-fighter calls, reused
    readonly TId[] pair = new TId[2];                               // EngageActivePair's pair, reused
    readonly MatchRules.MatchTeam[] pairTeams = [MatchRules.MatchTeam.TeamA, MatchRules.MatchTeam.TeamB];   // per instance: nobody else can write it
    readonly Action<string>? ownLog;                                // the port's own diagnostic lines (Meitou's log), no C++ text
    readonly Dictionary<string, int> failures = new(StringComparer.Ordinal);   // per step name: how often it threw in this world
    int failureCount;

    /// <param name="none">The "no character" id.</param>
    /// <param name="ownLog">Where the port's own lines go (once per step name per world: a step of a start undo, stop or abort that threw).</param>
    public SparSessionLogic(TId none, Action<string>? ownLog = null)
    {
        activeA = activeB = interBoutWaiting = pendingLastStanding = none;
        this.ownLog = ownLog;
    }

    /// <summary>Steps of a start undo, stop or abort that threw and were skipped in this world (the F10 row).</summary>
    public int FailureCount => failureCount;

    /// <summary>Counts the failure; logs the first one of each step name in this world.</summary>
    void LogFailure(string what, Exception e)
    {
        failureCount++;
        failures.TryGetValue(what, out int n);
        failures[what] = n + 1;
        if (n > 0)
            return;
        ownLog?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"SparSession {what} failed (logged once per step per world; the rest of the stop ran): {e.GetType().Name}: {e.Message}"));
    }

    /// <summary>Runs one step of a stop / abort under its own try/catch (port-only, note 13 of task 3.3): a throw there never keeps the
    /// fighters from being disengaged or the session from being cleared.</summary>
    void Guarded(string what, Action step)
    {
        try { step(); }
        catch (Exception e) { LogFailure(what, e); }
    }

    // ---------- anonymous namespace of the C++ ----------

    // C++: SparSession.cpp:54-59
    static string CharName(ISparHost<TId> h, TId c)
    {
        if (h.IsNone(c))
            return "(none)";
        return h.Name(c);
    }

    // C++: SparSession.cpp:61-69
    int FindParticipantIndex(TId c)
    {
        for (int i = 0; i < fighterCount; ++i)
        {
            if (fighters[i].Equals(c))
                return i;
        }
        return -1;
    }

    // C++: SparSession.cpp:71-74. The down test of KOWatcher.cpp (conscious = not dead and not unconscious) with isValid.
    static bool IsLiving(ISparHost<TId> h, TId c) => !h.IsNone(c) && h.Exists(c) && !h.IsDead(c) && !h.IsUnconscious(c);

    // C++: SparSession.cpp:76-87
    MatchRules.MatchParticipant MakeParticipant(ISparHost<TId> h, int index)
    {
        TId c = fighters[index];
        var p = new MatchRules.MatchParticipant();
        p.Id = index;
        p.Team = teams[index];
        p.Conscious = IsLiving(h, c);
        p.Dead = !h.IsNone(c) && h.IsDead(c);
        p.Eliminated = index >= 0 && index < eliminatedCount && eliminated[index];
        return p;
    }

    // C++: SparSession.cpp:89-96
    void ReleaseBench(ISparHost<TId> h, int index, string reason)
    {
        if (index < 0 || index >= benchCount || !bench[index].Reserved) return;
        h.ReleaseBench(fighters[index]);
        bench[index].Reserved = false;
        PGLog.Debug(string.Create(CultureInfo.InvariantCulture, $"Proving Grounds: Teams 1v1 bench released slot={index + 1} reason={reason}"));
    }

    // C++: SparSession.cpp:97-99. Port-only: each slot under its own try/catch (one throwing release never keeps the others held).
    void ReleaseAllBenches(ISparHost<TId> h)
    {
        for (int i = 0; i < benchCount; ++i)
        {
            int index = i;
            Guarded("bench release", () => ReleaseBench(h, index, "match-ended"));
        }
    }

    // C++: SparSession.cpp:100-129
    void MarkEliminated(ISparHost<TId> h, TId c)
    {
        int index = FindParticipantIndex(c);
        if (index < 0)
            return;
        if (index >= eliminatedCount)
            ResizeEliminated(index + 1);
        if (eliminated[index]) return;
        eliminated[index] = true;
        if (mode == MatchRules.MatchMode.Teams1v1)
        {
            if (h.GetWalkInFighter().Equals(c))
            {
                h.ClearWalkIn();
                PGLog.Debug("Proving Grounds: Teams 1v1 walk-in cancelled for eliminated fighter");
            }
            ReleaseBench(h, index, "eliminated");
            // Restore original combat orders once, before a medic takes over.
            // Final match cleanup must not cancel a carried/bed-bound patient.
            one[0] = c;
            h.DisengageMatch(one, 1);
            retired[index] = true;
            if (interBoutWaiting.Equals(c)) interBoutWaiting = h.None;
            TId other = c.Equals(activeA) ? activeB : c.Equals(activeB) ? activeA : h.None;
            int otherIndex = FindParticipantIndex(other);
            if (IsLiving(h, other) && otherIndex >= 0 && !eliminated[otherIndex] &&
                !other.Equals(h.GetWalkInFighter()))
            {
                one[0] = other;
                h.RetargetMatchAfterElimination(one, 1, null, null, 0, mode);
                interBoutWaiting = other;
            }
            PGLog.Debug("Proving Grounds: Teams 1v1 fighter permanently eliminated; released for care name=" + CharName(h, c));
        }
    }

    void ResizeEliminated(int n)
    {
        if (eliminated.Length < n) Array.Resize(ref eliminated, n);
        for (int i = eliminatedCount; i < n; ++i) eliminated[i] = false;
        eliminatedCount = n;
    }

    // C++: SparSession.cpp:131-136
    bool IsEliminatedIndex(int index)
    {
        return index >= 0 &&
            index < eliminatedCount &&
            eliminated[index];
    }

    // C++: SparSession.cpp:138-149
    static bool AllFightersValidAndConscious(ISparHost<TId> h, TId[] fighters, int count)
    {
        for (int i = 0; i < count; ++i)
        {
            TId c = fighters[i];
            if (h.IsNone(c) || !h.Exists(c))
                return false;
            if (h.IsDead(c) || h.IsUnconscious(c))
                return false;
        }
        return true;
    }

    // C++: SparSession.cpp:151-162
    static bool HasDuplicateFighters(TId[] fighters, int count)
    {
        for (int i = 0; i < count; ++i)
        {
            for (int j = i + 1; j < count; ++j)
            {
                if (fighters[i].Equals(fighters[j]))
                    return true;
            }
        }
        return false;
    }

    // C++: SparSession.cpp:164-186
    bool ValidateTeamMode(MatchRules.MatchTeam[] teams, int count)
    {
        bool hasA = false;
        bool hasB = false;
        for (int i = 0; i < count; ++i)
        {
            if (teams[i] != MatchRules.MatchTeam.TeamA && teams[i] != MatchRules.MatchTeam.TeamB)
            {
                status = "Team modes require TeamA or TeamB";
                return false;
            }
            if (teams[i] == MatchRules.MatchTeam.TeamA)
                hasA = true;
            else
                hasB = true;
        }
        if (!hasA || !hasB)
        {
            status = "Need at least one fighter per team";
            return false;
        }
        return true;
    }

    // C++: SparSession.cpp:188-196
    TId FirstOfTeam(ISparHost<TId> h, MatchRules.MatchTeam team)
    {
        for (int i = 0; i < fighterCount; ++i)
        {
            if (teams[i] == team)
                return fighters[i];
        }
        return h.None;
    }

    // C++: SparSession.cpp:198-212
    TId NextConsciousAfter(ISparHost<TId> h, TId current, MatchRules.MatchTeam team)
    {
        int start = FindParticipantIndex(current);
        int n = fighterCount;
        for (int i = start + 1; i < n; ++i)
        {
            if (teams[i] != team)
                continue;
            if (IsEliminatedIndex(i))
                continue;
            if (IsLiving(h, fighters[i]))
                return fighters[i];
        }
        return h.None;
    }

    /// <summary>C++ SparSession.cpp:236, :242: a bench fighter within 8 units of its slot (<c>squaredLength() &lt;= 64.0f</c>), the Ogre
    /// float sum x*x + y*y + z*z.</summary>
    public static float SquaredDistance(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = ax - bx, dy = ay - by, dz = az - bz;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>C++ SparSession.cpp:236, :242-243: the bench radius test (8 units).</summary>
    public const float BenchRadiusSquared = 64.0f;

    /// <summary>C++ SparSession.cpp:239: a bench fighter is checked again at most every 2 s.</summary>
    public const uint BenchRecallMs = 2000;

    // C++: SparSession.cpp:214-257
    void ParkBenchFighters(ISparHost<TId> h)
    {
        if (mode != MatchRules.MatchMode.Teams1v1) return;
        uint now = h.NowMs;
        for (int i = 0; i < fighterCount; ++i)
        {
            TId c = fighters[i];
            if (h.IsNone(c) || !h.Exists(c) || h.IsDead(c) || h.IsUnconscious(c) ||
                IsEliminatedIndex(i) ||
                ((c.Equals(activeA) || c.Equals(activeB)) && !c.Equals(interBoutWaiting)))
            {
                ReleaseBench(h, i, "not-waiting");
                continue;
            }
            ref BenchSlot slot = ref bench[i];
            bool issue = false;
            h.TryGetPosition(c, out float px, out float py, out float pz);
            if (!slot.Reserved)
            {
                if (!h.ReserveBench(c)) continue;
                slot.Reserved = true;
                slot.X = px; slot.Y = py; slot.Z = pz;
                issue = true;
                PGLog.Debug("Proving Grounds: Teams 1v1 bench reserved name=" + h.Name(c));
            }
            h.SetBenchStance(c, SquaredDistance(px, py, pz, slot.X, slot.Y, slot.Z) <= BenchRadiusSquared);
            if (!issue && unchecked(now - slot.LastRecall) >= BenchRecallMs)
            {
                float offset = SquaredDistance(px, py, pz, slot.X, slot.Y, slot.Z);
                bool moving = h.TryGetMoveDestination(c, out float dx, out float dy, out float dz);
                float destinationError = moving ? SquaredDistance(dx, dy, dz, slot.X, slot.Y, slot.Z) : 0f;
                issue = offset > BenchRadiusSquared ||
                    (moving && destinationError > BenchRadiusSquared);
            }
            if (issue)
            {
                slot.LastRecall = now;
                h.Recall(c, slot.X, slot.Y, slot.Z);
                PGLog.Debug("Proving Grounds: Teams 1v1 bench hold slot=" + (i + 1).ToString(CultureInfo.InvariantCulture) + " position=("
                    + CMath.FormatF(slot.X, 1) + "," + CMath.FormatF(slot.Y, 1) + "," + CMath.FormatF(slot.Z, 1) + ")");
            }
        }
    }

    // C++: SparSession.cpp:259-277
    void EngageActivePair(ISparHost<TId> h)
    {
        if (!IsLiving(h, activeA) || !IsLiving(h, activeB) ||
            IsEliminatedIndex(FindParticipantIndex(activeA)) || IsEliminatedIndex(FindParticipantIndex(activeB)))
            return;

        interBoutWaiting = h.None;
        ReleaseBench(h, FindParticipantIndex(activeA), "active-duel");
        ReleaseBench(h, FindParticipantIndex(activeB), "active-duel");
        // A new Teams 1v1 opponent may have walked in after the initial
        // formation; turn both fighters toward each other before engaging.
        if (h.TryGetPosition(activeB, out float bx, out float by, out float bz)) h.LookAt(activeA, bx, by, bz);
        if (h.TryGetPosition(activeA, out float ax, out float ay, out float az)) h.LookAt(activeB, ax, ay, az);
        pair[0] = activeA;
        pair[1] = activeB;
        h.EngageMatch(pair, pairTeams, 2, MatchRules.MatchMode.Teams1v1);
        ParkBenchFighters(h);
        status = "Sparring 1v1: " + CharName(h, activeA) + " vs " + CharName(h, activeB);
    }

    // C++: SparSession.cpp:279-317
    string BuildStatusString(ISparHost<TId> h, MatchRules.MatchMode mode, TId[] fighters, MatchRules.MatchTeam[] teams, int count)
    {
        if (mode == MatchRules.MatchMode.TeamAvB)
        {
            string teamA = "";
            string teamB = "";
            for (int i = 0; i < count; ++i)
            {
                string name = CharName(h, fighters[i]);
                if (teams[i] == MatchRules.MatchTeam.TeamA)
                {
                    if (teamA.Length != 0)
                        teamA += ", ";
                    teamA += name;
                }
                else if (teams[i] == MatchRules.MatchTeam.TeamB)
                {
                    if (teamB.Length != 0)
                        teamB += ", ";
                    teamB += name;
                }
            }
            return "Sparring: " + teamA + " vs " + teamB;
        }

        if (mode == MatchRules.MatchMode.Teams1v1)
        {
            return "Sparring 1v1: " + CharName(h, activeA) + " vs " + CharName(h, activeB);
        }

        string names = "";
        for (int i = 0; i < count; ++i)
        {
            if (names.Length != 0)
                names += ", ";
            names += CharName(h, fighters[i]);
        }
        return "Sparring Last Standing: " + names;
    }

    // C++: SparSession.cpp:319-327
    void EngageMatch(ISparHost<TId> h, TId[] fighters, MatchRules.MatchTeam[] teams, int count, MatchRules.MatchMode mode)
    {
        if (mode == MatchRules.MatchMode.Teams1v1)
        {
            EngageActivePair(h);
            return;
        }
        h.EngageMatch(fighters, teams, count, mode);
    }

    // C++: SparSession.cpp:329-345. Port-only: in Teams 1v1 each fighter's disengage under its own try/catch (FightStarter's DisengageMatch
    // already guards each fighter of one call, with the stop for all after), so one throwing fighter never keeps the others in the fight.
    void DisengageAll(ISparHost<TId> h)
    {
        int count = fighterCount;
        if (count <= 0)
            return;

        if (mode != MatchRules.MatchMode.Teams1v1)
        {
            h.DisengageMatch(fighters, count);
            return;
        }
        ReleaseAllBenches(h);
        for (int i = 0; i < count; ++i)
        {
            if (i < retiredCount && retired[i]) continue;
            TId c = fighters[i];
            Guarded("disengage", () => { one[0] = c; h.DisengageMatch(one, 1); });
        }
    }

    // C++: SparSession.cpp:347-382. BeginWalkIn (ArenaIngress, task 4.3b) answers false only when the walk-in cannot start (no site, another
    // walk-in); then the next fighter is promoted in place, the original's own fallback (:375-379).
    bool TryPromoteSide(ISparHost<TId> h, ref TId active, MatchRules.MatchTeam team)
    {
        if (IsLiving(h, active) && !IsEliminatedIndex(FindParticipantIndex(active)))
            return false;

        // Bout KO = out for the rest of Teams 1v1 (wake-ups on the bench don't revive).
        MarkEliminated(h, active);

        TId next = NextConsciousAfter(h, active, team);
        if (h.IsNone(next))
            return false;

        // Only one walk-in at a time (e.g. both actives dropped same tick).
        if (h.IsWalkInPending())
            return true;

        string downName = CharName(h, active);
        status = downName + " KO'd — " + CharName(h, next) + " walking in";
        PGLog.Debug("Proving Grounds: " + status);

        TId other = team == MatchRules.MatchTeam.TeamA ? activeB : activeA;
        if (IsLiving(h, other) && !IsEliminatedIndex(FindParticipantIndex(other)))
        {
            if (!interBoutWaiting.Equals(other))
            {
                one[0] = other;
                h.RetargetMatchAfterElimination(one, 1, null, null, 0, mode);
            }
            interBoutWaiting = other;
        }
        ReleaseBench(h, FindParticipantIndex(next), "walk-in");
        active = next;
        if (!h.BeginWalkIn(next, team))
        {
            // Fallback: promote in place if walk-in cannot start.
            EngageActivePair(h);
        }
        ParkBenchFighters(h);
        return true;
    }

    /// <summary>The C++'s clear of the match state (SparSession.cpp:392-403, :726-737).</summary>
    void ClearMatchState(ISparHost<TId> h)
    {
        active = false;
        benchCount = 0; retiredCount = 0;
        fighterCount = 0;
        eliminatedCount = 0;
        activeA = h.None;
        activeB = h.None;
        interBoutWaiting = h.None;
        hasPendingOutcome = false;
        pendingLastStanding = h.None;
        pendingOutcome = SparPodium.OutcomeKind.Stopped;
        pendingEndKind = MatchRules.MatchEndKind.None;
    }

    // ---------- namespace SparSession (SparSession.h) ----------

    /// <summary>C++: SparSession.cpp:387-404. Called by the save safety's cleanup (WorldLifecycle.cpp:66) after <see cref="AbortForSave"/>; the
    /// world teardown call (:265) is not needed (one session per world). The bench release touches the fighter (Engaged off, port), which is
    /// valid at that point (the save is not yet captured).</summary>
    public void AbandonWorldState(ISparHost<TId> h)
    {
        Guarded("combat log abandon", h.CombatLogAbandon);
        ReleaseAllBenches(h); // Removes receiver identities only; no actor access.
        ClearMatchState(h);
        status = "Idle";
    }

    /// <summary>C++: SparSession.cpp:406-417. Port-only: each step under its own try/catch, so the save's cancel sequence always completes
    /// (every fighter disengaged, the state cleared).</summary>
    public void AbortForSave(ISparHost<TId> h)
    {
        if (!active)
            return;

        Guarded("combat log abort", () => h.CombatLogAbort("safe_save"));
        Guarded("walk-in clear", h.ClearWalkIn);
        Guarded("disengage", () => DisengageAll(h));
        AbandonWorldState(h);
        status = "Match cancelled to safely save the game";
        PGLog.Debug("Proving Grounds: active match cancelled for safe save");
    }

    /// <summary>C++: SparSession.cpp:419-422.</summary>
    public bool IsActive => active;

    /// <summary>C++: SparSession.cpp:424-427.</summary>
    public string GetStatus() => status;

    /// <summary>C++: SparSession.cpp:429-432.</summary>
    public MatchRules.MatchMode GetMode() => mode;

    /// <summary>C++: SparSession.cpp:434-437.</summary>
    public int GetParticipantCount() => active ? fighterCount : 0;

    /// <summary>C++: SparSession.cpp:439-444 (<paramref name="none"/> for none).</summary>
    public TId GetParticipant(int i, TId none)
    {
        if (!active || i < 0 || i >= fighterCount)
            return none;
        return fighters[i];
    }

    /// <summary>C++: SparSession.cpp:446-452.</summary>
    public MatchRules.MatchTeam GetTeam(TId c)
    {
        int index = FindParticipantIndex(c);
        if (index < 0)
            return MatchRules.MatchTeam.None;
        return teams[index];
    }

    /// <summary>C++: SparSession.cpp:454-457.</summary>
    public TId GetActiveA(TId none) => active ? activeA : none;

    /// <summary>C++: SparSession.cpp:459-462.</summary>
    public TId GetActiveB(TId none) => active ? activeB : none;

    /// <summary>C++: SparSession.cpp:464-469.</summary>
    public bool IsEliminated(ISparHost<TId> h, TId c)
    {
        if (!active || h.IsNone(c))
            return false;
        return IsEliminatedIndex(FindParticipantIndex(c));
    }

    /// <summary>C++: SparSession.cpp:471-480.</summary>
    public bool EliminateFighter(ISparHost<TId> h, TId c)
    {
        if (!active || h.IsNone(c))
            return false;
        int index = FindParticipantIndex(c);
        if (index < 0 || IsEliminatedIndex(index))
            return false;
        MarkEliminated(h, c);
        return true;
    }

    /// <summary>C++: SparSession.cpp:482-485. KO elimination is always on; API kept for call-site compatibility.</summary>
    public void SetKoEliminationEnabled(bool enabled) { }

    /// <summary>C++: SparSession.cpp:487-490.</summary>
    public bool IsKoEliminationEnabled() => true;

    /// <summary>C++: SparSession.cpp:492-495.</summary>
    public bool HasPendingWalkIn(ISparHost<TId> h) => active && h.IsWalkInPending();

    /// <summary>C++: SparSession.cpp:497-504.</summary>
    public TId GetFighterA(TId none)
    {
        if (active && mode == MatchRules.MatchMode.Teams1v1)
            return activeA;
        if (!active || fighterCount < 1)
            return none;
        return fighters[0];
    }

    /// <summary>C++: SparSession.cpp:506-513.</summary>
    public TId GetFighterB(TId none)
    {
        if (active && mode == MatchRules.MatchMode.Teams1v1)
            return activeB;
        if (!active || fighterCount < 2)
            return none;
        return fighters[1];
    }

    /// <summary>C++: SparSession.cpp:515-540. Drives every hook (CombatHooks 3.2b, FightStarter's hit reaction, SparStats).</summary>
    public bool IsSparringOpponent(ISparHost<TId> h, TId a, TId b)
    {
        if (!active || h.IsNone(a) || h.IsNone(b))
            return false;

        int ia = FindParticipantIndex(a);
        int ib = FindParticipantIndex(b);
        if (ia < 0 || ib < 0)
            return false;
        if (IsEliminatedIndex(ia) || IsEliminatedIndex(ib))
            return false;

        if (mode == MatchRules.MatchMode.Teams1v1)
        {
            if (h.IsWalkInPending())
                return false;
            bool aActive = a.Equals(activeA) || a.Equals(activeB);
            bool bActive = b.Equals(activeA) || b.Equals(activeB);
            if (!aActive || !bActive)
                return false;
        }

        MatchRules.MatchParticipant pa = MakeParticipant(h, ia);
        MatchRules.MatchParticipant pb = MakeParticipant(h, ib);
        return MatchRules.IsOpponent(mode, pa, pb);
    }

    /// <summary>C++: SparSession.cpp:542-545.</summary>
    public bool IsSparringPair(ISparHost<TId> h, TId a, TId b) => IsSparringOpponent(h, a, b);

    /// <summary>C++: SparSession.cpp:547-550.</summary>
    public bool IsParticipant(ISparHost<TId> h, TId c) => active && !h.IsNone(c) && FindParticipantIndex(c) >= 0;

    /// <summary>C++: SparSession.cpp:552-578.</summary>
    public bool TickTeams1v1Bouts(ISparHost<TId> h)
    {
        if (!active || mode != MatchRules.MatchMode.Teams1v1)
            return false;

        ParkBenchFighters(h);
        if (h.IsWalkInPending())
        {
            h.TickWalkIn();
            if (h.IsWalkInArrived())
            {
                TId arrived = h.GetWalkInFighter();
                h.ClearWalkIn();
                if (IsLiving(h, arrived) && !IsEliminatedIndex(FindParticipantIndex(arrived)))
                    interBoutWaiting = arrived;
                EngageActivePair(h);
                ParkBenchFighters(h);
            }
            return true;
        }

        if (TryPromoteSide(h, ref activeA, MatchRules.MatchTeam.TeamA))
            return true;
        if (TryPromoteSide(h, ref activeB, MatchRules.MatchTeam.TeamB))
            return true;
        return false;
    }

    /// <summary>Port-only refusal text: a save prepared by the save safety is not captured yet (task 2.5).</summary>
    public const string SaveInFlightText = "Saving the game: start the match after the save";

    /// <summary>Port-only status after a start that threw part-way (the fighters were released; the C++ has no such case).</summary>
    public const string StartFailedText = "Match start failed: fighters released";

    /// <summary>C++: SparSession.cpp:580-671. Port-only: a first refusal while a save is in flight (<see cref="ISparHost{TId}.SaveInFlight"/>);
    /// the Teams 1v1 refusal reads <see cref="ISparHost{TId}.BenchControlAvailable"/> (the C++'s town AI hook; the port's Engaged, spike S10).
    /// A throw once the session is active (from the start's module calls or the engage) undoes what the start got to (the combat log, the
    /// remembered and engaged fighters), clears the session with <see cref="StartFailedText"/> and rethrows (port-only, note 13 of task 3.3):
    /// nobody is left with Jobs off and the combat stance on.</summary>
    public bool StartMatch(ISparHost<TId> h, MatchRules.MatchMode mode, TId[]? fighters, MatchRules.MatchTeam[]? teams, int count)
    {
        if (h.SaveInFlight)
        {
            status = SaveInFlightText;
            return false;
        }
        if (h.TownIsAftercare())
        {
            status = "Town medics are clearing the arena";
            return false;
        }
        if (active)
        {
            status = "Already sparring";
            return false;
        }
        if (fighters is null || teams is null || count < 2)
        {
            status = "Need at least two fighters";
            return false;
        }
        if (HasDuplicateFighters(fighters, count))
        {
            status = "Pick different fighters";
            return false;
        }
        if (!AllFightersValidAndConscious(h, fighters, count))
        {
            status = "Fighters must be valid and conscious";
            return false;
        }

        if (mode == MatchRules.MatchMode.TeamAvB || mode == MatchRules.MatchMode.Teams1v1)
        {
            if (!ValidateTeamMode(teams, count))
                return false;
        }
        else if (mode == MatchRules.MatchMode.LastStanding)
        {
            for (int i = 0; i < count; ++i)
            {
                if (teams[i] != MatchRules.MatchTeam.None)
                {
                    status = "Last Standing requires no teams";
                    return false;
                }
            }
        }

        if (mode == MatchRules.MatchMode.Teams1v1 && !h.BenchControlAvailable)
        {
            status = "Teams 1v1 unavailable: bench AI control is not installed";
            return false;
        }
        this.mode = mode;
        if (bench.Length < count) bench = new BenchSlot[count];
        Array.Clear(bench, 0, count);
        benchCount = count;
        if (retired.Length < count) retired = new bool[count];
        Array.Clear(retired, 0, count);
        retiredCount = count;
        if (this.fighters.Length < count) this.fighters = new TId[count];
        Array.Copy(fighters, this.fighters, count);
        if (this.teams.Length < count) this.teams = new MatchRules.MatchTeam[count];
        Array.Copy(teams, this.teams, count);
        fighterCount = count;
        eliminatedCount = 0;
        ResizeEliminated(count);
        activeA = h.None;
        activeB = h.None;
        interBoutWaiting = h.None;
        h.ClearWalkIn();

        if (mode == MatchRules.MatchMode.Teams1v1)
        {
            activeA = FirstOfTeam(h, MatchRules.MatchTeam.TeamA);
            activeB = FirstOfTeam(h, MatchRules.MatchTeam.TeamB);
            if (h.IsNone(activeA) || h.IsNone(activeB))
            {
                fighterCount = 0;
                status = "Need at least one fighter per team";
                return false;
            }
        }

        h.TownCancelPlannedNpcBout("Player match took priority");
        active = true;
        int reached = 0;   // port-only: how far the start got (1 = fighters remembered or being remembered, 2 = combat log started or starting)
        try
        {
            status = BuildStatusString(h, mode, fighters, teams, count);
            PGLog.Debug("Proving Grounds: spar start " + status);

            reached = 1;
            h.RememberMatchFighters(fighters, count);
            h.ResultsCancel();
            h.ResultsClose();
            h.StatsResetForMatch();
            hasPendingOutcome = false;
            pendingLastStanding = h.None;
            pendingOutcome = SparPodium.OutcomeKind.Stopped;
            pendingEndKind = MatchRules.MatchEndKind.None;

            reached = 2;
            h.CombatLogStart(mode, fighters, teams, count);
            EngageMatch(h, fighters, teams, count, mode);
            h.TownOnMatchStarted();
        }
        catch
        {
            // Only what the start got to is undone: nothing was remembered or engaged before reached 1 (so nobody is disengaged), the
            // combat log started only at reached 2. DisengageAll also releases any bench slot the engage reserved (Teams 1v1).
            if (reached >= 2) Guarded("start undo: combat log abort", () => h.CombatLogAbort("start_failed"));
            Guarded("start undo: walk-in clear", h.ClearWalkIn);
            if (reached >= 1) Guarded("start undo: disengage", () => DisengageAll(h));
            ClearMatchState(h);
            status = StartFailedText;
            throw;
        }
        return true;
    }

    /// <summary>C++: SparSession.cpp:673-678.</summary>
    public bool Start(ISparHost<TId> h, TId a, TId b) =>
        StartMatch(h, MatchRules.MatchMode.TeamAvB, [a, b], [MatchRules.MatchTeam.TeamA, MatchRules.MatchTeam.TeamB], 2);

    /// <summary>C++: SparSession.cpp:680-683.</summary>
    public void SetPendingEndKind(MatchRules.MatchEndKind kind) => pendingEndKind = kind;

    /// <summary>C++: SparSession.cpp:685-688.</summary>
    public MatchRules.MatchEndKind GetPendingEndKind() => pendingEndKind;

    /// <summary>C++: SparSession.cpp:690-695.</summary>
    public void SetPendingResultsOutcome(SparPodium.OutcomeKind kind, TId lastStanding)
    {
        pendingOutcome = kind;
        pendingLastStanding = lastStanding;
        hasPendingOutcome = true;
    }

    /// <summary>C++: SparSession.cpp:697-775. Port-only (note 13 of task 3.3): the result block (freeze, ratings, log, results) and the abort log
    /// each run under their own try/catch, so a throw there never keeps the fighters from being disengaged or the session from being cleared; a
    /// throw cannot repeat a rating either (the session is inactive afterwards).</summary>
    public void StopWithStatus(ISparHost<TId> h, SparStopReason reason, TId koVictim, string? statusOverride)
    {
        if (!active)
            return;

        if (reason != SparStopReason.Invalid)
        {
            Guarded("match result", () =>
            {
                SparPodium.OutcomeKind outcome = SparPodium.OutcomeKind.Stopped;
                if (reason == SparStopReason.Manual)
                    outcome = SparPodium.OutcomeKind.Stopped;
                else if (hasPendingOutcome)
                    outcome = pendingOutcome;

                h.StatsFreeze(outcome, mode, pendingLastStanding);
                LeaderboardData.Kind leaderboard = LeaderboardData.ForMatch(h.TownOwnsMatch());
                h.ApplyFromSnapshot(h.StatsSnapshot(), leaderboard, h.TownActiveMarksMultiplier(), h.TownActiveMarksBonus());
                h.CombatLogFinish(h.StatsSnapshot(), reason);
                h.TownOnMatchResult(h.StatsSnapshot());
                if (!h.TownOwnsMatch() || h.TownIsPlayerMatch())
                    h.ResultsScheduleShow(1.0f);
            });
        }

        if (reason == SparStopReason.Invalid)
        {
            Guarded("combat log abort", () => h.CombatLogAbort("invalid_fighter"));
            Guarded("town abort", h.TownOnMatchAborted);
        }
        Guarded("walk-in clear", h.ClearWalkIn);
        Guarded("disengage", () => DisengageAll(h));

        ClearMatchState(h);

        string returnStatus = "";
        Guarded("prisoner return", () => returnStatus = h.BeginReturnToCages());

        if (statusOverride is { Length: > 0 })
        {
            status = statusOverride;
        }
        else
        {
            switch (reason)
            {
                case SparStopReason.Ko:
                    status = "Over: " + CharName(h, koVictim) + " KO'd";
                    break;
                case SparStopReason.Death:
                    status = "Over: " + CharName(h, koVictim) + " died";
                    break;
                case SparStopReason.Invalid:
                    status = "Over: fighter invalid";
                    break;
                case SparStopReason.Manual:
                default:
                    status = "Stopped";
                    break;
            }
        }

        if (returnStatus.Length != 0)
        {
            PGLog.Debug(returnStatus);
            if (status.Length != 0)
                status += "\n";
            status += returnStatus;
        }

        PGLog.Debug("Proving Grounds: spar stop — " + status);
    }

    /// <summary>C++: SparSession.cpp:777-780.</summary>
    public void Stop(ISparHost<TId> h, SparStopReason reason, TId koVictim) => StopWithStatus(h, reason, koVictim, null);

    // ---------- reads for the port (no C++ counterpart) ----------

    /// <summary>The fighters of the running match (empty when none runs).</summary>
    public ReadOnlySpan<TId> Fighters => active ? fighters.AsSpan(0, fighterCount) : ReadOnlySpan<TId>.Empty;

    /// <summary>Whether the bench slot of fighter <paramref name="index"/> is reserved (tests, the probe).</summary>
    public bool IsBenched(int index) => index >= 0 && index < benchCount && bench[index].Reserved;

    /// <summary>Whether fighter <paramref name="index"/> was retired at its KO (Teams 1v1).</summary>
    public bool IsRetired(int index) => index >= 0 && index < retiredCount && retired[index];

    /// <summary>The fighter waiting between bouts (Teams 1v1), or none.</summary>
    public TId InterBoutWaiting => interBoutWaiting;
}
