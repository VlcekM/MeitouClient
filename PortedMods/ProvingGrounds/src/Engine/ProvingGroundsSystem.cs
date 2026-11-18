using System.Globalization;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Town;

namespace ProvingGrounds.Engine;

/// <summary>
/// The mod's one tick system: owns every piece of mod state for one world. Built again for each new game or load, so no state leaks
/// between saves. <see cref="SlowWorld"/> runs the original's per-frame loop (ArenaUI.cpp:3131-3186, PA §2.1) once per tick on
/// <see cref="PgClock"/> time; the frame thread sees only the published <see cref="PgSnapshot"/> and talks back with <see cref="PgCommand"/>s.
/// Subsystems get the objects they share (the adapter, the clock, the session) through this constructor, never through statics.
/// <see cref="IModSaveData"/> (task 2.4) hands the arena data to the save through <see cref="Game.ArenaNativePersistence"/>;
/// <see cref="ICombatHitObserver"/> (task 3.1) hands each landed melee hit to <see cref="Game.CombatHooks"/> (task 3.2b: the damage of the blow
/// its ApplyBlow patch counts, or, without that patch, the hit itself and FightStarter's reaction).
/// </summary>
sealed partial class ProvingGroundsSystem : ITickSystem, IModSaveData, ICombatHitObserver
{
    internal static volatile ProvingGroundsSystem? Current;
    readonly PopulationData data;
    readonly PgClock clock = new();
    readonly UiRefreshCadence publishCadence = new();
    string lastCommand = "none";   // sim thread only; the frame thread reads it through the snapshot
    string lastProbe = "none";     // sim thread only: the last `pg probe <id>` JSON, published
    string lastCensus = "none";    // sim thread only: the last census summary (the F10 census row), published
    readonly Towns towns;          // which town a point is in, and Scratch (task 2.2)
    readonly Game.FighterIdentity identity;   // the career key of each character (task 2.3)
    readonly Game.ArenaNativePersistence arena;   // the world's ledger (standings, progression, ...) and its save data (task 2.4)
    readonly Game.LeaderboardStore store;          // task 5.1: LeaderboardStore over that ledger (ratings, Marks, progression, purchases)
    readonly Game.RewardDelivery rewardDelivery;   // task 5.2b: the Rewards purchases over the store and the reward items (RewardPurchase)
    readonly CombatLogHost combatLog;   // CombatBalanceLog's reads (task 2.7; callers M3, M7)
    volatile PgSnapshot snapshot = PgSnapshot.Empty;
    // task 9.2a, the tick-cost instrument (src/Engine/PgPerf.cs; sim thread only): SlowWorld's and Inputs' last 600 durations while it is on
    readonly TickCostWindow slowWorldCost = new(), inputsCost = new();
    int perfResetSeen;
    PgPerfStats? lastPerf;
    // task 9.2b: SlowWorld's steps, each into its own counter while the instrument is on (Interlocked counters: the frame thread's `pg perf`
    // reads them); slots: StepsBefore, then the frame loop's steps in order, then StepsAfter
    readonly PatchCostCounters stepCost;
    static readonly string[] StepsBefore = ["Towns.Refresh", "FighterIdentity.Refresh", "Spikes.Tick", "FightStarter.ApplyIncomingAttacks", "FightStarter.SweepStaleTargets"];
    static readonly string[] StepsAfter = ["WorldLifecycle.AfterFrame", "CombatHooks.AfterSerialStep", "ArenaInterop.Tick", "PgWatch.Tick", "Publish"];

    // The subsystems, one object each per world. Glue names that are also Core class names (TownArena, ArenaRingGuard, TownBookie)
    // are written with their namespace.
    readonly Game.ContextMenuHooks contextMenuHooks;   // task 4.2: built in the constructor over arenaIngress
    readonly Ui.ArenaUI arenaUi;   // built in the constructor over arenaUiSim (task 4.5a)
    readonly Ui.DebugMenu debugMenu;   // task 4.6b: built in the constructor over its sim half
    readonly Game.ArenaIngress arenaIngress = new(OwnLog);
    readonly Game.PrisonerUtil prisonerUtil;   // task 6.1: built first in the constructor over arenaIngress and fightStarter
    readonly Game.WorldLifecycle worldLifecycle;   // save safety (task 2.5); built in the constructor over the subsystems below
    readonly Game.KOWatcher koWatcher;   // built in the constructor over sparSession (task 3.3)
    readonly Town.TownArena townArena;   // task 7.1: built in the constructor (over the towns, the clock, ingress; spar / medical / lifecycle lazily)
    readonly Game.ArenaRingGuard arenaRingGuard;   // built in the constructor over sparSession (task 3.4)
    readonly PgWatch watch = new();                 // TEST ONLY pg watch (task 3.5): PG-WATCH lines on a fighter's state change
    readonly Ui.TownArenaUI townArenaUi;   // task 7.5: built in the constructor over its sim half (src/Town/TownArenaUISim.cs)
    readonly Ui.MarksHUD marksHud;     // task 4.6b: built in the constructor over its sim half
    readonly Town.TownBookie townBookie = new();   // task 8.1a: its services are attached at the end of the constructor
    readonly Town.TownBookieUISim townBookieUi;    // task 8.1a: the bookie windows' models (pg.bookie, pg.bookie.result) and commands
    readonly Town.TownAnnouncer townAnnouncer = new();   // task 8.3 (TownArena attaches it): ArenaIngress ticks it, TownArena ticks its releases; not in the frame loop
    readonly Ui.ResultsUI resultsUi;             // task 4.6a: built in the constructor over its sim half (SparStats, the clock)
    readonly Ui.LeaderboardUI leaderboardUi;     // task 4.6a: built in the constructor over its sim half (the ledger)
    readonly Ui.RewardsUI rewardsUi;   // task 5.3a: built in the constructor over its sim half (the store, RewardDelivery)
    readonly Game.PrisonerRecruitment prisonerRecruitment;   // task 6.4a: freedom contracts (the price, the payment, the recruit)
    readonly Ui.PrisonerRecruitmentUI prisonerRecruitmentUi;   // task 6.4a: built in the constructor over its sim half
    // Not in the frame loop: only the save path calls them (task 2.5, WorldLifecycle.cpp); their tasks give them their steps.
    readonly Game.FightStarter fightStarter = new(OwnLog);   // task 3.1; its hit reactions run in their own slot of SlowWorld (fightSlot)
    readonly Game.SparSession sparSession;             // task 3.3; built in the constructor over fightStarter, sparStats and the subsystems it calls
    readonly Game.SparStats sparStats = new();   // task 3.2a; holds no roster: callers pass the match (SparSession, task 3.3) to each call
    readonly Game.CombatHooks combatHooks;       // task 3.2b; built in the constructor over sparSession; its view is published after each serial step
    readonly Game.ArenaMedical arenaMedical;     // task 4.7; built in the constructor over sparSession, arenaIngress and combatHooks (its view); not in the frame loop
    readonly Town.TownLimbShop townLimbShop = new();
    // ---------- task 4.4: the window models (src/Ui/ScreenHost.cs reads them from the snapshot; 4.5 / 4.6 add the real windows' sim halves) ----------
    readonly Game.TestScreenSim testScreen = new();   // TEST ONLY: the pg.test window's sim half (pg window test ...)
    readonly Game.ArenaUISim arenaUiSim;               // task 4.5a: the Arena window's sim half (pg.arena); built in the constructor
    IReadOnlyDictionary<string, PgUiModel>? uiModels;   // sim thread: the published map, rebuilt only when a model object changed
    PgUiModel? uiModelsOf, uiArenaOf;
    PgUiModel? uiResultsOf, uiLeaderboardOf;   // task 4.6a
    PgUiModel? uiDebugOf, uiMarksOf;   // task 4.6b
    PgUiModel? uiRewardsOf;   // task 5.3a
    PgUiModel? uiRecruitmentOf;   // task 6.4a
    PgUiModel? uiTownOf;          // task 7.5
    PgUiModel? uiBookieOf, uiBookieResultOf;   // task 8.1a

