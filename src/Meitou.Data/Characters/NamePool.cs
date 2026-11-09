namespace Meitou.Data.Characters;

/// <summary>
/// The random first names of characters whose CHARACTER is <c>named</c> (docs/characters.md "Names"): <c>data/namesM.txt</c> for men,
/// <c>data/namesF.txt</c> for women, <c>data/namesMF.txt</c> added to both; lines trimmed, blank ones skipped, a missing file adds nothing.
/// </summary>
public sealed class NamePool
{
    /// <summary>
    /// The male names: <c>namesM.txt</c> then <c>namesMF.txt</c>.
    /// </summary>
    public IReadOnlyList<string> Male { get; }

    /// <summary>
    /// The female names: <c>namesF.txt</c> then <c>namesMF.txt</c>.
    /// </summary>
    public IReadOnlyList<string> Female { get; }

    /// <summary>
    /// Pools of the given lines, <paramref name="both"/> appended to each.
    /// </summary>
    public NamePool(IEnumerable<string> male, IEnumerable<string> female, IEnumerable<string> both)
    {
        var shared = both.ToList();
        Male = [.. male, .. shared];
        Female = [.. female, .. shared];
    }

    /// <summary>
    /// The pools of <paramref name="installRoot"/>: each file as the game opens it from resource group <c>General</c> (<b>Verified</b>
    /// <c>FUN_140641d80</c>: an enabled mod's copy, else the active language's in <c>locale/&lt;code&gt;</c>, docs/formats/localisation.md, else
    /// <c>data/</c>'s).
    /// </summary>
    public static NamePool Load(string installRoot)
    {
        string data = Path.Combine(installRoot, "data");
        var install = Content.GameInstall.IsValid(installRoot) ? Content.GameInstall.Open(installRoot) : null;
        return new NamePool(Lines(install, data, "namesM.txt"), Lines(install, data, "namesF.txt"), Lines(install, data, "namesMF.txt"));
    }

    /// <summary>
    /// The trimmed non-blank lines of file <paramref name="name"/>: the game's resource copy, else the language's or <c>data/</c>'s; none when
    /// missing or unreadable.
    /// </summary>
    static IEnumerable<string> Lines(Content.GameInstall? install, string data, string name)
    {
        try
        {
            var path = (install is null ? null : Ogre.GameResources.Find(install, name, "General")) ?? Loc.FindTextFile(data, name);
            return path is not null ? [.. File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0)] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// A name from the sex's list picked by <paramref name="roll"/> (uniform over the list), or null when the list is empty.
    /// </summary>
    public string? Pick(bool female, ulong roll)
    {
        var list = female ? Female : Male;
        return list.Count == 0 ? null : list[(int)(roll % (ulong)list.Count)];
    }
}