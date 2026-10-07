using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Combat;
using static Meitou.Tests.Combat.CombatFixtures;

namespace Meitou.Tests.Combat;

/// <summary>The defender's reaction and the outcome rule (docs/game/combat.md "Technique choice, block and dodge", "Hit outcome").</summary>
public class ReactionTests
{
    static readonly CombatTechnique[] Techniques = KatanaTechniques();   // 0 cut left, 1 combo, 2 block up (1), 3 block left (2), 4 block right (3), 5 block thrust (4), 6 dodge back

    static string Name(int index) => index < 0 ? "none" : Techniques[index].Name;

    [Fact]
    public void A_block_needs_the_right_direction_and_more_than_half_the_animation()
    {
        var block = Block("block left", 2);
        Assert.Equal(BlowOutcome.Blocked, HitOutcomes.Decide(block, 0.8f, 2));
        Assert.Equal(BlowOutcome.Blocked, HitOutcomes.Decide(block, 0.51f, 2));
        Assert.Equal(BlowOutcome.Hit, HitOutcomes.Decide(block, 0.5f, 2));       // "past half": exactly half is not
        Assert.Equal(BlowOutcome.Hit, HitOutcomes.Decide(block, 0.3f, 2));
        Assert.Equal(BlowOutcome.Hit, HitOutcomes.Decide(block, 0.9f, 3));       // the wrong direction
        Assert.Equal(BlowOutcome.Hit, HitOutcomes.Decide(null, 1, 2));           // no reaction
    }

    [Fact]
    public void A_dodge_counts_inside_its_window_only()
    {
        var dodge = Dodge("dodge");
        Assert.Equal(BlowOutcome.Dodged, HitOutcomes.Decide(dodge, 0.1f, 3));
        Assert.Equal(BlowOutcome.Dodged, HitOutcomes.Decide(dodge, 0.5f, 3));
        Assert.Equal(BlowOutcome.Dodged, HitOutcomes.Decide(dodge, 0.98f, 3));
        Assert.Equal(BlowOutcome.Hit, HitOutcomes.Decide(dodge, 0.05f, 3));
        Assert.Equal(BlowOutcome.Hit, HitOutcomes.Decide(dodge, 0.99f, 3));
    }

    [Fact]
    public void A_technique_flagged_as_both_counts_as_a_block_for_the_direction_it_faces_and_a_dodge_for_the_rest()
    {
        var both = Block("dodge back", 1, dodge: true);
        Assert.Equal(BlowOutcome.Blocked, HitOutcomes.Decide(both, 0.8f, 1));
        Assert.Equal(BlowOutcome.Dodged, HitOutcomes.Decide(both, 0.8f, 2));
        Assert.Equal(BlowOutcome.Hit, HitOutcomes.Decide(both, 0.05f, 2));
    }

    [Fact]
    public void A_successful_block_roll_picks_a_technique_of_the_blows_direction()
    {
        // Direction 2 (left): only "block left". Direction 1 (down): "block up" and "dodge back" are flagged for it, both are block-capable here only if flagged: the fixture's dodge back is a pure dodge.
        for (float pick = 0; pick < 1; pick += 0.1f)
            Assert.Equal("block left", Name(TechniqueChooser.ChooseReaction(Techniques, WeaponKinds.Katana, 0, false, true, 2, 70, 0, 0.5f, pick)));
        Assert.Equal("block thrust", Name(TechniqueChooser.ChooseReaction(Techniques, WeaponKinds.Katana, 0, false, true, 4, 70, 0, 0.0f, 0.9f)));
    }

    [Fact]
    public void A_failed_block_roll_picks_a_block_of_another_direction_so_the_blow_passes()
    {
        var picks = new HashSet<string>();
        for (float pick = 0; pick < 1; pick += 0.05f)
            picks.Add(Name(TechniqueChooser.ChooseReaction(Techniques, WeaponKinds.Katana, 0, false, true, 2, 70, 0, 0.9f, pick)));
        Assert.Equal(new HashSet<string> { "block up", "block right", "block thrust" }, picks);   // never "block left", never the dodge
    }

    [Fact]
    public void A_rear_blow_is_only_wrongly_blocked_by_a_rear_technique()
    {
        var all = new[] { Block("up", 1), Block("rear up", 7), Block("rear right", 9) };
        for (float pick = 0; pick < 1; pick += 0.1f)
        {
            int i = TechniqueChooser.ChooseReaction(all, WeaponKinds.Katana, 0, false, true, 7, 70, 0, 0.99f, pick);
            Assert.Equal("rear right", all[i].Name);
        }
    }

