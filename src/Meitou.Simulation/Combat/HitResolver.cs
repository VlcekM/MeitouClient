using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The middle of the hit pipeline (docs/game/combat.md "The hit pipeline", steps 3 and 4): a damage packet through the armour that covered it and the
/// target's toughness, ending as the <see cref="HitDamage"/> the medical system takes (step 5 is <see cref="MedicalState.ApplyHit"/>).
/// </summary>
public static class HitResolver
{
    /// <summary>
    /// The toughness resistance r = lerp(clamp(toughness x 0.01, 0, 1), min, max) with the stored <c>damage resistance min / max</c> (-0.65 / 0.65):
    /// r is -0.65 at toughness 0, so an untrained target takes 1.65 times the damage and a toughness-100 target 0.35 times.
    /// <paramref name="toughness"/> is the effective stat.
    /// </summary>
    public static float ToughnessResistance(float toughness, CombatConstants c) =>
        WeaponStats.Lerp(Math.Clamp(toughness * 0.01f, 0, 1), c.DamageResistanceMin, c.DamageResistanceMax);

    /// <summary>
    /// cut' = cut (1 - cutRed); blunt' = blunt (1 - bluntRed); pierce' = max(0, pierce - flat); stun = cut x stun fraction + the same fraction of the
    /// pierce the armour absorbed (the packet's own stun is discarded, <c>FUN_1406508d0</c> zeroes it before the armour loop); then cut, blunt,
    /// pierce and stun are each multiplied by (1 - r), cut and blunt floored at 0, and all four by the global damage multiplier of the difficulty
    /// options. The packet's bleed multiplier becomes the hit's scale.
    /// </summary>
    public static HitDamage Resolve(in DamagePacket packet, in ArmourEffect armour, float toughness, float globalDamageMultiplier, CombatConstants c)
    {
        float cut = packet.Cut * (1 - armour.CutReduction);
        float blunt = packet.Blunt * (1 - armour.BluntReduction);
        float pierce = MathF.Max(0, packet.Pierce - armour.PierceFlat);
        float stun = packet.Cut * armour.StunFraction + armour.StunFraction * (packet.Pierce - pierce);
        float resist = 1 - ToughnessResistance(toughness, c);
        cut = MathF.Max(0, cut * resist) * globalDamageMultiplier;
        blunt = MathF.Max(0, blunt * resist) * globalDamageMultiplier;
        pierce = pierce * resist * globalDamageMultiplier;
        stun = stun * resist * globalDamageMultiplier;

        return new HitDamage(cut, blunt, pierce, stun, packet.BleedMultiplier);
    }

    /// <summary>
    /// Lands a blow on a body: chooses the part (weighted by hit weight x hitmult; a low strike excludes arms and head; hit direction 6 uses the
    /// weight alone), tests each piece's coverage of that part with a roll from <paramref name="coverRoll"/> (called per piece, in order), resolves
    /// the damage and applies it to the part. Returns the part hit and the damage applied.
    /// </summary>
    public static (int Part, HitDamage Damage) Land(
        MedicalState medical,
        in MedicalContext ctx,
        in DamagePacket packet,
        ReadOnlySpan<ArmourPiece> armour,
        Func<int, float> coverRoll,
        float toughness,
        bool lowStrike,
        bool directionSix,
        CombatConstants c,
        List<MedicalEvent>? events) =>
        Land(medical, ctx, packet, armour, new FuncCover(coverRoll), toughness, lowStrike, directionSix, c, events);

    /// <summary>
    /// The same as the delegate overload, with the cover roll as a struct so that a hit allocates nothing.
    /// </summary>
    public static (int Part, HitDamage Damage) Land<TCover>(
        MedicalState medical,
        in MedicalContext ctx,
        in DamagePacket packet,
        ReadOnlySpan<ArmourPiece> armour,
        TCover coverRoll,
        float toughness,
        bool lowStrike,
        bool directionSix,
        CombatConstants c,
        List<MedicalEvent>? events)
        where TCover : struct, ICoverRoll
    {
        var (part, damage, _, _) = LandDetailed(medical, ctx, packet, armour, coverRoll, toughness, lowStrike, directionSix, c, events);
        return (part, damage);
    }

    /// <summary>
    /// What the armour alone stopped of a packet (before toughness and the difficulty multiplier, as the damage floaters show it).
    /// </summary>
    public static ArmourAbsorbed Absorbed(in DamagePacket packet, in ArmourEffect armour) =>
        new(packet.Cut * armour.CutReduction, packet.Blunt * armour.BluntReduction, MathF.Min(packet.Pierce, armour.PierceFlat));