    /// <summary>The frame loop in the original's order, bound once here so a tick allocates nothing.</summary>
    readonly (string Name, Action<World> Run)[] frameLoop;
    readonly StepFailureLog stepFailures;
    readonly int spikeSlot, townsSlot, identitySlot, arenaSlot, saveSlot, snapshotSlot, settingsSlot, fightSlot, hooksSlot;   // failure slots after the frame loop's own
    readonly int interopSlot;   // task 10.1 (port only): ArenaInterop.Tick, the last failure slot
    // port only (tasks 10.1 / 10.2): what other mods read (src/Api/ProvingGroundsApi.cs) and the link to The Wasteland Remembers
    readonly Game.ArenaInterop interop;
    readonly int townArenaSlot;   // task 7.1: the frame-loop slot of TownArena.Tick (its F10 phase reports there)
    /// <summary>The frame-loop step name of TownArena.Tick, shared by the loop and the slot lookup (PgCheck engine checks the constructor uses it
    /// twice: a renamed or dropped step fails offline).</summary>
    internal const string TownArenaTickStep = "TownArena.Tick";
    volatile World? boundWorld;   // the world of this system's serial steps (set at the start of Inputs and SlowWorld; HooksFor reads it from any thread)

    /// <summary>The port's own diagnostic lines (no C++ counterpart): Meitou's log (task 2.7, divergences.md).</summary>
    static void OwnLog(string m) => ProvingGroundsMod.Ctx?.Log(m);

    /// <summary>TEST ONLY (task 4.3b): each countdown bark as a PG-INGRESS "say" line (sim tick) while the test verbs are armed.</summary>
    static void IngressSaid(World world, CharacterId c, string text)
    {
        if (!Spikes.SpikePatches.Armed) return;
        PgPerf.TestLine("PG-INGRESS {\"event\":\"say\",\"tick\":" + world.Tick.ToString(CultureInfo.InvariantCulture)
            + ",\"id\":" + PGLog.Quote(CharacterIdText.Format(c)) + ",\"text\":" + PGLog.Quote(text) + "}");
    }

    /// <summary>TEST ONLY (task 6.2): the prisoner's release line as a PG-PRISONER "say" line (sim tick) while the test verbs are armed.</summary>
    static void PrisonerSaid(World world, CharacterId c, string text)
    {
        if (!Spikes.SpikePatches.Armed) return;
        PgPerf.TestLine("PG-PRISONER {\"event\":\"say\",\"tick\":" + world.Tick.ToString(CultureInfo.InvariantCulture)
            + ",\"id\":" + PGLog.Quote(CharacterIdText.Format(c)) + ",\"text\":" + PGLog.Quote(text) + "}");
    }

    internal ProvingGroundsSystem(PopulationData data)
    {
        // task 9.2b: the JIT warm-up starts with the first world's systems (the AddSystem factory runs after every code mod's Initialize, so
        // every Harmony patch is in place first: Meitou's rule, GameMods.cs:20-58); once per process
        if (ProvingGroundsMod.Ctx is { } warmupCtx) PgJitWarmup.Start(warmupCtx.Log);
        this.data = data;
        prisonerUtil = new Game.PrisonerUtil(arenaIngress, fightStarter, data, clock, () => sparSession!, PrisonerSaid);   // task 6.1 (C++ PrepareMatch: ArenaIngress::GetBoundRegistry; ClearMatchState: FightStarter::DisengageMatch); task 6.2 (GetTickCount, SparSession::IsActive / IsSparringOpponent: read lazily, built below)
        towns = new Towns(data);
        townArena = new Town.TownArena(data, towns, clock, arenaIngress, () => sparSession!, () => arenaMedical!, () => worldLifecycle!,
            townBookie, townAnnouncer, townLimbShop, () => identity!, () => store!, () => combatLog!);   // task 7.1 (SparSession, ArenaMedical, WorldLifecycle, task 7.2's identity and store, task 7.5's combat log are built below: read lazily)
        townArenaUi = new Ui.TownArenaUI(new Town.TownArenaUISim(townArena));   // task 7.5
        identity = new Game.FighterIdentity(PGLog.Debug);   // C++ FighterIdentity.cpp:45-49 Log -> PGLog::Debug
        arena = new Game.ArenaNativePersistence(identity, PGLog.Debug, OwnLog, ProvingGroundsMod.Ctx?.Folder);   // C++ ArenaNativePersistence.cpp:57-60 Log -> PGLog::Debug; PG: lines -> Meitou
        arena.WireTownBout(p => townArena.CapturePlannedNpcBout(boundWorld, p), townArena.RestorePlannedNpcBout);   // task 7.3 (ArenaNativePersistence.cpp:205-206, :360-361)
        combatLog = new CombatLogHost(arena, () => townArena.Diagnostic);   // task 8.7a: the diagnostic run's state (TownDiagnosticRun::Get)
        arena.WireDiagnosticRun(sink => townArena.Diagnostic.RecoverPendingAfterLoad(sink), CombatBalanceLog.DiagnosticSink(combatLog));   // task 8.7a (ArenaNativePersistence.cpp:363, :370)
        store = new Game.LeaderboardStore(arena, identity, prisonerUtil, () => sparSession);   // task 5.1 (SparSession read lazily: built below)
        rewardDelivery = new Game.RewardDelivery(store, identity, data);   // task 5.2b
        rewardsUi = new Ui.RewardsUI(new Game.RewardsUISim(store, rewardDelivery, data));   // task 5.3a
        prisonerRecruitment = new Game.PrisonerRecruitment(store, identity, prisonerUtil, arenaIngress, () => sparSession!, data);   // task 6.4a
        prisonerRecruitmentUi = new Ui.PrisonerRecruitmentUI(new Game.PrisonerRecruitmentUISim(new Game.PrisonerRecruitmentUISim.Services(
            prisonerRecruitment, prisonerUtil, arenaIngress, () => sparSession!, store, () => arenaUiSim!)));   // task 6.4a (C++ Close: ArenaUI::ShowFromRegistry, lazy)
        // ---- task 4.6a: the Results and Leaderboard windows' sim halves ----
        resultsUi = new Ui.ResultsUI(new Game.ResultsUISim(sparStats, clock));
        leaderboardUi = new Ui.LeaderboardUI(new Game.LeaderboardUISim(arena, store));
        // ---- end task 4.6a ----
        // ---- task 4.6b: the Marks HUD's and the debug menu's sim halves ----
        marksHud = new Ui.MarksHUD(new Game.MarksHUDSim(new Game.MarksHUDSim.Services(store.HasActiveSave, towns, store.GetMarks, clock)));
        debugMenu = new Ui.DebugMenu(new Game.DebugMenuSim(new Game.DebugMenuSim.Services(arenaIngress, prisonerUtil, townArena, clock,
            store.GetPlayerRating, store.GetMarks, store.SetRating, store.SetMarks)));
        // ---- end task 4.6b ----
        sparSession = new Game.SparSession(new Game.SparSession.Services(
            fightStarter, sparStats, arenaIngress, townArena, resultsUi, prisonerUtil, clock, combatLog, CombatFighter,
            store, IdentityOf, () => worldLifecycle?.SaveInFlight == true, OwnLog,
            (w, snap) => interop?.OnMatchResult(w, snap)));   // task 10.2: built below (a result can only come after the constructor)
        interop = new Game.ArenaInterop(sparSession, townArena, prisonerUtil, towns, data, OwnLog);   // tasks 10.1 / 10.2 (port only)
        koWatcher = new Game.KOWatcher(sparSession, clock);
        arenaRingGuard = new Game.ArenaRingGuard(sparSession, clock);
        combatHooks = new Game.CombatHooks(new Game.CombatHooks.Services(sparSession, sparStats, fightStarter, prisonerUtil, townArena));
        arenaMedical = new Game.ArenaMedical(sparSession, arenaIngress, combatHooks, data, () => townArena);   // task 8.4c: data (the restock) and the town arena (RecordTreatment)
        arenaIngress.Wire(new Game.ArenaIngress.Services(fightStarter, () => sparSession, arenaRingGuard, resultsUi, prisonerUtil, townArena,
            townAnnouncer, townBookie, clock, data, () => worldLifecycle?.SaveInFlight == true, IngressSaid, OwnLog,
            () => arenaUiSim!, () => leaderboardUi.Sim));   // task 4.3b; the approach's windows (task 4.2)
        contextMenuHooks = new Game.ContextMenuHooks(arenaIngress, data, rewardsUi, townBookie);   // task 4.2; task 5.3a: Null's trade opens the Rewards window; task 8.1a: the bookie's, the betting window
        arenaUiSim = new Game.ArenaUISim(new Game.ArenaUISim.Services(arenaIngress, () => sparSession, prisonerUtil, townArena, townBookie,
            townArenaUi, prisonerRecruitmentUi, marksHud, rewardsUi, arenaMedical, clock, data, store.GetPlayerRating));   // task 4.5a
        arenaUi = new Ui.ArenaUI(arenaUiSim);
        arenaUi.PollDebugMenuKeyStep = debugMenu.PollKey;   // task 4.6b: PollF8Toggle -> the debug menu's key
        worldLifecycle = new Game.WorldLifecycle(
            new Game.WorldLifecycle.Subsystems(arenaIngress, sparSession, prisonerUtil, townArena, townLimbShop, resultsUi, sparStats, fightStarter,
                arenaUi, debugMenu, PGLog.Debug, OwnLog),   // C++ texts (WorldLifecycle.cpp:42-47 LogLifecycle -> PGLog::Debug); PG save safety: lines -> Meitou
            ArenaTick, OwnLog);
        townBookie.Attach(new Town.TownBookie.Services(townArena, towns, clock, () => store!, () => combatLog!, CombatFighter, townArenaUi.Sim));   // task 8.1a
        townBookieUi = new Town.TownBookieUISim(townBookie);   // task 8.1a
        // C++: ArenaUI.cpp:3151-3171 (ForgottenGUI_update_hook after the original update; Esc, wheel and scroll handling before it is
        // window-only and lives with the windows).
        frameLoop =
        [
            ("ContextMenuHooks.Tick", contextMenuHooks.Tick),                       // :3151
            ("ContextMenuHooks.RefreshArenaCursor", contextMenuHooks.RefreshArenaCursor), // :3152
            ("ArenaUI.PollDebugMenuKey", arenaUi.PollDebugMenuKey),                 // :3153 PollF8Toggle: the "Debug menu key" setting (overview §1)
            ("DebugMenu.Tick", debugMenu.Tick),                                     // :3154
            ("ArenaIngress.Tick", arenaIngress.Tick),                               // :3155
            ("PrisonerUtil.TickMatchUpkeep", prisonerUtil.TickMatchUpkeep),         // :3156
            ("PrisonerUtil.TickReturn", prisonerUtil.TickReturn),                   // :3157
            ("WorldLifecycle.Tick", worldLifecycle.Tick),                           // :3158
            ("KOWatcher.Tick", koWatcher.Tick),                                     // :3159
            (TownArenaTickStep, townArena.Tick),                                     // :3160
            ("ArenaRingGuard.Tick", arenaRingGuard.Tick),                           // :3163 (once, for town and player bouts)
            ("TownArenaUI.Tick", townArenaUi.Tick),                                 // :3164 sim half
            ("MarksHUD.Tick", marksHud.Tick),                                       // :3165 sim half (publishes)
            ("TownBookie.Tick", townBookie.Tick),                                   // :3166
            ("PrisonerUtil.TickRingsideAid", prisonerUtil.TickRingsideAid),         // :3167
            ("ResultsUI.Tick", resultsUi.Tick),                                     // :3168 sim half
            ("LeaderboardUI.Tick", leaderboardUi.Tick),                             // :3169 sim half
            ("RewardsUI.Tick", rewardsUi.Tick),                                     // :3170 sim half
            ("PrisonerRecruitmentUI.Tick", prisonerRecruitmentUi.Tick),             // :3171 sim half
            ("ArenaUI.Refresh", arenaUiSim.Tick),                                   // :3172-3185 the Arena window's idle refresh (task 4.5a)
        ];
        spikeSlot = frameLoop.Length;
        townsSlot = frameLoop.Length + 1;
        identitySlot = frameLoop.Length + 2;
        arenaSlot = frameLoop.Length + 3;
        saveSlot = frameLoop.Length + 4;
        // task 7.1: found once by the shared name (never throw here: Meitou's factory builds this system unguarded, a throw would refuse every
        // load); PgCheck engine checks offline that the frame loop names it. Missing: its failures go to the save-safety slot, one log line.
        townArenaSlot = Array.FindIndex(frameLoop, f => f.Name == TownArenaTickStep);
        if (townArenaSlot < 0) { townArenaSlot = saveSlot; OwnLog($"frame loop lacks {TownArenaTickStep}: its failures are counted on the save safety's slot"); }
        snapshotSlot = frameLoop.Length + 5;
        settingsSlot = frameLoop.Length + 6;
        fightSlot = frameLoop.Length + 7;
        hooksSlot = frameLoop.Length + 8;
        interopSlot = frameLoop.Length + 9;
        stepFailures = new StepFailureLog(interopSlot + 1);
        stepCost = new PatchCostCounters([.. StepsBefore, .. frameLoop.Select(f => f.Name), .. StepsAfter]);   // task 9.2b
        // Current and ProvingGroundsMod.Data are taken on the first serial step (TakeOver), not here: Meitou builds a loaded world's
        // systems ahead while the old world still shows (GameSaves.PrepareSystems, the "systems" stage, GameSaves.cs:324-327, 419-436),
        // and a swap that fails keeps the old world running (SwapInSave, WorldSession.ReplaceWorld, GameSaves.cs:444-466,
        // WorldSession.cs:142-156). Read (IModSaveData) reaches this object directly, never through Current.
    }

