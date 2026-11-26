using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Meitou.Reports;

/// <summary>
/// A GitHub release as the releases API lists it: its tag, whether it is a prerelease or a draft, when it was published and its notes.
/// </summary>
sealed record GithubRelease(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("published_at")] DateTimeOffset? Published,
    [property: JsonPropertyName("body")] string? Body = null);

/// <summary>
/// The version (no <c>v</c>) of the newest full game release and of the newest game release of any kind, nightlies included; null for none.
/// </summary>
sealed record Latest(string? Release, string? Any);

/// <summary>
/// A game build benchmarks may be submitted for: its tag, its version (the tag without <c>v</c>), <c>release</c> or <c>daily</c>, and when it was
/// published.
/// </summary>
sealed record BenchTarget(string Tag, string Version, string Kind, DateTimeOffset PublishedAt);

/// <summary>
/// <c>GET /bench/targets</c>: the benchmark targets, newest first.
/// </summary>
sealed record BenchTargets(List<BenchTarget> Targets);

/// <summary>
/// The game releases, for the game's feedback form and the benchmark targets: GitHub is read at most every 10 minutes, so players never meet its
/// rate limit.
/// </summary>
sealed class Releases(HttpClient http, Config config, GithubAuth github, ILogger<Releases> log)
{
    /// <summary>
    /// How long a fetch is reused before GitHub is read again.
    /// </summary>
    static readonly TimeSpan ReuseFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The most pages of 100 releases read in one fetch (launcher releases share the list).
    /// </summary>
    const int MaxPages = 10;

    /// <summary>
    /// How the notes of a nightly from the scheduled 17:00 UTC run start (nightly.yml); manual and 05:00 runs say <c>Nightly build</c>.
    /// </summary>
    const string DailyNotes = "Daily build";

    /// <summary>
    /// One fetch at a time.
    /// </summary>
    readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// When the releases were last fetched.
    /// </summary>
    DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// The game releases (<c>v*</c> tags, published, no drafts) from the last fetch, newest first; none known before one.
    /// </summary>
    List<GithubRelease> _game = [];

    /// <summary>
    /// The newest game versions (the version after the <c>v</c>): the newest full release (not a pre-release) and the newest of any kind.
    /// </summary>
    public async Task<Latest> Latest()
    {
        var game = await Game();
        return new Latest(game.FirstOrDefault(r => !r.Prerelease)?.Tag[1..], game.FirstOrDefault()?.Tag[1..]);
    }

    /// <summary>
    /// The benchmark targets, newest first: every full release and every nightly whose notes start with <see cref="DailyNotes"/>; null while no
    /// game release is known (GitHub not read yet or unreachable).
    /// </summary>
    public async Task<List<BenchTarget>?> Targets()
    {
        var game = await Game();
        if (game.Count == 0) return null;

        return [.. game.Select(AsTarget).OfType<BenchTarget>()];
    }

    /// <summary>
    /// A release as a benchmark target, null when it is not one.
    /// </summary>
    static BenchTarget? AsTarget(GithubRelease r)
    {
        bool daily = r.Prerelease && r.Body is { } body && body.StartsWith(DailyNotes, StringComparison.Ordinal);
        if (r.Prerelease && !daily) return null;

        return new BenchTarget(r.Tag, r.Tag[1..], daily ? "daily" : "release", r.Published!.Value);
    }

    /// <summary>
    /// The game releases, fetched again when older than <see cref="ReuseFor"/>; a failed fetch keeps the old list and retries in five minutes.
    /// </summary>
    async Task<List<GithubRelease>> Game()
    {
        if (DateTimeOffset.UtcNow - _fetchedAt < ReuseFor) return _game;

        await _gate.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow - _fetchedAt < ReuseFor) return _game;

            _game = await Fetch();
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

        return _game;
    }

    /// <summary>
    /// Reads every release from GitHub (up to <see cref="MaxPages"/> pages) and keeps the game's, drafts left out, newest first.
    /// </summary>
    async Task<List<GithubRelease>> Fetch()
    {
        string? token = github.Configured ? await github.Token() : null;
        var all = new List<GithubRelease>();
        for (int page = 1; page <= MaxPages; page++)
        {
            var batch = await FetchPage(page, token);
            all.AddRange(batch);
            if (batch.Count < 100) break;
        }

        return [.. all
            .Where(r => r.Tag.StartsWith('v') && !r.Draft && r.Published is not null)
            .OrderByDescending(r => r.Published)];
    }

    /// <summary>
    /// One page of 100 releases, authorised with <paramref name="token"/> when there is one (a higher rate limit).
    /// </summary>
    async Task<List<GithubRelease>> FetchPage(int page, string? token)
    {
        string url = $"https://api.github.com/repos/{config.Repo}/releases?per_page=100&page={page}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("meitou-reports");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync(ReportsJson.Default.ListGithubRelease) ?? [];
    }
}
