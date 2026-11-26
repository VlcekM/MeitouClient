namespace Meitou.Game.Rendering.Profiling;

/// <summary>
/// A backend's own GPU stage times for the <see cref="FrameProfiler"/>, in place of the Vulkan timestamps (Metal: the frame's command buffer split
/// at the stage laps, docs/metal-performance-memory.md "Metal profiler"). The profiler hands out its frame slots; a slot's times are read a few
/// frames later, without waiting.
/// </summary>
interface IStageGpuTimer
{
    /// <summary>
    /// Starts timing the frame in profiler slot <paramref name="slot"/> (its context's frame already begun); nothing is timed unless
    /// <paramref name="timing"/> (the GPU chart is shown: timing costs the frame).
    /// </summary>
    void BeginFrame(int slot, bool timing);

    /// <summary>
    /// The GPU work recorded since the last lap goes to <paramref name="stage"/>.
    /// </summary>
    void Lap(int slot, int stage);

    /// <summary>
    /// The frame in <paramref name="slot"/> was committed with its present: whether it has times to read.
    /// </summary>
    bool EndFrame(int slot);

    /// <summary>
    /// Adds slot <paramref name="slot"/>'s ms per stage into <paramref name="stages"/> once its frame has completed; <paramref name="total"/> is the
    /// frame's whole GPU time. False while it has not (nothing added).
    /// </summary>
    bool TryCollect(int slot, double[] stages, out double total);
}
