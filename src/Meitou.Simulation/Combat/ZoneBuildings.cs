using System.Numerics;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The doors and walls a blow's attack zone reaches (<see cref="CombatSystem"/>'s zone buildings step), nearest first. Engine readings: a wall
/// counts when its footprint hull is within the box's half width of its axis's three points (near end, centre, far end), a door when its leaf's
/// centre is in the box widened by the leaf's half width.
/// </summary>
sealed class ZoneBuildings
{
    /// <summary>
    /// The doors and walls found, keyed by squared distance (reused: the effects apply serially).
    /// </summary>
    readonly List<(float Key, string? Door, Construction.BuildSite? Wall)> _found = [];

    /// <summary>
    /// The doors (none without <paramref name="locks"/>) and walls (none without <paramref name="construction"/>) <paramref name="zone"/> reaches,
    /// nearest first; the list is reused by the next call.
    /// </summary>
    public List<(float Key, string? Door, Construction.BuildSite? Wall)> Collect(World world, CombatSystem.Zone zone, Locks.LockSystem? locks,
        Construction.ConstructionSystem? construction)
    {
        var found = _found;
        found.Clear();
        CollectDoors(world, zone, locks, found);
        CollectWalls(world, zone, construction, found);
        if (found.Count == 0) return found;

        found.Sort(static (x, y) => x.Key.CompareTo(y.Key));
        return found;
    }

    /// <summary>
    /// Adds the doors of the level's fixtures that the zone reaches and that are not broken.
    /// </summary>
    static void CollectDoors(World world, CombatSystem.Zone zone, Locks.LockSystem? locks,
        List<(float Key, string? Door, Construction.BuildSite? Wall)> found)
    {
        if (locks is null) return;

        foreach (var pick in world.Fixtures.DoorPicks)
        {
            var d = new Vector2(pick.Position.X, pick.Position.Z) - zone.Centre;
            float reach = zone.Deep + zone.Half + 30;
            if (d.LengthSquared() > reach * reach || MathF.Abs(pick.Position.Y - zone.Y) > zone.High + 20) continue;
            if (world.Fixtures.Find(pick.Id) is not { DoorBroken: false } door || world.Fixtures.DoorSpotOf(door) is not { } spot) continue;

            float along = MathF.Abs(Vector2.Dot(d, zone.Forward));
            float across = MathF.Abs(d.X * zone.Forward.Y - d.Y * zone.Forward.X);
            if (MathF.Max(0, across - zone.Half) > spot.HalfWidth || MathF.Max(0, along - zone.Deep) > spot.HalfWidth) continue;

            found.Add((d.LengthSquared(), pick.Id, null));
        }
    }

    /// <summary>
    /// Adds the walls of the construction system that the zone reaches.
    /// </summary>
    static void CollectWalls(World world, CombatSystem.Zone zone, Construction.ConstructionSystem? construction,
        List<(float Key, string? Door, Construction.BuildSite? Wall)> found)
    {
        if (construction is null) return;

        foreach (var site in world.Buildings.All)
        {
            if (site.Destroyed || site.Progress <= 0 || construction.Data(site.Record) is not { LinksTogether: true }) continue;

            float best = float.MaxValue;
            for (int n = -1; n <= 1; n++)
            {
                var at = zone.Centre + zone.Forward * (n * zone.Deep);
                float gap = construction.Settings.EdgeGap?.Invoke(site, at) ?? Vector2.Distance(at, new Vector2(site.Position.X, site.Position.Z));
                best = MathF.Min(best, gap);
            }

            if (best > zone.Half) continue;

            found.Add((Vector2.DistanceSquared(new Vector2(site.Position.X, site.Position.Z), zone.From), null, site));
        }
    }
}
