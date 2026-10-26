using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Ui;

public sealed record MyGuiEvent(string Screen, string Widget, string Action, string Text = "", float Value = 0);
public sealed record MyGuiScreenFrame(string Id, IReadOnlyList<UiControl> Controls);

/// <summary>Window lifetime, z-order, capture, focus and template events for the original MyGUI layouts.</summary>
public sealed partial class MyGuiScreenHost(MyGuiResources resources)
{
    sealed partial class Screen(MyGuiLayout layout, bool modal, bool centreRoot, bool panel = false)
    {
        public MyGuiLayout Layout { get; set; } = layout;
        public bool Modal { get; } = modal;
        public bool Panel { get; } = panel;
        public bool CentreRoot { get; } = centreRoot;
        public Vector2 Offset;
        public bool Pinned;
        public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Tabs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Selected { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, float> Scroll { get; } = new(StringComparer.Ordinal);
    }
    readonly Dictionary<string, Screen> screens = new(StringComparer.Ordinal);
    /// <summary>The last list line clicked and when, for the double click that accepts it; the window a double click has (the system's default, 500 ms).</summary>
    (string Screen, string List, int Line, long At)? lastListClick;
    const double DoubleClickSeconds = 0.5;
    readonly List<string> open = [];
    IReadOnlyList<MyGuiScreenFrame> frame = [];
    (string Screen, string Widget)? focus;
    UiTextBuffer? edit;
    (string Screen, Vector2 At, Vector2 Offset)? drag;
    public event Action<MyGuiEvent>? Activated;
    public bool HasWindows => open.Count > 0;
    public bool HasModal => open.Any(id => screens[id].Modal);
    /// <summary>Modal screens whose empty surroundings let a click through to the screens under them (the title's windows: another title button replaces them).</summary>
    public Func<string, bool>? PassThrough { get; set; }
    public bool CapturesPointer => drag is not null || CapturesWidget;
    public bool HasKeyboardFocus => focus is not null;
    public IReadOnlyList<string> Registered => screens.Keys.ToArray();
    public IReadOnlyList<string> OpenScreens => open.ToArray();
    public void Register(string id, MyGuiLayout layout, bool modal = false, bool centreRoot = false) => screens.Add(id, new(MyGuiTemplates.Expand(layout, resources), modal, centreRoot));
    /// <summary>Registers a HUD panel taken out of the HUD layout so that its edit boxes take typing (the town panel): drawn under every window, never raised, never closed by Escape.</summary>
    public void RegisterPanel(string id, MyGuiLayout layout) => screens.Add(id, new(MyGuiTemplates.Expand(layout, resources), false, false, panel: true));
    /// <summary>Swaps a screen's layout for one composed at run time (rows the code makes from templates, as the original's <c>createWidgetT</c>); the screen's state stays.</summary>
    public void Replace(string id, MyGuiLayout layout) => screens[id].Layout = MyGuiTemplates.Expand(layout, resources);
    public MyGuiLayout Layout(string id) => screens[id].Layout;
    public bool IsOpen(string id) => open.Contains(id);
    /// <summary>Whether the open window's pin button is down (closing the window lets it go).</summary>
    public bool IsPinned(string id) => open.Contains(id) && screens[id].Pinned;
    public void SetPinned(string id, bool pinned) => screens[id].Pinned = pinned;
    public bool IsRegistered(string id) => screens.ContainsKey(id);
    public void Open(string id)
    {
        if (!screens.TryGetValue(id, out var s)) throw new KeyNotFoundException(id);
        if (s.Panel) { if (!open.Contains(id)) open.Insert(0, id); return; }
        open.Remove(id);
        open.Add(id);
        focus = null; edit = null; keyButton = null;
    }
    /// <summary>
    /// Marries <paramref name="id"/> to <paramref name="leader"/> (the original's bag window beside the inventory): a drag on either moves both, closing the leader closes it
    /// and Escape on it closes the leader.
    /// </summary>
    public void Follow(string id, string leader) => leaders[id] = leader;
    readonly Dictionary<string, string> leaders = new(StringComparer.Ordinal);

