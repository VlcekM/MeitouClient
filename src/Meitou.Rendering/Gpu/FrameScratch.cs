using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// Device memory per frame slot for what a frame's compute passes write and its draws read (the GPU cull's per-view outputs, the grass
/// kernels' lists): bump allocated, reset when the slot comes round, and sized by need instead of by a fixed chunk.
/// <list type="bullet">
/// <item>A slot holds one buffer of the size the last frames needed (the largest need of the trailing <see cref="Window"/> frames, with
/// 25% of headroom, in whole MB). A frame that needs more takes extra buffers sized to the request; when its slot comes round again the
/// slot is made one buffer again, large enough for what that frame took (grow on demand).</item>
/// <item>A slot whose buffer is more than twice what the window needs is made smaller (shrink after): a peak that has passed gives its
/// memory back after about <see cref="Window"/> frames.</item>
/// <item><see cref="Cap"/> is the most a slot may hold. A request beyond it is refused (<see cref="TryAllocate"/> returns false, the
/// demand is counted in <see cref="Overflows"/> and <see cref="DemandBytes"/>) and the caller leaves that view out of the frame: the
/// memory-pressure guard (<see cref="VramGuard"/>) shortens the ranges when it happens, so the demand falls.</item>
/// </list>
/// Render thread only.
/// </summary>
public sealed class FrameScratch(GpuContext ctx, string name, BufferUse use, ulong cap, ulong minimum = 1ul << 20) : IDisposable
{
    /// <summary>Frames over which the largest need is kept before a slot may shrink (about two seconds at 60 frames per second).</summary>
    public const int Window = 120;
    const ulong Align = 256, Megabyte = 1ul << 20;

    sealed class Slot
    {
        public readonly List<DeviceBuffer> Buffers = [];
        public int Index;
        public ulong Offset;
        /// <summary>Bytes handed out in the slot's last frame (the aligned requests, tails abandoned for a later buffer not counted).</summary>
        public ulong Used;
        public ulong Capacity;
        /// <summary>The last frame took more than the slot's first buffer (or none was made yet): it is rebuilt when the slot comes round.</summary>
        public bool Grew;
    }

    readonly Slot[] slots = Enumerable.Range(0, ctx.Device.Frames.Count).Select(_ => new Slot()).ToArray();
    readonly ulong[] recent = new ulong[Window];
    int recentIndex;
    int slot = -1;
    long frame = -1;
    ulong frameNeed, frameDemand;

    /// <summary>The most one frame slot may hold (settable: the guard lowers it under pressure).</summary>
    public ulong Cap { get; set; } = cap;
    /// <summary>Bytes of device memory held now, over all slots.</summary>
    public ulong AllocatedBytes => slots.Aggregate(0ul, (s, x) => s + x.Capacity);
    /// <summary>The bytes the last finished frame took, and the largest and the mean over all frames so far (frames that took none count).</summary>
    public ulong LastNeed { get; private set; }
    public ulong PeakNeed { get; private set; }
    public double MeanNeed => frames == 0 ? 0 : (double)totalNeed / frames;
    /// <summary>The most any frame asked for, refused requests included (so the shortfall shows).</summary>
    public ulong PeakDemand { get; private set; }
    /// <summary>Requests refused for the cap (or for the driver's memory), and frames in which that happened.</summary>
    public long Overflows { get; private set; }
    public long OverflowFrames { get; private set; }
    /// <summary>Whether the current frame has had a request refused.</summary>
    public bool OverflowedThisFrame { get; private set; }
    public long Rebuilds { get; private set; }
    long frames;
    double totalNeed;

    void NewFrame()
    {
        var f = ctx.Frame;
        if (frame == f.Number) return;
        if (slot >= 0) EndFrame();
        (frame, slot, frameNeed, frameDemand) = (f.Number, f.Slot, 0, 0);
        OverflowedThisFrame = false;
        var s = slots[slot];
        // The slot's previous frame has finished (its fence was waited for): its buffers may be replaced. One buffer of what the trailing
        // window needed with headroom; more than that, or in pieces, or too small for what its last frame took: rebuilt.
        ulong window = 0;
        foreach (ulong r in recent) window = Math.Max(window, r);
        ulong want = Round(Math.Max(window, s.Used) + (Math.Max(window, s.Used) >> 2));
        want = Math.Min(Math.Max(want, minimum), Cap);
        if (s.Grew || s.Buffers.Count > 1 || s.Capacity > want * 2 || s.Capacity > Cap)
        {
            foreach (var b in s.Buffers) b.Dispose();   // freed after the frames in flight
            s.Buffers.Clear();
            s.Capacity = 0;
            if (window > 0 || s.Used > 0) TryAdd(s, want);
            Rebuilds++;
        }
        (s.Index, s.Offset, s.Used, s.Grew) = (0, 0, 0, false);
    }

    void EndFrame()
    {
        LastNeed = frameNeed;
        PeakNeed = Math.Max(PeakNeed, frameNeed);
        PeakDemand = Math.Max(PeakDemand, frameDemand);
        totalNeed += frameNeed;
        frames++;
        recent[recentIndex] = frameNeed;
        recentIndex = (recentIndex + 1) % Window;
        if (OverflowedThisFrame) OverflowFrames++;
    }

    static ulong Round(ulong bytes) => (bytes + Megabyte - 1) / Megabyte * Megabyte;

    bool TryAdd(Slot s, ulong size)
    {
        if (s.Capacity + size > Cap) return false;
        try { s.Buffers.Add(DeviceBuffer.Create(ctx, size, use, $"{name} {slot}")); }
        catch (Meitou.Rendering.Gpu.Core.VulkanException) { return false; }
        s.Capacity += size;
        return true;
    }

    /// <summary>Hands out <paramref name="bytes"/> (aligned to 256) of this frame's scratch; false when the slot's cap does not allow it.</summary>
    public bool TryAllocate(ulong bytes, out Buffer buffer, out ulong offset)
    {
        NewFrame();
        var s = slots[slot];
        bytes = (bytes + Align - 1) / Align * Align;
        frameDemand += bytes;
        while (true)
        {
            if (s.Index < s.Buffers.Count && s.Offset + bytes <= s.Buffers[s.Index].Size)
            {
                (buffer, offset) = (s.Buffers[s.Index].Handle, s.Offset);
                s.Offset += bytes;
                s.Used += bytes;
                frameNeed += bytes;
                return true;
            }
            if (s.Index + 1 < s.Buffers.Count) { s.Index++; s.Offset = 0; continue; }   // the tail is left; the next buffer may take it
            // None has room: another buffer, the size of the request (the slot is rebuilt as one when it comes round).
            s.Grew = true;
            if (!TryAdd(s, Math.Max(Round(bytes), s.Buffers.Count == 0 ? minimum : Megabyte)))
            {
                Overflows++;
                OverflowedThisFrame = true;
                (buffer, offset) = (default, 0);
                return false;
            }
            s.Index = s.Buffers.Count - 1;
            s.Offset = 0;
        }
    }

    public void Dispose()
    {
        foreach (var s in slots)
        {
            foreach (var b in s.Buffers) b.Dispose();
            s.Buffers.Clear();
            s.Capacity = 0;
        }
    }
}
