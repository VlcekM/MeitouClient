using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Meitou.Launcher;

/// <summary>
/// What a GitHub release of this repository is: the game (tags <c>v&lt;version&gt;</c>) or the launcher (<c>launcher-v&lt;version&gt;</c>).
/// </summary>
public enum ReleaseKind
{
    /// <summary>
    /// A game release (tag <c>v&lt;version&gt;</c>).
    /// </summary>
    Game,

    /// <summary>
    /// A launcher release (tag <c>launcher-v&lt;version&gt;</c>).
    /// </summary>
    Launcher,
}

/// <summary>
/// A file attached to a release: its name, download URL, size in bytes and GitHub's digest (<c>sha256:&lt;hex&gt;</c>) when listed.
/// </summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Digest);

/// <summary>
/// A published release as GitHub lists it, its version taken from the tag.
/// </summary>
public sealed record Release(ReleaseKind Kind, string Tag, string Version, string Title, bool Prerelease, DateTimeOffset Published,
    string Notes, string PageUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>
    /// The zip for this machine: <c>Meitou-&lt;version&gt;-&lt;rid&gt;.zip</c> or <c>MeitouLauncher-&lt;version&gt;-&lt;rid&gt;.zip</c>.
    /// </summary>
    public ReleaseAsset? Asset => Assets.FirstOrDefault(a => a.Name == AssetName);

    /// <summary>
    /// The name of <see cref="Asset"/>.
    /// </summary>
    string AssetName => $"{(Kind == ReleaseKind.Game ? "Meitou" : "MeitouLauncher")}-{Version}-{Releases.Rid}.zip";
}

/// <summary>
/// The repository's GitHub releases (docs/launcher.md): listed through the REST API without a token, with the last answer and its
/// ETag cached so an unchanged list costs no rate limit; installed game versions in <c>versions/&lt;version&gt;</c>.
/// </summary>
public sealed class Releases(HttpClient http)
{
    #region Listing

    /// <summary>
    /// The GitHub repository whose releases, news and launcher updates the launcher reads.
    /// </summary>
    public const string Repository = "VlcekM/MeitouClient";

    /// <summary>
    /// GitHub's largest page; the page cap only guards against a runaway loop (5000 releases).
    /// </summary>
    const int PageSize = 100, MaxPages = 50;

    /// <summary>
    /// The platform part of asset names, e.g. <c>win-x64</c>, <c>linux-x64</c>, <c>osx-arm64</c>.
    /// </summary>
    public static string Rid => RuntimeInformation.RuntimeIdentifier;