    [Fact]
    public void The_pick_is_weighted_by_chance()
    {
        var all = new[] { Block("a", 1, chance: 3), Block("b", 1, chance: 1) };
        int a = 0, n = 400;
        for (int i = 0; i < n; i++)
            if (TechniqueChooser.ChooseReaction(all, WeaponKinds.Katana, 0, false, true, 1, 100, 0, 0, (i + 0.5f) / n) == 0) a++;
        Assert.InRange(a, 295, 305);
    }

    [Fact]
    public void Fists_dodge_only_when_the_dodge_roll_succeeds()
    {
        var all = new[] { Block("block up", 1), Dodge("dodge", 1), Dodge("dodge fall", 0.4f) };
        // The block techniques are for weapons; the dodges list Unarmed in the fixture's kinds.
        Assert.Equal(-1, TechniqueChooser.ChooseReaction(all, WeaponKinds.Unarmed, 0, false, false, 1, 0, 40, 0.5f, 0));
        int pick = TechniqueChooser.ChooseReaction(all, WeaponKinds.Unarmed, 0, false, false, 1, 0, 40, 0.3f, 0.2f);
        Assert.True(pick is 1 or 2);
    }

    [Fact]
    public void Disabled_prone_animal_and_stumble_techniques_are_not_candidates()
    {
        var all = new[] { Block("disabled", 1) with { Disabled = true }, Block("prone", 1) with { IsProne = true }, Block("animal", 1) with { Animal = 9 },
            Block("stumble", 1) with { IsStumbleDodge = true }, Block("wrong kind", 1) with { Kinds = WeaponKinds.Blunt } };
        Assert.Equal(-1, TechniqueChooser.ChooseReaction(all, WeaponKinds.Katana, 0, false, true, 1, 100, 0, 0, 0));
        Assert.Equal(1, TechniqueChooser.ChooseReaction(all, WeaponKinds.Katana, 0, true, true, 1, 100, 0, 0, 0));
    }

    [Fact]
    public void Attack_choice_follows_weapon_distance_skill_and_the_static_rows()
    {
        var all = new[]
        {
            Cut("static only") with { AttackDistance = -999, AttackDistanceMinVsStatic = 99 },
            Cut("moving only") with { AttackDistance = 0, AttackDistanceMinVsStatic = -10 },
            Cut("both") with { AttackDistance = 10, AttackDistanceMinVsStatic = 10 },
            Cut("disabled") with { Disabled = true },
            Cut("expert") with { MinSkill = 70 },
            Block("block", 1),
        };
        string Pick(float gap, bool moving, float skill = 50, WeaponKinds kind = WeaponKinds.Katana, float reach = 10.5f)
        {
            var seen = new HashSet<string>();
            for (float r = 0; r < 1; r += 0.05f)
            {
                int i = TechniqueChooser.ChooseAttack(all, kind, 0, false, skill, gap, moving, reach, r);
                seen.Add(i < 0 ? "-" : all[i].Name);
            }
            return string.Join(",", seen.Order());
        }
        Assert.Equal("both,static only", Pick(5, false));
        Assert.Equal("-", Pick(11, false));                  // beyond the weapon's reach (10.5), whatever the technique allows
        Assert.Equal("both,moving only", Pick(0, true));
        Assert.Equal("both", Pick(8, true));
        Assert.Equal("both,expert,static only", Pick(5, false, skill: 80));
        Assert.Equal("-", Pick(5, false, kind: WeaponKinds.Blunt));
    }

    [Fact]
    public void Techniques_with_no_weapon_flag_are_never_valid()
    {
        var none = Cut("none") with { Kinds = WeaponKinds.None };
        Assert.Equal(-1, TechniqueChooser.ChooseAttack([none], WeaponKinds.Katana, 0, false, 50, 3, false, 10, 0));
    }

    [Fact]
    public void Frames_are_fractions_for_one_frame_records_and_frame_numbers_otherwise()
    {
        var sword = Cut() with { NumFrames = 1, BlockedFrame1 = 0.509f, StopFrame1 = 0.527f, AcceptableEndTime = 0.95f };
        Assert.Equal(0.509f, sword.StrikeProgress(1), 5);
        Assert.Equal(0.527f, sword.StopProgress(1), 5);
        Assert.Equal(0.95f, sword.EndProgress, 5);
        var fist = Cut() with { NumFrames = 57, BlockedFrame1 = 18, BlockedFrame2 = 31, StopFrame1 = 19, StopFrame2 = 38, AcceptableEndTime = 40 };
        Assert.Equal(18f / 57, fist.StrikeProgress(1), 5);
        Assert.Equal(31f / 57, fist.StrikeProgress(2), 5);
        Assert.Equal(38f / 57, fist.StopProgress(2), 5);
        Assert.Equal(40f / 57, fist.EndProgress, 5);
    }
}
