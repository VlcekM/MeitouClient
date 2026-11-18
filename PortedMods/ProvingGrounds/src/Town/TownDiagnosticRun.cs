using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// Port of the glue half of upstream/ProvingGrounds/plugin/src/TownDiagnosticRun.h/.cpp (Proving Grounds by Shutori, GPLv3), task 8.7a (TW §2.17):
// the controlled matchmaking diagnostic run. Every decision is Core/TownDiagnosticRun.cs (M1, tested in Tests/Core/TownDiagnosticRunTests.cs);
// this class is the C++ file's calls into LeaderboardStore, TownArena and CombatBalanceLog, and one member per entry point of TownDiagnosticRun.h,
// each delegating to Core over the store's state. The run keeps no state of its own: the state is LeaderboardStore::GetTownDiagnostic()
// (TownDiagnosticRun.cpp:40), the ledger's, saved with the snapshot as the C++ (no new save field). One object per world, TownArena's
// (src/Town/TownArena.Diagnostic.cs). Core's class of this name is Core.TownDiagnosticRun; never use the bare name in this namespace for it.
// Sim thread only: the serial steps (Inputs, SlowWorld) and the load path's applied step (ArenaWorldState: inside Resolve, or a save's Write
// between ticks on the thread that ticks the world).
//
// Calls of the C++ file and what replaces them (E1):
//   LeaderboardStore::GetTownDiagnostic (:40, every entry point's Get()) ... LeaderboardStore.GetTownDiagnostic (the ledger's state while Ready,
//       else a scratch default whose writes are lost, LeaderboardStore.cpp:91-97)
//   LeaderboardStore::HasActiveSave (:44) .......... LeaderboardStore.HasActiveSave
//   TownArena::IsBusy / HasBooking (:45) ........... TownArena.IsBusy / HasBooking (this world's TownArena)
//   TownArena::HasPlannedNpcBout (:57, :59) ........ TownArena.HasPlannedNpcBout
//   TownArena::CancelPlannedNpcBout(reason) (:58) .. TownArena.CancelPlannedNpcBout(world, reason) (it cancels this run's pending assignment
//       through TownArena's host, as the C++ :633)
//   LeaderboardStore::ResetTownStandings (:62) ..... LeaderboardStore.ResetTownStandings (a "fresh" run)
//   LeaderboardStore::ClearDiagnosticError (:64) ... LeaderboardStore.ClearDiagnosticError
//   GetTickCount() (:65, the run id) ............... PgClock.NowMs (overview §4.2); read when StartConfigured is called, the same value as at
//       :65 (the clock moves only between serial steps)
//   CombatBalanceLog::DiagnosticEvent(name, fields) (:68, :77, :78, :79, :117, :134) ... CombatBalanceLog.DiagnosticSink over the world's
//       CombatLogHost, built once: the line's record, game hours and save key are read when it is written, from the host the system binds to
//       each serial step's world. The load path's recover gets its sink from ArenaWorldState (RecoverPendingAfterLoad's parameter)
//   LeaderboardStore::GetDiagnosticError (:139) .... LeaderboardStore.GetDiagnosticError
//   sprintf_s (the event fields, StatusText) ....... Core (CMath.FormatF for the floats)
//
// Callers (C++ -> port): TownArena.cpp:456 RetryPending (NpcPreparation::Cancel), :633 CancelPending (CancelPlannedNpcBout), :660 NextRequest and
// :686 Assign (PlanNpcBout), :1096 CompletePending (OnMatchResult), :1142 AbortPending (OnMatchAborted), :1806 CancelPending (AbandonWorldState)
// -> TownArena's host (src/Town/TownArena.Bouts.cs, src/Town/TownArena.cs); DebugMenu.cpp:300-301 Get / StatusText and :480-507 the actions and
// ValidationError -> DebugMenuSim's host (src/Game/DebugMenuSim.cs); CombatBalanceLog.cpp:70, :118 Get -> src/Engine/CombatLogHost.cs;
// ArenaNativePersistence.cpp:363 RecoverPendingAfterLoad -> the load path (Core/Glue/ArenaWorldState.cs, wired by ProvingGroundsSystem). IsActive
// (:41) has no caller in the C++ either.

/// <summary>The controlled diagnostic run of one world (C++ namespace TownDiagnosticRun): every caller goes through this object. Its
/// <see cref="Core.TownDiagnosticRun.IHost"/> half is the C++'s LeaderboardStore and TownArena calls of StartConfigured and ValidationError,
/// bound to the world of the call.</summary>
sealed class TownDiagnosticRun : Core.TownDiagnosticRun.IHost
{
    readonly Func<LeaderboardStore> store;
    readonly PgClock clock;
    readonly Func<CombatBalanceLog.IHost> combatLog;
    readonly TownArena arena;
    Action<string, string>? sink;
    World? world;

    /// <param name="store">The world's store (read lazily: ProvingGroundsSystem builds it after TownArena).</param>
    /// <param name="combatLog">The world's combat log host (read lazily, at the first event).</param>
    internal TownDiagnosticRun(Func<LeaderboardStore> store, PgClock clock, Func<CombatBalanceLog.IHost> combatLog, TownArena arena)
    {
        this.store = store;
        this.clock = clock;
        this.combatLog = combatLog;
        this.arena = arena;
    }

    /// <summary>C++ <c>CombatBalanceLog::DiagnosticEvent</c> as Core's event sink, built once (no allocation per call).</summary>
    Action<string, string> Sink => sink ??= CombatBalanceLog.DiagnosticSink(combatLog());

