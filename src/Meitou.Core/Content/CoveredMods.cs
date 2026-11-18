namespace Meitou.Content;

/// <summary>
/// A Steam workshop item a covered mod was matched against: its id and Steam's update time (Unix seconds) of the version that was compared.
/// </summary>
public sealed record MatchedItem(ulong Id, long Updated);

/// <summary>
/// A mod whose feature Meitou has built in (docs/formats/rekenshi.md): its <c>.mod</c> name or its code mod's id, the Meitou setting the
/// feature became, and the workshop items it was matched against (none when it is matched by its code mod id, whatever its version).
/// </summary>
public sealed record BuiltInMod(string? ModName, string? CodeId, string Setting, MatchedItem[] Items);

/// <summary>
/// An RE_Kenshi mod whose plugin does not run in Meitou, with a port on the Steam workshop that Meitou endorses (docs/formats/rekenshi.md
/// "Endorsed ports"; the port's source is its maintainer's, not this repository's): the RE_Kenshi mod's <c>.mod</c> name and the workshop
/// items that carry it, and the port's title, <c>.mod</c> name, workshop item and maintainer.
/// </summary>
public sealed record EndorsedPort(string ModName, MatchedItem[] Items, string Port, string PortModName, ulong PortWorkshopId, string Maintainer);

/// <summary>
/// The mods Meitou covers, one table for the game (which takes in a built-in mod's settings and does not run its code mod) and the launcher
/// (which notes both kinds on the Mods page and offers to subscribe to the endorsed port of an RE_Kenshi mod).
/// </summary>
public static class CoveredMods
{
    /// <summary>
    /// xXDarkrusoXx's RE_Kenshi plugin "Player-Only XP x10" (workshop 3679678086, matched against its 2026-11-07 update).
    /// </summary>
    public static readonly BuiltInMod RekenshiXp =
        new("Player XP Only x10 RE", null, "Squad XP in the Advanced settings", [new(3679678086, 1794078276)]);

    /// <summary>
    /// Its code mod port "[MeitouClient] Player-Only XP x10" (workshop 3838916871), matched by its code mod id whatever its version.
    /// </summary>
    public static readonly BuiltInMod XpPort = new(null, "player-only-xp", "Squad XP in the Advanced settings", []);

    /// <summary>
    /// ikahurula's data mod "Swamp Turtle FIX" (workshop 2237731365, matched against its 2020-09-24 update): it gives the Swamp Turtle the
    /// elephant creature weapon, whose moves the Swamp turtle attacks fix gives it.
    /// </summary>
    public static readonly BuiltInMod SwampTurtleFix =
        new("Swamp Turtle FIX", null, "Swamp turtle attacks on the Fixes page", [new(2237731365, 1600962568)]);

    /// <summary>
    /// The mods Meitou has built in.
    /// </summary>
    public static IReadOnlyList<BuiltInMod> BuiltIn { get; } = [RekenshiXp, XpPort, SwampTurtleFix];

    /// <summary>
    /// Emkej's "Loot-Scoot-Execute" (workshop 3675700748, matched against its 2026-04-26 update, and its "MCM fork" 3813852124, its 2026-11-13
    /// update), with Jame's "[MeitouClient] Execution".
    /// </summary>
    static readonly EndorsedPort Execution = new(
        "Loot-Scoot-Execute", [new(3675700748, 1777211773), new(3813852124, 1794544300)], "[MeitouClient] Execution", "Execution", 3838346946, "Jame");

    /// <summary>
    /// Shutori's "Proving Grounds" (workshop 3774745195, matched against its 2026-09-26 update), with roscatoday's "[Meitou] Proving Grounds".
    /// </summary>
    static readonly EndorsedPort ProvingGrounds = new(
        "Proving Grounds", [new(3774745195, 1790470536)], "[Meitou] Proving Grounds", "ProvingGroundsMeitou", 3841575072, "roscatoday");

    /// <summary>
    /// The RE_Kenshi mods with an endorsed port on the Steam workshop.
    /// </summary>
    public static IReadOnlyList<EndorsedPort> Endorsed { get; } = [Execution, ProvingGrounds];

    /// <summary>
    /// The public wishlist of RE_Kenshi mods for Meitou (the roadmap's compatibility sheet), where players ask for one to be covered.
    /// </summary>
    public static Uri Wishlist { get; } = new("https://docs.google.com/spreadsheets/d/1h6HXRgT1MbioPtgj3enuXIdevj2JWaRU8qSz-99Haoo/edit?gid=0#gid=0");

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
    /// The endorsed port of <paramref name="mod"/>, an RE_Kenshi plugin without code of its own, by its <c>.mod</c> name or its workshop item;
    /// null when it has none.
    /// </summary>
    public static EndorsedPort? PortOf(ModInfo mod)
    {
        if (!mod.HasNativePlugin || mod.HasCode) return null;

        ulong? item = WorkshopId(mod);
        return Endorsed.FirstOrDefault(r => r.ModName == mod.Name || (item is { } id && r.Items.Any(i => i.Id == id)));
    }

    /// <summary>
    /// When the workshop copy of <paramref name="mod"/> was updated, if after the version <paramref name="items"/> was matched against
    /// (<see cref="WorkshopUpdates"/>); null for a local mod, an item the table does not name, a record file that cannot be read or has no
    /// entry for it, or no newer update.
    /// </summary>
    public static DateTimeOffset? UpdatedSince(ModInfo mod, IReadOnlyList<MatchedItem> items)
    {
        if (WorkshopId(mod) is not { } id || items.FirstOrDefault(i => i.Id == id) is not { } matched) return null;

        return WorkshopUpdates.TimeUpdated(mod.Folder, id) is { } updated && updated > matched.Updated
            ? DateTimeOffset.FromUnixTimeSeconds(updated)
            : null;
    }

    /// <summary>
    /// Whether <paramref name="mods"/> hold the port <paramref name="port"/> names: a workshop mod by its item, a local mod (a copy in Kenshi's
    /// mods folder) by its <c>.mod</c> name.
    /// </summary>
    public static bool HasPort(EndorsedPort port, IEnumerable<ModInfo> mods) =>
        mods.Any(m => WorkshopId(m) is { } id ? id == port.PortWorkshopId : m.Name == port.PortModName);

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
