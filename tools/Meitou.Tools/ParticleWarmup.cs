using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Particles;

/// <summary>
/// <c>meitou-tools particles warmup &lt;weather&gt;...</c>: what changing to a weather costs the viewer (docs/formats/particle-universe.md "Weather
/// changes"). For every effect of the weather, one group of the weather's list is made and warmed the way <see cref="EffectSet"/> does it: the
/// schedule (<see cref="EffectGroup.BeginWarm"/>, which the viewer runs on the warm-up thread), then the simulation of the queued time on one thread
/// (<see cref="EffectGroup.SimulateSerial"/>, the warm-up thread's) and in parallel (<see cref="EffectGroup.Simulate"/>, the old start-up), and then
/// the cost of a 1/60 s frame of the warmed group (serial and parallel). Every effect is measured twice and the second pass printed: the first one
/// only compiles what it touches (the viewer does that at start-up, <see cref="EffectGroups.PrimeJit"/>).
/// </summary>
static class ParticleWarmup
{
    /// <summary>
    /// Measures the warm-up of every effect of each weather in <paramref name="names"/> (six heavy base-game weathers when empty); returns the exit
    /// code.
    /// </summary>
    public static int Run(GameInstall install, string[] names)
    {
        var db = GameDatabase.Load(LoadOrder.BaseGame(install));
        var library = ParticleLibrary.Load(install);
        var camera = new EffectCamera(new Vector3(0, 400, 0), -Vector3.UnitZ);
        EffectGroups.PrimeJit();
        string[] weathers = names.Length == 0
            ? ["Kenshi_red_rain", "Sand stream ambient", "shek desert storm", "purple desert ambience", "Kenshi_Ash-Flakes", "Twister Storm"]
            : names;
        foreach (var name in weathers)
        {
            var weather = WeatherEffectAdapter.FromName(db, name);
            Console.WriteLine($"{name}: {weather.Effects.Count} effects");
            foreach (var entry in weather.Effects)
                WarmEffect(entry, library, camera, weather);
        }

        return 0;
    }

    /// <summary>
    /// Measures one weather effect: makes a group, schedules and simulates its warm-up serially and in parallel, then times a warmed frame; prints
    /// the second of two passes.
    /// </summary>
    static void WarmEffect(WeatherEffectEntry entry, ParticleLibrary library, EffectCamera camera, WeatherEffectInput weather)
    {
        EffectGroup? Make() => EffectGroups.Create(entry, library, 1,
                new EffectWorld { GroundHeight = static (_, _) => 300, Area = new DiscArea(0, 0, 6000) });
        if (Make() is not { } probe)
        {
            Console.WriteLine($"  {entry.Effect.Name}: no group");
            return;
        }

        float warm = Math.Min(ParticleSimulation.LongestLife(probe.System), EffectSet.MaxAutoPrewarm);
        string line = "";
        for (int pass = 0; pass < 2; pass++)
            line = MeasurePass(Make, entry, camera, weather, warm);
        Console.WriteLine(line);
    }

    /// <summary>
    /// One warm-up and frame measurement of the effect, as its printed line. The first pass only compiles what it touches; the second one is printed.
    /// </summary>
    static string MeasurePass(Func<EffectGroup?> make, WeatherEffectEntry entry, EffectCamera camera, WeatherEffectInput weather, float warm)
    {
        var serialGroup = make()!;
        var clock = Stopwatch.StartNew();
        serialGroup.BeginWarm(warm, camera, weather);
        double begin = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        serialGroup.SimulateSerial(CancellationToken.None);
        double serial = clock.Elapsed.TotalMilliseconds;
        var parallelGroup = make()!;
        parallelGroup.BeginWarm(warm, camera, weather);
        clock.Restart();
        parallelGroup.Simulate();
        double parallel = clock.Elapsed.TotalMilliseconds;
        var (frameSerial, frameParallel) = MeasureFrames(serialGroup, parallelGroup, camera, weather);
        string head = $"  {entry.Effect.Name,-28} {entry.Effect.Type,-12} units {serialGroup.Units.Count,3} particles {serialGroup.ParticleCount,6}";
        string simulation = $"simulation {serial,7:0.0} ms on one thread, {parallel,7:0.0} ms in parallel";
        string schedule = $"  warm-up of {warm,2:0} s: schedule {begin,6:0.0} ms, {simulation}";
        string frame = $" | a frame: {frameSerial,5:0.00} ms serial, {frameParallel,5:0.00} ms parallel";
        return head + schedule + frame;
    }

    /// <summary>
    /// Average milliseconds per 1/60 s frame of the warmed groups, serial then parallel.
    /// </summary>
    static (double Serial, double Parallel) MeasureFrames(
        EffectGroup serialGroup, EffectGroup parallelGroup, EffectCamera camera, WeatherEffectInput weather)
    {
        double frameSerial = 0, frameParallel = 0;
        const int frames = 120;
        var clock = Stopwatch.StartNew();
        for (int f = 0; f < frames; f++)
        {
            serialGroup.Update(1f / 60, camera, weather);
            var units = serialGroup.Units.Where(u => u.Active && u.Pending > 0).ToArray();
            clock.Restart();
            foreach (var u in units) u.Advance();
            frameSerial += clock.Elapsed.TotalMilliseconds;
            parallelGroup.Update(1f / 60, camera, weather);
            units = [.. parallelGroup.Units.Where(u => u.Active && u.Pending > 0)];
            clock.Restart();
            if (units.Length == 1)
                units[0].Advance();
            else
                Parallel.ForEach(units, u => u.Advance());
            frameParallel += clock.Elapsed.TotalMilliseconds;
        }

        return (frameSerial / frames, frameParallel / frames);
    }
}
