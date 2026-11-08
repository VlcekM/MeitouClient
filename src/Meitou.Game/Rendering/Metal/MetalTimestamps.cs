using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// The frame's timestamps on Metal: slots are handed out (the renderers' bookkeeping works unchanged), but
/// nothing is recorded and nothing is read — Metal's GPU counters (<c>MTLCounterSampleBuffer</c>) are a
/// sampling-point model of their own. <c>TryRead</c> answers false, so a pass's timer keeps no results.
/// </summary>
sealed class MetalTimestamps : IGpuTimestamps
{
    long _frame;
    int _slot = -1;
    int _count;

    public int Count => _count;

    internal void Begin(int frameSlot, long frameNumber)
    {
        (_slot, _frame) = (frameSlot, frameNumber);
        _count = 0;
    }

    internal void End() { }

    public QuerySlot Allocate()
    {
        if (_slot < 0) return default;
        return new QuerySlot(_frame, _slot, (uint)_count++);
    }

    public bool TryRead(in QuerySlot q, out ulong nanoseconds)
    {
        nanoseconds = 0;
        return false;
    }
}
