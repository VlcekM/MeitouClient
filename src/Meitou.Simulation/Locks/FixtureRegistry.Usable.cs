using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The registry's usable test: which faction may use a cage, bed or container.
/// </summary>
public sealed partial class FixtureRegistry
{
    /// <summary>
    /// The weight factor of a law enforcer for a cage inside a building that is not open to the public (<c>DAT_1416a2578</c>, <b>Verified</b>; the
    /// weight floor's value).
    /// </summary>
    public const float NotPublicPolice = 1e-5f;

    /// <summary>
    /// The population data the registered buildings came from (relations, factions, towns): what the usable test reads. Derived, not hashed.
    /// </summary>
    PopulationData? _usableData;

    /// <summary>
    /// A town building's Open to public flag at registration (the saved state, else the level's <c>is public day</c>, public when absent: the AI's
    /// reading, AiSystem places); derived.
    /// </summary>
    readonly Dictionary<string, bool> _levelPublic = new(StringComparer.Ordinal);

    /// <summary>
    /// Placed buildings flagged `is inside building` (a piece of another building, saved state first, else the level's); derived.
    /// </summary>
    readonly HashSet<string> _nested = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a fixture stands inside another building, the original's parent hand (vfunc <c>0x1d8</c>) being set (<b>Observed</b>): an item of a
    /// building's interior layout, or a placed building flagged <c>is inside building</c> (the parent's own key is not kept, so its flags are the
    /// fixture building's).
    /// </summary>
    public bool IsInside(Fixture f) => f.Id != f.Building || _nested.Contains(f.Building);

    /// <summary>
    /// Whether a building is open to the public (the original's <c>Building::isPublic</c> <c>+0xc8</c>, <b>Observed</b>): as the player set it, else
    /// as its state reads, else public.
    /// </summary>
    public bool IsPublic(string building) => PublicFlagOf(building) ?? (_levelPublic.TryGetValue(building, out bool open) ? open : true);

    /// <summary>
    /// The town a fixture's building stands in (<c>FUN_1400f6c20</c>): its site, else the town whose radius holds it.
    /// </summary>
    CampaignTown TownOfFixture(World world, Fixture f) =>
        f.Site >= 0 ? CampaignTown.OfSite(f.Site) : _usableData is { } data ? AiSystem.TownAt(data, world, f.Position) : CampaignTown.None;

    /// <summary>
    /// The cage finder gathers the buildings within the town radius times this of its centre (<c>DAT_1416b1640</c>, <b>Verified</b>).
    /// </summary>
    public const float TownReach = 1.1f;

    /// <summary>
    /// Which cages the finder of goals 112/281 considers for <paramref name="carrier"/> at <paramref name="at"/> (the gatherer <c>FUN_1408cbea0</c>
    /// with <c>FUN_140a00690</c>, <b>Verified</b>): the town it stands in, else its faction's nearest town within <see cref="BedSearchRange"/>
    /// (squared 1.5625e7; nest markers, town type 8, left out), the cages within <see cref="TownReach"/> x that town's radius of its centre; plus
    /// those of the town of its squad's home when that differs. With no town at all every cage of the world qualifies. The towns' fixtures are
    /// registered (a town no one has loaded has its cages all the same). A nest building's cage of the carrier's faction always qualifies
    /// (<see cref="Fixture.Nest"/>; the original's nest is a town of its own, which the town lookup here does not list: engine reading). Pass the
    /// result to <see cref="FindCage"/> as part of its skip test; serial phases only.
    /// </summary>
    public Func<Fixture, bool> CageCandidates(World world, PopulationData data, CharacterCold carrier, Vector3 at)
    {
        var inReach = TownCandidates(world, data, carrier, at);
        return f => f.Nest && f.Owner == carrier.Faction || inReach(f.Position);
    }

    /// <summary>
    /// The building gatherer's town rule (<c>FUN_1408cbea0</c> -> <c>FUN_140a00690</c>, <b>Verified</b>) as a test on a position: within
    /// <see cref="TownReach"/> x the radius of the town the character stands in, else of its faction's nearest, or of its squad's home town;
    /// membership is by the spatial query, not the building's own town, so an outpost's storages count by where they stand. Every position when it
    /// has no town at all. Used by the cage finders and the material finder (<c>FUN_14059d000</c>, mask <c>0x4002436</c>).
    /// </summary>
    public Func<Vector3, bool> TownCandidates(World world, PopulationData data, CharacterCold carrier, Vector3 at)
    {
        _usableData ??= data;
        var town = AiSystem.TownAt(data, world, at);
        if (town.IsNone) town = NearestTownOf(world, data, carrier.Faction, at);
        if (town.IsNone) return static _ => true;
        int faction = carrier.Faction;
        var home = world.Squads.Find(carrier.SquadId) is { Town: >= 0 } squad ? CampaignTown.OfSite(squad.Town) : CampaignTown.None;
        if (home == town) home = CampaignTown.None;
        var (centre, reach) = Reach(world, data, town);
        var (homeCentre, homeReach) = home.IsNone ? default : Reach(world, data, home);
        if (town.Site >= 0) RegisterSite(world, data, town.Site);
        if (home.Site >= 0) RegisterSite(world, data, home.Site);
        return position =>
        {
            var p = new Vector2(position.X, position.Z);
            return Vector2.DistanceSquared(p, centre) <= reach * reach || !home.IsNone
                    && Vector2.DistanceSquared(p, homeCentre) <= homeReach * homeReach;
        };
    }

