using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;

namespace Meitou.Tests.Combat;

/// <summary>The formulas of docs/game/combat.md with their worked numbers (base-game CONSTANTS, no install needed).</summary>
public class FormulaTests
{
    [Fact]
    public void Constants_are_rescaled_once_as_the_loader_does()
    {
        var c = Constants;
        Assert.Equal(0.65f, c.DamageMultiplier, 5);
        Assert.Equal(13f, c.CutDamage1, 4);
        Assert.Equal(52f, c.CutDamage99, 4);
        Assert.Equal(13f, c.BluntDamage1, 4);
        Assert.Equal(52f, c.BluntDamage99, 4);
        Assert.Equal(0.78f, c.PierceDamageMultiplier, 5);
        Assert.Equal(52f, c.StumbleDamageMax, 4);
        Assert.Equal(1.1f, c.BowDamage1);
        Assert.Equal(1.3f, c.BowDamage99);
        Assert.Equal(1.2f, c.BlockChanceIncreasePerLevel, 5);
        Assert.Equal(1.5f, c.BlockChanceReductionPerLevel, 5);
        Assert.Equal(70f, c.BaseBlockChance);
        Assert.Equal(0.8f, c.UnarmedDamageMult);
    }

    [Fact]
    public void Weapon_stats_follow_quality_manufacturer_and_model()
    {
        var m = new WeaponManufacturer { StringId = "m", CutDamageMod = 1.1f, BluntDamageMod = 0.9f, MinCutDamage = 2, WeightMod = 1.5f };
        var model = new WeaponMaterial { StringId = "mk", AttackMod = 2, DefenceMod = -1 };
        var s = WeaponStats.Compute(0.5f, Katana, m, model, Constants);
        Assert.Equal(1.15f * 1.1f, s.CutMultiplier, 5);      // lerp(0.5, 0.3, 2) = 1.15
        Assert.Equal(0f, s.BluntMultiplier);
        Assert.Equal(2f, s.MinCut);                          // 1 x 2
        Assert.Equal(3f, s.Weight, 5);                       // 2 kg x 1.5; the blunt term is 0
        Assert.Equal(6, s.AttackMod);
        Assert.Equal(-5, s.DefenceMod);
        // Quality 0 and 1 are the ends of the range.
        Assert.Equal(0.3f, KatanaAt(0).Stats.CutMultiplier, 5);
        Assert.Equal(2f, KatanaAt(1).Stats.CutMultiplier, 5);
    }

    [Fact]
    public void Blunt_weapon_weight_is_the_larger_of_the_kg_term_and_the_scaled_blunt_term()
    {
        var club = WeaponInstance.Create(1, Club, null, null, Constants).Stats;   // blunt multiplier 2 x 1.5 = 3
        Assert.Equal(3f, club.BluntMultiplier, 5);
        Assert.Equal(MathF.Max(3, 40 * 3 * 1 * 0.5f), club.Weight, 4);              // 60 kg: the blunt term wins
    }

    [Fact]
    public void Cut_damage_of_a_katana_at_quality_half_and_skill_fifty()
    {
        var w = KatanaAt(0.5f).Stats;
        // t = (50 + 50) x 1.15 / 200 = 0.575; cut = 13 + (52 - 13) x 0.575
        Assert.Equal(13 + 39 * 0.575f, DamageFormulas.Cut(50, 50, w, Constants), 3);
        Assert.Equal(0f, DamageFormulas.Blunt(50, 50, w, Constants));                // a katana has no blunt multiplier
        Assert.Equal(13f, DamageFormulas.Cut(0, 0, w, Constants), 3);                // zero skill is the base
        // Unclamped: a huge skill goes past the skill-99 value.
        Assert.True(DamageFormulas.Cut(300, 300, w, Constants) > 52);
    }

    [Fact]
    public void Blunt_dominant_weapon_has_other_bases()
    {
        var w = WeaponInstance.Create(1, Club, null, null, Constants).Stats;         // cut 1.0, blunt 3.0
        Assert.True(DamageFormulas.IsBluntDominant(w));
        // cut: base 0.4 x 13 = 5.2; t = 100 x 1 / 200 = 0.5
        Assert.Equal(5.2f + (52 - 5.2f) * 0.5f, DamageFormulas.Cut(50, 50, w, Constants), 3);
        // blunt: t = (25 + 75) x 3 / 200 = 1.5 from base 13
        Assert.Equal(13 + 39 * 1.5f, DamageFormulas.Blunt(50, 50, w, Constants), 3);
    }

    [Fact]
    public void Pierce_damage_of_a_creature_weapon()
    {
        Assert.Equal(0.78f * 100 * 1 + 10, DamageFormulas.Pierce(60, 40, 1, Constants), 3);
        Assert.Equal(0f, DamageFormulas.Pierce(60, 40, 0, Constants));
    }

