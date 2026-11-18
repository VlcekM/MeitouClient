// Port of the queue and generation rule of upstream/ProvingGrounds/plugin/src/TownMatchmakingSearch.h/.cpp
// (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++: TownMatchmakingSearch.h/.cpp (TW §2.4 "Search worker"): per-card SelectPlayer requests of one "generation", searched
/// off the game thread. Only the queue rules are here; the worker thread, its events, its lock (<c>CRITICAL_SECTION g_lock</c>) and
/// <c>EnsureStarted</c> / <c>Shutdown</c> stay in the glue (M7 task 7.2).
/// <para>The C++ file-scope globals (<c>g_generation</c>, <c>g_requests</c>, <c>g_results</c>, TownMatchmakingSearch.cpp:14-16) are one
/// <see cref="Queue"/> object, which the glue owns and guards with its lock exactly where the C++ enters <c>g_lock</c>: one lock
/// around <see cref="Queue.BeginGeneration"/>, <see cref="Queue.Enqueue"/>, <see cref="Queue.Poll"/>, <see cref="Queue.TryTake"/>
/// and <see cref="Queue.Complete"/> each, and the search itself (<c>SelectPlayer</c>) outside it, between TryTake and Complete, as
/// the worker does (TownMatchmakingSearch.cpp:30-67). That is what lets a result go stale: a BeginGeneration between the two drops it.
/// <see cref="Queue.ProcessOne"/> runs the three steps in a row for single-threaded use and tests.</para>
/// <para><see cref="Request"/> and <see cref="Result"/> are classes copied with <c>Clone</c> wherever the C++ copies them by value
/// (<c>push_back(request)</c>, <c>request = g_requests.front()</c>, <c>result = g_results.front()</c>).</para></summary>
public static class TownMatchmakingSearch
{
    /// <summary>C++: TownMatchmakingSearch.h:6-25</summary>
    public sealed class Request
    {
        public uint Generation;
        public int Slot;
        public List<TownMatchmakingPolicy.Fighter> Players = new List<TownMatchmakingPolicy.Fighter>();
        public List<TownMatchmakingPolicy.Fighter> Npcs = new List<TownMatchmakingPolicy.Fighter>();
        public TownMatchmakingPolicy.Division Division;
        public uint Seed;
        public List<List<string>> History = new List<List<string>>();
        public int MaxEnemies;
        public int MaxTotal;
        public string RequiredRole = "";
        public int Rarity;
        public bool ForceSoloNamed;
        public bool Teams1v1;
        public PGConfig.ChallengeDifficulty ChallengeDifficulty;

        // C++: TownMatchmakingSearch.h:22-24
        public Request()
        {
            Generation = 0; Slot = -1; Division = TownMatchmakingPolicy.Division.Easy;
            Seed = 0; MaxEnemies = 4; MaxTotal = 6; Rarity = -1; ForceSoloNamed = false; Teams1v1 = false;
            ChallengeDifficulty = PGConfig.ChallengeDifficulty.Normal;
        }

        /// <summary>The C++ by-value copy: the fighter lists (fighters are structs) and every history entry copied.</summary>
        public Request Clone() => new Request
        {
            Generation = Generation, Slot = Slot,
            Players = new List<TownMatchmakingPolicy.Fighter>(Players), Npcs = new List<TownMatchmakingPolicy.Fighter>(Npcs),
            Division = Division, Seed = Seed, History = History.Select(h => new List<string>(h)).ToList(),
            MaxEnemies = MaxEnemies, MaxTotal = MaxTotal, RequiredRole = RequiredRole, Rarity = Rarity,
            ForceSoloNamed = ForceSoloNamed, Teams1v1 = Teams1v1, ChallengeDifficulty = ChallengeDifficulty,
        };
    }

    /// <summary>C++: TownMatchmakingSearch.h:27-33</summary>
    public sealed class Result
    {
        public uint Generation;
        public int Slot;
        public TownMatchmakingPolicy.Match Match = new TownMatchmakingPolicy.Match();

        // C++: TownMatchmakingSearch.h:32
        public Result() { Generation = 0; Slot = -1; }

        public Result Clone() => new Result { Generation = Generation, Slot = Slot, Match = Match.Clone() };
    }

    /// <summary>The worker's search call: SelectPlayer with the request's fields in the C++ argument order.</summary>
    // C++: TownMatchmakingSearch.cpp:45-49
    public static TownMatchmakingPolicy.Match Search(Request request) =>
        TownMatchmakingPolicy.SelectPlayer(
            request.Players, request.Npcs, request.Division, request.Seed,
            request.History, request.MaxEnemies, request.MaxTotal,
            request.RequiredRole, request.Rarity, request.ForceSoloNamed,
            request.Teams1v1, request.ChallengeDifficulty);

