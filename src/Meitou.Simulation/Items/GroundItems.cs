using System.Numerics;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Items;

/// <summary>
/// Where and when a severed limb falls from (docs/game/character-stats.md, docs/simulation.md "Ground items"): the character it was cut from, the bone of its stump
/// (RACE-part <c>bone name 2</c>), the tick of the drop and the cut character's skin tone (the tint the original keeps on the item, <c>FUN_1400ced10</c>: 1 - the appearance's <c>Skin Tone</c>; not saved, so a limb loaded from a save has none). The simulation has no skeleton; the host draws the fall from that bone's place (display only, not hashed or saved).
/// </summary>
public readonly record struct FallSource(CharacterId Character, string Bone, long Tick, Vector3 SkinTone = default);

/// <summary>
/// An item lying in the world, as the host draws it (docs/simulation.md "Ground items"): a saved one by its whole <see cref="Rotation"/>, a <see cref="Dropped"/> one turned by it
/// about Y and laid flat on the ground by the host. <see cref="Type"/>, <see cref="Level"/> and <see cref="Quality"/> are the item's, for its label's grade colour, <see cref="OwnedBy"/> <see cref="GroundItem.OwnedBy"/>.
/// <see cref="Fall"/> is set for a severed limb, which the host drops from its stump.
/// </summary>
public readonly record struct GroundItemSnapshot(int Id, string Record, string Name, Vector3 Position, Quaternion Rotation, bool Dropped,
    Data.Fcs.FcsRecordType Type = default, int Level = 0, float Quality = 0, int OwnedBy = -1, FallSource? Fall = null);

/// <summary>One loose item: where it lies and how it is turned (a saved item's whole rotation, written back as read; a dropped one turned about Y).</summary>
public sealed record GroundItem(int Id, ItemInstance Item, Vector3 Position, Quaternion Rotation)
{
    /// <summary>Dropped in play (turned about Y only), not put back from the save.</summary>
    public bool Dropped { get; init; }
    /// <summary>The loose-item slot it fills (<see cref="ItemSlots"/>; null: none). Such items are not counted against <see cref="GroundItems.Limit"/>.</summary>
    public string? Slot { get; init; }
    /// <summary>
    /// The faction of its saved <c>ownedby</c> owner (-1: none, or a <c>not real</c> faction; <see cref="Saving.GroundItemSave"/>): taking it is stealing for another faction
    /// (label red, pick-up "Steal", the item marked stolen).
    /// </summary>
    public int OwnedBy { get; init; } = -1;
    /// <summary>The stump a severed limb fell from (null: none, e.g. any other item).</summary>
    public FallSource? Fall { get; init; }
    /// <summary>
    /// The camp that laid it (<see cref="NestBuildings"/>' key: a nest's debris or loot; null: none). Not counted against <see cref="GroundItems.Limit"/>; saved as the
    /// original's <c>persist</c> handle naming the nest or town.
    /// </summary>
    public int? Camp { get; init; }
}

/// <summary>
/// Loose items lying in the world (docs/simulation.md "Ground items"): the save's (its zones' <c>1-itemlist</c>, docs/formats/save.md "Zone files") and the severed limbs combat drops.
/// Oldest first; beyond <see cref="Limit"/> items dropped in play the oldest of those goes (not a slot's or a camp's). Hashed by <see cref="World.StateHash"/> only while it holds one.
/// </summary>
public sealed class GroundItems
{
    /// <summary>The default and largest <see cref="Limit"/>.</summary>
    public const int DefaultLimit = 1024, MaxLimit = 4096;

    int _limit = DefaultLimit;

    /// <summary>
    /// How many items dropped in play (not read from the save) lie in the world at most (an engine choice; the original's limbs carry a <c>timeout</c>, meaning
    /// <b>Unknown</b>): 1 to <see cref="MaxLimit"/>, the host's "Dropped items kept" setting. Lowered, the oldest beyond it go at the next drop.
    /// </summary>
    public int Limit { get => _limit; set => _limit = Math.Clamp(value, 1, MaxLimit); }

    readonly List<GroundItem> _items = [];
    int _nextId = 1;

