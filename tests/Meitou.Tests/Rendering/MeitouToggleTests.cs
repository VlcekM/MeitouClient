using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The one Meitou key (F1 in the viewer, Shift+F1 in the game): everything Faithful, then back to the switches as they were.</summary>
public class MeitouToggleTests
{
    static Enhancement Switch(string id, bool[] state, int i) => new(id, id, "f", "m", () => state[i], v => state[i] = v, "");

    [Fact]
    public void OffThenBackToTheMixThatWasOn()
    {
        bool[] state = [true, false, true];
        var toggle = new MeitouToggle([Switch("a", state, 0), Switch("b", state, 1), Switch("c", state, 2)]);
        Assert.True(toggle.On);

        Assert.Contains("off", toggle.Toggle());
        Assert.Equal([false, false, false], state);
        Assert.False(toggle.On);

        Assert.Equal("meitou    on: a, c", toggle.Toggle());
        Assert.Equal([true, false, true], state);
    }

    [Fact]
    public void AllFaithfulAtStartTurnsEveryOneOn()
    {
        bool[] state = [false, false];
        var toggle = new MeitouToggle([Switch("a", state, 0), Switch("b", state, 1)]);
        toggle.Toggle();
        Assert.Equal([true, true], state);
    }
}
