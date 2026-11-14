using Meitou.Game.Host;

namespace Meitou.Tests.Gameplay;

/// <summary>
/// <see cref="MetalRenderFraction"/> (docs/metal-backend.md "No upscaler and the render resolution"): a window is controlled by the Full Retina
/// resolution switch, full screen by the Render resolution division, and neither touches the other.
/// </summary>
public class MetalRenderFractionTests
{
    [Fact]
    public void A_window_renders_at_its_pixels_or_with_the_switch_off_at_one_per_point()
    {
        Assert.Equal(1f, MetalRenderFraction.Of(fullScreen: false, divisor: 1, retinaResolution: true, backingScale: 2));
        Assert.Equal(0.5f, MetalRenderFraction.Of(fullScreen: false, divisor: 1, retinaResolution: false, backingScale: 2));
    }

    [Fact]
    public void Without_Retina_the_switch_changes_nothing()
    {
        Assert.Equal(1f, MetalRenderFraction.Of(fullScreen: false, divisor: 1, retinaResolution: false, backingScale: 1));
        // A window smaller than its framebuffer cannot mean more than one pixel per point.
        Assert.Equal(1f, MetalRenderFraction.Of(fullScreen: false, divisor: 1, retinaResolution: false, backingScale: 0.5f));
    }

    [Fact]
    public void Full_screen_follows_the_division_alone()
    {
        foreach (bool retina in new[] { true, false })
        {
            Assert.Equal(1f, MetalRenderFraction.Of(fullScreen: true, divisor: 1, retinaResolution: retina, backingScale: 2));
            Assert.Equal(0.25f, MetalRenderFraction.Of(fullScreen: true, divisor: 4, retinaResolution: retina, backingScale: 2));
        }
    }

    [Fact]
    public void A_window_has_no_division()
    {
        Assert.Equal(1f, MetalRenderFraction.Of(fullScreen: false, divisor: 4, retinaResolution: true, backingScale: 2));
        Assert.Equal(0.5f, MetalRenderFraction.Of(fullScreen: false, divisor: 4, retinaResolution: false, backingScale: 2));
    }
}
