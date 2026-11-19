using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Game.Building;
using Meitou.Game.Ui;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Navigation;

/// <summary>
/// The <c>pick</c> mode of <c>meitou-tools probe</c>: the mouse ray from a camera eye to each of the player's sites near it, with what the
/// game's site pick (<c>BuildMode.PickSiteAt</c>) measures along it: the terrain, the site's own collision, and for the building the site stands
/// in whether its interior mask cuts that terrain, where the ray leaves its box and its shown floors' first hit (#1049: a bench in a house dug
/// into a hillside). Needs <c>--nav</c> for the terrain.
/// </summary>
static class PickProbe
{
    /// <summary>
    /// Prints each site within <paramref name="radius"/> of <paramref name="eye"/> as the ray to the middle of its collision meets it.
    /// </summary>
    public static void Run(ProbeWorld probe, GameInstall install, Vector3 eye, float radius)
    {
        var footprints = new BuildingFootprints(install, probe.Db, null);
        var collisions = new InteriorCollisions(install);
        var sites = probe.World.Buildings.All;
        foreach (var site in sites.Where(s => Vector3.Distance(s.Position, eye) < radius))
        {
            if (footprints.Wait(site.Record) is not { } footprint)
            {
                Console.WriteLine($"site {site.Id} '{site.Name}': no collision");
                continue;
            }

            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, PlacementCheck.YawOf(site.Rotation));
            var middle = site.Position + new Vector3(0, (footprint.YMin + footprint.YMax) / 2, 0);
            var direction = Vector3.Normalize(middle - eye);
            var mesh = footprints.Mesh(site.Record, destroyed: false);
            float? hit = BuildMode.RayPrism(eye, direction, footprint.Place(site.Position, yaw), site.Position.Y + footprint.YMin,
                site.Position.Y + footprint.YMax) is null ? null
                : WorldPicking.RayMesh((eye, direction), mesh, site.Position, yaw);
            float? terrain = TerrainT(probe, eye, direction);
            Console.WriteLine($"site {site.Id} '{site.Name}' at {site.Position:F1} parent={site.Parent}: site T={Text(hit)} "
                + $"terrain T={Text(terrain)}");
            if (Parent(sites, site) is not { } house) continue;

            PrintHouse(probe, collisions, house, site, eye, direction, terrain);
            if (footprints.Wait(house.Record) is { } hull)
                Console.WriteLine($"    site's gap to the house's footprint {hull.Gap(house.Position, house.Rotation, new(site.Position.X, site.Position.Z)):F1}");
        }
    }

    /// <summary>
    /// The site <paramref name="site"/> was placed in: the one with its parent's id, or the level building of that instance id.
    /// </summary>
    static BuildSite? Parent(IEnumerable<BuildSite> sites, BuildSite site) =>
        site.Parent is { } parent ? sites.FirstOrDefault(s => s.Id == parent || s.LevelPlacement?.InstanceId == parent) : null;

    /// <summary>
    /// The house's room along the ray: whether its mask cuts the terrain hit, the ray's exit from its box, each shown floor's first hit and the
    /// floor the site stands on.
    /// </summary>
    static void PrintHouse(ProbeWorld probe, InteriorCollisions collisions, BuildSite house, BuildSite site, Vector3 eye, Vector3 direction,
        float? terrain)
    {
        var room = probe.Db.Find(house.Record) is { } record
            ? InteriorVolume.For(probe.Db, record, house.Id, house.Position, house.Rotation, collisions) : null;
        if (room is null)
        {
            Console.WriteLine($"  house {house.Id} '{house.Name}': no rooms");
            return;
        }

        bool cut = terrain is { } t && room.Contains(eye + direction * t, 10) && room.CutsGround(eye, direction, t);
        Console.WriteLine($"  house {house.Id} '{house.Name}' at {house.Position:F1}: mask cuts the terrain hit={cut} "
            + $"box exit T={Text(room.BoxExit(eye, direction))} site floor={room.FloorAt(site.Position)}");
        for (int floor = 0; floor <= 3; floor++)
        {
            string shown = room.MoveHit(eye, direction, floor, 40000) is { } hit ? $"T={hit.Distance:F1} at {hit.Position:F1}" : "none";
            Console.WriteLine($"    floors up to {floor}: first hit {shown}");
        }
    }

    /// <summary>
    /// Where the ray meets the terrain, marched as the game's ground pick; null within 6000.
    /// </summary>
    static float? TerrainT(ProbeWorld probe, Vector3 origin, Vector3 direction)
    {
        float Above(float t)
        {
            var p = origin + direction * t;
            return p.Y - probe.Nav!.Walkability.TerrainHeight(p.X, p.Z);
        }

        float previous = 0;
        for (float t = 2; t <= 6000; t += MathF.Max(2, t * 0.01f))
        {
            if (Above(t) <= 0) return Bisect(Above, previous, t);

            previous = t;
        }

        return null;
    }

    /// <summary>
    /// The crossing of <paramref name="above"/> between two distances.
    /// </summary>
    static float Bisect(Func<float, float> above, float lo, float hi)
    {
        for (int i = 0; i < 24; i++)
        {
            float mid = (lo + hi) / 2;
            if (above(mid) > 0) lo = mid;
            else hi = mid;
        }

        return hi;
    }

    /// <summary>
    /// A distance, or "none".
    /// </summary>
    static string Text(float? t) => t is { } v ? v.ToString("F1") : "none";
}
