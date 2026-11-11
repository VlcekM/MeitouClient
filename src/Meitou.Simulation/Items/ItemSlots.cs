using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Economy;
using Meitou.Data.World;
using Meitou.Simulation.Population;
using Meitou.Simulation.Towns;
using Meitou.Simulation.Trading;

namespace Meitou.Simulation.Items;

/// <summary>
/// The loose-item slots of the towns' interiors (docs/game/factions-squads-towns.md "Loose items in buildings"; docs/simulation.md "Ground items"):
/// each ITEM_PLACEMENT_GROUP object of a building's interior layout keeps one item of its group lying at its place. Registered with the town's
/// fixtures (<see cref="RegisterSite"/>) and kept when the town unloads.
/// <list type="bullet">
/// <item>A slot starts empty and due, so it fills as soon as its zone is active. An empty slot whose time has come and whose zone is active gets one
/// item; when that item leaves the ground (picked up, used up), the next is due an int in [<c>min respawn time</c>, <c>max respawn time</c>] game
/// hours later (the original's timer, <b>Verified</b>).</item>
/// <item>A placed item of a type the slot does not keep (<see cref="Kept"/>: a blueprint) is a loose item at once, and the slot is due again after
/// the same timer.</item>
/// <item>The item is a weighted pick from the group's lists (as <see cref="ShopFiller"/>'s); armour's quality is
/// <see cref="CharacterGenerator.QualityOfGrade"/> of a grade rolled from the
/// owner faction's armour chances (the map is <b>Verified</b>, the grade's source an <b>engine</b> reading); a weapon gets a manufacturer from the
/// group's and a model.</item>
/// <item>It lies at the slot laid flat, turned as the slot or at random (<c>random yaw</c>), owned by the building's faction
/// (<see cref="GroundItem.OwnedBy"/>: taking it is stealing).</item>
/// <item>Engine choices: the respawn times are not saved (a reload makes taken slots due at once); a loaded save's item lying in its slot (within
/// <see cref="ClaimReach"/>, one of
/// the group's records) is bound to it instead of a new one.</item>
/// </list>
/// Serial phases only (the population's slow-world step).
/// </summary>
public sealed class ItemSlots
{
    /// <summary>
    /// How near a loaded item must lie to a slot (XZ; and within 10 in height) to be taken as its item.
    /// </summary>
    public const float ClaimReach = 2;

    /// <summary>
    /// One loose-item slot.
    /// </summary>
    sealed class Slot
    {
        /// <summary>
        /// The slot's id (its building key, then <c>/item</c> and its number).
        /// </summary>
        public required string Id;
        /// <summary>
        /// The ITEM_PLACEMENT_GROUP record it draws from.
        /// </summary>
        public required string GroupId;
        /// <summary>
        /// Where its item lies.
        /// </summary>
        public Vector3 Position;
        /// <summary>
        /// The slot's turn, for an item laid without a random yaw.
        /// </summary>
        public float Yaw;
        /// <summary>
        /// The zone it stands in (it fills while that zone is active).
        /// </summary>
        public ZoneCoordinate Zone;
        /// <summary>
        /// The faction owning its item (the building's).
        /// </summary>
        public int Owner;
        /// <summary>
        /// The ground item (<see cref="GroundItems"/> id) filling it; -1: empty.
        /// </summary>
        public int Item = -1;
        /// <summary>
        /// The game hour the next item is due.
        /// </summary>
        public double DueAt;
        /// <summary>
        /// How many items it has made (the rolls' key).
        /// </summary>
        public int Made;
    }

    /// <summary>
    /// Every slot, in registration order.
    /// </summary>
    readonly List<Slot> _all = [];
    /// <summary>
    /// The town sites registered.
    /// </summary>
    readonly HashSet<int> _sites = [];
    /// <summary>
    /// The placement groups by record id (null: not one, or empty).
    /// </summary>
    readonly Dictionary<string, ItemPlacementGroup?> _groups = new(StringComparer.Ordinal);
    /// <summary>
    /// The ground items lying in a slot, as of <see cref="_seenRevision"/>.
    /// </summary>
    readonly HashSet<int> _lying = [];
    /// <summary>
    /// The ground items' revision <see cref="_lying"/> was taken at (-1: never).
    /// </summary>
    long _seenRevision = -1;

