namespace Meitou.Game.Host;

/// <summary>
/// The record fields whose text the interface asks the mods' text sources about (<see cref="ShownText.Record"/>), by the names the game data gives
/// them: keys, not text to translate, so they are kept out of the player-facing files the translation check reads.
/// </summary>
static class ShownFields
{
    /// <summary>
    /// A TOWN's name before it is explored.
    /// </summary>
    internal const string UnexploredName = "unexplored name";

    /// <summary>
    /// A BUILDING's build menu category.
    /// </summary>
    internal const string BuildingCategory = "building category";

    /// <summary>
    /// A BUILDING's build menu group (its variants share one).
    /// </summary>
    internal const string BuildingGroup = "building group";

    /// <summary>
    /// A BUILDING's description in the build window (capitalised, unlike an ITEM's).
    /// </summary>
    internal const string BuildingDescription = "Description";

    /// <summary>
    /// A weapon manufacturer's text under a weapon's tooltip.
    /// </summary>
    internal const string CompanyDescription = "company description";
}
