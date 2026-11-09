using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;

namespace Meitou.Tests.Combat;

/// <summary>
/// The formulas of docs/game/combat.md with their worked numbers (base-game CONSTANTS, no install needed).
/// </summary>
public class FormulaTests
{
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

    [Theory]
    [InlineData(5, "0.00", "0.85")]
    [InlineData(20, "0.02", "0.86")]
    [InlineData(40, "0.06", "0.88")]
    [InlineData(60, "0.12", "0.90")]
    [InlineData(80, "0.21", "0.92")]
    [InlineData(95, "0.29", "0.94")]
    public void Heavy_metal_plate_tooltip_effects_follow_the_observed_samurai_armour_grades(int level, string stealth, string combatSpeed)
    {
        // The Samurai Armour at its six grades (docs/game/ui-screens.md "Item tooltip"): stealth mult 1, combat speed mult 0.94, metal plate, heavy.
        var armour = BodyPlate with
        {
            Class = ArmourClass.Heavy, Material = ArmourType.MetalPlate, StealthMult = 1, CombatSpeedMult = 0.94f, LevelBonus = 0,
        };
        var lines = ArmourTooltip.Tail(armour, id => id, level);
        string Value(string label) => lines.First(l => l.Label == label).Value;
        Assert.Equal(stealth + "x", Value("-Stealth effect"));
        Assert.Equal(combatSpeed + "x", Value("-Combat speed effect"));
    }

    [Theory]
    [InlineData(5, "0.00x", "0.81x", "0.89x")]
    [InlineData(20, "0.01x", "0.83x", "0.90x")]
    [InlineData(40, "0.03x", "0.86x", "0.92x")]
    [InlineData(60, "0.06x", "0.89x", "0.94x")]
    [InlineData(80, "0.11x", "0.92x", "0.96x")]
    [InlineData(95, "0.15x", "0.94x", "0.98x")]
    public void Samurai_legplates_effects_follow_the_wikis_six_grades(int level, string stealth, string athletics, string combatSpeed)
    {
        // The wiki's vanilla Legplates at each grade: stealth 0.00, 0.009, 0.03, 0.06, 0.1056, 0.14535x;
        // athletics 0.8075, 0.83, 0.86, 0.89, 0.92, 0.9425x; combat speed 0.887, 0.90, 0.92, 0.94, 0.96, 0.9751x.
        var legplates = BodyPlate with
        {
            Class = ArmourClass.Heavy, Material = ArmourType.MetalPlate, Slot = 6, StealthMult = 0.5f, CombatSpeedMult = 0.98f,
            AthleticsMult = 1, DodgeMult = 0.6f, LevelBonus = 0,
        };
        var lines = ArmourTooltip.Tail(legplates, id => id, level);
        string Value(string label) => lines.First(l => l.Label == label).Value;
        Assert.Equal(stealth, Value("-Stealth effect"));
        Assert.Equal(athletics, Value("-Athletics effect"));
        Assert.Equal(combatSpeed, Value("-Combat speed effect"));
    }

    [Fact]
    public void Samurai_legplates_resistances_at_level_80_read_as_the_original_prints_them()
    {
        // The original prints the level-80 Legplates as 52 %, 63 % and 71 pts (not 72: 90 x (80 x 0.01f) is just below 72 in single precision).
        var legplates = BodyPlate with
        {
            Class = ArmourClass.Heavy, Material = ArmourType.MetalPlate, Slot = 6, BluntDefBonus = 0.05f, CutDefBonus = -0.081f,
            CutIntoStun = 0, LevelBonus = 0,
        };
        var head = ArmourTooltip.Head(legplates, 80);
        Assert.Equal("52%", head.First(l => l.Label == "-Blunt resistance").Value);
        Assert.Equal("63%", head.First(l => l.Label == "-Cut resistance").Value);
        Assert.Equal("71 pts", head.First(l => l.Label == "-Harpoon resistance").Value);
    }

