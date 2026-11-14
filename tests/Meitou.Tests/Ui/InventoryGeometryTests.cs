using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Ui;

namespace Meitou.Tests.Ui;

/// <summary>
/// Inventory geometry must fit complete cell footprints inside the first frame, retaining headers, panel margins and progress bar gaps.
/// </summary>
public class InventoryGeometryTests
{
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
        Assert.Equal(grid, grid.Intersect(root));
        Assert.Equal(5, content.Y + content.Height - button.Y - button.Height);
    }

    /// <summary>
    /// A small synthetic production layout; dimensions deliberately differ from Kenshi's assets.
    /// </summary>
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
    [InlineData(0, 4, 5, false)]
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
        Assert.Equal(columns * 25, output.Bounds.Width);
        Assert.Equal(rows * 25, output.Bounds.Height);
        Assert.Equal(output.Bounds, output.Bounds.Intersect(output.Clip!.Value));
        Assert.Equal(bar.Bounds, bar.Bounds.Intersect(bar.Clip!.Value));
        Assert.Equal(25, bar.Bounds.Y - output.Bounds.Y - output.Bounds.Height);
        Assert.Equal(first.Select(c => c.Bounds), host.Arrange(1920, 1080, Bind).Single().Controls.Select(c => c.Bounds));

        if (inputCount == 0)
        {
            Assert.Equal(205, first.Single(c => c.Name == "Root").Bounds.Width);
            Assert.Equal(305, first.Single(c => c.Name == "Root").Bounds.Height);
            return;
        }

        var input = first.Single(c => c.Name == "in1");
        var second = first.Single(c => c.Name == "in2");
        Assert.Equal(input.Bounds, input.Bounds.Intersect(input.Clip!.Value));
        Assert.Equal(second.Bounds, second.Bounds.Intersect(second.Clip!.Value));
        var column = first.Single(c => c.Name == "InputColumn").Bounds;
        Assert.Equal(column.X + column.Width, first.Single(c => c.Name == "OutputColumn").Bounds.X);
        Assert.True(second.Bounds.Y >= input.Bounds.Y + input.Bounds.Height);
    }
}
