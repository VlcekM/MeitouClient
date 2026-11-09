using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Meitou.Reports;

/// <summary>
/// The token feedback issues are opened with. With a GitHub App set up (<c>GITHUB_APP_ID</c>, <c>GITHUB_APP_PRIVATE_KEY</c>) the issues are
/// authored by the app's bot: a JWT signed with the app's key is exchanged for an installation token (valid an hour, reused until 5 minutes
/// before it expires); the installation is looked up from <see cref="Config.Repo"/>. Without an app, the fixed <c>GITHUB_TOKEN</c>, if any.
/// </summary>
sealed class GithubAuth(Config config, IHttpClientFactory http, ILogger<GithubAuth> log)
{
    /// <summary>
    /// One token refresh at a time.
    /// </summary>
    readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The installation token in use, null before the first.
    /// </summary>
    string? _token;

    /// <summary>
    /// When <see cref="_token"/> expires.
    /// </summary>
    DateTimeOffset _expires;

    /// <summary>
    /// Whether issues can be opened: a GitHub App (id and key) or a fixed token is set.
    /// </summary>
    public bool Configured => config.GithubAppId is not null && config.GithubAppKey is not null || config.GithubToken is not null;

    /// <summary>
    /// A token that may open issues on the repo, or null when none is set up or GitHub refused.
    /// </summary>
    public async Task<string?> Token()
    {
        if (config.GithubAppId is not { } appId || config.GithubAppKey is not { } key) return config.GithubToken;
        await _gate.WaitAsync();
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expires.AddMinutes(-5)) return _token;
            string jwt;
            try
            {
                jwt = Jwt(appId, key);
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException)
            {
                log.LogWarning("GITHUB_APP_PRIVATE_KEY is not a usable PEM key: {Error}", e.Message);
                return null;
            }
            var installation = await Send(HttpMethod.Get, $"https://api.github.com/repos/{config.Repo}/installation", jwt);
            if (installation?["id"] is not { } id) return null;
            var created = await Send(HttpMethod.Post, $"https://api.github.com/app/installations/{(long)id}/access_tokens", jwt);
            if (created?["token"] is not { } t || created["expires_at"] is not { } at) return null;
            _token = (string)t!;
            _expires = DateTimeOffset.Parse((string)at!, System.Globalization.CultureInfo.InvariantCulture);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends one GitHub API request authorised with <paramref name="jwt"/> (a bearer token); the response's JSON, null when it failed (logged).
    /// </summary>
    async Task<JsonNode?> Send(HttpMethod method, string url, string jwt)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("meitou-reports");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        using var response = await http.CreateClient().SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode) return JsonNode.Parse(text);
        log.LogWarning("GitHub App token refused at {Url}: {Status} {Body}", url, (int)response.StatusCode, text);
        return null;
    }

    /// <summary>
    /// The app's RS256 JWT: issued a minute ago (clock drift), valid 9 minutes (GitHub allows at most 10).
    /// </summary>
    static string Jwt(string appId, string pem)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new JsonObject { ["iat"] = now - 60, ["exp"] = now + 540, ["iss"] = appId };
        string signing = Base64Url("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"u8.ToArray()) + "."
                + Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        byte[] signature = rsa.SignData(Encoding.ASCII.GetBytes(signing), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signing + "." + Base64Url(signature);
    }

    /// <summary>
    /// <paramref name="bytes"/> in URL-safe Base64 without padding (for the JWT).
    /// </summary>
    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
