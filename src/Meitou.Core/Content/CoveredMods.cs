namespace Meitou.Content;

/// <summary>
/// A mod whose feature Meitou has built in (docs/formats/rekenshi.md): its <c>.mod</c> name or its code mod's id, and the Meitou setting the
/// feature became.
/// </summary>
public sealed record BuiltInMod(string? ModName, string? CodeId, string Setting);

/// <summary>
/// An RE_Kenshi mod whose plugin does not run in Meitou, with a code mod on the Steam workshop that does the same, offered as a suggestion
/// (other ports may exist): the RE_Kenshi mod's <c>.mod</c> name and the workshop items that carry it, and the suggested mod's title,
/// <c>.mod</c> name and workshop item.
/// </summary>
public sealed record SuggestedMod(string ModName, ulong[] WorkshopIds, string By, string ByModName, ulong ByWorkshopId);

/// <summary>
/// The mods Meitou covers, one table for the game (which takes in a built-in mod's settings and does not run its code mod) and the launcher
/// (which notes both kinds on the Mods page and suggests a code mod for an RE_Kenshi one).
/// </summary>
public static class CoveredMods
{
    /// <summary>
    /// xXDarkrusoXx's RE_Kenshi plugin "Player-Only XP x10" (workshop 3679678086).
    /// </summary>
    public static readonly BuiltInMod RekenshiXp = new("Player XP Only x10 RE", null, "Squad XP in the Advanced settings");

    /// <summary>
    /// Its code mod port "[MeitouClient] Player-Only XP x10" (workshop 3838916871).
    /// </summary>
    public static readonly BuiltInMod XpPort = new(null, "player-only-xp", "Squad XP in the Advanced settings");

    /// <summary>
    /// The mods Meitou has built in.
    /// </summary>
    public static IReadOnlyList<BuiltInMod> BuiltIn { get; } = [RekenshiXp, XpPort];

    /// <summary>
    /// The RE_Kenshi mods with a suggested code mod: Emkej's "Loot-Scoot-Execute" (workshop 3675700748, and its "MCM fork" 3813852124), with
    /// "[MeitouClient] Execution".
    /// </summary>
    public static IReadOnlyList<SuggestedMod> Suggested { get; } =
    [
        new("Loot-Scoot-Execute", [3675700748, 3813852124], "[MeitouClient] Execution", "Execution", 3838346946),
    ];

    /// <summary>
    /// The entry that has <paramref name="mod"/> built in, by its <c>.mod</c> name or its code mod's id; null when none does.
    /// </summary>
    public static BuiltInMod? BuiltInOf(ModInfo mod) => BuiltInOf(mod.Name, mod.HasCode ? CodeModManifest.TryReadId(mod.CodeManifestPath) : null);

    /// <summary>
    /// The entry that has the mod named <paramref name="modName"/>, with code mod id <paramref name="codeId"/> (null: no code mod), built in;
    /// null when none does.
    /// </summary>
    public static BuiltInMod? BuiltInOf(string modName, string? codeId) =>
        BuiltIn.FirstOrDefault(c => c.ModName == modName || (codeId is not null && c.CodeId == codeId));

    /// <summary>
    /// The entry suggesting a code mod for <paramref name="mod"/>, an RE_Kenshi plugin without code of its own, by its <c>.mod</c> name or its
    /// workshop item; null when none does.
    /// </summary>
    public static SuggestedMod? SuggestionOf(ModInfo mod)
    {
        if (!mod.HasNativePlugin || mod.HasCode) return null;

        ulong? item = WorkshopId(mod);
        return Suggested.FirstOrDefault(r => r.ModName == mod.Name || (item is { } id && r.WorkshopIds.Contains(id)));
    }

    /// <summary>
    /// Whether <paramref name="mods"/> hold the code mod <paramref name="suggested"/> names: a workshop mod by its item, a local mod (a copy in
    /// Kenshi's mods folder) by its <c>.mod</c> name.
    /// </summary>
    public static bool HasSuggestion(SuggestedMod suggested, IEnumerable<ModInfo> mods) =>
        mods.Any(m => WorkshopId(m) is { } id ? id == suggested.ByWorkshopId : m.Name == suggested.ByModName);

    /// <summary>
    /// The workshop item of <paramref name="mod"/>: a workshop mod's folder is named by its published file id. Null for a local mod.
    /// </summary>
    public static ulong? WorkshopId(ModInfo mod) =>
        mod.Source == ModSource.Workshop && ulong.TryParse(Path.GetFileName(mod.Folder), out var id) ? id : null;

    /// <summary>
    /// The Steam workshop page of item <paramref name="id"/>.
    /// </summary>
    public static Uri WorkshopPage(ulong id) => new($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}");
}
