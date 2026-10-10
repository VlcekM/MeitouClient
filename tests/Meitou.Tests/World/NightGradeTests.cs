using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Tests.World;

/// <summary>Meitou's scotopic night grading (docs/render-post.md "Night grading"): the weights, the rod luminance and the blend, and that the composite shader carries the same constants.</summary>
public class NightGradeTests
{
    [Fact]
    public void Rod_weights_sum_to_one_so_grey_keeps_its_value()
    {
        Assert.Equal(1f, NightGrade.RodR + NightGrade.RodG + NightGrade.RodB, 4);
        Assert.Equal(0.5f, NightGrade.RodLuminance(0.5f, 0.5f, 0.5f), 4);
    }

    [Fact]
    public void Rods_see_blue_brighter_and_red_darker_than_cones_do()
    {
        Assert.True(NightGrade.RodLuminance(0, 0, 1) > 1.5f * NightGrade.Luminance(0, 0, 1));
        Assert.True(NightGrade.RodLuminance(1, 0, 0) < 0.25f * NightGrade.Luminance(1, 0, 0));
    }

    [Fact]
    public void Tint_has_unit_photopic_luminance_and_is_blue()
    {
        var t = NightGrade.Tint;
        Assert.Equal(1f, NightGrade.Luminance(t.R, t.G, t.B), 4);
        Assert.True(t.B > t.G && t.G > t.R);
    }

    [Fact]
    public void Adaptation_weight_is_zero_by_day_one_in_the_dark_and_falls_smoothly()
    {
        Assert.Equal(0f, NightGrade.AdaptationWeight(0.66f));      // midday, measured
        Assert.Equal(0f, NightGrade.AdaptationWeight(0.33f));      // the end of dusk
        Assert.Equal(1f, NightGrade.AdaptationWeight(0.042f));     // deep night, measured
        float a = NightGrade.AdaptationWeight(0.25f), b = NightGrade.AdaptationWeight(0.15f), c = NightGrade.AdaptationWeight(0.08f);
        Assert.InRange(a, 0f, b);
        Assert.InRange(b, a, c);
        Assert.InRange(c, b, 1f);
        Assert.True(a > 0 && c < 1);
        Assert.Equal(0.5f, NightGrade.AdaptationWeight(MathF.Sqrt(NightGrade.MeanDark * NightGrade.MeanLight)), 4);   // the middle of the log range
    }

    [Fact]
    public void Pixels_far_brighter_than_the_scene_keep_their_colour()
    {
        Assert.Equal(1f, NightGrade.PixelWeight(0.04f, 0.04f));   // as bright as the average
        Assert.Equal(0f, NightGrade.PixelWeight(0.2f, 0.04f));      // a lamp-lit surface
        Assert.True(NightGrade.PixelWeight(0.07f, 0.04f) is > 0 and < 1);
    }

    [Fact]
    public void Daylight_and_zero_strength_change_nothing()
    {
        var c = (R: 0.6f, G: 0.35f, B: 0.1f);
        Assert.Equal(c, NightGrade.Apply(c, 0.4f, 0.66f, 1));
        Assert.Equal(c, NightGrade.Apply(c, 0.04f, 0.042f, 0));
    }

    [Fact]
    public void At_night_orange_ground_goes_blue_grey_at_nearly_the_same_brightness()
    {
        var orange = (R: 0.30f, G: 0.14f, B: 0.04f);
        var g = NightGrade.Apply(orange, 0.05f, 0.042f, 1);
        Assert.True(g.B / g.R > 1.0f, "bluer than red now");
        Assert.True(orange.B / orange.R < 0.2f);
        float before = NightGrade.Luminance(orange.R, orange.G, orange.B), after = NightGrade.Luminance(g.R, g.G, g.B);
        Assert.InRange(after / before, 0.6f, 1.05f);              // the rods' share darkens reds a little, nothing like the exposure
        var grey = NightGrade.Apply((0.2f, 0.2f, 0.2f), 0.04f, 0.042f, 1);
        Assert.InRange(NightGrade.Luminance(grey.R, grey.G, grey.B), 0.199f, 0.201f);   // a neutral grey keeps its brightness exactly
    }

    [Fact]
    public void A_lamp_in_the_dark_keeps_its_colour()
    {
        var lamp = (R: 2.0f, G: 1.0f, B: 0.3f);
        Assert.Equal(lamp, NightGrade.Apply(lamp, 1.2f, 0.042f, 1));
    }

    [Fact]
    public void Composite_shader_carries_the_same_constants()
    {
        string s = PostProcessShaders.Composite;
        string F(float v) => v.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains($"vec3({F(NightGrade.RodR)}, {F(NightGrade.RodG)}, {F(NightGrade.RodB)})", s);
        Assert.Contains($"vec3({F(NightGrade.LumR)}, {F(NightGrade.LumG)}, {F(NightGrade.LumB)})", s);
        Assert.Contains($"vec3({F(NightGrade.TintR0)}, {F(NightGrade.TintG0)}, {F(NightGrade.TintB0)})", s);
        Assert.Contains($"PURKINJE = {F(NightGrade.PurkinjeShare)}", s);
        Assert.Contains($"log({F(NightGrade.MeanDark)}), log({F(NightGrade.MeanLight)})", s);
        Assert.Contains($"log({F(NightGrade.RatioFrom)}), log({F(NightGrade.RatioTo)})", s);
    }

    [Fact]
    public void Faithful_presets_have_no_grading_and_meitou_has_it()
    {
        Assert.Equal(NightGrade.MeitouStrength, PostOptions.Create("meitou").NightGradeStrength);
        Assert.Equal(0f, PostOptions.Create("kenshi").NightGradeStrength);
        Assert.Equal(0f, PostOptions.Create("off").NightGradeStrength);
    }
}
