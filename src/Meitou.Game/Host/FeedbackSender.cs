using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Meitou.Data.Textures;
using Meitou.Game.Ui;

namespace Meitou.Game.Host;

/// <summary>
/// One feedback report as the host hands it over: the form, the game's details, the screenshot and the navmesh screenshot being encoded, the camera's
/// location, the save being written (a temporary folder) and log.txt's path, each null when not sent.
/// </summary>
sealed record FeedbackPackage(FeedbackForm Form, string Version, string Commit, string Platform, IReadOnlyList<string> Mods,
    Task<byte[]>? Screenshot, Task<byte[]>? Navmesh, string? Location, string? SaveFolder, Task? SaveWritten, string? LogPath);

/// <summary>
/// A sent report's GitHub issue, or why it was not sent.
/// </summary>
readonly record struct FeedbackResult(int Number, string? Url, string? Error);

/// <summary>
/// Sends a feedback report to the reports service (services/Meitou.Reports, docs/feedback.md): the form first, then each
/// attachment as its own upload (Cloudflare takes at most 100 MB a request), then the send that opens the issue.
/// </summary>
static class FeedbackSender
{
    /// <summary>
    /// The reports service (services/Meitou.Reports).
    /// </summary>
    internal const string ServiceUrl = "https://gear-pastry-chorus.cloudovna.app";
    /// <summary>
    /// The service's limit for the zipped save.
    /// </summary>
    const long SaveMax = 95_000_000;
    /// <summary>
    /// The client for the feedback service, with a 30 min timeout (uploads are large).
    /// </summary>
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>
    /// The report sent; <paramref name="status"/> hears each step. Never throws: a failure is the result's error, for the form.
    /// </summary>
    public static async Task<FeedbackResult> Send(FeedbackPackage p, Action<string> status)
    {
        string? temp = p.SaveFolder is null ? null : Path.GetDirectoryName(p.SaveFolder);
        try
        {
            return await SendReport(p, status, temp);
        }
        catch (Refused e)
        {
            return new(0, null, e.Message);
        }
        catch (HttpRequestException e)
        {
            return new(0, null, $"Could not reach the feedback service: {e.Message}");
        }
        catch (TaskCanceledException)
        {
            return new(0, null, "The feedback service took too long to answer. Try again.");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"feedback  not sent: {e}");
            return new(0, null, $"Could not send: {e.Message}");
        }
        finally
        {
            try
            {
                if (temp is not null && Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The report's steps: the screenshots, the zipped save and the log made ready, the form sent, each attachment uploaded, the issue opened.
    /// </summary>
    static async Task<FeedbackResult> SendReport(FeedbackPackage p, Action<string> status, string? temp)
    {
        byte[]? png = p.Screenshot is null ? null : await p.Screenshot;
        byte[]? navPng = p.Navmesh is null ? null : await p.Navmesh;
        string? zip = null;
        if (p.SaveFolder is not null)
        {
            await p.SaveWritten!;
            status("Zipping the save...");
            zip = Path.Combine(temp!, "save.zip");
            await Task.Run(() => ZipFile.CreateFromDirectory(p.SaveFolder, zip, CompressionLevel.Optimal, includeBaseDirectory: true));
            long size = new FileInfo(zip).Length;
            if (size > SaveMax) return new(0, null, $"The save is {Mb(size)} zipped, over the 95 MB limit. Untick it to send without it.");
        }
        byte[]? log = p.LogPath is null ? null : await Task.Run(() => GzipLog(p.LogPath));

        status("Sending...");
        string token = await CreateReport(p, png, navPng, zip, log);
        await UploadAttachments(token, png, navPng, zip, log, status);
        status("Opening the issue...");
        var issue = await Answer(await Http.PostAsync($"{ServiceUrl}/feedback/{token}/send", null));
        return new((int)issue["number"]!, (string)issue["url"]!, null);
    }

    /// <summary>
    /// Sends the form (with which attachments follow) and returns the report's upload token.
    /// </summary>
    static async Task<string> CreateReport(FeedbackPackage p, byte[]? png, byte[]? navPng, string? zip, byte[]? log)
    {
        var form = new JsonObject
        {
            ["title"] = p.Form.Title,
            ["body"] = p.Form.Body,
            ["version"] = p.Version,
            ["commit"] = p.Commit,
            ["platform"] = p.Platform,
            ["discord"] = p.Form.Discord,
            ["mods"] = new JsonArray([.. p.Mods.Select(m => (JsonNode)m)]),
            ["screenshot"] = png is not null,
            ["navmesh"] = navPng is not null,
            ["location"] = p.Location,
            ["save"] = zip is not null,
            ["log"] = log is not null,
        };
        var content = new StringContent(form.ToJsonString(), Encoding.UTF8, "application/json");
        var created = await Answer(await Http.PostAsync($"{ServiceUrl}/feedback", content));
        return (string)created["token"]!;
    }

    /// <summary>
    /// Uploads each attachment of the report, one request each, the save with its progress.
    /// </summary>
    static async Task UploadAttachments(string token, byte[]? png, byte[]? navPng, string? zip, byte[]? log, Action<string> status)
    {
        if (png is not null)
        {
            status("Uploading the screenshot...");
            await Put(token, "screenshot", new ByteArrayContent(png), "image/png");
        }
        if (navPng is not null)
        {
            status("Uploading the navmesh screenshot...");
            await Put(token, "navmesh", new ByteArrayContent(navPng), "image/png");
        }
        if (zip is not null)
        {
            long size = new FileInfo(zip).Length;
            int shown = -1;
            await Put(token, "save", new FileUpload(zip, sent =>
            {
                int percent = (int)(sent * 100 / Math.Max(1, size));
                if (percent != shown) status($"Uploading the save ({Mb(size)}): {shown = percent}%");
            }), "application/zip");
        }
        if (log is not null)
        {
            status("Uploading log.txt...");
            await Put(token, "log", new ByteArrayContent(log), "application/gzip");
        }
    }

    /// <summary>
    /// GitHub shows an image in an issue only up to 5 MB (its proxy refuses more): the PNG stays under this.
    /// </summary>
    const int PngMax = 4_500_000;

    /// <summary>
    /// The screenshot as a PNG of at most 2560 × 1440 and <see cref="PngMax"/> bytes: scaled down to fit that size, then by three quarters again
    /// while the file is larger (a 1440p frame of the world is about 5.6 MB, at 1920 × 1080 under the limit).
    /// </summary>
    public static byte[] Png(byte[] rgba, int width, int height)
    {
        double scale = Math.Min(1, Math.Min(2560.0 / width, 1440.0 / height));
        while (true)
        {
            int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
            var pixels = w == width && h == height ? rgba : Shrink(rgba, width, height, w, h);
            using var png = new MemoryStream();
            PngWriter.Write(png, w, h, pixels);
            if (png.Length <= PngMax || w < 640) return png.ToArray();
            scale *= 0.75;
        }
    }

    /// <summary>
    /// The picture at <paramref name="w"/> × <paramref name="h"/>, each pixel the average of the source pixels it covers.
    /// </summary>
    static byte[] Shrink(byte[] rgba, int width, int height, int w, int h)
    {
        var small = new byte[w * h * 4];
        Span<int> sum = stackalloc int[4];
        for (int y = 0; y < h; y++)
        {
            int y0 = y * height / h, y1 = Math.Max(y0 + 1, (y + 1) * height / h);
            for (int x = 0; x < w; x++)
            {
                int x0 = x * width / w, x1 = Math.Max(x0 + 1, (x + 1) * width / w);
                sum.Clear();
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                        for (int c = 0; c < 4; c++) sum[c] += rgba[(sy * width + sx) * 4 + c];
                int n = (y1 - y0) * (x1 - x0);
                for (int c = 0; c < 4; c++) small[(y * w + x) * 4 + c] = (byte)(sum[c] / n);
            }
        }
        return small;
    }

    /// <summary>
    /// win, linux or mac; a Windows build under Wine or Proton (its registry has a Software\Wine key) is proton (as the launcher's reports).
    /// </summary>
    public static string Platform()
    {
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsMacOS()) return "mac";
        if (OperatingSystem.IsWindows())
        {
            using var wine = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Wine");
            if (wine is not null) return "proton";
        }
        return "win";
    }

    /// <summary>
    /// log.txt gzipped (GameLog keeps it under 10 MiB), read while the game goes on writing it.
    /// </summary>
    static byte[] GzipLog(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) file.CopyTo(gz);
        return packed.ToArray();
    }

    /// <summary>
    /// Uploads one part of a report (<paramref name="kind"/>) under its token; a refusal throws <see cref="Refused"/>.
    /// </summary>
    static async Task Put(string token, string kind, HttpContent content, string type)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        using var response = await Http.PutAsync($"{ServiceUrl}/feedback/{token}/{kind}", content);
        if (!response.IsSuccessStatusCode) await Answer(response);
    }

