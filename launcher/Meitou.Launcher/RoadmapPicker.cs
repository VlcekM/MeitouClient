using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Meitou.Launcher;

/// <summary>
/// A roadmap row: the id sent in the report and the system name shown (its text in the picker's list).
/// </summary>
sealed record RoadmapRow(string Id, string Name)
{
    /// <summary>
    /// The system name, as the picker's list shows it.
    /// </summary>
    public override string ToString() => Name;
}

/// <summary>
/// The report form's system picker: a plate button that drops a searchable list (a ComboBox cannot hold a search box), the pick committed by a
/// click or Enter; arrows move the highlight, Esc closes.
/// </summary>
sealed class RoadmapPicker
{
    /// <summary>
    /// The roadmap's rows, in its order.
    /// </summary>
    readonly List<RoadmapRow> _rows;

    /// <summary>
    /// The text brush the label takes once a system is picked.
    /// </summary>
    readonly IBrush _main;

    /// <summary>
    /// The button's label: the picked system's name, else a prompt.
    /// </summary>
    readonly TextBlock _systemLabel;

    /// <summary>
    /// The plate button that drops the list.
    /// </summary>
    readonly Button _system;

    /// <summary>
    /// The search box over the list.
    /// </summary>
    readonly TextBox _search;

    /// <summary>
    /// The rows the search matches.
    /// </summary>
    readonly ListBox _list;

    /// <summary>
    /// Shown when the search matches no row.
    /// </summary>
    readonly TextBlock _noMatch;

    /// <summary>
    /// The dropped panel: the search box, the list and the no-match line.
    /// </summary>
    readonly StackPanel _picker;

    /// <summary>
    /// The drop-down holding <see cref="_picker"/>.
    /// </summary>
    readonly Flyout _flyout;

    /// <summary>
    /// Anchored on a border around the button, not the button: keys typed in the popup bubble to its anchor, and a Button treats Space as a
    /// press. The form places this border.
    /// </summary>
    public Border Anchor { get; }

    /// <summary>
    /// The system picked, null until one is.
    /// </summary>
    public RoadmapRow? Chosen { get; private set; }

    /// <summary>
    /// A picker over <paramref name="rows"/>; the label is <paramref name="quiet"/> until a pick, then <paramref name="main"/>.
    /// </summary>
    public RoadmapPicker(List<RoadmapRow> rows, IBrush main, IBrush quiet)
    {
        _rows = rows;
        _main = main;
        _systemLabel = new TextBlock
        {
            Text = "Roadmap system", Foreground = quiet, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var chevron = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse("M0,0 H12 L6,7 Z"), Fill = main, VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 0, 0),
        };
        DockPanel.SetDock(chevron, Dock.Right);
        _system = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(12, 6),
            Content = new DockPanel { Children = { chevron, _systemLabel } },
        };
        _search = new TextBox { PlaceholderText = LauncherText.Translate("Search"), Margin = new(8, 8, 8, 4) };
        _list = new ListBox { MaxHeight = 280 };
        _noMatch = new TextBlock
        {
            Classes = { "desc" }, Text = LauncherText.Translate("No match"), Margin = new(16, 6, 16, 10), IsVisible = false,
        };
        _picker = new StackPanel { Children = { _search, _list, _noMatch } };

        Anchor = new Border { Child = _system };
        _flyout = new Flyout { Content = _picker, Placement = PlacementMode.BottomEdgeAlignedLeft };
        WireEvents();
    }

    /// <summary>
    /// Focuses the plate button.
    /// </summary>
    public void Focus() => _system.Focus();

    /// <summary>
    /// The button drops the list; the list follows the search, and a click, Enter or Esc ends it.
    /// </summary>
    void WireEvents()
    {
        _system.Click += (_, _) => _flyout.ShowAt(Anchor);
        _flyout.Opening += (_, _) =>
        {
            _picker.Width = Math.Max(0, Anchor.Bounds.Width - 2);
            _search.Text = "";
            Filter();
        };
        _flyout.Opened += (_, _) => _search.Focus();
        _flyout.Closed += (_, _) => _system.Focus();
        _search.TextChanged += (_, _) => Filter();
        _search.AddHandler(InputElement.KeyDownEvent, SearchKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        _list.AddHandler(InputElement.KeyDownEvent, ListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        _list.AddHandler(InputElement.TappedEvent, ListTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        _picker.AddHandler(InputElement.KeyDownEvent, PickerKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// The rows whose name holds the search, the picked one (or the first) highlighted.
    /// </summary>
    void Filter()
    {
        string query = _search.Text?.Trim() ?? "";
        var shown = _rows.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.ItemsSource = shown;
        _list.SelectedIndex = query.Length == 0 && Chosen is not null ? Math.Max(0, shown.IndexOf(Chosen)) : shown.Count > 0 ? 0 : -1;
        _noMatch.IsVisible = shown.Count == 0;
    }

    /// <summary>
    /// The highlighted row becomes the pick, and the list closes.
    /// </summary>
    void Pick()
    {
        if (_list.SelectedItem is not RoadmapRow row) return;
        Chosen = row;
        _systemLabel.Text = row.Name;
        _systemLabel.Foreground = _main;
        _flyout.Hide();
    }

    /// <summary>
    /// In the search box: Enter picks, Down moves into the list.
    /// </summary>
    void SearchKeyDown(object? sender, KeyEventArgs k)
    {
        if (k.Key == Key.Enter)
        {
            Pick();
            k.Handled = true;
        }
        else if (k.Key == Key.Down && _list.ContainerFromIndex(Math.Max(0, _list.SelectedIndex)) is { } row)
        {
            row.Focus();
            k.Handled = true;
        }
    }

    /// <summary>
    /// In the list: Enter picks.
    /// </summary>
    void ListKeyDown(object? sender, KeyEventArgs k)
    {
        if (k.Key == Key.Enter)
        {
            Pick();
            k.Handled = true;
        }
    }

    /// <summary>
    /// A click on a row picks it.
    /// </summary>
    void ListTapped(object? sender, TappedEventArgs t)
    {
        if ((t.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().Any() == true)
            Pick();
    }

    /// <summary>
    /// Esc closes the list.
    /// </summary>
    void PickerKeyDown(object? sender, KeyEventArgs k)
    {
        if (k.Key == Key.Escape)
        {
            _flyout.Hide();
            k.Handled = true;
        }
    }
}
