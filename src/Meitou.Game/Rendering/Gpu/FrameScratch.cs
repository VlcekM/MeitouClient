using Meitou.Game.Rendering.Frame;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// Device memory per frame slot for what a frame's compute passes write and its draws read (the GPU cull's per-view outputs, the grass kernels'
/// lists): bump allocated, reset when the slot comes round, and sized by need instead of by a fixed chunk.
/// <list type="bullet">
/// <item>A slot holds one buffer of the size the last frames needed (the largest need of the trailing <see cref="Window"/> frames, with 25% of
/// headroom, in whole MB). A frame that needs more takes extra buffers sized to the request; when its slot comes round again the slot is made one
/// buffer again, large enough for what that frame took (grow on demand).</item>
/// <item>A slot whose buffer is more than twice what the window needs is made smaller (shrink after): a peak that has passed gives its memory back
/// after about <see cref="Window"/> frames.</item>
/// <item><see cref="Cap"/> is the most a slot may hold. A request beyond it is refused (<see cref="TryAllocate"/> returns false, the demand is
/// counted in <see cref="Overflows"/> and <see cref="DemandBytes"/>) and the caller leaves that view out of the frame: the memory-pressure guard
/// (<see cref="VramGuard"/>) shortens the ranges when it happens, so the demand falls.</item>
/// </list>
/// Render thread only.
/// </summary>
public sealed class FrameScratch(IGpuContext ctx, string name, BufferUse use, ulong cap, ulong minimum = 1ul << 20) : IDisposable
{
    /// <summary>
    /// Frames over which the largest need is kept before a slot may shrink (about two seconds at 60 frames per second).
    /// </summary>
    public const int Window = 120;

    /// <summary>
    /// The allocation alignment (256 bytes), and one MB, the unit buffer sizes are rounded to.
    /// </summary>
    const ulong Align = 256, Megabyte = 1ul << 20;

    /// <summary>
    /// One slot per frame in flight.
    /// </summary>
    readonly Slot[] _slots = Enumerable.Range(0, ctx.FramesInFlight).Select(_ => new Slot()).ToArray();
    /// <summary>
    /// The need of each of the last <see cref="Window"/> frames (a ring).
    /// </summary>
    readonly ulong[] _recent = new ulong[Window];
    /// <summary>
    /// Where the next finished frame's need goes in <see cref="_recent"/>.
    /// </summary>
    int _recentIndex;
    /// <summary>
    /// The slot of the current frame; -1 before the first request.
    /// </summary>
    int _slot = -1;
    /// <summary>
    /// The number of the current frame; -1 before the first request.
    /// </summary>
    long _frame = -1;
    /// <summary>
    /// The bytes the current frame took, and the bytes it asked for (refused requests included).
    /// </summary>
    ulong _frameNeed, _frameDemand;
    /// <summary>
    /// The frames finished so far.
    /// </summary>
    long _frames;
    /// <summary>
    /// The bytes all finished frames took (for <see cref="MeanNeed"/>).
    /// </summary>
    double _totalNeed;

    /// <summary>
    /// One frame slot's buffers and its bump cursor.
    /// </summary>
    sealed class Slot
    {
        /// <summary>
        /// The slot's buffers; one, except in a frame that grew.
        /// </summary>
        public readonly List<DeviceBuffer> Buffers = [];

        /// <summary>
        /// The buffer the cursor is in.
        /// </summary>
        public int Index;

        /// <summary>
        /// The cursor's offset in that buffer.
        /// </summary>
        public ulong Offset;

        /// <summary>
        /// Bytes handed out in the slot's last frame (the aligned requests, tails abandoned for a later buffer not counted).
        /// </summary>
        public ulong Used;

        /// <summary>
        /// The bytes of all the slot's buffers.
        /// </summary>
        public ulong Capacity;

        /// <summary>
        /// The last frame took more than the slot's first buffer (or none was made yet): it is rebuilt when the slot comes round.
        /// </summary>
        public bool Grew;
    }

    /// <summary>
    /// The most one frame slot may hold (settable: the guard lowers it under pressure).
    /// </summary>
    public ulong Cap { get; set; } = cap;

    /// <summary>
    /// Bytes of device memory held now, over all slots.
    /// </summary>
    public ulong AllocatedBytes => _slots.Aggregate(0ul, (s, x) => s + x.Capacity);

    /// <summary>
    /// The bytes the last finished frame took, and the largest and the mean over all frames so far (frames that took none count).
    /// </summary>
    public ulong LastNeed { get; private set; }

    /// <summary>
    /// The largest need of any frame so far.
    /// </summary>
    public ulong PeakNeed { get; private set; }

    /// <summary>
    /// The mean need over all frames so far.
    /// </summary>
    public double MeanNeed => _frames == 0 ? 0 : (double)_totalNeed / _frames;

    /// <summary>
    /// The most any frame asked for, refused requests included (so the shortfall shows).
    /// </summary>
    public ulong PeakDemand { get; private set; }

    /// <summary>
    /// Requests refused for the cap (or for the driver's memory), and frames in which that happened.
    /// </summary>
    public long Overflows { get; private set; }

    /// <summary>
    /// Frames in which a request was refused.
    /// </summary>
    public long OverflowFrames { get; private set; }

    /// <summary>
    /// Whether the current frame has had a request refused.
    /// </summary>
    public bool OverflowedThisFrame { get; private set; }

    /// <summary>
    /// Times a slot's buffers were replaced (grown, merged or shrunk).
    /// </summary>
    public long Rebuilds { get; private set; }

