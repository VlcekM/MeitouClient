using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The buildings the player placed (docs/simulation.md "Buildings", host seam): one thread-safe overlay on the level data's placements, shared by
/// every <see cref="ZoneGeometryGatherer"/>. <see cref="ZoneGeometryGatherer.NearBuildings"/> and so <see cref="ZoneGeometryGatherer.BuildingHash"/>
/// see it, so a zone's cache key changes with it; an empty overlay changes nothing. Readers get a snapshot, writers replace it whole.
/// </summary>
public sealed class NavRuntimeBuildings
{
    /// <summary>
    /// How far (units) a building reaches past its zone box in the gatherer (<c>BuildingReach</c>) plus the gather margin: the zones a placement
    /// can shape.
    /// </summary>
    public const float Reach = 600 + 72;

    /// <summary>
    /// Serialises the writers; readers take <see cref="_byZone"/> without it.
    /// </summary>
    readonly object _gate = new();
    /// <summary>
    /// Every placement by its instance key, under <see cref="_gate"/>.
    /// </summary>
    readonly Dictionary<string, BuildingPlacement> _all = new(StringComparer.Ordinal);
    /// <summary>
    /// The placements by the zone they were placed in: a snapshot replaced whole by each change.
    /// </summary>
    volatile Dictionary<ZoneCoordinate, BuildingPlacement[]> _byZone = [];

    /// <summary>
    /// The player's towns as the foliage placer sees them: a snapshot replaced whole (<see cref="SetFoliageTowns"/>).
    /// </summary>
    volatile PlayerFoliageTowns _foliageTowns = PlayerFoliageTowns.None;

    /// <summary>
    /// The player's towns, which keep <c>avoid towns</c> foliage away like the level's (<see cref="PlayerFoliageTowns"/>); none by default.
    /// </summary>
    public PlayerFoliageTowns FoliageTowns => _foliageTowns;

    /// <summary>
    /// Replaces the player's towns. The caller builds the zones they reach again (<see cref="NavSystem.SetPlayerTowns"/>).
    /// </summary>
    public void SetFoliageTowns(PlayerFoliageTowns towns) => _foliageTowns = towns;

    /// <summary>
    /// Whether no placement is in the overlay.
    /// </summary>
    public bool IsEmpty => _byZone.Count == 0;

    /// <summary>
    /// Raised after a placement is added, replaced or removed, with the zone it was placed in (both zones when a replacement moves it): what else
    /// keeps zone-wide results (the ragdolls' collision) drops them.
    /// </summary>
    public event Action<ZoneCoordinate>? Changed;

    /// <summary>
    /// Adds or replaces a placement (by global placement key).
    /// </summary>
    public void Add(BuildingPlacement placement)
    {
        BuildingPlacement? old;
        lock (_gate)
        {
            _all.TryGetValue(placement.Key, out old);
            _all[placement.Key] = placement;
            _byZone = _all.Values.GroupBy(p => p.Zone).ToDictionary(g => g.Key, g => g.ToArray());
        }

        if (old is not null && old.Zone != placement.Zone) Changed?.Invoke(old.Zone);

        Changed?.Invoke(placement.Zone);
    }

    /// <summary>
    /// Removes a placement; returns it, or null when there was none.
    /// </summary>
    public BuildingPlacement? Remove(string instanceId)
    {
        BuildingPlacement? removed;
        lock (_gate)
        {
            if (!_all.Remove(instanceId, out removed)) return null;

            _byZone = _all.Values.GroupBy(p => p.Zone).ToDictionary(g => g.Key, g => g.ToArray());
        }

        Changed?.Invoke(removed.Zone);
        return removed;
    }

    /// <summary>
    /// Removes everything; returns the removed placements.
    /// </summary>
    public List<BuildingPlacement> Clear()
    {
        List<BuildingPlacement> removed;
        lock (_gate)
        {
            removed = _all.Values.ToList();
            _all.Clear();
            _byZone = [];
        }

        foreach (var zone in removed.Select(r => r.Zone).Distinct())
            Changed?.Invoke(zone);

        return removed;
    }

    /// <summary>
    /// Every placement now.
    /// </summary>
    public List<BuildingPlacement> All()
    {
        lock (_gate)
            return [.. _all.Values];
    }

    /// <summary>
    /// The placements in <paramref name="zone"/> (by where they were placed) in the snapshot of now; empty for none.
    /// </summary>
    public IReadOnlyList<BuildingPlacement> In(ZoneCoordinate zone) => _byZone.TryGetValue(zone, out var list) ? list : [];

    /// <summary>
    /// The zones whose mesh a building at <paramref name="position"/> can change: its own and the neighbours within <see cref="Reach"/> of it.
    /// </summary>
    public static IEnumerable<ZoneCoordinate> ZonesReached(Vector3 position)
    {
        var home = WorldLayout.ZoneOf(position.X, position.Z);
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                var zone = new ZoneCoordinate(home.X + dx, home.Y + dz);
                if (!zone.IsInsideGrid) continue;

                var (ox, oz) = WorldLayout.ZoneOrigin(zone);
                bool outside = position.X < ox - Reach || position.X > ox + WorldLayout.ZoneSize + Reach
                    || position.Z < oz - Reach || position.Z > oz + WorldLayout.ZoneSize + Reach;
                if (outside) continue;

                yield return zone;
            }
        }
    }
}
