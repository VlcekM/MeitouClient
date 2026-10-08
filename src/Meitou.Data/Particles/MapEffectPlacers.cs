using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Data.Particles;

/// <summary>One effect placed on the map by a hidden MAP_FEATURES placer: the effect and its place in the world.</summary>
public sealed record MapEffectPlacement(EffectRecord Effect, Vector3 Position, string Feature);

/// <summary>
/// The map-feature effect placers (docs/formats/weather.md "Effect placers on the map"): MAP_FEATURES records with <c>hidden</c> true draw no mesh and
/// carry an EFFECT as an instance of the feature record ("Volk-Cloud Placer", "Volc-small-steamers Placer", "Permanent-dust-storm Placer"); each
/// placement of such a feature in <c>features.dat</c> makes the effect at the placement's position plus the instance's offset (rotated by the
/// placement's rotation: Observed, the offsets of the base game are all 0).
/// </summary>
public static class MapEffectPlacers
{
    public static List<MapEffectPlacement> Find(GameDatabase db, MapFeatureFile features)
    {
        var effects = new Dictionary<string, EffectRecord>(StringComparer.Ordinal);
        var result = new List<MapEffectPlacement>();
        foreach (var (_, feature) in features.All())
        {
            if (db.Find(feature.StringId) is not { Type: FcsRecordType.MAP_FEATURES } record || !record.GetBool("hidden")) continue;
            foreach (var instance in record.Instances.Values)
            {
                if (instance.IsCleared || db.Find(instance.Target) is not { Type: FcsRecordType.EFFECT } effect) continue;
                if (!effects.TryGetValue(effect.StringId, out var def)) effects[effect.StringId] = def = EffectRecord.From(effect, db);
                result.Add(new MapEffectPlacement(def, feature.Position + Vector3.Transform(instance.Position * feature.Scale, feature.Rotation), record.Name));
            }
        }
        return result;
    }

    /// <summary>One group per placed effect, with a unit for every placement (nothing for an effect whose particle system is unknown).</summary>
    public static List<EffectGroup> Groups(IEnumerable<MapEffectPlacement> placements, ParticleLibrary library, EffectWorld world, int seed)
    {
        var groups = new List<EffectGroup>();
        int i = 0;
        foreach (var byEffect in placements.GroupBy(p => p.Effect))
            if (library.FindSystem(byEffect.Key.ParticleSystem) is { } system)
                groups.Add(new PlacerEffectGroup(byEffect.Key, system, byEffect.Select(p => p.Position), seed + 1000 + i++, world));
        return groups;
    }
}
