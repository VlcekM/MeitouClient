using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Data.Gameplay.Combat;

/// <summary>
/// A GUN_DATA record: a turret's gun (docs/game/combat.md "Turrets"). Read as the shared gun loader <c>FUN_14043baa0</c> reads it: no damage fields
/// (a turret's damage is its building's, <see cref="TurretGun"/>) and no ammunition (a turret never runs dry). fcs.def defaults where a record leaves
/// a field out.
/// </summary>
public sealed record GunData
{
    public required string StringId { get; init; }
    public string Name { get; init; } = "";
    public float AccuracyDeviationAtZeroSkill { get; init; } = 4;
    public int AccuracyPerfectSkill { get; init; } = 70;

    /// <summary>
    /// <c>aim speed</c>: the turning parts' speed in radians per second before the building's factors.
    /// </summary>
    public float AimSpeed { get; init; } = 1;

    public int NumShots { get; init; } = 1;
    public int Range { get; init; } = 1000;
    public float ReloadTimeMin { get; init; } = 1;
    public float ReloadTimeMax { get; init; } = 5;

    /// <summary>
    /// <c>shot speed</c>; the loader makes 0 or less 1.
    /// </summary>
    public float ShotSpeed { get; init; } = 1;

    /// <summary>
    /// <c>barrel pos Y</c>, <c>barrel pos Z</c>: the muzzle in the barrel part's frame (x 0).
    /// </summary>
    public float BarrelY { get; init; }
    public float BarrelZ { get; init; } = 5;

    /// <summary>
    /// <c>live ammo</c>: the mesh of the harpoon in flight.
    /// </summary>
    public string LiveAmmoMesh { get; init; } = "";

    public static GunData From(GameRecord r)
    {
        float speed = RecordReading.Float(r, "shot speed", 1);
        return new()
        {
            StringId = r.StringId,
            Name = r.Name,
            AccuracyDeviationAtZeroSkill = RecordReading.Float(r, "accuracy deviation at 0 skill", 4),
            AccuracyPerfectSkill = RecordReading.Int(r, "accuracy perfect skill", 70),
            AimSpeed = RecordReading.Float(r, "aim speed", 1),
            NumShots = RecordReading.Int(r, "num shots", 1),
            Range = RecordReading.Int(r, "range", 1000),
            ReloadTimeMin = RecordReading.Float(r, "reload time min", 1),
            ReloadTimeMax = RecordReading.Float(r, "reload time max", 5),
            ShotSpeed = speed > 0 ? speed : 1,
            BarrelY = RecordReading.Float(r, "barrel pos Y", 0),
            BarrelZ = RecordReading.Float(r, "barrel pos Z", 5),
            LiveAmmoMesh = r.GetPath("live ammo"),
        };
    }
}

