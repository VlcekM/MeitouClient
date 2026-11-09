using Meitou.Data.Gameplay.Combat;

namespace Meitou.Simulation.Combat;

/// <summary>
/// One worn piece with its defences computed (docs/game/combat.md "Armour", loader <c>FUN_140898e90</c>, <b>Observed</b>). <see cref="Data"/> keeps
/// the record for the coverage test.
/// </summary>
public readonly record struct ArmourPiece(ArmourData Data, float Quality, float CutDefence, float BluntDefence, float PierceDefence,
    float CutIntoStun, float Stealth = 1, float Athletics = 1, float CombatSpeed = 1)
{
    /// <summary>
    /// The quality fractions of the armour grades 0 to 5 (PROTOTYPE .. MASTER): 5, 20, 40, 60, 80, 95 per cent (docs/characters.md, <b>Verified</b>);
    /// any other grade 20.
    /// </summary>
    public static float QualityOfGrade(int grade) =>
        grade switch { 0 => 0.05f, 1 => 0.20f, 2 => 0.40f, 3 => 0.60f, 4 => 0.80f, 5 => 0.95f, _ => 0.20f };

    /// <summary>
    /// The class table's cut and blunt factors.
    /// </summary>
    public static (float Cut, float Blunt) ClassFactors(ArmourClass c) => c switch
    {
        ArmourClass.Cloth => (0.4f, 0.1f),
        ArmourClass.Light => (0.8f, 0.7f),
        ArmourClass.Medium => (0.9f, 0.9f),
        _ => (1f, 1f),
    };

    /// <summary>
    /// The material table's (cut, blunt, flat pierce) at the lowest and at the highest grade.
    /// </summary>
    public static ((float Cut, float Blunt, float Pierce) Low, (float Cut, float Blunt, float Pierce) High) MaterialRange(ArmourType m) => m switch
    {
        ArmourType.Cloth => ((0f, 0f, 0f), (0.1f, 0.3f, 0f)),
        ArmourType.Leather => ((0f, 0f, 0f), (0.55f, 0.4f, 35f)),
        ArmourType.Chain => ((0.05f, 0f, 0f), (0.65f, 0.3f, 60f)),
        _ => ((0.1f, 0f, 0f), (0.85f, 0.6f, 90f)),
    };

    /// <summary>
    /// The material and class tables' stealth factors (low, high) (docs/game/character-stats.md "Gear factors", <b>Verified</b>).
    /// </summary>
    public static (float Low, float High) MaterialStealth(ArmourType m) => m switch
    {
        ArmourType.Chain => (0.3f, 0.7f),
        ArmourType.MetalPlate => (0.1f, 0.8f),
        _ => (1, 1),
    };

    /// <summary>
    /// The class table's stealth factors (low, high): medium 0.9 both, heavy 0 and 0.4, any other 1 (docs/game/character-stats.md "Gear
    /// factors", <b>Verified</b>).
    /// </summary>
    public static (float Low, float High) ClassStealth(ArmourClass c) => c switch
    {
        ArmourClass.Medium => (0.9f, 0.9f),
        ArmourClass.Heavy => (0, 0.4f),
        _ => (1, 1),
    };

    /// <summary>
    /// The piece's stealth factor (<c>FUN_140898e90</c>): lerp(t, material) x lerp(t, class) x <c>stealth mult</c>; 1 when <c>stealth mult</c> is
    /// negative; a hat (slot 3) that lists coverage and is <paramref name="graded"/> never goes below 1.
    /// </summary>
    public static float StealthOf(ArmourData a, float t, bool graded = true)
    {
        var (mLo, mHi) = MaterialStealth(a.Material);
        var (cLo, cHi) = ClassStealth(a.Class);
        float s = a.StealthMult >= 0 ? WeaponStats.Lerp(t, mLo, mHi) * WeaponStats.Lerp(t, cLo, cHi) * a.StealthMult : 1;
        return a.Slot == 3 && graded && a.Coverage.Count > 0 && s < 1 ? 1 : s;
    }

    /// <summary>
    /// The quality of an armour item of <paramref name="level"/> (<c>FUN_140898e90</c>, <b>Verified</b> disassembly): level x 0.01, not clamped; 1
    /// (full strength) when the level is 0 or less or the record lists no coverage, the case that also skips the slot rules (<see cref="Graded"/>).
    /// </summary>
    public static float QualityOf(ArmourData a, float level) => Graded(a, level) ? level * 0.01f : 1f;

    /// <summary>
    /// Whether the piece takes the slot rules (the shirt factors, the hat's stealth floor): its level is above 0 and it lists coverage
    /// (<c>FUN_140898e90</c>).
    /// </summary>
    public static bool Graded(ArmourData a, float level) => level > 0 && a.Coverage.Count > 0;

    /// <summary>
    /// The piece's run speed factor (ARMOUR <c>+0x248</c>, <c>FUN_140898e90</c>, <b>Verified</b> disassembly): lerp(t, the class table's athletics
    /// column: medium 0.85 to 1, heavy 0.8 to 0.95, else 1) x <c>athletics mult</c>; 1 for slots 3, 5 and 8.
    /// </summary>
    public static float AthleticsOf(ArmourData a, float t) => a.Slot is 3 or 5 or 8 ? 1
        : a.AthleticsMult * a.Class switch
        {
            ArmourClass.Medium => WeaponStats.Lerp(t, 0.85f, 1f),
            ArmourClass.Heavy => WeaponStats.Lerp(t, 0.8f, 0.95f),
            _ => 1f,
        };

    /// <summary>
    /// The piece's combat speed factor (ARMOUR <c>+0x258</c>, <c>FUN_140898e90</c>; the product over the worn pieces is CharStats <c>+0x1c</c>,
    /// <c>FUN_140887030</c>): the class table's combat speed column (heavy lerp(t, 0.9, 1), else 1; the column is <b>Observed</b> by elimination) x
    /// <c>combat speed mult</c>; 1 for a hat (slot 3), whatever its mult.
    /// </summary>
    public static float CombatSpeedOf(ArmourData a, float t) =>
        a.Slot == 3 ? 1 : a.CombatSpeedMult * (a.Class == ArmourClass.Heavy ? WeaponStats.Lerp(t, 0.9f, 1f) : 1f);

    /// <summary>
    /// Builds the piece. With t = quality + level bonus x 0.01: cut defence = clamp(lerp(t, low.cut, high.cut) x class cut + lerp(t, 0, cut def
    /// bonus), 0, 0.9); blunt defence likewise (cap 0.8); flat pierce defence = lerp(t, low.pierce, high.pierce) x pierce def mult; cut-into-stun =
    /// clamp(field, 0, 1). A <paramref name="graded"/> shirt (slot 8: level above 0 and coverage listed, <see cref="Graded"/>) has its cut and blunt
    /// defence x0.8 and its flat pierce x0.5.
    /// </summary>
    public static ArmourPiece Create(ArmourData a, float quality, bool graded = true)
    {
        float t = quality + a.LevelBonus * 0.01f;
        var (low, high) = MaterialRange(a.Material);
        var (classCut, classBlunt) = ClassFactors(a.Class);
        float cut = Math.Clamp(WeaponStats.Lerp(t, low.Cut, high.Cut) * classCut + WeaponStats.Lerp(t, 0, a.CutDefBonus), 0, 0.9f);
        float blunt = Math.Clamp(WeaponStats.Lerp(t, low.Blunt, high.Blunt) * classBlunt + WeaponStats.Lerp(t, 0, a.BluntDefBonus), 0, 0.8f);
        float pierce = WeaponStats.Lerp(t, low.Pierce, high.Pierce) * a.PierceDefMult;
        if (a.Slot == 8 && graded && a.Coverage.Count > 0)
        {
            cut *= 0.8f;
            blunt *= 0.8f;
            pierce *= 0.5f;
        }
        return new ArmourPiece(a, quality, cut, blunt, pierce, Math.Clamp(a.CutIntoStun, 0, 1),
            StealthOf(a, t, graded), AthleticsOf(a, t), CombatSpeedOf(a, t));
    }
}

