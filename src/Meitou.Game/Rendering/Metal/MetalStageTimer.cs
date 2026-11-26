using Meitou.Game.Rendering.Profiling;
using SharpMetal.Metal;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// The profiler's GPU stage times on Metal (docs/metal-performance-memory.md "Metal profiler"): Apple GPUs sample their counters only at encoder
/// boundaries, so instead each stage lap commits the frame's commands so far as a command buffer of their own (<see cref="MetalContext.SplitFrame"/>)
/// and a stage's time is its command buffers' GPU span. Only while the GPU chart is shown: the splits cost the frame some time. Render thread only.
/// </summary>
sealed class MetalStageTimer(MetalContext ctx) : IStageGpuTimer
{
    #region State

    /// <summary>
    /// Per profiler slot, the frame's command buffers in queue order with the stage each one's time goes to (retained here until read).
    /// </summary>
    readonly List<(MTLCommandBuffer Command, int Stage)>[] _pieces =
        Enumerable.Range(0, FrameProfiler.Slots).Select(_ => new List<(MTLCommandBuffer, int)>()).ToArray();

    /// <summary>
    /// Per profiler slot, whether its frame is timed.
    /// </summary>
    readonly bool[] _timing = new bool[FrameProfiler.Slots];


    #endregion

    #region Timing

    /// <inheritdoc/>
    public void BeginFrame(int slot, bool timing)
    {
        Release(slot);
        _timing[slot] = timing;
        ctx.StoreAll = timing;
    }

    /// <inheritdoc/>
    public void Lap(int slot, int stage)
    {
        if (!_timing[slot]) return;

        var (pre, scene) = ctx.SplitFrame();
        Keep(slot, pre, StageClock.Uploads);
        Keep(slot, scene, stage);
    }

    /// <inheritdoc/>
    public bool EndFrame(int slot)
    {
        if (!_timing[slot]) return false;

        // Each pre-frame part ran before its scene part (all of it the pre-frame's: uploads, culls, bakes); the work after the last lap is the
        // interface's, the present the window copy's (the submit, as on Vulkan).
        var frame = ctx.Frame;
        Keep(slot, frame.PreFrameCommand, StageClock.Uploads);
        Keep(slot, frame.SceneCommand, StageClock.Ui);
        if (frame.Command.NativePtr != frame.SceneCommand.NativePtr) Keep(slot, frame.Command, StageClock.Submit);
        return true;
    }

    /// <inheritdoc/>
    public bool TryCollect(int slot, double[] stages, out double total)
    {
        total = 0;
        var pieces = _pieces[slot];
        if (pieces.Count == 0 || pieces[^1].Command.Status is not (MTLCommandBufferStatus.Completed or MTLCommandBufferStatus.Error)) return false;

        // Consecutive command buffers overlap on the GPU: each counts from the previous one's end, so no time is counted twice and the waits
        // between them (the GPU idle while the frame was recorded) are not counted.
        double previousEnd = 0;
        foreach (var (command, stage) in pieces)
        {
            if (!MetalContext.TryGpuTimes(command, out double start, out double end)) continue;

            double ms = Math.Max(0, end - Math.Max(start, previousEnd)) * 1000;
            stages[stage] += ms;
            total += ms;
            previousEnd = Math.Max(previousEnd, end);
        }

        Release(slot);
        return true;
    }

    /// <summary>
    /// Keeps <paramref name="command"/> (retained) for slot <paramref name="slot"/>, its time going to <paramref name="stage"/>.
    /// </summary>
    void Keep(int slot, MTLCommandBuffer command, int stage)
    {
        if (command.NativePtr == IntPtr.Zero) return;

        MetalContext.Retain(command);
        _pieces[slot].Add((command, stage));
    }

    /// <summary>
    /// Releases slot <paramref name="slot"/>'s command buffers.
    /// </summary>
    void Release(int slot)
    {
        foreach (var (command, _) in _pieces[slot]) command.Dispose();
        _pieces[slot].Clear();
    }

    #endregion
}
