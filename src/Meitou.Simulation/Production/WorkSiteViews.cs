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
}
