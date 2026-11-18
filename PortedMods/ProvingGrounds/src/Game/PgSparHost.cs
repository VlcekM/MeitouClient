using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

/// <summary>SparSession's and KOWatcher's host (task 3.3): each <see cref="ISparHost{TId}"/> member is one PgEngine adapter method or one call of
/// another subsystem of this world (the E1 table in src/Game/SparSession.cs). Bound to the world of each call and reused; sim thread, serial
/// steps only. Owned by the SparSession of one world.</summary>
sealed class PgSparHost(SparSession session, SparSession.Services services) : ISparHost<CharacterId>
{
    readonly SparSession session = session;
    readonly SparSession.Services s = services;
    World? world;
    readonly List<CharacterId> benched = [];    // the characters whose bench reservation this session took (task 8.4a: the aftercare's table + the hold registry)

    internal PgSparHost Bind(World w) { world = w; return this; }

    World W => world ?? throw new InvalidOperationException("PgSparHost used before Bind");

    // ---------- engine reads ----------
    public CharacterId None => CharacterId.None;
    public bool IsNone(CharacterId c) => c.IsNone;
    public bool Exists(CharacterId c) => PgEngine.Exists(W, c);
    public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);              // CharacterCold.Medical.Dead
    public bool IsUnconscious(CharacterId c) => PgEngine.IsUnconscious(W, c); // CharacterCold.Medical.Unconscious (playing dead included)
    public string Name(CharacterId c) => PgEngine.Name(W, c) ?? "";
    public bool TryGetPosition(CharacterId c, out float x, out float y, out float z)
    {
        if (PgEngine.Position(W, c) is { } p) { x = p.X; y = p.Y; z = p.Z; return true; }
        x = y = z = 0f;
        return false;
    }
    public uint NowMs => s.Clock.NowMs;

    // ---------- the bench ----------
    /// <summary>C++ <c>TownAftercare::ControlAvailable()</c>: the hold registry's members resolve (task 8.4a; spike S10's Engaged).</summary>
    public bool BenchControlAvailable => s.Town.Aftercare.ControlAvailable();

    /// <summary>C++ <c>TownAftercare::ReserveBenchOrders(c-&gt;getOrdersReciever())</c> (task 8.4a): the aftercare's bench table (capacity 16; refused
    /// for a character in the medic, patient or external table: a fighter the aftercare holds cannot be benched), then the hold registry's bench
    /// hold for an NPC (a player character is not held: the AI never runs one, AiSystem.cs:366).</summary>
    public bool ReserveBench(CharacterId c)
    {
        if (!s.Town.Aftercare.ReserveBenchOrders(W, c)) return false;
        if (!benched.Contains(c)) benched.Add(c);
        return true;
    }

    /// <summary>C++ <c>TownAftercare::ReleaseBenchOrders(receiver)</c>: the table entry and the bench's hold given back.</summary>
    public void ReleaseBench(CharacterId c)
    {
        int i = benched.IndexOf(c);
        if (i < 0) return;
        s.Town.Aftercare.ReleaseBenchOrders(W, c);
        benched.RemoveAt(i);   // after the release: a release that throws keeps the id, so ReleaseAllHeld tries again
    }

    /// <summary>Port only: releases every bench reservation still taken (a session that ended without its bench release, e.g. after a throw), each
    /// under its own try/catch (one throwing never keeps the others; the first failure in the world is logged); each is forgotten once released. The
    /// re-assert of the held ones is the world's one pass (TownArena.Tick, task 8.4a).</summary>
    internal void ReleaseAllHeld()
    {
        if (benched.Count == 0) return;
        for (int i = benched.Count - 1; i >= 0; i--)
        {
            try { s.Town.Aftercare.ReleaseBenchOrders(W, benched[i]); benched.RemoveAt(i); }
            catch (Exception e)
            {
                if (!releaseFailureLogged)
                {
                    releaseFailureLogged = true;
                    s.OwnLog?.Invoke($"SparSession bench release of {CharacterIdText.Format(benched[i])} failed (logged once per world): {e.GetType().Name}: {e.Message}");
                }
            }
        }
        // an id whose release threw stays listed: the next call (the next tick's KOWatcher, an abort, the world abandon) tries it again
    }

    bool releaseFailureLogged;

    public void SetBenchStance(CharacterId c, bool hold) => PgEngine.SetBenchStance(W, c, hold);
    public bool TryGetMoveDestination(CharacterId c, out float x, out float y, out float z)
    {
        if (PgEngine.MoveDestination(W, c) is { } d) { x = d.X; y = d.Y; z = d.Z; return true; }
        x = y = z = 0f;
        return false;
    }
    public void Recall(CharacterId c, float x, float y, float z) => PgEngine.MoveTo(W, c, new Vector3(x, y, z));
    public void LookAt(CharacterId c, float x, float y, float z) => PgEngine.Face(W, c, new Vector3(x, y, z));

    // ---------- FightStarter ----------
    public void RememberMatchFighters(CharacterId[] fighters, int count) => s.FightStarter.RememberMatchFighters(W, fighters, count);
    public void EngageMatch(CharacterId[] fighters, MatchRules.MatchTeam[] teams, int count, MatchRules.MatchMode mode) =>
        s.FightStarter.EngageMatch(W, fighters, teams, count, mode);
    public void DisengageMatch(CharacterId[] fighters, int count) => s.FightStarter.DisengageMatch(W, fighters, count);
    public void RetargetMatchAfterElimination(CharacterId[] allFighters, int allCount, CharacterId[]? remainingFighters,
        MatchRules.MatchTeam[]? remainingTeams, int remainingCount, MatchRules.MatchMode mode) =>
        s.FightStarter.RetargetMatchAfterElimination(W, allFighters, allCount, remainingFighters, remainingTeams, remainingCount, mode);
    public void KeepEngaged(CharacterId a, CharacterId b) => s.FightStarter.KeepEngaged(W, a, b);
    public void KeepEngagedMatch(CharacterId[] fighters, MatchRules.MatchTeam[] teams, int count, MatchRules.MatchMode mode) =>
        s.FightStarter.KeepEngagedMatch(W, fighters, teams, count, mode);

    // ---------- ArenaIngress (walk-in: task 4.3) ----------
    public CharacterId GetWalkInFighter() => s.Ingress.GetWalkInFighter(W);
    public void ClearWalkIn() => s.Ingress.ClearWalkIn(W);
    public bool IsWalkInPending() => s.Ingress.IsWalkInPending(W);
    public bool BeginWalkIn(CharacterId c, MatchRules.MatchTeam team) => s.Ingress.BeginWalkIn(W, c, team);
    public void TickWalkIn() => s.Ingress.TickWalkIn(W);
    public bool IsWalkInArrived() => s.Ingress.IsWalkInArrived(W);

    // ---------- TownArena (M7) ----------
    public bool TownIsAftercare() => s.Town.IsAftercare(W);
    public void TownCancelPlannedNpcBout(string reason) => s.Town.CancelPlannedNpcBout(W, reason);
    public void TownOnMatchStarted() => s.Town.OnMatchStarted(W);
    public bool TownOwnsMatch() => s.Town.OwnsMatch(W);
    public bool TownIsPlayerMatch() => s.Town.IsPlayerMatch(W);
    public float TownActiveMarksMultiplier() => s.Town.GetActiveMarksMultiplier(W);
    public int TownActiveMarksBonus() => s.Town.GetActiveMarksBonus(W);
    public void TownOnMatchResult(SparPodium.Snapshot snapshot)
    {
        s.Town.OnMatchResult(W, snapshot);
        // port only (task 10.2): other mods hear about the result (src/Game/ArenaInterop.cs), after the town's own handling and while the session
        // still lists its fighters; a throw here never reaches the result block (whose next steps show the Results window)
        try { s.ResultObserver?.Invoke(W, snapshot); }
        catch (Exception e) { s.OwnLog?.Invoke($"interop: the match result could not be read for other mods ({e.GetType().Name}: {e.Message})"); }
    }
    public void TownOnMatchAborted() => s.Town.OnMatchAborted(W);

    // ---------- ResultsUI (M4) ----------
    public void ResultsCancel() => s.Results.Cancel(W);
    public void ResultsClose() => s.Results.Close(W);

    /// <summary>C++ <c>ResultsUI::ScheduleShow(1.0f)</c> (the Results window, task 4.6a); the result data (outcome, podium, rating and Marks deltas)
    /// is also logged as one <c>PG-SPAR</c> line (Meitou's log, port-only test output) from a copy of the frozen snapshot. Its numbers go through
    /// TestJson (task 9.3e): a non-finite one is written null, as the test-only verbs' JSON (the C++ writes no such line).</summary>
    public void ResultsScheduleShow(float delaySeconds)
    {
        s.Results.ScheduleShow(W, delaySeconds);
        s.OwnLog?.Invoke("PG-SPAR " + ResultJson(s.SparStats.GetSnapshot(), delaySeconds));
    }

    // the line itself: Core/Glue/SparResultLine.cs (task 9.3e review: pure, so the test project pins it)
    string ResultJson(SparPodium.Snapshot snap, float delay) =>
        SparResultLine.Json(snap, W.Tick, delay, slot => session.Participant(W, slot) is { } x ? CharacterIdText.Format(x) : "");

    // ---------- SparStats ----------
    public void StatsResetForMatch() => s.SparStats.ResetForMatch();
    public void StatsFreeze(SparPodium.OutcomeKind outcome, MatchRules.MatchMode mode, CharacterId lastStanding) =>
        s.SparStats.Freeze(W, session, outcome, mode, lastStanding.IsNone ? null : lastStanding);
    public SparPodium.Snapshot StatsSnapshot() => s.SparStats.GetMutableSnapshot();
    public void StatsOnEliminated(CharacterId c) => s.SparStats.OnEliminated(c.IsNone ? null : c);

    // ---------- LeaderboardStore (task 5.1) ----------
    public void ApplyFromSnapshot(SparPodium.Snapshot snapshot, LeaderboardData.Kind kind, float marksMultiplier, int marksTeamBonus) =>
        s.Store.ApplyFromSnapshot(W, snapshot, kind, marksMultiplier, marksTeamBonus);

    // ---------- CombatBalanceLog ----------
    public void CombatLogStart(MatchRules.MatchMode mode, CharacterId[] fighters, MatchRules.MatchTeam[] teams, int count)
    {
        bool town = TownOwnsMatch(), player = TownIsPlayerMatch();
        var kind = LeaderboardData.ForMatch(town);
        var infos = new CombatBalanceLog.FighterInfo[count];
        for (int i = 0; i < count; i++) infos[i] = s.CombatFighter(W, fighters[i], kind);
        // C++ CombatBalanceLog.cpp:148 LeaderboardStore::GetStandings(kind) (sorted, rows with a match, live names)
        CombatBalanceLog.StartMatch(s.CombatLog, mode, infos, teams, count, town, player, s.Store.GetStandings(W, kind),
            PgLogGlue.WallMs64());   // GetTickCount64 (CombatBalanceLog.cpp:167): real ms, not the sim clock (task 8.7c)
    }

    public void CombatLogFinish(SparPodium.Snapshot snapshot, SparStopReason reason)
    {
        var participants = new CombatBalanceLog.ParticipantInfo[snapshot.FighterCount];
        for (int i = 0; i < snapshot.FighterCount; i++)
        {
            var c = session.Participant(W, snapshot.Fighters[i].Id) ?? CharacterId.None;
            bool valid = !c.IsNone && PgEngine.Exists(W, c);
            RosterStatus.Snapshot health = default;
            bool hasHealth = valid && PgEngine.RosterHealth(W, c, out health, out _);   // RosterStatus::Read (CombatBalanceLog.cpp:218-219; task 7.2)
            participants[i] = new CombatBalanceLog.ParticipantInfo
            {
                Identity = valid ? s.Identity(W, c) : "",   // TownMatchmakingRuntime::Identity: FighterIdentity::Find, "" for none
                Valid = valid, Dead = valid && PgEngine.IsDead(W, c), Unconscious = valid && PgEngine.IsUnconscious(W, c),
                HasHealth = hasHealth, LowestLimb = health.LowestLimb, Blood = health.Blood, Recovery = health.Recovery,
            };
        }
        CombatBalanceLog.FinishMatch(s.CombatLog, snapshot, (int)reason, participants, session.PendingEndKind, PgLogGlue.WallMs64());   // :199 GetTickCount64 (task 8.7c)
    }

    public void CombatLogAbort(string reason) => CombatBalanceLog.AbortMatch(s.CombatLog, reason);
    public void CombatLogAbandon() => CombatBalanceLog.Abandon(s.CombatLog);

    // ---------- PrisonerUtil (M6) ----------
    public string BeginReturnToCages() { s.Prisoners.BeginReturnToCages(W, out string status); return status; }

    // ---------- port only ----------
    public bool SaveInFlight => s.SaveInFlight();
}
