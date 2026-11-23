using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meitou.Game.Host;

namespace Meitou.Game.DevTools.Benchmark;

/// <summary>
/// The launcher benchmark's result.json (docs/bench.md "Launcher benchmark"): the run's spec, build, machine and the scenario's groups' metrics,
/// read back from the play bench's frames.csv as the old benchmark kit read it (<see cref="BenchmarkSpec"/>'s metric definitions).
/// </summary>
static class BenchmarkResult
{
    /// <summary>
    /// The frame time column.
    /// </summary>
    const string FrameColumn = "frame_ms";

    /// <summary>
    /// The slow-world tick's column (TickProfile's "slow world" phase).
    /// </summary>
    const string SlowWorldColumn = "sim_slow-world";

    /// <summary>
    /// What the run was, for <see cref="Write"/>: its scenario and tier, the hash of the settings in effect, the machine, the frame size and
    /// when the script started.
    /// </summary>
    public sealed record Run(BenchmarkScenario Scenario, int Tier, string Applied, HardwareRecord Hardware, int Width, int Height, DateTime StartedAt);

    /// <summary>
    /// Reads <paramref name="dir"/>'s frames.csv and writes result.json beside it.
    /// </summary>
    public static void Write(string dir, Run run)
    {
        var (columns, rows) = ReadFrames(Path.Combine(dir, "frames.csv"));
        var groups = new JsonObject();
        foreach (var (name, segments) in run.Scenario.Groups)
            groups[name] = Group(columns, [.. rows.Where(r => segments.Contains(r.Segment))]);

        var result = new JsonObject
        {
            ["schema"] = BenchmarkSpec.Schema,
            ["benchmarkVersion"] = BenchmarkSpec.Version,
            ["fingerprint"] = BenchmarkSpec.Fingerprint(run.Scenario),
            ["applied"] = run.Applied,
            ["tier"] = BenchmarkSpec.TierName(run.Tier),
            ["scenario"] = run.Scenario.Id,
            ["game"] = new JsonObject { ["version"] = BuildVersion.Tag.TrimStart('v'), ["commit"] = ShortCommit() },
            ["save"] = run.Scenario.Save,
            ["resolution"] = string.Create(CultureInfo.InvariantCulture, $"{run.Width}x{run.Height}"),
            ["startedAt"] = run.StartedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["hardware"] = run.Hardware.ToJson(),
            ["groups"] = groups,
        };
        string path = Path.Combine(dir, "result.json");
        File.WriteAllText(path, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"bench     result: {Path.GetFullPath(path)}");
    }

    /// <summary>
    /// The build's commit, its first eight characters.
    /// </summary>
    static string ShortCommit()
    {
        string commit = BuildVersion.Commit;
        return commit.Contains(' ') ? commit : commit[..Math.Min(8, commit.Length)];
    }

    /// <summary>
    /// One frames.csv row: its segment and its numbers by column (NaN where empty or not a number).
    /// </summary>
    sealed record Row(string Segment, double[] Values);

    /// <summary>
    /// frames.csv's columns (by index) and rows; settle's rows are left out.
    /// </summary>
    static (Dictionary<string, int> Columns, List<Row> Rows) ReadFrames(string path)
    {
        using var reader = new StreamReader(path);
        var header = (reader.ReadLine() ?? "").Split(',');
        var columns = header.Select((name, i) => (name, i)).ToDictionary(c => c.name, c => c.i);
        int segment = columns["segment"];
        var rows = new List<Row>();
        while (reader.ReadLine() is { } line)
        {
            var cells = line.Split(',');
            if (cells.Length != header.Length || cells[segment] == BenchmarkSpec.SettleSegment) continue;

            rows.Add(new(cells[segment], [.. cells.Select(Number)]));
        }

        foreach (var required in new[] { FrameColumn, SlowWorldColumn, "vram_mb", "working_set_mb" })
        {
            if (!columns.ContainsKey(required)) throw new InvalidDataException($"{path} has no {required} column");
        }

        return (columns, rows);
    }