    [Fact]
    public void Unarmed_damage_splits_by_martial_arts_skill()
    {
        var (cut0, blunt0) = DamageFormulas.Unarmed(0, 40, 40, Constants);
        var (cut100, blunt100) = DamageFormulas.Unarmed(100, 40, 40, Constants);
        Assert.Equal(0f, cut0, 4);                            // g = 1 at martial 0: all blunt
        Assert.True(blunt0 > 0);
        Assert.True(cut100 > 0 && blunt100 > 0);              // g = 0.4: 60% cut
        Assert.Equal(0.6f, cut100 / (cut100 + blunt100), 4);
        Assert.True(cut100 + blunt100 > cut0 + blunt0);       // f grows from 0.25 to 1
        // A weak fighter still does at least 1.
        var (c, b) = DamageFormulas.Unarmed(0, 0, 0, Constants);
        Assert.True(c + b >= 1);
    }

    [Fact]
    public void Stumble_threshold_is_toughness_times_052()
    {
        Assert.Equal(26f, DamageFormulas.StumbleThreshold(50, Constants), 3);
    }

    [Fact]
    public void Packet_scales_by_target_kind_race_and_damage_output()
    {
        var weapon = KatanaAt(0.5f);
        var human = DamageFormulas.Packet(50, 50, 50, weapon, TargetKind.Human, null, 1, Constants);
        var robot = DamageFormulas.Packet(50, 50, 50, weapon, TargetKind.Robot, null, 1, Constants);
        Assert.Equal(35.425f * 1.1f, human.Cut, 2);
        Assert.Equal(35.425f * 0.6f, robot.Cut, 3);
        Assert.Equal(1.2f, human.BleedMultiplier);
        Assert.Equal(-0.3f, human.ArmourPenetration);
        var raced = WeaponInstance.Create(0.5f, Katana with { RaceDamage = new Dictionary<string, int> { ["shek"] = 150 } }, null, null, Constants);
        Assert.Equal(35.425f * 1.1f * 1.5f, DamageFormulas.Packet(50, 50, 50, raced, TargetKind.Human, "shek", 1, Constants).Cut, 2);
        Assert.Equal(35.425f * 1.1f * 2, DamageFormulas.Packet(50, 50, 50, weapon, TargetKind.Human, null, 2, Constants).Cut, 3);
    }

    [Theory]
    [InlineData(50, 50, 70)]        // equal skill: the base
    [InlineData(60, 50, 82)]        // +10 levels: 70 + 1.2 x 10
    [InlineData(40, 50, 55)]        // -10 levels: 70 - 1.5 x 10
    [InlineData(80, 40, 95)]        // 70 + 48 = 118 -> 90 + 0.2 x 28 = 95.6 -> clamped to 95
    [InlineData(70, 40, 90 + 0.2f * (70 + 36 - 90))]  // 106 -> 93.2
    [InlineData(0, 90, 5)]          // 70 - 135 < 5
    public void Block_chance(float defence, float attack, float expected)
    {
        Assert.Equal(expected, DefenceFormulas.BlockChance(defence, attack, Constants), 3);
    }

    [Fact]
    public void Block_chance_guarding_and_the_unarmed_clamp()
    {
        float d = DefenceFormulas.Defence(40, -4, true, 3, true);
        Assert.Equal(40 - 4 + 3 + 20, d);
        // A weapon that cannot block contributes no defence mod.
        Assert.Equal(40, DefenceFormulas.Defence(40, -4, false, 0, false));
        Assert.Equal(5, DefenceFormulas.BlockChance(0, 90, Constants), 3);
        Assert.Equal(0, DefenceFormulas.BlockChance(0, 90, Constants, 0, 95), 3);   // the unarmed variant floors at 0
    }

    [Fact]
    public void Effective_defence_loses_to_encumbrance_and_bad_legs()
    {
        Assert.Equal(50f, DefenceFormulas.EffectiveDefence(50, 1, 1, 0, false), 4);
        Assert.Equal(50 - 0.4f * 50, DefenceFormulas.EffectiveDefence(50, 0.6f, 1, 0, false), 4);
        Assert.Equal(50 - 0.5f * 50, DefenceFormulas.EffectiveDefence(50, 1, 0, 0, false), 4);   // the leg factor floors at 0.5
        Assert.Equal(50 - 0.6f * 50, DefenceFormulas.EffectiveDefence(50, 0, 1, 0, false), 4);    // encumbrance floors at 0.4: it loses (1 - 0.4) x 50
    }

