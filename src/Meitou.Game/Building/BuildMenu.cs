using System.Globalization;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Game.Rendering.Objects;

namespace Meitou.Game.Building;

/// <summary>
/// A group of the build menu: the members that share a <c>building group</c> (or stand alone under their own name), the way the original lists
/// variants.
/// </summary>
public sealed record BuildGroup(string Name, IReadOnlyList<BuildingBuildData> Members)
{
    /// <summary>
    /// The group's default pick: the member with the highest <c>build materials</c>, the first of the sorted members
    /// (<see cref="BuildMenu.Groups"/>).
    /// </summary>
    public BuildingBuildData Default => Members[0];
}

/// <summary>
/// The build menu's lists (docs/game/buildings-production.md "Build menu"): every BUILDING is a candidate; the category is <c>building category</c>
/// (default "misc"), the group <c>building group</c> or the building's name; one is offered when the player's research unlocked it, and a category
/// shows when one of its members does.
/// </summary>
public sealed class BuildMenu(IReadOnlyList<BuildingBuildData> catalog)
{
    /// <summary>
    /// Every BUILDING's build data, the candidates of the menu.
    /// </summary>
    public IReadOnlyList<BuildingBuildData> Catalog { get; } = catalog;

    /// <summary>
    /// The categories with a member the set unlocks, by name in byte order (the original keeps them in a <c>std::map</c> of names,
    /// <c>FUN_1404e4480</c>).
    /// </summary>
    public IReadOnlyList<string> Categories(IReadOnlySet<string> unlocked) =>
        [.. Catalog.Where(b => unlocked.Contains(b.Id)).Select(b => b.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The groups of a category that have a member the set unlocks, by name in byte order (<c>FUN_1404e4480</c>); a group holds only the unlocked
    /// members, taken from the last in the database to the first and sorted by <c>build materials</c> descending, equal ones staying in that order
    /// (<c>FUN_1404e4210</c>, comparator <c>FUN_1404d6060</c>).
    /// </summary>
    public IReadOnlyList<BuildGroup> Groups(string category, IReadOnlySet<string> unlocked) =>
        [.. Catalog
            .Where(b => b.Category == category && unlocked.Contains(b.Id))
            .GroupBy(b => b.Group, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new BuildGroup(g.Key, [.. g.Reverse().OrderByDescending(m => m.BuildMaterials)]))];

    /// <summary>
    /// The hours the window estimates (<c>FUN_14055bf10</c>): the materials over the build speed, the same as an engineering 41.2 builder takes. The
    /// Building speed option is taken as 1 (its default is <b>Unknown</b>); the original's rounding to whole hours is <b>Unknown</b>, it is rounded
    /// up here.
    /// </summary>
    public static int EstimatedHours(BuildingBuildData building)
    {
        float materials = building.Materials.Sum(m => m.Required);
        float rate = building.BuildSpeedMult * 6;
        return rate <= 0 || materials <= 0 ? 0 : (int)MathF.Ceiling(materials / rate);
    }

    /// <summary>
    /// The stats panel's lines (<c>FUN_1404df450</c>): the materials cost (each item and amount, or "Nothing") and the estimated build time.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> Stats(BuildingBuildData building, Func<string, string> itemName)
    {
        var lines = new List<(string, string)> { ("Materials cost:", building.Materials.Count == 0 ? "Nothing" : "") };
        foreach (var m in building.Materials)
            lines.Add(("  " + itemName(m.Item), "x" + m.Required.ToString("0.##", CultureInfo.InvariantCulture)));
        lines.Add(("Estimated build time:", Loc.Plural("{1,num} hr", "{1,num} hrs", EstimatedHours(building), EstimatedHours(building))));
        return lines;
    }
}

/// <summary>
/// A level-data building as the selection panel names it: <paramref name="Name"/> is the placement state's own name (else the record's),
/// <paramref name="Type"/> the record's name when it differs; <paramref name="Town"/> and <paramref name="Owner"/> are null when it is in no town or
/// the town gives none; <paramref name="PublicDay"/> / <paramref name="PublicNight"/> are the state's flags; <paramref name="Turret"/> marks a town's
/// turret building (<c>TownBuilding.Turret</c>); <paramref name="Pieces"/> are the furniture standing in it that a click selects
/// (<c>TownBuilding.Pieces</c>), none once it is bought or ruined; <paramref name="Machine"/> marks a town's building whose record is a machine
/// its workers operate (a farm, a mine, a refinery: <c>ProductionSystem.TownWork</c>).
/// </summary>
public sealed record WorldBuildingInfo(
    string Name,
    string? Type,
    string? Town,
    string? Owner,
    bool PublicDay = false,
    bool PublicNight = false,
    bool Destroyed = false,
    bool Turret = false,
    IReadOnlyList<Data.World.TownPiece>? Pieces = null,
    bool Machine = false);

/// <summary>
/// What the game interface needs from the host to run build mode: the catalog, names, the parts of a building, the placement services and the ghost
/// drawing.
/// </summary>
public sealed class BuildServices
{
    /// <summary>
    /// The build menu's lists.
    /// </summary>
    public required BuildMenu Menu { get; init; }
    /// <summary>
    /// An ITEM record's name.
    /// </summary>
    public required Func<string, string> ItemName { get; init; }
    /// <summary>
    /// A BUILDING's description text.
    /// </summary>
    public required Func<string, string> Description { get; init; }
    /// <summary>
    /// A BUILDING's parts laid out at the origin with no rotation (the same list for the same record).
    /// </summary>
    public required Func<string, IReadOnlyList<PlacedMesh>> Parts { get; init; }
    /// <summary>
    /// The game's saved picture of a BUILDING (<c>data/icons/buildings</c>), null when none: the window then shows the renderer's drawing.
    /// </summary>
    public Func<string, string?> IconFile { get; init; } = _ => null;
    /// <summary>
    /// The ground, footprints and collision lookups the placement checks use.
    /// </summary>
    public required PlacementServices Placement { get; init; }
    /// <summary>
    /// The placed towns, for <c>PlacementRules.TownBlock</c>.
    /// </summary>
    public required Func<IReadOnlyList<PlacementTown>> Towns { get; init; }
    /// <summary>
    /// The player's faction id, for the town ownership checks.
    /// </summary>
    public required Func<string> PlayerFactionId { get; init; }
    /// <summary>
    /// What the panel shows of a level-data building: its name, type, town, owner faction and public flags.
    /// </summary>
    public Func<BuildingPlacement, WorldBuildingInfo> WorldInfo { get; init; } = p => new(p.BuildingId, null, null, null);
    /// <summary>
    /// Hands the renderer the ghosts to draw this frame (empty: none).
    /// </summary>
    public required Action<IReadOnlyList<WorldObjectRenderer.GhostDraw>> DrawGhosts { get; init; }
}