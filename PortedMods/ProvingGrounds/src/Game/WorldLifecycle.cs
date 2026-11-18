using System.Collections.Concurrent;
using HarmonyLib;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Saving;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/WorldLifecycle.h/.cpp (Proving Grounds by Shutori, GPLv3), task 2.5 (PA §2.20 "Save safety",
// spike S5). The state machine (busy, autosave postponed, manual save prepared or waiting for cage locks) is Core/Glue/SaveSafety.cs, copied
// line by line and tested there; this file is the Meitou side: the two Harmony patches, the thread handshake and the calls into the
// subsystems. Meitou 0.17.3 (tag v0.17.3, fa34a1b).
//
// Engine calls of the C++ and what replaces them (E1):
//   hook SaveManager::save (:182-201, InstallHooks :315-322) ... bool prefix on GameSaves.QuickSaveNow(string? destination, bool feedback)
//       (GameSaves.cs:190; Members.QuickSaveNow): the single entry of F5, menu Save, the agent's quicksave, the autosave and the feedback
//       report. The C++ `autosave` flag = the destination is one of QuickSave.AutosaveFolder(0..2) (QuickSave.cs:30-35; GameSaves.cs:105)
//   SaveManager_save_orig(self, name, autosave) (the C++ calls it to run a save past its hook, :176, :301) ... re-request the save on the
//       frame thread: GameSaves.SaveRequestedFolder = folder (GameSaves.cs:44, taken at :70-74) or QuickSaveRequested = true for the
//       quicksave slot (:31, :60-64), set in a prefix on GameSaves.HandleSaves (GameSaves.cs:54), which takes it in the same call; the
//       QuickSaveNow prefix lets that one save through (`reRequested`)
//   hook SaveManager::updateAutoSave (:151-180) ... none: Meitou's own autosave timer runs unchanged (GameSaves.cs:98-115) and its save is
//       caught by the QuickSaveNow prefix, as the C++ save hook catches it; the hold/release log lines and the release of a postponed
//       autosave run in AfterFrame, once per tick (only while the game runs: the C++ also waits for !pauseAutoSaveTimer)
//   hook SaveManager::execute (:203-226): a queued SAVEGAME held while save-sensitive ... a re-requested save waits in `reRequests` while
//       the published state is save-sensitive (BeforeHandleSaves). Its NEWGAME branch (AbandonWorldState, CompleteWorldTransition) ... none:
//       a new game builds a new ProvingGroundsSystem (and a fresh arena ledger, ArenaNativePersistence.Resolve)
//   hook GameWorld::_clearAndDestroyGameWorldStuff (:227-232) ... none: the system is rebuilt for every world; nothing carries over
//   ou->showPlayerAMessage(text, true) ... one way of showing for all: every message of a serial step is PgEngine.Message (world.Post),
//       which Meitou shows between ticks as Player.Notice(message) (GameHost.FrameShared.cs:121-127, error false); the one message the frame
//       thread shows at the key press (the match-cancelled text) is the same call, PlayerInterface.Notice(text, false, null), through
//       GameSaves' GameHost and GameHost.Player (Members.GameSavesHost / HostPlayer / Notice). Kenshi's `true` (shown in the HUD) has no
//       counterpart; Meitou's `error` flag is not used by world.Post, so neither uses it
//   SaveManager::save refusing nothing while a save is written (SaveManager_save_orig queues it) ... GameSaves.Busy() (GameSaves.cs:130:
//       a write still running, or the host covered): a re-requested save waits for it (Members.SavesBusy), since Meitou refuses a save made
//       then ("Save or load still in progress", :204-207) and it would be lost
//   LogLifecycle -> PGLog::Debug (:50-56) ... the mod log (task 2.7 routes it to PGLog.Debug)
//   KenshiLib::AddHook failures (:303-345) ... Members.MissingSave(): the part is off with one log line, saves run as without the mod
//
// C++ function -> port:
//   IsArenaOperationBusy (:235-249), HasSaveSensitiveActivity (:80-83), PrepareForManualSave (:84-150), FinishManualCleanup (:62-72),
//       ReportManualCancellation (:73-79), the save hook's branches (:182-201), the updateAutoSave logic (:151-180), Tick (:282-302)
//       ... Core/Glue/SaveSafety.cs (sim thread)
//   Tick's first line ArenaNativePersistence::Tick (:284) ... the arenaTick delegate, first in Tick
//   AbandonWorldState (:251-280) ... none (port map partial): one system per world. Its FighterIdentity::AbandonWorldState is not needed
//       either: the identity map is a field of the system, so a new world starts with an empty one (tokens included, which the C++ kept only
//       for the process; Meitou re-creates them from the save, task 2.3/2.4)
//   InstallHooks (:303-347) ... SaveSafetyHooks.Install
//
// THREADS (the handshake). The QuickSaveNow and HandleSaves patches run on the host's frame thread, between world ticks (GameSaves.cs:17-22,
// GameLoop.cs:1346), but a tick may be running ahead on another thread until QuickSaveNow settles it (:197, after our prefix), so the patches
// never touch the world. They read and write only:
//   - `published` (volatile, immutable SaveGateState): written by the sim thread at the end of every SlowWorld (AfterFrame);
//   - `forcedBusy` (volatile bool): test-only `pg busy on|off`, written by the console (frame thread), read by both; the sim clears it in
//     SparSession.AbortForSave (the forced flag stands in for a running match);
//   - pendingManual (Core/Glue PendingManualSave, the refused manual saves in press order, frame -> sim), delayedAutosaves (ConcurrentQueue, frame -> sim),
//     reRequests (ConcurrentQueue, sim -> frame);
//   - `reRequested`: frame thread only (set in the HandleSaves prefix, consumed by the QuickSaveNow prefix, cleared in the postfix).
// Sequence of a manual save during a match: the prefix sees published.NeedsManualPreparation, shows the C++ "match cancelled" notice, queues
//   the folder, returns false (nothing saved). A second press for the same folder before the next tick replaces it; a different folder is its own save
//   (prepared after the first, which leaves the arena idle, so no second notice, as in the C++). The next tick's WorldLifecycle.Tick (serial) runs PrepareForManualSave: the cancel calls in the
//   C++ order; ready -> the folder goes into reRequests; waiting for cage locks -> kept in SaveSafety until Tick's gate poll resumes it (then
//   reRequests) or cancels it. AfterFrame publishes the new state. On the next frame the HandleSaves prefix (state not save-sensitive, Meitou not
//   writing, no request of the player's or of Meitou's autosave already in this frame) sets SaveRequestedFolder / QuickSaveRequested for ONE
//   re-request; HandleSaves calls QuickSaveNow, the prefix recognises it and lets it through only if GameSaves.Busy() is false (else it stays
//   queued, refused silently, and goes again on a later frame): it leaves the queue only when its save starts, so none is lost. An autosave while
//   save-sensitive: the prefix queues it in delayedAutosaves and returns false; AfterFrame records it (SaveSafety.DeferAutosave) and releases
//   it into reRequests once idle.
// Why it cannot race in a harmful way: every cross-thread value is a volatile reference/bool or a concurrent queue; each queue has one
//   writer thread and one reader thread; the Meitou request fields are written only on the frame thread (which owns them: GameLoop.cs:683, :718),
//   and SaveRequestedFolder only when it is empty, so a request of the player's is never overwritten. Harmless windows: (1) `published` is up to
//   one tick old, so a save made in the tick a match starts goes through as an idle save (as a C++ save in the same frame would); the world is
//   then rebuilt fresh on load (borrowed NPCs are released by every save/load, 00 §9). (2) A manual save pressed while the game is paused is
//   refused at once and prepared only when the game runs again (no serial step runs while paused): divergences.md. (3) A re-requested save
//   whose notice the frame showed from a published state one tick old may find nothing to cancel in the sim (the
//   match ended meanwhile): the notice was shown and the save is simply made. (4) The prepare runs in the WorldLifecycle.Tick slot of the
//   frame loop; the rest of that tick (KOWatcher, TownArena, ArenaRingGuard, ...) still runs before Meitou captures the save on a later frame.
//   (5) A world change (load, new game) drops the saves the old world still held (a manual save refused before the load, a postponed
//   autosave, a re-request not yet started), with one log line, as the C++ AbandonWorldState clears them (:271-276). The new system takes
//   ProvingGroundsSystem.Current only on its first serial step, so a world Meitou builds ahead (GameSaves.PrepareSystems, the "systems"
//   load stage, GameSaves.cs:324-327, 419-436) or one whose swap fails (SwapInSave / WorldSession.ReplaceWorld keep the old world,
//   GameSaves.cs:444-466, WorldSession.cs:142-156) never takes the save path from the world still running.
//   (6) Fail open: a throw while reading the arena publishes Idle; a throw while preparing a manual save still makes that save; a throw in
//   a frame patch lets the save through (one log line each).
// After PrepareForManualSave cancelled arena work, nothing may restart activity before the re-requested save is captured (a town bout
//   scheduler starting a new bout in the same tick or the next would be saved mid-start); the C++ has no such gap (it prepares and saves in one
//   call). M7 done (task 7.1): TownArena's schedule plans and begins nothing while SaveInFlight (TownArenaLogic.Tick, port-only gate; town
//   events start only there, task 7.3's BeginEvent). M3 done (task 3.3): SparSession.StartMatch refuses while SaveInFlight (below); KOWatcher,
//   the Teams 1v1 bout and the ring guard act only on a running match, which the prepare ended. M4 done (task 4.3b): ArenaIngress.Begin
//   refuses while SaveInFlight; an ingress the prepare cancelled cannot resume (Cancel clears it), and its countdown's StartMatch refuses too.

/// <summary>A save folder (null = the quicksave slot) and whether the frame thread already showed the match-cancelled notice for it.</summary>
sealed record SaveSlot(string? Folder, bool Announced);

/// <summary>The world life cycle and save safety of one world (task 2.5). Owned by <see cref="ProvingGroundsSystem"/>; its frame-loop step is
/// <see cref="Tick"/> (C++ :3158) and <see cref="AfterFrame"/> runs after the frame loop, before the publish.</summary>
sealed class WorldLifecycle
{
    readonly SaveSafety safety;
    readonly Subsystems world;
    readonly Action<World> arenaTick;
    readonly Action<string> log;   // the port's own "PG save safety:" lines (no C++ counterpart): Meitou's log (task 2.7)

    volatile SaveGateState published = SaveGateState.Idle;
    volatile bool forcedBusy;
    readonly PendingManualSave pendingManual = new();
    readonly ConcurrentQueue<SaveSlot> delayedAutosaves = new(), reRequests = new();
    SaveSlot? reRequested;   // frame thread only
    int statusKey = -1;      // sim thread only
    string statusText = "idle";

    /// <summary>Meitou's three autosave folders (QuickSave.cs:30-35), the C++ hook's <c>autosave</c> flag.</summary>
    static readonly string[] AutosaveFolders = [.. Enumerable.Range(0, QuickSave.AutosaveSlots).Select(i => QuickSave.AutosaveFolder(i))];

    internal WorldLifecycle(Subsystems subsystems, Action<World> arenaTick, Action<string> log)
    {
        world = subsystems;
        subsystems.Owner = this;
        safety = new SaveSafety(subsystems);
        this.arenaTick = arenaTick;
        this.log = log;
    }

    /// <summary>Task 3.3 (the TO-DO above, for SparSession): a save the sim prepared (or a released autosave) is queued for the frame thread
    /// and not captured yet, or a manual save waits for cage locks. SparSession.StartMatch refuses then (port-only), so no match starts between
    /// the prepare and the capture. Sim thread (a concurrent queue's IsEmpty and a sim-owned read).</summary>
    internal bool SaveInFlight => !reRequests.IsEmpty || safety.ManualSaveWaiting;

    /// <summary>C++ <c>WorldLifecycle::IsArenaOperationBusy()</c> (WorldLifecycle.cpp:235-249) for another subsystem's serial step (task 7.1:
    /// TownArena's schedule). Binds <paramref name="w"/> for the reads and puts back whatever was bound (re-entry safe). Sim thread.</summary>
    internal bool IsArenaOperationBusy(World w)
    {
        var was = world.World;
        world.World = w;
        try { return safety.IsArenaOperationBusy(); }
        finally { world.World = was; }
    }

    /// <summary>The subsystems the save path calls (C++ module calls), bound to the world of the current serial step.</summary>
    internal sealed class Subsystems(ArenaIngress ingress, SparSession spar, PrisonerUtil prisoners, Town.TownArena town, Town.TownLimbShop limbShop,
        Ui.ResultsUI results, SparStats sparStats, FightStarter fightStarter, Ui.ArenaUI arenaUi, Ui.DebugMenu debugMenu, Action<string> cppLog,
        Action<string> ownLog)
        : ISaveSafetyWorld
    {
        internal WorldLifecycle? Owner;
        internal World? World;
        World W => World ?? throw new InvalidOperationException("save safety used outside a serial step");
        public bool IngressPending => ingress.IsPending(W);
        public bool WalkInPending => ingress.IsWalkInPending(W);
        /// <summary>SparSession::IsActive; TEST ONLY: or `pg busy on` (a stand-in match).</summary>
        public bool MatchActive => spar.IsActive(W) || Owner?.forcedBusy == true;
        public bool PrisonerRuntimeActivity => prisoners.HasRuntimeActivity(W);
        public bool PendingCageLocks => prisoners.HasPendingCageLocks(W);
        public bool CageReturnFailure => prisoners.HasCageReturnFailure(W);
        public bool TownBusy => town.IsBusy(W);
        public bool TownHasBooking => town.HasBooking(W);
        public bool LimbShopVisitActive => limbShop.IsVisitActive(W);
        public bool LimbShopPhaseActive => limbShop.PhaseActive(W);
        public string CageReturnStatus() => prisoners.GetCageReturnStatus(W);
        public void LimbShopSuspendVisit() => limbShop.SuspendVisit(W);
        public void TownCancel() => town.Cancel(W);
        public void TownCancelForSave() => town.CancelForSave(W);
        public void IngressCancel() => ingress.Cancel(W);
        public void SparAbortForSave()
        {
            spar.AbortForSave(W);
            if (Owner is { } o && o.forcedBusy) { o.forcedBusy = false; ownLog("PG save safety: the pg busy stand-in match was cancelled for the save"); }   // port only
        }
        public string PrisonersReturnAllToCages() { prisoners.ReturnAllToCages(W, out string status); return status; }
        public void ResultsClose() => results.Close(W);
        public void SparStatsAbandon() => sparStats.AbandonWorldState(W);
        public void IngressAbandon() => ingress.AbandonWorldState(W);
        public void SparSessionAbandon() => spar.AbandonWorldState(W);
        public void PrisonerAbandon() => prisoners.AbandonWorldState(W);
        public void FightStarterAbandon() => fightStarter.AbandonWorldState(W);
        public void ArenaUiAbandon() => arenaUi.AbandonWorldState(W);
        public void DebugMenuAbandon() => debugMenu.AbandonWorldState(W);
        public void Message(string text) => PgEngine.Message(W, text);
        /// <summary>Core/Glue/SaveSafety.cs's lines: the C++ texts of WorldLifecycle.cpp (LogLifecycle -> PGLog::Debug, :42-47).</summary>
        public void Log(string text) => cppLog(text);
    }

    // ---------- sim thread (serial steps) ----------

    /// <summary>C++: WorldLifecycle.cpp:282-302. The persistence notice first (:284), then the manual saves the frame thread refused since the
    /// last tick (the C++ save hook's manual branch, :189-201), then the gate poll of a save waiting for cage locks.</summary>
    internal void Tick(World w)
    {
        arenaTick(w);
        world.World = w;
        try
        {
            while (pendingManual.Take() is { } request)   // in press order, each prepared and saved (the C++ save hook per call)
                ReRequest(safety.ManualSaveOrSave(request.Folder, request.Announced, e => FailedOnce("manual save preparation", e)));
            ReRequest(safety.Tick());
        }
        finally { world.World = null; }
    }

    /// <summary>After the frame loop, every tick: the autosaves the frame thread postponed, the C++ updateAutoSave hold and release (:151-180),
    /// then the state the frame thread decides with.</summary>
    internal void AfterFrame(World w)
    {
        world.World = w;
        try
        {
            while (delayedAutosaves.TryDequeue(out var autosave)) safety.DeferAutosave(autosave.Folder);
            var next = safety.GateStateOrIdle(saveStateFailed ??= e => FailedOnce("save state", e));   // fail open: a broken read publishes Idle; task 9.2b: one delegate per world, not one per tick
            ReRequest(safety.AutosaveTick(next.SaveSensitive));
            if (next != published) published = next;
            bool busy = next.ArenaBusy, sensitive = next.SaveSensitive;
            int key = (busy ? 1 : 0) | (sensitive ? 2 : 0) | (forcedBusy ? 4 : 0) | (safety.AutosavePostponed ? 8 : 0)
                | (safety.ManualSaveWaiting ? 16 : 0) | (SaveSafetyHooks.Installed ? 32 : 0);
            if (key != statusKey)
            {
                statusKey = key;
                statusText = !SaveSafetyHooks.Installed ? "off (save hooks missing)"
                    : (busy ? "arena busy" : sensitive ? "save-sensitive" : "idle") + (forcedBusy ? " (pg busy on)" : "")
                    + (safety.AutosavePostponed ? ", autosave postponed" : "") + (safety.ManualSaveWaiting ? ", manual save waiting for cage locks" : "");
            }
        }
        finally { world.World = null; }
    }

    bool failureLogged;   // sim thread: one line per world for a fail-open step
    Action<Exception>? saveStateFailed;   // task 9.2b: AfterFrame's fail-open callback, built on first use

    void FailedOnce(string what, Exception e)
    {
        if (failureLogged) return;
        failureLogged = true;
        log($"PG save safety: {what} failed, saves run as without the mod (logged once per world): {e.GetType().Name}: {e.Message}");
    }

    /// <summary>A new world's system took over (ProvingGroundsSystem.TakeOver): saves this one still held are dropped, as the C++
    /// AbandonWorldState clears g_deferredAutosave and g_manualSaveName (:271-276). One log line when there were any.</summary>
    internal void DropOnWorldChange()
    {
        int n = 0;
        while (pendingManual.Take() is not null) n++;
        while (delayedAutosaves.TryDequeue(out _)) n++;
        while (reRequests.TryDequeue(out _)) n++;
        if (n > 0) log($"PG save safety: world changed: {n.ToString(System.Globalization.CultureInfo.InvariantCulture)} waiting save(s) of the old world dropped");
    }

    void ReRequest(SaveRequest request)
    {
        if (!request.Requested) return;
        reRequests.Enqueue(new SaveSlot(request.Folder, false));
        log("PG save safety: save re-requested: " + (request.Folder ?? "(quicksave)"));
    }

    /// <summary>The save-safety part of the snapshot (sim thread, at publish).</summary>
    internal string StatusText => statusText;

    // ---------- frame thread ----------

    /// <summary>TEST ONLY <c>pg busy on|off</c> (frame thread): forces a stand-in match, so a live run can test the refusal and the delay.</summary>
    internal bool ForcedBusy { get => forcedBusy; set => forcedBusy = value; }

    /// <summary>The QuickSaveNow prefix (frame thread): true lets Meitou save now.</summary>
    internal bool BeforeQuickSave(object saves, string? destination, bool feedback)
    {
        bool mine = reRequested is { } r && SaveGate.SameFolder(r.Folder, destination);
        var state = published;
        if (forcedBusy) state = state with { ArenaBusy = true, SaveSensitive = true, NeedsManualPreparation = true };   // test only
        bool autosave = SaveGate.IsAutosaveFolder(destination, AutosaveFolders);
        bool pending = pendingManual.Pending;
        switch (SaveGate.Decide(feedback, autosave, mine, mine && SaveSafetyHooks.SavesBusy(saves), pending, state))
        {
            case SaveGateDecision.Allow:
                if (mine)
                {
                    reRequests.TryDequeue(out _);   // its save starts now: only now does it leave the queue
                    reRequested = null;
                }
                return true;
            case SaveGateDecision.HoldReRequest:
                reRequested = null;
                log("PG save safety: re-requested save " + (destination ?? "(quicksave)") + " waits: Meitou is still writing a save");
                return false;
            case SaveGateDecision.DelayAutosave:
                delayedAutosaves.Enqueue(new SaveSlot(destination, false));
                log("PG save safety: autosave " + destination + " delayed until the arena is idle");
                return false;
            default:
                bool announce = SaveGate.Announce(state, pending);
                if (announce) SaveSafetyHooks.Notice(saves, SaveSafety.MatchCancelledText);
                else if (!pending && SaveSafetyHooks.GamePaused(saves)) SaveSafetyHooks.Notice(saves, ResumeNotice);   // port-only text
                pendingManual.Offer(destination, announce);
                log("PG save safety: manual save " + (destination ?? "(quicksave)") + " refused once; preparing it next tick"
                    + (announce ? " (notice shown: " + SaveSafety.MatchCancelledText + ")" : ""));
                return false;
        }
    }

    /// <summary>Port-only text (divergences.md): a manual save refused while the game is paused waits for the next tick, which only comes when
    /// the game runs again; said when no C++ message is shown at the key press.</summary>
    internal const string ResumeNotice = "Save will run when the game resumes.";

    /// <summary>The QuickSaveNow prefix threw (frame thread): a re-requested save for this folder leaves the queue, so it is not tried again
    /// every frame (the save itself goes through as without the mod).</summary>
    internal void PrefixFailed(string? destination)
    {
        if (reRequested is { } r && SaveGate.SameFolder(r.Folder, destination))
        {
            reRequests.TryDequeue(out _);
            reRequested = null;
        }
    }

    /// <summary>The HandleSaves prefix (frame thread): re-requests one save the system prepared or released (the C++ SaveManager_save_orig
    /// call), held while the published state is save-sensitive (the C++ execute hook, :203-213).</summary>
    internal void BeforeHandleSaves(object saves)
    {
        reRequested = null;
        if (forcedBusy || published.SaveSensitive || !reRequests.TryPeek(out var slot)) return;
        // One re-request per HandleSaves, and only when nothing else in this call would start a save before it: a running write
        // (GameSaves.Busy, :130), Meitou's own autosave due this frame (_autosaveDue, :39, written at :100-106), or the player's F5 (taken
        // first, :60) / folder request (:70). Otherwise QuickSaveNow would refuse ours with "Save or load still in progress" (:204-207).
        if (SaveSafetyHooks.SavesBusy(saves) || Members.AutosaveDue!.GetValue(saves) is true) return;
        bool quick = Members.QuickSaveRequested!.GetValue(saves) is true;
        if (slot.Folder is null) { if (quick || Members.SaveRequestedFolder!.GetValue(saves) is not null) return; Members.QuickSaveRequested.SetValue(saves, true); }   // never merged with the player's F5
        else
        {
            if (quick || Members.SaveRequestedFolder!.GetValue(saves) is not null) return;   // the player's own request first; ours next frame
            Members.SaveRequestedFolder.SetValue(saves, slot.Folder);
        }
        reRequested = slot;   // leaves the queue when the QuickSaveNow prefix lets it start
    }

    /// <summary>The HandleSaves postfix (frame thread): a re-requested save is let through only in the HandleSaves call that took it; one that
    /// did not start stays queued for a later frame.</summary>
    internal void AfterHandleSaves() => reRequested = null;
}

/// <summary>The Harmony patches of the save path (spike S5, kept for real in task 2.5; no arming). They find the live world's
/// <see cref="WorldLifecycle"/> through <see cref="ProvingGroundsSystem.Current"/>; with none (no world yet) every save runs as without the mod.
/// A patch never throws into the game: on an exception the save goes through and the failure is logged once.</summary>
static class SaveSafetyHooks
{
    static int failuresLogged;
    /// <summary>The three patches are on.</summary>
    internal static volatile bool Installed;

    /// <summary>The save path's Harmony patches (applied by <see cref="Install"/> and by PgCheck hooks, through PgPatches.Apply).</summary>
    internal static IEnumerable<PgPatch> Patches() =>
    [
        new(Part.Save, "GameSaves.QuickSaveNow prefix", Members.QuickSaveNow, PgPatches.Method(typeof(SaveSafetyHooks), nameof(BeforeQuickSave)), null),
        new(Part.Save, "GameSaves.HandleSaves prefix + postfix", Members.HandleSaves, PgPatches.Method(typeof(SaveSafetyHooks), nameof(BeforeHandleSaves)),
            PgPatches.Method(typeof(SaveSafetyHooks), nameof(AfterHandleSaves))),
    ];

    /// <summary>C++: WorldLifecycle.cpp:303-347 <c>InstallHooks</c> (the save half; ArenaNativePersistence's hooks are IModSaveData, task 2.4).</summary>
    internal static bool Install(ModContext c)
    {
        try
        {
            var missing = Members.MissingSave().ToList();
            // port-only texts (the C++ "failed to hook SaveManager::..." lines, :313-340, name hooks Meitou has not): Meitou's error log
            if (missing.Count > 0) { c.LogError($"save safety off: {string.Join(", ", missing)} not found (saves run as without the mod)"); return false; }
            foreach (var p in Patches()) PgPatches.Apply(c.Harmony, p);
            Installed = true;
            Core.PGLog.Debug("Proving Grounds: save/load lifecycle hooks installed");   // C++ :346-347 (LogLifecycle -> PGLog::Debug)
            return true;
        }
        catch (Exception e) { c.LogError($"save safety off: patch failed ({e.GetType().Name}: {e.Message})"); return false; }
    }

    static void Failed(string where, Exception e)
    {
        if (Interlocked.Increment(ref failuresLogged) <= 3) ProvingGroundsMod.Ctx?.LogError($"save safety {where} failed (the save runs as without the mod): {e.GetType().Name}: {e.Message}");
    }

    /// <summary>Prefix on GameSaves.QuickSaveNow (GameSaves.cs:190); parameter names match it.</summary>
    internal static bool BeforeQuickSave(object __instance, string? destination, bool feedback)
    {
        using var perfScope = PgPerf.Measure(PgPerf.QuickSave);   // task 9.2a: the cost instrument (off: one volatile read)
        try { return ProvingGroundsSystem.Current?.Lifecycle.BeforeQuickSave(__instance, destination, feedback) ?? true; }
        catch (Exception e)
        {
            Failed("QuickSaveNow prefix", e);
            try { ProvingGroundsSystem.Current?.Lifecycle.PrefixFailed(destination); } catch { }
            return true;
        }
    }

    /// <summary>Prefix on GameSaves.HandleSaves (GameSaves.cs:54).</summary>
    internal static void BeforeHandleSaves(object __instance)
    {
        using var perfScope = PgPerf.Measure(PgPerf.HandleSavesPrefix);   // task 9.2a: the cost instrument (off: one volatile read)
        try { ProvingGroundsSystem.Current?.Lifecycle.BeforeHandleSaves(__instance); }
        catch (Exception e) { Failed("HandleSaves prefix", e); }
    }

    /// <summary>Postfix on GameSaves.HandleSaves.</summary>
    internal static void AfterHandleSaves()
    {
        using var perfScope = PgPerf.Measure(PgPerf.HandleSavesPostfix);   // task 9.2a: the cost instrument (off: one volatile read)
        try { ProvingGroundsSystem.Current?.Lifecycle.AfterHandleSaves(); }
        catch (Exception e) { Failed("HandleSaves postfix", e); }
    }

    /// <summary><c>ou->showPlayerAMessage(text, true)</c> on the frame thread: PlayerInterface.Notice(text, false, null) through GameSaves'
    /// GameHost (spike S5): the same call Meitou makes for a world.Post message (GameHost.FrameShared.cs:127), so every save-safety message
    /// shows the same way.</summary>
    internal static void Notice(object saves, string text)
    {
        try
        {
            if (Members.GameSavesHost?.GetValue(saves) is { } host && Members.HostPlayer?.GetValue(host) is { } player)
                Members.Notice?.Invoke(player, [text, false, null]);
        }
        catch (Exception e) { Failed("notice", e); }   // the refuse / queue decision stands
    }

    /// <summary>The game is paused: GameHost.Session (GameHost.cs:283) .Simulation.IsPaused (public, WorldSession.cs:75,
    /// SimulationClock.cs:118), as Meitou's autosave reads it (GameSaves.cs:108). No serial step runs while it is true.</summary>
    internal static bool GamePaused(object saves)
    {
        try
        {
            return Members.GameSavesHost?.GetValue(saves) is { } host && Members.HostSession?.GetValue(host) is Meitou.Game.Host.WorldSession session
                && session.Simulation.IsPaused;
        }
        catch (Exception e) { Failed("pause read", e); return false; }
    }

    /// <summary>GameSaves.Busy() (GameSaves.cs:130): a save's write still running, or the host covered.</summary>
    internal static bool SavesBusy(object saves) => Members.SavesBusy?.Invoke(saves, null) is true;
}