    [Fact]
    public void Skill_roll_chance_is_half_at_equal_skill_and_matches_the_comparison()
    {
        Assert.Equal(0.5f, DefenceFormulas.SkillRollChance(40, 40, Constants), 5);
        Assert.Equal(0.75f, DefenceFormulas.SkillRollChance(60, 40, Constants), 5);   // k = 2
        Assert.Equal(0.25f, DefenceFormulas.SkillRollChance(40, 60, Constants), 5);   // k = 1/2
        // The closed form against the comparison itself, over a grid of the two randoms.
        foreach (var (s1, s2) in new[] { (50f, 50f), (70f, 30f), (20f, 90f) })
        {
            int n = 400, hits = 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (DefenceFormulas.SkillRoll(s1, s2, (i + 0.5f) / n, (j + 0.5f) / n, Constants)) hits++;
            Assert.Equal(DefenceFormulas.SkillRollChance(s1, s2, Constants), hits / (float)(n * n), 2);
        }
    }

    [Fact]
    public void Dodge_skill()
    {
        Assert.Equal(0.01f * (0.6f * 20 + 0.4f * 50), DefenceFormulas.DodgeSkill(20, 50), 5);
    }

    [Fact]
    public void Armour_defences_from_class_material_and_quality()
    {
        var plate = ArmourPiece.Create(BodyPlate, 0.5f);       // heavy metal plate, t = 0.5
        Assert.Equal(0.475f, plate.CutDefence, 4);              // lerp(0.5, 0.1, 0.85)
        Assert.Equal(0.3f, plate.BluntDefence, 4);              // lerp(0.5, 0, 0.6)
        Assert.Equal(45f, plate.PierceDefence, 3);              // lerp(0.5, 0, 90)
        // Lower class scales the factors; level bonus raises t.
        var medium = ArmourPiece.Create(Helmet, 0.5f);
        Assert.Equal(0.475f * 0.9f, medium.CutDefence, 4);
        var leveled = ArmourPiece.Create(BodyPlate with { LevelBonus = 10 }, 0.5f);     // t = 0.6
        Assert.Equal(0.1f + 0.75f * 0.6f, leveled.CutDefence, 4);
        // The cut cap is 0.9 and the blunt cap 0.8.
        var maxed = ArmourPiece.Create(BodyPlate with { CutDefBonus = 5, BluntDefBonus = 5 }, 1);
        Assert.Equal(0.9f, maxed.CutDefence);
        Assert.Equal(0.8f, maxed.BluntDefence);
        // A shirt with coverage is weaker.
        var shirt = ArmourPiece.Create(BodyPlate with { Slot = 8 }, 0.5f);
        Assert.Equal(0.475f * 0.8f, shirt.CutDefence, 4);
        Assert.Equal(22.5f, shirt.PierceDefence, 3);
        Assert.Equal(0.05f, ArmourPiece.QualityOfGrade(0));
        Assert.Equal(0.95f, ArmourPiece.QualityOfGrade(5));
        Assert.Equal(0.20f, ArmourPiece.QualityOfGrade(9));
    }

    [Fact]
    public void Armour_stacks_multiplicatively_and_penetration_scales_the_reduction()
    {
        var a = ArmourPiece.Create(BodyPlate, 0.5f);       // cut 0.475, blunt 0.3, cut-into-stun 0.4
        var b = ArmourPiece.Create(Helmet with { CutIntoStun = 0.5f, Class = ArmourClass.Heavy }, 0.5f);   // cut 0.475, blunt 0.3
        ReadOnlySpan<ArmourPiece> both = [a, b];
        var e = ArmourStack.Stack(both, [true, true], 0);
        Assert.Equal(0.475f + 0.475f * 0.525f, e.CutReduction, 4);
        Assert.Equal(0.3f + 0.3f * 0.7f, e.BluntReduction, 4);
        Assert.Equal(90f, e.PierceFlat, 3);
        Assert.Equal(0.475f * 0.4f + 0.475f * 0.5f * 0.525f, e.StunFraction, 4);
        // Only the covering pieces count.
        var one = ArmourStack.Stack(both, [false, true], 0);
        Assert.Equal(0.475f, one.CutReduction, 4);
        // A katana (-0.3) raises the reduction, capped at 0.9; a club (+0.2) lowers it.
        Assert.Equal(0.9f, ArmourStack.Stack(both, [true, true], -0.3f).CutReduction, 4);
        Assert.Equal(0.475f * 1.3f, ArmourStack.Stack([a], [true], -0.3f).CutReduction, 4);
        Assert.Equal(0.475f * 0.8f, ArmourStack.Stack([a], [true], 0.2f).CutReduction, 4);
        // Nothing covering, nothing absorbed.
        Assert.Equal(ArmourEffect.None, ArmourStack.Stack(both, [false, false], 0.5f));
    }