    /// <summary>
    /// How many slots there are.
    /// </summary>
    public int Count => _all.Count;
    /// <summary>
    /// How many slots hold an item.
    /// </summary>
    public int Filled => _all.Count(s => s.Item >= 0);

    /// <summary>
    /// The run-time name of the building whose slot <paramref name="slot"/> is (its id up to <c>/item</c>); null for none.
    /// </summary>
    public static string? BuildingOf(string? slot) => slot?.LastIndexOf("/item", StringComparison.Ordinal) is > 0 and var end ? slot[..end] : null;

    /// <summary>
    /// How many item slots the building with run-time name <paramref name="buildingKey"/> has and how many hold an item.
    /// </summary>
    public (int Total, int Filled) OfBuilding(string buildingKey)
    {
        string prefix = buildingKey + "/item";
        int total = 0, filled = 0;
        foreach (var s in _all)
            if (s.Id.StartsWith(prefix, StringComparison.Ordinal))
            {
                total++;
                if (s.Item >= 0) filled++;
            }
        return (total, filled);
    }

    /// <summary>
    /// Registers the slots of a town's buildings once, binding the loaded items lying in them.
    /// </summary>
    public void RegisterSite(World world, PopulationData data, int site)
    {
        if (site < 0 || site >= data.Sites.Count || !_sites.Add(site)) return;
        foreach (var b in data.Sites[site].Buildings) AddBuilding(world, data, site, b);
    }

    /// <summary>
    /// A building of a registered town laid out afresh (a town override's residents, <see cref="PopulationData.Relay"/>): its old slots go with the
    /// items lying in them, and its new layout's come.
    /// </summary>
    public void RelayBuilding(World world, PopulationData data, int site, TownBuilding old, TownBuilding relaid)
    {
        if (!_sites.Contains(site)) return;
        EmptyBuilding(world, old);
        AddBuilding(world, data, site, relaid);
    }

    /// <summary>
    /// A building added to a registered town while the world runs (<see cref="TownEdits.Place"/>): its slots, as <see cref="RegisterSite"/> makes a
    /// loaded one's. A town not registered yet takes them when it is.
    /// </summary>
    internal void PlaceBuilding(World world, PopulationData data, int site, TownBuilding b)
    {
        if (_sites.Contains(site)) AddBuilding(world, data, site, b);
    }

    /// <summary>
    /// A building taken out of a town (<see cref="TownEdits.Clear"/>): its slots go; the items lying in them stay on the ground.
    /// </summary>
    internal void RemoveBuilding(TownBuilding b)
    {
        if (b.ItemSlots.Count == 0) return;
        var ids = b.ItemSlots.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        _all.RemoveAll(s => ids.Contains(s.Id));
    }

    /// <summary>
    /// Adds the slots of building <paramref name="b"/> of <paramref name="site"/> once (none for an emptied house), owned by the building's owner.
    /// </summary>
    void AddBuilding(World world, PopulationData data, int site, TownBuilding b)
    {
        if (b.ItemSlots.Count == 0 || world.Fixtures.IsEmptied(b.Key)   // a bought house has none (EmptyBuilding)
            || _all.Any(s => s.Id.StartsWith(b.Key + "/item", StringComparison.Ordinal))) return;
        int townFaction = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        int owner = data.LevelOwnerOf(b, townFaction);
        owner = world.Fixtures.PropertyOwner(b.Key) ?? owner;
        foreach (var s in b.ItemSlots)
        {
            if (Group(data.Db, s.GroupId) is not { } group) continue;
            var slot = new Slot
            {
                Id = s.Id,
                GroupId = s.GroupId,
                Position = s.Position,
                Yaw = TownBuildings.YawOf(s.Rotation),
                Owner = owner,
                Zone = WorldLayout.ZoneOf(s.Position.X, s.Position.Z),
            };
            if (Lying(world, slot, group) is { } found)
            {
                world.GroundItems.Claim(found, slot.Id, owner);
                slot.Item = found;
            }
            _all.Add(slot);
        }
    }

