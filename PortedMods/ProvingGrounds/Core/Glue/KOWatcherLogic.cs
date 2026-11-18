namespace ProvingGrounds.Core.Glue;

// The decisions of upstream/ProvingGrounds/plugin/src/KOWatcher.cpp (Proving Grounds by Shutori, GPLv3), task 3.3, over the SparSession host
// (Core/Glue/SparSessionLogic.cs) so they run without the game (E3). src/Game/KOWatcher.cs binds the world; Tests/Glue/KOWatcherLogicTests.cs
// runs the same code over a fake. The clocks (KOWatcher.cpp:24-49) are Core/Glue/FightStarterState.cs FightKeepAliveCadence (task 3.1).

/// <summary>
/// KOWatcher.cpp: every 0.10 s it reads each fighter's state, eliminates the knocked out (not in AvB), runs the Teams 1v1 bouts, ends the match
/// through <see cref="MatchRules.EvaluateEnd"/>, retargets after an elimination and keeps the fighters engaged every 0.50 s. One object per world.
/// The buffers are kept between scans (no allocation per scan once a match's size was seen).
/// </summary>
public sealed class KOWatcherLogic<TId> where TId : IEquatable<TId>
{
    readonly FightKeepAliveCadence cadence = new();                 // C++ :27-29 the two clocks and g_lastTick
    TId[] fighters = [];                                            // C++ :30 g_fighters
    MatchRules.MatchParticipant[] snapshot = [];                    // C++ :31 g_snapshot
    sbyte[] previousConscious = [];                                 // C++ :32 g_previousConscious (-1 = unknown, 0, 1)
    int previousConsciousCount;                                     // its size()
    TId[] remaining = [];                                           // C++ :33 g_remaining
    MatchRules.MatchTeam[] remainingTeams = [];                     // C++ :34 g_remainingTeams
    int remainingCount;
    int consecutiveFailures;                                        // port-only: scans in a row that threw (TickGuarded)
    bool scanned;                                                   // the last Tick got past the 0.10 s cadence

    /// <summary>Port-only (task 3.5): whether the last <see cref="Tick"/> got past the 0.10 s cadence and scanned (the PG-SPAR test lines stamp
    /// the scan ticks, so a scenario can bound when a KO happened: after the scan before the one that saw it).</summary>
    public bool Scanned => scanned;

    /// <summary>Port-only: scans in a row that may throw before the match is stopped Invalid (<see cref="TickGuarded"/>).</summary>
    public const int MaxConsecutiveFailures = 3;

    /// <summary>C++: KOWatcher.cpp:45-56 (the clock part is FightKeepAliveCadence.Reset). The buffers keep their storage.</summary>
    void ResetRuntimeState()
    {
        cadence.Reset();
        previousConsciousCount = 0;
        remainingCount = 0;
        consecutiveFailures = 0;
        scanned = false;
    }

    // C++: KOWatcher.cpp:58-63
    static string CharName(ISparHost<TId> h, TId c)
    {
        if (h.IsNone(c))
            return "(none)";
        return h.Name(c);
    }

    static void Ensure<T>(ref T[] array, int count)
    {
        if (array.Length < count) array = new T[Math.Max(count, array.Length * 2)];
    }

    /// <summary>C++ :210-222, :248-258: the fighters still up (not eliminated, valid, not dead, not unconscious) and their teams.</summary>
    void CollectRemaining(ISparHost<TId> h, SparSessionLogic<TId> session, int count)
    {
        remainingCount = 0;
        for (int i = 0; i < count; ++i)
        {
            TId c = fighters[i];
            if (session.IsEliminated(h, c) ||
                h.IsNone(c) || !h.Exists(c) || h.IsDead(c) || h.IsUnconscious(c))
            {
                continue;
            }
            remaining[remainingCount] = c;
            remainingTeams[remainingCount] = session.GetTeam(c);
            remainingCount++;
        }
    }

