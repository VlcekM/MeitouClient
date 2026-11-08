using Meitou.Rendering.Gpu;

namespace Meitou.Metal;

/// <summary>
/// The frame's timestamps on Metal: slots are handed out (the renderers' bookkeeping works unchanged), but
/// nothing is recorded and nothing is read — Metal's GPU counters (<c>MTLCounterSampleBuffer</c>) are a
/// sampling-point model of their own. <c>TryRead</c> answers false, so a pass's timer keeps no results.
/// </summary>
sealed class MetalTimestamps : IGpuTimestamps
{
    long frame;
    int slot = -1;
    int count;

    public int Count => count;

    internal void Begin(int frameSlot, long frameNumber)
    {
        (slot, frame) = (frameSlot, frameNumber);
        count = 0;
    }

    internal void End() { }

    public QuerySlot Allocate()
    {
        if (slot < 0) return default;
        return new QuerySlot(frame, slot, (uint)count++);
    }

    public bool TryRead(in QuerySlot q, out ulong nanoseconds)
    {
        nanoseconds = 0;
        return false;
    }
}
