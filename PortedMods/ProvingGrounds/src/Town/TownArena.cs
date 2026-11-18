using System.Globalization;
using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// Port of upstream/ProvingGrounds/plugin/src/TownArena.h/.cpp (Proving Grounds by Shutori, GPLv3), M7 (TW §2.1-2.5). The C++ decisions are
// Core/Glue/TownArenaLogic*.cs (fake-tested, Tests/Glue/TownArenaLogicTests.cs) over ITownHost; this class binds them to a world and to CharacterIds.
// Its parts, by the plan's task groups (M7-town-core.md): this file = the members every caller uses and the host (task 7.1: when Scratch is
// active, the schedule clock, the stop rules, save safety's cancels); src/Town/TownMatchmakingRuntime.cs (task 7.2: the host's roster and readiness
// reads, the test-only pg town roster / search); TownArena.Bouts.cs (task 7.3: the bouts and the borrow list) and TownArena.Cards.cs (task 7.5: the card, booking and the wallet, src/Town/TownChallengeWallet.cs). The state (C++ g_phase, g_townRegistry, g_plannedNpc, ... TownArena.cpp:56-118) is the logic's one TownArenaState per world: this
// object is built by ProvingGroundsSystem, which is built again for every world; AbandonWorldState runs on the old world's object when a new
// world takes over (ProvingGroundsSystem.TakeOver; the C++ calls it from WorldLifecycle::AbandonWorldState on GameWorld clear).
// Core's class of this name is Core.TownArena (captions, texts, rules); never use the bare name in this namespace for it.
//
// Engine calls of the C++ in task 7.1's ranges (E1) and what replaces them:
//   ou / ou->player (null checks) ... a bound world (the host is bound to the serial step's World); PgEngine.PlayerCharacters never null
//   ou->isPaused() ... false (a paused world runs no SlowWorld, World.Interface.cs:16-40; as ArenaIngressLogic)
//   GetTickCount() ... PgClock.NowMs (sim ms, frozen while paused)
//   ou->player->getAllPlayerCharacters() ... PgEngine.FillPlayerCharacters(world, the host's reused list) (task 9.2b)
//   Character::isValid / getPosition ... PgEngine.Exists / Position
//   Character::getCurrentTownLocation() ... Towns.CurrentTownOf(world, id) (the status town: CharacterCold.StatusTown, then AiSystem.StatusTownAt,
//       then the building the character stands in; task 7.2 review), as a Core TownKey (Towns.Key)
//   ou->getObjectsWithinSphere(pos, 2500, BUILDING, 512, NULL) ... PgEngine.BuildingsNear(world, data, pos, 2500, null, 512) + ArenaIdentity.From
//       (town buildings of every placed site first, then the player's construction; the C++ order is the engine's: docs/divergences.md "TownArena")
//   Building::getTown() ... PgEngine.PgBuilding.Town (TownBuildingBuilder.MemberOf for a town building, BuildSite.Town for a player one)
//   hand::getBuilding() / Building::isValid() of a held registry ... ArenaIdentity.Resolve(world, data, ref, out town) (null: gone / another record)
//   ArenaIdentity::IsTownRegistry / IsRegistryFinished ... ArenaIdentityLogic (record 195 by its string id, never a site index: census)
//   WorldLifecycle::IsArenaOperationBusy ... WorldLifecycle.IsArenaOperationBusy(world); port-only: WorldLifecycle.SaveInFlight
//   ArenaIngress::IsPending / Cancel ... ArenaIngress; SparSession::IsActive / GetParticipantCount / GetParticipant / AbortForSave ... SparSession
//   ArenaMedical::GetProtocol / SetProtocol ... ArenaMedical (the protocol is process-wide)
//   TownBookie (Settle, RefundUnrestoredWager, CancelNpcBout) ... src/Town/TownBookie.cs (task 8.1a); TownAnnouncer (Release, TickReleases, AbandonWorldState) ...
//       src/Town/TownAnnouncer.cs (task 8.3; TownArena attaches its services to it);
//   TownLimbShop (IsActive, Tick, AbandonWorldState) ... src/Town/TownLimbShop.cs (task 8.5; TownArena attaches its services to it)
//   TownAftercare::Release / TickBedRecovery / AbandonWorldState ... src/Town/TownAftercare.cs (task 8.4a; TickBedRecovery task 8.4c)
//   TownSpectators (Release, Tick, AbandonWorldState) ... src/Town/TownSpectators.cs (task 8.2)
//   TownCorpseCleanup (Release, Tick, AbandonWorldState) ... src/Town/TownCorpseCleanup.cs (task 8.6); TownDiagnosticRun::CancelPending ... src/Town/TownDiagnosticRun.cs (task 8.7a);
//       TownMatchmakingSearch::Shutdown ... the world's search (task 7.2: src/Town/TownMatchmakingSearch.cs)
//   PGLog::Debug ... PGLog.Debug (the C++ texts byte for byte, Core.TownArena.UpdateStatus)
//   IsIncidentPair / IsActiveCollateralIncidentPair (:1415-1438, task 7.4) ... TownArenaLogic (Core/Glue/TownArenaLogic.Rules.cs); the town rules
//       themselves (crime, relations, the first-aid finder, carrying, the after-bout forgiveness) ... src/Town/TownCombatRules.cs (its own E1 list)
//
// Callers (C++ -> port): the frame loop (Tick, ArenaUI.cpp:3160), save safety (WorldLifecycle: IsBusy, HasBooking, Cancel, CancelForSave),
// SparSession (PgSparHost), ArenaIngress (PgIngressHost), the Arena window (PgArenaUIHost), the debug menu (DebugMenuSim), CombatHooks
// (CopyFighters, CopyIncidentState and OwnsMatch for the published view), the TEST ONLY pg town.

