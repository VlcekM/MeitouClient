using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Economy;
using Meitou.Simulation.Trading;

namespace Meitou.Simulation;

public sealed partial class PopulationSystem
{
    // The nests whose debris and loot have been laid in this world (a nest unloaded and loaded again keeps what lies there).
    readonly HashSet<int> nestsScattered = [];

    /// <summary>The ground-item tag of what a nest lays (kept, not counted against the ground item limit).</summary>
    const string NestTag = "nest";

    /// <summary>
    /// What a nest lays out when its zone first loads (docs/formats/zones.md "Nest debris", <b>Verified</b>, the TOWN virtual +0x388 <c>FUN_140938440</c>): its <c>debris</c> entries
    /// (v0..v1 of a NEST_ITEM; an item with a <c>physics file</c>, the eggs, is a real item: v0..v1 times <c>cluster min</c>..<c>cluster max</c> of them, each at a random point within
    /// <c>cluster range</c> of the centre; any other, bones, dung and trash, is a mesh on the ground, not modelled here) and its <c>loot spawn</c> entries (v0..v1 of items rolled from
    /// the VENDOR_LIST as a slot of an interior layout does, each at a random point of the nest where the ground is above 1). Not made for a nest a save names (its items are in the save).
    /// <b>Engine choices</b>: the random stream (the original seeds <c>rand()</c> from the nest's position); the loot's reach is the TOWN's <c>size radius</c> (<b>Unknown</b> in the original).
    /// </summary>
    void ScatterNest(World world, WildSite site, Vector2 centre)
    {
        if (site.SaveInstance is not null || site.Nest is not { } nest || nest.Debris.Count + nest.LootSpawn.Count == 0 || !nestsScattered.Add(site.Index)) return;
        var walk = world.Walkability;
        ulong key = Rng.Mix(SeedSalts.NestDebris ^ (ulong)BitConverter.SingleToUInt32Bits(centre.X) << 32 ^ BitConverter.SingleToUInt32Bits(centre.Y));
        int entry = 0;
        foreach (var link in nest.Debris)
        {
            if (data.Db.Find(link.Id) is not { Type: FcsRecordType.NEST_ITEM } record || record.GetPath("physics file").Length == 0) continue;
            var rng = new RandomStream(world.Seed, Rng.Mix(key ^ (ulong)entry++), RngPurpose.Loot);
            int count = Between(ref rng, link.V0, link.V1);
            for (int i = 0; i < count; i++)
                for (int copies = Between(ref rng, record.GetInt("cluster min"), record.GetInt("cluster max")); copies > 0; copies--)
                {
                    var at = Around(centre, record.GetInt("cluster range"), ref rng);
                    if (data.Items.Create(record.StringId, 1, 0) is not { } item) return;
                    world.GroundItems.Place(item, new Vector3(at.X, walk.TerrainHeight(at.X, at.Y), at.Y), rng.NextFloat() * MathF.Tau, NestTag, -1);
                }
        }
        foreach (var link in nest.LootSpawn)
        {
            if (data.Db.Find(link.Id) is not { Type: FcsRecordType.VENDOR_LIST } record) continue;
            var list = VendorList.From(data.Db, record);
            if (list.Entries.Count == 0) continue;
            var rng = new RandomStream(world.Seed, Rng.Mix(key ^ (ulong)entry++), RngPurpose.Loot);
            for (int i = Between(ref rng, link.V0, link.V1); i > 0; i--)
            {
                var at = Around(centre, nest.SizeRadius, ref rng);
                float ground = walk.TerrainHeight(at.X, at.Y);
                if (Items.ItemSlots.Roll(data, list, -1, ref rng).Item is not { } item || ground < 1) continue;
                world.GroundItems.Place(item, new Vector3(at.X, ground, at.Y), rng.NextFloat() * MathF.Tau, NestTag, -1);
            }
        }
    }

    /// <summary>An integer of [a, b] (<c>FUN_1409b3b40</c>, the bounds either way round).</summary>
    static int Between(ref RandomStream rng, int a, int b) => a == b ? a : Math.Min(a, b) + rng.NextInt(Math.Abs(b - a) + 1);

    /// <summary>A point at a random distance of at most <paramref name="range"/> from <paramref name="centre"/>, in a random direction (the original's two uniform draws of -1..1, normalised).</summary>
    static Vector2 Around(Vector2 centre, float range, ref RandomStream rng)
    {
        var direction = new Vector2(rng.NextFloat() * 2 - 1, rng.NextFloat() * 2 - 1);
        direction = direction.LengthSquared() < 1e-12f ? Vector2.UnitX : Vector2.Normalize(direction);
        return centre + direction * (rng.NextFloat() * range);
    }
}
