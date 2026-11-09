using System.Collections.Concurrent;
using Meitou.Data.Ogre;

namespace Meitou.Data.Characters;

/// <summary>
/// Skeleton rigs by file, plain or with the animation files they link, each read once; safe from any thread. <paramref name="find"/> finds an art
/// file the way <see cref="CharacterRigs.Find"/> does (a full path or null).
/// </summary>
internal sealed class SkeletonRigCache(Func<string, string?> find)
{
    /// <summary>
    /// The plain rigs by full skeleton path.
    /// </summary>
    readonly ConcurrentDictionary<string, Lazy<SkeletonRig>> _rigs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The rigs with linked animation files, by the skeleton's and the found files' paths joined with <c>|</c>.
    /// </summary>
    readonly ConcurrentDictionary<string, Lazy<SkeletonRig>> _linkedRigs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The linked skeleton files by full path.
    /// </summary>
    readonly ConcurrentDictionary<string, Lazy<OgreSkeleton?>> _sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The full path of an art file, or null.
    /// </summary>
    public string? Find(string fcsPath) => find(fcsPath);

    /// <summary>
    /// The rig of the skeleton file <paramref name="name"/>, or null when it is not in the install.
    /// </summary>
    public SkeletonRig? Rig(string name)
    {
        var path = find(name);
        if (path is null) return null;
        return _rigs.GetOrAdd(path, p => new Lazy<SkeletonRig>(() => new SkeletonRig(OgreSkeletonReader.ReadFile(p)))).Value;
    }

    /// <summary>
    /// The rig of the skeleton file <paramref name="name"/> linking <paramref name="animationFiles"/> (<see cref="AnimationFiles"/>, FCS paths in
    /// link order); a file not in the install is left out, as the game links only the ones it finds. Null when the skeleton is not in the install.
    /// </summary>
    public SkeletonRig? Rig(string name, IReadOnlyList<string> animationFiles)
    {
        if (Rig(name) is not { } own) return null;
        var found = animationFiles.Select(f => find(AnimationFiles.BareName(f))).OfType<string>().ToList();
        if (found.Count == 0) return own;
        return _linkedRigs.GetOrAdd(find(name) + "|" + string.Join("|", found),
            _ => new Lazy<SkeletonRig>(() => new SkeletonRig(own.Skeleton, [.. found.Select(Source)]))).Value;
    }

    /// <summary>
    /// A linked skeleton, read once per file whichever rigs link it; null when it cannot be read (the game logs and plays without it).
    /// </summary>
    Lazy<OgreSkeleton?> Source(string path) => _sources.GetOrAdd(path, p => new Lazy<OgreSkeleton?>(() =>
    {
        try
        {
            return OgreSkeletonReader.ReadFile(p);
        }
        catch (Exception e) when (e is OgreFormatException or IOException or EndOfStreamException or InvalidDataException)
        {
            return null;
        }
    }));
}
