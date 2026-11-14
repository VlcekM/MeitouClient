using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.Ui;
using Meitou.Game.Ui;

namespace Meitou.Tests.Ui;

/// <summary>
/// Inventory geometry must fit complete cell footprints inside the first frame, retaining headers, panel margins and progress bar gaps.
/// </summary>
public class InventoryGeometryTests
{
    /// <summary>
    /// A UI mod's cell skin controls both dimensions; an absent skin retains the stock fallback.
    /// </summary>
    [Fact]
    public void CellDimensionsFollowTheLoadedSkinOnBothAxes()
    {
        var resources = new MyGuiResources();
        Assert.Equal(new Vector2(25), InventoryGeometry.CellSize(resources, 1));
        resources.Skins["Kenshi_InventorySlotSkin"] = new("Kenshi_InventorySlotSkin", 28, 32, "atlas", []);
        Assert.Equal(new Vector2(35, 40), InventoryGeometry.CellSize(resources, 1.25f));
    }

    /// <summary>
    /// Opening uses the original pixel margin, appends without gaps and tries another row when the current one is full.
    /// </summary>
    [Fact]
    public void IndependentInventoriesAppendAndWrapAtTheOpeningMargin()
    {
        Assert.Equal(new Vector2(54, 54), InventoryPlacement.FindSpace(1920, 1080, 500, []));
        Assert.Equal(new Vector2(554, 54), InventoryPlacement.FindSpace(1920, 1080, 500, [new(54, 54, 500, 600)]));
        Assert.Equal(new Vector2(54, 162), InventoryPlacement.FindSpace(1920, 1080, 500, [new(54, 54, 1500, 600)]));
    }

    /// <summary>
    /// An explicit inventory origin is stable across size changes and still moves with the host's drag offset.
    /// </summary>
    [Fact]
    public void InventoryOriginKeepsItsTopLeftWhenItsFrameGrows()
    {
        var host = new MyGuiScreenHost(new());
        host.Register("inventory", Building());
        host.Open("inventory");
        float width = 400;
        UiBindings Bind(string _)
        {
            var bindings = new UiBindings { WindowPosition = new(54, 54), FitWindowToViewport = false };
            bindings.Positions["Root"] = new(0, 0, width, 400);
            return bindings;
        }

        host.SetOffset("inventory", new(100, 20));
        var before = host.Arrange(1920, 1080, Bind).Single().Controls[0].Bounds;
        width = 2200;
        var after = host.Arrange(1920, 1080, Bind).Single().Controls[0].Bounds;
        Assert.Equal(new Vector2(154, 74), new(before.X, before.Y));
        Assert.Equal(new Vector2(before.X, before.Y), new(after.X, after.Y));
        Assert.Equal(2200, after.Width);
    }