/// <summary>The TownArena of one world: the logic and its state, bound to a world for each call (sim thread, serial steps only).</summary>
sealed partial class TownArena
{
    readonly PopulationData data;
    readonly Towns towns;
    readonly PgClock clock;
    readonly ArenaIngress ingress;
    readonly Func<SparSession> spar;
    readonly Func<ArenaMedical> medical;
    readonly Func<WorldLifecycle> lifecycle;
    readonly TownBookie bookie;
    readonly TownAnnouncer announcer;
    readonly TownLimbShop limbShop;
    readonly Func<FighterIdentity> identity;
    readonly Func<LeaderboardStore> store;
    readonly Func<CombatBalanceLog.IHost> combatLog;   // task 7.5: ChallengeAccepted / ChallengeSettled
    readonly TownMatchmakingSearch search = new();   // task 7.2: this world's background search (src/Town/TownMatchmakingSearch.cs)
    readonly TownCombatRules rules = new();          // task 7.4: the after-bout forgiveness (src/Town/TownCombatRules.cs)
    readonly Holds holds = new();                    // task 8.4a: the world's one hold registry (src/Engine/Holds.cs; spike S10)
    readonly TownAftercare aftercare;                // task 8.4a: the medics, the hospital's beds, standby (src/Town/TownAftercare.cs)
    readonly TownSpectators spectators;              // task 8.2: the crowd on the Arena Pillows (src/Town/TownSpectators.cs)
    readonly TownCorpseCleanup corpseCleanup;        // task 8.6: the cage handler and the corpse furnace (src/Town/TownCorpseCleanup.cs)
    readonly Host host;
    readonly TownArenaLogic<CharacterId> logic = new(CharacterId.None);

    /// <summary>SparSession, ArenaMedical, WorldLifecycle, the fighter identity and the leaderboard store are read lazily (ProvingGroundsSystem
    /// builds them after this object).</summary>
    internal TownArena(PopulationData data, Towns towns, PgClock clock, ArenaIngress ingress, Func<SparSession> spar, Func<ArenaMedical> medical,
        Func<WorldLifecycle> lifecycle, TownBookie bookie, TownAnnouncer announcer, TownLimbShop limbShop, Func<FighterIdentity> identity,
        Func<LeaderboardStore> store, Func<CombatBalanceLog.IHost> combatLog)
    {
        this.combatLog = combatLog;
        this.identity = identity;
        this.store = store;
        this.data = data;
        this.towns = towns;
        this.clock = clock;
        this.ingress = ingress;
        this.spar = spar;
        this.medical = medical;
        this.lifecycle = lifecycle;
        this.bookie = bookie;
        this.announcer = announcer;
        this.limbShop = limbShop;
        aftercare = new TownAftercare(data, clock, holds, towns, spar);
        spectators = new TownSpectators(data, clock, holds, towns, this, spar);
        corpseCleanup = new TownCorpseCleanup(data, clock, towns, aftercare, spectators);
        limbShop.Attach(data, clock, towns, aftercare, identity, store);   // task 8.5: the limb shop's services (it is built first, for the save path)
        announcer.Attach(data, clock, towns, aftercare, holds, IsTownFactionCharacter);   // task 8.3: the announcer's services (it is built first, for ArenaIngress)
        host = new Host(this);
    }

    /// <summary>The world's hold registry (task 8.4a): ArenaIngress's park and SparSession's bench ask it.</summary>
    internal Holds Holds => holds;

    // Port only (task 8.4b quality review; per step since the 8.4c quality review): the aftercare steps the host catches, each logged once per world
    // (Meitou's log and pg_debug.log) and counted (F10's TownArena phase row, pg town care "failures"). Treat catches its own throws (the logic's
    // TreatFailures), and the bed recovery its entries' (BedEntryFailures).
    internal const int StepRelease = 0, StepBedRecovery = 1;
    static readonly string[] AftercareStepNames = ["release", "bed recovery"];
    readonly bool[] aftercareFailureLogged = new bool[2];
    readonly int[] aftercareFailures = new int[2];

    void LogAftercareFailure(int step, Exception e)
    {
        ++aftercareFailures[step];
        if (aftercareFailureLogged[step]) return;
        aftercareFailureLogged[step] = true;
        string name = AftercareStepNames[step];
        PGLog.Debug("Proving Grounds: aftercare " + name + " failed error=" + e.GetType().Name + ": " + e.Message);
        ProvingGroundsMod.Ctx?.LogError($"aftercare {name} failed: {e.GetType().Name}: {e.Message}");
    }

