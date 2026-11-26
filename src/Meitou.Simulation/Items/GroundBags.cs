namespace Meitou.Simulation.Items;

/// <summary>
/// A bag lying on the ground opened as a container (docs/game/ui-input.md "Ground item labels"): the original's menu gives an item with an inventory
/// Pick Up / Steal and Loot (26, <c>FUN_1407a6bc0</c>), and Loot opens its contents in the container window. Its site key names the ground item
/// (<see cref="Key"/>); <see cref="Find"/> resolves a key, or a container fixture's id, to the container the open path works on. The bag's fixture
/// is made for each ask and never registered, so no scan, save or hash sees it; its contents are the bag's own inventory.
/// </summary>
public static class GroundBags
{
    /// <summary>
    /// The prefix of a ground bag's site key; no fixture id has it.
    /// </summary>
    const string Prefix = "ground:";

    /// <summary>
    /// The site key of ground item <paramref name="id"/> (ground item ids are never reused).
    /// </summary>
    public static string Key(int id) => Prefix + id;

    /// <summary>
    /// Whether <paramref name="site"/> is a ground bag's key.
    /// </summary>
    public static bool IsKey([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? site) =>
        site is not null && site.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// The container <paramref name="site"/> names: a fixture with contents, or the ground bag of its key while it lies there; else null.
    /// </summary>
    internal static Locks.Fixture? Find(World world, string? site)
    {
        if (site is null) return null;
        if (!IsKey(site)) return world.Fixtures.Find(site) is { Contents: not null } fixture ? fixture : null;

        return Lying(world, site) is { } bag ? AsFixture(site, bag) : null;
    }

    /// <summary>
    /// The ground item with an inventory <paramref name="site"/> names, while it lies there; null for anything else.
    /// </summary>
    internal static GroundItem? Lying(World world, string? site)
    {
        if (!IsKey(site) || !int.TryParse(site.AsSpan(Prefix.Length), out int id)) return null;

        return world.GroundItems.Find(id) is { Item.Contents: not null } bag ? bag : null;
    }

    /// <summary>
    /// The bag as a container: its own id as its building, so it stands in none and a take from it has no owner character (the original's
    /// <c>FUN_14070cef0</c> finds one only for interior furniture), its owner the item's <see cref="GroundItem.OwnedBy"/>.
    /// </summary>
    static Locks.Fixture AsFixture(string site, GroundItem bag) => new()
    {
        Id = site,
        Building = site,
        Kind = Locks.FixtureKind.Container,
        RecordId = bag.Item.Record,
        Owner = bag.OwnedBy,
        Position = bag.Position,
        Contents = bag.Item.Contents,
    };
}
