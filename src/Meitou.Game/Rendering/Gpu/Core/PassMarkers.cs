using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu.Core;

/// <summary>
/// Diagnostic (issue #319, <c>MEITOU_DIAG_FAULT=1</c>): how far the GPU got in each frame in flight. Every pass of a frame (the pre-frame buffer, each
/// native segment) fills one word of a host-visible buffer with its number when it begins and another when it ends, so after a lost device the two
/// words of a frame slot name the pass the GPU was in (<see cref="Describe"/>). Recorded on the render thread.
/// </summary>
public sealed unsafe class PassMarkers
{
    /// <summary>
    /// The low bits of a marker hold the pass's number in its frame (1-based); the rest the frame number, which tells a slot's old values apart.
    /// </summary>
    const int IndexBits = 12;
    /// <summary>
    /// The largest pass number a marker holds; later passes of a frame share it.
    /// </summary>
    const uint MaxIndex = (1u << IndexBits) - 1;
    /// <summary>
    /// Bytes of the buffer per frame slot: the marker of the pass begun last, then of the pass finished last.
    /// </summary>
    const ulong SlotBytes = 8;

    /// <summary>
    /// The device the markers are recorded on.
    /// </summary>
    readonly VulkanDevice _dev;
    /// <summary>
    /// The host-visible buffer the GPU fills: <see cref="SlotBytes"/> per frame slot.
    /// </summary>
    readonly BufferAllocation _buffer;
    /// <summary>
    /// Per frame slot: the labels of the passes its frame recorded, in order (also the lock for them: the report may come from another thread).
    /// </summary>
    readonly List<string>[] _labels;
    /// <summary>
    /// Per frame slot: the number of the frame the labels belong to (0: none yet).
    /// </summary>
    readonly long[] _frames;

    /// <summary>
    /// The marker buffer and the per-slot label lists for the device's frame ring.
    /// </summary>
    internal PassMarkers(VulkanDevice dev)
    {
        _dev = dev;
        int slots = dev.Frames.Count;
        _buffer = dev.Allocator.CreateBuffer((ulong)slots * SlotBytes, BufferUsageFlags.TransferDstBit, MemoryKind.Readback, "pass markers");
        new Span<byte>(_buffer.Mapped, (int)((ulong)slots * SlotBytes)).Clear();
        _labels = new List<string>[slots];
        for (int i = 0; i < slots; i++) _labels[i] = [];
        _frames = new long[slots];
    }

    /// <summary>
    /// The frame ring began a frame: its slot's labels start over.
    /// </summary>
    public void BeginFrame()
    {
        int slot = _dev.Frames.Slot;
        lock (_labels[slot])
        {
            _labels[slot].Clear();
            _frames[slot] = _dev.Frames.FrameNumber;
        }
    }

    /// <summary>
    /// Records that the pass <paramref name="label"/> begins at this point of <paramref name="cb"/> (outside any rendering) and returns its marker
    /// for <see cref="End"/>.
    /// </summary>
    public uint Begin(CommandBuffer cb, string label)
    {
        int slot = _dev.Frames.Slot;
        uint id;
        lock (_labels[slot])
        {
            _labels[slot].Add(label);
            id = Marker(_frames[slot], (uint)Math.Min(_labels[slot].Count, (int)MaxIndex));
        }

        _dev.Vk.CmdFillBuffer(cb, _buffer.Buffer, (ulong)slot * SlotBytes, 4, id);
        return id;
    }

    /// <summary>
    /// Records that the pass with <paramref name="marker"/> ends at this point of <paramref name="cb"/> (outside any rendering).
    /// </summary>
    public void End(CommandBuffer cb, uint marker) =>
        _dev.Vk.CmdFillBuffer(cb, _buffer.Buffer, (ulong)_dev.Frames.Slot * SlotBytes + 4, 4, marker);

    /// <summary>
    /// One line per frame slot, oldest frame first: how many passes the frame recorded and which the GPU began and finished last.
    /// </summary>
    internal void Describe(List<string> lines)
    {
        var words = new ReadOnlySpan<uint>(_buffer.Mapped, _frames.Length * 2);
        foreach (int slot in Enumerable.Range(0, _frames.Length).OrderBy(s => _frames[s]))
        {
            lock (_labels[slot])
            {
                long frame = _frames[slot];
                if (frame == 0) continue;

                var labels = _labels[slot];
                string began = Pass(labels, frame, words[slot * 2]), finished = Pass(labels, frame, words[slot * 2 + 1]);
                lines.Add($"frame {frame}: {labels.Count} passes recorded; the GPU began {began}, finished {finished}");
            }
        }
    }

    /// <summary>
    /// The marker of pass <paramref name="index"/> (1-based) of <paramref name="frame"/>.
    /// </summary>
    static uint Marker(long frame, uint index) => (uint)frame << IndexBits | index;

    /// <summary>
    /// The pass a marker word names, as "7 'terrain'"; "none" when the word is not of <paramref name="frame"/> (the GPU has not reached the frame).
    /// </summary>
    static string Pass(List<string> labels, long frame, uint word)
    {
        uint index = word & MaxIndex;
        if (index == 0 || word >> IndexBits != (uint)frame << IndexBits >> IndexBits) return "none";
        if (index == MaxIndex && labels.Count > MaxIndex) return $"{MaxIndex} or later";

        return index <= labels.Count ? $"{index} '{labels[(int)index - 1]}'" : $"{index}";
    }
}
