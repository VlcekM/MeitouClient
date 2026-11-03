using Meitou.Data.Gameplay;

namespace Meitou.Data.World;

/// <summary>
/// The town buildings the residents' templates ruin (docs/game/buildings-production.md "Ruined by residents"): the original's squad setup <c>FUN_14057f1f0</c>
/// destroys a home whose template has <c>building ruined</c> when the building can be (<c>FUN_14055b520</c>). Meitou lays that out once in the level data, before
/// anything reads a building's <c>destroyed</c>, from the same seed-free assignment the population houses its towns with (<see cref="ResidentHomes.Assign"/>).
/// </summary>
public static class ResidentRuins
{
    /// <summary>Sets the level-data state's <c>destroyed</c> of each building its residents ruin; returns how many. A second call on the same levels does nothing.</summary>
    public static int Apply(GameDatabase db, WorldLevelData levels, BuildingLayouts layouts)
    {
        lock (levels)
        {
            if (levels.ResidentRuinsApplied) return 0;
            levels.ResidentRuinsApplied = true;
            var buildings = TownBuildings.Build(db, levels, layouts, (_, _) => 0f);
            string? player = db.Find(FactionData.PlayerId) is not null ? FactionData.PlayerId : null;
            int ruined = 0;
            foreach (var placement in levels.Towns())
            {
                if (!buildings.TryGetValue(placement.InstanceId, out var list) || db.Find(placement.TownId) is not { Type: Fcs.FcsRecordType.TOWN } record) continue;
                var town = TownData.From(record);
                var faction = town.Faction is { } id && db.Find(id) is { Type: Fcs.FcsRecordType.FACTION } f ? FactionData.From(f) : null;
                foreach (var (b, t) in ResidentHomes.Assign(db, town, faction, list, player))
                    if (t.BuildingRuined && Ruin(db, b)) ruined++;
            }
            return ruined;
        }
    }

    static bool Ruin(GameDatabase db, TownBuilding b)
    {
        if (b.Destroyed || b.LevelState is not { } state || db.Find(b.BuildingId) is not { } record || !CanBeDestroyed(db, record)) return false;
        state.BoolTable["destroyed"] = true;
        return true;
    }

    /// <summary>
    /// Whether a building can be ruined (<c>FUN_14055b520</c>, Verified decompile): it or a part reached through <c>parts</c> (followed to any depth) has a
    /// <c>destroyed mesh</c>. The original also says yes when the building's int at +0x198 is 7 (its meaning is Unknown; not modelled).
    /// </summary>
    public static bool CanBeDestroyed(GameDatabase db, GameRecord building)
    {
        var queue = new Queue<GameRecord>([building]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { building.StringId };
        while (queue.TryDequeue(out var r))
        {
            if (r.GetPath("destroyed mesh").Length > 0) return true;
            foreach (var part in r.GetReferences("parts"))
                if (seen.Add(part.TargetStringId) && db.Find(part.TargetStringId) is { } p) queue.Enqueue(p);
        }
        return false;
    }
}
