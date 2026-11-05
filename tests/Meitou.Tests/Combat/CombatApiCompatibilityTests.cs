using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;

namespace Meitou.Tests.Combat;

public sealed class CombatApiCompatibilityTests
{
    delegate int LegacyChoosePart(in MedicalContext context, bool lowStrike, bool directionSix);
    delegate (int Part, HitDamage Damage, ArmourAbsorbed Absorbed) LegacyLandDetailed(MedicalState medical,
        in MedicalContext context, in DamagePacket packet, ReadOnlySpan<ArmourPiece> armour, NoCover cover,
        float toughness, bool lowStrike, bool directionSix, CombatConstants constants, List<MedicalEvent>? events);
    readonly struct NoCover : ICoverRoll { public float Roll(int piece) => 1; }

    [Fact]
    public void Original_medical_and_resolver_signatures_remain_bindable_and_keep_the_native_roll_stream()
    {
        var oldMedical = Healthy(); var explicitMedical = Healthy(); var context = Context();
        LegacyChoosePart oldChoose = oldMedical.ChoosePart;
        LegacyLandDetailed oldLand = HitResolver.LandDetailed<NoCover>;
        Assert.Equal(3, oldChoose.Method.GetParameters().Length);
        Assert.Equal(10, oldLand.Method.GetParameters().Length);
        var packet = new DamagePacket(1, 2, 0, 0, 1, 0);
        for (int hit = 0; hit < 20; hit++)
        {
            bool low = hit % 2 == 0, six = hit % 3 == 0;
            Assert.Equal(explicitMedical.ChoosePart(context, low, six, targetPart: null), oldChoose(context, low, six));
            var expected = HitResolver.LandDetailed(explicitMedical, context, packet, [], new NoCover(), 50, low, six, Constants, null, targetPart: null);
            var actual = oldLand(oldMedical, context, packet, [], new NoCover(), 50, low, six, Constants, null);
            Assert.Equal(expected, actual);
        }
        Assert.Equal(explicitMedical.Parts.Select(part => (part.Flesh, part.HitMult)), oldMedical.Parts.Select(part => (part.Flesh, part.HitMult)));
        for (int roll = 0; roll < 20; roll++)
            Assert.Equal(explicitMedical.ChoosePart(context), oldChoose(context, false, false));
    }
}
