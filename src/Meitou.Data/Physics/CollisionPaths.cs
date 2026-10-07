using Meitou.Content;

namespace Meitou.Data.Physics;

/// <summary>Finds the collision (and other asset) files that records name by install-relative paths.</summary>
public static class CollisionPaths
{
    /// <summary>
    /// The file a record's path field names (<c>.\data\buildings\x.xml</c>, backslashes, optionally without the leading <c>.\</c>) under the install,
    /// or null when the field is empty or the file is not there.
    /// </summary>
    public static string? Resolve(GameInstall install, string recordPath)
    {
        if (string.IsNullOrWhiteSpace(recordPath)) return null;
        var rel = recordPath.Replace('\\', '/').TrimStart('.', '/');
        var full = Path.Combine(install.Root, rel);
        if (File.Exists(full)) return full;
        // Records may name the file relative to data/.
        full = Path.Combine(install.DataDirectory, rel.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ? rel[5..] : rel);
        return File.Exists(full) ? full : null;
    }
}
