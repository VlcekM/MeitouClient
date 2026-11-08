using System.Reflection;

namespace Meitou.Game;

static class BuildVersion
{
    static readonly Dictionary<string, string?> Metadata = typeof(BuildVersion).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);

    public static string Tag => Metadata.GetValueOrDefault("GitTag") ?? "untagged";
    public static string Commit => Metadata.GetValueOrDefault("GitCommit") ?? "unknown commit";
}
