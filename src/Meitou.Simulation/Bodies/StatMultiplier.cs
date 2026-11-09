using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// Which effects <see cref="StatMultiplier.Compute"/> applies (the flags of <c>FUN_1406457e0</c>).
/// </summary>
[Flags]
public enum StatUse
{
    /// <summary>
    /// No effect.
    /// </summary>
    None = 0,
    /// <summary>
    /// The hunger term H.
    /// </summary>
    Hunger = 1,
    /// <summary>
    /// The limb term L.
    /// </summary>
    Limbs = 2,
    /// <summary>
    /// The light factor: working, looking and shooting in the dark (the rows' floor p0, from the character's light level).
    /// </summary>
    Light = 4,
    /// <summary>
    /// The fitted limbs' multipliers and the 0.66 for a disabled arm.
    /// </summary>
    DamageState = 8,
    /// <summary>
    /// The dust factor on perception and friendly fire (<c>FUN_1406439f0</c>, <see cref="MedicalState.DustStatFactor"/>).
    /// </summary>
    Stun = 16,
    /// <summary>
    /// The worn gear's bonus factor of the stat's row (<see cref="StatFactors"/>).
    /// </summary>
    LimbItems = 32,
    /// <summary>
    /// Every effect.
    /// </summary>
    All = Hunger | Limbs | Light | DamageState | Stun | LimbItems,
}

/// <summary>
/// The extra factors some rows multiply in (docs/game/character-stats.md "Gear factors"): the <c>CharStats</c> values the worn-gear refresh
/// <c>FUN_140887030</c> writes, the products over the worn armour of <c>asassination mult</c> (+0x40, assassination), <c>dexterity mult</c> (+0x44,
/// dexterity; 1.1 with no armour and no backpack), <c>damage output mult</c> (+0x48, the five melee weapon skills) and <c>ranged skill mult</c>
/// (+0x50, crossbows and friendly fire); and the dust factor of <c>FUN_1406439f0</c>, lerp(dust, 1, 0.5) (<see cref="MedicalState.DustStatFactor"/>,
/// the default; docs/formats/weather.md "Effects on characters"). All 1 means none.
/// </summary>
public readonly record struct StatFactors(float Assassin, float Dexterity, float Weapons, float Ranged, float Stun)
{
    /// <summary>
    /// No factor: all 1.
    /// </summary>
    public static StatFactors None { get; } = new(1, 1, 1, 1, 1);
}

/// <summary>
/// The aggregates of the body parts that the stat multiplier reads (<c>FUN_140644e60</c>).
/// </summary>
/// <param name="Arm">A: the health fraction of the best arm (1 for an owner exempt from limb effects).</param>
/// <param name="Head">Hd: the head's health fraction (1 when the race has no head part, an engine choice).</param>
/// <param name="Torso">T: the lowest health fraction among the torso parts.</param>
public readonly record struct BodyAggregates(float Arm, float Head, float Torso, bool LeftArmDisabled, bool RightArmDisabled)
{
    /// <summary>
    /// Whether both arms are disabled.
    /// </summary>
    public bool BothArmsDisabled => LeftArmDisabled && RightArmDisabled;
    /// <summary>
    /// Whether either arm is disabled.
    /// </summary>
    public bool EitherArmDisabled => LeftArmDisabled || RightArmDisabled;
}

/// <summary>
/// The multiplier in [0, 1] by which a stat is scaled whenever the game uses the skill (docs/game/character-stats.md "Effective stats: injuries and
/// hunger", <c>FUN_1406457e0</c>). For every row: H = lerp(h0, 1, clamp(hunger - 1, 0, 1)); L = lerp(l0, 1, limb); m = H x L; with light m x=
/// lerp(p0, 1, clamp(2.5 P, 0, 1)), P the character's light level (Character <c>+0xd8</c>, docs/game/perception.md "Sight"), so a light of 0.4 or
/// more costs nothing; result = lerp(f0, 1, m) clamped, times the limb-item factor with <see cref="StatUse.DamageState"/> and the row's bonus factor
/// with <see cref="StatUse.LimbItems"/>. The rows are the doc's table, <b>Observed</b> (hand-decoded from a switch with fall-through cases).
/// </summary>
public static class StatMultiplier
{
    /// <summary>
    /// Which body aggregates a row's limb term reads.
    /// </summary>
    enum Limb
    {
        /// <summary>
        /// None: the limb term is 1.
        /// </summary>
        One,
        /// <summary>
        /// The best arm.
        /// </summary>
        Arm,
        /// <summary>
        /// The best arm times the worst torso part.
        /// </summary>
        ArmTorso,
        /// <summary>
        /// The head.
        /// </summary>
        Head,
        /// <summary>
        /// The best arm times the head.
        /// </summary>
        ArmHead
    }

