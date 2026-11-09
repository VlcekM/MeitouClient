using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Combat;
using static Meitou.Tests.Combat.CombatFixtures;

namespace Meitou.Tests.Combat;

/// <summary>
/// The defender's reaction and the outcome rule (docs/game/combat.md "Technique choice, block and dodge", "Hit outcome").
/// </summary>
public class ReactionTests
{
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
        // Reach = attack distance + half the weapon's length (21): "static only" never (-999), "moving only" to 10.5, the others to 20.5.
        string Pick(float gap, float skill = 50, int category = (int)WeaponCategory.Katanas)
        {
            var seen = new HashSet<string>();
            var q = new AttackQuery
            {
                Category = category, Skill = skill, UnarmedSkill = skill, Encumbrance = 1, WeaponLength = 21, Scale = 1, Gap = gap,
                Previous = -1, LeftArm = true, RightArm = true,
            };

            // Fine steps: a row under its "attack distance min vs static" weighs x0.02.
            for (float r = 0; r < 1; r += 0.001f)
            {
                int i = TechniqueChooser.ChooseAttack(all, q, r, out _);
                seen.Add(i < 0 ? "-" : all[i].Name);
            }
            return string.Join(",", seen.Order());
        }
        Assert.Equal("both,expert,moving only", Pick(5, skill: 80));
        Assert.Equal("both,moving only", Pick(5));
        Assert.Equal("both", Pick(11));                      // beyond "moving only"'s reach (10.5)
        Assert.Equal("-", Pick(21));                         // beyond every reach
        Assert.Equal("-", Pick(5, category: (int)WeaponCategory.Blunt));
    }
}