    /// <summary>
    /// The launcher's HTTP client: no timeout, the launcher's name and version as its user agent.
    /// </summary>
    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // a big download may take minutes; callers cancel instead
        string version = typeof(Releases).Assembly.GetName().Version?.ToString(3) ?? "0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("meitou-launcher", version));
        return client;
    }

    /// <summary>
    /// Every published release, newest first. Falls back to the cached list when GitHub cannot be reached or rate-limits.
    /// </summary>
    public async Task<(List<Release> Releases, string? Problem)> FetchAsync(CancellationToken ct = default)
    {
        var cached = ReleaseCache.Read();
        string? problem = null;
        string? body = cached?.Body;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            // Page 1 is asked for with the cached ETag: unchanged (304, free of rate limit) means a new release came or went nowhere, so
            // the cached list of every page stands. Otherwise every page is read until a short one, and the cache holds them as one array.
            var pages = new List<string>();
            string? etag = null;
            for (int page = 1; page <= MaxPages; page++)
            {
                string url = $"https://api.github.com/repos/{Repository}/releases?per_page={PageSize}&page={page}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                if (page == 1 && cached?.ETag is { } cachedTag)
                    request.Headers.TryAddWithoutValidation("If-None-Match", cachedTag);
                using var response = await http.SendAsync(request, timeout.Token);
                if (page == 1 && response.StatusCode == HttpStatusCode.NotModified) break;
                if (!response.IsSuccessStatusCode)
                {
                    problem = LauncherText.Translate("GitHub answered {1} {2}", (int)response.StatusCode, response.ReasonPhrase)
                        + (body is null ? "" : LauncherText.Translate("; showing the last list"));
                    pages.Clear();
                    break;
                }
                if (page == 1) etag = response.Headers.ETag?.ToString();
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                pages.Add(text);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.GetArrayLength() < PageSize) break;
            }
            if (pages.Count > 0)
            {
                body = Join(pages);
                ReleaseCache.Write(new ReleaseCache.Cache(etag, body));
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            problem = LauncherText.Translate("GitHub not reachable ({1})", e.Message)
                + (body is null ? "" : LauncherText.Translate("; showing the last list"));
        }
        return (body is null ? [] : Parse(body), problem);
    }

    /// <summary>
    /// The pages' JSON arrays as one array.
    /// </summary>
    static string Join(List<string> pages)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartArray();
            foreach (var page in pages)
            {
                using var doc = JsonDocument.Parse(page);
                foreach (var release in doc.RootElement.EnumerateArray()) release.WriteTo(json);
            }
            json.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The listed releases of this repository that are not drafts and are tagged as the game's or the launcher's, newest first.
    /// </summary>
    static List<Release> Parse(string json)
    {
        var list = new List<Release>();
        using var doc = JsonDocument.Parse(json);
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean()) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            ReleaseKind kind;
            string version;
            if (tag.StartsWith("launcher-v", StringComparison.Ordinal))
            {
                kind = ReleaseKind.Launcher;
                version = tag["launcher-v".Length..];
            }
            else if (tag.Length > 1 && tag[0] == 'v' && char.IsAsciiDigit(tag[1]))
            {
                kind = ReleaseKind.Game;
                version = tag[1..];
            }
            else continue;
            var assets = r.GetProperty("assets").EnumerateArray().Select(ReadAsset).ToList();
            list.Add(new Release(kind, tag, version, r.GetProperty("name").GetString() ?? tag, r.GetProperty("prerelease").GetBoolean(),
                r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTimeOffset() : default,
                r.GetProperty("body").GetString() ?? "", r.GetProperty("html_url").GetString() ?? "", assets));
        }
        return [.. list.OrderByDescending(r => r.Published)];
    }

    /// <summary>
    /// One entry of a release's <c>assets</c> list.
    /// </summary>
    static ReleaseAsset ReadAsset(JsonElement a) =>
        new ReleaseAsset(
            a.GetProperty("name").GetString() ?? "",
            a.GetProperty("browser_download_url").GetString() ?? "",
            a.GetProperty("size").GetInt64(),
            a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null);

    #endregion

    #region Versions

    /// <summary>
    /// A nightly's version, <c>0.15.1-nightly.20261020</c> or a same-day second build <c>….20261020.2</c> (nightly.yml): its number, build date and
    /// that day's build (1 for the plain date).
    /// </summary>
    public static (string Number, DateOnly? Nightly, int Build) ParseVersion(string version)
    {
        int at = version.IndexOf("-nightly.", StringComparison.Ordinal);
        if (at < 0) return (version, null, 0);
        var stamp = version[(at + "-nightly.".Length)..].Split('.');
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        int build = stamp.Length > 1 && int.TryParse(stamp[1], System.Globalization.NumberStyles.None, invariant, out int n) ? n : 1;
        return (version[..at], DateOnly.TryParseExact(stamp[0], "yyyyMMdd", out var date) ? date : null, build);
    }

    /// <summary>
    /// "v0.15.0", a nightly "v0.15.1 nightly".
    /// </summary>
    public static string Display(string version) =>
        version.Contains("-nightly.", StringComparison.Ordinal) ? $"v{ParseVersion(version).Number} nightly" : $"v{version}";

    /// <summary>
    /// Orders versions oldest first: by number, a release after its own pre-releases, nightlies by date, then that day's build.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        static Version Number(string v) => Version.TryParse(v.Split('-')[0], out var n) ? n : new Version();
        int c = Number(a).CompareTo(Number(b));
        if (c == 0) c = b.Contains('-').CompareTo(a.Contains('-'));
        if (c == 0) c = Nullable.Compare(ParseVersion(a).Nightly, ParseVersion(b).Nightly);
        if (c == 0) c = ParseVersion(a).Build.CompareTo(ParseVersion(b).Build);
        return c != 0 ? c : StringComparer.Ordinal.Compare(a, b);
    }

    #endregion

    #region Installed versions

    /// <summary>
    /// The game's executable names, newest first: <c>MeitouClient</c> since 0.15, <c>meitou</c> before (<c>.exe</c> on Windows).
    /// </summary>
    internal static readonly string[] GameExecutables =
        OperatingSystem.IsWindows() ? ["MeitouClient.exe", "meitou.exe"] : ["MeitouClient", "meitou"];

    /// <summary>
    /// The folder holding one folder per installed version.
    /// </summary>
    static string VersionsDirectory => Path.Combine(LauncherConfig.InstallDirectory, "versions");

    /// <summary>
    /// The game's executable in <paramref name="directory"/>, or null.
    /// </summary>
    public static string? FindGame(string directory) =>
        GameExecutables.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists);

    /// <summary>
    /// Installed game versions (folders of <c>versions/</c> holding the game), newest version first: a release before its own
    /// pre-releases (<c>0.15.0</c> before <c>0.15.0-nightly.20261020</c>), nightlies of one version newest date first, then that day's newest build.
    /// </summary>
    public static List<string> Installed()
    {
        if (!Directory.Exists(VersionsDirectory)) return [];
        return [.. Directory.EnumerateDirectories(VersionsDirectory)
            .Where(d => FindGame(d) is not null)
            .Select(Path.GetFileName).OfType<string>()
            .Order(Comparer<string>.Create((a, b) => CompareVersions(b, a)))];
    }

    /// <summary>
    /// The folder <paramref name="version"/> is installed in, whether it is there or not.
    /// </summary>
    public static string VersionDirectory(string version) => Path.Combine(VersionsDirectory, version);

    /// <summary>
    /// Moves the installed versions from <paramref name="from"/>'s <c>versions/</c> into <paramref name="to"/>'s, copying across drives.
    /// A version already in the target is left where it was.
    /// </summary>
    public static void MoveInstalled(string from, string to)
    {
        var source = Path.Combine(from, "versions");
        if (!Directory.Exists(source) || Path.GetFullPath(from) == Path.GetFullPath(to)) return;
        var target = Path.Combine(to, "versions");
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var dest = Path.Combine(target, Path.GetFileName(dir));
            if (Directory.Exists(dest)) continue;
            try
            {
                Directory.Move(dir, dest);
            }
            catch (IOException) when (Path.GetPathRoot(Path.GetFullPath(dir)) != Path.GetPathRoot(Path.GetFullPath(dest)))
            {
                CopyDirectory(dir, dest);
                Directory.Delete(dir, recursive: true);
            }
        }
        if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
    }

    /// <summary>
    /// Copies <paramref name="from"/> and everything below it to <paramref name="to"/>.
    /// </summary>
    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    #endregion
}
