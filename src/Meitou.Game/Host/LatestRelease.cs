using System.Text.Json.Nodes;
using Meitou.Content;

namespace Meitou.Game.Host;

/// <summary>
/// Whether a newer Meitou is out than the running one, for the feedback form (docs/feedback.md): the reports service's <c>GET
/// /latest</c> (it reads GitHub's releases itself, so players never meet GitHub's rate limit), asked at most once per <see cref="Reuse"/>. A nightly
/// compares with the newest game release of any kind, a release with the newest release. Any failure (offline, an untagged build) is no answer.
/// </summary>
static class LatestRelease
{
    /// <summary>
    /// How long an answer is reused before the service is asked again.
    /// </summary>
    static readonly TimeSpan Reuse = TimeSpan.FromMinutes(15);
    /// <summary>
    /// The client for the service, with a 10 s timeout.
    /// </summary>
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    /// <summary>
    /// The last question to the service: the newest release's tag and the newest of any kind; null inside for no answer.
    /// </summary>
    static Task<(string? Release, string? Any)?>? s_fetched;
    /// <summary>
    /// When <see cref="s_fetched"/> was asked.
    /// </summary>
    static DateTime s_fetchedAt;

    /// <summary>
    /// The tag of the newest game release newer than <paramref name="runningTag"/> (<c>v0.16.1-nightly.20261107</c>), or null.
    /// </summary>
    public static async Task<string?> NewerThan(string runningTag)
    {
        if (!runningTag.StartsWith('v') || !CodeModVersion.TryParse(runningTag[1..], out var running)) return null;
        if (s_fetched is null || DateTime.UtcNow - s_fetchedAt > Reuse)
        {
            s_fetchedAt = DateTime.UtcNow;
            s_fetched = Fetch();
        }

        if (await s_fetched is not { } latest) return null;
        string? newest = running.IsPrerelease ? latest.Any : latest.Release;
        return newest is not null && CodeModVersion.TryParse(newest, out var version) && version.CompareTo(running) > 0 ? "v" + newest : null;
    }

    /// <summary>
    /// The service's newest full release and newest release of any kind (versions without the <c>v</c>), or null on any failure.
    /// </summary>
    static async Task<(string? Release, string? Any)?> Fetch()
    {
        try
        {
            var answer = JsonNode.Parse(await Http.GetStringAsync($"{FeedbackSender.ServiceUrl}/latest"));
            return (answer?["release"]?.GetValue<string>(), answer?["any"]?.GetValue<string>());
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}