    /// <summary>The aftercare's failure counts (empty when none): release, bed recovery, treat, bed entries.</summary>
    internal string AftercareFailureText()
    {
        var l = aftercare.Logic;
        int treat = l.TreatFailures, entries = l.BedEntryFailures;
        if (aftercareFailures[0] == 0 && aftercareFailures[1] == 0 && treat == 0 && entries == 0) return "";
        return string.Create(CultureInfo.InvariantCulture,
            $"aftercare failures: release {aftercareFailures[0]}, bed recovery {aftercareFailures[1]}, treat {treat}, bed entries {entries}");
    }

    /// <summary>pg town care's "failures" object.</summary>
    internal string AftercareFailureJson()
    {
        var l = aftercare.Logic;
        return string.Create(CultureInfo.InvariantCulture,
            $"{{\"release\":{aftercareFailures[0]},\"bedRecovery\":{aftercareFailures[1]},\"treat\":{l.TreatFailures},\"bedEntries\":{l.BedEntryFailures}}}");
    }

    // Port only (task 8.2, as the aftercare's above): the spectator steps the host catches (Begin, Tick, ReactToResult, Release), each logged once per
    // world and counted (F10's TownArena phase row, pg town crowd "failures"). A throw never stops TownArena.Tick's other subsystems; Release's
    // backstop lets every Spectator hold go. A guest's own maintenance step catches its own throw (the logic's GuestFailures).
    internal const int StepSpectatorBegin = 0, StepSpectatorTick = 1, StepSpectatorReact = 2, StepSpectatorRelease = 3;
    static readonly string[] SpectatorStepNames = ["begin", "tick", "result", "release"];
    readonly bool[] spectatorFailureLogged = new bool[4];
    readonly int[] spectatorFailures = new int[4];

    void LogSpectatorFailure(int step, Exception e)
    {
        ++spectatorFailures[step];
        if (spectatorFailureLogged[step]) return;
        spectatorFailureLogged[step] = true;
        string name = SpectatorStepNames[step];
        PGLog.Debug("Proving Grounds: spectators " + name + " failed error=" + e.GetType().Name + ": " + e.Message);
        ProvingGroundsMod.Ctx?.LogError($"spectators {name} failed: {e.GetType().Name}: {e.Message}");
    }

    /// <summary>The spectators' failure counts (empty when none).</summary>
    internal string SpectatorFailureText()
    {
        int guest = spectators.Logic.GuestFailures;
        if (spectatorFailures[0] == 0 && spectatorFailures[1] == 0 && spectatorFailures[2] == 0 && spectatorFailures[3] == 0 && guest == 0) return "";
        return string.Create(CultureInfo.InvariantCulture,
            $"spectator failures: begin {spectatorFailures[0]}, tick {spectatorFailures[1]}, result {spectatorFailures[2]}, release {spectatorFailures[3]}, guests {guest}");
    }

    /// <summary>pg town crowd's "failures" object.</summary>
    internal string SpectatorFailureJson() => string.Create(CultureInfo.InvariantCulture,
        $"{{\"begin\":{spectatorFailures[0]},\"tick\":{spectatorFailures[1]},\"result\":{spectatorFailures[2]},\"release\":{spectatorFailures[3]},\"guests\":{spectators.Logic.GuestFailures}}}");

    // Port only (task 8.6, as the spectators' above): the corpse cleanup steps the host catches (Begin, Tick, AdmitArenaDeaths, HasPending, Release),
    // each logged once per world and counted (F10's TownArena phase row, pg town corpses "failures"). A throw never stops TownArena.Tick's other
    // subsystems; Release's backstop gives back every external reservation the cleanup took. One corpse's own step catches its own throw (the
    // logic's CorpseFailures).
    internal const int StepCorpseBegin = 0, StepCorpseTick = 1, StepCorpseAdmit = 2, StepCorpsePending = 3, StepCorpseRelease = 4;
    static readonly string[] CorpseStepNames = ["begin", "tick", "sweep", "pending", "release"];
    readonly bool[] corpseFailureLogged = new bool[5];
    readonly int[] corpseFailures = new int[5];

    void LogCorpseFailure(int step, Exception e)
    {
        ++corpseFailures[step];
        if (corpseFailureLogged[step]) return;
        corpseFailureLogged[step] = true;
        string name = CorpseStepNames[step];
        PGLog.Debug("Proving Grounds: corpse cleanup " + name + " failed error=" + e.GetType().Name + ": " + e.Message);
        ProvingGroundsMod.Ctx?.LogError($"corpse cleanup {name} failed: {e.GetType().Name}: {e.Message}");
    }

    /// <summary>The corpse cleanup's failure counts (empty when none).</summary>
    internal string CorpseFailureText()
    {
        var l = corpseCleanup.Logic;
        int total = l.CorpseFailures + l.DisposeFailures;
        for (int i = 0; i < corpseFailures.Length; i++) total += corpseFailures[i];
        if (total == 0) return "";
        return string.Create(CultureInfo.InvariantCulture,
            $"corpse failures: begin {corpseFailures[0]}, tick {corpseFailures[1]}, sweep {corpseFailures[2]}, pending {corpseFailures[3]}, release {corpseFailures[4]}, corpses {l.CorpseFailures}, disposals {l.DisposeFailures}");
    }

