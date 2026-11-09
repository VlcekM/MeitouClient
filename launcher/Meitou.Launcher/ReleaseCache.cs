using System.Text.Json;

namespace Meitou.Launcher;

/// <summary>
/// The last release list GitHub answered and its ETag, kept in <c>releases-cache.json</c> so an unchanged list costs no rate limit and the
/// list still shows offline (docs/launcher.md).
/// </summary>
static class ReleaseCache
{
    /// <summary>
    /// The cached answer: page 1's ETag and every page's releases as one JSON array.
    /// </summary>
    internal sealed record Cache(string? ETag, string Body);

    /// <summary>
    /// Where the cache is kept, in the launcher's data folder.
    /// </summary>
    static string CacheFile => Path.Combine(LauncherConfig.DataDirectory, "releases-cache.json");

    /// <summary>
    /// The cached answer, or null when there is none or it cannot be read.
    /// </summary>
    internal static Cache? Read()
    {
        try
        {
            return File.Exists(CacheFile) ? JsonSerializer.Deserialize(File.ReadAllText(CacheFile), LauncherJson.Default.Cache) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps <paramref name="cache"/> as the last answer.
    /// </summary>
    internal static void Write(Cache cache)
    {
        Directory.CreateDirectory(LauncherConfig.DataDirectory);
        File.WriteAllText(CacheFile, JsonSerializer.Serialize(cache, LauncherJson.Default.Cache));
    }
}
