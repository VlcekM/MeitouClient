using System.Numerics;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// Port of upstream/ProvingGrounds/plugin/src/TownBookie.h/.cpp (Proving Grounds by Shutori, GPLv3), M8 task 8.1a (TW §2.6, CAP 7, 10, 16, spike S4):
// the sim half. Every C++ decision is Core/Glue/TownBookieLogic.cs (fake-tested, Tests/Glue/TownBookieLogicTests.cs) over ITownBookieHost; this class
// binds it to a world and to CharacterIds. The state (C++ file-level globals, TownBookie.cpp:27-43) is the logic's, one object per world:
// ProvingGroundsSystem builds this object (for ArenaIngress, ArenaUI and the trade hook), attaches its services after the rest is built, and TownArena
// clears it through its AbandonWorldState chain (TownArena.cpp:1805). The two windows' models are src/Town/TownBookieUISim.cs; the windows themselves
// are task 8.1b. Nothing is saved besides the C++'s own: the ticket rides ArenaPersistence.PlannedNpcBout (Capture / RestorePlannedNpcBout), the credit
// LeaderboardStore.BookieCredit.
//
// Engine calls of the C++ (E1) and what replaces them:
//   ou->player->getFaction()->factionOwnerships (Wallet :44-47), getMoney / takeMoney / addMoney ... world.Player.Money (PlayerState, always present)
//       with Core/Glue/TownChallengeWalletRule (the long purse read as the C++ int; PayCredit's INT_MAX guard :90 = CanCredit), CAP 10
//   ou->player->getAllPlayerCharacters() (Player :48-53, AtBookie :67) ... PgEngine.FillPlayerCharacters into the host's one reused list (task 9.2b) / PgEngine.IsPlayer
//   Character::isValid / isDead / isUnconcious / getPosition ... PgEngine Exists / IsDead / IsUnconscious / Position
//   getCurrentTownLocation() ... Towns.CurrentTownOf as a town key (Towns.Key: two "no town" keys equal, as the C++ NULL pointers)
//   getGameData()->stringID == "111-Proving Grounds.mod" (IsBookie :274-277) ... PgEngine.RecordId == PgRecords.Bookie
//   getName ... PgEngine.Name; ArenaCombatProfile::Read ... Game.ArenaCombatProfile.Read; TownMatchmakingRuntime::Identity / Ability /
//       SelectionIdentity ... TownArena (TownMatchmakingRuntimeLogic over its host) / Game.ArenaCombatProfile.Ability
//   LeaderboardStore::HasActiveSave / GetBookieCredit / GetStandings(Town) ... src/Game/LeaderboardStore.cs
//   TownArena::HasPlannedNpcBout / GetPlannedNpcStartHours / BindRegistryNear / BeginTrial / GetStatus / IsNpcReadyForPreparation ... src/Town/TownArena.cs
//   TownArenaUI::Close ... src/Town/TownArenaUISim.cs Close
//   TownBookieUI / TownBookieResultUI Show / Refresh / Close / Destroy / IsVisible ... the logic's published views (TownBookieUISim), task 8.1b draws them
//   CombatBalanceLog::OpenMarket / MarketEvent / CloseMarket / Abandon ... Core/CombatBalanceLog.cs over the system's CombatLogHost, each fighter
//       ProvingGroundsSystem.CombatFighter(world, id, Town), the scheduled hours TownArena.GetPlannedNpcStartHours, the Town standings
//       (docs/divergences.md "Diagnostics and balance logs (task 8.7)", block "Task 8.7c"); no GetTickCount64 value is written by the market
//   ou->getTimeStamp_inGameHours().getTotalHours() ... World.GameHours; GetTickCount ... PgClock.NowMs (stands still while paused: ou->isPaused)
//   ou->showPlayerAMessage(text, true) ... PgEngine.Message; PGLog::Debug / Error ... PGLog.Debug / Error (texts byte for byte)
// Callers (C++ -> port): ArenaUI.cpp:3166 Tick (the frame-loop slot); ContextMenuHooks.cpp:317 Show (ContextMenuHooks.TickPendingTrade);
// ArenaUI.cpp:2986 / 3253 Close (PgArenaUIHost.TownBookieClose; Escape is the window's Closed report, task 8.1b); ArenaIngress.cpp:1641 / 1688
// HoldForBets / HasPendingWager (PgIngressHost); every TownArena.cpp call (TownArena's hosts).