    /// <summary>The host half bound to the world of this call (the C++'s TownArena calls need it).</summary>
    Core.TownDiagnosticRun.IHost Bound(World w)
    {
        world = w;
        return this;
    }

    World W => world ?? throw new InvalidOperationException("TownDiagnosticRun's host used before a world was bound");

    // C++: TownDiagnosticRun.cpp:40
    internal ref TownDiagnosticData.State Get() => ref store().GetTownDiagnostic();

    // C++: TownDiagnosticRun.cpp:41 (no caller in the C++)
    internal bool IsActive() => Core.TownDiagnosticRun.IsActive(Get());

    // C++: TownDiagnosticRun.cpp:42-50
    internal string ValidationError(World world) => Core.TownDiagnosticRun.ValidationError(Get(), Bound(world));

    // C++: TownDiagnosticRun.cpp:51-70
    internal bool StartConfigured(World world, bool resetTownRecords) =>
        Core.TownDiagnosticRun.StartConfigured(ref Get(), resetTownRecords, clock.NowMs, Bound(world), Sink);

    // C++: TownDiagnosticRun.cpp:71-76
    internal bool StartPreset(World world, bool resetTownRecords) =>
        Core.TownDiagnosticRun.StartPreset(ref Get(), resetTownRecords, clock.NowMs, Bound(world), Sink);

    // C++: TownDiagnosticRun.cpp:77
    internal bool Pause() => Core.TownDiagnosticRun.Pause(ref Get(), Sink);

    // C++: TownDiagnosticRun.cpp:78
    internal bool Resume() => Core.TownDiagnosticRun.Resume(ref Get(), Sink);

    // C++: TownDiagnosticRun.cpp:79
    internal bool End() => Core.TownDiagnosticRun.End(ref Get(), Sink);

    // C++: TownDiagnosticRun.cpp:80
    internal bool SetTarget(int target) => Core.TownDiagnosticRun.SetTarget(ref Get(), target);

    // C++: TownDiagnosticRun.cpp:81
    internal bool ToggleModel(int model) => Core.TownDiagnosticRun.ToggleModel(ref Get(), model);

    // C++: TownDiagnosticRun.cpp:82
    internal bool ToggleCoverage(int coverage) => Core.TownDiagnosticRun.ToggleCoverage(ref Get(), coverage);

    // C++: TownDiagnosticRun.cpp:83
    internal bool CycleReducedInfluence() => Core.TownDiagnosticRun.CycleReducedInfluence(ref Get());

    // C++: TownDiagnosticRun.cpp:84
    internal bool CycleUnderdogPercent() => Core.TownDiagnosticRun.CycleUnderdogPercent(ref Get());

    // C++: TownDiagnosticRun.cpp:85
    internal bool ToggleStyleTierBalance() => Core.TownDiagnosticRun.ToggleStyleTierBalance(ref Get());

    // C++: TownDiagnosticRun.cpp:86-98
    internal TownMatchmakingPolicy.DiagnosticRequest NextRequest() => Core.TownDiagnosticRun.NextRequest(Get());

    // C++: TownDiagnosticRun.cpp:99-106
    internal void Assign(TownMatchmakingPolicy.Match match, double hours) => Core.TownDiagnosticRun.Assign(ref Get(), match, hours);

    // C++: TownDiagnosticRun.cpp:107
    internal void CancelPending() => Core.TownDiagnosticRun.CancelPending(ref Get());

    // C++: TownDiagnosticRun.cpp:108-112
    internal void RetryPending() => Core.TownDiagnosticRun.RetryPending(ref Get());

    /// <summary>The load path's call (ArenaWorldState.RecoverDiagnosticAfterLoad, its only caller): <paramref name="eventSink"/> is the load's
    /// sink for the "diagnostic_assignment_cancelled" event (written now inside the world's first serial step, else held for it); the load path
    /// owns it, so it is required.</summary>
    // C++: TownDiagnosticRun.cpp:113-118
    internal void RecoverPendingAfterLoad(Action<string, string> eventSink) =>
        Core.TownDiagnosticRun.RecoverPendingAfterLoad(ref Get(), eventSink);

    // C++: TownDiagnosticRun.cpp:119
    internal void AbortPending() => Core.TownDiagnosticRun.AbortPending(ref Get());

    // C++: TownDiagnosticRun.cpp:120-136
    internal void CompletePending(SparPodium.Snapshot result, double hours) =>
        Core.TownDiagnosticRun.CompletePending(ref Get(), result, hours, Sink);

    // C++: TownDiagnosticRun.cpp:137-145
    internal string StatusText() => Core.TownDiagnosticRun.StatusText(Get(), store().GetDiagnosticError());

    // ---------- Core.TownDiagnosticRun.IHost: the C++'s LeaderboardStore and TownArena calls (:44-45, :57-58, :62, :64) ----------
    bool Core.TownDiagnosticRun.IHost.HasActiveSave() => store().HasActiveSave();
    bool Core.TownDiagnosticRun.IHost.IsBusy() => arena.IsBusy(W);
    bool Core.TownDiagnosticRun.IHost.HasBooking() => arena.HasBooking(W);
    bool Core.TownDiagnosticRun.IHost.HasPlannedNpcBout() => arena.HasPlannedNpcBout();
    void Core.TownDiagnosticRun.IHost.CancelPlannedNpcBout(string reason) => arena.CancelPlannedNpcBout(W, reason);
    bool Core.TownDiagnosticRun.IHost.ResetTownStandings() => store().ResetTownStandings();
    void Core.TownDiagnosticRun.IHost.ClearDiagnosticError() => store().ClearDiagnosticError();
}
