using System.Diagnostics;
using System.Globalization;

namespace Meitou.Rendering;

/// <summary>
/// Keeps the device-local memory in use under the driver's budget (VK_EXT_memory_budget) when the draw ranges ask for more than the card has.
/// Past that point the driver fails allocations or resets the device (a TDR): the world's streaming has no other limit than the ranges.
/// <list type="bullet">
/// <item>It samples the memory in use of the budget a few times a second (<see cref="Tick"/>). At <see cref="High"/> (90%) it enters
/// <see cref="Pressure"/>: <see cref="Streaming"/> goes false (no new zone layouts, grass pages, textures or mesh uploads are started),
/// the caches evict what has been idle for a couple of seconds instead of a minute, and <see cref="RangeScale"/> falls by about 12% every
/// 0.75 s (down to <see cref="MinScale"/>) while the use stays high: every renderer multiplies the ranges it draws and streams to by it.
/// The farthest things leave first because the ranges they were in end first.</item>
/// <item>Pressure ends under <see cref="Low"/> (80%); streaming resumes. The ranges come back, 4% a second, only once the use has stayed
/// under <see cref="Recover"/> (74%) for four seconds: the band between is where the system rests, so it does not swing.</item>
/// <item>Allocations that can be large call <see cref="Allows"/> first (a texture's image, a bigger scratch buffer or instance arena): it asks
/// for a fresh sample and refuses what would take the use past <see cref="Ceiling"/> (92%), counting what was reserved since the sample, and a
/// refusal puts the guard into pressure at once. Streaming moves a gigabyte in a quarter of a second at its fastest, so the sampling alone
/// would be too slow.</item>
/// <item>At the default settings the use is a third of an 11 GB card's budget: the guard never leaves its idle state there
/// (<see cref="Scale"/> 1 multiplies exactly, so pictures are unchanged).</item>
/// </list>
/// Render thread only (<see cref="Tick"/>, <see cref="Allows"/>); the properties may be read from the job threads.
/// </summary>
public sealed class VramGuard
{
    public const double High = 0.90, Low = 0.80, Recover = 0.74, Ceiling = 0.92;
    /// <summary>Seconds between samples, between two steps down, and between two steps up; and the calm needed before the ranges return.</summary>
    public const double SampleSeconds = 0.25, StepDownSeconds = 0.75, StepUpSeconds = 1.0, CalmSeconds = 4.0;
    /// <summary>The newest a sample must be for <see cref="Allows"/> to use it.</summary>
    const double FreshSeconds = 0.02;
    public const float StepDown = 0.88f, StepUp = 1.04f, MinScale = 0.15f;

    readonly Func<(ulong Used, ulong Budget)> sample;
    readonly Func<double> clock;
    readonly Action<string>? log;
    double lastSample = double.NegativeInfinity, lastStep, calmSince;
    volatile bool pressure;
    float scale = 1f;
    long used, budget, reserved;
    bool logged;

    /// <param name="sample">The device-local memory in use and the budget (<c>VulkanDevice.VideoMemory</c>).</param>
    /// <param name="clock">Seconds, monotonic (a stopwatch by default; tests drive it).</param>
    /// <param name="log">Where the one log line goes (the console by default).</param>
    public VramGuard(Func<(ulong Used, ulong Budget)> sample, Func<double>? clock = null, Action<string>? log = null)
    {
        this.sample = sample;
        var watch = Stopwatch.StartNew();
        this.clock = clock ?? (() => watch.Elapsed.TotalSeconds);
        this.log = log ?? Console.WriteLine;
    }

