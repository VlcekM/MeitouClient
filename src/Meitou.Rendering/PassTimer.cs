using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// GPU time of a pass from two native timestamps (the frame's <see cref="QueryArena"/>), written where the frame's commands are going
/// (<see cref="IGlInterop.Interleave"/>: VkGl's pass is not ended). Results arrive when the frame's slot comes round; nothing waits.
/// </summary>
sealed class PassTimer(GpuContext gpu)
{
    readonly Queue<(QuerySlot Start, QuerySlot End)> pending = new();
    QuerySlot start, stamp;
    Action<CommandList>? write;

    /// <summary>The start stamp, in the frame's command stream now.</summary>
    public void Begin() => start = Write();

    /// <summary>The end stamp; the pair is read by <see cref="Poll"/> once its frame has completed.</summary>
    public void End()
    {
        var end = Write();
        if (start.IsValid && end.IsValid) pending.Enqueue((start, end));
        start = default;
    }

    QuerySlot Write()
    {
        var arena = gpu.Frame.Timestamps;
        stamp = arena.Allocate();
        if (!stamp.IsValid) return default;
        write ??= cmd => cmd.Timestamp(gpu.Frame.Timestamps, stamp);
        gpu.Interleave(write);
        return stamp;
    }

    /// <summary>Adds the milliseconds of every finished pair to <paramref name="samples"/>; the last one, or null.</summary>
    public double? Poll(List<double> samples)
    {
        double? last = null;
        var arena = gpu.Frame.Timestamps;
        while (pending.TryPeek(out var p))
        {
            if (arena.TryRead(p.Start, out ulong a) && arena.TryRead(p.End, out ulong b))
            {
                last = (b - a) / 1e6;
                samples.Add(last.Value);
            }
            else if (gpu.Frame.Number - p.Start.Frame < 16) break;   // not collected yet (a frame ring later); older ones are lost
            pending.Dequeue();
        }
        return last;
    }
}

/// <summary>
/// A uniform block the CPU rewrites now and then, read by native segments through the frame globals: each read in a frame after a write gets
/// the data as it is now in a slice of the frame's constants, so a segment prepared earlier keeps what it was given (as VkGl renamed a GL
/// buffer written after a draw had read it). It starts as zeros (shadows off, as the zero-filled GL buffers on the binding points were).
/// </summary>
sealed class FrameBlock(GpuContext gpu, int bytes)
{
    readonly float[] data = new float[bytes / 4];
    readonly ulong align = Math.Max(gpu.Device.Limits.MinUniformBufferOffsetAlignment, 16);
    int version = 1, written = -1;
    long frame = -1;
    BufferBinding binding;

    /// <summary>Replaces the whole block.</summary>
    public void Set(ReadOnlySpan<float> values) { values.CopyTo(data); version++; }

    public BufferBinding Binding()
    {
        var f = gpu.Frame;
        if (frame != f.Number || written != version)
        {
            binding = f.Constants.Write<float>(data, align).Binding;
            (frame, written) = (f.Number, version);
            f.Stats.ConstantBytes += data.Length * 4;
        }
        return binding;
    }
}
