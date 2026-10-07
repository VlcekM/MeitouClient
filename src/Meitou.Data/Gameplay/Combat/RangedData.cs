using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Data.Gameplay.Combat;

/// <summary>
/// A GUN_DATA record (the turret and crossbow firing data), read only (docs/game/combat.md "Ranged and turrets": the maths that uses these fields is <b>Unknown</b> for turrets).
/// </summary>
public sealed record GunData
{
    public required string StringId { get; init; }
    public string Name { get; init; } = "";
    public float AccuracyDeviationAtZeroSkill { get; init; }
    public int AccuracyPerfectSkill { get; init; }
    public float AimSpeed { get; init; }
    public int NumShots { get; init; } = 1;
    public int Range { get; init; }
    public float ReloadTimeMin { get; init; }
    public float ReloadTimeMax { get; init; }
    public float ShotSpeed { get; init; }

    public static GunData From(GameRecord r) => new()
    {
        StringId = r.StringId,
        Name = r.Name,
        AccuracyDeviationAtZeroSkill = RecordReading.Float(r, "accuracy deviation at 0 skill", 0),
        AccuracyPerfectSkill = RecordReading.Int(r, "accuracy perfect skill", 0),
        AimSpeed = RecordReading.Float(r, "aim speed", 1),
        NumShots = RecordReading.Int(r, "num shots", 1),
        Range = RecordReading.Int(r, "range", 0),
        ReloadTimeMin = RecordReading.Float(r, "reload time min", 0),
        ReloadTimeMax = RecordReading.Float(r, "reload time max", 0),
        ShotSpeed = RecordReading.Float(r, "shot speed", 0),
    };
}

/// <summary>A CROSSBOW record, read only. The level-0 and level-1 values are lerped by the item's quality (<b>Observed</b>, <c>FUN_14043c180</c>); <see cref="At"/> does that.</summary>
public sealed record CrossbowData
{
    public required string StringId { get; init; }
    public string Name { get; init; } = "";
    public float PierceDamageMin0 { get; init; }
    public float PierceDamageMin1 { get; init; }
    public float PierceDamageMax0 { get; init; }
    public float PierceDamageMax1 { get; init; }
    public int Range0 { get; init; }
    public int Range1 { get; init; }
    public float ReloadTimeMin0 { get; init; }
    public float ReloadTimeMin1 { get; init; }
    public float ReloadTimeMax0 { get; init; }
    public float ReloadTimeMax1 { get; init; }
    public float AccuracyDeviation0 { get; init; }
    public float AccuracyDeviation1 { get; init; }
    public float ShotSpeed0 { get; init; }
    public float ShotSpeed1 { get; init; }
    public float BleedMult { get; init; } = 1;
    public float HumanDamageMult { get; init; } = 1;
    public float AnimalDamageMult { get; init; } = 1;
    public float RobotDamageMult { get; init; } = 1;

    /// <summary>The stats at quality fraction <paramref name="q"/> (0 to 1); the pierce damages times the global damage multiplier and truncated to integers, as the loader does.</summary>
    public CrossbowStats At(float q, float globalDamageMultiplier = 1)
    {
        static float L(float a, float b, float t) => a + (b - a) * t;
        return new CrossbowStats(
            (int)(L(PierceDamageMin0, PierceDamageMin1, q) * globalDamageMultiplier),
            (int)(L(PierceDamageMax0, PierceDamageMax1, q) * globalDamageMultiplier),
            L(Range0, Range1, q), L(ReloadTimeMin0, ReloadTimeMin1, q), L(ReloadTimeMax0, ReloadTimeMax1, q),
            L(AccuracyDeviation0, AccuracyDeviation1, q), L(ShotSpeed0, ShotSpeed1, q));
    }

    public static CrossbowData From(GameRecord r) => new()
    {
        StringId = r.StringId,
        Name = r.Name,
        PierceDamageMin0 = RecordReading.Float(r, "pierce damage min 0", 0),
        PierceDamageMin1 = RecordReading.Float(r, "pierce damage min 1", 0),
        PierceDamageMax0 = RecordReading.Float(r, "pierce damage max 0", 0),
        PierceDamageMax1 = RecordReading.Float(r, "pierce damage max 1", 0),
        Range0 = RecordReading.Int(r, "range", 0),
        Range1 = RecordReading.Int(r, "range 1", 0),
        ReloadTimeMin0 = RecordReading.Float(r, "reload time min", 0),
        ReloadTimeMin1 = RecordReading.Float(r, "reload time min 1", 0),
        ReloadTimeMax0 = RecordReading.Float(r, "reload time max", 0),
        ReloadTimeMax1 = RecordReading.Float(r, "reload time max 1", 0),
        AccuracyDeviation0 = RecordReading.Float(r, "accuracy deviation at 0 skill", 0),
        AccuracyDeviation1 = RecordReading.Float(r, "accuracy deviation at 0 skill 1", 0),
        ShotSpeed0 = RecordReading.Float(r, "shot speed", 0),
        ShotSpeed1 = RecordReading.Float(r, "shot speed 1", 0),
        BleedMult = RecordReading.Float(r, "bleed mult", 1),
        HumanDamageMult = RecordReading.Float(r, "human damage mult", 1),
        AnimalDamageMult = RecordReading.Float(r, "animal damage mult", 1),
        RobotDamageMult = RecordReading.Float(r, "robot damage mult", 1),
    };
}

/// <summary>A crossbow's numbers at one quality.</summary>
public readonly record struct CrossbowStats(int PierceMin, int PierceMax, float Range, float ReloadMin, float ReloadMax, float AccuracyDeviation, float ShotSpeed);