    /// <summary>
    /// Which gear factor (<see cref="StatFactors"/>) a row multiplies in, and whether the dust factor applies.
    /// </summary>
    enum Bonus
    {
        /// <summary>
        /// No factor.
        /// </summary>
        None,
        /// <summary>
        /// Assassination's armour factor.
        /// </summary>
        Assassin,
        /// <summary>
        /// Dexterity's armour factor.
        /// </summary>
        Dexterity,
        /// <summary>
        /// The melee weapon skills' damage output factor.
        /// </summary>
        Weapons,
        /// <summary>
        /// The ranged skill factor (crossbows).
        /// </summary>
        Ranged,
        /// <summary>
        /// The ranged skill factor and the dust factor.
        /// </summary>
        RangedStun,
        /// <summary>
        /// The dust factor only.
        /// </summary>
        StunOnly
    }

    /// <summary>
    /// A row of the table. <paramref name="LightFloor"/> p0 is 1 for rows without a light term.
    /// </summary>
    readonly record struct Row(Limb Limb, float H0, float L0, float F0, float LightFloor, Bonus Bonus,
        bool BothArmsPenalty = false, bool OneUnlessBothArmsDisabled = false, bool Scaled = true);

    /// <summary>
    /// The row of a stat (the doc's table); null for a stat without one, whose multiplier is 1.
    /// </summary>
    static Row? RowOf(StatsEnumerated stat) => stat switch
    {
        StatsEnumerated.Strength => new Row(Limb.ArmTorso, 0, 0.75f, 0.1f, 1, Bonus.None),
        StatsEnumerated.MeleeAttack or StatsEnumerated.MartialArts => new Row(Limb.ArmTorso, 0.75f, 0, 0.7f, 1, Bonus.None),
        StatsEnumerated.Labouring or StatsEnumerated.Engineering or StatsEnumerated.SmithingWeapon or StatsEnumerated.SmithingArmour
            or StatsEnumerated.Farming or StatsEnumerated.SmithingBow =>
                new Row(Limb.ArmTorso, 0.5f, 0, 0.25f, 0.25f, Bonus.None, BothArmsPenalty: true),
        StatsEnumerated.Science or StatsEnumerated.Robotics or StatsEnumerated.Cooking => new Row(Limb.Head, 0.6f, 0.4f, 0.25f, 0.25f, Bonus.None),
        StatsEnumerated.Medic or StatsEnumerated.Turrets or StatsEnumerated.HiveMedic or StatsEnumerated.Vet =>
            new Row(Limb.Head, 0.6f, 0.4f, 0.25f, 1, Bonus.None),
        StatsEnumerated.Thieving or StatsEnumerated.Lockpicking => new Row(Limb.Arm, 0.75f, 0.4f, 0.25f, 1, Bonus.None, BothArmsPenalty: true),
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
    /// The multiplier of <paramref name="stat"/> for a character's body. <paramref name="light"/> is the character's light level P: 0.4 or more means
    /// no penalty, 0 (dark) the full one. Stats without a row (toughness...) are 1.
    /// </summary>
    public static float Compute(MedicalState medical, StatsEnumerated stat, StatUse use, in StatFactors factors, in BodyAggregates agg,
        float light = 1)
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
            if (row.LightFloor < 1 && (use & StatUse.Light) != 0) m *= Lerp(row.LightFloor, 1, Math.Clamp(2.5f * light, 0, 1));
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

    /// <summary>
    /// The stats the 0.66 for a disabled arm applies to (<b>Observed</b>): 1, 2, 6, 9, 10, 11, 18, 19, 22, 23, 30, 35, 37; not stealth and athletics.
    /// </summary>
    public static bool ArmPenaltyApplies(StatsEnumerated stat) => stat is
        StatsEnumerated.Strength or StatsEnumerated.MeleeAttack or StatsEnumerated.Robotics or StatsEnumerated.Medic or StatsEnumerated.Thieving
        or StatsEnumerated.Turrets or StatsEnumerated.Dexterity or StatsEnumerated.MeleeDefence or StatsEnumerated.Assassination
        or StatsEnumerated.Swimming or StatsEnumerated.MartialArts or StatsEnumerated.Crossbows or StatsEnumerated.Lockpicking;

    /// <summary>
    /// The product of the fitted limbs' multipliers for a stat (<c>FUN_140644b90</c>): both arms' entries, and for stealth, athletics and swimming
    /// the legs' too; then x0.66 if either arm is disabled, for the arm-related stats only.
    /// </summary>
    public static float LimbItemFactor(MedicalState medical, StatsEnumerated stat, in BodyAggregates agg)
    {
        float f = medical.ArmItemFactor(stat);
        if (stat is StatsEnumerated.Stealth or StatsEnumerated.Athletics or StatsEnumerated.Swimming) f *= medical.LegItemFactor(stat);
        if (agg.EitherArmDisabled && ArmPenaltyApplies(stat)) f *= 0.66f;
        return f;
    }

    /// <summary>
    /// Linear interpolation from <paramref name="a"/> to <paramref name="b"/> by <paramref name="t"/>.
    /// </summary>
    static float Lerp(float a, float b, float t) => a + (b - a) * t;
}