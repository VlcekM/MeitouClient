using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The rules of the fog shading rate (<c>fog-vrs</c>, docs/render-post.md "Fog shading rate"): the codes of the rate attachment, the opacity of the air over a surface and the fragment size it gives.</summary>
public class FogShadingRateTests
{
    static readonly (float TwoByTwo, float FourByFour) Defaults = (FogShadingRate.DefaultTwoByTwo, FogShadingRate.DefaultFourByFour);

    [Fact]
    public void The_attachment_codes_are_log2_of_the_width_and_height()
    {
        Assert.Equal(0, FogShadingRate.Encode(1, 1));
        Assert.Equal(4, FogShadingRate.Encode(2, 1));
        Assert.Equal(1, FogShadingRate.Encode(1, 2));
        Assert.Equal(5, FogShadingRate.Encode(2, 2));
        Assert.Equal(10, FogShadingRate.Encode(4, 4));
        // What the fog rate shader writes for 2 x 2 and 4 x 4 (PostProcessShaders.FogRate).
        Assert.Equal(1, FogShadingRate.SizeOf(0));
        Assert.Equal(2, FogShadingRate.SizeOf(5));
        Assert.Equal(4, FogShadingRate.SizeOf(10));
    }

    [Theory]
    [InlineData(0f, 1)]
    [InlineData(0.79f, 1)]
    [InlineData(0.8f, 2)]
    [InlineData(0.9f, 2)]
    [InlineData(0.969f, 2)]
    [InlineData(0.97f, 4)]
    [InlineData(1f, 4)]
    public void The_opacity_picks_the_fragment_size(float opacity, int size) =>
        Assert.Equal(size, FogShadingRate.Size(opacity, 4, Defaults));

    [Fact]
    public void A_device_without_4x4_rates_stops_at_2x2_and_one_without_rates_at_1x1()
    {
        Assert.Equal(2, FogShadingRate.Size(1f, 2, Defaults));
        Assert.Equal(1, FogShadingRate.Size(1f, 1, Defaults));
    }

    [Fact]
    public void The_haze_and_the_volumes_multiply_what_shows_of_the_surface()
    {
        Assert.Equal(0f, FogShadingRate.Opacity(0f, 1f), 1e-6f);          // clear air
        Assert.Equal(1f, FogShadingRate.Opacity(1f, 1f), 1e-6f);          // the haze alone hides it
        Assert.Equal(1f, FogShadingRate.Opacity(0f, 0f), 1e-6f);          // a volume alone hides it
        Assert.Equal(0.75f, FogShadingRate.Opacity(0.5f, 0.5f), 1e-6f);   // half of half shows
        Assert.Equal(0f, FogShadingRate.Opacity(-1f, 2f), 1e-6f);         // out of range values are clamped
    }

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

    [Theory]
    [InlineData("0.5,0.9", 0.5f, 0.9f)]
    [InlineData(" 0.7 , 1 ", 0.7f, 1f)]
    [InlineData(null, FogShadingRate.DefaultTwoByTwo, FogShadingRate.DefaultFourByFour)]
    [InlineData("", FogShadingRate.DefaultTwoByTwo, FogShadingRate.DefaultFourByFour)]
    [InlineData("0.9,0.5", FogShadingRate.DefaultTwoByTwo, FogShadingRate.DefaultFourByFour)]   // out of order
    [InlineData("0.5", FogShadingRate.DefaultTwoByTwo, FogShadingRate.DefaultFourByFour)]
    [InlineData("a,b", FogShadingRate.DefaultTwoByTwo, FogShadingRate.DefaultFourByFour)]
    [InlineData("0.5,2", FogShadingRate.DefaultTwoByTwo, FogShadingRate.DefaultFourByFour)]
    public void The_thresholds_come_from_the_environment_or_the_defaults(string? text, float two, float four)
    {
        var (a, b) = FogShadingRate.ParseThresholds(text);
        Assert.Equal(two, a, 1e-6f);
        Assert.Equal(four, b, 1e-6f);
    }

    [Theory]
    [InlineData(1920, 16, 120)]
    [InlineData(1080, 16, 68)]
    [InlineData(1920, 8, 240)]
    [InlineData(1, 16, 1)]
    public void A_rate_image_has_a_texel_per_tile_rounded_up(int pixels, int texel, int tiles) => Assert.Equal(tiles, FogShadingRate.Tiles(pixels, texel));
}