    /// <summary>
    /// The debug "One-punch man mode": <paramref name="damage"/> cut damage on the part a blow would pick (the same roll as a normal hit, with the
    /// strike's height and direction), ignoring armour and toughness. A body the damage does not kill (a robot's limb, which cannot bleed) is killed
    /// outright. Returns what <see cref="LandDetailed{TCover}"/> returns, with nothing absorbed and no armour struck.
    /// </summary>
    public static (int Part, HitDamage Damage, ArmourAbsorbed Absorbed, ArmourType? Struck) LandLethal(MedicalState medical, in MedicalContext ctx,
        float damage, bool lowStrike, bool directionSix, List<MedicalEvent>? events)
    {
        int part = medical.ChoosePart(ctx, lowStrike, directionSix);
        if (part < 0) return (-1, default, default, null);

        var struck = StruckMaterial(medical.Parts[part], [], []);
        var hit = new HitDamage(damage, 0, 0);
        medical.ApplyHit(part, hit, ctx, events);
        if (!medical.Dead) medical.Kill(events);
        return (part, hit, default, struck);
    }

    /// <summary>
    /// <see cref="Land{TCover}"/> that also reports what the armour stopped and what the blow struck (<see cref="StruckMaterial"/>).
    /// </summary>
    public static (int Part, HitDamage Damage, ArmourAbsorbed Absorbed, ArmourType? Struck) LandDetailed<TCover>(
        MedicalState medical,
        in MedicalContext ctx,
        in DamagePacket packet,
        ReadOnlySpan<ArmourPiece> armour,
        TCover coverRoll,
        float toughness,
        bool lowStrike,
        bool directionSix,
        CombatConstants c,
        List<MedicalEvent>? events)
        where TCover : struct, ICoverRoll
    {
        int part = medical.ChoosePart(ctx, lowStrike, directionSix);
        if (part < 0) return (-1, default, default, null);

        string partId = medical.Parts[part].Template.StringId;
        Span<bool> covering = armour.Length <= 32 ? stackalloc bool[armour.Length] : new bool[armour.Length];
        for (int i = 0; i < armour.Length; i++) covering[i] = ArmourStack.Covers(armour[i].Data, partId, coverRoll.Roll(i));

        var struck = StruckMaterial(medical.Parts[part], armour, covering);
        var effect = ArmourStack.Stack(armour, covering, packet.ArmourPenetration);
        var damage = Resolve(packet, effect, toughness, ctx.Options.GlobalDamageMultiplier, c);
        medical.ApplyHit(part, damage, ctx, events);
        return (part, damage, Absorbed(packet, effect), struck);
    }

    /// <summary>
    /// What a blow struck (the medical hit <c>FUN_1406508d0</c>'s out value, <b>Verified</b> disassembly, docs/game/combat.md "Punch self-damage"):
    /// the <c>material type</c> of the last covering piece in worn order, metal plate on a fitted prosthetic, null for bare flesh. A robot race's
    /// body is metal plate too; the punch tests that itself.
    /// </summary>
    static ArmourType? StruckMaterial(HealthPart part, ReadOnlySpan<ArmourPiece> armour, ReadOnlySpan<bool> covering)
    {
        if (part.Template.LimbSlot >= 0 && part.Limb == LimbState.Replaced) return ArmourType.MetalPlate;

        ArmourType? last = null;
        for (int i = 0; i < armour.Length; i++)
            if (covering[i]) last = armour[i].Data.Material;
        return last;
    }

    /// <summary>
    /// A cover roll from a function of the piece index.
    /// </summary>
    readonly struct FuncCover(Func<int, float> roll) : ICoverRoll
    {
        /// <inheritdoc/>
        public float Roll(int piece) => roll(piece);
    }
}

/// <summary>
/// The cut, blunt and flat pierce damage worn armour stopped.
/// </summary>
public readonly record struct ArmourAbsorbed(float Cut, float Blunt, float Pierce);

/// <summary>
/// Whether the armour piece at an index covers the part a blow hit: a roll in [0, 1).
/// </summary>
public interface ICoverRoll
{
    /// <summary>
    /// The roll in [0, 1) for armour piece <paramref name="piece"/>.
    /// </summary>
    float Roll(int piece);
}
