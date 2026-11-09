using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Meitou.Launcher;

/// <summary>
/// The pictures and videos of news posts and release notes: downloaded once and kept in <c>news-cache/</c> by a hash of their URL, so they
/// show offline; a local post's files (Debug) are read where they are.
/// </summary>
public sealed class NewsMediaCache(HttpClient http)
{
    /// <summary>
    /// The cache file of <paramref name="url"/>: the first 32 hex digits of the URL's SHA-256, then <paramref name="extension"/>.
    /// </summary>
    static string CacheFile(string url, string extension) =>
        Path.Combine(NewsFeed.CacheDirectory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32] + extension);

    /// <summary>
    /// A file's first <paramref name="length"/> bytes (a video's poster frame), fetched with a Range request and cached apart from the
    /// whole file; the whole file when it is already cached or the server sends all of it. Null when it cannot be had.
    /// </summary>
    public async Task<byte[]?> HeadAsync(string url, int length, CancellationToken ct = default)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            try
            {
                await using var stream = File.OpenRead(uri.LocalPath);
                var head = new byte[Math.Min(length, stream.Length)];
                await stream.ReadExactlyAsync(head, ct);
                return head;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        var whole = CacheFile(url, ".img");
        var cached = CacheFile(url, ".head");
        try
        {
            if (File.Exists(whole)) return await File.ReadAllBytesAsync(whole, ct);
            if (File.Exists(cached)) return await File.ReadAllBytesAsync(cached, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, length - 1);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            Directory.CreateDirectory(NewsFeed.CacheDirectory);
            await File.WriteAllBytesAsync(response.StatusCode == HttpStatusCode.PartialContent ? cached : whole, bytes, ct);
            return bytes;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// An image's bytes, from the disk cache or downloaded once; null when it cannot be had.
    /// </summary>
    public async Task<byte[]?> ImageAsync(string url, CancellationToken ct = default)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile) // a local post's picture (Debug)
        {
            try
            {
                return await File.ReadAllBytesAsync(uri.LocalPath, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        var cached = CacheFile(url, ".img");
        try
        {
            if (File.Exists(cached)) return await File.ReadAllBytesAsync(cached, ct);
            var bytes = await http.GetByteArrayAsync(url, ct);
            Directory.CreateDirectory(NewsFeed.CacheDirectory);
            await File.WriteAllBytesAsync(cached, bytes, ct);
            return bytes;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return null;
        }
    }
}
