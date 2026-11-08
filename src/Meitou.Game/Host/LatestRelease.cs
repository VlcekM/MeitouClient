using System.Text.Json.Nodes;
using Meitou.Content;

namespace Meitou.Game.Host;

/// <summary>
/// Whether a newer Meitou is out than the running one, for the feedback form (docs/play-reports.md "Feedback"): the reports service's
/// <c>GET /latest</c> (it reads GitHub's releases itself, so players never meet GitHub's rate limit), asked at most once per <see cref="Reuse"/>.
/// A nightly compares with the newest game release of any kind, a release with the newest release. Any failure (offline, an untagged build) is no answer.
/// </summary>
static class LatestRelease
{
    static readonly TimeSpan Reuse = TimeSpan.FromMinutes(15);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static Task<(string? Release, string? Any)?>? fetched;
    static DateTime fetchedAt;

    /// <summary>The tag of the newest game release newer than <paramref name="runningTag"/> (<c>v0.16.1-nightly.20261107</c>), or null.</summary>
    public static async Task<string?> NewerThan(string runningTag)
    {
        if (!runningTag.StartsWith('v') || !CodeModVersion.TryParse(runningTag[1..], out var running)) return null;
        if (fetched is null || DateTime.UtcNow - fetchedAt > Reuse)
        {
            fetchedAt = DateTime.UtcNow;
            fetched = Fetch();
        }

        if (await fetched is not { } latest) return null;
        string? newest = running.IsPrerelease ? latest.Any : latest.Release;
        return newest is not null && CodeModVersion.TryParse(newest, out var version) && version.CompareTo(running) > 0 ? "v" + newest : null;
    }

    /// <summary>The service's newest full release and newest release of any kind (versions without the <c>v</c>), or null on any failure.</summary>
    static async Task<(string? Release, string? Any)?> Fetch()
    {
        try
        {
            var answer = JsonNode.Parse(await Http.GetStringAsync($"{FeedbackSender.ServiceUrl}/latest"));
            return (answer?["release"]?.GetValue<string>(), answer?["any"]?.GetValue<string>());
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
