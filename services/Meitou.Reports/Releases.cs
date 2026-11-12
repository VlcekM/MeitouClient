using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Meitou.Reports;

/// <summary>
/// A GitHub release as the releases API lists it: its tag, whether it is a prerelease or a draft, and when it was published.
/// </summary>
sealed record GithubRelease(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("published_at")] DateTimeOffset? Published);

/// <summary>
/// The version (no <c>v</c>) of the newest full game release and of the newest game release of any kind, nightlies included; null for none.
/// </summary>
sealed record Latest(string? Release, string? Any);

/// <summary>
/// The newest game releases, for the game's feedback form: GitHub is read at most every 10 minutes, so players never meet its rate limit.
/// </summary>
sealed class Releases(HttpClient http, Config config, ILogger<Releases> log)
{
    /// <summary>
    /// How long a fetch is reused before GitHub is read again.
    /// </summary>
    static readonly TimeSpan ReuseFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// One fetch at a time.
    /// </summary>
    readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// When the releases were last fetched.
    /// </summary>
    DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// The answer from the last fetch; none known before one.
    /// </summary>
    Latest _latest = new(null, null);

    /// <summary>
    /// The newest game versions (<c>v*</c> tags, the version after the <c>v</c>), fetched again when older than <see cref="ReuseFor"/>; a failed
    /// fetch keeps the old answer and retries in five minutes.
    /// </summary>
    public async Task<Latest> Latest()
    {
        if (DateTimeOffset.UtcNow - _fetchedAt < ReuseFor) return _latest;
        await _gate.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow - _fetchedAt < ReuseFor) return _latest;
            _latest = await Fetch();
            _fetchedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("Releases not read: {Error}", e.Message);
            _fetchedAt = DateTimeOffset.UtcNow - ReuseFor + TimeSpan.FromMinutes(5);
        }
        finally
        {
            _gate.Release();
        }
        return _latest;
    }

    /// <summary>
    /// Reads the releases from GitHub: the newest full release (not a pre-release) and the newest of any kind, drafts left out.
    /// </summary>
    async Task<Latest> Fetch()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{config.Repo}/releases?per_page=100");
        request.Headers.UserAgent.ParseAdd("meitou-reports");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var game = (await response.Content.ReadFromJsonAsync(ReportsJson.Default.ListGithubRelease) ?? [])
            .Where(r => r.Tag.StartsWith('v') && !r.Draft && r.Published is not null)
            .ToList();
        var newestFull = game.Where(r => !r.Prerelease).MaxBy(r => r.Published);
        return new Latest(newestFull?.Tag[1..], game.MaxBy(r => r.Published)?.Tag[1..]);
    }
}