    /// <summary>pg town corpses' "failures" object.</summary>
    internal string CorpseFailureJson() => string.Create(CultureInfo.InvariantCulture,
        $"{{\"begin\":{corpseFailures[0]},\"tick\":{corpseFailures[1]},\"sweep\":{corpseFailures[2]},\"pending\":{corpseFailures[3]},\"release\":{corpseFailures[4]},\"corpses\":{corpseCleanup.Logic.CorpseFailures},\"disposals\":{corpseCleanup.Logic.DisposeFailures}}}");

    /// <summary>The world's corpse cleanup (task 8.6).</summary>
    internal TownCorpseCleanup CorpseCleanup => corpseCleanup;

    /// <summary>The world's spectators (task 8.2): task 8.6 releases the cage handler it takes from the audience through it.</summary>
    internal TownSpectators Spectators => spectators;

    /// <summary>Task 8.2: whether <paramref name="c"/> belongs to the town faction 91 (the roster's rule, TownMatchmakingRuntime.cs).</summary>
    internal bool IsTownFactionCharacter(World world, CharacterId c) => TownFaction() is int f && f >= 0 && PgEngine.Faction(world, c) == f;

    /// <summary>The world's aftercare (task 8.4a): SparSession's bench reserves through its tables.</summary>
    internal TownAftercare Aftercare => aftercare;

    /// <summary>The C++ file-level state (read by the test verb pg town).</summary>
    internal TownArenaState<CharacterId> State => logic.State;

    ITownHost<CharacterId> H(World world) => host.Bind(world);

    // ---------- the frame loop ----------

    // C++: TownArena.cpp:1860-2066, called from ArenaUI.cpp ForgottenGUI_update_hook (:3160)
    internal void Tick(World world)
    {
        holds.ReassertAll(world);   // task 8.4a (spike S10): the one re-assert pass of the SlowWorld over every owner's holds (each guarded)
        logic.Tick(H(world));
        WatchIntrudersForTest(world);   // TEST ONLY: the lastBout copy runs every tick (cheap); only the intruder scan waits for the armed test verbs
    }

    // ---------- save safety (task 2.5, WorldLifecycle.cpp) ----------
    internal bool IsBusy(World world) => logic.IsBusy();                 // C++ TownArena.cpp:1405
    internal bool HasBooking(World world) => logic.HasBooking();         // C++ TownArena.cpp:1319
    internal void Cancel(World world) => logic.Cancel(H(world));         // C++ TownArena.cpp:1781-1785
    internal void CancelForSave(World world) => logic.CancelForSave(H(world));   // C++ TownArena.cpp:1786-1795

    /// <summary>C++: TownArena.cpp:1797-1858, on the old world's object when a new world takes over (ProvingGroundsSystem.TakeOver). Reads no
    /// actor: the other subsystems' resets and, when an event ran, the medical protocol put back (it is process-wide).</summary>
    internal void AbandonWorldState(World world)
    {
        rules.Clear();   // task 7.4: the old world's TEMP_ENEMY record is dropped, never cleared into the new world
        holds.Clear();   // task 8.4a: no actor access (the old world's people are gone with it); a new world never inherits a hold
        spectators.ForgetTestSitter();   // task 8.2: TEST ONLY pg town sit
        logic.AbandonWorldState(H(world));
    }

    // ---------- SparSession (task 3.3) ----------
    internal bool IsAftercare(World world) => logic.IsAftercare();                                       // C++ TownArena.cpp:1406
    internal void CancelPlannedNpcBout(World world, string reason) => logic.CancelPlannedNpcBout(H(world), reason);   // C++ TownArena.cpp:620-632
    // C++ TownArena.cpp:1464-1483 (task 7.3). Task 7.4: under the C++'s own test (:1466, phase Preparing and the town owns the match), the town
    // rules record the bout's TEMP_ENEMY state for the after-bout clearing (src/Town/TownCombatRules.cs), through the host's TownRulesBoutStarted.
    internal void OnMatchStarted(World world) => logic.OnMatchStarted(H(world));
    internal bool OwnsMatch(World world) => logic.OwnsMatch(H(world));                                   // C++ TownArena.cpp:1457-1462
    internal bool IsPlayerMatch(World world) => logic.IsPlayerMatch();                                   // C++ TownArena.cpp:1320
    internal float GetActiveMarksMultiplier(World world) => logic.GetActiveMarksMultiplier(H(world));    // C++ TownArena.cpp:1331-1333
    internal int GetActiveMarksBonus(World world) => logic.GetActiveMarksBonus(H(world));                // C++ TownArena.cpp:1334-1336
    // C++ TownArena.cpp:1074-1137 / :1138-1149 (task 7.3 the NPC halves, task 7.5 the player halves). Task 7.4's after-bout TEMP_ENEMY clearing
    // runs at the town event's end (Finish, after the aftercare: task 8.4b, ITownHost.TownRulesEventEnded), not here.
    internal void OnMatchResult(World world, SparPodium.Snapshot result) => logic.OnMatchResult(H(world), result);
    internal void OnMatchAborted(World world) => logic.OnMatchAborted(H(world));
    internal bool HasPlannedNpcBout() => logic.HasPlannedNpcBout();                                     // C++ TownArena.cpp:591
    internal double GetPlannedNpcStartHours() => logic.GetPlannedNpcStartHours();                       // C++ TownArena.cpp:592

