namespace ProvingGrounds.Core.Glue;

// Port of the thread half of upstream/ProvingGrounds/plugin/src/TownMatchmakingSearch.cpp (Proving Grounds by Shutori, GPLv3), task 7.2 (TW §2.4
// "Search worker"): the one background thread of the mod. Core.TownMatchmakingSearch.Queue holds the queue and generation rules (task 1.10); this
// class is the C++'s file-scope thread, events and lock around it, so it can be fake-tested (Tests/Glue/TownMatchmakingWorkerTests.cs: a fake search
// function, real threads). src/Town/TownMatchmakingSearch.cs owns one per world (TownArena) and logs through it.
//
// C++ -> port:
//   CRITICAL_SECTION g_lock (InitializeCriticalSection once) ... a private lock object, entered exactly where the C++ enters g_lock
//   HANDLE g_work = CreateEvent(auto reset) / g_stop = CreateEvent(manual reset) ... AutoResetEvent / ManualResetEvent
//   _beginthreadex(SearchThread) + SetThreadPriority(BELOW_NORMAL) ... new Thread(SearchThread) { Priority = BelowNormal, IsBackground = true }
//   WaitForMultipleObjects({stop, work}) ... WaitHandle.WaitAny (index 0 = stop wins, as WAIT_OBJECT_0)
//   WaitForSingleObject(g_thread, INFINITE) + CloseHandle ... Thread.Join + Dispose of the events
//   TownMatchmakingPolicy::SelectPlayer on the request (outside the lock) ... the search function (default Core.TownMatchmakingSearch.Search)
// Port only: (1) the thread is a background thread and a process-exit handler shuts it down, so it never outlives the game (the C++ thread dies with
// the process); (2) a search that throws is completed with an invalid match and reported once through the log callback (an exception on a .NET
// thread would end the game process; the C++ search cannot throw); (3) the worker is per world (TownArena owns it; the C++ one is per process and
// started again on the next search after Shutdown, which this also does). docs/divergences.md "Matchmaking snapshot and search worker".

/// <summary>C++: TownMatchmakingSearch.cpp (the thread and its lock). Every public member is safe to call from the sim thread; the worker thread runs
/// only the search on its own copy of a request (Enqueue copies, the queue hands out its stored copy), never anything of the world.</summary>
public sealed class TownMatchmakingWorker
{
    readonly object _lock = new();   // g_lock
    readonly TownMatchmakingSearch.Queue _queue = new();   // g_generation, g_requests, g_results
    readonly Func<TownMatchmakingSearch.Request, TownMatchmakingPolicy.Match> _search;
    readonly Action<string> _log;
    Thread? _thread;                 // g_thread
    AutoResetEvent? _work;           // g_work
    ManualResetEvent? _stop;         // g_stop
    EventHandler? _exitHandler;
    int _searches, _failures;
    bool _failureLogged;

    /// <summary><paramref name="search"/> = <see cref="TownMatchmakingSearch.Search"/> (SelectPlayer) unless a test gives another;
    /// <paramref name="log"/> receives the port-only lines (a failed start, a search that threw).</summary>
    public TownMatchmakingWorker(Action<string> log, Func<TownMatchmakingSearch.Request, TownMatchmakingPolicy.Match>? search = null)
    {
        _log = log;
        _search = search ?? TownMatchmakingSearch.Search;
    }

    /// <summary>Port only (tests, the test-only <c>pg town</c>): the worker thread exists and runs.</summary>
    public bool Running { get { lock (_lock) return _thread is { IsAlive: true }; } }
    /// <summary>Port only: the worker thread itself (tests check its priority and that it ended).</summary>
    public Thread? ThreadForTest { get { lock (_lock) return _thread; } }
    /// <summary>Port only: searches finished and searches that threw, since this object was made.</summary>
    public int Searches => Volatile.Read(ref _searches);
    public int Failures => Volatile.Read(ref _failures);
    /// <summary>Port only: the current generation and the queued work (one lock each).</summary>
    public uint Generation { get { lock (_lock) return _queue.Generation; } }
    public int PendingRequests { get { lock (_lock) return _queue.PendingRequests().Count; } }
    public int PendingResults { get { lock (_lock) return _queue.PendingResultCount; } }

    // C++: TownMatchmakingSearch.cpp:18-70
    void SearchThread(object? state)
    {
        try { SearchLoop(state); }
        catch (Exception e)
        {
            // port only: anything else that throws on this thread (the queue, the log, a wait on a disposed handle) would end the game process.
            // The thread ends instead, as a dead C++ thread: Enqueue keeps accepting, no result comes. Logged once (a throwing log is swallowed).
            Interlocked.Increment(ref _failures);
            try { _log("Proving Grounds: background matchmaking stopped (logged once): " + e.GetType().Name + ": " + e.Message); }
            catch (Exception) { }
        }
    }

