using System.Reflection;

namespace Meitou.Game.Host;

/// <summary>
/// The build's Git tag and commit, from the assembly's metadata (written at build time).
/// </summary>
static class BuildVersion
{
    /// <summary>
    /// The assembly's metadata, by key.
    /// </summary>
    static readonly Dictionary<string, string?> Metadata = typeof(BuildVersion).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);

    /// <summary>
    /// The build's Git tag; "untagged" for a build without one.
    /// </summary>
    public static string Tag => Metadata.GetValueOrDefault("GitTag") ?? "untagged";
    /// <summary>
    /// The build's Git commit; "unknown commit" without one.
    /// </summary>
    public static string Commit => Metadata.GetValueOrDefault("GitCommit") ?? "unknown commit";
}