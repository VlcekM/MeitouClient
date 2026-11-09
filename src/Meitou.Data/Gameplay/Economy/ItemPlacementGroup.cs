namespace Meitou.Data.Gameplay.Economy;

/// <summary>
/// An ITEM_PLACEMENT_GROUP (docs/game/factions-squads-towns.md "Loose items in buildings"): what a loose-item slot of an interior layout holds and
/// how soon it refills. Its reference lists (<c>items</c>, <c>weapons</c>, <c>clothing</c>, <c>weapon manufacturers</c>, ...) are a vendor list's and
/// are read as one (<see cref="VendorList.From"/>).
/// </summary>
public sealed record ItemPlacementGroup
{
    /// <summary>
    /// The ITEM_PLACEMENT_GROUP's string id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The weighted items and the weapon manufacturers.
    /// </summary>
    public required VendorList Items { get; init; }

    /// <summary>
    /// <c>random yaw</c>: the item is turned at random about Y rather than by the slot.
    /// </summary>
    public bool RandomYaw { get; init; }

    /// <summary>
    /// <c>min respawn time</c> / <c>max respawn time</c>: game hours from an item's going to the next one (an int in [min, max]).
    /// </summary>
    public int MinRespawnHours { get; init; }

    /// <summary>
    /// <c>max respawn time</c> (game hours, default 0).
    /// </summary>
    public int MaxRespawnHours { get; init; }

    /// <summary>
    /// Reads an ITEM_PLACEMENT_GROUP record, its reference lists as a vendor list's.
    /// </summary>
    public static ItemPlacementGroup From(GameDatabase db, GameRecord r) => new()
    {
        Id = r.StringId,
        Items = VendorList.From(db, r),
        RandomYaw = r.GetBool("random yaw"),
        MinRespawnHours = RecordReading.Int(r, "min respawn time", 0),
        MaxRespawnHours = RecordReading.Int(r, "max respawn time", 0),
    };
}
