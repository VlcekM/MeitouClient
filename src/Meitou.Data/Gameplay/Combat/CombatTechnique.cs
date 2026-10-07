using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Data.Gameplay.Combat;

/// <summary>
/// A COMBAT_TECHNIQUE record: one attack, block or dodge animation with its timings and which weapons it suits (docs/game/combat.md "Technique choice, block and dodge").
/// Field names <b>Verified</b> against the 44 base records. <b>Observed</b> in the data: <c>anim blocked frame</c>, <c>anim stop frame</c> and <c>acceptable end time</c> are fractions
/// of the animation when <c>num frames</c> is 1 (every sword, block, dodge and creature row) and frame numbers when it is larger (the martial-arts rows: blocked 18 and 31 of 57 frames);
/// the second value of a one-blow technique is a leftover default and is not used.
/// </summary>
public sealed record CombatTechnique
{
    public required string StringId { get; init; }
    public string Name { get; init; } = "";
    public string AnimName { get; init; } = "";
    public bool IsBlock { get; init; }
    public bool IsDodge { get; init; }
    public bool IsStumbleDodge { get; init; }
    public bool IsProne { get; init; }
    public bool LowStrike { get; init; }
    public bool GainsGround { get; init; }
    public bool Disabled { get; init; }
    public bool UseLeftArm { get; init; }
    public bool UseRightArm { get; init; }
    /// <summary>Weapon kinds the technique is valid for (the record's 1 handed, blunt, hackers, heavy weapons, katanas, polearm, sabre, unarmed flags).</summary>
    public WeaponKinds Kinds { get; init; }
    /// <summary><c>animal</c>: 0 for a person's technique, else the creature's weapon category (9 to 20).</summary>
    public int Animal { get; init; }
    /// <summary><c>num techniques</c>: the number of blows (1 or 2).</summary>
    public int Blows { get; init; } = 1;
    public int AttackDirection1 { get; init; }
    public int AttackDirection2 { get; init; }
    public int Power1 { get; init; } = 100;
    public int Power2 { get; init; } = 100;
    public int Limb1 { get; init; }
    public int Limb2 { get; init; }
    public int MaxSimultaneousHits { get; init; } = 1;
    public float Chance { get; init; } = 1;
    public float MinSkill { get; init; } = -100;
    public float MaxSkill { get; init; } = 999;
    public float MaxEncumbrance { get; init; } = 999;
    public float AttackDistance { get; init; }
    public float AttackDistanceMinVsStatic { get; init; }
    public float HesitatePoint { get; init; }
    public float AnimSpeedMult { get; init; } = 1;
    public float NumFrames { get; init; } = 1;
    public float BlockedFrame1 { get; init; } = 1;
    public float BlockedFrame2 { get; init; } = 1;
    public float StopFrame1 { get; init; } = 1;
    public float StopFrame2 { get; init; } = 1;
    public float AcceptableEndTime { get; init; } = 0.95f;

    /// <summary>The attack direction (CutDirection) of blow <paramref name="blow"/> (1 or 2).</summary>
    public int DirectionOf(int blow) => blow <= 1 ? AttackDirection1 : AttackDirection2;
    public int PowerOf(int blow) => blow <= 1 ? Power1 : Power2;

    /// <summary>The animation progress (0 to 1) at which blow <paramref name="blow"/> arrives (its <c>anim blocked frame</c>).</summary>
    public float StrikeProgress(int blow) => ToProgress(blow <= 1 ? BlockedFrame1 : BlockedFrame2);
    /// <summary>The progress at which the animation stops when blow <paramref name="blow"/> is stopped (<c>anim stop frame</c>).</summary>
    public float StopProgress(int blow) => ToProgress(blow <= 1 ? StopFrame1 : StopFrame2);
    /// <summary>The progress after which the next action may start (<c>acceptable end time</c>).</summary>
    public float EndProgress => ToProgress(AcceptableEndTime);

    float ToProgress(float frame) => Math.Clamp(NumFrames > 1 ? frame / NumFrames : frame, 0, 1);

    public static CombatTechnique From(GameRecord r)
    {
        WeaponKinds kinds = WeaponKinds.None;
        void Flag(string field, WeaponKinds k) { if (r.GetBool(field)) kinds |= k; }
        Flag("1 handed", WeaponKinds.OneHanded);
        Flag("blunt", WeaponKinds.Blunt);
        Flag("hackers", WeaponKinds.Hacker);
        Flag("heavy weapons", WeaponKinds.Heavy);
        Flag("katanas", WeaponKinds.Katana);
        Flag("polearm", WeaponKinds.Polearm);
        Flag("sabre", WeaponKinds.Sabre);
        Flag("unarmed", WeaponKinds.Unarmed);
        return new CombatTechnique
        {
            StringId = r.StringId,
            Name = r.Name,
            AnimName = r.GetString("anim name"),
            IsBlock = r.GetBool("is block"),
            IsDodge = r.GetBool("is dodge"),
            IsStumbleDodge = r.GetBool("is stumble dodge"),
            IsProne = r.GetBool("is prone"),
            LowStrike = r.GetBool("low strike"),
            GainsGround = r.GetBool("gains ground"),
            Disabled = r.GetBool("disabled"),
            UseLeftArm = r.GetBool("use L arm"),
            UseRightArm = r.GetBool("use R arm"),
            Kinds = kinds,
            Animal = RecordReading.Int(r, "animal", 0),
            Blows = Math.Clamp(RecordReading.Int(r, "num techniques", 1), 1, 2),
            AttackDirection1 = RecordReading.Int(r, "attack direction 1", 0),
            AttackDirection2 = RecordReading.Int(r, "attack direction 2", 0),
            Power1 = RecordReading.Int(r, "power 1", 100),
            Power2 = RecordReading.Int(r, "power 2", 100),
            Limb1 = RecordReading.Int(r, "limb 1", 4),
            Limb2 = RecordReading.Int(r, "limb 2", 4),
            MaxSimultaneousHits = RecordReading.Int(r, "max simultaneous hits", 1),
            Chance = RecordReading.Float(r, "chance", 1),
            MinSkill = RecordReading.Float(r, "min skill", -100),
            MaxSkill = RecordReading.Float(r, "max skill", 999),
            MaxEncumbrance = RecordReading.Float(r, "max encumbrance", 999),
            AttackDistance = RecordReading.Float(r, "attack distance", 0),
            AttackDistanceMinVsStatic = RecordReading.Float(r, "attack distance min vs static", 0),
            HesitatePoint = RecordReading.Float(r, "anim hesitate point", 0),
            AnimSpeedMult = RecordReading.Float(r, "anim speed mult", 1),
            NumFrames = RecordReading.Float(r, "num frames", 1),
            BlockedFrame1 = RecordReading.Float(r, "anim blocked frame 1", 1),
            BlockedFrame2 = RecordReading.Float(r, "anim blocked frame 2", 1),
            StopFrame1 = RecordReading.Float(r, "anim stop frame 1", 1),
            StopFrame2 = RecordReading.Float(r, "anim stop frame 2", 1),
            AcceptableEndTime = RecordReading.Float(r, "acceptable end time", 0.95f),
        };
    }
}
