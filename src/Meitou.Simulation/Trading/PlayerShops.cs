using Meitou.Data.World;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Trading;

/// <summary>
/// A building of a town that the player owns, as a shop for the town's shoppers (docs/game/ai-tasks.md "Town goals", 118 Shopping): the original's
/// shop test (<c>Building::isAShop</c>) has a branch for a building of the player's faction (<b>Observed</b>, disassembly): one with an interior,
/// holding a shop counter (function 9), and public is a shop with no residents needed. The goods for sale are what the shop counters the player
/// built inside it hold. Confirmed in the original (1.0.68): a townsperson of Squin bought from a Shop Counter the player built in a house bought
/// there and set public.
/// </summary>
static class PlayerShops
{
    /// <summary>
    /// The storages of the complete, standing shop counters <paramref name="player"/> (a faction) built inside buildings, by the building's key, in
    /// the order of the player's buildings: one pass for every building a refresh looks at.
    /// </summary>
    public static Dictionary<string, List<Inventory>> ByBuilding(World world, int player)
    {
        var counters = new Dictionary<string, List<Inventory>>(StringComparer.Ordinal);

        foreach (var site in world.Buildings.All)
        {
            if (CounterOf(world, site, player) is not { } storage) continue;

            // The parent is the building's instance id, or (from the original's save) its full key.
            string key = site.Parent!.Contains(':') ? site.Parent : BuildingKey.Of(site.Zone, site.Parent);
            (counters.TryGetValue(key, out var list) ? list : counters[key] = []).Add(storage);
        }

        return counters;
    }

    /// <summary>
    /// The storages of the shop counters <paramref name="player"/> built inside <paramref name="building"/>, in the order of the player's buildings;
    /// none for a building the player does not own.
    /// </summary>
    public static IEnumerable<Inventory> Counters(World world, TownBuilding building, int player)
    {
        if (!world.Fixtures.OwnsBuilding(building.Key, player)) yield break;

        string key = building.Key;
        foreach (var site in world.Buildings.All)
        {
            if (CounterOf(world, site, player) is not { } storage) continue;
            if (site.Parent == key || site.Parent == building.InstanceId && site.Zone == building.Zone) yield return storage;
        }
    }

    /// <summary>
    /// The storage of <paramref name="site"/> when it is a complete, standing shop counter <paramref name="player"/> built inside a building.
    /// </summary>
    static Inventory? CounterOf(World world, Construction.BuildSite site, int player)
    {
        if (player < 0 || !site.Complete || site.Destroyed || site.Owner != player || site.Parent is null) return null;

        return world.Machines.Find(site.Id) is { Data.Function: TownBuildings.ShopFunction, Storage: { } storage } ? storage : null;
    }
}
