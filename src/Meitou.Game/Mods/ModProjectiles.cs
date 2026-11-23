using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// What happened to a bolt (<see cref="ProjectileEvent"/>).
/// </summary>
public enum ProjectileChange
{
    /// <summary>
    /// It was shot: first seen in flight (or already stuck, then <see cref="Landed"/> follows in the same list).
    /// </summary>
    Fired,
    /// <summary>
    /// It stuck in the ground or a building.
    /// </summary>
    Landed,
    /// <summary>
    /// It left the world: it struck a body (the hit feed has the hit), its flight ran out or a stuck bolt was cleared.
    /// </summary>
    Gone,
}

/// <summary>
/// A bolt's change seen by the host between two frames (docs/modding.md "Projectile feed"): its id (<see cref="EffectAnchor.Projectile"/>),
/// the change, who shot it, where it is and the way it flies (unit), its speed (units a second, 0 once stuck) and its <c>live ammo</c> mesh. For
/// <see cref="ProjectileChange.Gone"/> the place, direction and speed are where it was last seen.
/// </summary>
public readonly record struct ProjectileEvent(int Id, ProjectileChange Change, CharacterId Shooter, Vector3 Position, Vector3 Direction, float Speed,
    string Mesh);

/// <summary>
/// The bolts' changes between the snapshots the host shows, for the mods' projectile feeds: display only, read from the published snapshot, so the
/// simulation never sees it.
/// </summary>
internal sealed class ProjectileWatch
{
    /// <summary>
    /// The bolts seen at the last look, by id.
    /// </summary>
    readonly Dictionary<int, ProjectileSnapshot> _seen = [];
    /// <summary>
    /// The ids of this look's bolts, kept to avoid allocating.
    /// </summary>
    readonly HashSet<int> _now = [];

    /// <summary>
    /// Adds to <paramref name="into"/> (not cleared) the changes from the last look to <paramref name="now"/>, in the snapshot's order, then the
    /// bolts gone in id order.
    /// </summary>
    public void Look(IReadOnlyList<ProjectileSnapshot> now, List<ProjectileEvent> into)
    {
        _now.Clear();
        foreach (var p in now)
        {
            _now.Add(p.Id);
            Compare(p, into);
            _seen[p.Id] = p;
        }

        if (_seen.Count == _now.Count) return;

        foreach (int id in _seen.Keys.Where(id => !_now.Contains(id)).Order().ToArray())
        {
            into.Add(Event(_seen[id], ProjectileChange.Gone));
            _seen.Remove(id);
        }
    }

    /// <summary>
    /// The changes of one bolt against what was seen of it.
    /// </summary>
    void Compare(in ProjectileSnapshot p, List<ProjectileEvent> into)
    {
        bool known = _seen.TryGetValue(p.Id, out var before);
        if (!known) into.Add(Event(p, ProjectileChange.Fired));
        if (p.Stuck && (!known || !before.Stuck)) into.Add(Event(p, ProjectileChange.Landed));
    }

    /// <summary>
    /// The event of <paramref name="p"/>.
    /// </summary>
    static ProjectileEvent Event(in ProjectileSnapshot p, ProjectileChange change) =>
        new(p.Id, change, p.Shooter, p.Position, p.Direction, p.Speed, p.Mesh);

    /// <summary>
    /// Forgets every bolt without telling (a new world).
    /// </summary>
    public void Reset(IReadOnlyList<ProjectileSnapshot> now)
    {
        _seen.Clear();
        foreach (var p in now) _seen[p.Id] = p;
    }
}

/// <summary>
/// A mod's host-side view of the bolts' changes (<see cref="ModContext.AddProjectileFeed"/>); faulted once it threw.
/// </summary>
internal sealed class ModProjectileFeed(string owner, Action<IReadOnlyList<ProjectileEvent>> run)
{
    /// <summary>
    /// The id of the mod that registered the feed.
    /// </summary>
    public string Owner => owner;

    /// <summary>
    /// Hands the feed the frame's changes.
    /// </summary>
    public Action<IReadOnlyList<ProjectileEvent>> Run => run;

    /// <summary>
    /// Set once the feed threw; a faulted feed is not called again.
    /// </summary>
    public bool Faulted { get; set; }
}
