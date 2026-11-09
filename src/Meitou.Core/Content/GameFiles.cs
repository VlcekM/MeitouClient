using System.Collections.Concurrent;

namespace Meitou.Content;

/// <summary>
/// Finds game files and folders whose letter case differs from the one a record, script or mesh writes. Windows ignores case, and Kenshi's own
/// data does not always agree with itself (<c>resources.cfg</c> says <c>data/particles/textures</c>, the disk has <c>Textures</c>; a mesh links
/// <c>mammal_beak.skeleton</c> and the file is <c>Mammal_beak.skeleton</c>; a record names <c>automine.xml</c> and the file is <c>automine.XML</c>),
/// so on a case-sensitive file system the exact path does not exist. Route every path that comes from game data through <see cref="Resolve"/>
/// before it is tested or opened. On Windows it returns the path untouched.
/// </summary>
public static class GameFiles
{
    /// <summary>
    /// Whether paths need case folding: everywhere but Windows (macOS too, for the spelling on disk).
    /// </summary>
    static readonly bool Fold = !OperatingSystem.IsWindows();

    /// <summary>
    /// A folder's entries by name ignoring case, each to its spelling on disk (<c>Entries</c>), and the folder's write time when it
    /// was listed (<c>Stamp</c>).
    /// </summary>
    sealed record Listing(DateTime Stamp, Dictionary<string, string> Entries);

    /// <summary>
    /// Folder -> its <see cref="Listing"/>: a name that is not in the listing makes it read again if the folder has changed since (the install is
    /// normally read-only while the game runs, so this is rare).
    /// </summary>
    static readonly ConcurrentDictionary<string, Listing> Listings = new(StringComparer.Ordinal);

    /// <summary>
    /// The path that exists: <paramref name="path"/> itself, else the same path with each part matched ignoring case; <paramref name="path"/>
    /// unchanged when nothing matches (the caller's own existence test then fails as before).
    /// </summary>
    public static string Resolve(string path)
    {
        if (!Fold || path.Length == 0) return path;

        // Walk even when the path exists: on macOS and Windows the file system ignores case, and the caller wants the spelling on disk
        // (the upstream test pins it; a record may name a file in another case than the install).
        return Walk(path) ?? path;
    }

    /// <summary>
    /// Whether a file exists at the path, ignoring the case of its parts.
    /// </summary>
    public static bool Exists(string path) => File.Exists(Resolve(path));

    /// <summary>
    /// The folder at the path (ignoring the case of its parts), or null when there is none.
    /// </summary>
    public static string? ExistingDirectory(string path)
    {
        var resolved = Resolve(path);
        return Directory.Exists(resolved) ? resolved : null;
    }

    /// <summary>
    /// <paramref name="path"/> made full and spelt as on disk, part by part; null when a part does not exist or the path is unreadable.
    /// </summary>
    static string? Walk(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (root is null) return null;
            var current = root;
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Find(current, part, out var real)) return null;
                current = Path.Combine(current, real);
            }
            return current;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// The spelling on disk of <paramref name="name"/> in <paramref name="folder"/>, relisting the folder when it changed since it was cached.
    /// </summary>
    static bool Find(string folder, string name, out string real)
    {
        var listing = Listings.GetOrAdd(folder, List);
        if (listing.Entries.TryGetValue(name, out real!)) return true;
        if (Directory.GetLastWriteTimeUtc(folder) == listing.Stamp) return false;
        Listings[folder] = listing = List(folder);
        return listing.Entries.TryGetValue(name, out real!);
    }

    /// <summary>
    /// Lists <paramref name="folder"/> now; an unreadable folder gives an empty listing.
    /// </summary>
    static Listing List(string folder)
    {
        var stamp = Directory.GetLastWriteTimeUtc(folder);
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                entries.TryAdd(Path.GetFileName(entry), Path.GetFileName(entry));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new Listing(stamp, entries);
    }
}