    public void Close(string id)
    {
        foreach (var (follower, leader) in leaders.ToArray()) if (leader == id && open.Contains(follower)) Close(follower);
        open.Remove(id);
        screens[id].Pinned = false;
        if (focus?.Screen == id) { focus = null; edit = null; }
        if (keyButton?.Screen == id) keyButton = null;
        if (drag?.Screen == id) drag = null;
        ForgetWidgets(id);
        frame = frame.Where(f => f.Id != id).ToArray();
    }
    public void CloseAll() { foreach (string id in open.ToArray()) Close(id); }
    public void SetTab(string id, string widget, int index) => screens[id].Tabs[widget] = Math.Max(0, index);
    /// <summary>Where a screen has been moved from its layout position (a dragged window keeps its offset; the host may place a window beside another).</summary>
    public Vector2 GetOffset(string id) => screens[id].Offset;
    public void SetOffset(string id, Vector2 offset) => screens[id].Offset = offset;
    public void SetText(string id, string widget, string text) { screens[id].Text[widget] = text; if (focus == (id, widget)) edit?.Set(text); }
    public string GetText(string id, string widget) => screens[id].Text.GetValueOrDefault(widget) ?? "";
    public int GetTab(string id, string widget) => screens[id].Tabs.GetValueOrDefault(widget);
    /// <summary>Ends typing in the focused edit box (a press the screen's decoration took).</summary>
    public void Blur() { focus = null; edit = null; keyButton = null; }
    /// <summary>The edit box being typed in (also one a screen's decoration added): its screen, name, caret and selection; null: none.</summary>
    public (string Screen, string Widget, int Caret, int SelectionStart, int SelectionLength)? Focus =>
        focus is { } f && edit is not null ? (f.Screen, f.Widget, edit.Caret, edit.SelectionStart, edit.SelectionLength) : null;

    public IReadOnlyList<MyGuiScreenFrame> Arrange(int width, int height, Func<string, UiBindings> bind, Action<string, List<UiControl>>? decorate = null)
    {
        var result = new List<MyGuiScreenFrame>();
        RepeatTick();
        foreach (string id in open.OrderBy(i => screens[i].Modal ? 1 : 0).ToArray())
        {
            var s = screens[id];
            var b = bind(id);
            foreach (var t in s.Tabs) b.SelectedTabs[t.Key] = t.Value;
            foreach (var t in s.Text) b.Text[t.Key] = t.Value;
            // A list the screen binds as having no selection (-1) drops the row kept from the last click or key press; any other binding yields to it until the next one.
            foreach (var key in s.Selected.Keys.ToArray())
                if (b.SelectedItems.TryGetValue(key, out int bound) && bound < 0) s.Selected.Remove(key);
            foreach (var t in s.Selected) b.SelectedItems[t.Key] = t.Value;
            foreach (var t in s.Scroll) b.ScrollOffsets[t.Key] = t.Value;
            PushWidgetState(id, s, b);
            if (focus is { } f && f.Screen == id && edit is not null)
            {
                foreach (var widget in s.Layout.Widgets.Where(w => w.Name == f.Widget || w.Properties.GetValueOrDefault("CaptionOwner") == f.Widget))
                {
                    var properties = new Dictionary<string, string>(b.Properties.GetValueOrDefault(widget.Name) ?? new Dictionary<string, string>(), StringComparer.Ordinal)
                    {
                        ["Focused"] = "true", ["CaretIndex"] = edit.Caret.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["SelectionStart"] = edit.SelectionStart.ToString(System.Globalization.CultureInfo.InvariantCulture), ["SelectionLength"] = edit.SelectionLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    };
                    if (widget.Properties.GetValueOrDefault("SkipCaption") != "true") b.Properties[widget.Name] = properties;
                }
            }
            PlaceRoots(s, b, width, height);
            var controls = MyGuiPresentation.Arrange(s.Layout, width, height, b, resources.Layers).ToList();
            // A pinned window's pin button stays down (the skin's checked state, else pushed).
            if (s.Pinned)
                for (int i = 0; i < controls.Count; i++)
                    if (controls[i].Action.StartsWith("mygui:pin", StringComparison.Ordinal))
                        controls[i] = controls[i] with { Properties = new Dictionary<string, string>(controls[i].Properties) { ["Checked"] = "true", ["StateSelected"] = "true" } };
            decorate?.Invoke(id, controls);
            ArrangeKeyboard(id, controls);
            result.Add(new(id, controls));
        }
        frame = result;
        return frame;
    }

