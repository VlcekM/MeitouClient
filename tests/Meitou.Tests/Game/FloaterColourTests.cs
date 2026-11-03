using System.Numerics;
using Meitou.Game;
using Meitou.Simulation;

namespace Meitou.Tests.Game;

/// <summary>The damage floater's tint (<c>FUN_1406508d0</c>): green for a player's blow or TEMP ENEMY, then red (winning) for a player's character or TEMP ALLY, else grey.</summary>
public class FloaterColourTests
{
    static Floater Hit(bool targetPlayer, bool sourcePlayer, bool enemy = false, bool ally = false) =>
        Floater.Hit(CharacterId.None, Vector3.Zero, targetPlayer, sourcePlayer, "Head", default, default) with { TargetTempEnemy = enemy, TargetTempAlly = ally };

    [Fact]
    public void Tints_follow_the_originals_two_tests()
    {
        Assert.Equal(FloaterText.Grey, FloaterText.Colour(Hit(false, false)));
        Assert.Equal(FloaterText.Green, FloaterText.Colour(Hit(false, true)));
        Assert.Equal(FloaterText.Green, FloaterText.Colour(Hit(false, false, enemy: true)));
        Assert.Equal(FloaterText.Red, FloaterText.Colour(Hit(false, false, ally: true)));
        Assert.Equal(FloaterText.Red, FloaterText.Colour(Hit(true, true)));
        Assert.Equal(FloaterText.Red, FloaterText.Colour(Hit(false, true, enemy: true, ally: true)));
    }
}
