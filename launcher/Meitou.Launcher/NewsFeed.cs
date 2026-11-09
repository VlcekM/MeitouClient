using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Meitou.Launcher;

/// <summary>
/// A news post: <c>news/&lt;yyyy-mm-dd&gt;-&lt;slug&gt;/index.md</c> with an optional front matter block, its images beside it (docs/launcher.md,
/// "News"). <see cref="Video"/>: the full video's web page, opened from a video cover.
/// </summary>
public sealed record NewsPost(
    string File, string Title, DateOnly? Date, string? Author, string? Image, string Body, string BaseUrl, string? Video = null);

/// <summary>
/// The Home page's posts, from the repository's <c>news/</c> folder, one folder per article: listed through the contents API (one call, cached
/// with its ETag), each folder's <c>index.md</c> fetched from raw.githubusercontent.com and kept on disk by the folder's tree hash,
/// so only new or changed posts download. Images are fetched and cached by <see cref="NewsMediaCache"/>. Works offline from the cache.
/// </summary>
public sealed class NewsFeed(HttpClient http)
{
    /// <summary>
    /// The branch the posts are read from.
    /// </summary>
    public const string Branch = "master";

    /// <summary>
    /// The repository's news folder.
    /// </summary>
    const string Folder = "news";

    /// <summary>
    /// At most this many posts, the newest.
    /// </summary>
    const int MaxPosts = 20;

    /// <summary>
    /// An article folder's post.
    /// </summary>
    const string PostFile = "index.md";

    /// <summary>
    /// Where the article list, the posts and (<see cref="NewsMediaCache"/>) their pictures are kept, in the launcher's data folder.
    /// </summary>
    internal static string CacheDirectory => Path.Combine(LauncherConfig.DataDirectory, "news-cache");

    /// <summary>
    /// Debug builds show the checkout's own <c>news/</c> (found above the executable, next to <c>MeitouClient.slnx</c>), so a post can be
    /// previewed before it is pushed; null in Release builds or outside a checkout.
    /// </summary>
    public static string? LocalFolder { get; } =
#if DEBUG
        FindCheckoutNews();
#else
        null;
#endif