    public int Count => _items.Count;
    public IReadOnlyList<GroundItem> All => _items;
    /// <summary>Bumped when an item is dropped, loaded or goes: the host diffs <see cref="Published"/> by id when it changes.</summary>
    public long Revision { get; private set; }

    /// <summary>Drops an item made in play, turned <paramref name="yaw"/> about Y; <paramref name="fall"/> is the stump a severed limb falls from.</summary>
    public void Drop(ItemInstance item, Vector3 position, float yaw, FallSource? fall = null)
    {
        _items.Add(new GroundItem(_nextId++, item, position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)) { Dropped = true, Fall = fall });
        for (int extra = _items.Count(Capped) - _limit; extra > 0; extra--) _items.RemoveAt(_items.FindIndex(Capped));
        Revision++;
    }

    static bool Capped(GroundItem i) => i.Item.Saved is null && i.Slot is null && i.Camp is null;

    /// <summary>
    /// Lays a slot's item (<see cref="ItemSlots"/>) on the ground, turned <paramref name="yaw"/> about Y and laid flat as a dropped one (no limit while it keeps
    /// <paramref name="slot"/>; null: a loose item at once), or a camp's (<paramref name="camp"/>, <see cref="GroundItem.Camp"/>); returns its id.
    /// </summary>
    public int Place(ItemInstance item, Vector3 position, float yaw, string? slot, int owner, int? camp = null)
    {
        _items.Add(new GroundItem(_nextId, item, position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)) { Dropped = true, Slot = slot, OwnedBy = owner, Camp = camp });
        Revision++;
        return _nextId++;
    }

    /// <summary>Binds a lying item to a slot (a loaded save's item found in its slot): it takes the slot and owner, and a yaw-only one is laid flat again.</summary>
    public void Claim(int id, string slot, int owner)
    {
        int k = _items.FindIndex(i => i.Id == id);
        if (k < 0) return;
        var g = _items[k];
        bool yawOnly = MathF.Abs(g.Rotation.X) < 1e-4f && MathF.Abs(g.Rotation.Z) < 1e-4f;
        _items[k] = g with { Slot = slot, OwnedBy = owner, Dropped = g.Dropped || yawOnly };
        Revision++;
    }

    /// <summary>Puts a saved item back where the save had it (no limit), owned by <paramref name="ownedBy"/> (<see cref="GroundItem.OwnedBy"/>).</summary>
    public void Load(ItemInstance item, Vector3 position, Quaternion rotation, int ownedBy = -1)
    {
        _items.Add(new GroundItem(_nextId++, item, position, rotation) { OwnedBy = ownedBy });
        Revision++;
    }

    public GroundItem? Find(int id) => _items.Find(i => i.Id == id);

    /// <summary>Takes an item off the ground (null when it is gone).</summary>
    public GroundItem? Take(int id)
    {
        int k = _items.FindIndex(i => i.Id == id);
        if (k < 0) return null;
        var taken = _items[k];
        _items.RemoveAt(k);
        Revision++;
        return taken;
    }

    /// <summary>The items as published, made again only when <see cref="Revision"/> changed.</summary>
    internal IReadOnlyList<GroundItemSnapshot> Published()
    {
        if (_publishedAt != Revision) (_published, _publishedAt) = ([.. _items.Select(i => new GroundItemSnapshot(i.Id, i.Item.Record, i.Item.Name, i.Position, i.Rotation, i.Dropped, i.Item.Type, i.Item.Level, i.Item.Quality, i.OwnedBy, i.Fall))], Revision);
        return _published;
    }
    IReadOnlyList<GroundItemSnapshot> _published = [];
    long _publishedAt;

    public void Hash(ref StateHasher h)
    {
        h.Add(_items.Count);
        foreach (var i in _items)
        {
            h.Add(i.Id);
            h.Add(Rng.StableHash(i.Item.Record));
            h.Add(i.Item.Quantity);
            h.Add(i.Position);
            h.Add(i.Rotation.X);
            h.Add(i.Rotation.Y);
            h.Add(i.Rotation.Z);
            h.Add(i.Rotation.W);
            if (i.Slot is not null) { h.Add(Rng.StableHash(i.Slot)); h.Add(i.OwnedBy); }   // only for a slot's, so other items hash as before
            if (i.Camp is { } camp) h.Add(camp);
        }
    }
}