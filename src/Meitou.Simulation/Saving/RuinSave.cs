using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The furniture of town ruins in a save (docs/formats/save.md "Zone files"). Setting a house destroyed destroys its layout objects with their
/// inventories (docs/game/buildings-production.md "A ruin holds no furniture"), so the original saves no building inside a ruin; a save keeps the
/// loaded zone files, which still list the objects of a house that stood when they were written. Each such entry goes with its state and its
/// inventory; the house's <c>internalbuildings-&lt;k&gt;</c> handles and the entries' slots stay, as the original leaves them. So a ruin that
/// stands again after the load has no saved state for its objects, which start new.
/// </summary>
public static class RuinSave
{
    /// <summary>
    /// Removes every building-list entry that stands inside a town building the world holds as a ruin, the save writes as one (<c>destroyed</c>
    /// true in <paramref name="states"/>, the zones' building states by key) and is not the player's.
    /// </summary>
    public static void Write(SaveGame game, World world, PopulationData data, IReadOnlyDictionary<string, FcsRecord> states)
    {
        var ruins = Ruins(world, data, states);
        if (ruins.Count == 0) return;

        foreach (var zone in game.Zones.Values) DropFurniture(zone, ruins);
    }

    /// <summary>
    /// The handles (container, slot) of the town buildings that are ruins in the world and in the save, the player's left out (the buildings they
    /// placed inside one are theirs).
    /// </summary>
    static HashSet<(int C, int I)> Ruins(World world, PopulationData data, IReadOnlyDictionary<string, FcsRecord> states)
    {
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        var ruins = new HashSet<(int C, int I)>();
        foreach (var site in data.Sites)
        {
            HashSet<string>? players = null;
            foreach (var b in site.Buildings)
            {
                if (states.GetValueOrDefault(b.Key) is not { } state || !state.Bools.GetValueOrDefault("destroyed")) continue;
                if (!world.Fixtures.IsDestroyed(b) || Hand.Read(state, "handle") is not { IsNull: false } hand) continue;

                players ??= world.Fixtures.PlayerOwnedIn(data, site.Index, player);
                if (!players.Contains(b.Key)) ruins.Add((hand.C, hand.I));
            }
        }

        return ruins;
    }

    /// <summary>
    /// Removes the zone's building-list entries inside one of <paramref name="ruins"/> (<c>is inside building</c>, its <c>isInsideBuilding</c>
    /// handle): the entry, its INVENTORY_STATE with the items and its GAMESTATE_BUILDING.
    /// </summary>
    static void DropFurniture(SaveZone zone, HashSet<(int C, int I)> ruins)
    {
        if (zone.BuildingList is not { } list) return;

        var states = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in zone.BuildingStates) states[r.StringId] = r;

        foreach (var instance in list.Instances.ToList())
        {
            var state = instance.States
                .Select(id => states.GetValueOrDefault(id))
                .FirstOrDefault(r => r is not null);
            if (state is null || !InsideRuin(state, ruins)) continue;

            MachineSave.WriteInventory(zone.File, zone.Coordinate, instance, null, []);
            zone.File.Records.Remove(state);
            list.Instances.Remove(instance);
        }
    }

    /// <summary>
    /// Whether a building state stands inside one of <paramref name="ruins"/>.
    /// </summary>
    static bool InsideRuin(FcsRecord state, HashSet<(int C, int I)> ruins) =>
        state.Bools.GetValueOrDefault("is inside building") && Hand.Read(state, "isInsideBuilding") is { IsNull: false } parent
        && ruins.Contains((parent.C, parent.I));
}
