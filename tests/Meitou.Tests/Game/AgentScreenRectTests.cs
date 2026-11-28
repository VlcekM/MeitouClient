using System.Numerics;
using Meitou.Game.DevTools.Agents;

namespace Meitou.Tests.Game;

/// <summary>
/// The control channel's on-screen box of an entity (<c>entity-screen</c>, <c>world pick --entity</c>): the part on screen and where a click on it
/// goes.
/// </summary>
public class AgentScreenRectTests
{
    /// <summary>
    /// A 1280 x 972 screen.
    /// </summary>
    static readonly Vector2 Screen = new(1280, 972);

    /// <summary>
    /// A box within the screen is kept whole and, without a point of its own, clicked at its middle. Catches a clip that moves a box already on
    /// screen.
    /// </summary>
    [Fact]
    public void A_box_on_screen_is_kept_and_clicked_at_its_middle()
    {
        var (rect, centre) = AgentOrders.ScreenRect(100, 200, 300, 260, Screen, null);

        Assert.Equal(new Vector4(100, 200, 200, 60), rect);
        Assert.Equal(new Vector2(200, 230), centre);
    }

    /// <summary>
    /// A building whose projected corners lie far past the screen's edges (the Bad Teeth bakery seen from 150 m: x from -25029 to 1881) gives the
    /// part on screen and is clicked at its own point. Catches a click sent off screen while the building fills it.
    /// </summary>
    [Fact]
    public void A_box_reaching_far_off_screen_is_clipped_and_clicked_at_its_own_point()
    {
        var (rect, centre) = AgentOrders.ScreenRect(-25029, 150, 1881, 13529, Screen, new Vector2(640.4f, 470.2f));

        Assert.Equal(new Vector4(0, 150, 1280, 822), rect);
        Assert.Equal(new Vector2(640, 470), centre);
    }

    /// <summary>
    /// Without a point on screen, a clipped box is clicked at the middle of its part on screen. Catches the middle of the whole box, off screen.
    /// </summary>
    [Fact]
    public void A_clipped_box_without_a_point_is_clicked_at_the_middle_of_its_part_on_screen()
    {
        var (_, centre) = AgentOrders.ScreenRect(-25029, 150, 1881, 13529, Screen, null);

        Assert.Equal(new Vector2(640, 561), centre);
    }
}
