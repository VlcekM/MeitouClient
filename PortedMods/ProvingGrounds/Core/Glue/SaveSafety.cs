// Save safety of upstream/ProvingGrounds/plugin/src/WorldLifecycle.cpp (Proving Grounds by Shutori, GPLv3), task 2.5 (PA §2.20 "Save safety").
// The C++ runs its whole save path inside Kenshi's SaveManager hooks on one thread. In Meitou the save starts on the host's frame thread
// (GameSaves.QuickSaveNow), which must not touch the world, so the port splits it: SaveGate (below) is the frame thread's decision over the
// published SaveGateState; SaveSafety is the rest of WorldLifecycle.cpp's state machine, run by the glue (src/Game/WorldLifecycle.cs) in serial
// steps on the sim thread. Texts are byte for byte the C++.
namespace ProvingGrounds.Core.Glue;

/// <summary>The arena's state and actions the save path reads and calls (the C++ calls these modules directly). The glue implements it over the
/// subsystems for the duration of one serial step.</summary>
public interface ISaveSafetyWorld
{
    /// <summary>C++ ArenaIngress::IsPending.</summary>
    bool IngressPending { get; }
    /// <summary>C++ ArenaIngress::IsWalkInPending.</summary>
    bool WalkInPending { get; }
    /// <summary>C++ SparSession::IsActive.</summary>
    bool MatchActive { get; }
    /// <summary>C++ PrisonerUtil::HasRuntimeActivity.</summary>
    bool PrisonerRuntimeActivity { get; }
    /// <summary>C++ PrisonerUtil::HasPendingCageLocks.</summary>
    bool PendingCageLocks { get; }
    /// <summary>C++ PrisonerUtil::HasCageReturnFailure.</summary>
    bool CageReturnFailure { get; }
    /// <summary>C++ TownArena::IsBusy.</summary>
    bool TownBusy { get; }
    /// <summary>C++ TownArena::HasBooking.</summary>
    bool TownHasBooking { get; }
    /// <summary>C++ TownLimbShop::IsVisitActive.</summary>
    bool LimbShopVisitActive { get; }
    /// <summary>C++ TownLimbShop's <c>g_phase != Idle</c>: when SuspendVisit acts (TownLimbShop.cpp:586-597; wider than IsVisitActive, :581-584).</summary>
    bool LimbShopPhaseActive { get; }
    /// <summary>C++ PrisonerUtil::GetCageReturnStatus.</summary>
    string CageReturnStatus();
    /// <summary>C++ TownLimbShop::SuspendVisit.</summary>
    void LimbShopSuspendVisit();
    /// <summary>C++ TownArena::Cancel.</summary>
    void TownCancel();
    /// <summary>C++ TownArena::CancelForSave.</summary>
    void TownCancelForSave();
    /// <summary>C++ ArenaIngress::Cancel.</summary>
    void IngressCancel();
    /// <summary>C++ SparSession::AbortForSave.</summary>
    void SparAbortForSave();
    /// <summary>C++ PrisonerUtil::ReturnAllToCages(statusOut): returns statusOut.</summary>
    string PrisonersReturnAllToCages();
    /// <summary>C++ ResultsUI::Close.</summary>
    void ResultsClose();
    /// <summary>C++ SparStats::AbandonWorldState.</summary>
    void SparStatsAbandon();
    /// <summary>C++ ArenaIngress::AbandonWorldState.</summary>
    void IngressAbandon();
    /// <summary>C++ SparSession::AbandonWorldState.</summary>
    void SparSessionAbandon();
    /// <summary>C++ PrisonerUtil::AbandonWorldState.</summary>
    void PrisonerAbandon();
    /// <summary>C++ FightStarter::AbandonWorldState.</summary>
    void FightStarterAbandon();
    /// <summary>C++ ArenaUI::AbandonWorldState.</summary>
    void ArenaUiAbandon();
    /// <summary>C++ DebugMenu::AbandonWorldState.</summary>
    void DebugMenuAbandon();
    /// <summary>C++ <c>ou->showPlayerAMessage(text, true)</c>.</summary>
    void Message(string text);
    /// <summary>C++ LogLifecycle -> PGLog::Debug.</summary>
    void Log(string text);
}

