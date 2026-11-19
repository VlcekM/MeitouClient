using System.Diagnostics;
using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Movement;

/// <summary>
/// The <c>cost</c> mode of <c>meitou-tools probe</c>: runs the world on and prints, every interval, what a tick costs: the living characters, the
/// mean and the slowest tick, the paths asked for, then ms a tick by phase, by system and by part, biggest first (the world's
/// <see cref="TickProfile"/>, as the game's <c>simprof</c> line). Each tick over a limit is listed with the characters whose path it asked for: the
/// probe's paths are found inside the tick, so a search storm shows as slow ticks (#1054).
/// </summary>
static class TickCostProbe
{
    /// <summary>
    /// The entries printed of each list.
    /// </summary>
    const int Top = 8;

    /// <summary>
    /// Runs <paramref name="rounds"/> rounds of <paramref name="interval"/> ticks, one line each, after the lines of its ticks that took
    /// <paramref name="slowMs"/> or more.
    /// </summary>
    public static void Run(ProbeWorld probe, int rounds, int interval, double slowMs)
    {
        var profile = probe.World.Profile;
        var paths = probe.World.Systems.OfType<MovementSystem>().First().Paths;
        profile.Enabled = true;
        for (int round = 0; round < rounds; round++)
        {
            profile.Clear();
            long asked = paths.Submitted;
            double slowest = RunRound(probe, paths, interval, slowMs);
            PrintRound(probe, profile, slowest, paths.Submitted - asked);
        }

        profile.Enabled = false;
    }

    /// <summary>
    /// Runs one round's ticks, naming the path askers of each tick that took <paramref name="slowMs"/> or more; the slowest one's ms.
    /// </summary>
    static double RunRound(ProbeWorld probe, PathService paths, int interval, double slowMs)
    {
        double slowest = 0;
        for (int k = 0; k < interval; k++)
        {
            long asked = paths.Submitted;
            long start = Stopwatch.GetTimestamp();
            probe.Tick(1);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            slowest = Math.Max(slowest, ms);
            if (ms < slowMs) continue;

            Console.WriteLine($"  tick {probe.World.Tick} took {ms:0.0} ms; {paths.Submitted - asked} paths asked: {string.Join("; ", PathAskers(probe, asked))}");
        }

        return slowest;
    }

    /// <summary>
    /// The characters whose latest path request is newer than request <paramref name="asked"/> (those the last tick asked for), each with its
    /// task, place, goal and the distance between them.
    /// </summary>
    static List<string> PathAskers(ProbeWorld probe, long asked)
    {
        var t = probe.Characters;
        var askers = new List<string>();
        for (int i = 0; i < t.HighWater; i++)
        {
            ref readonly var n = ref t.Next[i];
            if (!n.Alive || t.Cold(i) is not { } cold || cold.PathRequest <= asked) continue;

            float gap = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), n.Goal);
            askers.Add($"{cold.Name} #{i} {(CharacterTask)n.Task} at {n.Position.X:0},{n.Position.Y:0},{n.Position.Z:0} to {n.Goal.X:0},{n.Goal.Y:0} ({gap:0})");
        }

        return askers;
    }

    /// <summary>
    /// One round's line.
    /// </summary>
    static void PrintRound(ProbeWorld probe, TickProfile profile, double slowest, long asked)
    {
        var world = probe.World;
        int ticks = Math.Max(profile.Ticks, 1);
        int alive = Enumerable.Range(0, probe.Characters.HighWater).Count(i => probe.Characters.Next[i].Alive);
        var phases = TickProfile.PhaseNames.Select((name, p) => (name, profile.PhaseMs[p]));
        var systems = world.Systems.Select((s, k) => (s.GetType().Name, profile.SystemMs[k]));
        Console.WriteLine($"tick {world.Tick}: {alive} alive of {probe.Characters.HighWater} slots; ms a tick {profile.PhaseMs.Sum() / ticks:0.00}, "
            + $"slowest {slowest:0.0}; {asked} paths asked; phases: {List(phases, ticks)}; systems: {List(systems, ticks)}; "
            + $"parts: {List(profile.Parts, ticks)}");
    }

    /// <summary>
    /// The biggest entries as "name ms a tick".
    /// </summary>
    static string List(IEnumerable<(string Name, double Ms)> entries, int ticks) => string.Join(", ", entries
        .Where(e => e.Ms / ticks >= 0.01)
        .OrderByDescending(e => e.Ms)
        .Take(Top)
        .Select(e => $"{e.Name} {e.Ms / ticks:0.00}"));
}
