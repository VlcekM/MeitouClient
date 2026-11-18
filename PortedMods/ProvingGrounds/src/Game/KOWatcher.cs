using Meitou.Simulation;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/KOWatcher.h/.cpp (Proving Grounds by Shutori, GPLv3), task 3.3 (PA §2.9, CAP 3). The decisions, in the
// C++ order with line ranges, are Core/Glue/KOWatcherLogic.cs (tested with a fake, Tests/Glue/KOWatcherLogicTests.cs); this file binds the world.
// Engine calls of the C++ and what replaces them (E1):
//   GetTickCount() (AdvanceClock :36-43) ... PgClock.NowMs (sim ms; FightKeepAliveCadence does the unsigned dt and the 1 s cap)
//   c->isValid(), isDead(), isUnconcious() (:99, :108-109, :120, :216, :254) ... PgEngine.Exists / IsDead (Medical.Dead) / IsUnconscious
//       (Medical.Unconscious: playing dead counts, crawling does not, CAP 3), through PgSparHost
//   c->getName() (CharName :58-63) ... PgEngine.Name
//   SparSession::*, SparStats::OnEliminated, FightStarter::RetargetMatchAfterElimination / KeepEngaged / KeepEngagedMatch ... through the
//       session's host (src/Game/PgSparHost.cs)
// Caller: ArenaUI.cpp:3159 in ForgottenGUI_update_hook = the frame loop's KOWatcher.Tick slot (ProvingGroundsSystem.SlowWorld, serial).

/// <summary>The glue of the original KOWatcher.cpp: the per-frame KO scan of the running match. One object per world.</summary>
sealed class KOWatcher(SparSession spar, PgClock clock)
{
    readonly KOWatcherLogic<CharacterId> logic = new();

    // C++: KOWatcher.cpp:68-269 (ArenaUI.cpp:3159). Port-only around it (the bench NPCs' Engaged is re-asserted by the world's hold registry's one
    // pass in TownArena.Tick, task 8.4a), a scan that throws 3 times in a row stops the match Invalid (KOWatcherLogic.TickGuarded; the throw still reaches the system's failure
    // log), held NPCs released once no match runs, and the PG-SPAR test lines (status change, end).
    internal void Tick(World world)
    {
        var host = spar.HostFor(world);
        try { logic.TickGuarded(host, spar.Logic, clock.NowMs); }
        finally
        {
            if (!spar.Logic.IsActive) host.ReleaseAllHeld();
            if (logic.Scanned) spar.NoteScan(world.Tick);   // port only (task 3.5): the scan ticks of the PG-SPAR lines
            spar.LogTransition(world);
        }
    }
}
