using Meitou.Simulation;
using ProvingGrounds.Core;

namespace ProvingGrounds.Engine;

// Task M2.7: the reads Core/CombatBalanceLog.cs makes on every event line (CombatBalanceLog.cpp:45, 58-62, 67-68), over this world's state.
// Engine calls of the C++ and what replaces them:
//   ou->getTimeStamp_inGameHours().getTotalHours(), 0 without ou (Hours, :45) ... World.GameHours (World.cs:436), 0 before the first serial step
//   LeaderboardStore::GetActiveSaveKey (LeaderboardStore.cpp:669-673, g_ledger.State().saveKey) ... the world ledger's snapshot SaveKey
//       (task 2.4: the fixed ArenaSaveData.InSaveKey once a save or a new game resolved, "" before)
//   TownDiagnosticRun::Get() = LeaderboardStore::GetTownDiagnostic() (CombatBalanceLog.cpp:70, :118) ... the world's diagnostic run
//       (src/Town/TownDiagnosticRun.cs, task 8.7a): the store's saved state, so every line written while an assignment is pending carries the
//       C++ diagnostic fields
// The per-fighter reads (CombatBalanceLog.cpp:90-157) are ProvingGroundsSystem.CombatFighter, over Core/Glue/CombatLogFighter.cs.
// Callers come in M3 (SparSession match start / result / abort) and M7 (town bouts, markets, challenges): sim thread only, like the C++.

/// <summary><see cref="CombatBalanceLog.IHost"/> for one world (sim thread; the system binds the world at each serial step).</summary>
/// <param name="diagnostic">The world's diagnostic run (TownArena's, built lazily: it reads the store, built after this host).</param>
sealed class CombatLogHost(Game.ArenaNativePersistence arena, Func<Town.TownDiagnosticRun> diagnostic) : CombatBalanceLog.IHost
{
    World? world;

    /// <summary>The world of this serial step (the system's Inputs and SlowWorld).</summary>
    internal void Bind(World w) => world = w;

    // C++: CombatBalanceLog.cpp:45
    public double GameHours() => world?.GameHours ?? 0;

    // C++: LeaderboardStore.cpp:669-673
    public string ActiveSaveKey() => arena.Ledger.State().SaveKey ?? "";

    // C++: TownDiagnosticRun.cpp:40 (TownDiagnosticRun::Get, read only: a copy)
    public TownDiagnosticData.State Diagnostic() => diagnostic().Get();
}
