using System.Globalization;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/SparSession.h/.cpp (Proving Grounds by Shutori, GPLv3), task 3.3 (PA §2.9, CAP 1, 3, 5, spike S10).
// This file is the public surface (SparSession.h); every C++ function, in order with its line range, is in Core/Glue/SparSessionLogic.cs, which
// runs over ISparHost so its decisions are tested with a fake (E3, Tests/Glue/SparSessionLogicTests.cs). src/Game/PgSparHost.cs is that host over
// PgEngine and this world's subsystems. Meitou 0.17.3 (tag v0.17.3, fa34a1b). Every method runs on the sim thread in a serial step.
//
// Engine calls of the C++ and what replaces them (E1; ISparHost member -> adapter method in src/Engine/PgEngine.cs, bound in PgSparHost):
//   c->isValid(), isDead(), isUnconcious() (IsLiving :71-74, MakeParticipant, the refusals :138-149, ParkBenchFighters :220) ... PgEngine.Exists /
//       IsDead (CharacterCold.Medical.Dead) / IsUnconscious (CharacterCold.Medical.Unconscious; playing dead included, crawling not, CAP 3)
//   c->getName() (CharName :54-59, the status texts and log lines) ... PgEngine.Name (CharacterCold.Name)
//   c->getPosition() (ParkBenchFighters :231, :236, :240; EngageActivePair :270-271) ... PgEngine.Position
//   TownAftercare::ControlAvailable() (StartMatch :625) ... TownAftercare.ControlAvailable (task 8.4a: the hold registry's members resolve)
//   TownAftercare::ReserveBenchOrders / ReleaseBenchOrders(c->getOrdersReciever()) (:229, :91) ... TownAftercare's (task 8.4a): the bench table
//       with the C++'s refusals, then for an NPC the world's hold registry with the owner Bench (CharacterCold.Engaged, spike S10; re-asserted
//       every SlowWorld); a player character is reserved in the table but not held (the AI never runs one)
//   setStandingOrder(PASSIVE, true), (HOLD, within 8), (AGG, false) (:235-237) ... PgEngine.SetBenchStance (AGG has no counterpart)
//   getMovement(), movement->isCurrentlyMoving(), movement->destination (:238-243) ... PgEngine.MoveDestination (horizontal velocity, Goal)
//   clearAllAIGoals(), addOrder(MOVE_CUS_ORDERED, pos), movement->setDestination, setDestination (:247-250) ... PgEngine.MoveTo (MoveOrder)
//   lookatPosition(pos, true) (:270-271) ... PgEngine.Face (CharacterHot.Yaw)
//   GetTickCount() (ParkBenchFighters :217) ... PgClock.NowMs; CombatBalanceLog's GetTickCount64 ... PgLogGlue.WallMs64 (real ms, task 8.7c)
//   FightStarter::RememberMatchFighters / EngageMatch / DisengageMatch / RetargetMatchAfterElimination ... src/Game/FightStarter.cs (task 3.1)
//   ArenaIngress::GetWalkInFighter / ClearWalkIn / IsWalkInPending / BeginWalkIn / TickWalkIn / IsWalkInArrived ... src/Game/ArenaIngress.cs
//       (task 4.3b: the walk-in to the side's pit marker; BeginWalkIn false only when it cannot start, then the next fighter is promoted in
//       place, the C++'s fallback :375-379)
//   TownArena::IsAftercare / CancelPlannedNpcBout / OnMatchStarted / OwnsMatch / IsPlayerMatch / GetActiveMarksMultiplier / GetActiveMarksBonus /
//       OnMatchResult / OnMatchAborted ... src/Town/TownArena.cs (task 7.1: the state reads; OnMatchStarted / OnMatchResult / OnMatchAborted: tasks 7.3 (NPC halves) and 7.5 (player halves))
//   ResultsUI::Cancel / Close / ScheduleShow(1.0f) ... src/Ui/ResultsUI.cs placeholders until task 4.6; ScheduleShow logs the result data as a
//       PG-SPAR line (PgSparHost)
//   SparStats::ResetForMatch / Freeze / GetMutableSnapshot / GetSnapshot / OnEliminated ... src/Game/SparStats.cs (task 3.2a), this object as
//       the roster (ISparSessionRoster)
//   LeaderboardStore::ApplyFromSnapshot (:713-715) ... src/Game/LeaderboardStore.cs (task 5.1): ratings and Marks into the world's ledger
//   CombatBalanceLog::StartMatch / FinishMatch / AbortMatch / Abandon ... Core/CombatBalanceLog.cs over the system's CombatLogHost
//   PrisonerUtil::BeginReturnToCages (:740) ... src/Game/PrisonerUtil.Return.cs (task 6.3)
//   PGLog::Debug(...) ... PGLog.Debug, the C++ texts byte for byte
// Callers (C++ -> port): ArenaIngress.cpp:942 StartMatch (task 4.3b, started by the TEST-ONLY `pg ingress` until the Arena window; the TEST-ONLY `pg spar` too), ArenaUI.cpp:2476 Stop(StopManual) (M4; until
// then `pg stop`), KOWatcher.cpp (src/Game/KOWatcher.cs), FightStarter.cpp:575-581 (IFightSession), SparStats.cpp (ISparSessionRoster),
// CombatHooks.cpp:160-208 IsSparringOpponent (task 3.2b), WorldLifecycle.cpp:66, :119, :242 (src/Game/WorldLifecycle.cs, task 2.5),
// ArenaRingGuard.cpp:67-98 (task 3.4), ArenaMedical / PrisonerUtil / TownArena / TownSpectators / TownAftercare (M4-M8).
// AbandonWorldState's world-teardown call (WorldLifecycle.cpp:265) is not needed: one session per world's system (task 2.5).

