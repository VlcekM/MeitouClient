using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Tests.Ui;

public class MyGuiColourTests
{
    [Fact]
    public void TextSkinColoursHaveStateFallbackAndExplicitWidgetColoursTakePrecedence()
    {
        var resources = new MyGuiResources();
        resources.Skins["Text"] = new("Text", 100, 30, "", [new("SimpleText", new(0, 0, 100, 30), "Stretch", new Dictionary<string, UiRect>(),
            new Dictionary<string, IReadOnlyDictionary<string, string>> { ["normal"] = new Dictionary<string, string> { ["colour"] = "0 0 0" },
                    ["highlighted"] = new Dictionary<string, string> { ["colour"] = "0.3 0.2 0.1" } })]);
        var control = new UiControl("label", "TextBox", new(0, 0, 100, 30), "Text", "", "Text", true, new Dictionary<string, string>(), default);
        Assert.Equal(new Vector4(0, 0, 0, 1), MyGuiColours.Text(resources, control, "normal", Vector4.One));
        Assert.Equal(new Vector4(0.3f, 0.2f, 0.1f, 1), MyGuiColours.Text(resources, control, "highlighted", Vector4.One));
        Assert.Equal(new Vector4(0, 0, 0, 1), MyGuiColours.Text(resources, control, "disabled", Vector4.One));
        var explicitColour = control with { Properties = new Dictionary<string, string> { ["TextColour"] = "0.8 0.7 0.6" } };
        Assert.Equal(new Vector4(0.8f, 0.7f, 0.6f, 1), MyGuiColours.Text(resources, explicitColour, "normal", Vector4.One));
    }
    [Fact]
    public void ParchmentListRowsUseDarkInkAndADarkPanelCanRequestLightInk()
    {
        var layout = MyGuiReader.ParseLayout("<MyGUI type='Layout'><Widget name='List' position='0 0 200 100'/></MyGUI>");
        var bindings = new UiBindings();
        bindings.Lists["List"] = [new("one", "Melee attack 1")];
        var row = MyGuiPresentation.Arrange(layout, 800, 600, bindings, []).Single(c => c.Name == "List@row0");
        Assert.Equal(MyGuiColours.Ink, MyGuiColours.Text(new(), row, "normal", Vector4.One));
        bindings.Properties["List"] = new Dictionary<string, string> { ["TextColour"] = "1 1 1" };
        row = MyGuiPresentation.Arrange(layout, 800, 600, bindings, []).Single(c => c.Name == "List@row0");
        Assert.Equal(Vector4.One, MyGuiColours.Text(new(), row, "normal", default));
    }
}
