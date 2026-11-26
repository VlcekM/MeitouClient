using Meitou.Data;

namespace Meitou.Game.Building;

/// <summary>
/// The text the build window's description box shows for a BUILDING (docs/game/buildings-production.md "Build menu"): the record's
/// <c>Description</c>, else Meitou's own text for a base-game building the player can build that leaves it empty, else none. A mod that gives one
/// of those buildings a <c>Description</c> replaces Meitou's text.
/// </summary>
public static class BuildingDescriptions
{
    /// <summary>
    /// Meitou's text, translated, for a research-unlocked base-game building whose <c>Description</c> is empty, by string id; empty for any other
    /// (<b>Verified</b>: the 25 such of the 228 buildings named by a RESEARCH's <c>enable buildings</c> in <c>gamedata.base</c>,
    /// <c>Newwworld.mod</c> and <c>rebirth.mod</c>).
    /// </summary>
    static string Fallback(string stringId) => stringId switch
    {
        "4063-TwoStorey.mod" => Loc.Tr("A tall two-story building shell with stairs inside."),
        "1966-gamedata.base" => Loc.Tr("Makes basic first aid kits and sleeping bags from fabrics. Needs power."),
        "2267-gamedata.base" => Loc.Tr("Makes first aid kits and splint kits from fabrics and hemp. Needs power."),
        "40569-small_changes_otto.mod" => Loc.Tr("Grows hemp indoors, safe from the weather. Needs power and water."),
        "40560-small_changes_otto.mod" => Loc.Tr("Grows riceweed indoors, safe from the weather. Needs power and water."),
        "40550-small_changes_otto.mod" => Loc.Tr("Grows greenfruit indoors, safe from the weather. Needs power and water."),
        "97583-Newwworld.mod" => Loc.Tr("Collects water when it rains."),
        "668-gamedata.base" => Loc.Tr("A sturdy metal table."),
        "900-gamedata.base" => Loc.Tr("A simple round table."),
        "3470-otto.mod" => Loc.Tr("A wooden shelf, for decoration."),
        "3468-otto.mod" => Loc.Tr("A simple stool to sit on."),
        "1855-gamedata.base" => Loc.Tr("Stores power for when your generators fall short."),
        "42188-rebirth.mod" => Loc.Tr("A larger wind generator with twice the power of the first when the wind is up. Keep batteries as a backup."),
        "1865-gamedata.base" => Loc.Tr("A large storage tank for fuel."),
        "5193-TwoStorey.mod" => Loc.Tr("A stack of boxes you can store anything in."),
        "43909-rebirth.mod" => Loc.Tr("A storage barrel for cactus."),
        "43911-rebirth.mod" => Loc.Tr("A storage barrel for greenfruit."),
        "43912-rebirth.mod" => Loc.Tr("A storage barrel for riceweed."),
        "3005-Walls.mod" => Loc.Tr("A low, crude wall that won't keep enemies out for long."),
        "3004-Walls.mod" => Loc.Tr("A proper defensive wall, taller than a makeshift one."),
        "2916-Walls2.mod" => Loc.Tr("A taller, heavier defensive wall."),
        "2939-Walls2.mod" => Loc.Tr("A towering fortified wall."),
        "3006-Walls.mod" => Loc.Tr("The gate for Defensive Wall II."),
        "2932-Walls2.mod" => Loc.Tr("The gate for Defensive Wall III."),
        "2934-Walls2.mod" => Loc.Tr("The gate for Defensive Wall IV."),
        _ => "",
    };

    /// <summary>
    /// The build window's text for the BUILDING <paramref name="stringId"/>: its description (<see cref="Of"/>), then a blank line and the mods
    /// the record comes from (<see cref="Ui.RecordModsText"/>) when any defines or changes it; empty when it is not loaded.
    /// </summary>
    public static string WithMods(GameDatabase db, string stringId)
    {
        if (db.Find(stringId) is not { } record) return "";

        string description = Of(record);
        string mods = Ui.RecordModsText.Of(db.ModsOf(record));
        if (mods.Length == 0) return description;
        return description.Length == 0 ? mods : description + "\n\n" + mods;
    }

    /// <summary>
    /// <paramref name="record"/>'s description for the build window, translated and as the mods' text sources show it; empty when it has none.
    /// </summary>
    public static string Of(GameRecord? record)
    {
        if (record is null) return "";

        // The field is capitalised, unlike an ITEM's description (Verified: every BUILDING of gamedata.base, Newwworld.mod and rebirth.mod carries
        // "Description"; 18 also carry a lowercase one, empty on all but one).
        if (record.GetString("Description") is var own && !string.IsNullOrWhiteSpace(own))
            return record.GetShownString("Description");

        return Fallback(record.StringId);
    }
}
