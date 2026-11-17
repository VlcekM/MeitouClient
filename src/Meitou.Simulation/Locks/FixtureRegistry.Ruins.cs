using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Locks;

/// <summary>
/// A town building a town override has ruined, or stood up again, against what the level data has, or one laid out afresh (its placement names its
/// layouts, <see cref="FixtureRegistry.LayoutsFromSave"/>); <see cref="FixtureRegistry.Ruins"/>.
/// </summary>
public sealed record RuinedBuilding(TownBuilding Building, bool Destroyed);

/// <summary>
/// The registry's town ruins: the buildings a town override ruined or restored.
/// </summary>
public sealed partial class FixtureRegistry
{
    /// <summary>
    /// The town buildings whose ruin state a town override changed from the level data's, by key (docs/game/buildings-production.md "Ruined
    /// by residents").
    /// </summary>
    readonly Dictionary<string, RuinedBuilding> _ruins = new(StringComparer.Ordinal);

    /// <summary>
    /// The town buildings whose ruin state differs from the level data's or that were laid out afresh (the host draws, paths and lights them so);
    /// <see cref="RuinsRevision"/> counts changes.
    /// </summary>
    public IReadOnlyCollection<RuinedBuilding> Ruins => _ruins.Values;
    /// <summary>
    /// Counts changes of <see cref="Ruins"/>.
    /// </summary>
    public long RuinsRevision { get; private set; }
    /// <summary>
    /// The cache of the published ruins and the revision it was made at.
    /// </summary>
    (long Revision, RuinedBuilding[] List) _publishedRuins = (0, []);

    /// <summary>
    /// <see cref="Ruins"/> as an array made again only when they changed (the snapshot's).
    /// </summary>
    public IReadOnlyList<RuinedBuilding> PublishedRuins()
    {
        if (_publishedRuins.Revision != RuinsRevision) _publishedRuins = (RuinsRevision, [.. _ruins.Values]);
        return _publishedRuins.List;
    }

    /// <summary>
    /// Whether a town building is a ruin now: as a town override left it, else as its save or the level data has it.
    /// </summary>
    public bool IsDestroyed(TownBuilding b) => _ruins.TryGetValue(b.Key, out var r) ? r.Destroyed : SavedDestroyed(b);

    /// <summary>
    /// The ruins of a replaced town's residents (the original reads the town's zone back from the level file and houses the replacement's residents,
    /// whose ruined templates ruin their homes and whose others stand theirs up, <c>FUN_14057f1f0</c>), for its buildings that are not the player's:
    /// in its own zone a home is a ruin as its template says, any other building as its level file has it; in other zones (which keep their state)
    /// a home of a ruined template becomes a ruin, and one saved standing stands. A loaded save applies them again from its replacements.
    /// </summary>
    public void ApplyTownRuins(World world, PopulationData data, int site)
    {
        var town = data.Sites[site];
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        HashSet<string> own = [.. town.Buildings.Where(b => b.Zone == town.Zone && !IsPlayers(data, site, b, player)).Select(b => b.Key)];
        RelayTown(world, data, site,
                own.Where(key => Saved?.GetValueOrDefault(key)?.Strings.ContainsKey(ResidentRuins.InteriorKey) != true).ToHashSet());
        ApplyTownRuins(world, data, site, own);
    }

