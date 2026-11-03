using Meitou.Data.Gameplay;
using Meitou.Data.World;

namespace Meitou.Simulation.Locks;

/// <summary>A town building a town override has ruined, or stood up again, against what the level data has (<see cref="FixtureRegistry.Ruins"/>).</summary>
public sealed record RuinedBuilding(TownBuilding Building, bool Destroyed);

public sealed partial class FixtureRegistry
{
    // The town buildings whose ruin state a town override changed from the level data's, by key (docs/game/buildings-production.md "Ruined by residents").
    readonly Dictionary<string, RuinedBuilding> ruins = new(StringComparer.Ordinal);

    /// <summary>The town buildings whose ruin state differs from the level data's (the host draws, paths and lights them so); <see cref="RuinsRevision"/> counts changes.</summary>
    public IReadOnlyCollection<RuinedBuilding> Ruins => ruins.Values;
    public long RuinsRevision { get; private set; }
    (long Revision, RuinedBuilding[] List) publishedRuins = (0, []);

    /// <summary><see cref="Ruins"/> as an array made again only when they changed (the snapshot's).</summary>
    public IReadOnlyList<RuinedBuilding> PublishedRuins()
    {
        if (publishedRuins.Revision != RuinsRevision) publishedRuins = (RuinsRevision, [.. ruins.Values]);
        return publishedRuins.List;
    }

    /// <summary>Whether a town building is a ruin now: as a town override left it, else as its save or the level data has it.</summary>
    public bool IsDestroyed(TownBuilding b) => ruins.TryGetValue(b.Key, out var r) ? r.Destroyed : SavedDestroyed(b);

    /// <summary>
    /// The ruins of a replaced town's residents (the original reads the zone back from the level file and houses the replacement's residents, whose ruined templates
    /// ruin their homes, <c>FUN_14057f1f0</c>), for its buildings that are not the player's in its own zone. A loaded save applies them again from its replacements.
    /// </summary>
    public void ApplyTownRuins(World world, PopulationData data, int site)
    {
        var town = data.Sites[site];
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        ApplyTownRuins(world, data, site, town.Buildings.Where(b => b.Zone == town.Zone && !IsPlayers(data, site, b, player)));
    }

    void ApplyTownRuins(World world, PopulationData data, int site, IEnumerable<TownBuilding> reset)
    {
        var town = data.Sites[site];
        int factionIndex = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        var faction = factionIndex >= 0 ? data.Factions[factionIndex] : null;
        string? player = data.PlayerFaction >= 0 ? data.Factions[data.PlayerFaction].Id : null;
        var ruined = ResidentRuins.RuinedHomes(data.Db, town.Town, faction, town.Buildings, player);
        foreach (var b in reset) SetRuin(world, data, site, b, ruined.Contains(b.Key) || b.Destroyed && !b.RuinedByResidents);
    }

    void SetRuin(World world, PopulationData data, int site, TownBuilding b, bool destroyed)
    {
        bool was = IsDestroyed(b);
        bool changed = destroyed == b.Destroyed ? ruins.Remove(b.Key) : !ruins.TryGetValue(b.Key, out var old) || old.Destroyed != destroyed;
        if (destroyed != b.Destroyed) ruins[b.Key] = new RuinedBuilding(b, destroyed);
        if (changed) RuinsRevision++;
        if (was == destroyed || !sites.Contains(site) || data.Sight is not { } sight) return;
        if (destroyed) world.Sight.Remove(b.Key);
        else world.Sight.Restore(sight, b);
    }

    /// <summary>A building leaves the ruins list when the player takes it over: its construction site shows and keeps its state from then on.</summary>
    void DropRuin(string building)
    {
        if (ruins.Remove(building)) RuinsRevision++;
    }

    /// <summary>Whether a town building is the player's: bought or owned, else (its town not registered) saved as theirs.</summary>
    bool IsPlayers(PopulationData data, int site, TownBuilding b, int player) =>
        properties.TryGetValue(b.Key, out var p) ? p.Purchased || p.Owner == player
        : sites.Contains(site) ? OfBuilding(b.Key).Any(f => f.Owner == player)
        : Saved?.GetValueOrDefault(b.Key)?.Strings.GetValueOrDefault("owner faction ID") is { Length: > 0 } id && data.FactionIndex(id) == player;
}
