using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>Which effects <see cref="StatMultiplier.Compute"/> applies (the flags of <c>FUN_1406457e0</c>).</summary>
[Flags]
public enum StatUse
{
    None = 0,
    Hunger = 1,
    Limbs = 2,
    Pain = 4,
    /// <summary>The fitted limbs' multipliers and the 0.66 for a disabled arm.</summary>
    DamageState = 8,
    /// <summary>The stun (dizziness) factor on the perception-like stats.</summary>
    Stun = 16,
    /// <summary>The stat bonus factor of the stat's row.</summary>
    LimbItems = 32,
    All = Hunger | Limbs | Pain | DamageState | Stun | LimbItems,
}

/// <summary>
/// The extra factors some rows multiply in: the <c>CharStats</c> bonus values at +0x40 (assassination), +0x44 (dexterity), +0x48 (melee weapon skills), +0x50
/// (crossbows and friendly fire) and the stun factor of <c>FUN_1406439f0</c>, lerp(1, 0.5, v). Their sources are <b>Unknown</b>; all 1 means none.
/// </summary>
public readonly record struct StatFactors(float Assassin, float Dexterity, float Weapons, float Ranged, float Stun)
{
    public static StatFactors None { get; } = new(1, 1, 1, 1, 1);
}

/// <summary>The aggregates of the body parts that the stat multiplier reads (<c>FUN_140644e60</c>).</summary>
/// <param name="Arm">A: the health fraction of the best arm (1 for an owner exempt from limb effects).</param>
/// <param name="Head">Hd: the head's health fraction (1 when the race has no head part, an engine choice).</param>
/// <param name="Torso">T: the lowest health fraction among the torso parts.</param>
public readonly record struct BodyAggregates(float Arm, float Head, float Torso, bool LeftArmDisabled, bool RightArmDisabled)
{
    public bool BothArmsDisabled => LeftArmDisabled && RightArmDisabled;
    public bool EitherArmDisabled => LeftArmDisabled || RightArmDisabled;
}

/// <summary>
/// The multiplier in [0, 1] by which a stat is scaled whenever the game uses the skill (docs/game/character-stats.md "Effective stats: injuries and hunger",
/// <c>FUN_1406457e0</c>). For every row: H = lerp(h0, 1, clamp(hunger - 1, 0, 1)); L = lerp(l0, 1, limb); m = H x L; with pain m x= lerp(p0, 1, clamp(2.5 P, 0, 1));
/// result = lerp(f0, 1, m) clamped, times the limb-item factor with <see cref="StatUse.DamageState"/> and the row's bonus factor with <see cref="StatUse.LimbItems"/>.
/// The rows are the doc's table, <b>Observed</b> (hand-decoded from a switch with fall-through cases).
/// </summary>
public static class StatMultiplier
{
    enum Limb { One, Arm, ArmTorso, Head, ArmHead }

    enum Bonus { None, Assassin, Dexterity, Weapons, Ranged, RangedStun, StunOnly }

    /// <summary>A row of the table. <paramref name="PainFloor"/> p0 is 1 for rows without a pain term.</summary>
    readonly record struct Row(Limb Limb, float H0, float L0, float F0, float PainFloor, Bonus Bonus, bool BothArmsPenalty = false, bool OneUnlessBothArmsDisabled = false, bool Scaled = true);

    static Row? RowOf(StatsEnumerated stat) => stat switch
    {
        StatsEnumerated.Strength => new Row(Limb.ArmTorso, 0, 0.75f, 0.1f, 1, Bonus.None),
        StatsEnumerated.MeleeAttack or StatsEnumerated.MartialArts => new Row(Limb.ArmTorso, 0.75f, 0, 0.7f, 1, Bonus.None),
        StatsEnumerated.Labouring or StatsEnumerated.Engineering or StatsEnumerated.SmithingWeapon or StatsEnumerated.SmithingArmour
            or StatsEnumerated.Farming or StatsEnumerated.SmithingBow => new Row(Limb.ArmTorso, 0.5f, 0, 0.25f, 0.25f, Bonus.None, BothArmsPenalty: true),
        StatsEnumerated.Science or StatsEnumerated.Robotics or StatsEnumerated.Cooking => new Row(Limb.Head, 0.6f, 0.4f, 0.25f, 0.25f, Bonus.None),
        StatsEnumerated.Medic or StatsEnumerated.Turrets or StatsEnumerated.HiveMedic or StatsEnumerated.Vet => new Row(Limb.Head, 0.6f, 0.4f, 0.25f, 1, Bonus.None),
        StatsEnumerated.Thieving or StatsEnumerated.Lockpicking => new Row(Limb.Arm, 0.75f, 0.4f, 0.25f, 1, Bonus.None, BothArmsPenalty: true),
        StatsEnumerated.Stealth => new Row(Limb.Arm, 0.9f, 0.9f, 0.8f, 1, Bonus.None),
        StatsEnumerated.Athletics => new Row(Limb.One, 0.7f, 1, 0.5f, 1, Bonus.None),
        StatsEnumerated.Dexterity => new Row(Limb.ArmTorso, 0.7f, 0, 0.4f, 1, Bonus.Dexterity),
        StatsEnumerated.MeleeDefence => new Row(Limb.ArmHead, 0.75f, 0, 0.8f, 1, Bonus.None),
        StatsEnumerated.Assassination => new Row(Limb.One, 1, 0, 0.5f, 1, Bonus.Assassin, OneUnlessBothArmsDisabled: true),
        StatsEnumerated.Swimming => new Row(Limb.ArmTorso, 0.5f, 0, 0.01f, 1, Bonus.None),
        StatsEnumerated.Perception => new Row(Limb.Head, 0.6f, 0.5f, 0.33f, 0.25f, Bonus.StunOnly),
        StatsEnumerated.Crossbows => new Row(Limb.Head, 0.6f, 0.4f, 0.25f, 0.5f, Bonus.Ranged),
        StatsEnumerated.FriendlyFire => new Row(Limb.Head, 0.6f, 0.5f, 0.33f, 0.25f, Bonus.RangedStun),
        StatsEnumerated.Katanas or StatsEnumerated.Sabres or StatsEnumerated.Hackers or StatsEnumerated.HeavyWeapons or StatsEnumerated.Blunt
            => new Row(Limb.One, 1, 1, 0, 1, Bonus.Weapons, Scaled: false),
        _ => null,
    };