    /// <summary>
    /// Applies a town's resident ruins: the homes its override ruins are destroyed, the ones it no longer ruins restored. In other zones a home
    /// stands when its template stands it right after its override housed the residents (<paramref name="housed"/>), or when the save has it
    /// standing (a level ruin the override stood up, which the level data would ruin again).
    /// </summary>
    void ApplyTownRuins(World world, PopulationData data, int site, HashSet<string> reset, bool housed = false)
    {
        var town = data.Sites[site];
        int factionIndex = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        var faction = factionIndex >= 0 ? data.Factions[factionIndex] : null;
        string? playerId = data.PlayerFaction >= 0 ? data.Factions[data.PlayerFaction].Id : null;
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        var homes = ResidentRuins.HomeTemplates(data.Db, town.Town, faction, town.Buildings, playerId, PlayerOwnedIn(data, site, player));
        foreach (var b in town.Buildings.ToList())
        {
            bool home = homes.TryGetValue(b.Key, out var template);
            bool ruined = home && ResidentRuins.Ruins(data.Db, template!, b);
            if (reset.Contains(b.Key)) SetRuin(world, data, site, b, home ? ruined : b.LevelDestroyed);
            else if (IsPlayers(data, site, b, player)) continue;
            else if (ruined || home && (housed || SavedIntact(b))) SetRuin(world, data, site, b, ruined);
        }
    }

    /// <summary>
    /// Whether the loaded save has a town building standing (<c>destroyed</c> false).
    /// </summary>
    bool SavedIntact(TownBuilding b) => Saved?.GetValueOrDefault(b.Key) is { } saved && !saved.Bools.GetValueOrDefault("destroyed", true);

    /// <summary>
    /// A loaded save's ruins of standing level buildings that are not the player's (an override's homes outside its zone, kept by later overrides
    /// too): their saved <c>destroyed</c>; and its standing homes that housing would ruin now (<see cref="SavedStanding"/>). Before
    /// <see cref="ApplyTownRuins(World, PopulationData, int)"/> sets the replaced towns' own zones.
    /// </summary>
    public void RuinsFromSave(World world, PopulationData data)
    {
        if (Saved is null) return;
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        foreach (var site in data.Sites)
        {
            foreach (var b in site.Buildings)
            {
                if (Saved.GetValueOrDefault(b.Key) is not { } saved || IsPlayers(data, site.Index, b, player)) continue;

                if (!b.Destroyed && saved.Bools.GetValueOrDefault("destroyed") && !SavedLevelRuin(b, saved))
                    SetRuin(world, data, site.Index, b, true);
                else if (SavedStanding(data.Db, b, saved))
                    SetRuin(world, data, site.Index, b, false);
            }
        }
    }

    /// <summary>
    /// Whether a home that housing ruins now stands in the save: the save has it intact as the home of another template, one that ruins no
    /// building (the housing differs from the one the save was made under, e.g. a mod added residents to the town: The Hub's bar, issue #673).
    /// The save's residents live there, so its state holds.
    /// </summary>
    static bool SavedStanding(Data.GameDatabase db, TownBuilding b, Data.Fcs.FcsRecord saved) =>
        b.RuinedByResidents && !saved.Bools.GetValueOrDefault("destroyed", true)
        && saved.Strings.GetValueOrDefault(ResidentRuins.ResidentsTemplateKey) is { Length: > 0 } template && template != b.ResidentsTemplate
        && db.Find(template) is { } record && !record.GetBool("building ruined");

    /// <summary>
    /// A registered building that became a ruin loses what a ruin has none of (<see cref="FixturesOf"/>): its doors and own lock, its interior's
    /// objects with their contents and its seats, and its item slots with the items lying in them (setting a house destroyed destroys its
    /// interior's objects and item groups with their items, docs/game/buildings-production.md "A ruin's objects take their items with them").
    /// Whoever lies or is held in one is let out first, as when a bought house is emptied.
    /// </summary>
    void Ruin(World world, TownBuilding b)
    {
        RemoveSeats(b.Key);
        world.ItemSlots.EmptyBuilding(world, b);
        if (!_byBuilding.TryGetValue(b.Key, out var list)) return;

        var kept = new List<Fixture>();
        AddDummies(-1, b, -1, kept, layoutObjects: false);
        var keep = kept.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var f in list.Where(f => !keep.Contains(f.Id)).ToArray())
        {
            LetOut(world, f);
            list.Remove(f);
            _all.Remove(f);
            _byId.Remove(f.Id);
        }

