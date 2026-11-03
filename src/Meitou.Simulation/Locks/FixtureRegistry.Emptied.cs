using Meitou.Data.World;

namespace Meitou.Simulation.Locks;

/// <summary>
/// Bought town houses are empty (docs/simulation.md "Town house purchases"): the original's purchase destroys the furniture and the item groups of the house's interior
/// (<c>FUN_1407ad6c0</c> runs <c>FUN_14054f4d0</c>). Meitou keys it on the property being bought, so a loaded save empties the house again with no state of its own.
/// </summary>
public sealed partial class FixtureRegistry
{
    long emptiedRevision;
    (long Revision, TownBuilding[] List) publishedEmptied = (-1, []);

    /// <summary>Whether a town building is a house the player bought: its interior layout's furniture and loose-item slots are gone.</summary>
    public bool IsEmptied(string building) => properties.TryGetValue(building, out var p) && p.Purchased;

    /// <summary>Counts changes of <see cref="PublishedEmptied"/>.</summary>
    public long EmptiedRevision => emptiedRevision;

    /// <summary>
    /// The bought houses without a construction site, by key (a site's own placement says it, <see cref="Construction.BuildSite.LevelPlacement"/>): the host draws, paths and lights
    /// them without their interior layout (<see cref="BuildingPlacement.WithoutInterior"/>). Made again only when they changed (the snapshot's).
    /// </summary>
    public IReadOnlyList<TownBuilding> PublishedEmptied()
    {
        if (publishedEmptied.Revision == emptiedRevision) return publishedEmptied.List;
        var list = properties.Values.Where(p => p.Purchased && p.Construction is null).Select(p => p.Building).OrderBy(b => b.Key, StringComparer.Ordinal);
        publishedEmptied = (emptiedRevision, [.. list]);
        return publishedEmptied.List;
    }

    /// <summary>A registered town's bought houses lose their furniture as at the purchase; what a container held in a save from before that is dropped where it stood.</summary>
    void EmptyPurchased(World world, int site)
    {
        foreach (var p in properties.Values)
            if (p.Site == site && p.Purchased) EmptyInterior(world, p.Building.Key, spill: true);
    }

    /// <summary>
    /// Takes out the fixtures of a house's interior layout (cages, containers, beds, training dummies) and its seats; its doors and its own lock stay. Whoever lies or is held
    /// in one is let out first (the original makes an occupant leave each piece before destroying it). A container's contents go with it (<paramref name="spill"/>: dropped
    /// at its place instead).
    /// </summary>
    void EmptyInterior(World world, string building, bool spill)
    {
        emptiedRevision++;
        RemoveSeats(building);
        if (!byBuilding.TryGetValue(building, out var list)) return;
        foreach (var f in list.Where(f => f.Kind != FixtureKind.Door && f.Id != building).ToArray())
        {
            LetOut(world, f);
            if (spill && f.Contents is { } box) Spill(world, f, box);
            list.Remove(f);
            all.Remove(f);
            byId.Remove(f.Id);
        }
        containerSpots = null;
        bedSpots = null; cageList = null; publishedCages = null; dummySpots = null;
    }

    void LetOut(World world, Fixture f)
    {
        if (f.Occupant.IsNone) return;
        if (f.Kind == FixtureKind.Bed) LeaveBed(world, f.Occupant);
        else Release(world, f.Occupant);
        f.Occupant = CharacterId.None;
    }

    static void Spill(World world, Fixture f, Meitou.Simulation.Items.Inventory box)
    {
        foreach (var item in box.Items.ToArray())
        {
            box.Remove(item);
            world.GroundItems.Drop(item, f.Position, f.Yaw);
        }
    }
}
