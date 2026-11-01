using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation;

public sealed partial class PopulationSystem
{
    /// <summary>Queues nearby population and applies completed builds during loading, without moving squads or advancing their timers.</summary>
    internal bool PrepareLoading(World world, Vector3 cameraFocus)
    {
        focus = cameraFocus;
        world.Uniques.SeedPrisoners(data.Db);
        RefreshStates(world);
        Inputs(world, []);
        // Towns loaded behind the loading screen keep their TOWN, as the original checks no override then (its ZoneManager +8 byte).
        loadingScreen = true;
        try { RefreshZones(world); }
        finally { loadingScreen = false; }
        // A copy, as the tick's own loop: activating can change the registry (a town's squad that lost too many is made afresh as a new platoon).
        foreach (var platoon in world.Platoons.All.ToList())
            if (platoon.State == PlatoonState.Unloaded && !platoon.Unmade && Wanted(platoon) && Ready(platoon))
                Activate(world, platoon);

        foreach (var site in data.Sites)
            if (active.Contains(site.Zone) && states[site.Index].Status != SiteStatus.Loaded) return false;
        foreach (var zone in active)
            if (wildByZone.TryGetValue(zone, out var sites))
                foreach (int site in sites)
                    if (wildStates[site].Status != SiteStatus.Loaded) return false;
        if (world.Platoons.All.Any(p => p.State == PlatoonState.Activating && Wanted(p))) return false;
        // A save's never-made squads that come in at load are made last, once everything else is, so that they take no squad id or character slot that
        // another squad would have had (made at once from the template, they leave nothing activating).
        foreach (var platoon in world.Platoons.All.ToList())
            if (platoon.State == PlatoonState.Unloaded && platoon.Unmade && Wanted(platoon) && Ready(platoon) && ActivatesAtLoad(platoon, boxZones))
                Activate(world, platoon);
        return !world.Platoons.All.Any(p => p.State == PlatoonState.Activating && Wanted(p));
    }

    /// <summary>
    /// Whether a wanted platoon comes in behind the loading screen. Every one does except a save's never-made town squad (<see cref="Platoon.Unmade"/>) outside the
    /// zones of the player's box: that one waits its 5 s dwell (<see cref="Platoon.DwellLeft"/>), as the original's (<b>Observed</b> on one save, a new game saved at once:
    /// such squads in the player's zone at once, in the camera block's other zones 4 to 6 simulated s after load). <see cref="Platoon.Position"/> is X and Z.
    /// </summary>
    internal static bool ActivatesAtLoad(Platoon p, IReadOnlySet<ZoneCoordinate> playerBox) =>
        !p.Unmade || playerBox.Contains(WorldLayout.ZoneOf(p.Position.X, p.Position.Y));
}
