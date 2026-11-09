using System.Numerics;
using System.Xml.Linq;
using Meitou.Data.Ui;
using Meitou.Game.Ui;

namespace Meitou.Tests.Ui;

public class MyGuiScreenTests
{
    static MyGuiResources Resources()
    {
        var r = new MyGuiResources();
        r.Resources["WindowFrame"] = XElement.Parse("""
            <Resource type="ResourceLayout" name="WindowFrame">
              <Widget type="Widget" skin="PanelEmpty" position="0 0 200 150">
                <Widget type="Widget" skin="Panel" position="0 30 200 120" align="Stretch">
                  <Widget type="Widget" skin="PanelEmpty" name="Client" position="4 4 192 112" align="Stretch"/>
                </Widget>
                <Widget type="Widget" skin="Header" position="0 0 200 30" align="HStretch Top">
                  <Widget type="Button" name="Button" position="170 0 30 30" align="Right Top"><UserString key="Event" value="close"/></Widget>
                  <Widget type="TextBox" name="Caption" position="0 0 160 30" align="HStretch Top"/>
                </Widget>
              </Widget>
            </Resource>
            """);
        return r;
    }
    static MyGuiLayout Window() => MyGuiReader.ParseLayout("""
        <MyGUI type="Layout"><Widget type="Window" name="Root" skin="WindowFrame" position="0 0 300 200">
          <Property key="Caption" value="Inventory"/>
          <Widget type="EditBox" name="Name" position="5 5 100 25"/>
          <Widget type="ListBox" name="Items" position="5 40 150 80"/>
        </Widget></MyGUI>
        """);

    [Fact]
    public void BoundWindowSizeAndTemplateScalePreserveTheClientAndCloseButtonWithoutChangingSource()
    {
        var host = new MyGuiScreenHost(Resources());
        host.Register("bag", Window());
        host.Open("bag");
        UiBindings Bind(string _) { var b = new UiBindings { TemplateScale = 2 }; b.Positions["Root"] = new(0, 0, 500, 400); return b; }
        var frame = host.Arrange(1000, 800, Bind).Single().Controls;
        Assert.Equal(new UiRect(250, 200, 500, 400), frame.Single(c => c.Type == "Window").Bounds);
        Assert.Equal(new UiRect(690, 200, 60, 60), frame.Single(c => c.Action == "mygui:close:Root").Bounds);
        Assert.Equal(new UiRect(263, 273, 100, 25), frame.Single(c => c.Name == "Name").Bounds);

        // Unbound, the template's client and header resize without changing the source.
        var source = Window();
        var expanded = MyGuiTemplates.Expand(source, Resources());
        var plain = MyGuiPresentation.Arrange(expanded, 800, 600, new(), []);
        Assert.Equal(new UiRect(9, 39, 100, 25), plain.Single(c => c.Name == "Name").Bounds);
        Assert.Equal(new UiRect(270, 0, 30, 30), plain.Single(c => c.Action == "mygui:close:Root").Bounds);
        Assert.Equal("Inventory", plain.Single(c => c.Properties.ContainsKey("CaptionOwner")).Caption);
        Assert.Equal("WindowFrame", source.Roots[0].Skin);
        Assert.Equal(3, source.Widgets.Count());
    }

