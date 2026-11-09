using System.Globalization;
using System.Xml;
using Meitou.Content;
using Meitou.Data.Ogre;
using Meitou.Data.Ui;

namespace Meitou.Tests.Ui;

public class MyGuiTests
{
    [Fact]
    public void LayoutPreservesTreeAndComputesParentRelativeCoordinates()
    {
        var layout = MyGuiReader.ParseLayout("""
            <MyGUI type="Layout" version="3.2.0">
              <Widget type="Widget" name="Root" skin="PanelEmpty" position_real="0 0 1 1">
                <Property key="NeedMouse" value="false"/>
                <Widget type="Widget" name="Panel" position_real="0.5 0.5 0.5 0.5" skin="Panel">
                  <Widget type="Button" name="Speed" position_real="0.1 0.2 0.4 0.3">
                    <Property key="Caption" value="Go &amp; run"/>
                    <UserString key="Event" value="start"/>
                  </Widget>
                </Widget>
              </Widget>
            </MyGUI>
            """);
        Assert.Equal("start", layout.Find("Speed")!.UserStrings["Event"]);
        var bindings = new UiBindings();
        bindings.Actions["Speed"] = "speed:1";
        bindings.Text["Speed"] = "1x";
        var frame = MyGuiPresentation.Arrange(layout, 1000, 800, bindings, []);
        var speed = Assert.Single(frame, c => c.Name == "Speed");
        Assert.Equal(550, speed.Bounds.X, 3);
        Assert.Equal(480, speed.Bounds.Y, 3);
        Assert.Equal(200, speed.Bounds.Width, 3);
        Assert.Equal(120, speed.Bounds.Height, 3);
        Assert.Equal("1x", speed.Caption);
        Assert.Equal("speed:1", MyGuiPresentation.HitTest(frame, 600, 500)!.Action);
        Assert.Null(MyGuiPresentation.HitTest(frame, 10, 10));
        bindings.Hidden.Add("Panel");
        Assert.Single(MyGuiPresentation.Arrange(layout, 1000, 800, bindings, []));
    }

    [Fact]
    public void CoordinatesAreInvariantAndInvalidXmlIsRejected()
    {
        var previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sk-SK");
            Assert.Equal(0.25f, UiRect.Parse("0.25 0 1 1").X);
            Assert.Throws<InvalidDataException>(() => UiRect.Parse("1 2 3"));
            Assert.Throws<InvalidDataException>(() => UiRect.Parse("NaN 0 1 1"));
            Assert.Throws<XmlException>(() => MyGuiReader.ParseLayout("<!DOCTYPE MyGUI [<!ENTITY x 'secret'>]><MyGUI type='Layout'/>"));
            Assert.Throws<InvalidDataException>(() => MyGuiReader.ParseLayout("<MyGUI type='Resource'/>"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("Right Bottom", 90, 80, 10, 20)]
    [InlineData("Stretch", 40, 30, 60, 70)]
    [InlineData("HStretch Top", 40, 30, 60, 20)]
    public void SkinPiecesAnchorAndStretch(string align, float x, float y, float w, float h)
    {
        Assert.Equal(new UiRect(x, y, w, h), new UiRect(40, 30, 10, 20).AlignTo(new(0, 0, 100, 100), 50, 50, align));
    }

    [Fact]
    public void ResourceIncludesResolveOnceAndKeepUnknownResources()
    {
        string directory = Path.Combine(Path.GetTempPath(), "meitou-ui-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "init.xml"), "<MyGUI type='List'><List file='skin.xml'/><List file='skin.xml'/></MyGUI>");
            File.WriteAllText(Path.Combine(directory, "skin.xml"), """
                <MyGUI type="Resource"><List file="init.xml"/>
                  <Resource type="ResourceSkin" name="Panel" size="10 20" texture="atlas.png">
                    <BasisSkin type="SubSkin" offset="0 0 10 20" align="Stretch">
                      <State name="normal" offset="40 50 10 20"/>
                    </BasisSkin>
                  </Resource>
                  <Resource type="ResourceLayout" name="Template"><Widget type="Button"/></Resource>
                  <Resource type="ResourceSkin" name="Text" size="10 20"><BasisSkin type="SimpleText" offset="0 0 10 20"><State name="normal" colour="0 0 0" shift="1"/></BasisSkin></Resource>
                </MyGUI>
                """);
            var resources = MyGuiResources.Load("init.xml", name => Path.Combine(directory, name));
            Assert.Equal(2, resources.LoadedFiles.Count);
            Assert.Equal(new UiRect(40, 50, 10, 20), resources.Skins["Panel"].Parts[0].States["normal"]);
            Assert.NotNull(resources.Resources["Template"].Element("Widget"));
            Assert.Equal("0 0 0", resources.Skins["Text"].Parts[0].StateProperties!["normal"]["colour"]);
            Assert.Equal("1", resources.Skins["Text"].Parts[0].StateProperties!["normal"]["shift"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact, Trait("Category", "Slow")]
    public void AllInstalledLayoutsAndHudResourcesCanBeRead()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "No Kenshi install configured.");
        var resources = OgreScriptResources.ForInstall(install, Data.LoadOrder.FromInstall(install));
        var ui = MyGuiResources.Load("kenshi_init.xml", name => resources.Find(name));
        Assert.Contains("Middle", ui.Layers);
        Assert.Contains("Kenshi_Button1Skin", ui.Skins.Keys);
        Assert.Equal("0 0 0", ui.Skins["Kenshi_TextBoxEmptySkin"].Parts.First(p => p.Type == "SimpleText").StateProperties!["normal"]["colour"]);
        string directory = Path.Combine(install.DataDirectory, "gui", "layout");

        foreach (string file in Directory.EnumerateFiles(directory, "*.layout", SearchOption.AllDirectories))
        {
            var expanded = MyGuiTemplates.Expand(MyGuiReader.ReadLayout(file), ui);
            Assert.NotEmpty(expanded.Roots);
            var arranged = MyGuiPresentation.Arrange(expanded, 1600, 900, new(), ui.Layers);
            Assert.All(arranged, c => Assert.True(float.IsFinite(c.Bounds.X + c.Bounds.Y + c.Bounds.Width + c.Bounds.Height)));
        }

        var hud = MyGuiReader.ReadLayout(resources.Find("Kenshi_MainPanel.layout")!);
        Assert.NotNull(hud.Find("TimeSpeedButton4"));
        Assert.NotNull(hud.Find("MoneyAmountText"));
        Assert.NotNull(hud.Find("LifeBar9"));
        Assert.Null(hud.Find("LifeBar10"));
    }
}