/// <summary>A save the system wants run now (the C++ calls <c>SaveManager_save_orig</c>): <see cref="Folder"/> null = the quicksave slot.</summary>
public readonly record struct SaveRequest(bool Requested, string? Folder)
{
    public static readonly SaveRequest None = default;
    public static SaveRequest Save(string? folder) => new(true, folder);
}

/// <summary>The save-safety state of one world (C++ WorldLifecycle.cpp's module globals :59-65 and the functions over them). Sim thread only;
/// one per world (the system is rebuilt for every new game and load, which replaces the C++ AbandonWorldState reset, :255-279).</summary>
public sealed class SaveSafety(ISaveSafetyWorld w)
{
    /// <summary>C++ :111-113.</summary>
    public const string MatchCancelledText = "Proving Grounds match cancelled to safely save the game.";

    bool autosavePauseLogged;          // C++ g_autosavePauseLogged
    bool deferredAutosave;             // C++ g_deferredAutosave
    string? deferredAutosaveName;      // C++ g_deferredAutosaveName (null = the quicksave slot; Meitou's autosave always names its folder)
    readonly LeaderboardSavePolicy.ManualSaveGate manualGate = new();   // C++ g_manualGate
    bool manualSaveDeferred;           // C++ g_manualSaveDeferred
    string? manualSaveName;            // C++ g_manualSaveName

    /// <summary>An autosave is waiting for the arena to be idle.</summary>
    public bool AutosavePostponed => deferredAutosave;

    /// <summary>A manual save is waiting for cage locks (C++ g_manualGate.Waiting()).</summary>
    public bool ManualSaveWaiting => manualGate.Waiting();

    // C++: WorldLifecycle.cpp:62-72
    void FinishManualCleanup()
    {
        w.IngressAbandon();
        w.SparSessionAbandon();
        w.PrisonerAbandon();
        w.FightStarterAbandon();
        w.ArenaUiAbandon();
        w.DebugMenuAbandon();
        manualGate.Abandon();
        w.Log("Proving Grounds: safe-save combat/AI cleanup complete; cage locks verified");
    }

    // C++: WorldLifecycle.cpp:73-79
    void ReportManualCancellation()
    {
        string message = "Manual save CANCELLED: prisoner return was not secured: " +
            w.CageReturnStatus() + ". Check the prisoner/cage. A later manual save can save the current unsecured world.";
        w.Log(message);
        w.Message(message);
    }

    // C++: WorldLifecycle.cpp:80-83
    public bool HasSaveSensitiveActivity()
    {
        return IsArenaOperationBusy() || w.LimbShopVisitActive;
    }

    /// <summary>Not in the C++ (glue): whether <see cref="PrepareForManualSave"/> would do anything but return true, checked against each of
    /// its branches: the gate waits (:88-89), the unsecured-return warning (:90-97), SuspendVisit acts (phase not Idle, TownLimbShop.cpp:588),
    /// a town booking is cancelled (:105) or the arena is busy (:101). When false the frame thread lets the save through at once, which is then
    /// exactly the C++ result.</summary>
    public bool NeedsManualPreparation()
    {
        return manualGate.Waiting() || w.CageReturnFailure || w.LimbShopPhaseActive || w.TownHasBooking || IsArenaOperationBusy();
    }

    /// <summary>Not in the C++ (glue): <see cref="PrepareForManualSave"/> would NOT show <see cref="MatchCancelledText"/> first: it returns at the
    /// waiting gate before any message (:88-89), or it shows the unsecured-return warning before it (:90-97, then :111-113). In both cases the
    /// frame thread leaves the message to the sim so the C++ order is kept.</summary>
    public bool CancelNoticeDeferred()
    {
        return manualGate.Waiting() || w.CageReturnFailure;
    }

