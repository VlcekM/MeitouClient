using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Construction;

/// <summary>
/// A RESEARCH record (docs/game/buildings-production.md "Research").
/// </summary>
public sealed record ResearchData
{
    /// <summary>
    /// The RESEARCH's string id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The RESEARCH's name in the active language.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// <c>category</c>.
    /// </summary>
    public string Category { get; init; } = "";

    /// <summary>
    /// <c>description</c>.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// <c>level</c> (raised per repetition by <see cref="LevelAt"/>).
    /// </summary>
    public int Level { get; init; }

    /// <summary>
    /// <c>time</c>: the base hours before <c>research rate</c> and the level growth.
    /// </summary>
    public float Time { get; init; }

    /// <summary>
    /// <c>is level upgrade</c>.
    /// </summary>
    public bool IsLevelUpgrade { get; init; }

    /// <summary>
    /// <c>blueprint only</c>: not offered as available research (the gear blueprints <see cref="GameDatabase.AddGearBlueprints"/> adds set it).
    /// </summary>
    public bool BlueprintOnly { get; init; }

    /// <summary>
    /// <c>requirements</c>: RESEARCH ids that must be finished, other records (buildings) that must be unlocked.
    /// </summary>
    public IReadOnlyList<string> Requirements { get; init; } = [];

    /// <summary>
    /// <c>cost</c>: items and counts (v0) paid when it is queued.
    /// </summary>
    public IReadOnlyList<(string Item, int Count)> Cost { get; init; } = [];

    /// <summary>
    /// <c>enable buildings</c>: the BUILDINGs it unlocks.
    /// </summary>
    public IReadOnlyList<string> EnableBuildings { get; init; } = [];

    /// <summary>
    /// <c>repeats</c>: how many times it can be researched in all, at least once (<b>Verified</b>, the loader <c>FUN_140834b70</c> makes repetitions
    /// 1 .. repeats - 1 beside the base record).
    /// </summary>
    public int Repeats { get; init; }

    /// <summary>
    /// <c>repeat mult</c>: the cost and time factor per repetition (<b>Verified</b>, <c>FUN_1408320a0</c>).
    /// </summary>
    public float RepeatMult { get; init; } = 1;

    /// <summary>
    /// The crafts it unlocks (<c>enable item</c>, <c>enable armour</c>, ...): kind, record and the repetition (v0, 0 = the first) that unlocks it.
    /// </summary>
    public IReadOnlyList<(CraftKind Kind, string Id, int Repetition)> EnableCrafts { get; init; } = [];

    /// <summary>
    /// The repetition (v0) and level floor (v1) of every entry of the eight <c>enable</c> lists (<see cref="UnlockFields"/>).
    /// </summary>
    public IReadOnlyList<(int Repetition, int Level)> EnableFloors { get; init; } = [];

    /// <summary>
    /// The <c>level</c> of repetition <paramref name="times"/> (0 the first; <b>Verified</b>, <c>FUN_1408320a0</c>, <c>FUN_140831e60</c>): with
    /// <c>repeats</c> above 0 each repetition keeps the <c>enable</c> entries whose v0 is its number and its level is raised to at least their v1;
    /// without repeats nothing is filtered and the level stays.
    /// </summary>
    public int LevelAt(int times)
    {
        int level = Level;
        if (Repeats > 0)
            foreach (var (repetition, floor) in EnableFloors)
                if (repetition == times && floor > level) level = floor;
        return level;
    }

    /// <summary>
    /// <c>improve buildings</c>: the BUILDINGs its <see cref="ProductionMult"/>, <see cref="PowerIncrease"/> and <see cref="CapacityIncrease"/> apply
    /// to (<b>Observed</b>, <c>FUN_140831a60</c>).
    /// </summary>
    public IReadOnlyList<string> ImproveBuildings { get; init; } = [];

    /// <summary>
    /// <c>production mult</c>: replaces the listed buildings' multiplier (the last finished research wins).
    /// </summary>
    public float ProductionMult { get; init; } = 1;

    /// <summary>
    /// <c>power increase</c> and <c>power capacity increase</c>: added to the listed buildings' <c>power output</c> and <c>power capacity</c>.
    /// </summary>
    public int PowerIncrease { get; init; }

    /// <summary>
    /// <c>power capacity increase</c>: added to the listed buildings' <c>power capacity</c>.
    /// </summary>
    public int CapacityIncrease { get; init; }

    /// <summary>
    /// The records it unlocks, field by field in <see cref="UnlockFields"/> order (the research window's description, <c>FUN_140495e00</c>).
    /// </summary>
    public IReadOnlyList<(string Field, string Id)> Unlocks { get; init; } = [];

