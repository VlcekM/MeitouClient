using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// A build that counts for comparisons, as the reports service lists it: every full release and each scheduled daily nightly.
/// </summary>
sealed record BenchmarkTarget(string Tag, string Version, string Kind);

/// <summary>
/// What the reports service answered a submission: its id for the stored session, or the reason it refused.
/// </summary>
sealed record SubmitAnswer(string? Id, string? Error);

/// <summary>
/// The benchmark endpoints of the reports service (<c>services/Meitou.Reports</c>, the game's <c>FeedbackSender.ServiceUrl</c>).
/// </summary>
sealed class BenchmarkService(HttpClient http)
{
    /// <summary>
    /// The reports service.
    /// </summary>
    const string ServiceUrl = "https://gear-pastry-chorus.cloudovna.app";

    /// <summary>
    /// The longest a request may take.
    /// </summary>
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The targets, newest first (<c>GET /bench/targets</c>).
    /// </summary>
    public async Task<List<BenchmarkTarget>> TargetsAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var response = await http.GetAsync($"{ServiceUrl}/bench/targets", timeout.Token);
        response.EnsureSuccessStatusCode();
        var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token)) as JsonObject;
        return [.. (answer?["targets"] as JsonArray ?? []).OfType<JsonObject>().Select(ReadTarget).OfType<BenchmarkTarget>()];
    }

    /// <summary>
    /// Posts a session (<c>POST /bench</c>): <c>{ installId, handle, machine, platform, target, runs, summary }</c>.
    /// </summary>
    public async Task<SubmitAnswer> SubmitAsync(JsonObject body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"{ServiceUrl}/bench", content, timeout.Token);
        string text = await response.Content.ReadAsStringAsync(timeout.Token);
        var answer = Parse(text);
        if (response.IsSuccessStatusCode) return new SubmitAnswer(answer?["id"]?.ToJsonString().Trim('"') ?? "?", null);

        string? error = answer is not null ? BenchmarkResult.Text(answer, "error") : null;
        return new SubmitAnswer(null, error ?? Unexplained(response.StatusCode, response.ReasonPhrase, text));
    }

    /// <summary>
    /// A target entry; null when it lacks the tag or the version.
    /// </summary>
    static BenchmarkTarget? ReadTarget(JsonObject entry) =>
        BenchmarkResult.Text(entry, "tag") is { } tag && BenchmarkResult.Text(entry, "version") is { } version
            ? new BenchmarkTarget(tag, version, BenchmarkResult.Text(entry, "kind") ?? "")
            : null;

    /// <summary>
    /// <paramref name="text"/> as a JSON object, null when it is none (a proxy's error page).
    /// </summary>
    static JsonObject? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// An answer without an <c>error</c>: its status and the start of its text.
    /// </summary>
    static string Unexplained(HttpStatusCode status, string? reason, string text)
    {
        string start = text.Length > 300 ? text[..300] : text;
        return string.Join(' ', new[] { ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), reason, start.Trim() }
            .Where(part => !string.IsNullOrEmpty(part)));
    }
}
