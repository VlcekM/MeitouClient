using Meitou.Game.Rendering.Sky;


namespace Meitou.Tests.Rendering;

/// <summary>
/// The rules of the fog shading rate (<c>fog-vrs</c>, docs/render-post.md "Fog shading rate"): the codes of the rate attachment, the opacity of the
/// air over a surface and the fragment size it gives.
/// </summary>
public class FogShadingRateTests
{
    static readonly (float TwoByTwo, float FourByFour) Defaults = (FogShadingRate.DefaultTwoByTwo, FogShadingRate.DefaultFourByFour);

    [Fact]
    public void A_more_opaque_surface_never_gets_a_finer_rate()
    {
        int last = 1;
        for (float o = 0; o <= 1f; o += 0.01f)
        {
            int size = FogShadingRate.Size(o, 4, Defaults);

            Assert.True(size >= last, $"opacity {o}");

            last = size;
        }
    }
}
