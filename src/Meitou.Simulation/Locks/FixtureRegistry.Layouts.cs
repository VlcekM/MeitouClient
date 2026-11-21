using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
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
    /// original's furniture was generated from them). When a non-player building in a saved zone has no saved interior layout name (e.g. a save whose
    /// zone file lacked its building state), falls back to the layout of a saved resident platoon whose <c>homebuilding</c> names it. Before the
    /// replaced towns' own zones are laid out (<see cref="ApplyTownRuins(World, PopulationData, int)"/>).
    /// </summary>
    public void LayoutsFromSave(World world, PopulationData data, SaveGame? save = null, SaveHandles? handles = null)
    {
        if (Saved is null) return;

        var resident = save is not null && handles is not null ? ResidentLayouts(save, handles, data) : null;
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        foreach (var site in data.Sites)
        {
            foreach (var b in site.Buildings.ToList())
            {
                if (Saved.GetValueOrDefault(b.Key) is { } saved && saved.Strings.TryGetValue(ResidentRuins.InteriorKey, out string? interior))
                    Relay(world, data, site.Index, b, interior, saved.Strings.GetValueOrDefault(ResidentRuins.ExteriorKey, ""));
                else if (resident is not null && world.SavedZones.Contains(b.Zone) && !IsPlayers(data, site.Index, b, player)
                    && TryResidentLayout(resident, b, out var fallback) && b.InteriorLayouts.Contains(fallback.Interior))
                    Relay(world, data, site.Index, b, fallback.Interior, fallback.Exterior);
            }
        }
    }

    /// <summary>
    /// Resident templates' layout names indexed by building key and by level handle, for <see cref="LayoutsFromSave"/>.
    /// </summary>
    sealed record ResidentLayoutMap(
        Dictionary<string, (string Interior, string Exterior)> ByKey,
        Dictionary<(ZoneCoordinate Zone, int Slot, int Serial), (string Interior, string Exterior)> ByHandle);

    /// <summary>
    /// Interior and exterior layout names of each saved resident platoon's template, indexed by building key (when the save holds the building state)
    /// and by level handle <c>(zone, slot, serial)</c>. Only templates with a non-empty <c>layout interior</c> are entered, so a housemate guard squad
    /// does not shadow its home's shop or bar layout.
    /// </summary>
    static ResidentLayoutMap ResidentLayouts(SaveGame save, SaveHandles handles, PopulationData data)
    {
        var byKey = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var byHandle = new Dictionary<(ZoneCoordinate, int, int), (string, string)>();
        foreach (var platoon in save.Platoons)
        {
            if (!platoon.Record.Bools.GetValueOrDefault("is resident")) continue;

            string templateId = platoon.Record.Strings.GetValueOrDefault(ResidentRuins.SquadTemplateKey, "");
            if (templateId.Length == 0 || data.Db.Find(templateId) is not { } rec || !ResidentHomes.IsTemplate(rec)) continue;

            var t = SquadTemplate.From(rec);
            if (t.LayoutInterior.Length == 0) continue;

            var h = Hand.Read(platoon.Record, "homebuilding");
            if (h.IsNull || h.Type != (int)FcsRecordType.Building) continue;

            int c = h.C - 1;
            var zone = new ZoneCoordinate(c % WorldLayout.ZoneCount, c / WorldLayout.ZoneCount);
            if (handles.Building(h) is { } state && state.StringId.EndsWith("-S23", StringComparison.Ordinal))
                byKey.TryAdd($"{zone.X}.{zone.Y}:{state.StringId[..^4]}", (t.LayoutInterior, t.LayoutExterior));
            else byHandle.TryAdd((zone, h.I, h.S), (t.LayoutInterior, t.LayoutExterior));
        }

        return new(byKey, byHandle);
    }

    /// <summary>
    /// Looks up <paramref name="b"/> in <paramref name="resident"/> by key, else by its level handle.
    /// </summary>
    static bool TryResidentLayout(ResidentLayoutMap resident, TownBuilding b, out (string Interior, string Exterior) layout) =>
        resident.ByKey.TryGetValue(b.Key, out layout) || resident.ByHandle.TryGetValue((b.Zone, b.HandleSlot, b.HandleSerial), out layout);

    /// <summary>
    /// Houses town <paramref name="site"/>'s residents afresh in its buildings <paramref name="keys"/> (a town override houses the replacement's
    /// residents): a home takes its template's layouts and naming; any other building its level files' naming (<see cref="TownBuilding.LevelNaming"/>)
    /// and, unless it stands furnished, their layouts (<see cref="TownBuilding.LevelLayouts"/>): housing leaves the furniture of a building no
    /// template takes, so a former home keeps its own (docs/game/factions-squads-towns.md "Town overrides").
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
            else if (b.Furnished && !IsDestroyed(b))
                Relay(world, data, site, b, b.LayoutName(exterior: false), b.LayoutName(exterior: true), b.LevelNaming);
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
    /// Writes a building's layout names into its saved record when it was laid out afresh and has an interior: the original's state save writes
    /// them from the interior object, ruin or not, and a building without one has none (<c>FUN_1405547f0</c>).
    /// </summary>
    static void WriteLayouts(TownBuilding b, Data.Fcs.FcsRecord state)
    {
        if (!b.HasInterior || b.Resolved?.Placement is not { InteriorLayout: { } interior } placement) return;
        state.Strings[ResidentRuins.InteriorKey] = interior;
        state.Strings[ResidentRuins.ExteriorKey] = placement.ExteriorLayout ?? "";
    }
}