using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meitou.Rendering;

/// <summary>One side of a run: its metrics (<c>gpu:terrain</c>, <c>cpu:total</c>, <c>post:ssao</c>, <c>frame</c>...) in ms, and the main view's counts per frame.</summary>
public sealed class BenchConfig
{
    public string Label { get; set; } = "";
    public int Frames { get; set; }
    public Dictionary<string, MetricStats> Metrics { get; set; } = [];
    public Dictionary<string, double> Counts { get; set; } = [];
    /// <summary>With <c>--bench-tris</c>: what the pipeline statistics queries counted per pass of a frame (mean per frame; key: the stage, or stage/part), in the order the frame runs them.</summary>
    public Dictionary<string, PassStat> Passes { get; set; } = [];
    /// <summary>With <c>--bench-tris</c>: the main view's triangles per frame by screen area, per category (<see cref="TriangleBins.Categories"/>).</summary>
    public Dictionary<string, SizeHistogram> Sizes { get; set; } = [];
}

/// <summary>Differing pixels between the A and the B still (<c>--ab</c>): counts of pixels whose largest channel difference reaches each threshold (1, 4, 12 of 255).</summary>
public sealed class PictureDiffResult
{
    public int Width { get; set; }
    public int Height { get; set; }
    public long Over1 { get; set; }
    public long Over4 { get; set; }
    public long Over12 { get; set; }
    public double MeanDiff { get; set; }
    public int MaxDiff { get; set; }
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    public string Diff { get; set; } = "";
}