    // CombatHooks reads it (task 3.2b, CombatHooks.cpp:162-163 TownArena::IsFighter): the town bout's fighters, copied into the combat hooks'
    // published view on each serial step.
    internal void CopyFighters(World world, List<CharacterId> into) => logic.CopyFighters(into);        // C++ TownArena.cpp:1409-1414 IsFighter

    /// <summary>Port only (task 10.1): the town event's fighters from the walk in to the end of the aftercare (src/Game/ArenaInterop.cs).</summary>
    internal void CopyEventFighters(List<CharacterId> into) => logic.CopyEventFighters(into);

    /// <summary>Port only (task 10.1): the town faction's index in PopulationData.Factions (-1 when absent).</summary>
    internal int TownFactionIndex => TownFaction();

    // CombatHooks reads it (task 7.4): the state of IsIncidentPair / IsActiveCollateralIncidentPair (TownArena.cpp:1415-1438), published in the view,
    // whose predicates run the same loops as TownArenaLogic's (TownIncidentPairs); the patches read only the view.
    internal void CopyIncidentState(World world, List<CharacterId> fighters, List<MatchRules.MatchTeam> teams, out bool retained, out bool collateral) =>
        logic.CopyIncidentState(H(world), fighters, teams, out retained, out collateral);

    // ---------- ArenaIngress (task 4.3b) ----------
    internal bool IsFighter(World world, CharacterId c) => logic.IsFighter(H(world), c);                 // C++ TownArena.cpp:1409-1414
    internal bool ValidatePreparation(World world) => logic.ValidatePreparation(H(world));              // C++ TownArena.cpp:462-465
    internal void QuarantineIngressFighter(World world, CharacterId c) => logic.QuarantineIngressFighter(H(world), c);   // C++ TownArena.cpp:441-446
    internal bool IsNpcReadyForPreparation(World world, CharacterId c) => logic.IsNpcReadyForPreparation(H(world), c);   // C++ TownArena.cpp:433-440

    // ---------- the debug menu (task 4.6b, DebugMenu.cpp:462-476) ----------
    internal bool BeginTrial(World world) => logic.BeginTrial(H(world));                                // C++ TownArena.cpp:1766-1779
    internal bool IsScheduleEnabled(World world) => logic.IsScheduleEnabled();                           // C++ TownArena.cpp:1318
    internal void SetScheduleEnabled(World world, bool enabled) => logic.SetScheduleEnabled(H(world), enabled);   // C++ TownArena.cpp:1352-1359
    internal string GetStatus(World world) => logic.GetStatus();                                          // C++ TownArena.cpp:1407

    // ---------- registries (TownArenaUI, task 7.6) ----------
    internal void BindRegistry(ArenaBuilding? registry) => logic.BindRegistry(registry);                 // C++ TownArena.cpp:1296-1300

    // ---------- the bookie (task 8.1a) ----------
    internal bool BindRegistryNear(World world, CharacterId c) => logic.BindRegistryNear(H(world), c);   // C++ TownArena.cpp:1301-1317
    internal string IdentityOf(World world, CharacterId c) => TownMatchmakingRuntimeLogic.Identity(H(world), c);   // C++ TownMatchmakingRuntime::Identity
    internal string SelectionIdentityOf(World world, CharacterId c) => TownMatchmakingRuntimeLogic.SelectionIdentity(H(world), c);   // C++ ::SelectionIdentity

    // ---------- port only ----------

    /// <summary>The F10 status row's phase (sim thread, at publish): idle / active in Scratch / the event phase, and the status text.</summary>
    internal string Phase(World? world)
    {
        if (world is null) return "idle";
        string where = logic.IsBusy() ? logic.State.Phase.ToString().ToLowerInvariant() : logic.IsActive(H(world)) ? "active in Scratch" : "idle";
        string failures = AftercareFailureText(), crowd = SpectatorFailureText(), corpses = CorpseFailureText();
        return where + ": " + logic.GetStatus() + (failures.Length > 0 ? "; " + failures : "") + (crowd.Length > 0 ? "; " + crowd : "")
            + (corpses.Length > 0 ? "; " + corpses : "");
    }