/// <summary>
/// The glue of the original SparSession.cpp: the running match (mode, fighters, teams, who is out), the Teams 1v1 bench and promotion, the
/// start with its refusals, the stop with its results and ratings. One object per world, owned by ProvingGroundsSystem. Public surface =
/// SparSession.h; each call binds the world and runs the C++ function in <see cref="SparSessionLogic{TId}"/>.
/// </summary>
sealed class SparSession : IFightSession, ISparSessionRoster
{
    /// <summary>The other subsystems and reads the session calls (built by ProvingGroundsSystem; delegates read the system's state lazily).</summary>
    internal sealed record Services(
        FightStarter FightStarter,
        SparStats SparStats,
        ArenaIngress Ingress,
        Town.TownArena Town,
        Ui.ResultsUI Results,
        PrisonerUtil Prisoners,
        PgClock Clock,
        CombatBalanceLog.IHost CombatLog,
        Func<World, CharacterId, LeaderboardData.Kind, CombatBalanceLog.FighterInfo> CombatFighter,
        LeaderboardStore Store,                                   // C++ LeaderboardStore (task 5.1)
        Func<World, CharacterId, string> Identity,                // C++ TownMatchmakingRuntime::Identity (FighterIdentity::Find, "" for none)
        Func<bool> SaveInFlight,                                  // task 2.5: a prepared save not yet captured
        Action<string>? OwnLog,                                   // the port's own lines (Meitou's log)
        Action<World, SparPodium.Snapshot>? ResultObserver = null);   // port only (task 10.2): every result, after the town's own (ArenaInterop's news)

    readonly SparSessionLogic<CharacterId> logic;
    readonly PgSparHost host;
    readonly Services services;

    internal SparSession(Services services)
    {
        this.services = services;
        logic = new SparSessionLogic<CharacterId>(CharacterId.None, services.OwnLog);
        host = new PgSparHost(this, services);
    }

    PgSparHost H(World world) => host.Bind(world);

    /// <summary>The logic, for KOWatcher (same world, same object).</summary>
    internal SparSessionLogic<CharacterId> Logic => logic;

    /// <summary>Steps of a start undo, stop or abort that threw and were skipped in this world (the F10 row).</summary>
    internal int FailureCount => logic.FailureCount;

    /// <summary>The host bound to <paramref name="world"/>, for KOWatcher.</summary>
    internal PgSparHost HostFor(World world) => H(world);

    // ---------- namespace SparSession (SparSession.h), C++ line ranges in Core/Glue/SparSessionLogic.cs ----------

