using System.Numerics;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Items;

/// <summary>
/// The ownership test the AI's loose-item finders ask (<c>FUN_140997d80</c>, <b>Verified</b>; docs/game/character-stats.md "Food on the ground",
/// docs/game/buildings-production.md "Material delivery"): 259's ground pass and the source finder <c>FUN_14059d000</c> (building materials, a
/// machine's inputs).
/// </summary>
internal static class ItemOwnership
{
    /// <summary>
    /// Whether the character standing at <paramref name="at"/> may take the loose item <paramref name="g"/>: one with no owner, or of its faction;
    /// one of another faction only while the character's status town is a player faction's (<see cref="InPlayerTown"/>) and the item lies outdoors
    /// or in a building of the player's faction. A world without population data has no status towns.
    /// </summary>
    public static bool MayTake(World world, PopulationData? data, CharacterCold cold, Vector3 at, GroundItem g)
    {
        if (g.OwnedBy < 0 || g.OwnedBy == cold.Faction) return true;
        if (data is null || !InPlayerTown(world, data, at, cold)) return false;

        int place = world.Sight.PlaceAt(g.Position);
        if (place < 0) return true;

        return world.Buildings.Find(world.Sight.PlaceKey(place)) is { } building && building.Owner == world.Player.Faction;
    }

    /// <summary>
    /// Whether the character's status town (status <c>+0x88</c>, as its next status update writes it: <see cref="Ai.AiSystem.StatusTownAt"/>) is of a
    /// player faction (the town's faction's <c>+0x250</c>): a player outpost the player owns, or a placed town of the player's faction.
    /// </summary>
    static bool InPlayerTown(World world, PopulationData data, Vector3 at, CharacterCold cold)
    {
        if (world.Player.Faction < 0) return false;

        var town = Ai.AiSystem.StatusTownAt(data, world, at, cold.StatusTown);
        if (town.Site >= 0) return town.Site < data.SiteFactions.Count && data.SiteFactions[town.Site] == world.Player.Faction;

        return town.PlayerTown > 0 && world.Buildings.FindTown(town.PlayerTown) is { } built && built.Owner == world.Player.Faction;
    }
}