/// <summary>The TownBookie of one world: the logic and its state, bound to a world for each call (sim thread, serial steps only).</summary>
sealed class TownBookie
{
    /// <summary>The services, attached by ProvingGroundsSystem once everything is built (the bookie is built first, for ArenaIngress and ArenaUI).</summary>
    internal sealed record Services(TownArena Town, Towns Towns, PgClock Clock, Func<LeaderboardStore> Store, Func<CombatBalanceLog.IHost> CombatLog,
        Func<World, CharacterId, LeaderboardData.Kind, CombatBalanceLog.FighterInfo> CombatFighter, TownArenaUISim TownUi);

    Services? s;
    readonly Host host;
    readonly TownBookieLogic<CharacterId> logic = new(CharacterId.None);

    internal TownBookie() => host = new Host(this);

    internal void Attach(Services services) => s = services;

    internal TownBookieLogic<CharacterId> Logic => logic;

    // Each entry point binds the host to its world and lets go after (a nested call from TownArena, e.g. Arrange -> BeginTrial -> BeginNpcBout, keeps
    // it bound until the outer one returns), so the host never holds a world between serial steps.
    ITownBookieHost<CharacterId> Enter(World world) => host.Enter(world);
    void Leave() => host.Leave();

    /// <summary>The windows' commands (TownBookieUISim): one logic call bound to the world; false before the services are attached.</summary>
    internal bool Run(World world, Func<ITownBookieHost<CharacterId>, bool> call)
    {
        if (s is null) return false;
        try { return call(Enter(world)); } finally { Leave(); }
    }

    /// <summary>TEST ONLY (pg town bookie): the last player message the bookie posted (ou->showPlayerAMessage).</summary>
    internal string LastNotice { get; set; } = "";
    /// <summary>TEST ONLY (pg town bookie): the sim clock (PgClock.NowMs).</summary>
    internal uint NowMsForTest => s?.Clock.NowMs ?? 0;

    /// <summary>TEST ONLY (pg town bookie): the saved credit (LeaderboardStore.BookieCredit).</summary>
    internal int CreditForTest(World world) => s?.Store().BookieCredit ?? 0;

    // Port only (the M8 quality rule): the balance-log writes the host catches (a broken log line never stops the market), each logged once per world
    // and counted (pg town bookie "failures").
    int logFailures;
    bool logFailureLogged;
    internal int LogFailures => logFailures;
    internal int FighterReadFailures => logic.FighterReadFailures;

    void LogFailed(Exception e)
    {
        ++logFailures;
        if (logFailureLogged) return;
        logFailureLogged = true;
        PGLog.Debug("Proving Grounds: bookie market log failed error=" + e.GetType().Name + ": " + e.Message);
        ProvingGroundsMod.Ctx?.LogError($"bookie market log failed: {e.GetType().Name}: {e.Message}");
    }

    // ---------- the frame loop and the windows ----------
    // C++: TownBookie.cpp:419-438 (ArenaUI.cpp:3166)
    internal void Tick(World world) { if (s is null) return; try { logic.Tick(Enter(world)); } finally { Leave(); } }
    // C++: TownBookie.cpp:439-448 (ContextMenuHooks.cpp:317)
    internal void Show(World world, CharacterId bookie) { if (s is null) return; try { logic.Show(Enter(world), bookie); } finally { Leave(); } }
    // C++: TownBookie.cpp:278
    internal bool IsVisible(World world) => logic.IsVisible;
    // C++: TownBookie.cpp:279-288 (ArenaUI.cpp:2986, :3253)
    internal void Close(World world) { if (s is null) return; try { logic.Close(Enter(world)); } finally { Leave(); } }
    // C++: TownBookie.cpp:274-277
    internal static bool IsBookie(World world, CharacterId c) => !c.IsNone && PgEngine.Exists(world, c) && PgEngine.RecordId(world, c) == PgRecords.Bookie;

