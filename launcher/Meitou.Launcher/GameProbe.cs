using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Meitou.Launcher;

/// <summary>
/// Runs the game for a report it writes and exits: the Graphics page's preset detection and the Benchmark page's tier recommendation.
/// </summary>
static class GameProbe
{
    /// <summary>
    /// Starts <paramref name="start"/> and waits for it to exit, killing it (and what it started) after <paramref name="limit"/> or when
    /// <paramref name="ct"/> is cancelled; the game's exit code.
    /// </summary>
    public static async Task<int> RunAsync(ProcessStartInfo start, TimeSpan limit, CancellationToken ct = default)
    {
        using var probe = Process.Start(start) ?? throw new InvalidOperationException("the game did not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try
        {
            await probe.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            probe.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("the game did not finish in time");
        }
        return probe.ExitCode;
    }

    /// <summary>
    /// The preset and the summary line a <c>--recommend-preset</c> report in <paramref name="file"/> holds; the preset is null when it names none.
    /// </summary>
    public static async Task<(string? Preset, string Summary)> ReadPresetAsync(string file)
    {
        var report = JsonNode.Parse(await File.ReadAllTextAsync(file)) as JsonObject;
        string? preset = report?["preset"] is JsonValue pv && pv.TryGetValue(out string? name) ? name : null;
        string summary = report?["summary"] is JsonValue sv && sv.TryGetValue(out string? line) ? line : preset ?? "";
        return (preset, summary);
    }
}
