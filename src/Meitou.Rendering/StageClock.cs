using System.Diagnostics;

namespace Meitou.Rendering;

/// <summary>
/// Per-stage render-thread time of one frame (for <c>--fly-benchmark</c> and the <see cref="FrameProfiler"/>); <see cref="Lap"/> costs
/// a timestamp read and does nothing unless started. With a <see cref="Profiler"/> each lap also writes a GPU timestamp.
/// </summary>
static class StageClock
{
    public static readonly string[] Names = ["upd-terrain", "upd-objects", "upd-foliage", "sky-prepare", "reflection", "sky-draw", "terrain", "objects", "foliage", "water", "post", "gpu-wait", "shadows"];
    public static readonly double[] Ms = new double[Names.Length];
    public static bool Active;
    public static FrameProfiler? Profiler;
    static long last;

    /// <summary>
    /// The pass meter's hook (<c>MEITOU_PASS_STATS=1</c>, the viewer's PassMeter; docs/engine.md "Frame cost breakdown"): called with the label of
    /// what just ended and whether it is a part (<see cref="Sub"/>) of the enclosing stage or a stage of its own (<see cref="Lap"/>,
    /// <see cref="Phase"/>); null (the default) costs one compare. <see cref="OnStart"/> is called when a frame's clock starts.
    /// </summary>
    public static Action<string, bool>? OnClose;
    public static Action? OnStart;

    /// <summary>
    /// The recording jobs' CPU time per stage this frame (docs/renderer-native.md 9.3, wave 4): each job's own time, summed whatever thread ran it,
    /// so it does not add up to the frame's wall time; the render thread's wall time (which includes waiting for the jobs) stays in <see cref="Ms"/>.
    /// </summary>
    public static readonly double[] JobMs = new double[Names.Length];

    /// <summary>Adds a recording job's <paramref name="ticks"/> (Stopwatch) to <paramref name="stage"/> (render thread, after the jobs).</summary>
    public static void AddJob(int stage, long ticks)
    {
        if (Active && (uint)stage < (uint)JobMs.Length) JobMs[stage] += ticks * 1000.0 / Stopwatch.Frequency;
    }

    public static void Start() { Active = true; Array.Clear(Ms); Array.Clear(JobMs); last = Stopwatch.GetTimestamp(); OnStart?.Invoke(); }

    public static void Lap(int stage)
    {
        if (!Active) return;
        long now = Stopwatch.GetTimestamp();
        Ms[stage] += (now - last) * 1000.0 / Stopwatch.Frequency;
        last = now;
        Profiler?.Stamp(stage);
        OnClose?.Invoke(Names[stage], false);
    }

    /// <summary>Ends a part of the stage running (a cascade's terrain, the foliage's grass); the meter shows it under that stage. Does not touch the stage times.</summary>
    public static void Sub(string label) { if (OnClose is { } close && Active) close(label, true); }

    /// <summary>Ends a stage the stage list has no entry for (a shadow cascade, the overlays); the meter shows it as its own row. Does not touch the stage times.</summary>
    public static void Phase(string label) { if (OnClose is { } close && Active) close(label, false); }
}
