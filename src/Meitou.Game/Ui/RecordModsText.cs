namespace Meitou.Game.Ui;

/// <summary>
/// The line that closes an item's tooltip and a building's description naming the mods its record comes from (Meitou's own, not in the
/// original): every mod that defines or changes the record, in load order (<see cref="Data.GameDatabase.ModsOf"/>).
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
}
