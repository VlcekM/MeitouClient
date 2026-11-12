using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Locks;

/// <summary>
/// Town buildings laid out with other layout names than the boot gave them (docs/game/factions-squads-towns.md "Town residents", Layouts): a town
/// override houses its residents afresh, and a loaded save's building states name the layouts the original furnished them with. Each such building
/// replaces the boot's in its town (<see cref="PopulationData.Relay"/>) and is published with <see cref="Ruins"/>, whose placement carries the names,
/// so the host draws, paths and lights it so.
/// </summary>
public sealed partial class FixtureRegistry
{
    /// <summary>
    /// A loaded save's layouts: every town building whose saved state names its interior layout takes the saved interior and exterior names (the
    /// original's furniture was generated from them). Before the replaced towns' own zones are laid out
    /// (<see cref="ApplyTownRuins(World, PopulationData, int)"/>).
    /// </summary>
    public void LayoutsFromSave(World world, PopulationData data)
    {
        if (Saved is null) return;
        foreach (var site in data.Sites)
        {
            foreach (var b in site.Buildings.ToList())
            {
                if (Saved.GetValueOrDefault(b.Key) is { } saved && saved.Strings.TryGetValue(ResidentRuins.InteriorKey, out string? interior))
                    Relay(world, data, site.Index, b, interior, saved.Strings.GetValueOrDefault(ResidentRuins.ExteriorKey, ""));
            }
        }
    }

    /// <summary>
    /// Houses town <paramref name="site"/>'s residents afresh in its buildings <paramref name="keys"/> (a town override reads its zone back from the
    /// level file and houses the replacement's residents there): a home takes its template's layouts and naming, any other building its level files'
    /// (<see cref="TownBuilding.LevelLayouts"/>, <see cref="TownBuilding.LevelNaming"/>).
    /// </summary>
    void RelayTown(World world, PopulationData data, int site, IReadOnlySet<string> keys)
    {
        var town = data.Sites[site];
        int factionIndex = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        var faction = factionIndex >= 0 ? data.Factions[factionIndex] : null;
        string? playerId = data.PlayerFaction >= 0 ? data.Factions[data.PlayerFaction].Id : null;
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        var homes = ResidentRuins.HomeTemplates(data.Db, town.Town, faction, town.Buildings, playerId, PlayerOwnedIn(data, site, player));
        foreach (var b in town.Buildings.ToList())
        {
            if (!keys.Contains(b.Key)) continue;
            if (homes.TryGetValue(b.Key, out var t))
                Relay(world, data, site, b, t.LayoutInterior, t.LayoutExterior, ResidentRuins.Naming(t, b.LevelNaming));
            else Relay(world, data, site, b, b.LevelLayouts.Interior, b.LevelLayouts.Exterior, b.LevelNaming);
        }
    }

    /// <summary>
    /// Lays one building out afresh: its town takes the new one, the published list has it, a registered town's item slots follow, and a standing
    /// one's room and lamps. A registered town's fixtures follow when it is registered again (<see cref="TownReplaced"/>). A building only renamed
    /// (<paramref name="naming"/>) keeps the rest.
    /// </summary>
    void Relay(World world, PopulationData data, int site, TownBuilding b, string interior, string exterior, BuildingNaming? naming = null)
    {
        bool destroyed = IsDestroyed(b);
        var relaid = data.Relay(site, b, interior, exterior, naming);
        if (relaid.LayoutName(exterior: false) == b.LayoutName(exterior: false) && relaid.LayoutName(exterior: true) == b.LayoutName(exterior: true))
            return;

        _ruins[b.Key] = new RuinedBuilding(relaid, destroyed);
        RuinsRevision++;
        world.ItemSlots.RelayBuilding(world, data, site, b, relaid);
        if (!destroyed && _sites.Contains(site) && data.Sight is { } sight) world.Sight.Relay(sight, relaid);
    }

    /// <summary>
    /// A building laid out afresh: its placement names its layouts (<see cref="TownBuildings.Relay"/>).
    /// </summary>
    static bool Relaid(TownBuilding b) => b.Resolved?.Placement.InteriorLayout is not null;

    /// <summary>
    /// Writes a building's layout names into its saved record when it was laid out afresh.
    /// </summary>
    static void WriteLayouts(TownBuilding b, Data.Fcs.FcsRecord state)
    {
        if (b.Resolved?.Placement is not { InteriorLayout: { } interior } placement) return;
        state.Strings[ResidentRuins.InteriorKey] = interior;
        state.Strings[ResidentRuins.ExteriorKey] = placement.ExteriorLayout ?? "";
    }
}