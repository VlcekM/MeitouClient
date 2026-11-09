using System.IO.Compression;
using System.Security.Cryptography;

namespace Meitou.Launcher;

/// <summary>
/// Installs a game release into <c>versions/&lt;version&gt;</c>: downloads its zip, checks it against GitHub's digest and unpacks it.
/// </summary>
public sealed class ReleaseInstaller(HttpClient http)
{
    /// <summary>
    /// Downloads the release's zip, checks its SHA-256 against the digest GitHub lists (when it lists one) and unpacks it into
    /// <c>versions/&lt;version&gt;</c>, taking the zip's single top folder as the version's folder. <paramref name="progress"/> gets the bytes
    /// downloaded and the total (0 when unknown), at most every 100 ms and once at the end.
    /// </summary>
    public async Task InstallAsync(Release release, IProgress<(long Done, long Total)> progress, CancellationToken ct = default)
    {
        var asset = release.Asset ?? throw new InvalidOperationException($"{release.Title} has no download for {Releases.Rid}.");
        var downloads = Path.Combine(LauncherConfig.InstallDirectory, "downloads");
        Directory.CreateDirectory(downloads);
        var zip = Path.Combine(downloads, asset.Name);
        var target = Releases.VersionDirectory(release.Version);
        var staging = target + ".partial";
        try
        {
            await DownloadAsync(asset, zip, progress, ct);
            await UnpackAsync(asset, zip, staging, target, ct);
        }
        finally
        {
            // Also on failure or cancel: no half download or half unpacked version is left behind.
            File.Delete(zip);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    /// <summary>
    /// Downloads the asset to <paramref name="zip"/>, reporting progress, and checks its SHA-256 against the one GitHub lists.
    /// </summary>
    async Task DownloadAsync(ReleaseAsset asset, string zip, IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        using (var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(zip);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long done = 0, reported = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                sha.AppendData(buffer, 0, n);
                done += n;
                if (Environment.TickCount64 - reported >= 100)
                {
                    progress.Report((done, total));
                    reported = Environment.TickCount64;
                }
            }

            progress.Report((done, total));
            var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (asset.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.Ordinal) && digest[7..] != hash)
                throw new InvalidDataException($"{asset.Name} is damaged: SHA-256 {hash}, GitHub lists {digest[7..]}.");
        }
    }

    /// <summary>
    /// Unpacks the download into <paramref name="staging"/>, checks the game's executable is there and moves the version's folder to
    /// <paramref name="target"/>.
    /// </summary>
    static async Task UnpackAsync(ReleaseAsset asset, string zip, string staging, string target, CancellationToken ct)
    {
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        await ZipFile.ExtractToDirectoryAsync(zip, staging, ct);
        var root = staging;
        if (Releases.FindGame(root) is null && Directory.GetFiles(root).Length == 0 && Directory.GetDirectories(root) is [var only])
            root = only;
        var exe = Releases.FindGame(root) ?? throw new InvalidDataException($"{asset.Name} has no {Releases.GameExecutables[0]}.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(root, target);
    }
}