        _containerSpots = null;
        _doorPicks = null;
        _doorsByBuilding = null;
        _bedSpots = null;
        _bedList = null;
        _cageList = null;
        _doorList = null;
        _publishedCages = null;
        _dummySpots = null;
        _doors?.SetDoorsBlocked(b.Key, false);
    }

    /// <summary>
    /// Writes each ruined or stood-up building's <c>destroyed</c> into its saved record (those outside a reset zone have no other writer of it), and
    /// the layout names of each one laid out afresh; a building listed only for its layouts keeps its saved <c>destroyed</c>, but a home an override
    /// furnished anew takes the state the override left (a saved ruin it stood up stands as the level has it).
    /// </summary>
    void WriteRuins(IReadOnlyDictionary<string, Data.Fcs.FcsRecord> states)
    {
        foreach (var (key, ruin) in _ruins)
        {
            if (!states.TryGetValue(key, out var state)) continue;
            if (ruin.Destroyed != ruin.Building.Destroyed || _rebuilt.Contains(key)) state.Bools["destroyed"] = ruin.Destroyed;
            WriteLayouts(ruin.Building, state);
        }
    }

    /// <summary>
    /// Sets a town building's ruin state. One laid out as a ruin that stands now is laid out standing from the layout names it has (housing
    /// furnishes it from them: docs/game/factions-squads-towns.md "Town residents"); in a registered town its fixtures and item slots come when the
    /// town registers it again (<see cref="TownReplaced"/>). A registered one that became a ruin loses them (<see cref="Ruin"/>).
    /// </summary>
    void SetRuin(World world, PopulationData data, int site, TownBuilding b, bool destroyed)
    {
        bool was = IsDestroyed(b);
        if (!destroyed) b = data.Stand(site, b);
        ListRuin(b, destroyed);
        if (was == destroyed || !_sites.Contains(site)) return;

        if (destroyed) Ruin(world, b);
        if (data.Sight is not { } sight) return;
        if (destroyed) world.Sight.Remove(b.Key);
        else world.Sight.Restore(sight, b);
    }

    /// <summary>
    /// Keeps a town building in <see cref="Ruins"/> while its ruin state differs from the level data's or the save's (a saved ruin an override
    /// stood up) or it was laid out afresh.
    /// </summary>
    void ListRuin(TownBuilding b, bool destroyed)
    {
        bool listed = destroyed != b.Destroyed || Relaid(b) || destroyed != SavedDestroyed(b);
        bool changed = !listed ? _ruins.Remove(b.Key) : !_ruins.TryGetValue(b.Key, out var old) || old.Destroyed != destroyed
                || !ReferenceEquals(old.Building, b);
        if (listed) _ruins[b.Key] = new RuinedBuilding(b, destroyed);
        if (changed) RuinsRevision++;
    }

    /// <summary>
    /// A building leaves the ruins list when the player takes it over: its construction site shows and keeps its state from then on.
    /// </summary>
    void DropRuin(string building)
    {
        if (_ruins.Remove(building)) RuinsRevision++;
    }

    /// <summary>
    /// The keys of a town's buildings that are the player's (<see cref="IsPlayers"/>): its residents are never housed in them
    /// (docs/game/factions-squads-towns.md "Town residents").
    /// </summary>
    public HashSet<string> PlayerOwnedIn(PopulationData data, int site, int player) =>
        [.. data.Sites[site].Buildings.Where(b => IsPlayers(data, site, b, player)).Select(b => b.Key)];

    /// <summary>
    /// Whether a town building is the player's: bought or owned, else (its town not registered) saved as theirs.
    /// </summary>
    bool IsPlayers(PopulationData data, int site, TownBuilding b, int player) =>
        _properties.TryGetValue(b.Key, out var p) ? p.Purchased || p.Owner == player
        : _sites.Contains(site) ? OfBuilding(b.Key).Any(f => f.Owner == player)
        : Saved?.GetValueOrDefault(b.Key)?.Strings.GetValueOrDefault("owner faction ID") is { Length: > 0 } id && data.FactionIndex(id) == player;
}