/// <summary>
/// A CROSSBOW record (docs/game/combat.md "Ranged and turrets"). The level-0 and level-1 values are lerped by the item's quality (<b>Observed</b>,
/// <c>FUN_14043c180</c>); <see cref="At(float, CombatConstants, float)"/> does that the way the gun loader does. <c>num shots</c>, <c>accuracy
/// perfect skill</c>, <c>ammo</c>, <c>reload anim</c> and <c>live ammo</c> have no level-1 variant (the shared GUN_DATA loader <c>FUN_14043baa0</c>
/// reads them).
/// </summary>
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

    /// <summary>
    /// <c>bleed mult</c> and the target-kind multipliers: shown on the tooltip, but no use of them in a bolt's damage was found (<b>Observed</b>);
    /// the engine does not apply them.
    /// </summary>
    public float BleedMult { get; init; } = 1;
    public float HumanDamageMult { get; init; } = 1;
    public float AnimalDamageMult { get; init; } = 1;
    public float RobotDamageMult { get; init; } = 1;

    /// <summary>
    /// <c>num shots</c>: the magazine (1 for every base-game crossbow).
    /// </summary>
    public int NumShots { get; init; } = 1;

    /// <summary>
    /// <c>accuracy perfect skill</c>: shown as "Required skill" on the tooltip; no use in the firing maths was found (<b>Observed</b>).
    /// </summary>
    public int AccuracyPerfectSkill { get; init; }

    /// <summary>
    /// The string id of the <c>ammo</c> ITEM; empty means infinite ammunition (fcs.def: "if nothing here then magic infinite ammo").
    /// </summary>
    public string AmmoId { get; init; } = "";

    /// <summary>
    /// The string id of the <c>reload anim</c> ANIMATION record; empty when the record names none.
    /// </summary>
    public string ReloadAnimId { get; init; } = "";

    /// <summary>
    /// <c>live ammo</c>: the mesh of the bolt in flight.
    /// </summary>
    public string LiveAmmoMesh { get; init; } = "";

    /// <summary>
    /// The stats at quality fraction <paramref name="q"/> (0 to 1) with the pierce damages times the global damage multiplier and truncated to
    /// integers, without the CONSTANTS <c>bow damage 1/99</c> scaling (the plain lerp; the gun itself uses
    /// <see cref="At(float, CombatConstants, float)"/>).
    /// </summary>
    public CrossbowStats At(float q, float globalDamageMultiplier = 1) => Lerp(q, globalDamageMultiplier, 1, 1);

    /// <summary>
    /// The gun's numbers at quality fraction <paramref name="q"/> (<c>FUN_14043c180</c>, <b>Observed</b>, the float flow checked in the disassembly):
    /// every level-0/level-1 pair is lerped by q; the pierce ends are first scaled by <c>bow damage 1</c> (level 0) and <c>bow damage 99</c> (level
    /// 1), then lerped, multiplied by the global damage multiplier and truncated: pierceMin = trunc(lerp(q, bow1 x min0, bow99 x min1) x G). The shot
    /// speed is the lerp of <c>shot speed</c> and <c>shot speed 1</c> with no clamp (<b>Verified</b>: the constructor overwrites the loader's clamped
    /// value with it).
    /// </summary>
    public CrossbowStats At(float q, CombatConstants constants, float globalDamageMultiplier = 1) =>
        Lerp(q, globalDamageMultiplier, constants.BowDamage1, constants.BowDamage99);

    CrossbowStats Lerp(float q, float g, float bow1, float bow99)
    {
        static float L(float a, float b, float t) => a + (b - a) * t;

        float speed = L(ShotSpeed0, ShotSpeed1, q);   // FUN_14043c180 stores this lerp over the loader's clamped speed, unclamped
        return new CrossbowStats(
            (int)(L(bow1 * PierceDamageMin0, bow99 * PierceDamageMin1, q) * g),
            (int)(L(bow1 * PierceDamageMax0, bow99 * PierceDamageMax1, q) * g),
            L(Range0, Range1, q),
            L(ReloadTimeMin0, ReloadTimeMin1, q),
            L(ReloadTimeMax0, ReloadTimeMax1, q),
            L(AccuracyDeviation0, AccuracyDeviation1, q),
            speed);
    }

    static string FirstReference(GameRecord r, string list)
    {
        var refs = r.GetReferences(list);
        return refs.Count > 0 ? refs[0].TargetStringId : "";
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
        NumShots = Math.Max(RecordReading.Int(r, "num shots", 1), 1),
        AccuracyPerfectSkill = RecordReading.Int(r, "accuracy perfect skill", 0),
        AmmoId = FirstReference(r, "ammo"),
        ReloadAnimId = FirstReference(r, "reload anim"),
        LiveAmmoMesh = r.GetPath("live ammo"),
    };
}

/// <summary>
/// A crossbow's numbers at one quality: pierce damage ends (ints), range (units), reload time ends (seconds), accuracy deviation at skill 0
/// (degrees), shot speed (units per second).
/// </summary>
public readonly record struct CrossbowStats(
    int PierceMin, int PierceMax, float Range, float ReloadMin, float ReloadMax, float AccuracyDeviation, float ShotSpeed);
