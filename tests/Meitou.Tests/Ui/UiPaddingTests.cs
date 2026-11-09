using Meitou.Data.Ui;

namespace Meitou.Tests.Ui;

public class UiPaddingTests
{
    [Fact]
    public void SkinTextInsetsResizeAndExtraPaddingOnlyChangesTheTextClient()
    {
        var resources = new MyGuiResources();
        resources.Skins["parchment"] = new("parchment", 152, 41, "atlas.png",
                [new("SimpleText", new(13, 7, 126, 26), "Stretch", new Dictionary<string, UiRect>())]);
        var control = new UiControl("description", "TextBox", new(10, 20, 300, 100), "Text", "", "parchment", true, new Dictionary<string,
                string> { ["TextPadding"] = "6 8 10 12" }, default);
        Assert.Equal(new UiRect(29, 35, 258, 65), UiTextLayout.ContentRect(resources, control));
        Assert.Equal(new UiRect(10, 20, 300, 100), control.Bounds);
        Assert.Equal("description", MyGuiPresentation.HitTest([control with { Pickable = true }], 11, 21)!.Name);

        // A client narrower than its insets collapses without extending outside the widget.
        Assert.Equal(new UiRect(15, 23, 0, 0), UiInsets.Uniform(20).Apply(new(10, 20, 5, 3)));
        Assert.Throws<InvalidDataException>(() => UiInsets.Parse("-1"));
        Assert.Throws<InvalidDataException>(() => UiInsets.Parse("NaN"));
    }
    [Theory]
    [InlineData("8", 8, 8, 8, 8)]
    [InlineData("12 6", 12, 6, 12, 6)]
    [InlineData("3 4 5 6", 3, 4, 5, 6)]
    public void TextPaddingUsesInvariantPixelInsets(string value, float left, float top, float right, float bottom) =>
        Assert.Equal(new UiInsets(left, top, right, bottom), UiInsets.Parse(value));
}
