using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace Meitou.Reports;

/// <summary>
/// The game's feedback button: a form (title, text, version, mods, camera location) is started, its attachments (screenshot, navmesh screenshot,
/// zipped save, gzipped log) are uploaded one request each, and sending it opens a GitHub issue that links them. The files stay in
/// <see cref="Config.FeedbackDir"/>, public under an unguessable token; one person starts at most 20 forms a day.
/// </summary>
sealed class Feedback(Store store, Config config, GithubAuth github, IHttpClientFactory http, ILogger<Feedback> log)
{
    /// <summary>
    /// The attachment kinds a form can ask for and upload, in their upload order.
    /// </summary>
    public static readonly string[] Kinds = ["screenshot", "navmesh", "save", "log"];

    /// <summary>
    /// The platform names a form may give; any other is stored as "other".
    /// </summary>
    static readonly string[] Platforms = ["win", "linux", "mac", "proton", "other"];

    /// <summary>
    /// The longest title, text, version, camera location and mods list kept (characters), and the forms one person may start a day.
    /// </summary>
    const int TitleMax = 100, BodyMax = 5000, VersionMax = 64, LocationMax = 200, ModsMax = 1000, PerDay = 20;

    /// <summary>
    /// The largest upload of a kind: Cloudflare in front of the service takes at most 100 MB a request.
    /// </summary>
    public static long MaxBytes(string kind) => kind == "save" ? 95_000_000 : 16 << 20;

    /// <summary>
    /// The file name an attachment of <paramref name="kind"/> is stored under in the form's folder.
    /// </summary>
    public static string FileName(string kind) => kind switch
    {
        "screenshot" => "screenshot.png",
        "navmesh" => "navmesh.png",
        "save" => "save.zip",
        _ => "log.txt",
    };

    /// <summary>
    /// Starts a form; returns its token (what the uploads, the send and the file links name), or why it was refused.
    /// </summary>
    public async Task<(string? Token, string? Error)> Create(FeedbackBody b, string reporter)
    {
        string? title = Clean(b.Title, TitleMax, lines: false);
        if (title is null) return (null, "give it a title");
        string? body = Clean(b.Body, BodyMax, lines: true);
        string version = Clean(b.Version, VersionMax, lines: false) ?? "unknown";
        string? commit = Clean(b.Commit, VersionMax, lines: false);
        string platform = b.Platform is { } p && Platforms.Contains(p) ? p : "other";
        string? discord = Clean(b.Discord, 64, lines: false);
        string[] mods = [.. (b.Mods ?? []).Select(m => Clean(m, 200, lines: false)).OfType<string>().Take(ModsMax)];
        string? location = Clean(b.Location, LocationMax, lines: false);
        string[] wants = [.. Kinds.Where(k => Wanted(b, k))];
        if (await store.FeedbackCountBy(reporter, DateTimeOffset.UtcNow.AddDays(-1)) >= PerDay)
            return (null, "too many feedback reports today, try again tomorrow");
        foreach (string stale in await store.DeleteFeedbackDrafts(DateTimeOffset.UtcNow.AddDays(-1))) DeleteFiles(stale);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await store.AddFeedback(token, reporter, title, body, version, commit, platform, mods, wants, discord, location);
        return (token, null);
    }

