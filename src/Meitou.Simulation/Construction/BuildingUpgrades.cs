using System.Runtime.CompilerServices;
using Meitou.Data;
using Meitou.Data.Fcs;

namespace Meitou.Simulation.Construction;

/// <summary>
/// Building upgrade chains (docs/game/buildings-production.md "Upgrades"): a BUILDING's upgrade is its first <c>upgrades to</c> reference (Building
/// slot 77), its downgrade the first BUILDING whose <c>upgrades to</c> names it (slot 78). The reverse map is built once per database.
/// </summary>
public static class BuildingUpgrades
{
    static readonly ConditionalWeakTable<GameDatabase, Dictionary<string, string>> Downgrades = [];

    /// <summary>
    /// The BUILDING <paramref name="record"/> upgrades to, or null.
    /// </summary>
    public static string? UpgradeOf(GameDatabase db, string record)
    {
        if (db.Find(record)?.GetReferences("upgrades to") is not [var first, ..]) return null;

        return db.Find(first.TargetStringId) is { Type: FcsRecordType.Building } target ? target.StringId : null;
    }

    /// <summary>
    /// The BUILDING that upgrades to <paramref name="record"/>, or null.
    /// </summary>
    public static string? DowngradeOf(GameDatabase db, string record) => Downgrades.GetValue(db, Build).GetValueOrDefault(record);

    static Dictionary<string, string> Build(GameDatabase db)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in db.OfType(FcsRecordType.Building))
        {
            if (r.GetReferences("upgrades to") is [var first, ..]) map.TryAdd(first.TargetStringId, r.StringId);
        }

        return map;
    }
}
