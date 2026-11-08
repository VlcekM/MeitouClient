using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Meitou.Reports;

/// <summary>A roadmap row as the site's <c>/roadmap.json</c> lists it.</summary>
sealed record RoadmapRow(string Id, string System, string Category, string Status);

sealed record RoadmapFile(List<RoadmapRow> Rows);

sealed record GithubRelease(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("published_at")] DateTimeOffset? Published);

/// <summary>
/// Which reports count: none from before <see cref="Since"/>, and a Broken one only while no full release came out after its build
/// (<see cref="Builds"/> and <see cref="Published"/> pair each game release's version with its publish time; a build that is not a
/// release, such as a self-built one, is dated by the report itself). <see cref="NewestFull"/> is null before any full release.
/// <see cref="Latest"/>: the newest game versions, for the game's feedback form.
/// </summary>
sealed record Window(DateTimeOffset Since, DateTimeOffset? NewestFull, string[] Builds, DateTimeOffset[] Published, Latest Latest);

/// <summary>The version (no <c>v</c>) of the newest full game release and of the newest game release of any kind, nightlies included; null for none.</summary>
sealed record Latest(string? Release, string? Any);

/// <summary>
/// The roadmap's rows (from the site, so reports can only name a real row) and the report window: reports older than two full game
/// releases stop counting, and a Broken report stops once a full release is newer than its build (the fix ships, or players report again).
/// </summary>
sealed partial class Roadmap(HttpClient http, Config config, ILogger<Roadmap> log)
{
    static readonly TimeSpan RowsFor = TimeSpan.FromMinutes(10), ReleasesFor = TimeSpan.FromMinutes(10);
    readonly SemaphoreSlim _gate = new(1, 1);
    Dictionary<string, RoadmapRow> _rows = [];
    DateTimeOffset _rowsAt = DateTimeOffset.MinValue, _releasesAt = DateTimeOffset.MinValue;
    Window _window = new(DateTimeOffset.UtcNow.AddDays(-90), null, [], [], new(null, null));

    public async Task<IReadOnlyDictionary<string, RoadmapRow>> Rows()
    {
        if (DateTimeOffset.UtcNow - _rowsAt < RowsFor) return _rows;
        await _gate.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow - _rowsAt < RowsFor) return _rows;
            string text = await http.GetStringAsync(config.RoadmapUrl);
            var list = config.RoadmapUrl.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                ? System.Text.Json.JsonSerializer.Deserialize(text, ReportsJson.Default.RoadmapFile)?.Rows ?? []
                : ParseSource(text);
            if (list.Count > 0) _rows = list.ToDictionary(r => r.Id);
            _rowsAt = DateTimeOffset.UtcNow;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("Roadmap rows from {Url} not read: {Error}", config.RoadmapUrl, e.Message);
            _rowsAt = DateTimeOffset.UtcNow - RowsFor + TimeSpan.FromMinutes(1);   // retry in a minute, keep the old list
        }
        finally { _gate.Release(); }
        return _rows;
    }

    /// <summary>
    /// The rows of <c>site/src/data/roadmap.ts</c> itself: each category line <c>{ id: "...", name: "..."</c> is followed by its row lines
    /// <c>{ id: "...", system: "...", status: "..."</c>. Read this way so the service needs neither the site nor a TypeScript runtime.
    /// </summary>
    public static List<RoadmapRow> ParseSource(string source)
    {
        var list = new List<RoadmapRow>();
        string category = "";
        foreach (var line in source.Split('\n'))
        {
            if (CategoryLine().Match(line) is { Success: true } c) category = c.Groups[1].Value;
            else if (RowLine().Match(line) is { Success: true } r)
                list.Add(new(r.Groups[1].Value, System.Text.RegularExpressions.Regex.Unescape(r.Groups[2].Value), category, r.Groups[3].Value));
        }
        return list;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\{\s*id:\s*""([^""]+)"",\s*name:\s*""")]
    private static partial System.Text.RegularExpressions.Regex CategoryLine();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\{\s*id:\s*""([^""]+)"",\s*system:\s*""((?:[^""\\]|\\.)*)"",\s*status:\s*""(\w+)""")]
    private static partial System.Text.RegularExpressions.Regex RowLine();

    /// <summary>
    /// The report window from the game releases (<c>v*</c> tags, the version after the <c>v</c>): reports made before the publish time of
    /// the second newest full release (not a pre-release) stop counting, and a Broken one stops once the newest full release is newer than its build.
    /// </summary>
    public async Task<Window> Window()
    {
        if (DateTimeOffset.UtcNow - _releasesAt < ReleasesFor) return _window;
        await _gate.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow - _releasesAt < ReleasesFor) return _window;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{config.Repo}/releases?per_page=100");
            request.Headers.UserAgent.ParseAdd("meitou-reports");
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var game = (await response.Content.ReadFromJsonAsync(ReportsJson.Default.ListGithubRelease) ?? [])
                .Where(r => r.Tag.StartsWith('v') && !r.Draft && r.Published is not null).ToList();
            var full = game.Where(r => !r.Prerelease).OrderByDescending(r => r.Published).ToList();
            var latest = new Latest(full.FirstOrDefault()?.Tag[1..], game.MaxBy(r => r.Published)?.Tag[1..]);
            _window = new(full.Count >= 2 ? full[1].Published!.Value : DateTimeOffset.MinValue, full.FirstOrDefault()?.Published,
                game.Select(r => r.Tag[1..]).ToArray(), game.Select(r => r.Published!.Value).ToArray(), latest);
            _releasesAt = DateTimeOffset.UtcNow;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("Releases not read: {Error}", e.Message);
            _releasesAt = DateTimeOffset.UtcNow - ReleasesFor + TimeSpan.FromMinutes(5);
        }
        finally { _gate.Release(); }
        return _window;
    }

    /// <summary>
    /// Status changes the reports suggest, for a person to approve: Untested to Works on 3 distinct reporters or 1 trusted tester; any Broken
    /// report flags the row for a look (a reproduced one with steps is what moves Works to Bug, and that is a person's call).
    /// </summary>
    public static List<Proposal> Propose(IReadOnlyDictionary<string, RoadmapRow> rows, IEnumerable<RowSummary> summary)
    {
        var list = new List<Proposal>();
        foreach (var s in summary)
        {
            if (!rows.TryGetValue(s.Row, out var row)) continue;
            if (row.Status == "untested" && (s.Works >= 3 || s.TrustedWorks))
                list.Add(new(row.Id, row.System, row.Status, "works", s.TrustedWorks ? "a trusted tester saw it work" : $"{s.Works} people saw it work"));
            if (s.Broken > 0)
                list.Add(new(row.Id, row.System, row.Status, "look", $"{s.Broken} broken report{(s.Broken == 1 ? "" : "s")}, newest on {s.LastBuild}"));
        }
        return list;
    }
}

sealed record Proposal(string Row, string System, string From, string To, string Why);