    /// <summary>
    /// The installed layout and expanded window skin must enclose a well's complete output and bar at several viewport/interface scales.
    /// </summary>
    [Theory, Slow]
    [InlineData(1920, 1080, 1)]
    [InlineData(1600, 900, 1)]
    [InlineData(1920, 1080, 1.25f)]
    public void InstalledBuildingLayoutEnclosesTallOutput(int width, int height, float scale)
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "No Kenshi install configured");
        var files = OgreScriptResources.ForInstall(install!, LoadOrder.FromInstall(install!));
        var resources = MyGuiResources.Load("kenshi_init.xml", name => files.Find(name), "kenshi_fonts.xml");
        var layout = MyGuiTemplates.Expand(MyGuiReader.ReadLayout(files.Find("Kenshi_GenericBuildingWindow.layout")!), resources);
        float uiScale = height / 1080f * scale;
        var geometry = new InventoryGeometry(layout, width, height, scale, uiScale);
        geometry.FitBuilding([], InventoryGeometry.CellSize(resources, uiScale) * 5, false, false);
        var bindings = new UiBindings { TemplateScale = uiScale };
        bindings.Hidden.UnionWith(["CraftingPanel", "InputColumn", "InputTitleText"]);
        geometry.Bind(bindings);
        var controls = MyGuiPresentation.Arrange(layout, width, height, bindings, resources.Layers);
        foreach (string name in new[] { "out", "OutputProgressBar" })
        {
            var c = controls.Single(c => c.Name == name);
            Assert.InRange(Math.Abs(c.Bounds.Height - c.Bounds.Intersect(c.Clip!.Value).Height), 0, 0.01f);
            Assert.InRange(Math.Abs(c.Bounds.Width - c.Bounds.Intersect(c.Clip!.Value).Width), 0, 0.01f);
        }
    }

    /// <summary>
    /// A resized main grid keeps clear of the bottom controls, and its heading stays directly above it.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(250)]
    public void CharacterGridKeepsItsBottomAndHeading(float height)
    {
        var layout = MyGuiReader.ParseLayout("""
            <MyGUI type="Layout"><Widget type="Window" name="Root" position="0 0 500 600">
              <Widget name="InventoryMainTitle" position="250 295 200 20"/>
              <Widget name="main" position="250 320 200 230"/>
              <Widget name="ArrangeButton" position="300 560 100 25"/>
            </Widget></MyGUI>
            """);
        var geometry = new InventoryGeometry(layout, 1920, 1080, 1, 1);
        geometry.FitCharacter(new(200, height));
        var bindings = new UiBindings();
        geometry.Bind(bindings);
        var controls = MyGuiPresentation.Arrange(layout, 1920, 1080, bindings, []);
        var main = controls.Single(c => c.Name == "main").Bounds;
        var title = controls.Single(c => c.Name == "InventoryMainTitle").Bounds;
        Assert.Equal(550, main.Y + main.Height);
        Assert.Equal(5, main.Y - title.Y - title.Height, 0.01f);
        Assert.Equal(560, controls.Single(c => c.Name == "ArrangeButton").Bounds.Y);
    }

    /// <summary>
    /// Storage keeps its layout minimum, expands only by positive grid deltas and remains natural-sized when larger than the viewport.
    /// </summary>
    [Theory]
    [InlineData(100, 50, false)]
    [InlineData(400, 500, false)]
    [InlineData(400, 500, true)]
    public void ContainerGridRetainsItsChromeAndAlignment(int width, int height, bool fixedTop)
    {
        var layout = MyGuiReader.ParseLayout("""
            <MyGUI type="Layout"><Widget type="Window" name="Root" position="0 0 200 240">
              <Widget name="Content" position="5 40 190 195" align="Stretch">
                <Widget name="backpack_content" position="20 40 150 100"/>
                <Widget name="ArrangeButton" position="45 160 100 30" align="HCenter Bottom"/>
              </Widget>
            </Widget></MyGUI>
            """);
        var host = new MyGuiScreenHost(new());
        host.Register("storage", layout);
        host.Open("storage");
        UiBindings Bind(string id)
        {
            var bindings = new UiBindings();
            var geometry = new InventoryGeometry(host.Layout(id), 320, 240, 1, 1);
            geometry.FitContainer(new(width, height), fixedTop);
            geometry.Bind(bindings);
            return bindings;
        }

        var controls = host.Arrange(320, 240, Bind).Single().Controls;
        var root = controls.Single(c => c.Name == "Root").Bounds;
        var content = controls.Single(c => c.Name == "Content").Bounds;
        var grid = controls.Single(c => c.Name == "backpack_content").Bounds;
        var button = controls.Single(c => c.Name == "ArrangeButton").Bounds;
        Assert.Equal(200 + Math.Max(0, width - 150), root.Width);
        Assert.Equal(240 + Math.Max(0, height - 100), root.Height);
        Assert.Equal((content.Width - width) / 2, grid.X - content.X);
        Assert.Equal(fixedTop ? 40 : (content.Height - height) / 2, grid.Y - content.Y);
        AssertWithin(grid, root);
        Assert.Equal(5, content.Y + content.Height - button.Y - button.Height, 0.01f);
    }

    /// <summary>
    /// A small synthetic production layout; dimensions deliberately differ from Kenshi's assets.
    /// </summary>
    static void AssertWithin(UiRect inner, UiRect outer)
    {
        var clipped = inner.Intersect(outer);
        Assert.Equal(inner.X, clipped.X, 0.01f);
        Assert.Equal(inner.Y, clipped.Y, 0.01f);
        Assert.Equal(inner.Width, clipped.Width, 0.01f);
        Assert.Equal(inner.Height, clipped.Height, 0.01f);
    }

    static MyGuiLayout Building() => MyGuiReader.ParseLayout("""
        <MyGUI type="Layout"><Widget type="Window" name="Root" position="0 0 400 400">
          <Widget name="CraftingPanel" position="5 0 390 45" align="HStretch Top"/>
          <Widget name="TitlesPanel" position="5 45 390 35" align="HStretch Top">
            <Widget name="InputTitleText" position="0 0 195 35"/>
            <Widget name="OutputTitleText" position="195 0 195 35"/>
          </Widget>
          <Widget name="InputOutputPanel" position="5 80 390 300">
            <Widget name="InputColumn" position="0 0 195 300">
              <Widget name="Input1Panel" position="0 0 195 150">
                <Widget name="Input1Content" position="10 45 175 90" align="Stretch">
                  <Widget name="in1" position="0 0 150 25"/>
                  <Widget name="Input1ProgressBar" position="0 50 175 25" align="Left Bottom"/>
                </Widget>
              </Widget>
              <Widget name="Input2Panel" position="0 150 195 150">
                <Widget name="Input2Content" position="10 45 175 90" align="Stretch">
                  <Widget name="in2" position="0 0 150 25"/>
                  <Widget name="Input2ProgressBar" position="0 50 175 25" align="Left Bottom"/>
                </Widget>
              </Widget>
            </Widget>
            <Widget name="OutputColumn" position="195 0 195 300">
              <Widget name="OutputPanel" position="0 0 195 150">
                <Widget name="OutputContent" position="10 45 175 90" align="Stretch">
                  <Widget name="out" position="0 0 150 25"/>
                  <Widget name="OutputProgressBar" position="0 50 175 25" align="Left Bottom"/>
                </Widget>
              </Widget>
              <Widget name="OutputDataPanel" position="0 150 195 150"/>
            </Widget>
          </Widget>
        </Widget></MyGUI>
        """);

    /// <summary>
    /// Output-only, two-input production and wide/tall mod-sized crafting sections all fit on the first arrange.
    /// </summary>
    [Theory]
    [InlineData(0, 5, 5, false)]
    [InlineData(2, 6, 3, false)]
    [InlineData(2, 16, 12, true)]
    public void BuildingSectionsFitTheFirstFrame(int inputCount, int columns, int rows, bool crafting)
    {
        var host = new MyGuiScreenHost(new());
        host.Register("building", Building());
        host.Open("building");
        UiBindings Bind(string id)
        {
            var bindings = new UiBindings();
            var geometry = new InventoryGeometry(host.Layout(id), 1920, 1080, 1, 1);
            Vector2[] inputs = Enumerable.Repeat(new Vector2(200, 175), inputCount).ToArray();
            geometry.FitBuilding(inputs, new(columns * 25, rows * 25), crafting, false);
            geometry.Bind(bindings);
            return bindings;
        }

        var first = host.Arrange(1920, 1080, Bind).Single().Controls;
        var output = first.Single(c => c.Name == "out");
        var bar = first.Single(c => c.Name == "OutputProgressBar");
        Assert.Equal(columns * 25, output.Bounds.Width, 0.01f);
        Assert.Equal(rows * 25, output.Bounds.Height, 0.01f);
        AssertWithin(output.Bounds, output.Clip!.Value);
        AssertWithin(bar.Bounds, bar.Clip!.Value);
        Assert.Equal(25, bar.Bounds.Y - output.Bounds.Y - output.Bounds.Height, 0.01f);
        Assert.Equal(first.Select(c => c.Bounds), host.Arrange(1920, 1080, Bind).Single().Controls.Select(c => c.Bounds));

        if (inputCount == 0)
        {
            Assert.Equal(205, first.Single(c => c.Name == "Root").Bounds.Width);
            Assert.Equal(305, first.Single(c => c.Name == "Root").Bounds.Height);
            return;
        }

        var input = first.Single(c => c.Name == "in1");
        var second = first.Single(c => c.Name == "in2");
        AssertWithin(input.Bounds, input.Clip!.Value);
        AssertWithin(second.Bounds, second.Clip!.Value);
        var column = first.Single(c => c.Name == "InputColumn").Bounds;
        Assert.Equal(column.X + column.Width, first.Single(c => c.Name == "OutputColumn").Bounds.X);
        Assert.True(second.Bounds.Y >= input.Bounds.Y + input.Bounds.Height);
    }
}