    [Fact]
    public void WindowsCaptureDraggingTextEditingCloseAndModality()
    {
        var host = new MyGuiScreenHost(Resources());
        host.Register("inventory", Window());
        host.Register("modal", MyGuiReader.ParseLayout("<MyGUI type='Layout'><Widget name='Root' position='0 0 10 10'/></MyGUI>"), modal: true);
        host.Open("inventory");
        var frame = host.Arrange(800, 600, _ => new()).Single().Controls;
        var input = frame.Single(c => c.Name == "Name").Bounds;
        Assert.True(host.PointerDown(new(input.X + 2, input.Y + 2)));
        Assert.True(host.TextInput('A'));
        Assert.True(host.TextInput('B'));
        Assert.True(host.Backspace());
        Assert.Equal("A", host.Arrange(800, 600, _ => new()).Single().Controls.Single(c => c.Name == "Name").Caption);
        Assert.True(host.PointerDown(new(260, 210)));
        Assert.True(host.CapturesPointer);
        host.PointerMove(new(290, 230));
        host.PointerUp();
        var moved = host.Arrange(800, 600, _ => new()).Single().Controls;
        Assert.Equal(new UiRect(280, 220, 300, 200), moved.First(c => c.Name == "Root").Bounds);
        var close = moved.Single(c => c.Action == "mygui:close:Root").Bounds;
        host.PointerDown(new(close.X + 2, close.Y + 2));
        Assert.False(host.HasWindows);
        Assert.False(host.HasKeyboardFocus);
        host.Open("modal");
        host.Arrange(800, 600, _ => new());
        Assert.True(host.Contains(new(799, 599)));
        Assert.True(host.PointerDown(new(799, 599)));
        Assert.True(host.Escape());
        Assert.False(host.Contains(new(799, 599)));
    }

    [Fact]
    public void ListsScrollSelectAndClipAndTabsHideInactiveContents()
    {
        var host = new MyGuiScreenHost(Resources());
        host.Register("inventory", Window());
        host.Open("inventory");
        UiBindings Bind(string _) { var b = new UiBindings(); b.Lists["Items"] = Enumerable.Range(0,
                20).Select(i => new UiListItem(i.ToString(), "Item " + i)).ToArray(); return b; }
        var frame = host.Arrange(800, 600, Bind).Single().Controls;
        var list = frame.Single(c => c.Name == "Items").Bounds;
        MyGuiEvent? selected = null;
        host.Activated += e => selected = e;
        host.PointerDown(new(list.X + 6, list.Y + 4));
        Assert.Equal("0", selected!.Text);
        host.Wheel(new(list.X + 6, list.Y + 4), -2);
        var scrolled = host.Arrange(800, 600, Bind).Single().Controls;
        Assert.DoesNotContain(scrolled, c => c.Name == "Items@row0");
        // A ListBox wheel event moves one line whatever its size (MyGUI, docs/formats/mygui.md).
        Assert.Equal("Items@row1", MyGuiPresentation.HitTest(scrolled, list.X + 6, list.Y + 4)!.Name);
        var tabs = MyGuiReader.ParseLayout("""
            <MyGUI type="Layout"><Widget type="TabControl" name="Tabs" position="0 0 300 200">
              <Widget type="TabItem" name="One" position="0 30 300 170"><Widget type="Button" name="First" position="0 0 20 20"/></Widget>
              <Widget type="TabItem" name="Two" position="0 30 300 170"><Widget type="Button" name="Second" position="0 0 20 20"/></Widget>
            </Widget></MyGUI>
            """);
        var b = new UiBindings();
        b.SelectedTabs["Tabs"] = 1;
        var second = MyGuiPresentation.Arrange(tabs, 800, 600, b, []);
        Assert.DoesNotContain(second, c => c.Name == "First");
        Assert.Contains(second, c => c.Name == "Second");
        b.Hidden.Add("Two");
        Assert.DoesNotContain(MyGuiPresentation.Arrange(tabs, 800, 600, b, []), c => c.Name == "Tabs@tab1");
    }

    [Fact]
    public void TemplateCyclesFailWithADiagnostic()
    {
        var resources = Resources();
        resources.Resources["Cycle"] = XElement.Parse("<Resource type='ResourceLayout'><Widget><Widget skin='Cycle'/></Widget></Resource>");
        var source = MyGuiReader.ParseLayout("<MyGUI type='Layout'><Widget skin='Cycle'/></MyGUI>");
        Assert.Throws<InvalidDataException>(() => MyGuiTemplates.Expand(source, resources));
    }
}
