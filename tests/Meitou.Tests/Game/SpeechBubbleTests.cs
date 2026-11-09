using Meitou.Data.Ui;
using Meitou.Game;
using Meitou.Game.Ui;

namespace Meitou.Tests.Game;

/// <summary>
/// The speech bubbles' push-apart (<c>FUN_140671f50</c>, docs/game/dialogue.md "Speech bubbles").
/// </summary>
public class SpeechBubbleTests
{
    [Fact]
    public void Overlapping_bubbles_move_apart_along_the_shorter_overlap()
    {
        // 100 x 40 boxes overlapping by 10 in x and 30 in y: horizontal push of 10 / 2 + 1 = 6 each way.
        var moved = SpeechBubbles.SeparateBubbles([new(0, 0, 100, 40), new(90, 10, 100, 40)]);
        Assert.Equal(-6, moved[0].X);
        Assert.Equal(96, moved[1].X);
        Assert.Equal(0, moved[0].Y);
    }
}
