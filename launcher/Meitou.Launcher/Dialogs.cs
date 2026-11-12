using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Meitou.Launcher;

/// <summary>
/// Questions as a plate over the dimmed window (SetupHost), placed so its main button is under the pointer that asked.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// The last pointer position over the window, in window coordinates; null before the pointer moved.
    /// </summary>
    Point? _pointer;   // the last pointer position over the window, in window coordinates

    /// <summary>
    /// Follows the pointer over the window (tunnelling, handled events too) into <see cref="_pointer"/>.
    /// </summary>
    void TrackPointer() =>
        AddHandler(PointerMovedEvent, (_, e) => _pointer = e.GetPosition(this), Avalonia.Interactivity.RoutingStrategies.Tunnel,
                handledEventsToo: true);

    /// <summary>
    /// Asks; true for <paramref name="yes"/>. No, Escape and closing the question answer false.
    /// </summary>
    Task<bool> AskAsync(string title, string text, string yes, string no) =>
        ChooseAsync(title, [Wrapped(text)], [(no, false), (yes, true)], cancel: false);

    /// <summary>
    /// A question's text: wrapped, medium weight.
    /// </summary>
    static TextBlock Wrapped(string text) =>
        new() { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontWeight = Avalonia.Media.FontWeight.Medium };

    /// <summary>
    /// Shows <paramref name="body"/> with a row of buttons, the last one the main one; answers the clicked button's value, or
    /// <paramref name="cancel"/> for Escape.
    /// </summary>
    Task<T> ChooseAsync<T>(string title, IReadOnlyList<Control> body, IReadOnlyList<(string Label, T Value)> choices, T cancel)
    {
        var answer = new TaskCompletionSource<T>();
        void Close(T result)
        {
            SetupHost.IsVisible = false;
            SetupHost.Child = null;
            answer.TrySetResult(result);
        }
        var buttons = MakeButtons(choices, Close);
        var header = new Border { Classes = { "header" }, Margin = new(0, 0, 0, 10), Child = new TextBlock { Text = title } };
        var content = new StackPanel { Children = { header } };
        content.Children.AddRange(body);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        row.Children.AddRange(buttons);
        content.Children.Add(row);
        var plate = new Border
        {
            Classes = { "plate" }, Width = 520, Padding = new(20, 16), Opacity = 0, Child = content,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        plate.KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(cancel); };
        PlaceUnderPointer(plate, buttons[^1]);
        LauncherText.TranslateTree(plate, _originalUiText);
        SetupHost.Child = plate;
        SetupHost.IsVisible = true;
        buttons[0].Focus();
        return answer.Task;
    }

    /// <summary>
    /// The question's buttons in order, each closing it with its value; the last one is the main (accent) button.
    /// </summary>
    List<Button> MakeButtons<T>(IReadOnlyList<(string Label, T Value)> choices, Action<T> close)
    {
        var buttons = new List<Button>();
        for (int i = 0; i < choices.Count; i++)
        {
            var (label, value) = choices[i];
            bool main = i == choices.Count - 1;
            var secondary = (Avalonia.Media.IBrush)this.FindResource("Secondary")!;
            var button = main
                ? new Button { Classes = { "paper" }, Content = new TextBlock { Text = label, Foreground = secondary } }
                : new Button { Content = label, Margin = new(0, 0, 8, 0) };
            button.Click += (_, _) => close(value);
            buttons.Add(button);
        }
        return buttons;
    }

    /// <summary>
    /// Once laid out: move the plate so the main button is centred on the pointer, kept inside the host; then show it. Without a pointer (keyboard)
    /// it sits bottom right, over the footer.
    /// </summary>
    void PlaceUnderPointer(Border plate, Button main)
    {
        void Place(object? sender, EventArgs e)
        {
            if (plate.Bounds.Width <= 0) return;
            plate.LayoutUpdated -= Place;
            var host = SetupHost.Bounds.Size;
            var centre = main.TranslatePoint(new Point(main.Bounds.Width / 2, main.Bounds.Height / 2), plate) ?? default;
            var target = _pointer is { } p && this.TranslatePoint(p, SetupHost) is { } at ? at - centre
                : new Point(host.Width - plate.Bounds.Width - 14, host.Height - plate.Bounds.Height - 12);
            double x = Math.Clamp(target.X, 0, Math.Max(0, host.Width - plate.Bounds.Width));
            double y = Math.Clamp(target.Y, 0, Math.Max(0, host.Height - plate.Bounds.Height));
            plate.Margin = new(x, y, 0, 0);
            plate.Opacity = 1;
        }
        plate.LayoutUpdated += Place;
    }
}
