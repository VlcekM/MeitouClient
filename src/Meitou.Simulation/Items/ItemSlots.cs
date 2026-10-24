using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Economy;
using Meitou.Data.World;
using Meitou.Simulation.Trading;

namespace Meitou.Simulation.Items;

/// <summary>
/// The loose-item slots of the towns' interiors (docs/game/factions-squads-towns.md "Loose items in buildings"; docs/simulation.md "Ground items"): each ITEM_PLACEMENT_GROUP object
/// of a building's interior layout keeps one item of its group lying at its place. Registered with the town's fixtures (<see cref="RegisterSite"/>) and kept when the town unloads.
/// <list type="bullet">
/// <item>A slot starts empty and due, so it fills as soon as its zone is active. An empty slot whose time has come and whose zone is active gets one item; when that item leaves
/// the ground (picked up, used up), the next is due an int in [<c>min respawn time</c>, <c>max respawn time</c>] game hours later (the original's timer, <b>Verified</b>).</item>
/// <item>The item is a weighted pick from the group's lists (as <see cref="ShopFiller"/>'s); armour's quality is <see cref="CharacterGenerator.QualityOfGrade"/> of a grade rolled from the
/// owner faction's armour chances (the map is <b>Verified</b>, the grade's source an <b>engine</b> reading); a weapon gets a manufacturer from the group's and a model.</item>
/// <item>It lies at the slot laid flat, turned as the slot or at random (<c>random yaw</c>), owned by the building's faction (<see cref="GroundItem.OwnedBy"/>: taking it is stealing).</item>
/// <item>Engine choices: the respawn times are not saved (a reload makes taken slots due at once); a loaded save's item lying in its slot (within <see cref="ClaimReach"/>, one of
/// the group's records) is bound to it instead of a new one.</item>
/// </list>
/// Serial phases only (the population's slow-world step).
/// </summary>
public sealed class ItemSlots
{
    /// <summary>How near a loaded item must lie to a slot (XZ; and within 10 in height) to be taken as its item.</summary>
    public const float ClaimReach = 2;

    sealed class Slot
    {
        public required string Id;
        public required string GroupId;
        public Vector3 Position;
        public float Yaw;
        public ZoneCoordinate Zone;
        public int Owner;
        /// <summary>The ground item (<see cref="GroundItems"/> id) filling it; -1: empty.</summary>
        public int Item = -1;
        public double DueAt;
        public int Made;
    }

    readonly List<Slot> all = [];
    readonly HashSet<int> sites = [];
    readonly Dictionary<string, ItemPlacementGroup?> groups = new(StringComparer.Ordinal);
    readonly HashSet<int> lying = [];
    long seenRevision = -1;

    public int Count => all.Count;
    /// <summary>How many slots hold an item.</summary>
    public int Filled => all.Count(s => s.Item >= 0);

    /// <summary>Registers the slots of a town's buildings once, binding the loaded items lying in them.</summary>
    public void RegisterSite(World world, PopulationData data, int site)
    {
        if (site < 0 || site >= data.Sites.Count || !sites.Add(site)) return;
        int townFaction = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        foreach (var b in data.Sites[site].Buildings)
        {
            if (b.ItemSlots.Count == 0) continue;
            int owner = b.OwnerFactionId is { } o && data.FactionIndex(o) is var fi and >= 0 ? fi : townFaction;
            owner = world.Fixtures.PropertyOwner(b.Key) ?? owner;
            foreach (var s in b.ItemSlots)
            {
                if (Group(data.Db, s.GroupId) is not { } group) continue;
                var slot = new Slot
                {
                    Id = s.Id, GroupId = s.GroupId, Position = s.Position, Yaw = TownBuildings.YawOf(s.Rotation), Owner = owner,
                    Zone = WorldLayout.ZoneOf(s.Position.X, s.Position.Z),
                };
                if (Lying(world, slot, group) is { } found)
                {
                    world.GroundItems.Claim(found, slot.Id, owner);
                    slot.Item = found;
                }
                all.Add(slot);
            }
        }
    }