    /// <summary>
    /// The service's JSON answer; a refusal throws <see cref="Refused"/> with its reason.
    /// </summary>
    static async Task<JsonNode> Answer(HttpResponseMessage response)
    {
        using (response)
        {
            string text = await response.Content.ReadAsStringAsync();
            JsonNode? json = null;
            try
            {
                json = text.Length > 0 ? JsonNode.Parse(text) : null;
            }
            catch (System.Text.Json.JsonException)
            {
            }
            if (response.IsSuccessStatusCode && json is not null) return json;
            string reason = (json as JsonObject)?["error"]?.ToString()
                ?? (response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "too many requests, try again later"
                    : $"HTTP {(int)response.StatusCode}");
            throw new Refused($"The feedback service refused it: {reason}.");
        }
    }

    /// <summary>
    /// Bytes as megabytes to one decimal ("12.3 MB").
    /// </summary>
    static string Mb(long bytes) => (bytes / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    /// <summary>
    /// A refusal of the feedback service, with its reason for the player.
    /// </summary>
    sealed class Refused(string message) : Exception(message);

    /// <summary>
    /// A file sent from the disk, telling how much has gone.
    /// </summary>
    sealed class FileUpload(string path, Action<long> sent) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var file = File.OpenRead(path);
            var buffer = new byte[1 << 16];
            long total = 0;
            int read;
            while ((read = await file.ReadAsync(buffer)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read));
                sent(total += read);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = new FileInfo(path).Length;
            return true;
        }
    }
}