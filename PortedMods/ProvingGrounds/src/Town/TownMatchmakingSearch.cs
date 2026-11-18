using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Town;

// Port of upstream/ProvingGrounds/plugin/src/TownMatchmakingSearch.h/.cpp (Proving Grounds by Shutori, GPLv3), task 7.2 (TW §2.4 "Search worker").
// The queue and generation rule are Core/TownMatchmakingSearch.cs (task 1.10); the thread, its events and its lock are
// Core/Glue/TownMatchmakingWorker.cs (fake-tested, Tests/Glue/TownMatchmakingWorkerTests.cs). This is the world's one search: TownArena owns it and
// reaches it through ITownHost (MatchmakingBeginGeneration / Enqueue / Poll / Shutdown); TownArena.AbandonWorldState shuts it down first when the
// next world takes over (TownArena.cpp:1799), so no worker outlives its world, and the process-exit handler stops it at the end of the game.
//
// The worker reads only the copied requests (fighter ids, roles, abilities, ratings: snapshot data built on the sim thread), never World.
// What the C++ calls and what replaces it: _beginthreadex / CreateEvent / CRITICAL_SECTION ... Thread / AutoResetEvent / ManualResetEvent / a lock
// (TownMatchmakingWorker); PGLog has no line in this file of the C++; the port's lines (a failed start, a search that threw) go to PGLog.Debug.

/// <summary>C++ <c>namespace TownMatchmakingSearch</c> for one world. Every member is called on the sim thread.</summary>
sealed class TownMatchmakingSearch
{
    readonly TownMatchmakingWorker worker = new(PGLog.Debug);

    // C++: TownMatchmakingSearch.cpp:107-115
    internal void BeginGeneration(uint generation) => worker.BeginGeneration(generation);
    // C++: TownMatchmakingSearch.cpp:117-126
    internal bool Enqueue(Core.TownMatchmakingSearch.Request request) => worker.Enqueue(request);
    // C++: TownMatchmakingSearch.cpp:128-140
    internal bool Poll(out Core.TownMatchmakingSearch.Result? result) => worker.Poll(out result);
    // C++: TownMatchmakingSearch.cpp:142-158
    internal void Shutdown() => worker.Shutdown();

    /// <summary>Port only (the test-only <c>pg town</c>): the worker's state.</summary>
    internal bool Running => worker.Running;
    internal uint Generation => worker.Generation;
    internal int PendingRequests => worker.PendingRequests;
    internal int PendingResults => worker.PendingResults;
    internal int Searches => worker.Searches;
    internal int Failures => worker.Failures;
}
