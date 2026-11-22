using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Production;

/// <summary>
/// The town panel's outposts (<see cref="ProductionSystem.OutpostView"/>): the last published list is handed out again while every entry is the
/// same, so the host's foliage towns, keyed on the list's reference, are not laid out again every frame.
/// </summary>
sealed class OutpostViews
{
    /// <summary>
    /// The list last handed out; never changed once published (the interface reads it on its own thread). One reference, so a second caller (the
    /// agent channel's town report) only ever sees a whole list.
    /// </summary>
    OutpostSnapshot[] _published = [];

    /// <summary>
    /// The outposts of <paramref name="towns"/> with their grids' totals from <paramref name="power"/>: the last list while nothing in it changed.
    /// </summary>
    public IReadOnlyList<OutpostSnapshot> Of(IReadOnlyList<PlayerTown> towns, Dictionary<int, TownPower> power)
    {
        if (towns.Count == 0) return [];

        var published = _published;
        if (Same(published, towns, power)) return published;

        var fresh = new OutpostSnapshot[towns.Count];
        for (int k = 0; k < fresh.Length; k++)
            fresh[k] = SnapshotOf(towns[k], power);

        _published = fresh;
        return fresh;
    }

    /// <summary>
    /// The snapshot of town <paramref name="t"/>.
    /// </summary>
    static OutpostSnapshot SnapshotOf(PlayerTown t, Dictionary<int, TownPower> power) =>
        new(t.Number, t.Name, t.Position, t.Radius, t.Owner, t.Public, power.GetValueOrDefault(t.Number));

    /// <summary>
    /// Whether <paramref name="published"/> holds exactly the snapshots <paramref name="towns"/> would give now.
    /// </summary>
    static bool Same(OutpostSnapshot[] published, IReadOnlyList<PlayerTown> towns, Dictionary<int, TownPower> power)
    {
        if (published.Length != towns.Count) return false;

        for (int k = 0; k < published.Length; k++)
            if (!Matches(published[k], towns[k], power)) return false;

        return true;
    }

    /// <summary>
    /// Whether snapshot <paramref name="o"/> is the one town <paramref name="t"/> would give now.
    /// </summary>
    static bool Matches(OutpostSnapshot o, PlayerTown t, Dictionary<int, TownPower> power) =>
        o.Number == t.Number && o.Name == t.Name && o.Position == t.Position && o.Radius == t.Radius && o.Owner == t.Owner && o.Public == t.Public
        && o.Power == power.GetValueOrDefault(t.Number);
}
