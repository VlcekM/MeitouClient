using System.Text.RegularExpressions;

namespace Meitou.Content;

/// <summary>
/// The Steam workshop item a mod was published as. A downloaded item's folder is named by its id; the mod editor's upload writes the id into
/// <c>_&lt;mod name&gt;.info</c> beside the <c>.mod</c> (XML <c>ModData</c> with an <c>id</c> element, docs/formats/overview.md), which a local mod
/// keeps when it was uploaded from its folder or copied from a download.
/// </summary>
public static partial class WorkshopItem
{
    /// <summary>
    /// The workshop item id of <paramref name="mod"/>: a workshop mod's folder name, else the <c>id</c> of a local mod's <c>.info</c> file; null
    /// when it has neither (a mod never uploaded) or the file cannot be read.
    /// </summary>
    public static ulong? IdOf(ModInfo mod)
    {
        if (mod.Source == ModSource.Workshop && ulong.TryParse(Path.GetFileName(mod.Folder), out ulong item)) return item;

        string info = Path.Combine(mod.Folder, $"_{mod.Name}.info");
        try
        {
            return File.Exists(info) && InfoId().Match(File.ReadAllText(info)) is { Success: true } m
                && ulong.TryParse(m.Groups["id"].Value, out ulong id) && id > 0 ? id : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The <c>id</c> element of a <c>.info</c> file.
    /// </summary>
    [GeneratedRegex(@"<id>\s*(?<id>\d{1,20})\s*</id>")]
    private static partial Regex InfoId();
}