    /// <summary>Serial: the commands the frame thread queued for this tick, in order; then a fresh snapshot.</summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        long perfStart = PgPerf.Begin();   // task 9.2a: 0 while the instrument is off
        if (perfStart != 0) PerfResetIfAsked();
        boundWorld = world;
        TakeOver();
        combatLog.Bind(world);
        try { Ui.PGSettingsUI.ApplyLive(); }   // the settings page's at-once values (crowd preset, debug menu key; task 2.6)
        catch (Exception e) { Failed(settingsSlot, e); }
        try { arena.Resolve(world); }   // the loaded (or new-game) state before any command reads the ledger
        catch (Exception e) { Failed(arenaSlot, e); }
        bool any = false;
        for (int i = 0; i < commands.Count; i++)
        {
            if (commands[i] is not PgCommand command) continue;
            any = true;
            Apply(world, command);
        }
        try { combatHooks.AfterSerialStep(world); }   // the combat hooks' match view for this tick's think and commit (task 3.2b)
        catch (Exception e) { Failed(hooksSlot, e); }
        if (any) Publish();
        if (perfStart != 0) inputsCost.Add(PgPerf.Since(perfStart));
    }

    void Apply(World world, PgCommand command)
    {
        switch (command)
        {
            case ConsoleLine pg:
                try
                {
                    string result = pg.Verb switch
                    {
                        "census" => lastCensus = Census.Run(world, data, m => ProvingGroundsMod.Ctx?.Log(m)),   // test output (task 0.6): the mod log, as the scenarios read it
                        "probe" => Probe(world, pg),
                        "identity-dupes" => IdentityLine(Game.FighterIdentity.DupesJson(world)),   // TEST ONLY (task 2.3)
                        "identity" => IdentityCreate(world, pg),                                    // TEST ONLY (task 2.3)
                        "give-rating" => GiveRating(world, pg),                                     // TEST ONLY (task 2.4)
                        "give-marks" => GiveMarks(world, pg),                                       // TEST ONLY (task 2.4)
                        "spar" => Spar(world, pg),                                                  // TEST ONLY (task 3.3; 3.5 keeps it)
                        "stop" => sparSession.StopForTest(world),                                   // TEST ONLY (task 3.3)
                        "town" => pg.Args.Count == 1 && pg.Args[0] == "model" ? townArenaUi.Sim.ModelForTest()   // TEST ONLY (task 7.6: the window model)
                            : pg.Args.Count >= 1 && pg.Args[0] == "bookie" ? townBookieUi.Command(world, pg.Args.Skip(1).ToList(), NearestBookie)   // TEST ONLY (task 8.1a)
                            : townArena.Command(world, pg.Args),                                    // TEST ONLY (task 7.1)
                        "ring" => arenaRingGuard.Command(world, pg.Args),                           // TEST ONLY (task 3.4; 4.3b keeps it: push / status)
                        "ingress" => arenaIngress.Command(world, data, pg.Args),                    // TEST ONLY (task 4.3b, until the Arena window 4.5)
                        "watch" => watch.Command(world, pg.Args),                                   // TEST ONLY (task 3.5)
                        "medical" => arenaMedical.Command(world, pg.Args),                          // TEST ONLY (task 4.7, until the Arena window 4.5)
                        _ when Game.ArenaIdentityTestVerbs.Verbs.Contains(pg.Verb) => Game.ArenaIdentityTestVerbs.Run(world, data, pg),   // TEST ONLY (task 4.1)
                        "window" => testScreen.Command(world, pg.Args),                             // TEST ONLY (task 4.4)
                        "arena" => arenaUiSim.Command(world, data, pg.Args),                        // TEST ONLY (task 4.5a; kept after 4.5b / 4.2: the scenarios drive the window with it)
                        "leaderboard" => leaderboardUi.Sim.Command(world, data, pg.Args),          // TEST ONLY (task 4.6a; kept after task 4.2: results-board uses it)
                        "debug" => debugMenu.Sim.Command(world, pg.Args),                           // TEST ONLY (task 4.6b)
                        "items" => ItemsList(world, pg),                                            // TEST ONLY (task 5.2a)
                        "reward-item" => RewardItemTest(world, pg),                                 // TEST ONLY (task 5.2a)
                        "reward" => rewardDelivery.Command(world, pg.Args),                         // TEST ONLY (task 5.2b: offer / drop; the purchases are RewardPurchase)
                        "rewards" => rewardsUi.Sim.Command(world, pg.Args),                          // TEST ONLY (task 5.3a, until the window half 5.3b)
                        "recruit" => prisonerRecruitmentUi.Sim.Command(world, pg.Args),              // TEST ONLY (task 6.4a; kept for the scenarios since the window half 6.4b)
                        _ when Game.PrisonerTestVerbs.Verbs.Contains(pg.Verb) => Game.PrisonerTestVerbs.Run(world, prisonerUtil, arenaIngress, pg),   // TEST ONLY (task 6.1)
                        _ when Spikes.Spikes.Verbs.Contains(pg.Verb) => Spikes.Spikes.Run(world, pg),   // SPIKE (task 0.7)
                        _ => $"unknown {pg.Verb}",
                    };
                    lastCommand = string.Create(CultureInfo.InvariantCulture, $"pg {pg.Verb} at tick {world.Tick}: {result}");
                }
                catch (Exception e) { lastCommand = $"pg {pg.Verb} failed: {e.GetType().Name}: {e.Message}"; }
                ProvingGroundsMod.Ctx?.Log(lastCommand);
                return;
            case ArenaUiCommand arenaCommand:   // task 4.5a: an Arena window command, to its sim half
                try { lastCommand = $"window click {Game.ArenaUISim.ScreenId}: {arenaUiSim.Apply(world, arenaCommand).Done}"; }
                catch (Exception e) { lastCommand = $"window click {Game.ArenaUISim.ScreenId} failed: {e.GetType().Name}: {e.Message}"; }
                return;
            case ArenaInteract interact:   // task 4.2: a right press on a Registry / Banner / Leaderboard (ContextMenuHooks)
                try { lastCommand = $"interact {Game.ArenaIdentity.KeyText(interact.Site)}: {contextMenuHooks.ApplyInteract(world, interact)}"; }
                catch (Exception e) { lastCommand = $"interact failed: {e.GetType().Name}: {e.Message}"; }
                ProvingGroundsMod.Ctx?.Log("PG-INTERACT " + lastCommand);   // one line per right press on our buildings (rare; the live check reads it)
                return;
            case ArenaInteractIgnored pressIgnored:   // task 4.2: the held menu of an unfinished one of ours opened (identity + "ignored")
                try { lastCommand = $"interact {Game.ArenaIdentity.KeyText(pressIgnored.Site)}: {contextMenuHooks.ApplyInteractIgnored(world, pressIgnored)}"; }
                catch (Exception e) { lastCommand = $"interact failed: {e.GetType().Name}: {e.Message}"; }
                ProvingGroundsMod.Ctx?.Log("PG-INTERACT " + lastCommand);
                return;
            case ArenaUseIgnored ignored:   // task 4.2: the quick order on an unfinished one of ours was dropped
                try { lastCommand = $"interact {Game.ArenaIdentity.KeyText(ignored.Site)}: {contextMenuHooks.ApplyUseIgnored()}"; }
                catch (Exception e) { lastCommand = $"interact failed: {e.GetType().Name}: {e.Message}"; }
                ProvingGroundsMod.Ctx?.Log("PG-INTERACT " + lastCommand);
                return;
            case ArenaExternalMenu:   // task 4.2: another right-click menu opened
                try { lastCommand = contextMenuHooks.ApplyExternalMenu(world); }
                catch (Exception e) { lastCommand = $"menu watch failed: {e.GetType().Name}: {e.Message}"; }
                return;
            case RewardPurchase purchase:   // task 5.2b: a Rewards purchase (the Rewards window, task 5.3; the test-only pg reward)
                try { lastCommand = $"reward purchase {purchase.Kind} for {CharacterIdText.Format(purchase.Fighter)}: {rewardDelivery.Apply(world, purchase).Status}"; }   // publishes LastPurchases (Publish after Inputs)
                catch (Exception e) { lastCommand = $"reward purchase failed: {e.GetType().Name}: {e.Message}"; }
                ProvingGroundsMod.Ctx?.Log(lastCommand);
                try { rewardsUi.Sim.AfterPurchase(world); }   // task 5.3a: the C++ RefreshDetails after each purchase (RewardsUI.cpp:502, :537, :550, :559)
                catch (Exception e) { ProvingGroundsMod.Ctx?.Log($"rewards window refresh failed: {e.GetType().Name}: {e.Message}"); }
                return;
            case RewardsUiCommand rewardsCommand:   // task 5.3a: a Rewards window button (fighter, tab, close), to its sim half
                try { lastCommand = $"window click {Game.RewardsUISim.ScreenId}: {rewardsUi.Sim.Apply(world, rewardsCommand).Done}"; }
                catch (Exception e) { lastCommand = $"window click {Game.RewardsUISim.ScreenId} failed: {e.GetType().Name}: {e.Message}"; }
                return;
            case TownUiCommand townCommand:   // task 7.5: a town registry window command, to its sim half
                try { lastCommand = $"window click {Town.TownArenaUISim.ScreenId}: {townArenaUi.Sim.Apply(world, townCommand).Done}"; }
                catch (Exception e) { lastCommand = $"window click {Town.TownArenaUISim.ScreenId} failed: {e.GetType().Name}: {e.Message}"; }
                if (townCommand is not TownSelectFighter and not TownSetDivision) ProvingGroundsMod.Ctx?.Log(lastCommand);   // one line per booking / refresh click
                return;
            case BookieUiCommand bookieCommand:   // task 8.1a: a bookie window command, to its sim half
                try { lastCommand = $"window click {Town.TownBookieUISim.ScreenId}: {townBookieUi.Apply(world, bookieCommand).Done}"; }
                catch (Exception e) { lastCommand = $"window click {Town.TownBookieUISim.ScreenId} failed: {e.GetType().Name}: {e.Message}"; }
                if (bookieCommand is BookiePlaceBet or BookieArrange) ProvingGroundsMod.Ctx?.Log(lastCommand);   // one line per bet / arrange click
                return;
            case RecruitmentUiCommand recruitmentCommand:   // task 6.4a: a recruitment window button (row, close, Grant Freedom), to its sim half
                try { lastCommand = $"window click {Game.PrisonerRecruitmentUISim.ScreenId}: {prisonerRecruitmentUi.Sim.Apply(world, recruitmentCommand).Done}"; }
                catch (Exception e) { lastCommand = $"window click {Game.PrisonerRecruitmentUISim.ScreenId} failed: {e.GetType().Name}: {e.Message}"; }
                if (recruitmentCommand is RecruitPrisoner) ProvingGroundsMod.Ctx?.Log(lastCommand);   // one line per recruit click (rare)
                return;
            case DebugMenuKeyPressed:   // task 4.6b: the debug menu key; the frame loop's PollDebugMenuKey slot toggles
                debugMenu.Sim.KeyPressed();
                lastCommand = "debug menu key pressed";
                return;
            case DebugMenuPress debugPress:   // task 8.7b: a debug menu button with what the window drew, to its sim half
                try { lastCommand = $"window click {Game.DebugMenuSim.ScreenId}/{debugPress.Widget}: {debugMenu.Sim.ApplyPress(world, debugPress).Done}"; }
                catch (Exception e) { lastCommand = $"window click {Game.DebugMenuSim.ScreenId} failed: {e.GetType().Name}: {e.Message}"; }
                return;
            case UiClick click:   // task 4.4: a window's widget event, to the screen's sim half
                try { lastCommand = $"window click {click.Click.Screen}/{click.Click.Widget}: {ApplyUiClick(world, click.Click)}"; }
                catch (Exception e) { lastCommand = $"window click {click.Click.Screen} failed: {e.GetType().Name}: {e.Message}"; }
                return;
            default:
                ProvingGroundsMod.Ctx?.Log($"unhandled command {command.GetType().Name}");
                return;
        }
    }

    /// <summary>A window click (task 4.4) to the sim half of its screen (sim thread, serial Inputs). 4.5 / 4.6 add their screens.</summary>
    string ApplyUiClick(World world, Core.Glue.UiClickData click) => click.Screen switch
    {
        Game.TestScreenSim.ScreenId => testScreen.Click(click),   // TEST ONLY
        Game.ArenaUISim.ScreenId => arenaUiSim.Apply(world, click).Done,   // task 4.5a (Closed reports)
        Game.ResultsUISim.ScreenId => resultsUi.Sim.Apply(click).Done,          // task 4.6a
        Game.LeaderboardUISim.ScreenId => leaderboardUi.Sim.Apply(world, click).Done,   // task 4.6a
        Game.DebugMenuSim.ScreenId => debugMenu.Sim.Apply(world, click).Done,           // task 4.6b
        Game.RewardsUISim.ScreenId => rewardsUi.Sim.Apply(world, click).Done,           // task 5.3a
        Game.PrisonerRecruitmentUISim.ScreenId => prisonerRecruitmentUi.Sim.Apply(world, click).Done,   // task 6.4a (Closed reports)
        Town.TownArenaUISim.ScreenId => townArenaUi.Sim.Apply(world, click).Done,                        // task 7.5 (Closed reports)
        Town.TownBookieUISim.ScreenId or Town.TownBookieUISim.ResultScreenId => townBookieUi.Apply(world, click).Done,   // task 8.1a (Closed reports)
        _ => $"no sim half for screen {click.Screen}",
    };

    /// <summary>The window models for the snapshot (task 4.4): the same map object while no model changed (the window side compares by reference).</summary>
    IReadOnlyDictionary<string, PgUiModel> UiModels()
    {
        var test = testScreen.Model;
        var arenaModel = arenaUiSim.Model;
        var resultsModel = resultsUi.Sim.Model;            // task 4.6a
        var leaderboardModel = leaderboardUi.Sim.Model;    // task 4.6a
        var debugModel = debugMenu.Sim.Model;              // task 4.6b
        var marksModel = marksHud.Sim.Model;               // task 4.6b
        var rewardsModel = rewardsUi.Sim.Model;            // task 5.3a
        var recruitmentModel = prisonerRecruitmentUi.Sim.Model;   // task 6.4a
        var townModel = townArenaUi.Sim.Model;                    // task 7.5
        var bookieModel = townBookieUi.Model;                     // task 8.1a
        var bookieResultModel = townBookieUi.ResultModel;         // task 8.1a
        if (uiModels is null || !ReferenceEquals(uiModelsOf, test) || !ReferenceEquals(uiArenaOf, arenaModel)
            || !ReferenceEquals(uiResultsOf, resultsModel) || !ReferenceEquals(uiLeaderboardOf, leaderboardModel)
            || !ReferenceEquals(uiDebugOf, debugModel) || !ReferenceEquals(uiMarksOf, marksModel) || !ReferenceEquals(uiRewardsOf, rewardsModel)
            || !ReferenceEquals(uiRecruitmentOf, recruitmentModel) || !ReferenceEquals(uiTownOf, townModel)
            || !ReferenceEquals(uiBookieOf, bookieModel) || !ReferenceEquals(uiBookieResultOf, bookieResultModel))
        {
            uiModels = new Dictionary<string, PgUiModel>(StringComparer.Ordinal)
            {
                [Game.TestScreenSim.ScreenId] = test,
                [Game.ArenaUISim.ScreenId] = arenaModel,
                [Game.ResultsUISim.ScreenId] = resultsModel,
                [Game.LeaderboardUISim.ScreenId] = leaderboardModel,
                [Game.DebugMenuSim.ScreenId] = debugModel,      // task 4.6b
                [Game.MarksHUDSim.ModelId] = marksModel,        // task 4.6b (not a screen: the overlay label reads it)
                [Game.RewardsUISim.ScreenId] = rewardsModel,    // task 5.3a
                [Game.PrisonerRecruitmentUISim.ScreenId] = recruitmentModel,   // task 6.4a
                [Town.TownArenaUISim.ScreenId] = townModel,                     // task 7.5
                [Town.TownBookieUISim.ScreenId] = bookieModel,                  // task 8.1a
                [Town.TownBookieUISim.ResultScreenId] = bookieResultModel,      // task 8.1a
            };
            uiModelsOf = test;
            uiArenaOf = arenaModel;
            uiResultsOf = resultsModel;
            uiLeaderboardOf = leaderboardModel;
            uiDebugOf = debugModel;
            uiMarksOf = marksModel;
            uiRewardsOf = rewardsModel;
            uiRecruitmentOf = recruitmentModel;
            uiTownOf = townModel;
            uiBookieOf = bookieModel;
            uiBookieResultOf = bookieResultModel;
        }
        return uiModels;
    }

    /// <summary>TEST ONLY (pg town bookie open): the Scratch Bookie (record 111) nearest to the first living player character (none: no bookie).</summary>
    static CharacterId NearestBookie(World world)
    {
        var players = PgEngine.PlayerCharacters(world);
        System.Numerics.Vector3? from = null;
        foreach (var p in players) if (!PgEngine.IsDead(world, p) && PgEngine.Position(world, p) is { } pos) { from = pos; break; }
        var table = world.Characters;
        var next = table.Next;
        CharacterId best = CharacterId.None;
        float bestD = float.MaxValue;
        for (int slot = 0; slot < next.Length; slot++)
        {
            if (!next[slot].Alive || table.Cold(slot) is not { } c || c.RecordId != PgRecords.Bookie) continue;
            float d = from is { } f ? System.Numerics.Vector3.DistanceSquared(f, next[slot].Position) : 0;
            if (d < bestD) { bestD = d; best = new CharacterId(slot, next[slot].Generation); }
        }
        return best;
    }

    /// <summary>The reads <see cref="CombatBalanceLog"/> makes on every event line (task 2.7; callers M3, M7). Sim thread.</summary>
    internal CombatBalanceLog.IHost CombatLog => combatLog;

    /// <summary>
    /// One fighter of a CombatBalanceLog line, as <c>Fighter()</c> reads it (CombatBalanceLog.cpp:136-157; the M3 and M7 callers fill their arrays
    /// with this): <see cref="CombatBalanceLog.FighterInfo.Unavailable"/> for no character or one that is not valid (a stale id, as the C++'s
    /// <c>isValid()</c> false), else its identity (<c>FighterIdentity::Find</c>), record string id, name and, for a valid character only
    /// (<c>valid ? GetRating(c, kind) : 0</c>, :153), <c>LeaderboardStore::GetRating(c, kind)</c> (src/Game/LeaderboardStore.cs, which logs a
    /// missing identity once per character), and (task 7.2) <c>ArenaCombatProfile::Read</c> (src/Game/ArenaCombatProfile.cs) and
    /// <c>RosterStatus::Read</c> (PgEngine.RosterHealth). StartAbility's own GetRating of any non-null character (:96) stays with its M3 / M7
    /// caller. Sim thread, serial step.
    /// </summary>
    internal CombatBalanceLog.FighterInfo CombatFighter(World world, CharacterId id, LeaderboardData.Kind kind)
    {
        bool present = !id.IsNone;
        bool valid = present && PgEngine.Exists(world, id);
        string token = "";
        bool found = valid && identity.Find(world, id, out token);
        // not read (and not written: the line says mmr 0) for a character that is not valid
        float rating = valid ? store.GetRating(world, id, kind) : MmrRating.DefaultMmr;
        RosterStatus.Snapshot health = default;
        bool hasHealth = valid && PgEngine.RosterHealth(world, id, out health, out _);
        return CombatLogFighter.From(new CombatLogFighter.Reads
        {
            Present = present, Valid = valid, Identity = found ? token : "", Role = valid ? PgEngine.RecordId(world, id) : "",
            Name = valid ? PgEngine.Name(world, id) : "", Rating = rating,
            Profile = valid ? Game.ArenaCombatProfile.Read(world, id) : new Core.ArenaCombatProfile.Profile(),
            HasHealth = hasHealth, Health = health,
        });
    }

    /// <summary>C++ TownMatchmakingRuntime.cpp:21-25 <c>Identity(c)</c>: FighterIdentity::Find, "" for none (CombatBalanceLog's result rows).</summary>
    string IdentityOf(World world, CharacterId id) => !id.IsNone && PgEngine.Exists(world, id) && identity.Find(world, id, out string token) ? token : "";

    /// <summary>IModSaveData (task 2.4): the arena data for the save (between ticks on the sim thread, IMod.cs:17-21).</summary>
    public void Write(Stream stream) => arena.Write(stream);

    /// <summary>IModSaveData (task 2.4): the saved arena data, applied in the first serial step. Only when the save holds our data
    /// (IMod.cs:29-33); a save made before the mod keeps the fresh ledger.</summary>
    public void Read(Stream stream) => arena.Read(stream);

    /// <summary>ICombatHitObserver (task 3.1): called serially on the tick thread for each landed contact (CombatHit.cs:47-59, from the combat
    /// system's Apply, CombatSystem.Apply.cs:49, :210; World.CombatHits.cs:71-91). It must not change the world: a melee hit goes to CombatHooks
    /// (task 3.2b), which offers its damage to the blow its ApplyBlow pair is counting, or, without that patch, counts the hit and records
    /// FightStarter's reaction itself (task 3.1's path; the C++ hook is the melee one, CombatClass::_iHitYouAreYouHit, CombatHooks.cpp:199-250).
    /// Nothing is allocated. Blocks never reach Meitou's observer (a block is not a landed contact): the ApplyBlow pair sees them.</summary>
    public void HitResolved(World world, in CombatHit hit)
    {
        if (hit.Source != CombatHitSource.Melee) return;
        // never throws: Meitou disables an observer that throws for the rest of the world and hashes the fault (World.CombatHits.cs:83-90)
        try { combatHooks.HitResolved(world, hit); }
        catch (Exception e) { Failed(hooksSlot, e); }
    }

    /// <summary>The combat hooks of <paramref name="world"/> when this process's live system runs it, else null (any thread: two volatile reads
    /// here, Current and the bound world; callers then read the hooks' volatile View; no allocation). The Harmony patches of src/Game/CombatHooks.cs find their per-world state through it.</summary>
    /// <summary>Task 10.1 (port only): the view ProvingGroundsApi answers from (any thread).</summary>
    internal ArenaApiView<CharacterId> ApiView => interop.View;

    /// <summary>Task 10.1: the interop line (pg api; any thread).</summary>
    internal string InteropPhase => interop.Phase;

    internal static Game.CombatHooks? HooksFor(World world) => Current is { } s && ReferenceEquals(s.boundWorld, world) ? s.combatHooks : null;

    /// <summary>The building right-click's per-world half for <paramref name="world"/> (task 4.2; the PlayerOrders.Give prefix asks it on the tick
    /// thread, serial phases), or null when this system does not run that world yet.</summary>
    internal static Game.ContextMenuHooks? InteractFor(World world) =>
        Current is { } s && ReferenceEquals(s.boundWorld, world) ? s.contextMenuHooks : null;

    /// <summary>The medical protocol's per-world half for <paramref name="world"/> (as <see cref="HooksFor"/>; the FirstAidSystem.Apply prefix of
    /// src/Game/ArenaMedical.cs, task 4.7). Null (the block fails open: treatment runs) once the combat hooks' serial step failed in this world:
    /// their match view may then be stale and must not keep blocking a match that is over. Read on the tick thread (World.Commit), as the
    /// failure log is written.</summary>
    internal static Game.ArenaMedical? MedicalFor(World world) =>
        Current is { } s && ReferenceEquals(s.boundWorld, world) && !s.Failing(s.hooksSlot) ? s.arenaMedical : null;

    /// <summary>Serial, once per tick: the clock, the original's frame loop in its order, then the 0.20 s publish.</summary>
    public void SlowWorld(World world)
    {
        long perfStart = PgPerf.Begin();   // task 9.2a: 0 while the instrument is off
        if (perfStart != 0) PerfResetIfAsked();
        boundWorld = world;
        TakeOver();
        combatLog.Bind(world);
        clock.Advance();
        if (perfStart != 0) SpikeBegin();
        long t = perfStart;   // task 9.2b: each step's end is the next one's start (one timestamp per step while on, none while off)
        int k = 0;
        try { towns.Refresh(world); }   // before every step that asks which town (task 2.2)
        catch (Exception e) { Failed(townsSlot, e); }
        t = StepDone(k++, t);
        try { identity.Refresh(world); }   // the lazy scan: only when the character table changed (src/Game/FighterIdentity.cs)
        catch (Exception e) { Failed(identitySlot, e); }
        t = StepDone(k++, t);
        try { Spikes.Spikes.Tick(world); }   // SPIKE: inert unless armed
        catch (Exception e) { Failed(spikeSlot, e); }
        t = StepDone(k++, t);
        // C++ CombatHooks.cpp:246-247 answers a hit or block at once inside the hit hook; the port records it in the commit step (CombatHooks'
        // ApplyBlow postfix, task 3.2b; HitResolved without that patch, CAP 3), and this first serial step after the commit answers them.
        try { fightStarter.ApplyIncomingAttacks(world, sparSession.Active); }
        catch (Exception e) { Failed(fightSlot, e); }
        t = StepDone(k++, t);
        try { fightStarter.SweepStaleTargets(world); }   // Meitou-only (task 3.5): a target left after a disengage
        catch (Exception e) { Failed(fightSlot, e); }
        t = StepDone(k++, t);
        for (int i = 0; i < frameLoop.Length; i++)
        {
            try { frameLoop[i].Run(world); }
            catch (Exception e) { Failed(i, e); }   // one step failing never stops the others nor reaches the game
            t = StepDone(k++, t);
        }
        try { worldLifecycle.AfterFrame(world); }   // save safety: postponed autosaves and the state the save prefix reads (task 2.5)
        catch (Exception e) { Failed(saveSlot, e); }
        t = StepDone(k++, t);
        try { combatHooks.AfterSerialStep(world); }   // the match view after every change this step made (task 3.2b)
        catch (Exception e) { Failed(hooksSlot, e); }
        t = StepDone(k++, t);
        try { interop.Tick(world); }   // port only (tasks 10.1 / 10.2): the API's view for other mods, then what we tell The Wasteland Remembers
        catch (Exception e) { Failed(interopSlot, e); }
        t = StepDone(k++, t);
        try { watch.Tick(world); }   // TEST ONLY (task 3.5): nothing unless a pg watch runs
        catch (Exception e) { Failed(spikeSlot, e); }
        t = StepDone(k++, t);
        PublishIfDue();
        StepDone(k, t);
        if (perfStart != 0)
        {
            long elapsed = PgPerf.Since(perfStart);
            slowWorldCost.Add(elapsed);
            if (elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= SpikeMs) SpikeLog(world, elapsed);
        }
    }

    /// <summary>Task 9.2a: <c>pg perf reset</c> empties this world's tick windows on its next measured serial step (sim thread only).</summary>
    void PerfResetIfAsked()
    {
        int serial = PgPerf.ResetSerial;
        if (serial == perfResetSeen) return;
        perfResetSeen = serial;
        slowWorldCost.Reset();
        inputsCost.Reset();
        stepCost.Reset();
    }

    /// <summary>Task 9.2b: one SlowWorld step began at <paramref name="start"/> (0 = the instrument was off at the tick's start: nothing) and
    /// ended now; its counter gets the time; returns now, the next step's start.</summary>
    long StepDone(int slot, long start)
    {
        if (start == 0) return 0;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        stepCost.Record(slot, now - start);
        stepLast[slot] = now - start;
        return now;
    }

    // task 9.2b, TEST ONLY while pg perf is on: a SlowWorld of 2 ms or more is logged as one PG-PERF line (its steps of 0.25 ms or more, the
    // collections and this thread's JIT time during it, and the time spent writing test-only lines and log-file lines in it; those two are
    // process-wide, so another thread's writes in the same moment count too), so a spike can be told apart: our work, a garbage collection,
    // first-call compilation, or log writing.
    const double SpikeMs = 2.0;
    long[] stepLast = [];
    int gcBefore0, gcBefore1, gcBefore2;
    TimeSpan jitBefore;
    long jitMethodsBefore, testLineBefore, fileLineBefore;
    ulong cyclesBefore;

    void SpikeBegin()
    {
        if (stepLast.Length != stepCost.SlotCount) stepLast = new long[stepCost.SlotCount];
        else Array.Clear(stepLast);
        gcBefore0 = GC.CollectionCount(0); gcBefore1 = GC.CollectionCount(1); gcBefore2 = GC.CollectionCount(2);
        jitBefore = System.Runtime.JitInfo.GetCompilationTime(currentThread: true);
        jitMethodsBefore = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
        testLineBefore = PgPerf.TestLineTicks;
        fileLineBefore = PgPerf.FileLineTicks;
        cyclesBefore = ThreadCpu.Cycles();
    }

    void SpikeLog(World world, long elapsed)
    {
        double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var steps = new System.Text.StringBuilder("[");
        for (int i = 0; i < stepLast.Length; i++)
            if (stepLast[i] * f >= 0.25)
                steps.Append(steps.Length > 1 ? "," : "").Append('[').Append(PGLog.Quote(stepCost.Name(i))).Append(',').Append(TestJson.F(stepLast[i] * f, 3)).Append(']');
        steps.Append(']');
        ProvingGroundsMod.Ctx?.Log("PG-PERF {\"event\":\"spike\",\"tick\":" + world.Tick.ToString(CultureInfo.InvariantCulture)
            + ",\"ms\":" + TestJson.F(elapsed * f, 3) + ",\"steps\":" + steps
            + ",\"gc\":[" + (GC.CollectionCount(0) - gcBefore0).ToString(CultureInfo.InvariantCulture) + "," + (GC.CollectionCount(1) - gcBefore1).ToString(CultureInfo.InvariantCulture)
            + "," + (GC.CollectionCount(2) - gcBefore2).ToString(CultureInfo.InvariantCulture) + "]"
            + ",\"jitMs\":" + TestJson.F((System.Runtime.JitInfo.GetCompilationTime(currentThread: true) - jitBefore).TotalMilliseconds, 3)
            + ",\"jitMethods\":" + (System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - jitMethodsBefore).ToString(CultureInfo.InvariantCulture)
            + ",\"testLineMs\":" + TestJson.F((PgPerf.TestLineTicks - testLineBefore) * f, 3) + ",\"fileLineMs\":" + TestJson.F((PgPerf.FileLineTicks - fileLineBefore) * f, 3)
            + ",\"cpuMs\":" + (ThreadCpu.Ms(cyclesBefore, ThreadCpu.Cycles()) is { } cpu ? TestJson.F(cpu, 3) : "null") + "}");
    }

    /// <summary>
    /// Task 9.2b, TEST ONLY (the spike line's <c>cpuMs</c>): the sim thread's own CPU time, from <c>QueryThreadCycleTime</c> (kernel32; exact cycles,
    /// where GetThreadTimes / ProcessThread.TotalProcessorTime count in whole scheduler ticks, about 15.6 ms, as long as the spikes being judged).
    /// Cycles become ms by a rate measured once on this thread: a 2 ms busy wait, cycles over Stopwatch time, the highest of three (the wait least
    /// interrupted). A tick whose CPU time is much less than its wall time was descheduled. Windows only: elsewhere, or if a call fails, null.
    /// </summary>
    static class ThreadCpu
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentThread();

        static bool unavailable = !OperatingSystem.IsWindows();
        static double cyclesPerMs;   // 0 = not measured yet

        internal static ulong Cycles()
        {
            if (unavailable) return 0;
            try { return QueryThreadCycleTime(GetCurrentThread(), out ulong c) ? c : 0; }
            catch (Exception) { unavailable = true; return 0; }
        }

        internal static double? Ms(ulong before, ulong after)
        {
            if (unavailable || before == 0 || after < before) return null;
            if (cyclesPerMs == 0) Calibrate();
            return cyclesPerMs > 0 ? (after - before) / cyclesPerMs : null;
        }

        static void Calibrate()
        {
            double best = 0;
            for (int i = 0; i < 3; i++)
            {
                ulong c0 = Cycles(); long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                while (PgPerf.Since(t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < 2.0) { }
                ulong c1 = Cycles(); double ms = PgPerf.Since(t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (c0 == 0 || c1 <= c0) { unavailable = true; return; }
                best = Math.Max(best, (c1 - c0) / ms);
            }
            cyclesPerMs = best;
        }
    }

    /// <summary>Task 9.2b: SlowWorld's step counters (any thread: Interlocked reads; the test-only <c>pg perf</c> answer's "steps").</summary>
    internal PatchCostCounters StepCosts => stepCost;

    /// <summary>Task 9.2a: the numbers to publish: fresh while the instrument is on, the last ones (marked off) while it is off.</summary>
    PgPerfStats? PerfStats()
    {
        if (PgPerf.On) return lastPerf = new PgPerfStats(true, slowWorldCost.Stats(), inputsCost.Stats());
        return lastPerf is { On: true } p ? lastPerf = p with { On = false } : lastPerf;
    }

    /// <summary>C++ WorldLifecycle.cpp:284 (first line of WorldLifecycle::Tick): the persistence notice; its own failure slot, so a throw here
    /// never skips the save-safety half of the step.</summary>
    void ArenaTick(World world)
    {
        try { arena.Tick(world); }
        catch (Exception e) { Failed(arenaSlot, e); }
    }

    /// <summary>This system becomes <see cref="Current"/> on its world's first serial step (only the sim thread writes Current; a volatile
    /// write). The old world's waiting saves are dropped with one log line. Frame-thread readers that find no system yet behave as before.</summary>
    void TakeOver()
    {
        var old = Current;
        if (ReferenceEquals(old, this)) return;
        Current = this;
        ProvingGroundsMod.Data = data;
        Items.ForgetWorld();   // task 5.2a: the old world's fighter-refresh assembly (and its PopulationData) is not kept
        try { Spikes.Spikes.Clear(); }   // SPIKE: the old world's holds and spar never carry into this one
        catch (Exception e) { Failed(spikeSlot, e); }
        try { old?.worldLifecycle.DropOnWorldChange(); }
        catch (Exception e) { Failed(saveSlot, e); }
        // C++ WorldLifecycle::AbandonWorldState (:265, :267) on the old world: SparSession first, then TownArena (TownArena.cpp:1797-1858), which
        // puts back the process-wide medical protocol an interrupted town event had set; ArenaMedical::SetProtocol refuses while a match is
        // active, so the old match must be abandoned before (task 7.1; Core/Glue/OldWorldAbandon.cs). Each step guarded.
        if (old?.boundWorld is { } oldWorld)
            OldWorldAbandon.Run(() => old.sparSession.AbandonWorldState(oldWorld), () => old.townArena.AbandonWorldState(oldWorld),
                (step, e) => Failed(saveSlot, new InvalidOperationException($"old world {step} abandon failed: {e.Message}", e)));   // the world abandon is the save safety's (WorldLifecycle), not this world's frame loop
    }

    /// <summary>The world's save safety (task 2.5): the save patches reach it from the frame thread through <see cref="Current"/>.</summary>
    internal Game.WorldLifecycle Lifecycle => worldLifecycle;

    string SlotName(int slot) => slot == interopSlot ? "interop" : slot == hooksSlot ? "combat hooks" : slot == fightSlot ? "fight starter" : slot == spikeSlot ? "spike tick" : slot == townsSlot ? "towns" : slot == identitySlot ? "fighter identity" : slot == arenaSlot ? "arena persistence" : slot == saveSlot ? "save safety" : slot == snapshotSlot ? "snapshot" : slot == settingsSlot ? "settings page" : frameLoop[slot].Name;

    /// <summary>Counts the failure; logs only the first per step per world. The step keeps running every tick.</summary>
    void Failed(int slot, Exception e)
    {
        if (stepFailures.Record(slot))
            ProvingGroundsMod.Ctx?.Log($"{SlotName(slot)} failed (logged once per world): {e.GetType().Name}: {e.Message}");
    }

    /// <summary>"N steps failing: name (xcount), ..." or empty; allocates only while something fails.</summary>
    string FailingText()
    {
        if (stepFailures.FailedSteps == 0) return "";
        var names = new List<string>();
        for (int i = 0; i <= interopSlot; i++)
            if (stepFailures.Count(i) > 0) names.Add(string.Create(CultureInfo.InvariantCulture, $"{SlotName(i)} (x{stepFailures.Count(i)})"));
        return string.Create(CultureInfo.InvariantCulture, $"{stepFailures.FailedSteps} steps failing: {string.Join(", ", names)}");
    }

    /// <summary>
    /// C++: ArenaUI.cpp:3172-3185, the idle UI refresh every kIdleUiRefreshIntervalSec (0.20 s, :213) through FrameCadencePolicy, with
    /// the clock's dt in place of GetTickCount(). The original refreshed only while its window was visible (and restarted the cadence on
    /// show/hide); the port publishes always: building the snapshot is cheap and the windows draw it on another thread whenever they are
    /// open. Nothing in the simulation reads the snapshot, so this changes no behaviour.
    /// </summary>
    void PublishIfDue()
    {
        if (publishCadence.Advance(clock.DtSeconds)) Publish();
    }

    void Publish()
    {
        try
        {
            long ticks = clock.Ticks;
            var d = arena.Data;
            string arenaText = string.Create(CultureInfo.InvariantCulture,
                $"{d.PlayerStandings.Count} player rows, {d.TownStandings.Count} town rows, {d.Progression.Count} progression rows, {d.FactionUnlocks.Count} unlocks");
            snapshot = new PgSnapshot(ticks, SimClock.NowMsAfter(ticks), lastCommand, FailingText(), lastProbe, arenaText, arena.PersistenceError,
                arena.ResetOffered, worldLifecycle.StatusText, Phases(), lastCensus,
                sparSession.Running ? sparSession.GetStatus().Replace('\n', ' ') : "none", UiModels(), contextMenuHooks.InteractRecords,
                sparSession.Running || arenaIngress.Logic.FightIngressOrWalkInPending, rewardDelivery.LastPurchases, PerfStats());
        }
        catch (Exception e) { Failed(snapshotSlot, e); }
    }

    /// <summary>Each subsystem's phase for the F10 status row (sim thread, in Publish): the real ones say what they know, a placeholder
    /// "idle" (its task replaces it), a frame-loop step that threw in this world "failing".</summary>
    (string Name, string Phase)[] Phases()
    {
        var phases = new List<(string, string)>
        {
            ("log", PGLog.Directory.Length > 0 ? "pg_debug.log in the mod folder" : "off"),
            ("settings", Ui.PGSettingsUI.Loaded ? "loaded" : "not loaded"),
            ("towns", towns.Scratch is null ? "no Scratch" : "Scratch found"),
            ("fighter identity", Failing(identitySlot) ? "failing" : "ready"),
            ("arena persistence", arena.Ready ? "ready" : arena.PersistenceError.Length > 0 ? "blocked" : "not resolved"),
            ("save safety", worldLifecycle.StatusText),
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < frameLoop.Length; i++)
        {
            string name = PgStatusRow.Subsystem(frameLoop[i].Name);
            if (name == "WorldLifecycle") continue;   // "save safety" above
            bool failing = Failing(i);
            string idle = name == "ArenaRingGuard" ? arenaRingGuard.Phase : name == "ArenaIngress" ? arenaIngress.Phase : name == "ArenaUI" ? arenaUiSim.Phase
                : name == "ContextMenuHooks" ? contextMenuHooks.Phase : name == "DebugMenu" ? debugMenu.Sim.Phase : name == "MarksHUD" ? marksHud.Sim.Phase
                : name == "RewardsUI" ? rewardsUi.Sim.Phase : name == "PrisonerRecruitmentUI" ? prisonerRecruitmentUi.Sim.Phase
                : name == "TownArenaUI" ? townArenaUi.Sim.Phase
                : name == "TownBookie" ? townBookie.Phase
                : name == "TownArena" ? (failing ? "failing" : TownPhase()) : "idle";   // the real ones say what they know
            if (seen.Add(name)) phases.Add((name, failing ? "failing" : idle));
            else if (failing) phases[phases.FindIndex(p => p.Item1 == name)] = (name, "failing");
        }
        phases.Add(("SparSession", sparSession.Phase
            + (sparSession.FailureCount > 0 ? string.Create(CultureInfo.InvariantCulture, $", {sparSession.FailureCount} stop steps failed") : "")));
        phases.Add(("FightStarter", (Failing(fightSlot) ? "failing"
            : fightStarter.RememberedCount > 0 ? string.Create(CultureInfo.InvariantCulture, $"{fightStarter.RememberedCount} fighters engaged") : "idle")
            + (fightStarter.DroppedHits > 0 ? string.Create(CultureInfo.InvariantCulture, $", {fightStarter.DroppedHits} hits dropped") : "")
            + (fightStarter.DisengageFailures > 0 ? string.Create(CultureInfo.InvariantCulture, $", {fightStarter.DisengageFailures} disengage steps failed") : "")));
        phases.Add(("SparStats", sparStats.Phase));
        phases.Add(("CombatHooks", Failing(hooksSlot) ? "failing" : combatHooks.Phase));
        phases.Add(("Interop", Failing(interopSlot) ? "failing" : interop.Phase));   // tasks 10.1 / 10.2
        phases.Add(("ArenaMedical", arenaMedical.Phase + (Failing(hooksSlot) ? "; match view failing: treatment not blocked" : "")));
        phases.Add(("TownLimbShop", townLimbShop.PhaseText()));   // task 8.5
        phases.Add(("TownAnnouncer", townAnnouncer.PhaseText()));   // task 8.3
        return [.. phases];
    }

    bool Failing(int slot) => stepFailures.Count(slot) > 0;

    /// <summary>The TownArena phase for the F10 row (task 7.1): its own guard, so a throw there reads "failing" and never blanks the snapshot.
    /// A publish-time throw is counted on the TownArena.Tick slot on purpose (the same state the step reads), so the row then stays "failing".</summary>
    string TownPhase()
    {
        try { return townArena.Phase(boundWorld); }
        catch (Exception e) { Failed(townArenaSlot, e); return "failing"; }
    }

    /// <summary>The F10 row "Proving Grounds: status" (frame thread, the snapshot only): <see cref="Status"/>, each subsystem's phase, the
    /// current match (M3: SparSession) and the town state (M7: TownArena).</summary>
    internal string StatusRow()
    {
        var s = snapshot;   // read once: the status line and the phases come from the same snapshot
        return PgStatusRow.Text(Status(s), s.Phases, s.Match, "idle",
            s.Perf is { On: true } perf ? perf.Tick.RowText() : null);   // the current match: published by Publish (task 3.3); task 9.2a: the tick cost while pg perf is on
    }

    /// <summary>The published snapshot (any thread).</summary>
    internal PgSnapshot Snapshot => snapshot;

    /// <summary>One line for <c>pg status</c> and the debug command (frame thread: reads only the snapshot and the clock).</summary>
    internal string Status() => Status(snapshot);

    /// <summary><see cref="Status()"/> of one snapshot.</summary>
    internal string Status(PgSnapshot s)
    {
        long ticks = clock.Ticks;   // read once: both values come from the same tick
        uint now = SimClock.NowMsAfter(ticks);
        string failing = s.Failing.Length > 0 ? "; " + s.Failing : "";
        string reset = (s.ArenaBlocked.Length != 0 ? $" BLOCKED: {s.ArenaBlocked}" : "")
            + (s.ArenaResetOffered ? $" ('{Game.ArenaNativePersistence.ResetChoiceCaption}' offered)" : "");
        return string.Create(CultureInfo.InvariantCulture, $"Proving Grounds {ProvingGroundsMod.Version}: {ticks} slow ticks this world; clock {now} ms; snapshot {SimClock.ElapsedMs(now, s.NowMs)} ms old; arena: {s.Arena}{reset}; saves: {s.SaveSafety}; last command: {s.LastCommand}{failing}");
    }
}
