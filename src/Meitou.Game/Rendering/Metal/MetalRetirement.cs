namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// Actions that must not run until every frame that could name their resource has left the ring. Kept separate from
/// <see cref="MetalContext"/> so the window and budget rules are unit-testable without a GPU or a window.
/// </summary>
internal sealed class DeferredReleaseQueue
{
    /// <summary>
    /// The pending actions and the frame each was queued in.
    /// </summary>
    readonly List<(long Frame, Action Release)> _items = [];
    /// <summary>
    /// Guards <see cref="_items"/>.
    /// </summary>
    readonly object _gate = new();

    /// <summary>
    /// How many actions are pending.
    /// </summary>
    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>
    /// Queues <paramref name="release"/> as named by <paramref name="frame"/>.
    /// </summary>
    public void Add(long frame, Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (_gate) _items.Add((frame, release));
    }

    /// <summary>
    /// Releases every action older than <paramref name="ringDepth"/> frames, up to <paramref name="budget"/>. Returns
    /// the number of actions run. Actions run outside the lock: a release must be free to call back into the context.
    /// </summary>
    public int ReleaseDue(long currentFrame, int ringDepth, int budget, bool force = false)
    {
        List<Action>? run = null;
        int released = 0;

        lock (_gate)
        {
            int limit = force ? int.MaxValue : Math.Max(0, budget);

            for (int i = 0; i < _items.Count && released < limit;)
            {
                var item = _items[i];

                if (!force && currentFrame - item.Frame < ringDepth)
                {
                    i++;
                    continue;
                }

                (run ??= []).Add(item.Release);
                _items.RemoveAt(i);
                released++;
            }
        }

        if (run is not null) foreach (var release in run) release();

        return released;
    }
}
