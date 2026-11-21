using Meitou.Data.World;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Trading;

/// <summary>
/// A building of a town that the player owns, as a shop for the town's shoppers (docs/game/ai-tasks.md "Town goals", 118 Shopping): the original's
/// shop test (<c>Building::isAShop</c>) has a branch for a building of the player's faction (<b>Observed</b>, disassembly): one with an interior,
/// holding a shop counter (function 9), and public is a shop with no residents needed. The goods for sale are what the shop counters the player
/// built inside it hold. Confirmed in the original (1.0.68): a townsperson of Squin bought from a Shop Counter the player built in a house bought
/// there and set public. A building the player built in an outpost is a shop of that outpost the same way, for shoppers standing in it.
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
    /// The storages of the complete, standing shop counters <paramref name="player"/> built inside buildings it also built (an outpost's), by the
    /// site id of the building each stands in, in the order of the player's buildings.
    /// </summary>
    public static Dictionary<string, List<Inventory>> InBuiltBuildings(World world, int player)
    {
        var counters = new Dictionary<string, List<Inventory>>(StringComparer.Ordinal);

        foreach (var site in world.Buildings.All)
        {
            if (CounterOf(world, site, player) is not { } storage || world.Buildings.Find(site.Parent!) is null)
                continue;

            (counters.TryGetValue(site.Parent!, out var list) ? list : counters[site.Parent!] = []).Add(storage);
        }

        return counters;
    }

    /// <summary>
    /// The shop counters <paramref name="player"/> built inside <paramref name="building"/>, each its site id and storage, in the order of the
    /// player's buildings; none for a building the player does not own. A building the player built stands in for itself by its site id
    /// (<see cref="TownBuilding.RuntimeKey"/>).
    /// </summary>
    public static IEnumerable<(string Site, Inventory Storage)> Counters(World world, TownBuilding building, int player)
    {
        if (building.RuntimeKey is { } id && world.Buildings.Find(id) is { } built)
            return built.Owner == player ? InBuilt(world, id, player) : [];

        return world.Fixtures.OwnsBuilding(building.Key, player) ? InTownBuilding(world, building, player) : [];
    }

    /// <summary>
    /// The shop counters <paramref name="player"/> built inside the building it built with site id <paramref name="id"/>: site id and storage.
    /// </summary>
    static IEnumerable<(string Site, Inventory Storage)> InBuilt(World world, string id, int player)
    {
        foreach (var site in world.Buildings.All)
            if (site.Parent == id && CounterOf(world, site, player) is { } storage) yield return (site.Id, storage);
    }

    /// <summary>
    /// The shop counters <paramref name="player"/> built inside the town building <paramref name="building"/>: site id and storage.
    /// </summary>
    static IEnumerable<(string Site, Inventory Storage)> InTownBuilding(World world, TownBuilding building, int player)
    {
        string key = building.Key;
        foreach (var site in world.Buildings.All)
        {
            if (CounterOf(world, site, player) is not { } storage) continue;
            if (site.Parent == key || site.Parent == building.InstanceId && site.Zone == building.Zone) yield return (site.Id, storage);
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