    /// <summary>
    /// Rebuilds whose replacement was refused: the slot kept (the largest of) its old buffers.
    /// </summary>
    public long KeptOnRefusal { get; private set; }

    /// <summary>
    /// Asked before a buffer is made: false refuses it (the memory-pressure guard, <c>VramGuard.Allows</c>).
    /// </summary>
    public Func<ulong, bool>? MayGrow { get; set; }

    /// <summary>
    /// On the frame's first request: closes the last frame's statistics, then rebuilds the slot when it grew, is in pieces, is over twice the
    /// window's need or over the cap, and resets its cursor.
    /// </summary>
    void NewFrame()
    {
        var f = ctx.Frame;
        if (_frame == f.Number) return;

        if (_slot >= 0) EndFrame();
        (_frame, _slot, _frameNeed, _frameDemand) = (f.Number, f.Slot, 0, 0);
        OverflowedThisFrame = false;
        var s = _slots[_slot];

        // The slot's previous frame has finished (its fence was waited for): its buffers may be replaced. One buffer of what the trailing window
        // needed with headroom; more than that, or in pieces, or too small for what its last frame took: rebuilt.
        ulong window = 0;
        foreach (ulong r in _recent) window = Math.Max(window, r);

        ulong want = Round(Math.Max(window, s.Used) + (Math.Max(window, s.Used) >> 2));
        want = Math.Min(Math.Max(want, minimum), Cap);
        if (s.Grew || s.Buffers.Count > 1 || s.Capacity > want * 2 || s.Capacity > Cap)
            Rebuild(s, want, window);

        (s.Index, s.Offset, s.Used, s.Grew) = (0, 0, 0, false);
    }

    /// <summary>
    /// Replaces a slot's buffers with one of <paramref name="want"/> bytes (none when nothing was needed lately); when that is refused, keeps the
    /// largest old one.
    /// </summary>
    void Rebuild(Slot s, ulong want, ulong window)
    {
        // The replacement is made before the old buffers go: when it is refused (the driver's memory) the slot keeps what it has, so a frame never
        // starts with nothing where the last one had room. The old ones are freed after the frames in flight.
        var old = s.Buffers.ToArray();
        s.Buffers.Clear();
        s.Capacity = 0;
        if ((window > 0 || s.Used > 0) && !TryAdd(s, want) && old.Length > 0)
            KeepLargest(s, old);

        foreach (var b in old) b?.Dispose();

        Rebuilds++;
    }

    /// <summary>
    /// Keeps the largest old buffer when it is within the cap (the rest are freed by the caller).
    /// </summary>
    void KeepLargest(Slot s, DeviceBuffer[] old)
    {
        // Keep the largest old buffer (the slot grows again by pieces if a frame needs more) and give the rest back.
        int keep = 0;
        for (int i = 1; i < old.Length; i++)
            if (old[i].Size > old[keep].Size) keep = i;

        if (old[keep].Size <= Cap)
        {
            s.Buffers.Add(old[keep]);
            s.Capacity = old[keep].Size;
            old[keep] = null!;
        }

        KeptOnRefusal++;
    }

    /// <summary>
    /// Records the finished frame's need and demand in the statistics and the window.
    /// </summary>
    void EndFrame()
    {
        LastNeed = _frameNeed;
        PeakNeed = Math.Max(PeakNeed, _frameNeed);
        PeakDemand = Math.Max(PeakDemand, _frameDemand);
        _totalNeed += _frameNeed;
        _frames++;
        _recent[_recentIndex] = _frameNeed;
        _recentIndex = (_recentIndex + 1) % Window;
        if (OverflowedThisFrame) OverflowFrames++;
    }

    /// <summary>
    /// Bytes rounded up to whole MB.
    /// </summary>
    static ulong Round(ulong bytes) => (bytes + Megabyte - 1) / Megabyte * Megabyte;

    /// <summary>
    /// Adds a buffer of <paramref name="size"/> bytes to the slot when the cap and <see cref="MayGrow"/> allow it and the driver has the memory.
    /// </summary>
    bool TryAdd(Slot s, ulong size)
    {
        if (s.Capacity + size > Cap) return false;

        if (MayGrow is { } may && !may(size)) return false;

        try
        {
            s.Buffers.Add(DeviceBuffer.Create(ctx, size, use, $"{name} {_slot}"));
        }
        catch (Core.VulkanException)
        {
            return false;
        }

        s.Capacity += size;
        return true;
    }

    /// <summary>
    /// Hands out <paramref name="bytes"/> (aligned to 256) of this frame's scratch; false when the slot's cap does not allow it.
    /// </summary>
    public bool TryAllocate(ulong bytes, out GpuBuffer buffer, out ulong offset)
    {
        NewFrame();
        var s = _slots[_slot];
        bytes = (bytes + Align - 1) / Align * Align;
        _frameDemand += bytes;
        while (true)
        {
            if (s.Index < s.Buffers.Count && s.Offset + bytes <= s.Buffers[s.Index].Size)
            {
                (buffer, offset) = (s.Buffers[s.Index].Handle, s.Offset);
                s.Offset += bytes;
                s.Used += bytes;
                _frameNeed += bytes;
                return true;
            }

            if (s.Index + 1 < s.Buffers.Count)
            {
                // The tail is left; the next buffer may take it.
                s.Index++;
                s.Offset = 0;
                continue;
            }

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

    /// <summary>
    /// Releases every slot's buffers.
    /// </summary>
    public void Dispose()
    {
        foreach (var s in _slots)
        {
            foreach (var b in s.Buffers) b.Dispose();

            s.Buffers.Clear();
            s.Capacity = 0;
        }
    }
}