    // ---------- ArenaIngress (task 4.3b) ----------
    internal bool HoldForBets(World world) => logic.HoldForBets();                          // C++ TownBookie.cpp:376
    internal bool HasPendingWager(World world) => logic.HasPendingWager();                  // C++ TownBookie.cpp:329

    // ---------- TownArena (tasks 7.1, 7.3) ----------
    internal void Settle(World world, int winner) { if (s is null) return; try { logic.Settle(Enter(world), winner); } finally { Leave(); } }                         // C++ :377-404
    internal void RefundUnrestoredWager(World world, ArenaPersistence.PlannedNpcBout saved) { if (s is null) return; try { logic.RefundUnrestoredWager(Enter(world), saved); } finally { Leave(); } }   // C++ :351-356
    internal void CancelNpcBout(World world, string reason)                                                                   // C++ :370-375
    {
        LastCancelReason = reason;
        if (s is null) return;
        try { logic.CancelNpcBout(Enter(world), reason); } finally { Leave(); }
    }
    /// <summary>TEST ONLY (pg town's lastCancel): the reason of the last CancelNpcBout.</summary>
    internal string LastCancelReason { get; private set; } = "";
    internal void AbandonWorldState(World world) { if (s is null) return; try { logic.AbandonWorldState(Enter(world)); } finally { Leave(); } }                      // C++ :405-418
    internal void BeginNpcBout(World world, CharacterId[] fighters, MatchRules.MatchTeam[] matchTeams, int count, double scoreA, double scoreB)
    {
        if (s is null) return;
        try { logic.BeginNpcBout(Enter(world), fighters, matchTeams, count, scoreA, scoreB); } finally { Leave(); }                                  // C++ :289-319
    }
    internal bool PreparationValid(World world) { if (s is null) return false; try { return logic.PreparationValid(Enter(world)); } finally { Leave(); } }                              // C++ :320-328
    internal void NpcAssembling(World world) => logic.NpcAssembling();                                                             // C++ :365-369
    internal void CombatStarted(World world) { if (s is null) return; try { logic.CombatStarted(Enter(world)); } finally { Leave(); } }                                 // C++ :357-364
    internal void CapturePlannedNpcBout(World? world, ArenaPersistence.PlannedNpcBout output) => logic.CapturePlannedNpcBout(output);   // C++ :330-336
    internal bool RestorePlannedNpcBout(World world, ArenaPersistence.PlannedNpcBout saved) => logic.RestorePlannedNpcBout(saved);     // C++ :337-350

    /// <summary>The F10 phase row.</summary>
    internal string Phase =>
        (logic.IsMarketOpen ? (logic.IsFighting ? "market, combat" : "market open") : logic.IsCompleted ? "bout finished" : "no market")
        + (logic.Ticket.Pending ? ", wager pending" : "") + (logic.IsVisible ? ", window open" : "")
        + (logFailures + logic.FighterReadFailures > 0
            ? $"; failures: log {logFailures}, fighter reads {logic.FighterReadFailures}" : "");

    sealed class Host(TownBookie owner) : ITownBookieHost<CharacterId>
    {
        World? world;
        int depth;
        internal Host Enter(World w) { world = w; ++depth; return this; }
        internal void Leave() { if (--depth <= 0) { depth = 0; world = null; } }
        World W => world ?? throw new InvalidOperationException("TownBookie used before a world was bound");
        Services S => owner.s ?? throw new InvalidOperationException("TownBookie used before its services were attached");

