using Meitou.Data;

namespace Meitou.Game.Ui;

/// <summary>
/// The line that closes an item's tooltip and a building's description naming the mods its record comes from (Meitou's own, not in the
/// original): the mod that made the record apart from the mods that only change it (<see cref="GameDatabase.ModsOf"/>), so a base-game record a
/// mod edits is not shown as that mod's.
/// </summary>
public static class RecordModsText
{
    /// <summary>
    /// "Mod: A" or "Mods: A, B", translated; empty when <paramref name="mods"/> is empty (a base-game record no mod changed).
    /// </summary>
    public static string Of(IReadOnlyList<string> mods) => mods.Count switch
    {
        0 => "",
        1 => Loc.Tr("Mod: {1}", mods[0]),
        _ => Loc.Tr("Mods: {1}", string.Join(", ", mods)),
    };

    /// <summary>
    /// <paramref name="record"/>'s line: "Mod: A" for a record mod A made, "Mod: A, changed by: B, C" when other mods change it too, "Base game,
    /// changed by: B, C" for a base-game record (made by a core file, <see cref="LoadOrder.BaseFiles"/>) mods change; empty for a base-game record
    /// no mod changed.
    /// </summary>
    public static string Of(GameDatabase db, GameRecord record)
    {
        var mods = db.ModsOf(record);
        if (mods.Count == 0) return "";

        if (LoadOrder.BaseFiles.Contains(record.DefinedBy)) return Loc.Tr("Base game, changed by: {1}", string.Join(", ", mods));
        if (mods.Count == 1) return Loc.Tr("Mod: {1}", mods[0]);

        return Loc.Tr("Mod: {1}, changed by: {2}", mods[0], string.Join(", ", mods.Skip(1)));
    }
}
