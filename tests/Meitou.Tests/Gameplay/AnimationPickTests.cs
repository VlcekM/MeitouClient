using Meitou.Data.Gameplay;

namespace Meitou.Tests.Gameplay;

/// <summary>Rules of the original's animation candidate filters (docs/animation.md "Choosing candidates").</summary>
public class AnimationPickTests
{
    static AnimationDefinition Clip(string id, int category = 0, string[]? stumbles = null) => new()
    {
        Id = id, Name = id, Clip = id, Category = category, Stumbles = stumbles ?? [], Chance = 100, WeaponLeft = 2, WeaponRight = 2, CombatMode = 2, StealthMode = 2, Kinds = WeaponKinds.All,
    };

    [Fact]
    public void Stumbles_must_match_the_animation_class_category()
    {
        var lib = new AnimationLibrary([Clip("stand", 0, ["Chest"]), Clip("swim", 4, ["Chest"]), Clip("crawl", 5, ["Chest"])]);
        var into = new List<(int Index, float Weight)>();
        foreach (var (category, expected) in new[] { (0, 0), (4, 1), (5, 2) })
        {
            into.Clear();
            lib.Stumbles("Chest", false, 0, new AnimationStance { Category = category, Prone = false }, -1, into);
            Assert.Equal([expected], into.Select(c => c.Index));
        }
        into.Clear();
        lib.Stumbles("Chest", false, 0, new AnimationStance { Category = 4 }, -1, into);
        Assert.Single(into);
    }

    [Fact]
    public void A_weather_record_with_stumbles_is_a_stumble_in_its_own_category_and_nothing_else()
    {
        var weather = Clip("wind", AnimationLibrary.WeatherCategory, ["Chest"]) with { StumbleCategory = 0 };
        var lib = new AnimationLibrary([Clip("stand"), weather]);
        var into = new List<(int Index, float Weight)>();
        lib.Stumbles("Chest", false, 0, new AnimationStance(), -1, into);
        Assert.Equal([1], into.Select(c => c.Index));
        Assert.Empty(lib.Idles(new AnimationStance()));
    }

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