    /// <summary>C++: WorldLifecycle.cpp:84-150. <paramref name="announced"/>: the frame thread already showed <see cref="MatchCancelledText"/>
    /// when it refused the save (the port's one difference: the C++ shows it here, :109-114).</summary>
    public bool PrepareForManualSave(bool announced)
    {
        LeaderboardSavePolicy.ManualSaveDecision decision =
            manualGate.Request(w.CageReturnFailure);
        if (decision == LeaderboardSavePolicy.ManualSaveDecision.DeferManualSave) return false;
        if (decision == LeaderboardSavePolicy.ManualSaveDecision.WarnUnsecuredSave)
        {
            string warning = "Saving current world, but prisoner return was NOT secured: " +
                w.CageReturnStatus() + ". Check the prisoner and cage after saving.";
            w.Log(warning);
            w.Message(warning);
        }
        w.LimbShopSuspendVisit();
        if (!IsArenaOperationBusy())
        {
            // Idle reservations do not postpone autosave, but a manual save
            // explicitly clears the session-only player queue. Ambient NPC
            // scheduling is a default runtime policy, not an idle operation.
            if (w.TownHasBooking) w.TownCancel();
            return true;
        }

        w.Log("Proving Grounds: manual save requested during arena operation - safely aborting");
        if (!announced) w.Message(MatchCancelledText);

        // This path runs before Kenshi serialises the world, while actors are
        // still valid. Restore native combat state and cage prisoners first.
        w.TownCancelForSave();
        w.IngressCancel();
        w.SparAbortForSave();

        if (w.PrisonerRuntimeActivity && decision != LeaderboardSavePolicy.ManualSaveDecision.WarnUnsecuredSave)
        {
            string prisonerStatus = w.PrisonersReturnAllToCages();
            if (prisonerStatus.Length != 0)
                w.Log("Proving Grounds: safe-save prisoner cleanup - " + prisonerStatus);
        }

        w.ResultsClose();
        w.SparStatsAbandon();

        // Placement can queue native lock orders. Keep actor/handler references
        // until the normal GUI TickReturn verifies them, even after spar abort.
        if (w.PendingCageLocks)
        {
            manualGate.Defer();
            const string waiting = "Manual save waiting for cage locks. Unpause so the handler can finish locking; a timeout will cancel this save.";
            w.Log(waiting);
            w.Message(waiting);
            return false;
        }
        if (w.CageReturnFailure)
        {
            if (decision == LeaderboardSavePolicy.ManualSaveDecision.WarnUnsecuredSave) return true;
            ReportManualCancellation();
            return false;
        }
        FinishManualCleanup();
        return true;
    }

    /// <summary>C++: WorldLifecycle.cpp:151-180 <c>SaveManager_updateAutoSave_hook</c>, without the native timer (Meitou's own autosave
    /// keeps running: the prefix records its save, as the C++ save hook does). Runs once per tick (only while the game runs, the port's
    /// "!pauseAutoSaveTimer"). Returns the postponed autosave when the arena became idle.</summary>
    public SaveRequest AutosaveTick() => AutosaveTick(HasSaveSensitiveActivity());

    /// <summary><see cref="AutosaveTick()"/> with HasSaveSensitiveActivity already computed this tick (<see cref="GateState"/>).</summary>
    public SaveRequest AutosaveTick(bool saveSensitive)
    {
        bool busy = saveSensitive;
        if (busy)
        {
            if (!autosavePauseLogged)
            {
                w.Log("Proving Grounds: autosave postponed until arena operation completes");
                autosavePauseLogged = true;
            }
        }
        else if (autosavePauseLogged)
        {
            w.Log("Proving Grounds: arena operation complete - autosave hold released");
            autosavePauseLogged = false;
        }

        if (!busy && deferredAutosave)
        {
            string? name = deferredAutosaveName;
            deferredAutosave = false;
            deferredAutosaveName = null;
            w.Log("Proving Grounds: running postponed autosave");
            return SaveRequest.Save(name);
        }
        return SaveRequest.None;
    }

    /// <summary>C++: WorldLifecycle.cpp:182-187, the autosave branch of <c>SaveManager_save_hook</c> (the frame thread already refused it).</summary>
    public void DeferAutosave(string? name)
    {
        deferredAutosave = true;
        deferredAutosaveName = name;
        w.Log("Proving Grounds: queued autosave postponed until arena operation completes");
    }

    /// <summary>C++: WorldLifecycle.cpp:189-201, the manual branch of <c>SaveManager_save_hook</c>: prepare, then save (returned), or remember
    /// the save while the gate waits for cage locks.</summary>
    public SaveRequest ManualSave(string? name, bool announced)
    {
        if (!PrepareForManualSave(announced))
        {
            manualSaveDeferred = manualGate.Waiting();
            manualSaveName = manualSaveDeferred ? name : null;
            return SaveRequest.None;
        }
        return SaveRequest.Save(name);
    }

