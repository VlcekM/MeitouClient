using System.Numerics;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The pieces sitting around (275) and reigning (255) can take in an outpost a squad has as its town (<see cref="TownPlaceIndex"/>): the complete,
/// standing chairs, thrones and training dummies the player built there, each stood in for by a <see cref="TownBuilding"/> whose one piece of
/// <see cref="TownBuilding.Furniture"/> it is. The stand-ins are kept across refreshes, as the sitting claims hold their building by reference
/// (<see cref="TownPlaceClaims.ClaimSitting"/>); one whose piece changed gets a new one. Derived, not hashed. Meitou only: the original plans no
/// goals for the player's squads.
/// </summary>
sealed class OutpostSeats(PopulationData data)
{
    /// <summary>
    /// The stand-in of each seat piece, by site id (derived, not hashed).
    /// </summary>
    readonly Dictionary<string, TownBuilding> _standIns = new(StringComparer.Ordinal);

    /// <summary>
    /// The first functionality of each building record, by id (<see cref="TownBuildings.FunctionOf"/>); derived from the data.
    /// </summary>
    readonly Dictionary<string, GameRecord?> _functions = new(StringComparer.Ordinal);

    /// <summary>
    /// The seats of outpost <paramref name="number"/> (<see cref="TownPlaces.Seats"/>, each piece its stand-in's seat 0) and the faction owning
    /// each (<see cref="TownPlaces.SeatOwners"/>: the building it stands in, else its own), in the order of the player's buildings; their sites are
    /// noted in <paramref name="seen"/>.
    /// </summary>
    public (List<SeatClaim> Seats, Dictionary<TownBuilding, int> Owners) Of(World world, int number, HashSet<string> seen)
    {
        var seats = new List<SeatClaim>();
        var owners = new Dictionary<TownBuilding, int>(ReferenceEqualityComparer.Instance);

        foreach (var site in world.Buildings.All)
        {
            if (!site.Complete || site.Destroyed || !TownPlaceIndex.InOutpost(world, site, number)) continue;
            var building = site.Parent is { } parent ? world.Buildings.Find(parent) : site;
            if (building is null || !Open(world, building) || StandIn(world, site) is not { } piece) continue;

            seats.Add(new SeatClaim(piece, 0));
            owners[piece] = building.Owner;
            seen.Add(site.Id);
        }

        return (seats, owners);
    }

    /// <summary>
    /// Forgets the stand-ins of the sites not in <paramref name="seen"/> (nothing to do, and nothing allocated, while there are none).
    /// </summary>
    public void Prune(HashSet<string> seen)
    {
        if (_standIns.Count == 0) return;

        foreach (string gone in _standIns.Keys.Where(id => !seen.Contains(id)).ToList()) _standIns.Remove(gone);
    }

    /// <summary>
    /// Whether the members may go to a piece in <paramref name="building"/> (the building it stands in, else the piece itself): public, as the
    /// buildings they wander into, and not shut by all its doors locked (the AI-run squad keeps to the player's locks,
    /// <see cref="SquadAutonomy.KeepsPlayerLocks"/>).
    /// </summary>
    static bool Open(World world, BuildSite building) => building.Public && !world.Fixtures.AllDoorsLocked(building.Id);

    /// <summary>
    /// The stand-in of <paramref name="site"/> when it is a chair, throne or training dummy (<see cref="TownBuildings.FurnitureSpot"/>,
    /// <see cref="TownPlaceClaims.Sittable"/>); null otherwise.
    /// </summary>
    TownBuilding? StandIn(World world, BuildSite site)
    {
        if (data.Db.Find(site.Record) is not { } record) return null;

        float yaw = TownBuildings.YawOf(site.Rotation);
        if (TownBuildings.FurnitureSpot(data.Db, record, site.Position, yaw) is not { } spot) return null;
        if (!TownPlaceClaims.Sittable(spot.Kind, dummies: true)) return null;

        spot = Facing(world, site, record, spot);
        bool same = _standIns.TryGetValue(site.Id, out var known) && known.BuildingId == site.Record && known.Furniture[0] == spot;
        if (same) return known!;

        return _standIns[site.Id] = new TownBuilding(site.Id, site.Record, site.Name, site.Position, yaw, false, false, [])
        {
            RuntimeKey = site.Id,
            Zone = site.Zone,
            Furniture = [spot],
        };
    }

    /// <summary>
    /// A chair's sitter facing, as the player's chair takes it (docs/game/ai-tasks.md "Chairs"; <c>ProductionSystem.SeatFacing</c>): by its
    /// functionality's <c>flags</c>, towards the nearest complete table, corpse disposal or refinery in the same building, else (a chair standing
    /// on its own) in the same outpost; <see cref="HomeSpot.FaithfulYaw"/> the original's, <see cref="HomeSpot.Yaw"/> the fixed one. Other pieces
    /// keep their own facing.
    /// </summary>
    HomeSpot Facing(World world, BuildSite site, GameRecord record, HomeSpot spot)
    {
        if (spot.Kind != HomeSpotKind.Chair) return spot;
        int flags = TownBuildings.FunctionOf(data.Db, record, _functions)?.GetInt("flags") ?? 0;
        if (flags is not (1 or 2)) return spot;

        var faced = Faced(world, site).ToList();
        return spot with
        {
            Yaw = TownBuildings.SeatYaw(spot.Position, spot.PlacementYaw, flags, faced),
            FaithfulYaw = TownBuildings.SeatYaw(spot.Position, spot.PlacementYaw, flags, faced, faithful: true),
        };
    }

    /// <summary>
    /// The furniture a chair faces (<see cref="Facing"/>): the town house's own tables when it stands in one, then the complete tables, corpse
    /// disposals and refineries the player built in the same building (or, standing on its own, in the same outpost).
    /// </summary>
    IEnumerable<Vector3> Faced(World world, BuildSite site)
    {
        if (site.Parent is { } parent)
            foreach (var at in world.Fixtures.TablesOfBuilding(parent)) yield return at;

        foreach (var other in world.Buildings.All)
        {
            bool together = other.Parent == site.Parent && (site.Parent is not null || other.Town == site.Town);
            if (!other.Complete || other.Destroyed || !together) continue;
            if (data.Db.Find(other.Record) is not { } record) continue;

            if (TownBuildings.FunctionOf(data.Db, record, _functions)?.GetInt("function")
                is TownBuildings.TableFunction or TownBuildings.CorpseDisposalFunction or TownBuildings.RefineryFunction)
                yield return other.Position;
        }
    }
}