    /// <summary>
    /// A cell as a number (invariant culture); NaN when empty or not one.
    /// </summary>
    static double Number(string cell) =>
        double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;

    /// <summary>
    /// A group's metrics over <paramref name="rows"/> (only its frame count when it has none).
    /// </summary>
    static JsonObject Group(Dictionary<string, int> columns, List<Row> rows)
    {
        var frames = Column(rows, columns[FrameColumn]);
        if (frames.Count == 0) return new JsonObject { ["frames"] = 0 };

        var slowWorld = Column(rows, columns[SlowWorldColumn]);
        var vram = Column(rows, columns["vram_mb"]);
        var sysmem = Column(rows, columns["working_set_mb"]);
        var scenario = new JsonObject
        {
            ["frames"] = frames.Count,
            ["seconds"] = Math.Round(frames.Sum() / 1000, 1),
            ["avgFps"] = Math.Round(1000 / frames.Average(), 1),
            ["p50Ms"] = Ms(Percentile(frames, 50)),
            ["p99Ms"] = Ms(Percentile(frames, 99)),
            ["maxMs"] = Ms(frames[^1]),
            ["over33Pct"] = Math.Round(100.0 * frames.Count(f => f > 33) / frames.Count, 2),
            ["over100"] = frames.Count(f => f > 100),
            ["slowWorldP99Ms"] = slowWorld.Count > 0 ? Ms(Percentile(slowWorld, 99)) : null,
            ["slowWorldMaxMs"] = slowWorld.Count > 0 ? Ms(slowWorld[^1]) : null,
            ["vramPeakMb"] = vram.Count > 0 ? Math.Round(vram[^1]) : null,
            ["sysmemPeakMb"] = sysmem.Count > 0 ? Math.Round(sysmem[^1]) : null,
            ["stages"] = Stages(columns, rows),
        };
        return scenario;
    }

    /// <summary>
    /// The p50 and p99 of every <c>cpu_*</c> and <c>gpu_*</c> column with a finite value, keyed with '_' for '-' and without "_ms"
    /// (<c>cpu_upd_terrain</c>, <c>gpu_total</c>).
    /// </summary>
    static JsonObject Stages(Dictionary<string, int> columns, List<Row> rows)
    {
        var stages = new JsonObject();
        foreach (var (name, index) in columns.Where(c => c.Key.StartsWith("cpu_", StringComparison.Ordinal)
            || c.Key.StartsWith("gpu_", StringComparison.Ordinal)))
        {
            var values = Column(rows, index);
            if (values.Count == 0) continue;

            string key = name.Replace('-', '_');
            if (key.EndsWith("_ms", StringComparison.Ordinal)) key = key[..^3];
            stages[key] = new JsonObject { ["p50Ms"] = Stage(Percentile(values, 50)), ["p99Ms"] = Stage(Percentile(values, 99)) };
        }

        return stages;
    }

    /// <summary>
    /// The finite values of column <paramref name="index"/>, sorted.
    /// </summary>
    static List<double> Column(List<Row> rows, int index) =>
        [.. rows.Select(r => r.Values[index]).Where(double.IsFinite).Order()];

    /// <summary>
    /// The value at percentile <paramref name="p"/> of the sorted <paramref name="sorted"/>: index round(p/100*(n-1)), rounded half to even as
    /// Python's round.
    /// </summary>
    static double Percentile(List<double> sorted, double p) =>
        sorted[Math.Min(sorted.Count - 1, (int)Math.Round(p / 100 * (sorted.Count - 1), MidpointRounding.ToEven))];

    /// <summary>
    /// A frame or tick time, to 0.01 ms.
    /// </summary>
    static double Ms(double value) => Math.Round(value, 2);

    /// <summary>
    /// A stage time, to 0.001 ms.
    /// </summary>
    static double Stage(double value) => Math.Round(value, 3);
}