    /// <summary>Not in the C++ (glue): the state the frame thread decides with, IsArenaOperationBusy computed once.</summary>
    public SaveGateState GateState()
    {
        bool busy = IsArenaOperationBusy();
        bool waiting = manualGate.Waiting(), failure = w.CageReturnFailure;
        return new SaveGateState(busy, busy || w.LimbShopVisitActive,
            waiting || failure || w.LimbShopPhaseActive || w.TownHasBooking || busy, waiting || failure);
    }

    /// <summary>Fail open (glue): <see cref="GateState"/>, or <see cref="SaveGateState.Idle"/> when reading the arena throws (saves then run as
    /// without the mod rather than being refused on a broken read).</summary>
    public SaveGateState GateStateOrIdle(Action<Exception> failed)
    {
        try { return GateState(); }
        catch (Exception e) { failed(e); return SaveGateState.Idle; }
    }

    /// <summary>Fail open (glue): <see cref="ManualSave"/>; when the preparation throws, the save is still made (the player asked for it).</summary>
    public SaveRequest ManualSaveOrSave(string? name, bool announced, Action<Exception> failed)
    {
        try { return ManualSave(name, announced); }
        catch (Exception e) { failed(e); return SaveRequest.Save(name); }
    }

    /// <summary>C++: WorldLifecycle.cpp:235-249</summary>
    public bool IsArenaOperationBusy()
    {
        WorldLifecyclePolicy.ArenaActivity activity = default;
        activity.IngressPending = w.IngressPending;
        activity.WalkInPending = w.WalkInPending;
        activity.MatchActive = w.MatchActive;
        activity.PrisonerActivity = LeaderboardSavePolicy.HoldForPrisoners(
            w.PrisonerRuntimeActivity, w.PendingCageLocks,
            w.CageReturnFailure) || manualGate.Waiting();
        activity.TownActivity = w.TownBusy;
        return WorldLifecyclePolicy.ShouldPostponeAutosave(activity);
    }

    /// <summary>C++: WorldLifecycle.cpp:282-302 <c>Tick</c>, after its first line (ArenaNativePersistence::Tick, called by the glue):
    /// the manual save waiting for cage locks resumes, or is cancelled.</summary>
    public SaveRequest Tick()
    {
        LeaderboardSavePolicy.ManualSaveDecision decision = manualGate.Poll(
            w.PendingCageLocks, w.CageReturnFailure);
        if (decision == LeaderboardSavePolicy.ManualSaveDecision.NoDeferredSave ||
            decision == LeaderboardSavePolicy.ManualSaveDecision.DeferManualSave) return SaveRequest.None;
        if (decision == LeaderboardSavePolicy.ManualSaveDecision.CancelManualSave)
        {
            manualSaveDeferred = false;
            manualSaveName = null;
            ReportManualCancellation();
            return SaveRequest.None;
        }
        bool save = manualSaveDeferred;
        string? name = manualSaveName;
        manualSaveDeferred = false;
        manualSaveName = null;
        FinishManualCleanup();
        return save ? SaveRequest.Save(name) : SaveRequest.None;
    }
}

/// <summary>What the sim thread publishes every tick for the frame thread's save decision (volatile, immutable).</summary>
/// <param name="ArenaBusy">C++ IsArenaOperationBusy: a manual save cancels arena work and shows <see cref="SaveSafety.MatchCancelledText"/>.</param>
/// <param name="SaveSensitive">C++ HasSaveSensitiveActivity: an autosave waits; a re-requested save is held (the C++ execute hook, :203-213).</param>
/// <param name="NeedsManualPreparation">A manual save must go through <see cref="SaveSafety.PrepareForManualSave"/> first.</param>
/// <param name="CancelNoticeDeferred">The match-cancelled text is not the first thing a manual save shows (<see cref="SaveSafety.CancelNoticeDeferred"/>).</param>
public sealed record SaveGateState(bool ArenaBusy, bool SaveSensitive, bool NeedsManualPreparation, bool CancelNoticeDeferred)
{
    public static readonly SaveGateState Idle = new(false, false, false, false);
}

