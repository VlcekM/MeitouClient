using System.Numerics;

namespace Meitou.Data.World;

/// <summary>A zone's instances of one mesh from one layer, as the culls' records (<see cref="FoliageInstanceRecord"/>: transform, and in <c>Ground</c> the ground position, scale and the group's index in its zone).</summary>
/// <param name="MaxScale">The largest instance scale in the group.</param>
public sealed record FoliageInstanceGroup(FoliageMesh Mesh, FoliageLayer Layer, FoliageInstanceRecord[] Instances, float MaxScale);

/// <summary>
/// A laid-out zone with its instances already grouped by (mesh, layer), in the order each pair first appears, each instance in the order placed (what the renderer
/// built from <see cref="FoliageZone.Instances"/> on its worker with <c>GroupBy</c>). <see cref="Zone"/> has the grass patches, <see cref="FoliageZone.Resources"/>
/// and <see cref="FoliageZone.Complete"/> but an empty <see cref="FoliageZone.Instances"/>: they are in <see cref="Groups"/>, which a layout read back from the cache
/// fills straight from the file's records (<see cref="FoliageLayoutCache.TryLoadGrouped"/>), without the 56-byte-per-instance list and the copies grouping it made.
/// </summary>
public sealed class GroupedFoliageZone
{
    public required FoliageZone Zone { get; init; }
    public required FoliageGround? Ground { get; init; }
    public required List<FoliageInstanceGroup> Groups { get; init; }
    /// <summary>The number of instances over all groups.</summary>
    public required int InstanceCount { get; init; }
    /// <summary>Lowest and highest placed instance (0 and 0 when there are none).</summary>
    public required float MinY { get; init; }
    public required float MaxY { get; init; }
}

/// <summary>Groups a zone's instances for the renderer.</summary>
public static class FoliageGrouping
{
    /// <summary>Groups <paramref name="zone"/>'s instances (two passes: count per group, then fill exact-size arrays). The zone's own instance list is left alone.</summary>
    public static GroupedFoliageZone Group(FoliageZone zone, FoliageGround? ground)
    {
        var list = zone.Instances;
        var slotOf = new Dictionary<(FoliageMesh, FoliageLayer), int>();
        var keys = new List<(FoliageMesh Mesh, FoliageLayer Layer)>();
        var counts = new List<int>();
        var slots = new int[list.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            var key = (list[i].Mesh, list[i].Layer);
            if (!slotOf.TryGetValue(key, out int slot)) { slot = keys.Count; slotOf[key] = slot; keys.Add(key); counts.Add(0); }
            slots[i] = slot;
            counts[slot]++;
        }
        var records = new FoliageInstanceRecord[keys.Count][];
        var maxScale = new float[keys.Count];
        var filled = new int[keys.Count];
        for (int g = 0; g < records.Length; g++) records[g] = ZoneArrays.Uninitialized<FoliageInstanceRecord>(counts[g]);
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        for (int i = 0; i < slots.Length; i++)
        {
            ref readonly var n = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)[i];
            int g = slots[i], at = filled[g]++;
            records[g][at] = new FoliageInstanceRecord
            {
                Transform = FoliageInstance.TransformOf(n.Scale, n.Orientation, n.Position),
                Ground = new Vector4(n.Position.X, n.Position.Z, n.Scale, g),
            };
            maxScale[g] = at == 0 ? n.Scale : Math.Max(maxScale[g], n.Scale);
            minY = Math.Min(minY, n.Position.Y);
            maxY = Math.Max(maxY, n.Position.Y);
        }
        var groups = new List<FoliageInstanceGroup>(keys.Count);
        for (int g = 0; g < keys.Count; g++) groups.Add(new FoliageInstanceGroup(keys[g].Mesh, keys[g].Layer, records[g], maxScale[g]));
        return new GroupedFoliageZone
        {
            Zone = zone, Ground = ground, Groups = groups, InstanceCount = slots.Length,
            MinY = slots.Length == 0 ? 0 : minY, MaxY = slots.Length == 0 ? 0 : maxY,
        };
    }
}
