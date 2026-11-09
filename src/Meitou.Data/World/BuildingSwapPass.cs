using System.Numerics;
using Meitou.Data.Gameplay;

namespace Meitou.Data.World;

/// <summary>
/// The factions' building swaps applied to the level's placed buildings (docs/game/factions-squads-towns.md "Building swaps"):
/// <c>RootObjectFactory::createBuilding</c> (<c>FUN_14057cc70</c>) asks the owner's faction (the placement's own <c>owner faction ID</c>, else the
/// faction of the town it stands in) whether a building is replaced, skipped or randomly swapped before it makes it, in a zone that is made fresh.
/// Meitou resolves it once for the level data, which every consumer of the placements shares (the drawn world, the town buildings, the fixtures, the
/// navmesh): a replaced placement takes the new building's id, a skipped one is cleared. The original's weighted pick is unseeded and its result
/// lives in the zone's saved building list; here the roll is fixed by the placement, so a game and its reloads agree (engine choice). The layouts'
/// furniture goes through the same swaps (other callers of <c>createBuilding</c>): the levels keep the swaps and owners
/// (<see cref="WorldLevelData.FurnitureSwaps"/>) for every <see cref="BuildingLayouts"/> read from them.
/// </summary>
public static class BuildingSwapPass
{
    /// <summary>
    /// Applies the swaps of <paramref name="db"/> to <paramref name="levels"/>; returns how many placements changed or went.
    /// </summary>
    public static int ApplyBuildingSwaps(this WorldLevelData levels, GameDatabase db, BuildingLayouts? layouts = null)
    {
        var swaps = BuildingSwaps.From(db);
        if (!swaps.Any) return 0;
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var towns = new List<(Vector3 At, float Radius, string Faction)>();
        foreach (var t in levels.Towns())
            if (db.Find(t.TownId) is { Type: Fcs.FcsRecordType.Town } record && TownData.From(record) is { Faction: { } faction, Radius: > 0 } data)
                towns.Add((t.Position, data.Radius, faction));
        int changed = 0;
        foreach (var (zone, zoneDb) in levels.Zones)
        {
            if (zoneDb.Find(WorldLevelData.BuildingListId) is not { } list) continue;
            foreach (var instance in list.Instances.Values)
            {
                if (instance.IsCleared || zoneDb.Find(instance.Target) is not null) continue;
                string? state = instance.States.Count > 0 ? instance.States[0] : null;
                string? owner = state is not null && zoneDb.Find(state) is { } stateRecord && stateRecord.GetString("owner faction ID")
                        is { Length: > 0 } id ? id : TownFaction(
                    towns,
                    instance.Position
                );
                if (owner is null) continue;
                owners[BuildingKey.Of(zone, instance.Id)] = owner;
                if (swaps.Replacement(owner, instance.Target, Roll($"{zone.X}.{zone.Y}:{instance.Id}")) is not { } answer)
                    continue;
                instance.Target = answer;
                changed++;
            }
        }
        levels.FurnitureSwaps = (swaps, owners);
        layouts?.UseSwaps(levels);
        return changed;
    }

    static string? TownFaction(List<(Vector3 At, float Radius, string Faction)> towns, Vector3 at)
    {
        string? best = null;
        float bestD = float.MaxValue;
        foreach (var (centre, radius, faction) in towns)
        {
            float d = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(centre.X, centre.Z));
            if (d <= radius * radius && d < bestD) (best, bestD) = (faction, d);
        }
        return best;
    }

    /// <summary>
    /// A draw in [0, 1) fixed by a name (FNV-1a).
    /// </summary>
    internal static float Roll(string name)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in name) h = (h ^ c) * 1099511628211UL;
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        return (h >> 40) / (float)(1 << 24);
    }
}