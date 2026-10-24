using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>
/// The buildings the player placed (docs/simulation.md "Buildings", host seam): one thread-safe overlay on the level data's placements, shared by every
/// <see cref="ZoneGeometryGatherer"/>. <see cref="ZoneGeometryGatherer.NearBuildings"/> and so <see cref="ZoneGeometryGatherer.BuildingHash"/> see it, so a zone's cache key
/// changes with it; an empty overlay changes nothing. Readers get a snapshot, writers replace it whole.
/// </summary>
public sealed class NavRuntimeBuildings
{
    /// <summary>How far (units) a building reaches past its zone box in the gatherer (<c>BuildingReach</c>) plus the gather margin: the zones a placement can shape.</summary>
    public const float Reach = 600 + 72;

    readonly object gate = new();
    readonly Dictionary<string, BuildingPlacement> all = new(StringComparer.Ordinal);
    volatile Dictionary<ZoneCoordinate, BuildingPlacement[]> byZone = [];

    public bool IsEmpty => byZone.Count == 0;

    /// <summary>Adds or replaces a placement (by global placement key).</summary>
    public void Add(BuildingPlacement placement)
    {
        lock (gate)
        {
            all[placement.Key] = placement;
            byZone = all.Values.GroupBy(p => p.Zone).ToDictionary(g => g.Key, g => g.ToArray());
        }
    }

    /// <summary>Removes a placement; returns it, or null when there was none.</summary>
    public BuildingPlacement? Remove(string instanceId)
    {
        lock (gate)
        {
            if (!all.Remove(instanceId, out var removed)) return null;
            byZone = all.Values.GroupBy(p => p.Zone).ToDictionary(g => g.Key, g => g.ToArray());
            return removed;
        }
    }

    /// <summary>Removes everything; returns the removed placements.</summary>
    public List<BuildingPlacement> Clear()
    {
        lock (gate)
        {
            var removed = all.Values.ToList();
            all.Clear();
            byZone = [];
            return removed;
        }
    }

    /// <summary>Every placement now.</summary>
    public List<BuildingPlacement> All() { lock (gate) return [.. all.Values]; }

    public IReadOnlyList<BuildingPlacement> In(ZoneCoordinate zone) => byZone.TryGetValue(zone, out var list) ? list : [];

    /// <summary>The zones whose mesh a building at <paramref name="position"/> can change: its own and the neighbours within <see cref="Reach"/> of it.</summary>
    public static IEnumerable<ZoneCoordinate> ZonesReached(Vector3 position)
    {
        var home = WorldLayout.ZoneOf(position.X, position.Z);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var zone = new ZoneCoordinate(home.X + dx, home.Y + dz);
                if (!zone.IsInsideGrid) continue;
                var (ox, oz) = WorldLayout.ZoneOrigin(zone);
                if (position.X < ox - Reach || position.X > ox + WorldLayout.ZoneSize + Reach || position.Z < oz - Reach || position.Z > oz + WorldLayout.ZoneSize + Reach) continue;
                yield return zone;
            }
    }
}