    (string Id, UiControl Control)? Pick(Vector2 p)
    {
        foreach (var screen in frame.Reverse())
        {
            if (MyGuiPresentation.HitTest(screen.Controls, p.X, p.Y) is { } hit) return (screen.Id, hit);
            if (screens[screen.Id].Modal && PassThrough?.Invoke(screen.Id) != true) return (screen.Id, new("modal", "Widget", default, "", "", "", false, new Dictionary<string, string>(), default));
        }
        return null;
    }
    public bool Contains(Vector2 p) => Pick(p) is not null;
    /// <summary>The screens as the last <see cref="Arrange"/> laid them out (what <see cref="HitTest"/> picks from).</summary>
    public IReadOnlyList<MyGuiScreenFrame> LastFrame => frame;
    public (string Screen, UiControl Control)? HitTest(Vector2 p) => Pick(p);
    public bool PointerDown(Vector2 p)
    {
        keyButton = null;
        if (Pick(p) is not { } picked) { focus = null; edit = null; PressOutside(null); return false; }
        var (id, c) = picked;
        var s = screens[id];
        if (!s.Panel) { open.Remove(id); open.Add(id); }
        if (!c.Enabled) return true;
        if (PressWidget(id, c, p)) return true;
        if (c.Action.StartsWith("mygui:", StringComparison.Ordinal))
        {
            var parts = c.Action.Split(':');
            if (parts[1] == "close") Close(id);
            else if (parts[1] == "pin") s.Pinned = !s.Pinned;
            else if (parts[1] == "tab" && parts.Length == 4) s.Tabs[parts[2]] = int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
            else if (parts[1] == "list" && parts.Length == 4)
            {
                int selected = int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
                s.Selected[parts[2]] = selected;
                Activated?.Invoke(new(id, parts[2], "select", c.Argument, selected));
                // A second click on the same line soon after accepts it (MyGUI ListBox: eventListSelectAccept on a double click).
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (lastListClick is { } last && last.Screen == id && last.List == parts[2] && last.Line == selected
                    && System.Diagnostics.Stopwatch.GetElapsedTime(last.At, now).TotalSeconds < DoubleClickSeconds)
                {
                    lastListClick = null;
                    Activated?.Invoke(new(id, parts[2], "accept", c.Argument, selected));
                }
                else lastListClick = (id, parts[2], selected, now);
            }
            return true;
        }
        if (c.Type == "EditBox" && c.Properties.GetValueOrDefault("Static") != "true") { FocusEdit(id, c); return true; }
        focus = null; edit = null;
        if (c.Type == "Button") { Activated?.Invoke(new(id, c.Name, c.Action)); return true; }
        var root = frame.First(f => f.Id == id).Controls.FirstOrDefault(n => n.Type == "Window");
        if (root is not null && p.Y < root.Bounds.Y + 38 && root.Properties.GetValueOrDefault("Movable") != "false")
            drag = leaders.TryGetValue(id, out var leader) && open.Contains(leader) ? (leader, p, screens[leader].Offset) : (id, p, s.Offset);
        return true;
    }
    public bool PointerMove(Vector2 p)
    {
        if (MoveWidget(p)) return true;
        if (drag is not { } d) return false;
        DragWindow(d, p);
        return true;
    }
    public void PointerUp() { drag = null; ReleaseWidget(); }

    public bool Wheel(Vector2 p, float steps)
    {
        if (Pick(p) is not { } picked) return false;
        WheelWidget(picked.Id, p, steps);
        return true;
    }
    public bool Escape()
    {
        if (open.Count == 0) return false;
        if (CloseCombo()) return true;
        if ((frame.LastOrDefault(f => !screens[f.Id].Panel)?.Id ?? open.LastOrDefault(i => !screens[i].Panel)) is not { } id) return false;
        Close(leaders.TryGetValue(id, out var leader) && open.Contains(leader) ? leader : id);
        return true;
    }
    public bool TextInput(char value)
    {
        if (focus is null || edit is null || char.IsControl(value)) return false;
        edit.Insert(value.ToString()); PublishEdit();
        return true;
    }
    void PublishEdit()
    {
        if (focus is not { } f || edit is null || edit.ReadOnly) return;
        screens[f.Screen].Text[f.Widget] = edit.Text;
        Activated?.Invoke(new(f.Screen, f.Widget, "text", edit.Text));
    }
    public bool EditKey(UiEditKey key, bool extend = false)
    {
        if (focus is null || edit is null) return false;
        if (key >= UiEditKey.Copy) return Clipboard(key);
        edit.Key(key, extend); PublishEdit();
        return true;
    }
    public bool Backspace() => EditKey(UiEditKey.Backspace);
    /// <summary>Tab (Shift+Tab): the next edit box or button of the top window (<see cref="CycleFocus"/>).</summary>
    public bool FocusNext(bool backwards = false) => CycleFocus(backwards);
    public bool Submit()
    {
        if (ActivateFocused()) return true;
        if (frame.LastOrDefault() is not { } top) return false;
        var button = top.Controls.FirstOrDefault(c => c.Type == "Button" && c.Enabled && c.Properties.GetValueOrDefault("DefaultAction") == "true");
        if (button is null) return false;
        Activated?.Invoke(new(top.Id, button.Name, button.Action)); return true;
    }
}