    /// <summary>
    /// A town house the player bought loses its slots and the items lying in them (the original's purchase destroys the interior's item groups,
    /// <c>FUN_14054f4d0</c>, and a group going destroys the item it still holds, <c>FUN_14054a600</c>); an item taken out of its slot already stays
    /// where it is.
    /// </summary>
    public void EmptyBuilding(World world, TownBuilding building)
    {
        var ids = building.ItemSlots.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var slot in _all)
            if (ids.Contains(slot.Id) && slot.Item >= 0) world.GroundItems.Take(slot.Item);
        _all.RemoveAll(s => ids.Contains(s.Id));
    }

    /// <summary>
    /// A building's slots and the items lying in them take <paramref name="owner"/>, including later refills (a town override hands the town's
    /// buildings back).
    /// </summary>
    public void TransferBuilding(World world, TownBuilding building, int owner)
    {
        var ids = building.ItemSlots.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var slot in _all)
        {
            if (!ids.Contains(slot.Id)) continue;
            slot.Owner = owner;
            if (slot.Item >= 0) world.GroundItems.Claim(slot.Item, slot.Id, owner);
        }
    }

    /// <summary>
    /// The id of a loaded ground item, of a record the group keeps, lying in <paramref name="slot"/>; null: none.
    /// </summary>
    static int? Lying(World world, Slot slot, ItemPlacementGroup group)
    {
        foreach (var g in world.GroundItems.All)
        {
            if (g.Slot is not null || g.Item.Saved is null || MathF.Abs(g.Position.Y - slot.Position.Y) > 10) continue;
            if (Vector2.DistanceSquared(new(g.Position.X, g.Position.Z), new(slot.Position.X, slot.Position.Z)) > ClaimReach * ClaimReach)
                continue;
            foreach (var e in group.Items.Entries)
                if (e.Id == g.Item.Record && Kept(e.Type)) return g.Id;
        }
        return null;
    }

    /// <summary>
    /// The placement group of record <paramref name="id"/>, cached; null when it is not one or has no items.
    /// </summary>
    ItemPlacementGroup? Group(GameDatabase db, string id)
    {
        if (!_groups.TryGetValue(id, out var group))
            _groups[id] = group = db.Find(id) is { Type: FcsRecordType.ItemPlacementGroup } r
                    && ItemPlacementGroup.From(db, r) is { Items.Entries.Count: > 0 } g ? g : null;
        return group;
    }

    /// <summary>
    /// The slots of a zone start again: empty and due (a replaced town's zone, whose ITEM_PLACEMENT_GROUP states the original drops). Their items are
    /// the caller's to remove.
    /// </summary>
    public void ResetZone(ZoneCoordinate zone)
    {
        foreach (var s in _all)
            if (s.Zone == zone) (s.Item, s.DueAt) = (-1, double.MinValue);
    }

    /// <summary>
    /// Empties the slots whose item left the ground and fills the due ones in active zones.
    /// </summary>
    public void Step(World world, PopulationData data)
    {
        if (_all.Count == 0 || world.ActiveZones is not { } active) return;
        if (_seenRevision != world.GroundItems.Revision)
        {
            _lying.Clear();
            foreach (var g in world.GroundItems.All)
                if (g.Slot is not null) _lying.Add(g.Id);
            foreach (var s in _all)
            {
                if (s.Item < 0 || _lying.Contains(s.Item)) continue;
                s.Item = -1;
                s.DueAt = NextDue(world, Group(data.Db, s.GroupId)!, s);
            }
        }
        foreach (var s in _all)
            if (s.Item < 0 && world.GameHours >= s.DueAt && active.Contains(s.Zone)) Fill(world, data, s);
        _seenRevision = world.GroundItems.Revision;
    }

    /// <summary>
    /// When an emptied slot is due again: now plus a whole number of hours in the group's [min, max] respawn hours (<c>FUN_140549e60</c>).
    /// </summary>
    static double NextDue(World world, ItemPlacementGroup group, Slot s)
    {
        int lo = Math.Max(group.MinRespawnHours, 0), hi = Math.Max(group.MaxRespawnHours, lo);
        return world.GameHours + lo + Rng.Int(Rng.Hash(world.Seed, Rng.StableHash(s.Id), RngPurpose.Loot, (ulong)s.Made << 1 | 1), hi - lo + 1);
    }

    /// <summary>
    /// The item types a slot keeps while they lie in it (<c>FUN_140549e60</c>, <b>Verified</b>): ITEM, WEAPON, ARMOUR, CONTAINER, NEST_ITEM,
    /// MAP_ITEM, CROSSBOW, LIMB_REPLACEMENT. Any other (a BLUEPRINT) is let go at once: the slot is due again after the respawn time whether or not
    /// the item still lies there.
    /// </summary>
    static bool Kept(FcsRecordType type) => type is FcsRecordType.Item or FcsRecordType.Weapon or FcsRecordType.Armour or FcsRecordType.Container
            or FcsRecordType.NestItem
        or FcsRecordType.MapItem or FcsRecordType.Crossbow or FcsRecordType.LimbReplacement;

    /// <summary>
    /// Puts a new item from the group's pick in slot <paramref name="s"/>, or makes it due again when the pick gives none or a loose item.
    /// </summary>
    void Fill(World world, PopulationData data, Slot s)
    {
        var group = Group(data.Db, s.GroupId)!;
        var rng = new RandomStream(world.Seed, Rng.StableHash(s.Id) ^ ((ulong)s.Made << 40), RngPurpose.Loot);
        s.Made++;
        var (item, type, noMaker) = Roll(data, group.Items, s.Owner, ref rng);
        if (item is null)
        {
            // The data cannot make it: try again later; a weapon with no usable manufacturer is tried again on the next update (FUN_1409540d0,
            // Verified).
            if (!noMaker) s.DueAt = world.GameHours + Math.Max(group.MinRespawnHours, 1);
            return;
        }
        float yaw = group.RandomYaw ? rng.NextFloat() * MathF.Tau : s.Yaw;
        bool kept = Kept(type);
        int placed = world.GroundItems.Place(item, s.Position, yaw, kept ? s.Id : null, s.Owner);
        if (kept) s.Item = placed;
        else s.DueAt = NextDue(world, group, s);
    }

    /// <summary>
    /// One item of an item list as the original's pick makes it (<c>FUN_1409540d0</c>): a weighted entry, the grade's quality for armour (from
    /// <paramref name="owner"/>'s faction), a weapon's manufacturer and model. Null (with the entry's type) when the data cannot make it, or a weapon
    /// has no usable manufacturer (<c>NoMaker</c>).
    /// </summary>
    public static (ItemInstance? Item, FcsRecordType Type, bool NoMaker) Roll(PopulationData data, VendorList list, int owner, ref RandomStream rng)
    {
        // FUN_1409540d0: one entry is taken with no draw
        var entry = list.Entries.Count == 1 ? list.Entries[0] : ShopFiller.Pick(list.Entries, ref rng);
        var faction = (uint)owner < (uint)data.Factions.Count ? data.Db.Find(data.Factions[owner].Id) : null;

        // The original hands every pick the grade's quality (FUN_140580750, Verified): it becomes the level of armour, crossbows and limbs; what it
        // does to a plain ITEM is Unknown (none here).
        float quality = CharacterGenerator.QualityOfGrade(ShopFiller.Grade(faction, ref rng));
        if (entry.Type is not (FcsRecordType.Armour or FcsRecordType.Crossbow or FcsRecordType.LimbReplacement))
            quality = 0;
        if (data.Items.Create(entry.Id, 1, quality) is not { } item) return (null, entry.Type, false);
        if (entry.Type != FcsRecordType.Weapon) return (item, entry.Type, false);
        if (ShopFiller.Maker(list, data.Db, ref rng) is not { } made) return (null, entry.Type, true);
        item.CompanyId = made.Maker.StringId;
        item.MaterialId = made.Model ?? "";
        item.Level = made.Level;
        return (item, entry.Type, false);
    }

    /// <summary>
    /// Adds each slot's item, due hour and count made to the world hash.
    /// </summary>
    public void Hash(ref StateHasher h)
    {
        h.Add(_all.Count);
        foreach (var s in _all)
        {
            h.Add(s.Item);
            h.Add(s.DueAt);
            h.Add(s.Made);
        }
    }
}