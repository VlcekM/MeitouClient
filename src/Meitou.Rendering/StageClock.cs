using System.Diagnostics;

namespace Meitou.Rendering;

/// <summary>Per-stage render-thread time of one frame (for <c>--fly-benchmark</c>); <see cref="Lap"/> costs a timestamp read and does nothing unless started.</summary>
static class StageClock
{
    public static readonly string[] Names = ["upd-terrain", "upd-objects", "upd-foliage", "sky-prepare", "reflection", "sky-draw", "terrain", "objects", "foliage", "water", "post", "gpu-wait", "shadows"];
    public static readonly double[] Ms = new double[Names.Length];
    public static bool Active;
    static long last;

    public static void Start() { Active = true; Array.Clear(Ms); last = Stopwatch.GetTimestamp(); }

    public static void Lap(int stage)
    {
        if (!Active) return;
        long now = Stopwatch.GetTimestamp();
        Ms[stage] += (now - last) * 1000.0 / Stopwatch.Frequency;
        last = now;
    }
}