    /// <summary>The queue state of TownMatchmakingSearch.cpp:14-16 and its rules. Not thread-safe by itself (see the class summary).</summary>
    public sealed class Queue
    {
        // C++: TownMatchmakingSearch.cpp:14-16
        uint _generation = 0;
        readonly LinkedList<Request> _requests = new LinkedList<Request>();
        readonly LinkedList<Result> _results = new LinkedList<Result>();

        /// <summary>The current generation (<c>g_generation</c>).</summary>
        public uint Generation => _generation;
        /// <summary>Requests still queued, front first (copies).</summary>
        public IReadOnlyList<Request> PendingRequests() => _requests.Select(r => r.Clone()).ToList();
        public int PendingResultCount => _results.Count;

        /// <summary>Replaces all queued work. A currently running request may finish, but its stale result is discarded before it
        /// reaches the game thread. The C++ first starts the worker and does nothing when that fails (glue).</summary>
        // C++: TownMatchmakingSearch.cpp:107-115
        public void BeginGeneration(uint generation)
        {
            _generation = generation;
            _requests.Clear();
            _results.Clear();
        }

        /// <summary>Queues a copy of <paramref name="request"/> when it belongs to the current generation. The glue sets the work
        /// event when this returns true, and returns false itself when the worker cannot be started.</summary>
        // C++: TownMatchmakingSearch.cpp:117-126
        public bool Enqueue(Request request)
        {
            bool current = request.Generation == _generation;
            if (current) _requests.AddLast(request.Clone());
            return current;
        }

        /// <summary>The front result, removed. The returned <see cref="Result"/> belongs to the caller: the queue keeps no reference
        /// to it (it stored its own copy in <see cref="Complete"/>), so no clone is needed. The C++ returns false while the worker was
        /// never started (glue).</summary>
        // C++: TownMatchmakingSearch.cpp:128-140
        public bool Poll(out Result? result)
        {
            bool found = _results.Count != 0;
            result = null;
            if (found)
            {
                result = _results.First!.Value;
                _results.RemoveFirst();
            }
            return found;
        }

        /// <summary>The worker taking its next request (a copy of the front, removed).</summary>
        // C++: TownMatchmakingSearch.cpp:30-40
        public bool TryTake(out Request? request)
        {
            request = null;
            bool found = false;
            if (_requests.Count != 0)
            {
                request = _requests.First!.Value;
                _requests.RemoveFirst();
                found = true;
            }
            return found;
        }

        /// <summary>The worker storing a finished search: kept only when its generation is still current; a valid match then
        /// adds its opponents to every other queued request's history of that generation and takes them out of that request's
        /// NPC pool, so the cards do not share opponents. <paramref name="request"/> must be the one <see cref="TryTake"/> handed out
        /// (its generation and slot label the result; it is no longer in the queue, so it is not changed itself).</summary>
        // C++: TownMatchmakingSearch.cpp:42-67 (the result built at :42-44, the locked part at :51-67)
        public void Complete(Request request, TownMatchmakingPolicy.Match match)
        {
            var result = new Result();
            result.Generation = request.Generation;
            result.Slot = request.Slot;
            result.Match = match;
            if (request.Generation == _generation)
            {
                _results.AddLast(result.Clone());
                if (result.Match.Valid)
                    foreach (Request queued in _requests)
                        if (queued.Generation == request.Generation)
                        {
                            TownMatchmakingPolicy.RecordHistory(queued.History, result.Match.B);
                            List<TownMatchmakingPolicy.Fighter> roster = queued.Npcs;
                            for (int n = roster.Count; n > 0; --n)
                                if (result.Match.B.Contains(roster[n - 1].Id))
                                    roster.RemoveAt(n - 1);
                        }
            }
        }

        /// <summary>One pass of the worker's inner loop: take, search (<paramref name="select"/>, default <see cref="Search"/>),
        /// complete. False when nothing was queued.</summary>
        // C++: TownMatchmakingSearch.cpp:27-68
        public bool ProcessOne(Func<Request, TownMatchmakingPolicy.Match>? select = null)
        {
            if (!TryTake(out Request? request)) return false;
            TownMatchmakingPolicy.Match match = (select ?? Search)(request!);
            Complete(request!, match);
            return true;
        }
    }
}
