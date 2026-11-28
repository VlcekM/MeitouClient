using System.Numerics;
using Meitou.Game.DevTools.Agents;

namespace Meitou.Tests.Game;

/// <summary>
/// The control channel's on-screen box of an entity (<c>entity-screen</c>, <c>world pick --entity</c>): the part on screen.
/// </summary>
public class AgentScreenRectTests
{
    /// <summary>
    /// A 1280 x 972 screen.
    /// </summary>
    static readonly Vector2 Screen = new(1280, 972);

    /// <summary>
    /// A box within the screen is kept whole. Catches a clip that moves a box already on screen.
    /// </summary>
    [Fact]
    public void A_box_on_screen_is_kept()
    {
        var rect = AgentOrders.ScreenRect([new(100, 200), new(300, 260)], Screen);

        Assert.Equal(new Vector4(100, 200, 200, 60), rect);
    }

    /// <summary>
    /// A building whose projected corners lie far past the screen's edges (the Bad Teeth bakery seen from 150 m: x from -25029 to 1881) gives the
    /// part on screen. Catches a box whose middle lies off screen while the building fills it.
    /// </summary>
    [Fact]
    public void A_box_reaching_far_off_screen_is_clipped()
    {
        var rect = AgentOrders.ScreenRect([new(-25029, 150), new(1881, 13529)], Screen);

        Assert.Equal(new Vector4(0, 150, 1280, 822), rect);
    }
}
