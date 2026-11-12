using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Items;

/// <summary>
/// Read a map item (inventory right click, docs/game/ui-screens.md "Map"): adds its towns to the player's map.
/// </summary>
public sealed record ReadMapItem(CharacterId Character, InventoryItemToken Item) : SimCommand;

/// <summary>
/// The towns a MAP_ITEM reveals (docs/game/ui-screens.md "Map"). Per <c>towns</c> link: the placed towns of that record, without nests and ones
/// already discovered; v0 of them at random when v0 is above 0 and below their number. Then <c>unlock count</c> weighted draws by v1 without repeats,
/// or all of them when it is 0 or not less than the candidates. The original seeds the draws from the item; Meitou from the world seed and the item
/// record (engine choice), so the tooltip's list is what a read reveals.
/// </summary>
public static class MapItems
{
    /// <summary>
    /// The placed town sites reading <paramref name="map"/> reveals to a player who knows <paramref name="known"/>, drawn from
    /// <paramref name="seed"/> and the record.
    /// </summary>
    public static List<int> Reveals(PopulationData data, GameRecord map, IReadOnlyList<TownKnowledge> known, ulong seed)
    {
        ulong key = Rng.StableHash(map.StringId);
        int roll = 0;
        ulong Next() => Rng.Hash(seed, key, RngPurpose.MapItem, (ulong)roll++);
        var pool = new List<(int Site, float Weight)>();
        foreach (var link in map.GetReferences("towns"))
        {
            var candidates = data.Sites.Where(s => s.Town.Id == link.TargetStringId
                    && s.Town.Type is not (TownType.Nest or TownType.NestMarker or TownType.Null)
                && (s.Index >= known.Count || (known[s.Index] & TownKnowledge.Discovered) == 0)).Select(s => s.Index).ToList();
            int count = link.Values.Value0;
            while (count > 0 && count < candidates.Count) candidates.RemoveAt(Rng.Int(Next(), candidates.Count));
            foreach (var site in candidates) pool.Add((site, Math.Max(0, link.Values.Value1)));
        }
        int unlock = map.GetInt("unlock count", 1);
        if (unlock <= 0 || unlock >= pool.Count) return [.. pool.Select(p => p.Site)];
        var chosen = new List<int>();
        while (chosen.Count < unlock && pool.Count > 0)
        {
            float total = pool.Sum(p => p.Weight);
            int pick = pool.Count - 1;
            if (total > 0)
            {
                float r = Rng.Float(Next()) * total;
                for (int i = 0; i < pool.Count; i++) { if (r < pool[i].Weight) { pick = i; break; } r -= pool[i].Weight; }
            }
            else pick = Rng.Int(Next(), pool.Count);
            chosen.Add(pool[pick].Site);
            pool.RemoveAt(pick);
        }
        return chosen;
    }
}