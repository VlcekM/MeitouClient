namespace Meitou.Simulation.Hosting;

/// <summary>
/// The thread ticks run ahead on (one per host, idle between batches).
/// </summary>
sealed class TickRunner : IDisposable
{
    /// <summary>
    /// The runner's thread.
    /// </summary>
    readonly Thread _thread;
    /// <summary>
    /// Released to start a batch, and when the batch is done.
    /// </summary>
    readonly SemaphoreSlim _start = new(0), _done = new(0);
    /// <summary>
    /// The world the batch ticks; null between batches.
    /// </summary>
    World? _world;
    /// <summary>
    /// Each tick's game hour and daytime, the clock's as the ticks would run in the frame.
    /// </summary>
    (double Hours, bool Daytime)[] _starts = [];
    /// <summary>
    /// The batch's wall time in ms.
    /// </summary>
    double _ms;
    /// <summary>
    /// What the batch threw, rethrown by <see cref="Wait"/>.
    /// </summary>
    System.Runtime.ExceptionServices.ExceptionDispatchInfo? _failure;
    /// <summary>
    /// Set by <see cref="Dispose"/> to end the thread.
    /// </summary>
    volatile bool _stopping;

    /// <summary>
    /// Starts the runner's background thread.
    /// </summary>
    public TickRunner()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Meitou.Tick" };
        _thread.Start();
    }

    /// <summary>
    /// Starts a batch: the world's ticks with these start times.
    /// </summary>
    public void Start(World w, (double Hours, bool Daytime)[] tickStarts)
    {
        (_world, _starts, _failure) = (w, tickStarts, null);
        _start.Release();
    }

    /// <summary>
    /// Waits for the batch; rethrows what it threw, else returns its wall time in ms.
    /// </summary>
    public double Wait()
    {
        _done.Wait();
        _world = null;
        _failure?.Throw();
        return _ms;
    }

    /// <summary>
    /// The thread's loop: waits for a batch, runs its ticks (only the last two publish), keeps a failure and the time.
    /// </summary>
    void Run()
    {
        while (true)
        {
            _start.Wait();
            if (_stopping) return;

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // Only the last two publish, as in a frame (the shown pair keeps its buffers: World's snapshot ring).
                for (int i = 0; i < _starts.Length; i++)
                {
                    (_world!.GameHours, _world.Daytime) = _starts[i];
                    _world.RunTick(publish: i >= _starts.Length - 2);
                }
            }
            catch (Exception e)
            {
                _failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
            }

            _ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            _done.Release();
        }
    }

    /// <summary>
    /// Ends the thread after any batch running.
    /// </summary>
    public void Dispose()
    {
        _stopping = true;
        _start.Release();
    }
}
