using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// The benchmark's save: its zip downloaded once into <see cref="BenchmarkFolders.Downloads"/>, unpacked into <see cref="BenchmarkFolders.Save"/>
/// and checked against the save hash before a session, and a fresh copy for each run.
/// </summary>
sealed class BenchmarkSave(HttpClient http)
{
    /// <summary>
    /// The save's name, also its folder's.
    /// </summary>
    public const string Name = "bench-bases-2026-10-27";

    /// <summary>
    /// Where the zip is published.
    /// </summary>
    const string Url = "https://github.com/Demitrix2k/meitou-bench/releases/download/bench-save-2026-10-27/bench-bases-2026-10-27.zip";

    /// <summary>
    /// The save hash the benchmark expects (<see cref="Hash"/> of the unpacked folder); the game checks it too.
    /// </summary>
    const string ExpectedHash = "ceb2fb186fcfce7d3841a947914eac89f37547b028fbf05fb760aa8613dac4ff";

    /// <summary>
    /// The cached zip.
    /// </summary>
    static string ZipPath => Path.Combine(BenchmarkFolders.Downloads, Name + ".zip");

    /// <summary>
    /// The unpacked save.
    /// </summary>
    static string Folder => Path.Combine(BenchmarkFolders.Save, Name);

    /// <summary>
    /// The copy a run loads.
    /// </summary>
    public static string Copy => Path.Combine(BenchmarkFolders.SaveCopy, Name);

    /// <summary>
    /// Makes sure the unpacked save is there and holds exactly the expected files: unpacked again from the zip (downloaded when missing) when
    /// it is not. Throws <see cref="InvalidDataException"/> when even a fresh download does not match.
    /// </summary>
    public async Task EnsureAsync(IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        if (Directory.Exists(Folder) && await Task.Run(() => Hash(Folder), ct) == ExpectedHash) return;
        bool cached = File.Exists(ZipPath);
        if (!cached) await DownloadAsync(progress, ct);
        await UnpackAsync(ct);
        if (await Task.Run(() => Hash(Folder), ct) == ExpectedHash) return;

        // A damaged cached zip: once more from GitHub.
        File.Delete(ZipPath);
        if (!cached) throw new InvalidDataException("the downloaded benchmark save does not match its hash");
        await DownloadAsync(progress, ct);
        await UnpackAsync(ct);
        if (await Task.Run(() => Hash(Folder), ct) != ExpectedHash)
            throw new InvalidDataException("the downloaded benchmark save does not match its hash");
    }

    /// <summary>
    /// Replaces <see cref="Copy"/> with a fresh copy of the checked save.
    /// </summary>
    public static Task CopyForRunAsync(CancellationToken ct) => Task.Run(() =>
    {
        if (Directory.Exists(Copy)) Directory.Delete(Copy, recursive: true);
        CopyDirectory(Folder, Copy);
    }, ct);

    /// <summary>
    /// The save hash of <paramref name="folder"/>: SHA-256 of a manifest with one line <c>&lt;sha256 hex&gt;  ./&lt;path&gt;\n</c> per file
    /// of the folder (relative, <c>/</c> separators), sorted by path (ordinal). Verified: the published zip's folder gives
    /// <see cref="ExpectedHash"/>, as the old kit's <c>save-checksums.sha256</c> did.
    /// </summary>
    public static string Hash(string folder)
    {
        var lines = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .Select(path => $"{FileHash(Path.Combine(folder, path))}  ./{path}\n");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines))));
    }

    /// <summary>
    /// The SHA-256 of one file, lower-case hex.
    /// </summary>
    static string FileHash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Downloads the zip into the cache through a partial file, reporting the bytes done and the total.
    /// </summary>
    async Task DownloadAsync(IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(BenchmarkFolders.Downloads);
        string partial = ZipPath + ".partial";
        try
        {
            using var response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(partial))
                await CopyWithProgressAsync(source, file, total, progress, ct);
            File.Move(partial, ZipPath, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="target"/>, reporting at most every 100 ms and once at the end.
    /// </summary>
    static async Task CopyWithProgressAsync(Stream source, Stream target, long total, IProgress<(long Done, long Total)> progress,
        CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        long done = 0, reported = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if (Environment.TickCount64 - reported < 100) continue;
            progress.Report((done, total));
            reported = Environment.TickCount64;
        }
        progress.Report((done, total));
    }

    /// <summary>
    /// Unpacks the cached zip into <see cref="Folder"/>, its single top folder (the save's) becoming the folder.
    /// </summary>
    static async Task UnpackAsync(CancellationToken ct)
    {
        string staging = Folder + ".partial";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            await ZipFile.ExtractToDirectoryAsync(ZipPath, staging, ct);
            MakeWritable(staging);
            string root = Directory.GetFiles(staging).Length == 0 && Directory.GetDirectories(staging) is [var only] ? only : staging;
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
            Directory.CreateDirectory(BenchmarkFolders.Save);
            Directory.Move(root, Folder);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// Gives the owner write access to <paramref name="folder"/> and its folders (Unix): the published zip stores its folders without it,
    /// which would keep the unpacked save from being deleted or replaced.
    /// </summary>
    static void MakeWritable(string folder)
    {
        if (OperatingSystem.IsWindows()) return;

        const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(folder, File.GetUnixFileMode(folder) | owner);
        foreach (string directory in Directory.EnumerateDirectories(folder)) MakeWritable(directory);
    }

    /// <summary>
    /// Copies <paramref name="from"/> and everything below it to <paramref name="to"/>.
    /// </summary>
    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(from)) CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
    }
}
