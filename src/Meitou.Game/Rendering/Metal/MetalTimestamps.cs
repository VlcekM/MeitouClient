using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// The frame's timestamps on Metal: slots are handed out (the renderers' bookkeeping works unchanged), but
/// nothing is recorded and nothing is read — Metal's GPU counters (<c>MTLCounterSampleBuffer</c>) are a
/// sampling-point model of their own. <c>TryRead</c> answers false, so a pass's timer keeps no results.
/// </summary>
sealed class MetalTimestamps : IGpuTimestamps
{
    /// <summary>
    /// The number of the frame the slots are handed out for.
    /// </summary>
    long _frame;
    /// <summary>
    /// The frame slot the slots are handed out for; -1 before the first frame.
    /// </summary>
    int _slot = -1;
    /// <summary>
    /// How many slots the frame has handed out.
    /// </summary>
    int _count;

    /// <summary>
    /// How many slots the frame has handed out.
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Starts handing out slots for a new frame.
    /// </summary>
    internal void Begin(int frameSlot, long frameNumber)
    {
        (_slot, _frame) = (frameSlot, frameNumber);
        _count = 0;
    }

    /// <summary>
    /// Nothing: no timestamps are recorded.
    /// </summary>
    internal void End() { }

    /// <summary>
    /// The frame's next slot (none before the first frame); nothing is recorded into it.
    /// </summary>
    public QuerySlot Allocate()
    {
        if (_slot < 0) return default;

        return new QuerySlot(_frame, _slot, (uint)_count++);
    }

    /// <summary>
    /// False: Metal timestamps are never read.
    /// </summary>
    public bool TryRead(in QuerySlot q, out ulong nanoseconds)
    {
        nanoseconds = 0;
        return false;
    }
}
