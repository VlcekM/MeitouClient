using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// The Benchmark page's building blocks, in the launcher's row style (name and description left, the control right).
/// </summary>
static class BenchmarkLayout
{
    /// <summary>
    /// A settings row: <paramref name="name"/> over <paramref name="description"/> (both already translated) beside <paramref name="control"/>
    /// in a column <paramref name="width"/> wide.
    /// </summary>
    public static Border Row(string name, string description, Control control, int width)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"*,{width}") };
        grid.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new(0, 0, 16, 0),
            Children = { new TextBlock { Classes = { "name" }, Text = name }, Description(description) },
        });
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return new Border { Classes = { "row" }, Child = grid };
    }

    /// <summary>
    /// A wrapping description line.
    /// </summary>
    public static TextBlock Description(string text) => new() { Classes = { "desc" }, Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    /// <summary>
    /// A text block whose text the code sets later (as inlines, so no language pass puts an old text back).
    /// </summary>
    public static TextBlock Line(string classes = "desc") => new() { Classes = { classes }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    /// <summary>
    /// Sets <paramref name="block"/> to <paramref name="text"/> (already translated), hiding it when empty.
    /// </summary>
    public static void SetLine(TextBlock block, string? text)
    {
        block.Inlines = [new Run(text ?? "")];
        block.IsVisible = !string.IsNullOrEmpty(text);
    }

    /// <summary>
    /// A button with <paramref name="caption"/> (translated) running <paramref name="click"/>.
    /// </summary>
    public static Button Button(string caption, Action click)
    {
        var button = new Button { Content = caption, Margin = new(6, 0, 0, 0) };
        button.Click += (_, _) => click();
        return button;
    }
}
