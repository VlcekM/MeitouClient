#if MEITOU_METAL
// Metal exists on macOS only; the file is compiled only there (the csproj's MEITOU_METAL constant).
#pragma warning disable CA1416
using Meitou.Game.DevTools;
using Meitou.Game.Rendering.Metal;
using Meitou.Game.Rendering.Profiling;
using Silk.NET.Maths;

namespace Meitou.Game.Host;

/// <summary>
/// The play benchmark on the Metal loop (docs/bench.md "Play benchmark"): <see cref="PlayBench"/>'s hooks at the Metal loop's points, the
/// render-thread stage clock (the Vulkan loop's FrameProfiler starts it; Metal has no profiler), each frame's command-buffer GPU time and the
/// device's allocated size as the video memory. Inert without <c>--play-bench</c>.
/// </summary>
sealed class MetalPlayBench
{
    /// <summary>
    /// The host's benchmark recorder.
    /// </summary>
    readonly PlayBench _bench;

    /// <summary>
    /// The Metal device whose frames are measured.
    /// </summary>
    readonly MetalContext _ctx;

    /// <summary>
    /// The run has <c>--play-bench</c>.
    /// </summary>
    public bool Active { get; }

    /// <summary>
    /// The benchmark of <paramref name="host"/>'s run on <paramref name="ctx"/>; <paramref name="describe"/> is the summary's run line.
    /// </summary>
    public MetalPlayBench(GameHost host, MetalContext ctx, Func<string> describe)
    {
        _bench = host.PlayBench;
        _ctx = ctx;
        Active = host.GameOptions.PlayBench is not null;
        if (!Active) return;

        ctx.OnGpuFrame = _bench.PlayBenchGpu;
        _bench.VideoMemoryMb = () => ctx.Device.CurrentAllocatedSize / 1048576.0;
        _bench.RunDescription = describe;
    }

    /// <summary>
    /// The frame's script step before the world's update, ending the events section: false once the script is done.
    /// </summary>
    public bool BeginFrame(double dt)
    {
        if (!Active) return true;

        bool more = _bench.PlayBenchFrame(dt, profiler: null);
        _bench.BenchLap(0);
        return more;
    }

    /// <summary>
    /// Ends loop section <paramref name="section"/> (<see cref="PlayBench.BenchTail"/> before the window's events).
    /// </summary>
    public void Lap(int section) => _bench.BenchLap(section);

    /// <summary>
    /// After the world's ticks: their wall time, then the update section ends.
    /// </summary>
    public void Updated(double ms)
    {
        if (!Active) return;

        _bench.PlayBenchUpdated(ms);
        _bench.BenchLap(1);
    }

    /// <summary>
    /// The context began the frame (the slot and drawable waits are the begin-frame section): its GPU time is asked for, the stage clock starts.
    /// </summary>
    public void FrameBegun()
    {
        if (!Active) return;

        _bench.BenchLap(6);
        _bench.PlayBenchGpuTag(_ctx.FrameNumber);
        StageClock.Start();
    }

    /// <summary>
    /// The frame was committed with its present: the stage clock stops, the render section ends and the frame is recorded with
    /// <paramref name="busyMs"/> (the recording, from the frame's begin to the commit).
    /// </summary>
    public void FramePresented(double busyMs)
    {
        if (!Active) return;

        StageClock.EndFrame();
        _bench.BenchLap(7);
        _bench.PlayBenchRendered(busyMs, _ctx.Pipelines.LatePipelines);
    }

    /// <summary>
    /// Writes frames.csv and summary.md for the window's <paramref name="size"/> (before the renderers are released).
    /// </summary>
    public void Finish(Vector2D<int> size)
    {
        if (Active) _bench.WritePlayBench(size);
    }
}
#endif
