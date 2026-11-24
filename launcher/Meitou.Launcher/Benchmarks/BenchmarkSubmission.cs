using System.Text.Json.Nodes;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// The body of <c>POST /bench</c> for one session: <c>{ installId, handle, machine, platform, target, runs: [result, result], summary }</c>,
/// the runs' <c>result.json</c> sent as the game wrote them.
/// </summary>
static class BenchmarkSubmission
{
    /// <summary>
    /// The hardware fields the machine record copies from the session's last run (its probe record).
    /// </summary>
    static readonly string[] HardwareFields = ["cpu", "gpu", "ramGb", "os", "osFamily"];

    /// <summary>
    /// The body for <paramref name="session"/> of build <paramref name="target"/> (its version, the tag without <c>v</c>) on
    /// <paramref name="platform"/>; <paramref name="resolution"/> is the primary screen's, for the machine record.
    /// </summary>
    public static JsonObject Body(BenchmarkSession session, BenchmarkSettings settings, string platform, string target, string resolution)
    {
        var runs = new JsonArray();
        foreach (var run in session.Runs) runs.Add(run.Document.DeepClone());
        return new JsonObject
        {
            ["installId"] = settings.EnsureInstallId(),
            ["handle"] = settings.Handle.Trim(),
            ["machine"] = Machine(session, settings, resolution),
            ["platform"] = platform,
            ["target"] = target,
            ["runs"] = runs,
            ["summary"] = session.Summary,
        };
    }

    /// <summary>
    /// The machine record: the generated id, optional notes, the hardware of the session's last run and the screen's resolution.
    /// </summary>
    static JsonObject Machine(BenchmarkSession session, BenchmarkSettings settings, string resolution)
    {
        var machine = new JsonObject { ["id"] = settings.MachineId.Trim(), ["name"] = "" };
        var hardware = session.Runs[^1].Hardware;
        foreach (string field in HardwareFields) machine[field] = hardware?[field]?.DeepClone();
        machine["resolution"] = resolution;
        machine["notes"] = settings.MachineNotes.Trim();
        return machine;
    }
}