    /// <summary>Port-only (task 3.3 review): <see cref="Tick"/>, and when its scan throws <see cref="MaxConsecutiveFailures"/> times in a row
    /// (a throw that recurs every scan would otherwise keep the match running forever, its fighters stuck in it), the match is stopped Invalid
    /// (SparSession's own guarded stop: disengage, clear); the exception is rethrown either way so the caller's failure log sees it. A scan that
    /// completes resets the count.</summary>
    public void TickGuarded(ISparHost<TId> h, SparSessionLogic<TId> session, uint nowMs)
    {
        try
        {
            Tick(h, session, nowMs);
            if (scanned) consecutiveFailures = 0;
        }
        catch
        {
            if (++consecutiveFailures >= MaxConsecutiveFailures)
            {
                consecutiveFailures = 0;
                if (session.IsActive)
                {
                    try { session.Stop(h, SparStopReason.Invalid, h.None); }
                    catch { /* the stop guards its own steps; nothing more to do here */ }
                }
            }
            throw;
        }
    }

    /// <summary>C++: KOWatcher.cpp:68-269. The down test, everywhere: conscious = not dead (<c>isDead</c>) and not unconscious
    /// (<c>isUnconcious</c>); the port reads <c>CharacterCold.Medical.Dead</c> and <c>.Unconscious</c> (MedicalState.cs:81-86; playing dead
    /// stays Unconscious, MedicalState.cs:217-221; a crawler is neither, CAP 3).</summary>
    public void Tick(ISparHost<TId> h, SparSessionLogic<TId> session, uint nowMs)
    {
        if (!session.IsActive)
        {
            ResetRuntimeState();
            return;
        }

        scanned = false;
        if (!cadence.Scan(nowMs))
            return;
        scanned = true;

        int count = session.GetParticipantCount();
        if (count <= 0)
        {
            session.Stop(h, SparStopReason.Invalid, h.None);
            return;
        }

        MatchRules.MatchMode mode = session.GetMode();
        Ensure(ref fighters, count);
        Ensure(ref snapshot, count);
        Ensure(ref remaining, count);
        Ensure(ref remainingTeams, count);
        if (previousConsciousCount != count)
        {
            Ensure(ref previousConscious, count);
            for (int i = 0; i < count; ++i) previousConscious[i] = -1;
            previousConsciousCount = count;
        }
        bool eliminatedThisTick = false;
        bool teamStateChangedThisTick = false;

        for (int i = 0; i < count; ++i)
        {
            TId c = session.GetParticipant(i, h.None);
            fighters[i] = c;
            if (h.IsNone(c) || !h.Exists(c))
            {
                session.Stop(h, SparStopReason.Invalid, h.None);
                return;
            }

            ref MatchRules.MatchParticipant p = ref snapshot[i];
            p.Id = i;
            p.Team = session.GetTeam(c);
            p.Dead = h.IsDead(c);
            p.Conscious = !p.Dead && !h.IsUnconscious(c);

            ref sbyte previous = ref previousConscious[i];
            if (mode == MatchRules.MatchMode.TeamAvB && previous >= 0 &&
                previous != (p.Conscious ? 1 : 0))
                teamStateChangedThisTick = true;
            previous = (sbyte)(p.Conscious ? 1 : 0);

            if (session.IsKoEliminationEnabled() &&
                mode != MatchRules.MatchMode.TeamAvB &&
                !p.Dead &&
                h.IsUnconscious(c))
            {
                eliminatedThisTick =
                    session.EliminateFighter(h, c) || eliminatedThisTick;
            }

            p.Eliminated = session.IsEliminated(h, c);
            if (p.Eliminated)
                p.Conscious = false;

            if (!p.Conscious)
                h.StatsOnEliminated(c);
        }

        if (mode == MatchRules.MatchMode.Teams1v1)
        {
            if (session.TickTeams1v1Bouts(h))
            {
                // Bout transition / walk-in in progress — do not end yet.
                cadence.ResetKeepAlive();
                return;
            }

            // Promotion marks newly KO'd actives eliminated — refresh before wipe check
            // so a woken bench fighter can't keep a wiped team "alive".
            for (int i = 0; i < count; ++i)
            {
                snapshot[i].Eliminated = session.IsEliminated(h, fighters[i]);
            }
        }

        MatchRules.MatchEndKind end =
            MatchRules.EvaluateEnd(mode, snapshot, count,
                BalanceTuning.Get().DownedPercent);

        if (end != MatchRules.MatchEndKind.None)
        {
            // Recorded so the telemetry log can tell a team wipe from a draw or a
            // last-standing; StopReason alone cannot.
            session.SetPendingEndKind(end);
            switch (end)
            {
                case MatchRules.MatchEndKind.TeamWipeA:
                    session.SetPendingResultsOutcome(SparPodium.OutcomeKind.TeamBWins, h.None);
                    if (mode == MatchRules.MatchMode.Teams1v1)
                        session.StopWithStatus(h, SparStopReason.Ko, h.None, "Over: Team B wins");
                    else
                        session.StopWithStatus(h, SparStopReason.Ko, h.None, "Over: Team A wiped");
                    return;
                case MatchRules.MatchEndKind.TeamWipeB:
                    session.SetPendingResultsOutcome(SparPodium.OutcomeKind.TeamAWins, h.None);
                    if (mode == MatchRules.MatchMode.Teams1v1)
                        session.StopWithStatus(h, SparStopReason.Ko, h.None, "Over: Team A wins");
                    else
                        session.StopWithStatus(h, SparStopReason.Ko, h.None, "Over: Team B wiped");
                    return;
                case MatchRules.MatchEndKind.LastStanding:
                {
                    TId winner = h.None;
                    for (int i = 0; i < count; ++i)
                    {
                        if (snapshot[i].Conscious)
                        {
                            winner = fighters[i];
                            break;
                        }
                    }
                    session.SetPendingResultsOutcome(SparPodium.OutcomeKind.LastStanding, winner);
                    string status = "Over: " + CharName(h, winner) + " last standing";
                    session.StopWithStatus(h, SparStopReason.Ko, winner, status);
                    return;
                }
                case MatchRules.MatchEndKind.Draw:
                    session.SetPendingResultsOutcome(SparPodium.OutcomeKind.Draw, h.None);
                    session.StopWithStatus(h, SparStopReason.Ko, h.None, "Over: Draw / none standing");
                    return;
                default:
                    break;
            }
        }

        if ((eliminatedThisTick || teamStateChangedThisTick) &&
            mode != MatchRules.MatchMode.Teams1v1)
        {
            // Clear stale orders and temporary-enemy memories that still point
            // at the eliminated fighter, then immediately restart the remaining
            // combatants against valid opponents. This is an in-match retarget,
            // not a full disengage: keep Jobs and saved pre-match orders intact.
            CollectRemaining(h, session, count);

            h.RetargetMatchAfterElimination(
                fighters,
                count,
                remainingCount == 0 ? null : remaining,
                remainingCount == 0 ? null : remainingTeams,
                remainingCount,
                mode);
            cadence.ResetKeepAlive();
            return;
        }

        // Periodically refresh attack orders if AI dropped the target.
        if (cadence.KeepAlive())
        {
            if (mode == MatchRules.MatchMode.Teams1v1)
            {
                TId a = session.GetActiveA(h.None);
                TId b = session.GetActiveB(h.None);
                if (!h.IsNone(a) && !h.IsNone(b))
                    h.KeepEngaged(a, b);
                return;
            }

            CollectRemaining(h, session, count);

            if (remainingCount >= 2)
            {
                h.KeepEngagedMatch(
                    remaining,
                    remainingTeams,
                    remainingCount,
                    mode);
            }
        }
    }
}