public sealed class BenchResult
{
    public Dictionary<string, string> Meta { get; set; } = [];
    /// <summary>"single" or "ab".</summary>
    public string Mode { get; set; } = "single";
    public string? Ab { get; set; }
    public int Period { get; set; } = 1;
    public int Drop { get; set; }
    public Dictionary<string, BenchConfig> Configs { get; set; } = [];
    public Dictionary<string, DeltaStats> Deltas { get; set; } = [];
    public PictureDiffResult? Picture { get; set; }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static BenchResult Load(string path) => JsonSerializer.Deserialize<BenchResult>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path} is not a bench result");

    static string F(double v) => double.IsNaN(v) ? "-" : v.ToString("0.00", CultureInfo.InvariantCulture);
    static string Signed(double v) => v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);

    // Metric groups in print order: (prefix, heading).
    static readonly (string Prefix, string Heading)[] Groups =
    [
        ("frame", "Frame (interval between frames; frames in flight unless the run says serial), ms"), ("gpu:", "GPU ms per stage (timestamps)"),
        ("post:", "Post-processing GPU ms per section"), ("cpu:", "Render thread ms per stage (recording; job threads not included)"),
    ];

    static bool InGroup(string key, string prefix) => prefix == "frame" ? key == "frame" : key.StartsWith(prefix, StringComparison.Ordinal);

    static string Short(string key, string prefix) => prefix == "frame" ? key : key[prefix.Length..];

    /// <summary>The run's tables: per stage mean / median / p95 / p99 / max for each side, and A minus B with its 95% interval (<c>*</c>: excludes zero).</summary>
    public void Print(TextWriter w)
    {
        w.WriteLine($"bench     {string.Join(", ", new[] { "view", "commit", "gpu", "resolution", "upscaler", "motion" }.Where(Meta.ContainsKey).Select(k => $"{k} {Meta[k]}"))}");
        var sides = Configs.Keys.ToList();
        foreach (var (prefix, heading) in Groups)
        {
            var keys = Configs.Values.SelectMany(c => c.Metrics.Keys).Distinct().Where(k => InGroup(k, prefix))
                .Where(k => Configs.Values.Any(c => c.Metrics.TryGetValue(k, out var m) && (m.Mean >= 0.01 || m.Max >= 0.05))).ToList();
            if (keys.Count == 0) continue;
            w.WriteLine();
            w.WriteLine(heading + (Mode == "ab" ? $"   (delta = A - B; {Ab}, period {Period})" : ""));
            var header = new StringBuilder($"{"",-16}");
            foreach (var s in sides) header.Append($"| {s} {"mean",6}{"med",7}{"p95",7}{"p99",7}{"max",8} ");
            if (Mode == "ab") header.Append($"| {"delta",7} {"+-95%",6} {"med",6}");
            w.WriteLine(header);
            foreach (var key in keys)
            {
                var line = new StringBuilder($"{Short(key, prefix),-16}");
                foreach (var s in sides)
                {
                    if (Configs[s].Metrics.TryGetValue(key, out var m)) line.Append($"| {s} {F(m.Mean),6}{F(m.Median),7}{F(m.P95),7}{F(m.P99),7}{F(m.Max),8} ");
                    else line.Append($"| {s} {"-",6}{"",7}{"",7}{"",7}{"",8} ");
                }
                if (Mode == "ab")
                {
                    if (Deltas.TryGetValue(key, out var d)) line.Append($"| {Signed(d.Delta),7} {F(d.Ci95),6} {Signed(d.MedianDelta),6}{(d.Significant ? " *" : "")}");
                    else line.Append($"| {"-",7}");
                }
                w.WriteLine(line.ToString().TrimEnd());
            }
            if (prefix == "frame") PrintFps(w, sides);
        }
        foreach (var s in sides)
        {
            if (Configs[s].Counts.Count == 0) continue;
            w.WriteLine();
            w.WriteLine($"counts per frame, side {s} (main view; objects include the reflection pass): " +
                string.Join(", ", Configs[s].Counts.Select(c => $"{c.Key} {(c.Value >= 1e5 ? (c.Value / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + "M" : c.Value.ToString("N0", CultureInfo.InvariantCulture))}")));
            PrintTriangles(w, s, Configs[s]);
        }
        if (Picture is { } p)
        {
            w.WriteLine();
            w.WriteLine($"picture   A vs B at {p.Width}x{p.Height}: {p.Over1:N0} pixels differ by at least 1/255, {p.Over4:N0} by at least 4, {p.Over12:N0} by at least 12; mean {p.MeanDiff:0.0000}, max {p.MaxDiff}  ({p.Diff})");
        }
    }

    static string Big(double v) => v >= 1e6 ? (v / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + "M" : v >= 1e4 ? (v / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "k" : v.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>The tables of <c>--bench-tris</c> for one side: triangles and fragments per pass, and the main view's triangles by screen area per category.</summary>
    void PrintTriangles(TextWriter w, string side, BenchConfig c)
    {
        if (c.Passes.Count > 0)
        {
            double pixels = Meta.TryGetValue("renderPixels", out var px) && double.TryParse(px, NumberStyles.Float, CultureInfo.InvariantCulture, out var pv) ? pv : 0;
            w.WriteLine();
            w.WriteLine($"triangles per pass, side {side} (pipeline statistics queries, mean per frame over {Meta.GetValueOrDefault("passFrames", "?")} serial frames; a part of a stage is indented under it; 'fs/tri' = fragment invocations per primitive out of the clipper, 'fs/px' = per render pixel)");
            w.WriteLine($"{"pass",-30}{"prims",9}{"vs inv",9}{"clip in",9}{"clip out",9}{"fs inv",9}{"fs/tri",8}{"fs/px",7}");
            foreach (var (key, s) in c.Passes)
            {
                if (s.Primitives < 1 && s.FragmentInvocations < 1 && s.VertexInvocations < 1) continue;
                string name = key.Contains('/') ? "  " + key.Replace("/", " / ") : key;
                w.WriteLine($"{name,-30}{Big(s.Primitives),9}{Big(s.VertexInvocations),9}{Big(s.ClipIn),9}{Big(s.ClipOut),9}{Big(s.FragmentInvocations),9}" +
                    $"{(s.ClipOut > 0 ? s.FragmentsPerTriangle.ToString("0.0", CultureInfo.InvariantCulture) : "-"),8}{(pixels > 0 && s.FragmentInvocations > 0 ? (s.FragmentInvocations / pixels).ToString("0.00", CultureInfo.InvariantCulture) : "-"),7}");
            }
        }
        if (c.Sizes.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"main view triangles by screen area, side {side} (estimated per frame from the shaded samples; the 'sub-pixel' share includes triangles that hit no sample; pixels = samples shaded)");
            w.WriteLine($"{"category",-16}{"triangles",10}" + string.Concat(TriangleBins.Coarse.Select(n => $"{n,10}")) + $"{"< 1 px %",10}{"pixels",9}{"px < 4 px %",12}");
            SizeHistogram total = new();
            foreach (var (name, h) in c.Sizes)
            {
                total.Add(h);
                PrintSizeRow(w, name, h);
            }
            if (c.Sizes.Count > 1) PrintSizeRow(w, "all", total);
        }
    }

    static void PrintSizeRow(TextWriter w, string name, SizeHistogram h)
    {
        double small = h.TotalPixels > 0 ? 100 * (h.Pixels[0] + h.Pixels[1]) / h.TotalPixels : 0;
        w.WriteLine($"{name,-16}{Big(h.TotalTriangles),10}" + string.Concat(h.Triangles.Select(t => $"{Big(t),10}")) +
            $"{(100 * h.SubPixelShare).ToString("0.0", CultureInfo.InvariantCulture),10}{Big(h.TotalPixels),9}{small.ToString("0.0", CultureInfo.InvariantCulture),12}");
    }

    void PrintFps(TextWriter w, List<string> sides)
    {
        var line = new StringBuilder(string.Format(CultureInfo.InvariantCulture, "{0,-16}", "fps"));
        foreach (var s in sides)
            if (Configs[s].Metrics.TryGetValue("frame", out var m) && m.Mean > 0 && m.Median > 0 && m.P99 > 0)
                line.Append(string.Format(CultureInfo.InvariantCulture, "| {0} {1,6:0.0}{2,7:0.0}{3,7}{4,7:0.0}{5,8} ", s, 1000 / m.Mean, 1000 / m.Median, "", 1000 / m.P99, ""));
        w.WriteLine(line.ToString().TrimEnd() + "   (1000 / mean, of the median frame, of the p99 frame)");
        if (Meta.TryGetValue("frameMode", out var mode)) w.WriteLine("frame mode: " + mode);
    }

    /// <summary><c>--bench-compare a.json b.json</c>: the first side of each file next to each other, B - A with an unpaired 95% interval from the spreads.</summary>
    public static void Compare(string fileA, string fileB, TextWriter w)
    {
        var a = Load(fileA);
        var b = Load(fileB);
        var ca = a.Configs.Values.First();
        var cb = b.Configs.Values.First();
        w.WriteLine($"compare   A = {fileA} ({Describe(a)})");
        w.WriteLine($"          B = {fileB} ({Describe(b)})");
        foreach (var key in new[] { "view", "resolution", "upscaler", "motion", "gpu" })
            if (a.Meta.GetValueOrDefault(key) != b.Meta.GetValueOrDefault(key)) w.WriteLine($"warning   {key} differs: A {a.Meta.GetValueOrDefault(key)}, B {b.Meta.GetValueOrDefault(key)}");
        foreach (var (prefix, heading) in Groups)
        {
            var keys = ca.Metrics.Keys.Where(k => InGroup(k, prefix))
                .Where(k => cb.Metrics.ContainsKey(k) && (ca.Metrics[k].Mean >= 0.01 || cb.Metrics[k].Mean >= 0.01)).ToList();
            if (keys.Count == 0) continue;
            w.WriteLine();
            w.WriteLine(heading + "   (delta = B - A)");
            w.WriteLine($"{"",-16}| {"A mean",7}{"A p95",7}{"A max",8} | {"B mean",7}{"B p95",7}{"B max",8} | {"delta",7} {"+-95%",6} {"%",7}");
            foreach (var key in keys)
            {
                var ma = ca.Metrics[key];
                var mb = cb.Metrics[key];
                var (delta, se) = BenchStats.Unpaired(ma, mb);
                double ci = 1.96 * se;
                string pct = ma.Mean > 0.01 ? (100 * delta / ma.Mean).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%" : "";
                w.WriteLine($"{Short(key, prefix),-16}| {F(ma.Mean),7}{F(ma.P95),7}{F(ma.Max),8} | {F(mb.Mean),7}{F(mb.P95),7}{F(mb.Max),8} | " +
                    $"{Signed(delta),7} {F(ci),6} {pct,7}{(Math.Abs(delta) > ci && Math.Abs(delta) >= BenchStats.MinMeaningful ? " *" : "")}");
            }
        }
        foreach (var key in ca.Passes.Keys.Where(cb.Passes.ContainsKey).Where(k => !k.Contains('/')))
            w.WriteLine($"tris      {key}: primitives A {Big(ca.Passes[key].Primitives)}, B {Big(cb.Passes[key].Primitives)}; fragment invocations A {Big(ca.Passes[key].FragmentInvocations)}, B {Big(cb.Passes[key].FragmentInvocations)}");
        foreach (var key in ca.Sizes.Keys.Where(cb.Sizes.ContainsKey))
            w.WriteLine($"sizes     {key}: triangles A {Big(ca.Sizes[key].TotalTriangles)}, B {Big(cb.Sizes[key].TotalTriangles)}; under 1 px A {100 * ca.Sizes[key].SubPixelShare:0.0}%, B {100 * cb.Sizes[key].SubPixelShare:0.0}%");
        foreach (var key in ca.Counts.Keys.Where(cb.Counts.ContainsKey))
            w.WriteLine($"count     {key}: A {ca.Counts[key]:N0}, B {cb.Counts[key]:N0}");
    }

    static string Describe(BenchResult r) => $"{r.Meta.GetValueOrDefault("commit")}, {r.Meta.GetValueOrDefault("view")}, side {r.Configs.Keys.First()}, {r.Configs.Values.First().Frames} frames";
}
