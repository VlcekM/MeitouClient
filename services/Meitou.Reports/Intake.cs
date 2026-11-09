using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Meitou.Reports;

/// <summary>
/// A report as it arrives, from any source, before it is checked.
/// </summary>
sealed record Incoming(string? Row, string? Verdict, string? Build, string? Platform, bool? Modded, string? Note, string? Log);

/// <summary>
/// Checks a report and stores it: the row must be on the roadmap, fields are bounded, and one person makes at most 30 a day.
/// </summary>
sealed class Intake(Store store, Roadmap roadmap, Config config)
{
    /// <summary>
    /// The platform names a report may give.
    /// </summary>
    public static readonly string[] Platforms = ["win", "linux", "mac", "proton", "other"];

    /// <summary>
    /// The longest note and build kept (characters), the largest log (bytes) and the reports one person may make a day.
    /// </summary>
    const int NoteMax = 300, BuildMax = 64, LogMax = 512 * 1024, PerDay = 30;

    /// <summary>
    /// The salted hash a reporter is stored as: counts distinct people, cannot be turned back into an IP or a user id.
    /// </summary>
    public string Reporter(string key) => Convert.ToHexString(HMACSHA256.HashData(config.Salt, Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();

    /// <summary>
    /// Stores the report and returns its id, or returns the reason it was refused.
    /// </summary>
    public async Task<(long? Id, string? Error)> Add(Incoming r, string source, string reporter, bool trusted)
    {
        var rows = await roadmap.Rows();
        if (r.Row is null || !rows.ContainsKey(r.Row)) return (null, "unknown roadmap row");
        if (r.Verdict is not ("works" or "broken")) return (null, "verdict must be works or broken");
        string platform = r.Platform is { } p && Platforms.Contains(p) ? p : "other";
        string build = Clean(r.Build, BuildMax) ?? "unknown";
        string? note = Clean(r.Note, NoteMax);
        if (r.Verdict == "broken" && note is null) return (null, "say what happened");
        byte[]? log = null;
        if (r.Log is { Length: > 0 } text)
        {
            if (text.Length > LogMax) text = text[^LogMax..];   // the end of the log is the run that went wrong
            using var buffer = new MemoryStream();
            using (var gz = new GZipStream(buffer, CompressionLevel.SmallestSize)) gz.Write(Encoding.UTF8.GetBytes(text));
            log = buffer.ToArray();
        }
        if (await store.CountBy(reporter, DateTimeOffset.UtcNow.AddDays(-1)) >= PerDay)
            return (null, "too many reports today");
        return (await store.Add(r.Row, r.Verdict, build, platform, r.Modded, note, source, reporter, trusted, log), null);
    }

    /// <summary>
    /// <paramref name="s"/> trimmed, without control characters and cut to <paramref name="max"/>; null when empty.
    /// </summary>
    static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = new string(s.Trim().Where(c => !char.IsControl(c)).ToArray());
        return s.Length > max ? s[..max] : s;
    }

    /// <summary>
    /// The text of a gzipped log (UTF-8).
    /// </summary>
    public static string Unzip(byte[] gz)
    {
        using var input = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
