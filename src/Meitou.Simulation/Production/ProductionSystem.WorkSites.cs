namespace Meitou.Simulation.Production;

/// <summary>
/// The work sites of the player's jobs, for the snapshot (<see cref="WorldSnapshot.WorkSites"/>).
/// </summary>
public sealed partial class ProductionSystem
{
    /// <summary>
    /// The work site lists last handed out (<see cref="WorkSitesView"/>).
    /// </summary>
    readonly WorkSiteViews _workSites = new();

    /// <summary>
    /// Every site a job of a living player character names (its perma-job list), once each in the order first named: player buildings, natural
    /// resource nodes, town machines, dummies and seats; a site that no longer resolves is left out. The same list while unchanged. Serial (the
    /// publish).
    /// </summary>
    public IReadOnlyList<WorkSiteSnapshot> WorkSitesView(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        return _workSites.Of(JobSiteIds(world), id => WorkSiteOf(world, id));
    }

    /// <summary>
    /// The site ids the living player characters' jobs name, once each, in slot and job order.
    /// </summary>
    static List<string> JobSiteIds(World world)
    {
        var table = world.Characters;
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int slot = 0; slot < table.HighWater; slot++)
        {
            if (!table.Previous[slot].Alive || table.Cold(slot) is not { IsPlayer: true, Jobs.Count: > 0 } cold) continue;

            foreach (var job in cold.Jobs)
                if (job.Site is { } id && seen.Add(id)) ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// The snapshot of the work site <paramref name="id"/>; null when it does not resolve.
    /// </summary>
    WorkSiteSnapshot? WorkSiteOf(World world, string id)
    {
        if (Site(world, id) is not { } site) return null;

        string name = site.Name.Length > 0 ? site.Name : _db.Find(site.Record)?.Name ?? site.Record;
        return new WorkSiteSnapshot(id, site.Record, name, WorkSiteKindOf(world, id), site.Owner, site.Position);
    }

    /// <summary>
    /// What kind of site <paramref name="id"/> is.
    /// </summary>
    static WorkSiteKind WorkSiteKindOf(World world, string id)
    {
        if (world.Buildings.Find(id) is not null) return WorkSiteKind.Building;
        if (IsNatural(id)) return WorkSiteKind.ResourceNode;
        if (IsTownWork(id)) return WorkSiteKind.TownMachine;
        return Locks.FixtureRegistry.IsDummy(id) ? WorkSiteKind.TrainingDummy : WorkSiteKind.Seat;
    }
}
