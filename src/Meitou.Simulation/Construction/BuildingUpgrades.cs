using System.Runtime.CompilerServices;
using Meitou.Data;
using Meitou.Data.Fcs;

namespace Meitou.Simulation.Construction;

/// <summary>
/// Building upgrade chains (docs/game/buildings-production.md "Upgrades"): a BUILDING's upgrade is its first <c>upgrades to</c> reference (Building
/// slot 77), its downgrade the first BUILDING whose <c>upgrades to</c> names it (slot 78). The reverse map is built once per database. A wall
/// piece with a <c>wall master</c> follows its master's research (<see cref="UnlockedUpgradeOf"/>).
/// </summary>
public static class BuildingUpgrades
{
    /// <summary>
    /// The downgrade map of each database (built once, by <see cref="Build"/>).
    /// </summary>
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
    /// The BUILDING <paramref name="record"/> may be upgraded to now, or null (Building slot 77; WallBuilding's <c>FUN_1402a5270</c> for a
    /// <paramref name="wall"/>, <b>Verified</b>): a wall piece with a <c>wall master</c> (its subsections) takes its own upgrade when the master's
    /// upgrade is unlocked, whether or not its own is; otherwise the upgrade counts only when it is unlocked itself.
    /// </summary>
    public static string? UnlockedUpgradeOf(GameDatabase db, string record, bool wall, Func<string, bool> unlocked)
    {
        if (UpgradeOf(db, record) is not { } target) return null;
        if (wall && MasterUnlocked(db, record, unlocked)) return target;

        return unlocked(target) ? target : null;
    }

    /// <summary>
    /// Whether the record's first <c>wall master</c> has an upgrade research has unlocked.
    /// </summary>
    static bool MasterUnlocked(GameDatabase db, string record, Func<string, bool> unlocked) =>
        db.Find(record)?.GetReferences("wall master") is [var master, ..] && UpgradeOf(db, master.TargetStringId) is { } up && unlocked(up);

    /// <summary>
    /// The BUILDING that upgrades to <paramref name="record"/>, or null.
    /// </summary>
    public static string? DowngradeOf(GameDatabase db, string record) => Downgrades.GetValue(db, Build).GetValueOrDefault(record);

    /// <summary>
    /// Builds the downgrade map: for each BUILDING its first <c>upgrades to</c> target maps back to it (the first such BUILDING wins).
    /// </summary>
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