/// <summary>
/// The combined effect of the pieces that covered a blow.
/// </summary>
/// <param name="CutReduction">Fraction of the cut that armour removes.</param>
/// <param name="BluntReduction">Fraction of the blunt that armour removes.</param>
/// <param name="PierceFlat">Flat pierce damage armour absorbs.</param>
/// <param name="StunFraction">Fraction of the cut that turns into stun.</param>
public readonly record struct ArmourEffect(float CutReduction, float BluntReduction, float PierceFlat, float StunFraction)
{
    /// <summary>
    /// No armour: nothing removed, no stun.
    /// </summary>
    public static ArmourEffect None => default;
}

/// <summary>
/// Armour stacking and the coverage test (docs/game/combat.md "The hit pipeline" steps 1 and 2).
/// </summary>
public static class ArmourStack
{
    /// <summary>
    /// Whether a piece covers a blow on a part: the part's entry in <c>part coverage</c> is a percentage, and a roll above it (a uniform number in
    /// [0, 100)) means the piece does not cover (<c>14065d620</c>); a part the piece does not list is never covered.
    /// </summary>
    public static bool Covers(ArmourData piece, string partStringId, float roll01)
    {
        foreach (var c in piece.Coverage)
            if (c.PartStringId == partStringId) return roll01 * 100 <= c.Percent;
        return false;
    }

    /// <summary>
    /// Stacks the covering pieces in order: each raises cutRed to cutRed + cutDef (1 - cutRed), bluntRed likewise, adds its flat pierce defence and
    /// adds cutDef x cutIntoStun x (1 - the cutRed before it) to the stun fraction (so the order of the list matters, and is the order of the
    /// character's gear). With a non-zero penetration both reductions are multiplied by (1 - pen) and then capped at 0.9, the cap applying only in
    /// that case; a negative penetration (a katana's -0.3) therefore raises the reduction.
    /// </summary>
    public static ArmourEffect Stack(ReadOnlySpan<ArmourPiece> pieces, ReadOnlySpan<bool> covering, float armourPenetration)
    {
        float cutRed = 0, bluntRed = 0, pierce = 0, stun = 0;
        for (int i = 0; i < pieces.Length; i++)
        {
            if (!covering[i]) continue;
            var p = pieces[i];
            stun += p.CutDefence * p.CutIntoStun * (1 - cutRed);
            cutRed += p.CutDefence * (1 - cutRed);
            bluntRed += p.BluntDefence * (1 - bluntRed);
            pierce += p.PierceDefence;
        }
        if (armourPenetration != 0)
        {
            cutRed = MathF.Min(cutRed * (1 - armourPenetration), 0.9f);
            bluntRed = MathF.Min(bluntRed * (1 - armourPenetration), 0.9f);
        }
        return new ArmourEffect(cutRed, bluntRed, pierce, stun);
    }
}