    // C++: SparSession.cpp:419-422. IFightSession.IsActive reads the same field: the two always agree.
    internal bool IsActive(World world) => logic.IsActive;
    /// <summary>The same flag without a world (the publish of the snapshot).</summary>
    internal bool Running => logic.IsActive;
    internal string GetStatus() => logic.GetStatus();
    internal MatchRules.MatchMode GetMode() => logic.GetMode();
    internal int GetParticipantCount() => logic.GetParticipantCount();
    internal CharacterId? GetParticipant(int i) => logic.GetParticipant(i, CharacterId.None) is { IsNone: false } c ? c : null;
    internal MatchRules.MatchTeam GetTeam(CharacterId c) => logic.GetTeam(c);
    internal bool IsEliminated(World world, CharacterId c) => logic.IsEliminated(H(world), c);
    internal bool EliminateFighter(World world, CharacterId c) => logic.EliminateFighter(H(world), c);
    internal CharacterId GetActiveA() => logic.GetActiveA(CharacterId.None);
    internal CharacterId GetActiveB() => logic.GetActiveB(CharacterId.None);
    internal bool HasPendingWalkIn(World world) => logic.HasPendingWalkIn(H(world));
    internal bool TickTeams1v1Bouts(World world) => logic.TickTeams1v1Bouts(H(world));
    internal bool IsSparringOpponent(World world, CharacterId a, CharacterId b) => logic.IsSparringOpponent(H(world), a, b);
    internal bool IsParticipant(World world, CharacterId c) => logic.IsParticipant(H(world), c);
    internal MatchRules.MatchEndKind PendingEndKind => logic.GetPendingEndKind();
    internal bool StartMatch(World world, MatchRules.MatchMode mode, CharacterId[] fighters, MatchRules.MatchTeam[] teams, int count)
    {
        bool ok = logic.StartMatch(H(world), mode, fighters, teams, count);
        if (ok) scanTick = prevScanTick = -1;   // port only (task 3.5): the scan ticks start again with the match, whoever started it
        return ok;
    }
    internal void Stop(World world, SparStopReason reason) => logic.Stop(H(world), reason, CharacterId.None);   // C++ default koVictim = 0
    internal void Stop(World world, SparStopReason reason, CharacterId koVictim) => logic.Stop(H(world), reason, koVictim);
    internal CharacterId GetFighterA() => logic.GetFighterA(CharacterId.None);
    internal CharacterId GetFighterB() => logic.GetFighterB(CharacterId.None);

    /// <summary>C++: SparSession.cpp:406-417 (WorldLifecycle.cpp:119, the safe-save cancel). Each step guarded: the save always goes on.</summary>
    internal void AbortForSave(World world)
    {
        logic.AbortForSave(H(world));
        host.ReleaseAllHeld();
        LogTransition(world);
    }

    /// <summary>C++: SparSession.cpp:387-404 (WorldLifecycle.cpp:66, the safe-save cleanup).</summary>
    internal void AbandonWorldState(World world)
    {
        logic.AbandonWorldState(H(world));
        host.ReleaseAllHeld();
        LogTransition(world);
    }

    // ---------- IFightSession (what FightStarter reads) ----------

    bool IFightSession.IsActive => logic.IsActive;
    MatchRules.MatchMode IFightSession.Mode => logic.GetMode();
    bool IFightSession.IsEliminated(World world, CharacterId c) => logic.IsEliminated(H(world), c);
    bool IFightSession.IsSparringOpponent(World world, CharacterId a, CharacterId b) => logic.IsSparringOpponent(H(world), a, b);

    /// <summary>The session FightStarter's hit reaction reads, or null when nothing runs.</summary>
    internal IFightSession? Active => logic.IsActive ? this : null;

    // ---------- ISparSessionRoster (what SparStats reads) ----------

    public int ParticipantCount(World world) => logic.GetParticipantCount();
    public CharacterId? Participant(World world, int index) => GetParticipant(index);
    public MatchRules.MatchTeam Team(World world, CharacterId fighter) => logic.GetTeam(fighter);
    // Serial steps only (the SparSession contract, ISparSessionRoster): the commit step's ApplyBlow postfix (CombatHooks, task 3.2b) is one;
    // the parallel AI threads read CombatHooks' published CombatMatchView instead.
    bool ISparSessionRoster.IsSparringOpponent(World world, CharacterId attacker, CharacterId defender) =>
        logic.IsSparringOpponent(H(world), attacker, defender);

    // ---------- the F10 row and the test output (port only) ----------