    /// <summary>
    /// The key of the building the carrier's squad lives in, the home weight of <see cref="FindCage"/> (the ownerships' home building,
    /// <c>FUN_1407956a0</c> +0x38, <b>Verified</b>); null when none.
    /// </summary>
    public static string? HomeOf(World world, CharacterCold carrier) =>
        world.Squads.Find(carrier.SquadId) is { } squad ? (squad.Ai?.OwnHome ?? squad.HomeBuilding)?.Key : null;

    /// <summary>
    /// A town's centre and TownReach x radius.
    /// </summary>
    static (Vector2 Centre, float Reach) Reach(World world, PopulationData data, CampaignTown town)
    {
        if (town.Site >= 0)
            return (new Vector2(data.Sites[town.Site].Position.X, data.Sites[town.Site].Position.Z), data.Sites[town.Site].Town.Radius * TownReach);

        return world.Buildings.FindTown(town.PlayerTown) is { } t ? (new Vector2(t.Position.X, t.Position.Z), t.Radius * TownReach) : default;
    }

    /// <summary>
    /// The nearest town of the faction (FUN_140927f10, X/Z) when within BedSearchRange; the placed towns by their faction, the player's by
    /// their owner.
    /// </summary>
    static CampaignTown NearestTownOf(World world, PopulationData data, int faction, Vector3 at)
    {
        var here = new Vector2(at.X, at.Z);
        var best = CampaignTown.None;
        float bestD = BedSearchRange * BedSearchRange;
        foreach (var site in data.Sites)
        {
            if (site.Town.Type == TownType.NestMarker || site.Index >= data.SiteFactions.Count || data.SiteFactions[site.Index] != faction)
                continue;

            float d = Vector2.DistanceSquared(here, new Vector2(site.Position.X, site.Position.Z));
            if (d <= bestD) (best, bestD) = (CampaignTown.OfSite(site.Index), d);
        }

        foreach (var town in world.Buildings.Towns)
        {
            float d = Vector2.DistanceSquared(here, new Vector2(town.Position.X, town.Position.Z));
            if (town.Owner == faction && d <= bestD) (best, bestD) = (CampaignTown.OfPlayer(town.Number), d);
        }

        return best;
    }

    /// <summary>
    /// <c>Ownerships::canIUseThisBuilding</c> (<c>FUN_140621230</c>, <b>Verified</b>, docs/game/locks.md) for a cage and the carrier whose squad's
    /// ownerships it reads: true for a squad with no faction, for a cage of its faction, and for one in the town its occupation holds; false when the
    /// cage owner's faction counts the squad's as an enemy; false for a fixture with an inventory; a cage on its own (no parent building) is usable
    /// in the squad's home town, one inside a building when that building is open to the public, the squad's faction is real and the owner is not the
    /// player's; else when the owner regards the squad's faction as an ally. The original also lets a carrier with a slave state use any cage, but
    /// its gatherer (<c>FUN_140a00690</c>) tests with no character and so drops these first: not modelled. Without population data only the carrier's
    /// own faction passes.
    /// </summary>
    public bool CanUse(World world, CharacterCold carrier, Fixture f) => CanUse(world, carrier, f.Owner, f);

    /// <summary>
    /// The one <c>canIUseThisBuilding</c> test (<c>FUN_140621230</c>) of every function-mask building finder: <paramref name="f"/> is the building's
    /// fixture, or null for a standalone building that is no fixture (a player's machine, with no town of its own), whose owner faction is
    /// <paramref name="owner"/>.
    /// </summary>
    public bool CanUse(World world, CharacterCold carrier, int owner, Fixture? f)
    {
        var squad = world.Squads.Find(carrier.SquadId);
        int faction = squad?.Faction ?? carrier.Faction;
        if (faction < 0 || owner == faction) return true;
        if (_usableData is not { } data) return false;
        if (f is not null && squad?.Ai is { CurrentClass: PackageClass.OccupyTown, Destination.IsNone: false } board
                && TownOfFixture(world, f) == board.Destination)
            return true;

        var relations = data.Relations;
        bool known = owner >= 0 && relations.Contains(owner) && relations.Contains(faction);
        if (known && relations.IsHostile(owner, faction) || f?.Contents is not null) return false;
        if (f is not null && !IsInside(f) && squad is { Town: >= 0 } && f.Site == squad.Town) return true;
        if (f is not null && IsInside(f) && IsPublic(f.Building) && faction < data.Factions.Count && !data.Factions[faction].NotReal
                && owner != world.Player.Faction)
            return true;

        return known && relations.IsAlly(owner, faction);
    }
}