    /// <summary>TEST ONLY <c>pg town [status|start|roster|search|search-poll|model|cards|book|cancel|money|refresh|hold]</c>: one PG-TOWN line (the
    /// town state and, task 7.3, the bout: the planned and active fighters with their teams and town-board rows, the borrow list, the start time;
    /// <c>start</c> first plans a bout when none is announced and makes the announced one due now (TownArena.Bouts.cs StartForTest); task 7.2:
    /// <c>roster</c> adds the roster snapshot's counts, <c>search</c> queues one card search on the background worker, <c>search-poll</c> takes one
    /// result, src/Town/TownMatchmakingRuntime.cs; task 7.5: <c>cards &lt;ids&gt;</c> the cards for a lineup, <c>book &lt;0-4|direct&gt; &lt;ids&gt;</c>,
    /// <c>cancel</c>, <c>money &lt;cats&gt;</c> (the purse), <c>refresh</c> (the paid refresh), <c>hold on|off</c> (holds the next event's start); task 8.4a: <c>care</c> (the aftercare's state); task 8.4c: <c>heal &lt;id&gt;</c> (one character healed, src/Town/TownArena.Cards.cs);
    /// <c>model</c> (task 7.6, the window's published model) is answered by src/Engine/PgConsole.cs from the snapshot and never reaches here).
    /// Answers the same JSON.</summary>
    internal string Command(World world, IReadOnlyList<string> args)
    {
        string verb = args.Count > 0 ? args[0] : "status";
        string extra = "";
        CharacterId? limbDetail = null;
        string? error = verb switch
        {
            "status" => null,
            "start" => StartForTest(world),
            "roster" => null,
            "search" => QueueSearchForTest(world, out extra),
            "search-poll" => null,
            "cards" => CardsForTest(world, args),        // task 7.5
            "book" => BookForTest(world, args),          // task 7.5
            "cancel" => CancelForTest(world),            // task 7.5
            "money" => MoneyForTest(world, args),        // task 7.5
            "refresh" => RefreshForTest(world),          // task 7.5
            "hold" => HoldForTest(args),                 // task 7.5 review
            "care" => null,                              // task 8.4a: the aftercare's state
            "heal" => HealForTest(world, args),          // task 8.4c: the bed recovery's healed patient
            "crowd" => null,                             // task 8.2: the spectators' state
            "sit" => SitForTest(world, args),            // task 8.2: spike S16, one NPC on a pillow
            "stand" => spectators.StandForTest(world),   // task 8.2: the test sitter stands up and is let go
            "corpses" => null,                           // task 8.6: the corpse cleanup's state
            "limbs" => LimbsForTest(args, out limbDetail),   // task 8.5: the limb shop's state [and one character's limbs]
            "sever" => SeverForTest(world, args, out limbDetail),   // task 8.5: one limb cut off (Meitou's Sever)
            "announcer" => null,                         // task 8.3: the announcer's state
            _ => "usage pg town [status|start|roster|search|search-poll|model|cards <ids>|book <0-4|direct> <ids>|cancel|refresh|money <cats>|hold on|off|care|heal <id>|crowd|sit [id]|stand|corpses|limbs [id]|sever <id> <0-3>|announcer]",
        };
        if (verb == "roster") extra = RosterJson(world);
        else if (verb == "care") extra = aftercare.CareJson(world, AftercareFailureJson());
        else if (verb is "crowd" or "sit" or "stand") extra = spectators.CrowdJson(world, SpectatorFailureJson());
        else if (verb == "corpses") extra = corpseCleanup.CorpsesJson(world, CorpseFailureJson());
        else if (verb is "limbs" or "sever") extra = limbShop.LimbsJson(world, limbDetail);
        else if (verb == "announcer") extra = announcer.AnnouncerJson(world);
        else if (verb == "search-poll") extra = PollSearchForTest(world);
        string line = Answer(world, verb, error, extra);
        ProvingGroundsMod.Ctx?.Log("PG-TOWN " + line);
        return line;
    }