    /// <summary>The F10 phase row: the C++ status while a match runs, else "idle (last: ...)".</summary>
    internal string Phase => logic.IsActive ? logic.GetStatus() : "idle (last: " + logic.GetStatus().Replace('\n', ' ') + ")";

    // What the PG-SPAR lines report (port-only test output, not gated by `pg spikes on`: the scenarios read them, and they are written only
    // at a match's transitions, never per tick): the fighters and teams of the current / last match, copied into reused arrays when
    // LogTransition first sees the match running, whoever started it; the status and active pair last logged; whether a match was seen.
    CharacterId[] loggedFighters = [];
    MatchRules.MatchTeam[] loggedTeams = [];
    int loggedCount;
    string loggedStatus = "";
    CharacterId loggedActiveA = CharacterId.None, loggedActiveB = CharacterId.None;
    bool loggedActive;

    // The ticks of KOWatcher's last two scans of this match (port only, task 3.5): a fighter seen down by the scan at scanTick was still up at
    // prevScanTick (that scan found no end), so its KO happened in (prevScanTick, scanTick]; the end and status lines carry both. -1 = none yet.
    long scanTick = -1, prevScanTick = -1;

    /// <summary>KOWatcher's scan at <paramref name="tick"/> (port only, task 3.5): kept for the PG-SPAR lines.</summary>
    internal void NoteScan(long tick)
    {
        if (tick == scanTick) return;
        prevScanTick = scanTick;
        scanTick = tick;
    }

    string ScanTicks() => ",\"scanTick\":" + scanTick.ToString(CultureInfo.InvariantCulture)
        + ",\"prevScanTick\":" + prevScanTick.ToString(CultureInfo.InvariantCulture);

    void Log(string json) => services.OwnLog?.Invoke("PG-SPAR " + json);

    string Fighter(World world, CharacterId id)
    {
        var o = PgEngine.Orders(world, id);
        return "{\"id\":" + PGLog.Quote(CharacterIdText.Format(id))
            + ",\"name\":" + PGLog.Quote(PgEngine.Name(world, id) ?? "")
            + ",\"team\":" + ((int)LoggedTeam(id)).ToString(CultureInfo.InvariantCulture)
            + ",\"orders\":" + (o is { } x ? PGLog.Quote(x.Orders.ToString()) : "null")
            + ",\"hold\":" + (o?.HoldPoint is null ? "false" : "true")
            + ",\"jobsRemembered\":" + (services.FightStarter.TryGetRemembered(id, out var st) ? (st.JobsToggledOff ? "\"off\"" : "\"kept\"") : "null")
            + ",\"eliminated\":" + (logic.IsActive && logic.IsEliminated(H(world), id) ? "true" : "false")
            + ",\"active\":" + (id == GetActiveA() || id == GetActiveB() ? "true" : "false")
            + ",\"benched\":" + (IndexOf(id) is int i and >= 0 && logic.IsBenched(i) ? "true" : "false")
            + ",\"unconscious\":" + (PgEngine.IsUnconscious(world, id) ? "true" : "false")
            + ",\"dead\":" + (PgEngine.IsDead(world, id) ? "true" : "false") + "}";
    }

    /// <summary>The fighter's team: the session's while it runs, else the one copied at the start (the end line is written after the clear).</summary>
    MatchRules.MatchTeam LoggedTeam(CharacterId id)
    {
        if (logic.IsActive) return logic.GetTeam(id);
        int i = Array.IndexOf(loggedFighters, id, 0, loggedCount);
        return i >= 0 ? loggedTeams[i] : MatchRules.MatchTeam.None;
    }

    int IndexOf(CharacterId id)
    {
        for (int i = 0; i < logic.GetParticipantCount(); i++) if (logic.Fighters[i] == id) return i;
        return -1;
    }

    string Fighters(World world)
    {
        var parts = new string[loggedCount];
        for (int i = 0; i < parts.Length; i++) parts[i] = Fighter(world, loggedFighters[i]);
        return "[" + string.Join(",", parts) + "]";
    }