/// <summary>What the QuickSaveNow prefix does with one save.</summary>
public enum SaveGateDecision
{
    /// <summary>Let Meitou save now.</summary>
    Allow,
    /// <summary>An autosave while the arena is busy: refuse it, remember its folder, re-request it when idle (C++ :182-187).</summary>
    DelayAutosave,
    /// <summary>A manual save that needs <see cref="SaveSafety.PrepareForManualSave"/>: refuse it now, prepare in the next serial step, then
    /// re-request it (C++ :189-201, which prepares and saves in one call).</summary>
    PrepareFirst,
    /// <summary>The system's own re-requested save while Meitou is still writing (GameSaves.Busy, GameSaves.cs:130): refuse it silently and
    /// keep it, to try again on a later frame (Meitou would refuse it with "Save or load still in progress", :204-207, and it would be lost;
    /// the C++ SaveManager_save_orig only queues, so a save is never lost there).</summary>
    HoldReRequest,
}

/// <summary>The frame thread's half of the save hook (not in the C++, which decides on one thread): pure, reads only published state.</summary>
public static class SaveGate
{
    /// <summary>Two save folders are the same (Windows paths are case-insensitive; null = Meitou's quicksave slot).</summary>
    public static bool SameFolder(string? a, string? b) =>
        a is null ? b is null : b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a QuickSaveNow destination is one of Meitou's autosave slots (the C++ hook gets <c>autosave</c> as a flag; Meitou's
    /// autosave calls QuickSaveNow(QuickSave.AutosaveFolder(i)), GameSaves.cs:105).</summary>
    public static bool IsAutosaveFolder(string? destination, IReadOnlyList<string> autosaveFolders)
    {
        if (destination is null) return false;
        for (int i = 0; i < autosaveFolders.Count; i++)
            if (SameFolder(destination, autosaveFolders[i])) return true;
        return false;
    }

    /// <summary>The prefix's decision. <paramref name="reRequested"/>: this is the save the system itself re-requested after preparing it
    /// (the C++ calls the original save directly, past its hook); <paramref name="savesBusy"/>: Meitou's GameSaves.Busy() (a write still running).
    /// <paramref name="manualPending"/>: an earlier manual save is still waiting for the sim to prepare it. A feedback-report save
    /// (FeedbackCapture.cs:253) is never refused or delayed.</summary>
    public static SaveGateDecision Decide(bool feedback, bool autosave, bool reRequested, bool savesBusy, bool manualPending, SaveGateState state)
    {
        if (feedback) return SaveGateDecision.Allow;
        if (reRequested) return savesBusy ? SaveGateDecision.HoldReRequest : SaveGateDecision.Allow;
        if (autosave) return state.SaveSensitive ? SaveGateDecision.DelayAutosave : SaveGateDecision.Allow;
        return state.NeedsManualPreparation || manualPending ? SaveGateDecision.PrepareFirst : SaveGateDecision.Allow;
    }

    /// <summary>Whether the prefix shows <see cref="SaveSafety.MatchCancelledText"/> for a refused manual save now: only when the arena is busy,
    /// the C++ shows that text first (not while waiting for cage locks or after the unsecured-return warning), and no earlier manual save is
    /// still queued (a double press shows it once).</summary>
    public static bool Announce(SaveGateState state, bool manualPending) =>
        state.ArenaBusy && !state.CancelNoticeDeferred && !manualPending;
}

/// <summary>The manual saves the frame thread has refused and the sim has not prepared yet, in press order (frame writes, sim takes). A
/// second press for the SAME folder before the sim ran replaces it (one notice, one prepare, one re-request); a different folder is its own
/// save, as each C++ SaveManager::save call prepares and saves (its second prepare finds the arena idle and shows no message). A short lock:
/// only the frame thread and the sim thread use it, never a worker.</summary>
public sealed class PendingManualSave
{
    public sealed record Slot(string? Folder, bool Announced);
    readonly List<Slot> slots = [];

    /// <summary>Any manual save waiting for the sim.</summary>
    public bool Pending { get { lock (slots) return slots.Count > 0; } }

    /// <summary>Frame thread: queue <paramref name="folder"/>; <paramref name="announced"/> = the notice was shown for this press.</summary>
    public void Offer(string? folder, bool announced)
    {
        lock (slots)
        {
            int i = slots.FindIndex(x => SaveGate.SameFolder(x.Folder, folder));
            if (i >= 0) slots[i] = new Slot(folder, announced || slots[i].Announced);
            else slots.Add(new Slot(folder, announced));
        }
    }

    /// <summary>Sim thread: take the oldest waiting save, if any.</summary>
    public Slot? Take()
    {
        lock (slots)
        {
            if (slots.Count == 0) return null;
            var first = slots[0];
            slots.RemoveAt(0);
            return first;
        }
    }
}
