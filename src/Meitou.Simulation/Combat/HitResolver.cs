using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The middle of the hit pipeline (docs/game/combat.md "The hit pipeline", steps 3 and 4): a damage packet through the armour that covered it and the target's toughness, ending as the
/// <see cref="HitDamage"/> the medical system takes (step 5 is <see cref="MedicalState.ApplyHit"/>).
/// </summary>
public static class HitResolver
{
    /// <summary>
    /// The toughness resistance r = lerp(clamp(toughness x 0.01, 0, 1), min, max) with the stored <c>damage resistance min / max</c> (-0.65 / 0.65): r is -0.65 at toughness 0, so an untrained
    /// target takes 1.65 times the damage and a toughness-100 target 0.35 times. <paramref name="toughness"/> is the effective stat.
    /// </summary>
    public static float ToughnessResistance(float toughness, CombatConstants c) =>
        WeaponStats.Lerp(Math.Clamp(toughness * 0.01f, 0, 1), c.DamageResistanceMin, c.DamageResistanceMax);

    /// <summary>
    /// cut' = cut (1 - cutRed); blunt' = blunt (1 - bluntRed); pierce' = max(0, pierce - flat); stun = the packet's stun + cut x stun fraction + the same fraction of the pierce the armour
    /// absorbed; then cut, blunt, pierce and stun are each multiplied by (1 - r), cut and blunt floored at 0, and all four by the global damage multiplier of the difficulty options.
    /// The packet's bleed multiplier becomes the hit's scale.
    /// </summary>
    public static HitDamage Resolve(in DamagePacket packet, in ArmourEffect armour, float toughness, float globalDamageMultiplier, CombatConstants c)
    {
        float cut = packet.Cut * (1 - armour.CutReduction);
        float blunt = packet.Blunt * (1 - armour.BluntReduction);
        float pierce = MathF.Max(0, packet.Pierce - armour.PierceFlat);
        float stun = packet.Stun + packet.Cut * armour.StunFraction + armour.StunFraction * (packet.Pierce - pierce);
        float resist = 1 - ToughnessResistance(toughness, c);
        cut = MathF.Max(0, cut * resist) * globalDamageMultiplier;
        blunt = MathF.Max(0, blunt * resist) * globalDamageMultiplier;
        pierce = pierce * resist * globalDamageMultiplier;
        stun = stun * resist * globalDamageMultiplier;
        return new HitDamage(cut, blunt, pierce, stun, packet.BleedMultiplier);
    }

    /// <summary>
    /// Lands a blow on a body: chooses the part (weighted by hit weight x hitmult; a low strike excludes arms and head; hit direction 6 uses the weight alone), tests each piece's coverage of
    /// that part with a roll from <paramref name="coverRoll"/> (called per piece, in order), resolves the damage and applies it to the part. Returns the part hit and the damage applied.
    /// </summary>
    public static (int Part, HitDamage Damage) Land(MedicalState medical, in MedicalContext ctx, in DamagePacket packet, ReadOnlySpan<ArmourPiece> armour,
        Func<int, float> coverRoll, float toughness, bool lowStrike, bool directionSix, CombatConstants c, List<MedicalEvent>? events)
    {
        int part = medical.ChoosePart(ctx, lowStrike, directionSix);
        if (part < 0) return (-1, default);
        string partId = medical.Parts[part].Template.StringId;
        Span<bool> covering = armour.Length <= 32 ? stackalloc bool[armour.Length] : new bool[armour.Length];
        for (int i = 0; i < armour.Length; i++) covering[i] = ArmourStack.Covers(armour[i].Data, partId, coverRoll(i));
        var effect = ArmourStack.Stack(armour, covering, packet.ArmourPenetration);
        var damage = Resolve(packet, effect, toughness, ctx.Options.GlobalDamageMultiplier, c);
        medical.ApplyHit(part, damage, ctx, events);
        return (part, damage);
    }
}