    string Answer(World world, string verb, string? error, string extra = "")
    {
        var h = H(world);
        var st = logic.State;
        static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
        static string B(bool b) => b ? "true" : "false";
        string Ref(ArenaBuildingRef? r) => r is { } x ? PGLog.Quote(ArenaIdentity.KeyText(x)) : "null";
        var players = PgEngine.PlayerCharacters(world);
        int inRegistryTown = 0;
        PlacedBuilding? bound = st.TownRegistry is { } tr ? h.Resolve(tr) : null;
        long registryTown = bound is { } placed ? placed.Town : TownKey.None;
        foreach (var p in players) if (TownKey.Same(h.CurrentTown(p), registryTown)) inRegistryTown++;
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"event\":\"town\",\"verb\":").Append(PGLog.Quote(verb)).Append(",\"ok\":").Append(B(error is null))
          .Append(",\"tick\":").Append(N(world.Tick));
        if (error is not null) sb.Append(",\"error\":").Append(PGLog.Quote(error));
        sb.Append(",\"active\":").Append(B(logic.IsActive(h)))
          .Append(",\"phase\":").Append(PGLog.Quote(st.Phase.ToString()))
          .Append(",\"busy\":").Append(B(logic.IsBusy()))
          .Append(",\"status\":").Append(PGLog.Quote(logic.GetStatus()))
          .Append(",\"registry\":").Append(Ref(st.TownRegistry))
          .Append(",\"activeRegistry\":").Append(Ref(st.ActiveRegistry))
          .Append(",\"registryPos\":").Append(bound is { } rp ? "[" + TestJson.G(rp.Building.Position.X) + "," + TestJson.G(rp.Building.Position.Y) + ","
              + TestJson.G(rp.Building.Position.Z) + "]" : "null")
          .Append(",\"registryTown\":").Append(registryTown == TownKey.None ? "null" : N(registryTown))
          .Append(",\"scratchSite\":").Append(towns.Scratch is { } sc ? N(sc.Index) : "null")
          .Append(",\"players\":").Append(N(players.Count))
          .Append(",\"playersInRegistryTown\":").Append(N(inRegistryTown))
          .Append(",\"scheduleEnabled\":").Append(B(logic.IsScheduleEnabled()))
          .Append(",\"booking\":").Append(B(logic.HasBooking()))
          .Append(",\"planned\":").Append(B(st.PlannedNpc.Valid))
          .Append(",\"scheduleChecks\":").Append(N(st.ScheduleChecks))
          .Append(",\"lastScheduleCheck\":").Append(N(st.LastScheduleCheck))
          .Append(",\"now\":").Append(N(clock.NowMs))
          .Append(",\"protocol\":").Append(PGLog.Quote(ArenaMedical.GetProtocol().ToString()))
          .Append(",\"searchRunning\":").Append(B(search.Running))
          .Append(BoutJson(world))
          .Append(CardsJson(world))   // task 7.5
          .Append(",\"hold\":").Append(B(holdForTest))   // task 7.5 review
          .Append(extra)
          .Append('}');
        return sb.ToString();
    }

    /// <summary>ITownHost over one world, rebound for every call (<see cref="Bind"/>).</summary>
    sealed partial class Host(TownArena owner) : ITownHost<CharacterId>
    {
        World? world;

        internal Host Bind(World w)
        {
            world = w;
            return this;
        }

        World W => world ?? throw new InvalidOperationException("TownArena used before a world was bound");

        // ---------- the world ----------
        public bool HasWorld => world is not null;
        public bool IsPaused => false;   // a paused world runs no SlowWorld (World.Interface.cs:16-40)
        public uint NowMs => owner.clock.NowMs;
        public void Debug(string line) => PGLog.Debug(line);

        // ---------- characters ----------
        public bool IsNull(CharacterId c) => c.IsNone;
        public bool Valid(CharacterId c) => !c.IsNone && PgEngine.Exists(W, c);
        // Vector3.Zero for a stale id: the logic reads a position only after Valid(c) (ITownHost contract)
        public Vector3 Position(CharacterId c) => PgEngine.Position(W, c) ?? Vector3.Zero;
        public long CurrentTown(CharacterId c) =>
            Towns.Key(owner.towns.CurrentTownOf(W, c));   // the original's status town (Towns.CurrentTownOf, task 7.2 review)
        // task 9.2b: one list per host, refilled per call (TownArena.Tick asks up to three times a tick while a bout is busy or announced). The
        // ITownHost callers (TownArenaLogic: the stop rules, the registry discovery, the candidate check) read it before their next call; none
        // keeps it, none asks again while it reads it. Not on the worker: the matchmaking snapshot reads PgEngine.PlayerCharacters itself.
        readonly List<CharacterId> players = [];
        public IReadOnlyList<CharacterId>? PlayerCharacters() { PgEngine.FillPlayerCharacters(W, players); return players; }

        // ---------- buildings ----------
        public IReadOnlyList<PlacedBuilding>? BuildingsInSphere(Vector3 origin, float radius, int max)
        {
            var production = PgEngine.Production(W);
            var found = PgEngine.BuildingsNear(W, owner.data, origin, radius, null, max);
            var list = new List<PlacedBuilding>(found.Count);
            foreach (var b in found) list.Add(new PlacedBuilding(ArenaIdentity.From(b, production), Towns.Key(b.Town)));
            return list;
        }
        public PlacedBuilding? Resolve(ArenaBuildingRef held) =>
            ArenaIdentity.Resolve(W, owner.data, held, out var town) is { } b ? new PlacedBuilding(b, Towns.Key(town)) : null;

        // ---------- other subsystems ----------
        public bool ArenaOperationBusy() => owner.lifecycle().IsArenaOperationBusy(W) || owner.holdForTest;   // + TEST ONLY pg town hold (task 7.5)
        public bool SaveInFlight => owner.lifecycle().SaveInFlight;
        public bool IngressPending() => owner.ingress.IsPending(W);
        public void IngressCancel() => owner.ingress.Cancel(W);
        public bool SparIsActive() => owner.spar().IsActive(W);
        public int SparParticipantCount() => owner.spar().GetParticipantCount();
        public CharacterId SparParticipant(int i) => owner.spar().GetParticipant(i) ?? CharacterId.None;
        public void SparAbortForSave() => owner.spar().AbortForSave(W);
        public void TownRulesEventEnded() => owner.rules.BoutEnded(W, ownedMatchEnding: true);   // task 7.4 / 8.4b: Finish, the town event's end
        public ArenaMedicalPolicy.Protocol GetProtocol() => ArenaMedical.GetProtocol();
        public void SetProtocol(ArenaMedicalPolicy.Protocol p) => owner.medical().SetProtocol(W, p);

        // ---------- M8 subsystems ----------
        public void AnnouncerRelease() => owner.announcer.Release(W);
        // C++ TownAnnouncer::TickReleases (task 8.3), with the port's backstop: a cast that outlived its staging (ingress no longer pending) is released
        public void AnnouncerTickReleases() => owner.announcer.TickReleases(W, owner.ingress.IsPending(W));
        public void AnnouncerAbandon() => owner.announcer.AbandonWorldState(W);
        // C++ TownSpectators::Release (task 8.2): at the result, at a match end the town no longer owns, and in Finish. Port only: a throw is logged
        // once and every Spectator hold is let go; after a clean release the backstop lets go of any Spectator hold that is neither a guest nor a
        // deferred release (those keep theirs until healthy, the C++'s pending list)
        public void SpectatorsRelease()
        {
            try { owner.spectators.Release(W); }
            catch (Exception e)
            {
                owner.LogSpectatorFailure(StepSpectatorRelease, e);
                owner.spectators.ResetAfterFailedRelease();
                owner.holds.ReleaseAll(W, HoldOwner.Spectator);
                return;
            }
            owner.spectators.ReleaseOrphanHolds(W);
        }
        // C++ TownSpectators::Tick (task 8.2). Port only: a throw is logged once per world and the rest of TownArena.Tick still runs
        public void SpectatorsTick()
        {
            try { owner.spectators.Tick(W); }
            catch (Exception e) { owner.LogSpectatorFailure(StepSpectatorTick, e); }
        }
        public void SpectatorsAbandon() => owner.spectators.AbandonWorldState();   // C++ TownSpectators::AbandonWorldState (task 8.2)
        // C++ TownCorpseCleanup::Release (task 8.6): at the fight's end and in Finish. Port only: a throw is logged once and every external
        // reservation the cleanup took is given back; after a clean release the backstop gives back any it took that is neither its handler nor a
        // deferred handler (those keep theirs until healthy, the C++'s deferred list)
        public void CorpseCleanupRelease()
        {
            try { owner.corpseCleanup.Release(W); }
            catch (Exception e)
            {
                owner.LogCorpseFailure(StepCorpseRelease, e);
                owner.corpseCleanup.Logic.ResetAfterFailedRelease();
                owner.corpseCleanup.ReleaseOrphanReservations(W, all: true);
                return;
            }
            owner.corpseCleanup.ReleaseOrphanReservations(W, all: false);
        }
        // C++ TownCorpseCleanup::Tick (task 8.6). Port only: a throw is logged once per world and the rest of TownArena.Tick still runs
        public void CorpseCleanupTick()
        {
            try { owner.corpseCleanup.Tick(W); }
            catch (Exception e) { owner.LogCorpseFailure(StepCorpseTick, e); }
        }
        public void CorpseCleanupAbandon() => owner.corpseCleanup.AbandonWorldState();   // C++ TownCorpseCleanup::AbandonWorldState (task 8.6)
        // C++ TownAftercare::Release (task 8.4a). Port only (task 8.4b quality review): a throw in Release is logged once and the backstop below
        // still runs; every medic and patient hold the aftercare took is given back (the registry's ReleaseAll per owner), so a hold can never
        // outlive the event (a patient still in bed keeps lying: the AI holds Sleep in a bed)
        public void AftercareRelease()
        {
            try { owner.aftercare.Release(W); }
            catch (Exception e) { owner.LogAftercareFailure(StepRelease, e); owner.aftercare.ResetAfterFailedRelease(); }
            owner.holds.ReleaseAll(W, HoldOwner.AftercareMedic);
            owner.holds.ReleaseAll(W, HoldOwner.AftercarePatient);
        }
        // C++ TownAftercare::TickBedRecovery (task 8.4c). Port only: a throw is logged once per world and the sweep tries again at the next tick
        // (the C++ cannot throw here); the rest of TownArena.Tick still runs.
        public void AftercareTickBedRecovery()
        {
            try { owner.aftercare.TickBedRecovery(W); }
            catch (Exception e) { owner.LogAftercareFailure(StepBedRecovery, e); }
        }
        public void AftercareAbandon() => owner.aftercare.AbandonWorldState();        // C++ TownAftercare::AbandonWorldState (task 8.4a)
        public bool LimbShopIsActive() => owner.limbShop.IsActive(W);   // C++ TownLimbShop::IsActive (task 8.5)
        public void LimbShopTick() => owner.limbShop.Tick(W);   // C++ TownLimbShop::Tick (task 8.5; it catches its own throws)
        public void LimbShopAbandon() => owner.limbShop.AbandonWorldState(W);   // C++ TownLimbShop::AbandonWorldState (task 8.5)
        public void BookieSettle(int outcome) => owner.bookie.Settle(W, outcome);
        public void BookieRefundUnrestoredWager(ArenaPersistence.PlannedNpcBout saved) => owner.bookie.RefundUnrestoredWager(W, saved);
        public void BookieCancelNpcBout(string reason) => owner.bookie.CancelNpcBout(W, reason);
        public void BookieAbandon() => owner.bookie.AbandonWorldState(W);
        public void DiagnosticCancelPending() => owner.Diagnostic.CancelPending();   // C++ TownDiagnosticRun::CancelPending (task 8.7a)
        public void MatchmakingShutdown() => owner.search.Shutdown();   // C++ TownMatchmakingSearch::Shutdown (task 7.2)
        public void MatchmakingBeginGeneration(uint generation) => owner.search.BeginGeneration(generation);
        public bool MatchmakingEnqueue(Core.TownMatchmakingSearch.Request request) => owner.search.Enqueue(request);
        public bool MatchmakingPoll(out Core.TownMatchmakingSearch.Result? result) => owner.search.Poll(out result);
    }
}
