using Meitou.Data.World;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Towns;

/// <summary>
/// Town buildings added and taken away while the world runs (the level editor, docs/game/level-editor.md "Simulation side"), on the simulation thread
/// at a tick boundary (<see cref="DebugPlaceBuilding"/>, <see cref="DebugClearBuilding"/>). The town's list in <see cref="PopulationData"/> is
/// replaced by a changed copy; a town the registries already hold (<see cref="Locks.FixtureRegistry.IsRegistered"/>) gets the building registered or
/// removed at once, any other when it is first registered, from the list.
/// </summary>
static class TownEdits
{
    /// <summary>
    /// Places <paramref name="building"/> in <paramref name="town"/>, replacing a building of the same key, and registers it when the town
    /// is registered.
    /// </summary>
    public static void Place(World world, PopulationData data, string town, TownBuilding building)
    {
        Clear(world, data, building.Key);   // a key placed again replaces the old one
        if (data.PutBuilding(town, building) is not (>= 0 and var site)) return;
        world.Fixtures.RegisterAdded(world, data, site, building);
        world.ItemSlots.PlaceBuilding(world, data, site, building);
    }

    /// <summary>
    /// Takes the building of <paramref name="key"/> away, and removes it from the registries when its town is registered.
    /// </summary>
    public static void Clear(World world, PopulationData data, string key)
    {
        if (data.TakeBuilding(key) is not ( >= 0 and var site, { } taken)) return;
        if (!world.Fixtures.IsRegistered(site)) return;
        world.Fixtures.RemoveBuilding(world, key);
        world.ItemSlots.RemoveBuilding(taken);
    }
}