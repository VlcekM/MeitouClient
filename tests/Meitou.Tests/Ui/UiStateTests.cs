using System.Numerics;
using System.Xml.Linq;
using Meitou.Data.Ui;
using Meitou.Game.Ui;

namespace Meitou.Tests.Ui;

public class UiStateTests
{
    [Fact]
    public void EditingSelectionInsertsAndRemovesWholeUnicodeCharacters()
    {
        var text = new UiTextBuffer("a😀b");
        text.Key(UiEditKey.Left);
        text.Key(UiEditKey.Backspace);
        Assert.Equal("ab", text.Text);
        Assert.Equal(1, text.Caret);
        text.Key(UiEditKey.Home);
        text.Key(UiEditKey.Right, extend: true);
        text.Insert("Kenshi");
        Assert.Equal("Kenshib", text.Text);
        text.Key(UiEditKey.SelectAll);
        text.Key(UiEditKey.Delete);
        Assert.Equal("", text.Text);
        Assert.Equal(0, text.Caret);
        text.Insert(new string('x', 600));
        Assert.Equal(512, text.Text.Length);
        text.Key(UiEditKey.SelectAll);
        text.Insert("slot");
        Assert.Equal("slot", text.Text);
    }
    [Fact]
    public void MapZoomPreservesThePointUnderTheCursorAndPanClampsAtTheEdges()
    {
        var map = new UiMapView();
        var viewport = new UiRect(100, 50, 600, 400);
        var cursor = new Vector2(460, 210);
        var original = map.At(viewport, cursor);
        map.ZoomAt(viewport, cursor, 4);
        Assert.True(Vector2.Distance(original, map.At(viewport, cursor)) < 0.0001f);
        map.Pan(viewport, new(100000));
        var image = map.ImageBounds(viewport);
        Assert.True(image.X <= viewport.X && image.Y <= viewport.Y);
        Assert.True(image.X + image.Width >= viewport.X + viewport.Width && image.Y + image.Height >= viewport.Y + viewport.Height);
        map.ZoomAt(viewport, cursor, 0.0001f);
        Assert.Equal(new UiRect(100, -50, 600, 600), map.ImageBounds(viewport));
    }
    [Fact]
    public void RuntimeCanRevealSourceHiddenTabsAndDecodeAnImageSet()
    {
        var layout = MyGuiReader.ParseLayout("""
            <MyGUI type="Layout"><Widget type="TabControl" name="Tabs" position="0 0 200 100">
              <Widget type="TabItem" name="Hidden" position="0 30 200 70"><Property key="Visible" value="false"/>
                <Widget name="Content" position="0 0 100 30"/>
              </Widget>
            </Widget></MyGUI>
            """);
        var bindings = new UiBindings();
        Assert.DoesNotContain(MyGuiPresentation.Arrange(layout, 800, 600, bindings, []), c => c.Name == "Content");
        bindings.Properties["Hidden"] = new Dictionary<string, string> { ["Visible"] = "true" };
        Assert.Contains(MyGuiPresentation.Arrange(layout, 800, 600, bindings, []), c => c.Name == "Content");
        var resources = new MyGuiResources();
        resources.Resources["Markers"] = XElement.Parse("<Resource type='ResourceImageSet'><Group name='Character' texture='atlas.png' size='32 24'>"
            + "<Index name='Player'><Frame point='100 50'/></Index></Group></Resource>");
        var image = MyGuiImages.Resolve(resources,
            new Dictionary<string, string> { ["ImageResource"] = "Markers", ["ImageGroup"] = "Character", ["ImageName"] = "Player" });
        Assert.Equal(new MyGuiImage("atlas.png", new(100, 50, 32, 24)), image);
    }
    [Fact]
    public void ModalRootsCentreAndTabFocusAndReturnUseTheEnabledDefaultAction()
    {
        var host = new MyGuiScreenHost(new());
        host.Register("save", MyGuiReader.ParseLayout("""
            <MyGUI type="Layout"><Widget name="Root" position_real="0 0 0.5 0.5">
              <Widget type="EditBox" name="Name" position="0 0 100 30"/>
              <Widget type="Button" name="Save" position="0 40 100 30"/>
            </Widget></MyGUI>
            """), modal: true, centreRoot: true);
        host.Open("save");
        UiBindings Bind(string _) { var b = new UiBindings(); b.Properties["Save"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
                return b;
                }
        var frame = host.Arrange(800, 600, Bind).Single().Controls;
        Assert.Equal(new UiRect(200, 150, 400, 300), frame.Single(c => c.Name == "Root").Bounds);
        Assert.True(host.FocusNext());
        host.TextInput('a');
        Assert.Equal("a", host.GetText("save", "Name"));
        MyGuiEvent? clicked = null;
        host.Activated += e => clicked = e;
        Assert.True(host.Submit());
        Assert.Equal("Save", clicked!.Widget);
        host.Arrange(800, 600, _ => { var b = Bind(""); b.Disabled.Add("Save"); return b; });
        Assert.False(host.Submit());
    }
}
