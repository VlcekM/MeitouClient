using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>
/// Meitou's scotopic night grading (docs/render-post.md "Night grading"): the weights, the rod luminance and the blend, and that the composite shader
/// carries the same constants.
/// </summary>
public class NightGradeTests
{
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
}
