using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// One scenario group's headline numbers from <c>result.json</c>.
/// </summary>
sealed record GroupMetrics(double AvgFps, double P99Ms, double Over33Pct);

/// <summary>
/// One run's <c>result.json</c> (written by <c>--benchmark</c>): the whole document, sent to the reports service as it is, and the fields the
/// page reads from it.
/// </summary>
sealed record BenchmarkResult(JsonObject Document, string Scenario, string Tier, int Version, string Fingerprint, string GameVersion,
    IReadOnlyList<(string Name, GroupMetrics Metrics)> Groups)
{
    /// <summary>
    /// The run's <c>result.json</c> in <paramref name="folder"/>; null when it is missing or not a result.
    /// </summary>
    public static BenchmarkResult? Read(string folder)
    {
        string file = Path.Combine(folder, "result.json");
        try
        {
            return File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject document ? FromDocument(document) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The hardware record the run was measured on, or null.
    /// </summary>
    public JsonObject? Hardware => Document["hardware"] as JsonObject;

    /// <summary>
    /// The result of a parsed document; null when it lacks the tier or the fingerprint.
    /// </summary>
    static BenchmarkResult? FromDocument(JsonObject document)
    {
        if (Text(document, "tier") is not { } tier || Text(document, "fingerprint") is not { } fingerprint) return null;

        int version = document["benchmarkVersion"] is JsonValue v && v.TryGetValue(out int n) ? n : 0;
        string game = document["game"] is JsonObject g && Text(g, "version") is { } gameVersion ? gameVersion : "";
        string scenario = Text(document, "scenario") ?? BenchmarkScenario.Default;
        var groups = new List<(string, GroupMetrics)>();
        if (document["groups"] is JsonObject listed)
        {
            foreach (var (name, node) in listed)
                if (node is JsonObject group && group.Count > 0) groups.Add((name, Metrics(group)));
        }
        return new BenchmarkResult(document, scenario, tier, version, fingerprint, game, groups);
    }

    /// <summary>
    /// A group's average frame rate, 99th percentile frame time and share of frames over 33 ms (NaN where missing).
    /// </summary>
    static GroupMetrics Metrics(JsonObject group) => new(Number(group, "avgFps"), Number(group, "p99Ms"), Number(group, "over33Pct"));

    /// <summary>
    /// A string property, or null.
    /// </summary>
    public static string? Text(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0 ? text : null;

    /// <summary>
    /// A number property, NaN when missing.
    /// </summary>
    static double Number(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue(out double number) ? number : double.NaN;
}