    /// <summary>After anything that may change the match (a KOWatcher scan, a command, the save path): one PG-SPAR line when the status or the
    /// active pair changed while a match runs (a Teams 1v1 promotion), or when the match ended (event "end", the final status and the fighters' state). Allocates
    /// only when it logs.</summary>
    internal void LogTransition(World world)
    {
        string status = logic.GetStatus();
        if (logic.IsActive && !loggedActive)
        {
            // first sight of this match: copy its roster (the end line is written after the session cleared it); no line here, the starter
            // writes its own (pg spar's "start")
            var f = logic.Fighters;
            if (loggedFighters.Length < f.Length) { loggedFighters = new CharacterId[f.Length]; loggedTeams = new MatchRules.MatchTeam[f.Length]; }
            for (int i = 0; i < f.Length; i++) { loggedFighters[i] = f[i]; loggedTeams[i] = logic.GetTeam(f[i]); }
            loggedCount = f.Length;
            loggedStatus = status;
            loggedActiveA = GetActiveA();
            loggedActiveB = GetActiveB();
            loggedActive = true;
            return;
        }
        if (logic.IsActive)
        {
            CharacterId a = GetActiveA(), b = GetActiveB();
            if (status != loggedStatus || a != loggedActiveA || b != loggedActiveB)   // the active pair too: two fighters may share a name
            {
                loggedStatus = status;
                loggedActiveA = a;
                loggedActiveB = b;
                Log("{\"event\":\"status\",\"tick\":" + world.Tick.ToString(CultureInfo.InvariantCulture) + ",\"status\":" + PGLog.Quote(status)
                    + ",\"activeA\":" + PGLog.Quote(CharacterIdText.Format(GetActiveA())) + ",\"activeB\":" + PGLog.Quote(CharacterIdText.Format(GetActiveB()))
                    + ScanTicks() + ",\"fighters\":" + Fighters(world) + "}");
            }
            loggedActive = true;
            return;
        }
        if (!loggedActive) return;
        loggedActive = false;
        loggedStatus = status;
        Log("{\"event\":\"end\",\"tick\":" + world.Tick.ToString(CultureInfo.InvariantCulture) + ",\"status\":" + PGLog.Quote(status)
            + ScanTicks() + ",\"fighters\":" + Fighters(world) + "}");
    }

    /// <summary>TEST ONLY <c>pg spar &lt;avb|1v1|ffa&gt; &lt;idA...&gt; [vs &lt;idB...&gt;]</c> (task 3.3; task 3.5 keeps it): the start the
    /// original's Arena window makes after its ingress (ArenaUI.cpp:2388-2430 builds team A then team B, or the pool with TeamNone for Last
    /// Standing; ArenaIngress.cpp:942 calls StartMatch). Answers one PG-SPAR line (event "start") with the C++ status.</summary>
    internal string StartForTest(World world, MatchRules.MatchMode mode, CharacterId[] fighters, MatchRules.MatchTeam[] teams)
    {
        string before = "[" + string.Join(",", fighters.Select(f => Fighter(world, f))) + "]";
        bool ok = StartMatch(world, mode, fighters, teams, fighters.Length);
        if (ok) LogTransition(world);   // captures the roster (first sight of the match)
        string json = "{\"event\":\"start\",\"ok\":" + (ok ? "true" : "false") + ",\"tick\":" + world.Tick.ToString(CultureInfo.InvariantCulture)
            + ",\"mode\":" + ((int)mode).ToString(CultureInfo.InvariantCulture) + ",\"status\":" + PGLog.Quote(logic.GetStatus())
            + ",\"before\":" + before + (ok ? ",\"engaged\":" + Fighters(world) : "") + "}";
        Log(json);
        return json;
    }

    /// <summary>TEST ONLY <c>pg stop</c>: the Arena window's Stop (ArenaUI.cpp:2476, <c>Stop(StopManual)</c>): outcome Stopped, results, no rating.</summary>
    internal string StopForTest(World world)
    {
        if (!logic.IsActive)
        {
            string none = "{\"event\":\"stop\",\"ok\":false,\"error\":\"no match running\",\"status\":" + PGLog.Quote(logic.GetStatus()) + "}";
            Log(none);
            return none;
        }
        Stop(world, SparStopReason.Manual);
        host.ReleaseAllHeld();
        LogTransition(world);
        // task 3.5: the ok answer is a PG-SPAR line too, as the refusal above (it was only in the 'pg stop at tick' line)
        string done = "{\"event\":\"stop\",\"ok\":true,\"tick\":" + world.Tick.ToString(CultureInfo.InvariantCulture)
            + ",\"status\":" + PGLog.Quote(logic.GetStatus()) + "}";
        Log(done);
        return done;
    }
}