    /// <summary>
    /// The checkout's <c>news/</c> above the executable, or null.
    /// </summary>
    static string? FindCheckoutNews()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MeitouClient.slnx")) && Directory.Exists(Path.Combine(dir.FullName, Folder)))
                return Path.Combine(dir.FullName, Folder);
        }
        return null;
    }

    /// <summary>
    /// The posts, newest first, and a problem line when GitHub could not be read; the local folder's in Debug builds.
    /// </summary>
    public async Task<(List<NewsPost> Posts, string? Problem)> FetchAsync(CancellationToken ct = default)
    {
        if (LocalFolder is { } local) return (await ReadLocalAsync(local, ct), null);
        Directory.CreateDirectory(CacheDirectory);
        var listFile = Path.Combine(CacheDirectory, "list.json");
        var etagFile = Path.Combine(CacheDirectory, "list.etag");
        string? listJson = File.Exists(listFile) ? await File.ReadAllTextAsync(listFile, ct) : null;
        var (json, problem) = await FetchListAsync(listJson, listFile, etagFile, ct);
        if (json is null) return ([], problem);
        return await ParsePostsAsync(json, problem, ct);
    }

    /// <summary>
    /// Asks GitHub for the article list, conditionally on the saved ETag, and keeps a fresh copy. A failure keeps the list read before (null when
    /// there was none) with a problem line.
    /// </summary>
    async Task<(string? Json, string? Problem)> FetchListAsync(string? listJson, string listFile, string etagFile, CancellationToken ct)
    {
        string? problem = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            string url = $"https://api.github.com/repos/{Releases.Repository}/contents/{Folder}?ref={Branch}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (listJson is not null && File.Exists(etagFile))
                request.Headers.TryAddWithoutValidation("If-None-Match", await File.ReadAllTextAsync(etagFile, ct));
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotModified) { }
            else if (response.IsSuccessStatusCode)
            {
                listJson = await response.Content.ReadAsStringAsync(timeout.Token);
                await File.WriteAllTextAsync(listFile, listJson, ct);
                await File.WriteAllTextAsync(etagFile, response.Headers.ETag?.ToString() ?? "", ct);
            }
            else if (response.StatusCode == HttpStatusCode.NotFound) listJson = "[]";
            else problem = LauncherText.Translate("News: GitHub answered {1}", (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            problem = LauncherText.Translate("News: GitHub not reachable")
                + (listJson is null ? "" : LauncherText.Translate("; showing what was read before"));
        }

        return (listJson, problem);
    }

    /// <summary>
    /// The posts of the article list, newest first: each article's post is read from the cache, or downloaded once per tree hash.
    /// </summary>
    async Task<(List<NewsPost> Posts, string? Problem)> ParsePostsAsync(string listJson, string? problem, CancellationToken ct)
    {
        var posts = new List<NewsPost>();
        using var doc = JsonDocument.Parse(listJson);
        var folders = ArticleFolders(doc);
        foreach (var (name, sha) in folders)
        {
            var baseUrl = $"https://raw.githubusercontent.com/{Releases.Repository}/{Branch}/{Folder}/{Uri.EscapeDataString(name)}/";
            var cached = Path.Combine(CacheDirectory, sha + ".md");
            try
            {
                if (!File.Exists(cached))
                {
                    using var response = await http.GetAsync(baseUrl + PostFile, ct);
                    if (response.StatusCode == HttpStatusCode.NotFound) continue; // a folder without a post yet
                    response.EnsureSuccessStatusCode();
                    await File.WriteAllTextAsync(cached, await response.Content.ReadAsStringAsync(ct), ct);
                }

                posts.Add(Parse(name, await File.ReadAllTextAsync(cached, ct), baseUrl));
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
            {
                problem ??= $"News: {name} could not be read";
            }
        }

        return (posts, problem);
    }

    /// <summary>
    /// The article folders of the list (named by date, newest first, at most <see cref="MaxPosts"/>): their name and tree hash, which changes
    /// whenever anything in them does.
    /// </summary>
    static List<(string Name, string Sha)> ArticleFolders(JsonDocument doc) =>
        doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
                .Where(IsArticleFolder)
                .Select(f => (Name: f.GetProperty("name").GetString()!, Sha: f.GetProperty("sha").GetString()!))
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Take(MaxPosts)
                .ToList()
            : [];

    /// <summary>
    /// Whether a contents-list entry is an article folder: a directory whose name starts with a date.
    /// </summary>
    static bool IsArticleFolder(JsonElement f) =>
        f.GetProperty("type").GetString() == "dir" && f.GetProperty("name").GetString() is { Length: > 10 } n && DateOnly.TryParse(n[..10], out _);

    /// <summary>
    /// The local folder's posts (Debug), newest first.
    /// </summary>
    static async Task<List<NewsPost>> ReadLocalAsync(string folder, CancellationToken ct)
    {
        var posts = new List<NewsPost>();
        foreach (var dir in Directory.EnumerateDirectories(folder).OrderByDescending(d => d, StringComparer.Ordinal).Take(MaxPosts))
        {
            var name = Path.GetFileName(dir);
            var file = Path.Combine(dir, PostFile);
            if (name.Length > 10 && DateOnly.TryParse(name[..10], out _) && File.Exists(file))
                posts.Add(Parse(name, await File.ReadAllTextAsync(file, ct), new Uri(dir + Path.DirectorySeparatorChar).ToString()));
        }
        return posts;
    }

    /// <summary>
    /// Front matter (<c>---</c> lines around <c>key: value</c> lines: title, date, image), then Markdown.
    /// </summary>
    static NewsPost Parse(string file, string text, string baseUrl)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int bodyStart = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            int end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
            if (end > 0)
            {
                foreach (var line in lines[1..end])
                {
                    if (line.IndexOf(':') is var colon and > 0)
                        meta[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('"');
                }
                bodyStart = end + 1;
            }
        }
        DateOnly? date = meta.TryGetValue("date", out var d) && DateOnly.TryParse(d, out var parsed) ? parsed
            : file.Length >= 10 && DateOnly.TryParse(file[..10], out var fromName) ? fromName : null;
        var title = meta.GetValueOrDefault("title") ?? Path.GetFileNameWithoutExtension(file);
        string body = string.Join("\n", lines[bodyStart..]).Trim();
        return new NewsPost(file, title, date, meta.GetValueOrDefault("author"), meta.GetValueOrDefault("image"), body, baseUrl,
            meta.GetValueOrDefault("video"));
    }

    /// <summary>
    /// A link or image path in a post, made absolute against the post's folder.
    /// </summary>
    public static string Resolve(NewsPost post, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var absolute) ? absolute.ToString() : new Uri(new Uri(post.BaseUrl), url).ToString();
}
