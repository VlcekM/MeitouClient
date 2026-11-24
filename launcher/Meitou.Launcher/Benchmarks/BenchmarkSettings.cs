namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// What the Benchmark page keeps in <c>launcher.json</c>: this install's random id, the handle results are submitted under and the machine
/// record (docs/launcher.md "Benchmark").
/// </summary>
public sealed class BenchmarkSettings
{
    /// <summary>
    /// A random GUID made once per launcher install; the reports service keys a sender by it (salted) and never shows it.
    /// </summary>
    public string? InstallId { get; set; }

    /// <summary>
    /// The name results are submitted under; may be empty.
    /// </summary>
    public string Handle { get; set; } = "";

    /// <summary>
    /// The auto-generated machine id (hardware slug + per-PC hash), which the benchmarks site groups results by.
    /// </summary>
    public string MachineId { get; set; } = "";

    /// <summary>
    /// The machine record's free notes (cooling, power plan, ...).
    /// </summary>
    public string MachineNotes { get; set; } = "";

    /// <summary>
    /// Whether a session was submitted with the machine record, which then can no longer be edited (the site would split its results).
    /// </summary>
    public bool MachineLocked { get; set; }

    /// <summary>
    /// Whether completed sessions are submitted automatically after a benchmark finishes: null until asked before the first target benchmark.
    /// </summary>
    public bool? AutoSubmit { get; set; }

    /// <summary>
    /// The last <c>--benchmark-probe</c> record as the game wrote it (JSON), null before the first probe.
    /// </summary>
    public string? Hardware { get; set; }

    /// <summary>
    /// <see cref="Hardware"/> parsed; null before the first probe or when it cannot be read.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject? HardwareRecord()
    {
        if (Hardware is not { } json) return null;
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the last probe found ray queries: null before one.
    /// </summary>
    public bool? RayQueries() =>
        HardwareRecord()?["rayQueries"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out bool has) ? has : null;

    /// <summary>
    /// <see cref="InstallId"/>, made (and left for the caller to save) when there is none yet.
    /// </summary>
    public string EnsureInstallId() => InstallId ??= Guid.NewGuid().ToString();
}