    /// <summary>
    /// The <c>enable</c> fields the research window lists, in its order (<b>Verified</b>, <c>FUN_140495e00</c>).
    /// </summary>
    public static readonly string[] UnlockFields =
        ["enable buildings", "enable weapon type", "enable weapon model", "enable armour", "enable item", "enable crossbow",
            "enable backpack", "enable robotics"];

    /// <summary>
    /// The <c>enable</c> list of each craft kind (fcs.def, <b>Verified</b>); weapon types and models both unlock weapons.
    /// </summary>
    public static readonly (string Field, CraftKind Kind)[] EnableFields =
    [
        ("enable item", CraftKind.Item), ("enable armour", CraftKind.Armour), ("enable weapon type", CraftKind.Weapon),
        ("enable weapon model", CraftKind.Weapon),
        ("enable crossbow", CraftKind.Crossbow), ("enable backpack", CraftKind.Backpack), ("enable robotics", CraftKind.Robotics),
    ];

    /// <summary>
    /// The factor of repetition <paramref name="times"/> (0 the first; <b>Verified</b>, <c>FUN_1408320a0</c>): 1 for the first two, then <c>repeat
    /// mult</c>^(times - 1) by repeated multiplication, at least 1.
    /// </summary>
    float FactorAt(int times)
    {
        float f = 1;
        for (int k = 1; k < times; k++) f *= RepeatMult;
        return MathF.Max(1, f);
    }

    /// <summary>
    /// The cost of its repetition <paramref name="times"/> (0 the first; <b>Verified</b>, <c>FUN_1408320a0</c>): each count x the factor, rounded
    /// down, and at least count + times; counts of 0 are paid nothing.
    /// </summary>
    public IEnumerable<(string Item, int Count)> CostAt(int times)
    {
        float f = FactorAt(times);
        foreach (var (item, count) in Cost)
            if (Math.Max((int)(count * f), count + times) is var n and > 0) yield return (item, n);
    }

    /// <summary>
    /// The <c>time</c> of its repetition <paramref name="times"/>: x the repetition's factor, rounded down (<b>Verified</b>, <c>FUN_1408320a0</c>).
    /// </summary>
    public int TimeAt(int times) => (int)(Time * FactorAt(times));

    /// <summary>
    /// The research hours of repetition <paramref name="times"/> (<b>Observed</b>, <c>FUN_14082ed10</c>): <c>time x research rate x level
    /// increase^(level - 1) / option</c> (the repetition's <see cref="LevelAt"/>), the power by repeated multiplication (as the exe).
    /// </summary>
    public float Hours(GameConstants constants, float option = 1, int times = 0)
    {
        float hours = TimeAt(times) * constants.ResearchRate / option;
        for (int i = 1, level = LevelAt(times); i < level; i++) hours *= constants.ResearchLevelIncreaseRate;
        return hours;
    }

    /// <summary>
    /// Reads a RESEARCH record (<c>cost</c> entries with a negative count are left out).
    /// </summary>
    public static ResearchData From(GameRecord r) => new()
    {
        Id = r.StringId,
        Name = r.Name,
        Category = r.GetString("category"),
        Description = r.GetString("description"),
        Level = r.GetInt("level"),
        Time = r.GetInt("time"),
        IsLevelUpgrade = r.GetBool("is level upgrade"),
        BlueprintOnly = r.GetBool("blueprint only"),
        Requirements = [.. r.GetReferences("requirements").Select(l => l.TargetStringId)],
        Cost = [.. r.GetReferences("cost").Where(l => l.Values.Value0 >= 0).Select(l => (l.TargetStringId, l.Values.Value0))],
        EnableBuildings = [.. r.GetReferences("enable buildings").Select(l => l.TargetStringId)],
        Repeats = Math.Max(0, r.GetInt("repeats")),
        RepeatMult = r.GetFloat("repeat mult", 1),
        ImproveBuildings = [.. r.GetReferences("improve buildings").Select(l => l.TargetStringId)],
        ProductionMult = r.GetFloat("production mult", 1),
        PowerIncrease = r.GetInt("power increase"),
        CapacityIncrease = r.GetInt("power capacity increase"),
        EnableCrafts = [.. EnableFields.SelectMany(e => r.GetReferences(e.Field).Select(l => (e.Kind, l.TargetStringId, l.Values.Value0)))],
        EnableFloors = [.. UnlockFields.SelectMany(f => r.GetReferences(f).Select(l => (l.Values.Value0, l.Values.Value1)))],
        Unlocks = [.. UnlockFields.SelectMany(f => r.GetReferences(f).Select(l => (f, l.TargetStringId)))],
    };

    /// <summary>
    /// Every RESEARCH record, in the database's order.
    /// </summary>
    public static List<ResearchData> LoadAll(GameDatabase db) => [.. db.OfType(FcsRecordType.Research).Select(From)];
}