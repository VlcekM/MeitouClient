using System.Diagnostics;

namespace Meitou.Simulation;

/// <summary>
/// Wall time of the world's ticks per phase and per system, summed until the host takes it (the profiler's sim rows). Off by default: a tick then
/// pays one null check per measured span (and a flag check per <see cref="Part"/>). A system's time is every span it ran in (its serial calls, its parallel phases up to the barrier, its effects' apply).
/// </summary>
public sealed class TickProfile
{
    public static readonly string[] PhaseNames = ["inputs", "grid", "sight", "schedule", "think", "move", "act", "commit", "slow world", "publish"];
    public const int Inputs = 0, Grid = 1, Sight = 2, Schedule = 3, Think = 4, Move = 5, Act = 6, Commit = 7, SlowWorld = 8, Publish = 9;

    long last;

    /// <summary>Measure the ticks (set by the host between ticks).</summary>
    public bool Enabled { get; set; }
    /// <summary>Ticks measured since the last <see cref="Clear"/>.</summary>
    public int Ticks { get; private set; }
    /// <summary>Ms per phase (<see cref="PhaseNames"/>) since the last <see cref="Clear"/>.</summary>
    public double[] PhaseMs { get; } = new double[PhaseNames.Length];
    /// <summary>Ms per system, by its index in <see cref="World.Systems"/>, since the last <see cref="Clear"/>.</summary>
    public double[] SystemMs { get; private set; } = [];
    /// <summary>
    /// <see cref="SystemMs"/> split by phase: phase x systems + system (<see cref="Ms"/>). The serial phases, the parallel ones up to their barrier and the
    /// commit's apply; the grid, sight and publish have no system.
    /// </summary>
    public double[] PhaseSystemMs { get; private set; } = [];
    /// <summary>A system's ms in one phase since the last <see cref="Clear"/>.</summary>
    public double Ms(int phase, int system) => PhaseSystemMs[phase * SystemMs.Length + system];

    /// <summary>Ms per part a system marked inside its own span (<see cref="Part"/>), by name in the order first marked; they stay listed once seen.</summary>
    public List<(string Name, double Ms)> Parts { get; } = [];

    public void Clear()
    {
        Ticks = 0;
        Array.Clear(PhaseMs);
        Array.Clear(SystemMs);
        Array.Clear(PhaseSystemMs);
        for (int i = 0; i < Parts.Count; i++) Parts[i] = (Parts[i].Name, 0);
    }

    internal void Begin(int systems)
    {
        if (SystemMs.Length != systems) (SystemMs, PhaseSystemMs) = (new double[systems], new double[systems * PhaseNames.Length]);
        Ticks++;
        last = partLast = Stopwatch.GetTimestamp();
    }

    long partLast;

    // Per parallel phase (think, move, act): the parts it adds, "<phase> cpu" (its jobs' time summed over the threads) and "<phase> slow" (the slowest job of each system's run,
    // summed). Against the phase's wall time: wall near slow means one job holds the others up; cpu / threads near wall means the threads are all busy.
    static readonly Dictionary<int, (string Cpu, string Slow)> JobNames = new()
    {
        [Think] = ("think cpu", "think slow"), [Move] = ("move cpu", "move slow"), [Act] = ("act cpu", "act slow"),
    };

    internal void Jobs(int phase, long sumTicks, long maxTicks)
    {
        var (cpu, slow) = JobNames[phase];
        Add(cpu, sumTicks * 1000.0 / Stopwatch.Frequency);
        Add(slow, maxTicks * 1000.0 / Stopwatch.Frequency);
    }

    void Add(string name, double ms)
    {
        for (int i = 0; i < Parts.Count; i++)
            if (ReferenceEquals(Parts[i].Name, name) || Parts[i].Name == name) { Parts[i] = (name, Parts[i].Ms + ms); return; }
        Parts.Add((name, ms));
    }

    /// <summary>
    /// Inside a system's span: the time since the span began or the last part ended goes to the part <paramref name="name"/> (a breakdown of the system's own time, which still
    /// goes to the system at its lap). Serial phases only.
    /// </summary>
    public void Part(string name)
    {
        if (!Enabled) return;
        long now = Stopwatch.GetTimestamp();
        double ms = (now - Math.Max(partLast, last)) * 1000.0 / Stopwatch.Frequency;
        partLast = now;
        Add(name, ms);
    }

    /// <summary>The time since the last lap goes to <paramref name="phase"/> and, given one, to <paramref name="system"/>.</summary>
    internal void Lap(int phase, int system = -1)
    {
        long now = Stopwatch.GetTimestamp();
        double ms = (now - last) * 1000.0 / Stopwatch.Frequency;
        PhaseMs[phase] += ms;
        if (system >= 0) SystemMs[system] += ms;
        if (system >= 0) PhaseSystemMs[phase * SystemMs.Length + system] += ms;
        last = now;
    }
}
