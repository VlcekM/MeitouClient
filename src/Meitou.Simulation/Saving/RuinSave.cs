using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// Town ruins in a save, and the houses a town override stood up again (docs/formats/save.md "Zone files"). Setting a house destroyed destroys its
/// layout objects with their inventories (docs/game/buildings-production.md "A ruin's objects take their items with them"), so the original saves
/// no building inside a ruin; a save keeps the loaded zone files, which still list the objects of a house that stood when they were written. Each
/// such entry goes with its state and its inventory; the house's <c>internalbuildings-&lt;k&gt;</c> handles and the entries' slots stay, as the
/// original leaves them. So a ruin that stands again after the load has no saved state for its objects, which start new.
/// </summary>
public static class RuinSave
{
    /// <summary>
    /// The building state key that keeps the original's building factory from making the interior again (docs/formats/save.md "Zone files").
    /// </summary>
    const string SerialisedKey = "serialised interior";

    /// <summary>
    /// Marks every saved ruin's interior as serialised, removes every building-list entry that stands inside a town building the world holds as a
    /// ruin, the save writes as one (<c>destroyed</c> true in <paramref name="states"/>, the zones' building states by key) and is not the player's,
    /// or inside a home an override furnished anew (<see cref="Rebuilt"/>), and lets the original furnish the houses an override stood up again or
    /// furnished anew that hold no saved object.
    /// </summary>
    public static void Write(SaveGame game, World world, PopulationData data, IReadOnlyDictionary<string, FcsRecord> states)
    {
        foreach (var zone in game.Zones.Values) MarkRuins(zone);

        var gone = Ruins(world, data, states);
        gone.UnionWith(Rebuilt(world, data, states));
        if (gone.Count > 0)
        {
            foreach (var zone in game.Zones.Values) DropFurniture(zone, gone);
        }

        UnmarkStood(game, world, data, states);
    }

    /// <summary>
    /// The handles (container, slot) of the standing homes a town override furnished anew (<see cref="Locks.FixtureRegistry.IsRebuilt"/>): their
    /// saved objects are the old layout's, which the override's housing destroyed (<b>Observed</b>, Beep Fan Club 1: every home of Stack's and
    /// Okran's Shield's replacements holds its new template's objects; docs/game/factions-squads-towns.md "Town overrides").
    /// </summary>
    static HashSet<(int C, int I)> Rebuilt(World world, PopulationData data, IReadOnlyDictionary<string, FcsRecord> states)
    {
        var rebuilt = new HashSet<(int C, int I)>();
        foreach (var site in data.Sites)
        {
            foreach (var b in site.Buildings)
            {
                if (!world.Fixtures.IsRebuilt(b.Key) || world.Fixtures.IsDestroyed(b)) continue;
                if (states.GetValueOrDefault(b.Key) is { } state && Hand.Read(state, "handle") is { IsNull: false } hand) rebuilt.Add((hand.C, hand.I));
            }
        }

        return rebuilt;
    }

    /// <summary>
    /// Gives each ruin's state of the zone <c>serialised interior</c> 1 when it has none (a zone file Meitou made from the level data): the
    /// original's own saved ruins all have it, and without it its building factory makes the interior from the state's layout name, a ruin's too
    /// (docs/game/buildings-production.md "A ruin's objects take their items with them").
    /// </summary>
    static void MarkRuins(SaveZone zone)
    {
        foreach (var state in zone.BuildingStates)
            if (state.Bools.GetValueOrDefault("destroyed") && !state.Ints.ContainsKey(SerialisedKey)) state.Ints[SerialisedKey] = 1;
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

    /// <summary>
    /// Takes <c>serialised interior</c> off each house a town override reset or furnished anew that stands with its interior layout placing objects,
    /// when no saved object stands inside it (a ruin stood up again, a home furnished anew: the original's override makes its objects anew, which
    /// Meitou does not write), so the original makes its interior from its layout names on load as for a fresh zone; with the key it would load the
    /// house empty.
    /// </summary>
    static void UnmarkStood(SaveGame game, World world, PopulationData data, IReadOnlyDictionary<string, FcsRecord> states)
    {
        var parents = Parents(game);
        foreach (var site in data.Sites)
        {
            foreach (var b in site.Buildings)
            {
                if (!b.Furnished || world.Fixtures.IsDestroyed(b)) continue;
                if (!world.Fixtures.IsReset(b.Key) && !world.Fixtures.IsRebuilt(b.Key)) continue;
                if (states.GetValueOrDefault(b.Key) is not { } state || Hand.Read(state, "handle") is not { IsNull: false } hand) continue;

                if (!parents.Contains((hand.C, hand.I))) state.Ints.Remove(SerialisedKey);
            }
        }
    }

    /// <summary>
    /// The handles (container, slot) of the buildings some saved building stands inside.
    /// </summary>
    static HashSet<(int C, int I)> Parents(SaveGame game)
    {
        var parents = new HashSet<(int C, int I)>();
        foreach (var zone in game.Zones.Values)
        {
            foreach (var state in zone.BuildingStates)
            {
                if (state.Bools.GetValueOrDefault("is inside building") && Hand.Read(state, "isInsideBuilding") is { IsNull: false } parent)
                    parents.Add((parent.C, parent.I));
            }
        }

        return parents;
    }
}
