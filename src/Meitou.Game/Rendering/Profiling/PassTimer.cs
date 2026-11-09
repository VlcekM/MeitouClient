using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Profiling;

/// <summary>
/// GPU time of a pass from two native timestamps (the frame's <see cref="QueryArena"/>), written where the frame's commands are going (<see
/// cref="GpuContext.Interleave"/>: an open host pass is not ended). Results arrive when the frame's slot comes round; nothing waits.
/// </summary>
sealed class PassTimer(IGpuContext gpu)
{
    readonly Queue<(QuerySlot Start, QuerySlot End)> _pending = new();
    QuerySlot _start, _stamp;
    Action<IGpuCommandList>? _write;

    /// <summary>
    /// The start stamp, in the frame's command stream now.
    /// </summary>
    public void Begin() => _start = Write();

    /// <summary>
    /// The end stamp; the pair is read by <see cref="Poll"/> once its frame has completed.
    /// </summary>
    public void End()
    {
        var end = Write();
        if (_start.IsValid && end.IsValid) _pending.Enqueue((_start, end));
        _start = default;
    }

    QuerySlot Write()
    {
        var arena = gpu.Frame.Timestamps;
        _stamp = arena.Allocate();
        if (!_stamp.IsValid) return default;
        _write ??= cmd => cmd.Timestamp(gpu.Frame.Timestamps, _stamp);
        gpu.Interleave(_write);
        return _stamp;
    }

    /// <summary>
    /// Adds the milliseconds of every finished pair to <paramref name="samples"/>; the last one, or null.
    /// </summary>
    public double? Poll(List<double> samples)
    {
        double? last = null;
        var arena = gpu.Frame.Timestamps;
        while (_pending.TryPeek(out var p))
        {
            if (arena.TryRead(p.Start, out ulong a) && arena.TryRead(p.End, out ulong b))
            {
                last = (b - a) / 1e6;
                samples.Add(last.Value);
            }
            else if (gpu.Frame.Number - p.Start.Frame < 16) break;   // not collected yet (a frame ring later); older ones are lost
            _pending.Dequeue();
        }
        return last;
    }
}

/// <summary>
/// A uniform block the CPU rewrites now and then, read by native segments through the frame globals: each read in a frame after a write gets the data
/// as it is now in a slice of the frame's constants, so a segment prepared earlier keeps what it was given (as VkGl renamed a GL buffer written after
/// a draw had read it). It starts as zeros (shadows off, as the zero-filled GL buffers on the binding points were).
/// </summary>
sealed class FrameBlock(IGpuContext gpu, int bytes)
{
    readonly float[] _data = new float[bytes / 4];
    readonly ulong _align = Math.Max(gpu.UniformBufferAlign, 16);
    int _version = 1, _written = -1;
    long _frame = -1;
    GpuBufferBinding _binding;

    /// <summary>
    /// Replaces the whole block.
    /// </summary>
    public void Set(ReadOnlySpan<float> values)
    {
        values.CopyTo(_data);
        _version++;
    }

    public GpuBufferBinding Binding()
    {
        var f = gpu.Frame;
        if (_frame != f.Number || _written != _version)
        {
            _binding = f.WriteConstants(System.Runtime.InteropServices.MemoryMarshal.AsBytes((ReadOnlySpan<float>)_data), _align).Binding;
            (_frame, _written) = (f.Number, _version);
            f.Stats.ConstantBytes += _data.Length * 4;
        }
        return _binding;
    }
}