    /// <summary>
    /// Stores one attachment of an unsent form from the request body; null when stored, else why not (and the status code).
    /// </summary>
    public async Task<(int Status, string Error)?> Upload(string token, string kind, HttpRequest request)
    {
        if (!ValidToken(token) || !Kinds.Contains(kind)) return (404, "no such form");
        if (await store.GetFeedback(token) is not { } row) return (404, "no such form");
        if (row.Issue is not null) return (409, "already sent");
        if (!row.Wants.Contains(kind)) return (400, $"the form has no {kind}");
        long max = MaxBytes(kind);
        if (request.ContentLength > max) return (413, $"over {max / 1_000_000} MB");
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = max + 1;
        string folder = Directory.CreateDirectory(Path.Combine(config.FeedbackDir, token)).FullName;
        string path = Path.Combine(folder, FileName(kind)), part = path + ".part";
        long size = 0;
        await using (var file = File.Create(part))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(buffer)) > 0)
            {
                size += read;
                if (size > max)
                {
                    file.Close();
                    File.Delete(part);
                    return (413, $"over {max / 1_000_000} MB");
                }
                await file.WriteAsync(buffer.AsMemory(0, read));
            }
        }
        if (size == 0)
        {
            File.Delete(part);
            return (400, "empty upload");
        }
        File.Move(part, path, overwrite: true);
        await store.SetFeedbackUpload(token, kind, size);
        return null;
    }

    /// <summary>
    /// Sends the form: opens the GitHub issue (once; a second send returns the same issue).
    /// </summary>
    public async Task<(FeedbackIssue? Issue, string? Error)> Submit(string token)
    {
        if (!ValidToken(token) || await store.GetFeedback(token) is not { } row) return (null, "no such form");
        if (row.Issue is { } done) return (new(done, row.IssueUrl!), null);
        if (!github.Configured) return (null, "feedback is not set up on the server");
        var issue = new JsonObject { ["title"] = $"Feedback: {row.Title}", ["body"] = IssueBody(row), ["labels"] = new JsonArray("feedback") };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/repos/{config.Repo}/issues")
        {
            Content = new StringContent(issue.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.UserAgent.ParseAdd("meitou-reports");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        try
        {
            if (await github.Token() is not { } auth) return (null, "GitHub did not take the issue, try again later");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth);
            using var response = await http.CreateClient().SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Feedback issue not opened: {Status} {Body}", (int)response.StatusCode, text);
                return (null, "GitHub did not take the issue, try again later");
            }
            var created = JsonNode.Parse(text)!;
            var result = new FeedbackIssue((int)created["number"]!, (string)created["html_url"]!);
            await store.SetFeedbackIssue(token, result.Number, result.Url);
            return (result, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("Feedback issue not opened: {Error}", e.Message);
            return (null, "GitHub could not be reached, try again later");
        }
    }

    /// <summary>
    /// The stored file of a form's attachment, or null.
    /// </summary>
    public string? FilePath(string token, string kind)
    {
        if (!ValidToken(token) || !Kinds.Contains(kind)) return null;
        string path = Path.Combine(config.FeedbackDir, token, FileName(kind));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Deletes the form's folder and its attachments (nothing for a malformed token).
    /// </summary>
    public void DeleteFiles(string token)
    {
        if (!ValidToken(token)) return;
        string folder = Path.Combine(config.FeedbackDir, token);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    /// <summary>
    /// Whether the report asked to attach a file of <paramref name="kind"/>.
    /// </summary>
    static bool Wanted(FeedbackBody b, string kind) => kind switch
    {
        "screenshot" => b.Screenshot == true,
        "navmesh" => b.Navmesh == true,
        "save" => b.Save == true,
        _ => b.Log == true,
    };

    /// <summary>
    /// Whether a wanted file of <paramref name="kind"/> never arrived.
    /// </summary>
    static bool Missing(FeedbackRow row, string kind) => kind switch
    {
        "screenshot" => row.ScreenshotSize is null,
        "navmesh" => row.NavmeshSize is null,
        "save" => row.SaveSize is null,
        _ => row.LogSize is null,
    };

    /// <summary>
    /// The GitHub issue's text: the player's text (mentions defused), the game's details and links to the attachments with their sizes.
    /// </summary>
    string IssueBody(FeedbackRow row)
    {
        string Link(string kind) => new Uri(config.PublicUrl, $"feedback/{row.Token}/{FileName(kind)}").AbsoluteUri;
        static string Mb(long bytes) => (bytes / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        var s = new StringBuilder();
        s.AppendLine(row.Body is { } body ? Unmention(body) : "_No description._").AppendLine();

        // One header row and one value row: GitHub has no header-less tables, and an empty header shows as a blank bar.
        string? discord = row.Discord is { } name ? $"`{name.Replace('`', '\'').Replace('|', '/')}`" : null;
        s.AppendLine($"| Meitou version | Platform |{(discord is null ? "" : " Discord |")} Data files |");
        s.AppendLine($"|---|---|{(discord is null ? "" : "---|")}---|");
        string commitText = row.Commit is { } commit ? $" (`{commit}`)" : "";
        string discordText = discord is null ? "" : $" {discord} |";
        s.AppendLine($"| `{row.Version}`{commitText} | {row.Platform} |{discordText} {row.Mods.Length} |").AppendLine();
        if (row.Mods.Length > 0)
        {
            s.AppendLine("<details><summary>Load order</summary>").AppendLine();
            foreach (string mod in row.Mods) s.AppendLine($"1. `{mod.Replace('`', '\'')}`");
            s.AppendLine().AppendLine("</details>").AppendLine();
        }
        if (row.Location is { } location) s.AppendLine($"Camera: `{location.Replace('`', '\'')}`").AppendLine();

        // GitHub's image proxy refuses pictures over 5 MB (older games sent larger ones): those are linked instead of shown.
        void Picture(string kind, string label, long? size)
        {
            if (size is { } bytes)
            {
                s.AppendLine(bytes <= 5_000_000 ? $"![{label}]({Link(kind)})" : $"[{label}]({Link(kind)}) (png, {Mb(bytes)}, too large to show)")
                    .AppendLine();
            }
        }
        Picture("screenshot", "Screenshot", row.ScreenshotSize);
        Picture("navmesh", "Navmesh", row.NavmeshSize);
        if (row.SaveSize is { } save) s.AppendLine($"- [Save]({Link("save")}) (zip, {Mb(save)})");
        if (row.LogSize is not null) s.AppendLine($"- [log.txt]({Link("log")})");
        foreach (string missing in row.Wants.Where(k => Missing(row, k)))
            s.AppendLine($"- The {missing} did not upload.");
        s.AppendLine().AppendLine("<sub>Sent with the in-game feedback button.</sub>");
        return s.ToString();
    }

    /// <summary>
    /// A player's text cannot ping anyone: a zero-width space after each @.
    /// </summary>
    static string Unmention(string text) => text.Replace("@", "@​", StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="s"/> trimmed, without control characters (line breaks kept when <paramref name="lines"/>) and cut to <paramref name="max"/>;
    /// null when empty.
    /// </summary>
    static string? Clean(string? s, int max, bool lines)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = new string(s.Trim().ReplaceLineEndings("\n").Where(c => !char.IsControl(c) || lines && c == '\n').ToArray());
        return s.Length > max ? s[..max] : s;
    }

    /// <summary>
    /// Whether <paramref name="token"/> has the form of a form token: 32 lower-case hexadecimal digits.
    /// </summary>
    static bool ValidToken(string token) => token.Length == 32 && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// The JSON body of <c>POST /feedback</c>: the form, and which attachments will follow.
/// </summary>
sealed record FeedbackBody(string? Title, string? Body, string? Version, string? Commit, string? Platform, List<string>? Mods,
    bool? Screenshot, bool? Save, bool? Log, string? Discord = null, string? Location = null, bool? Navmesh = null);

/// <summary>
/// <c>POST /feedback</c>: the new form's token.
/// </summary>
sealed record FeedbackCreated(string Token);

/// <summary>
/// <c>POST /feedback/{token}/send</c>: the issue the form became.
/// </summary>
sealed record FeedbackIssue(int Number, string Url);
