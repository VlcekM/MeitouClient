using System.Security.Cryptography;
using System.Text;

namespace Meitou.Game.DevTools.Benchmark;

/// <summary>
/// A save folder's hash (docs/bench.md "Launcher benchmark"): SHA-256 hex of its manifest, one line per file of the folder,
/// <c>&lt;sha256 hex&gt;  ./&lt;relative path with /&gt;\n</c>, sorted by path (ordinal), the same text <c>sha256sum</c> prints.
/// </summary>
static class SaveDigest
{
    /// <summary>
    /// The hash of the save folder <paramref name="folder"/> (every file, subfolders included).
    /// </summary>
    public static string Of(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => (Path: f, Name: Path.GetRelativePath(folder, f).Replace('\\', '/')))
            .OrderBy(f => f.Name, StringComparer.Ordinal);
        var manifest = new StringBuilder();
        foreach (var (path, name) in files) manifest.Append(FileHash(path)).Append("  ./").Append(name).Append('\n');

        return BenchmarkSpec.Sha256Hex(manifest.ToString());
    }

    /// <summary>
    /// SHA-256 hex of the file at <paramref name="path"/>.
    /// </summary>
    static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Checks <paramref name="folder"/> against <paramref name="scenario"/>'s save hash; the exit code (<see cref="BenchmarkSpec.SaveMismatchExit"/>)
    /// with the reason on stderr when it is not the scenario's save, else null.
    /// </summary>
    public static int? Check(string folder, BenchmarkScenario scenario)
    {
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"--benchmark: the save folder {folder} does not exist");
            return BenchmarkSpec.SaveMismatchExit;
        }

        string hash = Of(folder);
        if (hash == scenario.SaveHash)
        {
            Console.WriteLine($"bench     save {scenario.Save} checked ({hash})");
            return null;
        }

        Console.Error.WriteLine($"--benchmark: {folder} is not the save {scenario.Save}: its hash is {hash}, expected {scenario.SaveHash}");
        return BenchmarkSpec.SaveMismatchExit;
    }
}