    /// <summary>Loose furnishings follow the purchased house, including subsequent slot refills.</summary>
    public void TransferBuilding(World world, TownBuilding building, int owner)
    {
        var ids = building.ItemSlots.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var slot in all)
        {
            if (!ids.Contains(slot.Id)) continue;
            slot.Owner = owner;
            if (slot.Item >= 0) world.GroundItems.Claim(slot.Item, slot.Id, owner);
        }
    }

    static int? Lying(World world, Slot slot, ItemPlacementGroup group)
    {
        foreach (var g in world.GroundItems.All)
        {
            if (g.Slot is not null || g.Item.Saved is null || MathF.Abs(g.Position.Y - slot.Position.Y) > 10) continue;
            if (Vector2.DistanceSquared(new(g.Position.X, g.Position.Z), new(slot.Position.X, slot.Position.Z)) > ClaimReach * ClaimReach) continue;
            foreach (var e in group.Items.Entries) if (e.Id == g.Item.Record) return g.Id;
        }
        return null;
    }

    ItemPlacementGroup? Group(GameDatabase db, string id)
    {
        if (!groups.TryGetValue(id, out var group))
            groups[id] = group = db.Find(id) is { Type: FcsRecordType.ITEM_PLACEMENT_GROUP } r && ItemPlacementGroup.From(db, r) is { Items.Entries.Count: > 0 } g ? g : null;
        return group;
    }

    /// <summary>Empties the slots whose item left the ground and fills the due ones in active zones.</summary>
    public void Step(World world, PopulationData data)
    {
        if (all.Count == 0 || world.ActiveZones is not { } active) return;
        if (seenRevision != world.GroundItems.Revision)
        {
            lying.Clear();
            foreach (var g in world.GroundItems.All) if (g.Slot is not null) lying.Add(g.Id);
            foreach (var s in all)
            {
                if (s.Item < 0 || lying.Contains(s.Item)) continue;
                s.Item = -1;
                var group = Group(data.Db, s.GroupId)!;
                int lo = Math.Max(group.MinRespawnHours, 0), hi = Math.Max(group.MaxRespawnHours, lo);
                s.DueAt = world.GameHours + lo + Rng.Int(Rng.Hash(world.Seed, Rng.StableHash(s.Id), RngPurpose.Loot, (ulong)s.Made << 1 | 1), hi - lo + 1);
            }
        }
        foreach (var s in all)
            if (s.Item < 0 && world.GameHours >= s.DueAt && active.Contains(s.Zone)) Fill(world, data, s);
        seenRevision = world.GroundItems.Revision;
    }

    void Fill(World world, PopulationData data, Slot s)
    {
        var group = Group(data.Db, s.GroupId)!;
        var rng = new RandomStream(world.Seed, Rng.StableHash(s.Id) ^ ((ulong)s.Made << 40), RngPurpose.Loot);
        s.Made++;
        var entry = ShopFiller.Pick(group.Items.Entries, ref rng);
        var faction = (uint)s.Owner < (uint)data.Factions.Count ? data.Db.Find(data.Factions[s.Owner].Id) : null;
        // The original hands every item the grade's quality; only armour keeps it here, as shop stock does (what it does to other items is Unknown).
        float quality = CharacterGenerator.QualityOfGrade(ShopFiller.Grade(faction, ref rng));
        if (entry.Type != FcsRecordType.ARMOUR) quality = 0;
        if (data.Items.Create(entry.Id, 1, quality) is not { } item)
        {
            s.DueAt = world.GameHours + Math.Max(group.MinRespawnHours, 1);   // the data cannot make it: try again later
            return;
        }
        if (entry.Type == FcsRecordType.WEAPON && ShopFiller.Maker(group.Items, data.Db, ref rng) is { } made)
        {
            item.CompanyId = made.Maker.StringId;
            item.MaterialId = made.Model ?? "";
            item.Level = made.Level;
        }
        float yaw = group.RandomYaw ? rng.NextFloat() * MathF.Tau : s.Yaw;
        s.Item = world.GroundItems.Place(item, s.Position, yaw, s.Id, s.Owner);
    }

    public void Hash(ref StateHasher h)
    {
        h.Add(all.Count);
        foreach (var s in all)
        {
            h.Add(s.Item);
            h.Add(s.DueAt);
            h.Add(s.Made);
        }
    }
}