    // C++: TownMatchmakingSearch.cpp:18-70 (the loop)
    void SearchLoop(object? state)
    {
        var (stop, work) = ((ManualResetEvent, AutoResetEvent))state!;
        WaitHandle[] events = [stop, work];
        for (; ; )
        {
            int wait = WaitHandle.WaitAny(events);
            if (wait == 0) return;
            if (wait != 1) return;

            for (; ; )
            {
                if (stop.WaitOne(0)) return;
                TownMatchmakingSearch.Request? request;
                bool found;
                lock (_lock) found = _queue.TryTake(out request);
                if (!found) break;

                TownMatchmakingPolicy.Match? match;
                try { match = _search(request!); }
                catch (Exception e)
                {
                    // port only: never let an exception end the game process; the slot reads as an unanswered search
                    match = new TownMatchmakingPolicy.Match();
                    Interlocked.Increment(ref _failures);
                    if (!_failureLogged) { _failureLogged = true; _log("Proving Grounds: challenge search failed (logged once): " + e.GetType().Name + ": " + e.Message); }
                }
                match ??= new TownMatchmakingPolicy.Match();   // port only: a search answering null is an unanswered search

                lock (_lock)
                {
                    _queue.Complete(request!, match);
                    Interlocked.Increment(ref _searches);   // inside the lock, right after Complete: a test that sees it sees the result stored or dropped
                }
            }
        }
    }

    // C++: TownMatchmakingSearch.cpp:72-104 (the lock is created with the object; called with _lock held)
    bool EnsureStarted()
    {
        if (_thread != null) return true;
        AutoResetEvent? work = null;
        ManualResetEvent? stop = null;
        try
        {
            work = new AutoResetEvent(false);
            stop = new ManualResetEvent(false);
            var thread = new Thread(SearchThread) { Name = "Proving Grounds matchmaking", IsBackground = true, Priority = ThreadPriority.BelowNormal };
            thread.Start((stop, work));
            _thread = thread;
            _work = work;
            _stop = stop;
        }
        catch (Exception e)
        {
            work?.Dispose();
            stop?.Dispose();
            _log("Proving Grounds: background matchmaking could not start: " + e.GetType().Name + ": " + e.Message);
            return false;
        }
        // port only: the thread never outlives the process (the C++ thread dies with it). An Enqueue racing the exit handler can start a new
        // background thread after it ran: harmless, a background thread ends with the process.
        _exitHandler ??= (_, _) => Shutdown();
        AppDomain.CurrentDomain.ProcessExit += _exitHandler;
        return true;
    }

    /// <summary>Replaces all queued work. A currently running request may finish, but its stale result is discarded before it reaches the game
    /// thread.</summary>
    // C++: TownMatchmakingSearch.cpp:107-115
    public void BeginGeneration(uint generation)
    {
        lock (_lock)
        {
            if (!EnsureStarted()) return;
            _queue.BeginGeneration(generation);
        }
    }

    /// <summary>Queues a copy of the request when it belongs to the current generation and wakes the worker; false when it does not, or the
    /// worker cannot be started.</summary>
    // C++: TownMatchmakingSearch.cpp:117-126
    public bool Enqueue(TownMatchmakingSearch.Request request)
    {
        bool current;
        lock (_lock)
        {
            if (!EnsureStarted()) return false;
            current = _queue.Enqueue(request);
            // set inside the lock: Shutdown detaches the events under this lock before it disposes them, so this one is still live
            if (current) _work?.Set();
        }
        return current;
    }

    /// <summary>The oldest finished result (it is the caller's); false when there is none, or the worker never started.</summary>
    // C++: TownMatchmakingSearch.cpp:128-140
    public bool Poll(out TownMatchmakingSearch.Result? result)
    {
        result = null;
        lock (_lock)
        {
            if (_thread == null) return false;
            return _queue.Poll(out result);
        }
    }

    /// <summary>Stops and joins the worker (a search under way finishes first) and drops all queued work; the next call that needs the worker
    /// starts a new one. Nothing when it never started.</summary>
    // C++: TownMatchmakingSearch.cpp:142-158
    public void Shutdown()
    {
        Thread? thread;
        ManualResetEvent? stop;
        AutoResetEvent? work;
        lock (_lock)
        {
            // taken under the lock, so a second Shutdown (the process-exit handler racing the sim thread) finds nothing to stop
            thread = _thread;
            if (thread == null) return;
            stop = _stop;
            work = _work;
            _thread = null;
            _work = null;
            _stop = null;
            if (_exitHandler != null) AppDomain.CurrentDomain.ProcessExit -= _exitHandler;
        }
        stop!.Set();
        if (Thread.CurrentThread != thread) thread.Join();
        lock (_lock)
        {
            // after the join, as the C++ (TownMatchmakingSearch.cpp:153-156): a search the thread finished meanwhile is dropped too;
            // BeginGeneration with the current generation clears both lists and keeps g_generation
            _queue.BeginGeneration(_queue.Generation);
        }
        work!.Dispose();
        stop.Dispose();
    }
}