    /// <summary>
    /// The multiplier of <paramref name="stat"/> for a character's body. <paramref name="pain"/> is the value P of the formula as it reads: 1 or at least 0.4 means no
    /// pain penalty and 0 the full one (its source, Character <c>+0xd8</c>, is <b>Unknown</b>). Stats without a row (toughness...) are 1.
    /// </summary>
    public static float Compute(MedicalState medical, StatsEnumerated stat, StatUse use, in StatFactors factors, in BodyAggregates agg, float pain = 1)
    {
        if (RowOf(stat) is not { } row) return 1;
        float result = 1;
        if (row.Scaled)
        {
            float limb = row.Limb switch
            {
                Limb.Arm => agg.Arm,
                Limb.ArmTorso => agg.Arm * agg.Torso,
                Limb.Head => agg.Head,
                Limb.ArmHead => agg.Arm * agg.Head,
                _ => 1,
            };
            if (row.BothArmsPenalty && agg.BothArmsDisabled) limb *= 0.01f;
            if (row.OneUnlessBothArmsDisabled) limb = agg.BothArmsDisabled ? agg.Arm * 0.01f : 1;
            float h = (use & StatUse.Hunger) != 0 ? Lerp(row.H0, 1, Math.Clamp(medical.Hunger - 1, 0, 1)) : 1;
            float l = (use & StatUse.Limbs) != 0 ? Lerp(row.L0, 1, limb) : 1;
            float m = h * l;
            if (row.PainFloor < 1 && (use & StatUse.Pain) != 0) m *= Lerp(row.PainFloor, 1, Math.Clamp(2.5f * pain, 0, 1));
            if ((use & StatUse.Stun) != 0 && row.Bonus is Bonus.StunOnly or Bonus.RangedStun) m *= factors.Stun;
            result = Math.Clamp(Lerp(row.F0, 1, m), 0, 1);
            if ((use & StatUse.DamageState) != 0) result *= LimbItemFactor(medical, stat, agg);
        }
        if ((use & StatUse.LimbItems) != 0)
            result *= row.Bonus switch
            {
                Bonus.Assassin => factors.Assassin,
                Bonus.Dexterity => factors.Dexterity,
                Bonus.Weapons => factors.Weapons,
                Bonus.Ranged or Bonus.RangedStun => factors.Ranged,
                _ => 1,
            };
        return result;
    }

    /// <summary>The stats the 0.66 for a disabled arm applies to (<b>Observed</b>): 1, 2, 6, 9, 10, 11, 18, 19, 22, 23, 30, 35, 37; not stealth and athletics.</summary>
    public static bool ArmPenaltyApplies(StatsEnumerated stat) => stat is
        StatsEnumerated.Strength or StatsEnumerated.MeleeAttack or StatsEnumerated.Robotics or StatsEnumerated.Medic or StatsEnumerated.Thieving
        or StatsEnumerated.Turrets or StatsEnumerated.Dexterity or StatsEnumerated.MeleeDefence or StatsEnumerated.Assassination or StatsEnumerated.Swimming
        or StatsEnumerated.MartialArts or StatsEnumerated.Crossbows or StatsEnumerated.Lockpicking;

    /// <summary>
    /// The product of the fitted limbs' multipliers for a stat (<c>FUN_140644b90</c>): both arms' entries, and for stealth, athletics and swimming the legs' too; then x0.66 if
    /// either arm is disabled, for the arm-related stats only.
    /// </summary>
    public static float LimbItemFactor(MedicalState medical, StatsEnumerated stat, in BodyAggregates agg)
    {
        float f = medical.ArmItemFactor(stat);
        if (stat is StatsEnumerated.Stealth or StatsEnumerated.Athletics or StatsEnumerated.Swimming) f *= medical.LegItemFactor(stat);
        if (agg.EitherArmDisabled && ArmPenaltyApplies(stat)) f *= 0.66f;
        return f;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
