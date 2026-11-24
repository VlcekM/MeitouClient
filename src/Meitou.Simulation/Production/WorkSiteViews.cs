using System.Numerics;

namespace Meitou.Simulation.Production;

/// <summary>
/// What a work site is (<see cref="WorkSiteSnapshot.Kind"/>); room for more kinds.
/// </summary>
public enum WorkSiteKind : byte
{
    /// <summary>
    /// A player-built building or machine (also in <see cref="WorldSnapshot.Buildings"/>).
    /// </summary>
    Building,
    /// <summary>
    /// A natural resource node: a rock of the terrain mined in place.
    /// </summary>
    ResourceNode,
    /// <summary>
    /// A town building's machine the player's characters work (a town work site).
    /// </summary>
    TownMachine,
    /// <summary>
    /// A town's training dummy.
    /// </summary>
    TrainingDummy,
    /// <summary>
    /// A town's seat or workstation.
    /// </summary>
    Seat,
}

/// <summary>
/// A site a job of the player's characters names (<see cref="WorldSnapshot.WorkSites"/>): its id as the jobs and commands name it, its BUILDING
/// record, its name, its kind, its owning faction (-1 none) and its position.
/// </summary>
public sealed record WorkSiteSnapshot(string Id, string Record, string Name, WorkSiteKind Kind, int Owner, Vector3 Position);

/// <summary>
/// The work sites of the player's jobs (<see cref="ProductionSystem.WorkSitesView"/>): the last list is handed out again while the sites are the
/// same, so a reader keyed on its reference does not lay it out again every frame.
/// </summary>
sealed class WorkSiteViews
{
    /// <summary>
    /// The list last handed out; never changed once published.
    /// </summary>
    WorkSiteSnapshot[] _published = [];

    /// <summary>
    /// The snapshot of each of <paramref name="ids"/> <paramref name="resolve"/> finds, in order: the last list while every site is the same.
    /// </summary>
    public IReadOnlyList<WorkSiteSnapshot> Of(List<string> ids, Func<string, WorkSiteSnapshot?> resolve)
    {
        var fresh = new List<WorkSiteSnapshot>(ids.Count);
        foreach (var id in ids)
            if (resolve(id) is { } site) fresh.Add(site);

        if (fresh.SequenceEqual(_published)) return _published;

        _published = [.. fresh];
        return _published;
    }

    /// <summary>
    /// The site ids the living player characters' jobs name, once each, in slot and job order.
    /// </summary>
    public static List<string> JobSiteIds(World world)
    {
        var table = world.Characters;
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int slot = 0; slot < table.HighWater; slot++)
        {
            if (!table.Previous[slot].Alive || table.Cold(slot) is not { IsPlayer: true, Jobs.Count: > 0 } cold)
                continue;

            foreach (var job in cold.Jobs)
                if (job.Site is { } id && seen.Add(id)) ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// What kind of site <paramref name="id"/> is.
    /// </summary>
    public static WorkSiteKind KindOf(World world, string id)
    {
        if (world.Buildings.Find(id) is not null) return WorkSiteKind.Building;
        if (ProductionSystem.IsNatural(id)) return WorkSiteKind.ResourceNode;
        if (ProductionSystem.IsTownWork(id)) return WorkSiteKind.TownMachine;
        return Locks.FixtureRegistry.IsDummy(id) ? WorkSiteKind.TrainingDummy : WorkSiteKind.Seat;
    }
}
