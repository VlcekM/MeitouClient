using Meitou.Data.Gameplay;

namespace Meitou.Tests.Gameplay;

/// <summary>
/// Rules of the original's animation candidate filters (docs/animation.md "Choosing candidates").
/// </summary>
public class AnimationPickTests
{
    static AnimationDefinition Clip(string id, int category = 0, string[]? stumbles = null) => new()
    {
        Id = id, Name = id, Clip = id, Category = category, Stumbles = stumbles ?? [], Chance = 100, WeaponLeft = 2, WeaponRight = 2,
        CombatMode = 2, StealthMode = 2, Kinds = WeaponKinds.All,
    };

    [Fact]
    public void An_unarmed_idle_that_wants_an_item_in_a_hand_weighs_a_tenth()
    {
        var hand = Clip("hold") with { Area = AnimationArea.Upper, Idle = true, IdleChance = 10, WeaponRight = 1 };
        var either = hand with { WeaponRight = 2, WeaponLeft = 2 };
        var unarmedItem = new AnimationStance { Right = new HandHold(WeaponKinds.Unarmed) };
        Assert.Equal(1f, hand.IdleWeight(unarmedItem), 4);
        Assert.Equal(10f, either.IdleWeight(unarmedItem), 4);
        Assert.Equal(10f, (hand with { Area = AnimationArea.Lower }).IdleWeight(unarmedItem), 4);
        Assert.Equal(10f, hand.IdleWeight(new AnimationStance { Right = new HandHold(WeaponKinds.Katana) }), 4);
    }
}