    /// <summary>The factor every draw and streaming range is multiplied by (1: no clamp).</summary>
    public float RangeScale => scale;
    /// <summary>The memory use is high: the caches evict what they can, and nothing new is started.</summary>
    public bool Pressure => pressure;
    /// <summary>New zones, pages, textures and uploads may be started (false under <see cref="Pressure"/>).</summary>
    public bool Streaming => !pressure;
    /// <summary>The last sample, as a fraction of the budget.</summary>
    public double Fraction => budget == 0 ? 0 : (double)used / budget;
    /// <summary>The driver's device-local budget at the last sample (0 until one is taken, or when unknown).</summary>
    public long BudgetBytes => budget;
    /// <summary>Bytes in use above <see cref="Low"/> of the budget while under pressure (what is to be shed), else 0.</summary>
    public long ExcessBytes => pressure ? Math.Max(used - (long)(Low * budget), 0) : 0;
    /// <summary>Times the guard entered pressure, the lowest <see cref="RangeScale"/> it reached, and allocations <see cref="Allows"/> refused.</summary>
    public int Activations { get; private set; }
    public float LowestScale { get; private set; } = 1f;
    public int Refusals { get; private set; }

    /// <summary>Whether the guard is doing anything (pressure, or ranges not back at 1).</summary>
    public bool Active => pressure || scale < 1f;

    /// <summary>The statistics line (F11): idle, or the clamp and what is paused.</summary>
    public string Status =>
        Active ? $"guard: ranges x{scale:0.00}{(pressure ? ", streaming paused" : ", recovering")}, {Fraction * 100:0}% of the {budget / 1048576} MB budget{(Refusals > 0 ? $", {Refusals} allocations refused" : "")}"
               : $"guard: ok, {Fraction * 100:0}% of the {budget / 1048576} MB budget";

    /// <summary>Once a frame: takes a sample when one is due and moves the state.</summary>
    public void Tick()
    {
        double now = clock();
        if (now - lastSample >= SampleSeconds) Sample(now);
    }

    void Sample(double now)
    {
        lastSample = now;
        var (u, b) = sample();
        (used, budget, reserved) = ((long)u, (long)b, 0);
        Step(now);
    }

    /// <summary>
    /// Whether <paramref name="bytes"/> more device-local memory may be taken: the use (freshly sampled, plus what earlier calls reserved since)
    /// stays under <see cref="Ceiling"/> of the budget. A refusal puts the guard into pressure now. An unknown budget allows everything.
    /// </summary>
    public bool Allows(ulong bytes) => Allows(bytes, 0);

    /// <summary><see cref="Allows(ulong)"/> with <paramref name="pending"/> bytes the caller knows are on their way but are not in the memory
    /// in use yet (they count against the room without being reserved again).</summary>
    public bool Allows(ulong bytes, ulong pending)
    {
        double now = clock();
        if (now - lastSample >= FreshSeconds) Sample(now);
        if (budget <= 0) return true;
        long wanted = used + reserved + (long)pending + (long)bytes;
        if (wanted <= Ceiling * budget)
        {
            reserved += (long)bytes;
            return true;
        }
        Refusals++;
        if (!pressure) Enter(now, (double)wanted / budget);
        return false;
    }

    void Enter(double now, double fraction)
    {
        pressure = true;
        Activations++;
        lastStep = now - StepDownSeconds;   // the first step at once
        if (logged) return;
        logged = true;
        log?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"vram      guard: device-local memory at {fraction * 100:0}% of the {budget / 1048576} MB budget: new streaming paused, idle textures and meshes evicted, " +
            $"draw and streaming ranges clamped (x{scale * StepDown:0.00} and falling while over {Low * 100:0}%); the statistics show it, this line is not repeated"));
    }

    void Step(double now)
    {
        if (budget <= 0) return;
        double f = Fraction;
        if (!pressure && f >= High) Enter(now, f);
        else if (pressure && f < Low)
        {
            pressure = false;
            calmSince = now;
            lastStep = now;
        }
        if (pressure)
        {
            if (now - lastStep >= StepDownSeconds)
            {
                scale = Math.Max(MinScale, scale * StepDown);
                LowestScale = Math.Min(LowestScale, scale);
                lastStep = now;
            }
        }
        else if (scale < 1f)
        {
            if (f >= Recover) calmSince = now;
            else if (now - calmSince >= CalmSeconds && now - lastStep >= StepUpSeconds)
            {
                scale = Math.Min(1f, scale * StepUp);
                lastStep = now;
            }
        }
    }
}
