using Npgsql;

namespace Meitou.Reports;

/// <summary>
/// The service's settings, from environment variables (docs/feedback.md lists them).
/// </summary>
sealed record Config(
    string ConnectionString,
    byte[] Salt,
    string? AdminToken,
    string Repo,
    string? GithubToken,
    string? GithubAppId,
    string? GithubAppKey,
    string FeedbackDir,
    Uri PublicUrl)
{
    /// <summary>
    /// Reads the settings from the environment and configuration (empty values count as unset); throws when the database connection or REPORTS_SALT
    /// is missing.
    /// </summary>
    public static Config Load(IConfiguration c)
    {
        string? Get(string key) => c[key] is { Length: > 0 } v ? v : null;

        string connection = Get("ConnectionStrings:Reports") ?? FromUrl(Get("DATABASE_URL"))
            ?? throw new InvalidOperationException("Set ConnectionStrings__Reports (ADO.NET) or DATABASE_URL (postgresql://...).");
        string salt = Get("REPORTS_SALT")
                ?? throw new InvalidOperationException("Set REPORTS_SALT: a long random string that keeps reporter hashes unguessable.");
        return new Config(
            connection,
            System.Text.Encoding.UTF8.GetBytes(salt),
            Get("REPORTS_ADMIN_TOKEN"),
            Get("GITHUB_REPO") ?? "VlcekM/MeitouClient",
            Get("GITHUB_TOKEN"),
            Get("GITHUB_APP_ID"),
            // The PEM may come on one line with its newlines written as \n, for hosts whose variables can't hold several lines.
            Get("GITHUB_APP_PRIVATE_KEY")?.Replace("\\n", "\n", StringComparison.Ordinal),
            Get("FEEDBACK_DIR") ?? "/data/feedback",
            new Uri(Get("PUBLIC_URL") ?? "https://gear-pastry-chorus.cloudovna.app"));
    }

    /// <summary>
    /// A <c>postgresql://user:pass@host:port/db?sslmode=...</c> URL as an Npgsql connection string.
    /// </summary>
    static string? FromUrl(string? url)
    {
        if (url is null) return null;
        var u = new Uri(url);
        var userInfo = u.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = u.Host,
            Port = u.Port > 0 ? u.Port : 5432,
            Database = Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            SslMode = SslMode.VerifyFull,
        };
        foreach (var part in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv[0] == "sslmode" && kv.Length == 2)
            {
                b.SslMode = kv[1] switch
                {
                    "disable" => SslMode.Disable,
                    "require" => SslMode.Require,
                    "verify-ca" => SslMode.VerifyCA,
                    "prefer" => SslMode.Prefer,
                    _ => SslMode.VerifyFull,
                };
            }
        }
        return b.ConnectionString;
    }
}