        public CharacterId None => CharacterId.None;
        public bool IsNull(CharacterId c) => c.IsNone;
        public bool Valid(CharacterId c) => !c.IsNone && PgEngine.Exists(W, c);
        public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
        public bool IsUnconscious(CharacterId c) => PgEngine.IsUnconscious(W, c);
        public bool IsPlayer(CharacterId c) => PgEngine.IsPlayer(W, c);
        // task 9.2b: one list per host, refilled per call (AtBookie reads it in one loop and keeps nothing; the window's model asks it each tick)
        readonly List<CharacterId> players = [];
        public IReadOnlyList<CharacterId> PlayerCharacters() { PgEngine.FillPlayerCharacters(W, players); return players; }
        public long CurrentTown(CharacterId c) => Towns.Key(S.Towns.CurrentTownOf(W, c));
        public bool Position(CharacterId c, out Vector3 position)
        {
            if (PgEngine.Position(W, c) is { } p) { position = p; return true; }
            position = default;
            return false;
        }
        public bool IsBookieRecord(CharacterId c) => TownBookie.IsBookie(W, c);
        public long Money => W.Player.Money;
        public void AddMoney(long delta) => W.Player.Money += delta;
        public bool HasActiveSave => S.Store().HasActiveSave();
        public int BookieCredit { get => S.Store().BookieCredit; set => S.Store().BookieCredit = value; }
        public IReadOnlyList<LeaderboardData.Standing> TownStandings() => S.Store().GetStandings(W, LeaderboardData.Kind.Town);
        public string Name(CharacterId c) => PgEngine.Name(W, c) ?? "";
        public Core.ArenaCombatProfile.Profile Profile(CharacterId c) => Game.ArenaCombatProfile.Read(W, c);
        public string Identity(CharacterId c) => S.Town.IdentityOf(W, c);
        public string SelectionIdentity(CharacterId c) => S.Town.SelectionIdentityOf(W, c);
        public double Ability(CharacterId c) => Game.ArenaCombatProfile.Ability(W, c);
        public bool IsNpcReadyForPreparation(CharacterId c) => S.Town.IsNpcReadyForPreparation(W, c);
        public bool HasPlannedNpcBout => S.Town.HasPlannedNpcBout();
        public double PlannedNpcStartHours => S.Town.GetPlannedNpcStartHours();
        public double NowHours => W.GameHours;
        public bool BindRegistryNear(CharacterId c) => S.Town.BindRegistryNear(W, c);
        public void BeginTrial() => S.Town.BeginTrial(W);
        public string TownStatus() => S.Town.GetStatus(W);
        public void CloseTownRegistryWindow() => S.TownUi.Close(W);
        public uint NowMs => S.Clock.NowMs;

        // CombatBalanceLog over the system's host (task 8.7c's caller list); a throwing write is counted, never stops the market
        public void LogOpenMarket(CharacterId[] fighters, MatchRules.MatchTeam[] teams, int count, double scoreA, double scoreB, bool eligible)
        {
            try
            {
                int n = Math.Min(count, Math.Min(fighters.Length, teams.Length));
                var infos = new CombatBalanceLog.FighterInfo[n];
                for (int i = 0; i < n; i++) infos[i] = S.CombatFighter(W, fighters[i], LeaderboardData.Kind.Town);
                CombatBalanceLog.OpenMarket(S.CombatLog(), infos, teams, n, scoreA, scoreB, eligible, S.Town.GetPlannedNpcStartHours(),
                    S.Store().GetStandings(W, LeaderboardData.Kind.Town));
            }
            catch (Exception e) { owner.LogFailed(e); }
        }
        public void LogMarketEvent(string ev, string fields)
        {
            try { CombatBalanceLog.MarketEvent(S.CombatLog(), ev, fields); }
            catch (Exception e) { owner.LogFailed(e); }
        }
        public void LogCloseMarket(int winner, int side, int stake, int quotedReturn, int credit, bool hadTicket)
        {
            try { CombatBalanceLog.CloseMarket(S.CombatLog(), winner, side, stake, quotedReturn, credit, hadTicket); }
            catch (Exception e) { owner.LogFailed(e); }
        }
        public void LogAbandon()
        {
            try { CombatBalanceLog.Abandon(S.CombatLog()); }
            catch (Exception e) { owner.LogFailed(e); }
        }
        public void Debug(string line) => PGLog.Debug(line);
        public void Error(string line) => PGLog.Error(line);
        public void Message(string text) { owner.LastNotice = text; PgEngine.Message(W, text); }
    }
}