    [Fact]
    public void Armour_weight_follows_the_part_coverage_as_the_original_prints_it()
    {
        // The wiki's weights of the Samurai pieces (12.5, 6.25, 20.25, 4 kg), all metal plate,
        // from the coverage lists of the load order (docs/game/ui-screens.md "Item tooltip").
        PartCoverage[] Coverage(params (string Part, int Percent)[] parts) => [.. parts.Select(p => new PartCoverage(p.Part, p.Percent))];
        var plate = BodyPlate with { Class = ArmourClass.Heavy, Material = ArmourType.MetalPlate };
        Assert.Equal(12.5f, (plate with { Slot = 6, Coverage = Coverage(("rl", 100), ("ll", 100), ("st", 50)) }).WeightAt(95));
        Assert.Equal(6.25f, (plate with { Slot = 3, Coverage = Coverage(("head", 100)) }).WeightAt(95));
        Assert.Equal(
            20.25f,
            (plate with { Slot = 5, Coverage = Coverage(("la", 85), ("st", 85), ("ra", 85), (ArmourData.ChestPartId, 100)) }).WeightAt(95));
        Assert.Equal(4f, (plate with { Slot = 9, Coverage = Coverage(("rl", 40), ("ll", 40)) }).WeightAt(95));
        Assert.Null((plate with { Slot = 6, Coverage = Coverage(("rl", 100)) }).WeightAt(0));
        Assert.Null((plate with { Slot = 6, Coverage = [] }).WeightAt(95));
    }

    [Fact]
    public void Samurai_legplates_tooltip_at_level_95_matches_the_original()
    {
        // The Legplates as the game's modded data have them (stealth 0.5, combat speed 0.98, dodge 0.6, unarmed -10, blunt +0.05,
        // cut -0.081): the original shows 61 % / 73 % / 85 pts, 0.15x, 0.98x.
        var legplates = BodyPlate with
        {
            Class = ArmourClass.Heavy, Material = ArmourType.MetalPlate, Slot = 6, StealthMult = 0.5f, CombatSpeedMult = 0.98f,
            DodgeMult = 0.6f, UnarmedBonus = -10, BluntDefBonus = 0.05f, CutDefBonus = -0.081f, CutIntoStun = 0, LevelBonus = 0,
            Coverage = [new("Stomach", 50), new("Right Leg", 100), new("Left Leg", 100)],
        };
        var head = ArmourTooltip.Head(legplates, 95);
        Assert.Equal("61%", head.First(l => l.Label == "-Blunt resistance").Value);
        Assert.Equal("73%", head.First(l => l.Label == "-Cut resistance").Value);
        Assert.Equal("85 pts", head.First(l => l.Label == "-Harpoon resistance").Value);
        Assert.Equal("[Masterwork Grade]", head[0].Label);
        var tail = ArmourTooltip.Tail(legplates, id => id, 95);
        Assert.Equal(["[Coverage]", "-Right Leg", "-Left Leg", "-Stomach"], tail.Select(l => l.Label).Take(4).ToArray());
        Assert.Equal("0.15x", tail.First(l => l.Label == "-Stealth effect").Value);
        Assert.Equal("0.94x", tail.First(l => l.Label == "-Athletics effect").Value);
        Assert.DoesNotContain(head, l => l.Label == "-Cut resistance efficiency");
        Assert.Equal("0.98x", tail.First(l => l.Label == "-Combat speed effect").Value);
    }

    [Fact]
    public void Armour_stacks_multiplicatively_and_penetration_scales_the_reduction()
    {
        // Cut 0.475, blunt 0.3, cut-into-stun 0.4.
        var a = ArmourPiece.Create(BodyPlate, 0.5f);

        // Cut 0.475, blunt 0.3.
        var b = ArmourPiece.Create(Helmet with { CutIntoStun = 0.5f, Class = ArmourClass.Heavy }, 0.5f);
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
}
