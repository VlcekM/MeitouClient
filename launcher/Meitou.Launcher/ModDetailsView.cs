using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// The Mods page's side pane for the selected mod: its name, source and author, its notes, its dependencies (each one not enabled above it in
/// the warning red), its description (<see cref="BbCodeView"/>) and its folder.
/// </summary>
static class ModDetailsView
{
    /// <summary>
    /// The pane's content for <paramref name="row"/>; <paramref name="isMet"/> says whether a dependency is enabled above it, and
    /// <paramref name="open"/> opens a clicked web link.
    /// </summary>
    public static Control Render(ModRow row, Func<string, bool> isMet, Action<string> open)
    {
        var pane = new StackPanel { Spacing = 10 };
        pane.Children.Add(Line(row.Name, 18, "PaperLight"));
        pane.Children.Add(Line(row.Detail, 12, "Quiet"));
        if (!row.Enabled) pane.Children.Add(Line(LauncherText.Translate("Not enabled: the game does not load it"), 12, "Quiet"));
        if (row.HasNotes) pane.Children.Add(Line(row.Notes, 12, row.DependencyProblems.Count > 0 ? "BadBright" : "BrassLight"));

        if (row.Mod.Header is { Dependencies.Count: > 0 } header) pane.Children.Add(Dependencies(header.Dependencies, isMet));

        pane.Children.Add(Rule());
        pane.Children.Add(Description(row.Mod.Header?.Description, open));
        pane.Children.Add(Rule());
        pane.Children.Add(Folder(row.Mod.Folder));
        return pane;
    }

    /// <summary>
    /// The dependencies under a label: each met one in the main colour, each not enabled above the mod in red with why.
    /// </summary>
    static StackPanel Dependencies(IReadOnlyList<string> dependencies, Func<string, bool> isMet)
    {
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Line(LauncherText.Translate("Needs"), 12, "Quiet"));
        string missing = LauncherText.Translate("not enabled above it");
        foreach (string dependency in dependencies)
        {
            bool met = isMet(dependency);
            list.Children.Add(Line(met ? $"✓ {dependency}" : $"✗ {dependency} ({missing})", 13, met ? "Main" : "BadBright"));
        }

        return list;
    }

    /// <summary>
    /// The description drawn, or a quiet note when the mod has none.
    /// </summary>
    static Control Description(string? description, Action<string> open) =>
        string.IsNullOrWhiteSpace(description)
            ? Line(LauncherText.Translate("No description"), 13, "Quiet")
            : BbCodeView.Render(description, open);

    /// <summary>
    /// The mod's folder, selectable so it can be copied.
    /// </summary>
    static SelectableTextBlock Folder(string folder) =>
        new() { Text = folder, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = MarkdownView.Brush("Quiet") };

    /// <summary>
    /// A thin rule between the pane's parts.
    /// </summary>
    static Border Rule() => new() { Height = 1, Background = MarkdownView.Brush("PlateRim"), Margin = new Thickness(0, 2) };

    /// <summary>
    /// A wrapping line of text of <paramref name="size"/> in the brush <paramref name="brush"/>.
    /// </summary>
    static TextBlock Line(string text, double size, string brush) =>
        new()
        {
            Text = text, FontSize = size, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap, Foreground = MarkdownView.Brush(brush),
        };
}