    [Fact]
    public void Coverage_is_a_percentage_roll_and_a_missing_part_never_covers()
    {
        Assert.True(ArmourStack.Covers(Rags, "101-t", 0.89f));     // 90 per cent
        Assert.False(ArmourStack.Covers(Rags, "101-t", 0.91f));
        Assert.False(ArmourStack.Covers(Rags, "32-t", 0));          // not listed
        Assert.True(ArmourStack.Covers(BodyPlate, "29-t", 0.9999f));
    }

    [Fact]
    public void Toughness_resistance_runs_from_minus_to_plus_065()
    {
        Assert.Equal(-0.65f, HitResolver.ToughnessResistance(0, Constants), 5);
        Assert.Equal(0f, HitResolver.ToughnessResistance(50, Constants), 5);
        Assert.Equal(0.65f, HitResolver.ToughnessResistance(100, Constants), 5);
        Assert.Equal(0.65f, HitResolver.ToughnessResistance(500, Constants), 5);        // the fraction is clamped
    }

    [Fact]
    public void Resolve_applies_armour_then_toughness_then_the_global_multiplier()
    {
        var packet = new DamagePacket(40, 20, 30, 0, 1.2f, 0);
        var none = HitResolver.Resolve(packet, ArmourEffect.None, 50, 1, Constants);
        Assert.Equal(40f, none.Cut, 3);
        Assert.Equal(20f, none.Blunt, 3);
        Assert.Equal(30f, none.Pierce, 3);
        Assert.Equal(1.2f, none.Scale);
        // Toughness 0: x1.65; toughness 100: x0.35.
        Assert.Equal(40 * 1.65f, HitResolver.Resolve(packet, ArmourEffect.None, 0, 1, Constants).Cut, 3);
        Assert.Equal(40 * 0.35f, HitResolver.Resolve(packet, ArmourEffect.None, 100, 1, Constants).Cut, 3);
        Assert.Equal(80f, HitResolver.Resolve(packet, ArmourEffect.None, 50, 2, Constants).Cut, 3);
        // Armour: cut -60%, blunt -50%, 12 flat pierce, a quarter of the cut turns to stun (and of the absorbed pierce).
        var worn = new ArmourEffect(0.6f, 0.5f, 12, 0.25f);
        var r = HitResolver.Resolve(packet, worn, 50, 1, Constants);
        Assert.Equal(16f, r.Cut, 3);
        Assert.Equal(10f, r.Blunt, 3);
        Assert.Equal(18f, r.Pierce, 3);
        Assert.Equal(40 * 0.25f + 0.25f * 12, r.ExtraStun, 3);
        // Pierce below the flat defence is nothing.
        Assert.Equal(0f, HitResolver.Resolve(new DamagePacket(0, 0, 5, 0, 1, 0), worn, 50, 1, Constants).Pierce);
    }

    [Fact]
    public void Landing_a_blow_takes_flesh_from_the_chosen_part_and_blood_from_the_body()
    {
        var medical = Healthy();
        var ctx = Context();
        float bloodBefore = medical.Blood;
        var packet = new DamagePacket(20, 0, 0, 0, 1, 0);
        var (part, damage) = HitResolver.Land(medical, ctx, packet, [], _ => 0.5f, 50, false, false, Constants, null);
        Assert.InRange(part, 0, medical.Parts.Count - 1);
        Assert.Equal(20f, damage.Cut, 3);
        Assert.Equal(medical.Parts[part].MaxHp - 20, medical.Parts[part].Flesh, 2);
        Assert.True(medical.Blood < bloodBefore);
        // The same part is more likely next time: its hitmult rose by one.
        Assert.Equal(2f, medical.Parts[part].HitMult, 3);
    }

    [Fact]
    public void Plate_over_the_chest_absorbs_a_katana_cut()
    {
        var medical = Healthy();
        var ctx = Context();
        var weapon = KatanaAt(0.5f);
        var packet = DamageFormulas.Packet(50, 50, 50, weapon, TargetKind.Human, null, 1, Constants);
        var plate = ArmourPiece.Create(BodyPlate, 0.5f);
        // Force every part to be covered (the roll is 0) and compare with the bare blow.
        var bare = HitResolver.Resolve(packet, ArmourEffect.None, 50, 1, Constants);
        var covered = HitResolver.Resolve(packet, ArmourStack.Stack([plate], [true], packet.ArmourPenetration), 50, 1, Constants);
        Assert.True(covered.Cut < bare.Cut * 0.5f);
        Assert.True(covered.ExtraStun > 0);
        Assert.Equal(1, medical.Parts.Count(p => p.Template.StringId == "101-t"));   // fixture check: the chest is "101-t"
    }
}
