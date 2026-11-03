using Meitou.Game;

namespace Meitou.Tests.Game;

/// <summary>The splash slide height scales with the screen width over 1920 (<c>FUN_140914850</c>, <c>DAT_1421337ec</c> set by <c>FUN_1406e3c30</c>).</summary>
public class SplashScaleTests
{
    [Fact]
    public void Slide_height_follows_the_width_scale()
    {
        Assert.Equal(490, LoadingScreen.SplashSlideHeight(700, 1920, 1080, 0.7f));
        Assert.Equal(877, LoadingScreen.SplashSlideHeight(700, 3440, 1440, 0.7f));
        Assert.Equal(1440, LoadingScreen.SplashSlideHeight(768, 3440, 1440, 100));
    }
}
